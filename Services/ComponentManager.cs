using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using QuickTrans.Models;

namespace QuickTrans.Services;

/// <summary>下载进度。Phase 用于界面显示当前阶段。</summary>
public sealed record DownloadProgress(long Received, long Total, string Phase)
{
    public double Percent => Total > 0 ? Math.Clamp(Received * 100.0 / Total, 0, 100) : 0;
}

/// <summary>
/// 可下载组件（GPU 引擎、模型）的安装与卸载。
/// 下载走断点续传，完成后做 SHA256 校验，再解压到目标目录。
/// </summary>
public sealed class ComponentManager
{
    private readonly string _baseDir;
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public ComponentManager(string baseDir) => _baseDir = baseDir;

    public string ModelsDir => Path.Combine(_baseDir, "models");
    public string EngineDir => Path.Combine(_baseDir, "engine");
    public string EngineCpuDir => Path.Combine(_baseDir, "engine-cpu");

    /// <summary>组件是否已经装好（靠 CheckFile 判断）。</summary>
    public bool IsInstalled(ComponentItem item)
    {
        if (string.IsNullOrWhiteSpace(item.CheckFile)) return false;
        var target = Path.Combine(_baseDir, item.Target);
        return item.ArchiveKind == "zip"
            ? File.Exists(Path.Combine(target, item.CheckFile))
            : File.Exists(Path.Combine(target, item.CheckFile));
    }

    /// <summary>下载并安装组件。镜像失败会自动回落主地址。</summary>
    public async Task DownloadAsync(
        ComponentItem item,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct = default)
    {
        var targetDir = Path.Combine(_baseDir, item.Target);
        Directory.CreateDirectory(targetDir);

        var temp = Path.Combine(_baseDir, $".download-{item.Id}.part");
        var isArchive = string.Equals(item.ArchiveKind, "zip", StringComparison.OrdinalIgnoreCase);

        // 已有部分文件说明上次中断了，续传
        var existing = File.Exists(temp) ? new FileInfo(temp).Length : 0;
        if (existing > 0 && progress != null)
            progress.Report(new DownloadProgress(existing, item.SizeBytes, "继续上次的下载"));

        var urls = new List<string> { item.Url };
        if (!string.IsNullOrWhiteSpace(item.MirrorUrl))
        {
            // 镜像放前面：国内环境下通常更快，失败再回落直连
            urls.Insert(0, item.MirrorUrl);
        }

        Exception? lastError = null;
        foreach (var url in urls)
        {
            try
            {
                await DownloadWithResumeAsync(url, temp, item.SizeBytes, progress, ct);
                lastError = null;
                break;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                // 换下一个地址重试，保留已下载的部分
            }
        }

        if (lastError != null)
            throw new InvalidOperationException("下载失败：" + lastError.Message, lastError);

        // 校验：官方清单带 SHA256 时做强校验；模型文件额外检查 GGUF 文件头，防止镜像返回 HTML 错误页
        if (!string.IsNullOrWhiteSpace(item.Sha256))
        {
            progress?.Report(new DownloadProgress(1, 1, "校验文件完整性 (SHA256)"));
            var actual = await ComputeSha256Async(temp, ct);
            if (!actual.Equals(item.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(temp); } catch { }
                throw new InvalidOperationException(
                    "下载的文件 SHA256 校验失败，可能不完整或被篡改，已自动清理。请重试。");
            }
        }
        else if (string.Equals(item.Kind, "model", StringComparison.OrdinalIgnoreCase))
        {
            progress?.Report(new DownloadProgress(1, 1, "校验模型文件头"));
            var gguf = EngineManager.ValidateGgufHeader(temp);
            if (!gguf.Ok)
            {
                try { File.Delete(temp); } catch { }
                throw new InvalidOperationException(gguf.Reason + "（已自动清理损坏文件，请重试）");
            }
        }

        // GPU 引擎是解压到 engine 目录，会覆盖现有的 CPU 引擎。
        // 覆盖前先把 CPU 引擎复制一份到 engine-cpu，用户以后还能切回来。
        if (item.Id is "engine-vulkan" or "engine-cuda")
            BackupCpuEngineIfNeeded(progress);

        progress?.Report(new DownloadProgress(1, 1, "正在安装"));
        if (isArchive)
        {
            ZipFile.ExtractToDirectory(temp, targetDir, overwriteFiles: true);
            try { File.Delete(temp); } catch { }
        }
        else
        {
            var dest = Path.Combine(targetDir, item.CheckFile);
            if (File.Exists(dest)) File.Delete(dest);
            File.Move(temp, dest, overwrite: true);
        }

        progress?.Report(new DownloadProgress(1, 1, "完成"));
    }

    private async Task DownloadWithResumeAsync(
        string url, string tempPath, long expectedSize,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var existing = File.Exists(tempPath) ? new FileInfo(tempPath).Length : 0;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (existing > 0)
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        // 服务器不支持续传（返回 200 而不是 206），只能从头下
        if (existing > 0 && response.StatusCode == System.Net.HttpStatusCode.OK)
        {
            existing = 0;
            try { File.Delete(tempPath); } catch { }
        }
        else
        {
            response.EnsureSuccessStatusCode();
        }

        var total = expectedSize > 0
            ? expectedSize
            : (response.Content.Headers.ContentLength ?? 0) + existing;

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var dest = new FileStream(tempPath, FileMode.Append, FileAccess.Write, FileShare.None,
            bufferSize: 81920, useAsync: true);

        var buffer = new byte[81920];
        long received = existing;
        var lastReport = DateTime.UtcNow;

        while (true)
        {
            var read = await source.ReadAsync(buffer, ct);
            if (read <= 0) break;

            await dest.WriteAsync(buffer.AsMemory(0, read), ct);
            received += read;

            // 限制刷新频率，避免界面被高频进度事件拖慢
            if ((DateTime.UtcNow - lastReport).TotalMilliseconds >= 120)
            {
                lastReport = DateTime.UtcNow;
                progress?.Report(new DownloadProgress(received, total, "下载中"));
            }
        }

        progress?.Report(new DownloadProgress(received, total, "下载完成"));

        // 大小对不上说明连接被中断，保留 .part 供下次续传
        if (total > 0 && received < total)
            throw new IOException($"下载不完整（{received}/{total} 字节），请重试续传");
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(fs, ct);
        return Convert.ToHexString(hash);
    }

    /// <summary>把当前 engine 目录里的 CPU 引擎备份到 engine-cpu，避免被 GPU 引擎覆盖后丢失。</summary>
    private void BackupCpuEngineIfNeeded(IProgress<DownloadProgress>? progress)
    {
        try
        {
            var engineExe = Path.Combine(EngineDir, "llama-server.exe");
            if (!File.Exists(engineExe)) return;              // 还没有引擎，无需备份
            if (Directory.Exists(EngineCpuDir) &&
                File.Exists(Path.Combine(EngineCpuDir, "llama-server.exe")))
                return;                                        // 已经备份过

            progress?.Report(new DownloadProgress(1, 1, "备份现有 CPU 引擎"));
            CopyDirectory(EngineDir, EngineCpuDir);
        }
        catch
        {
            // 备份失败不影响主流程：GPU 引擎装好后仍能用，只是切不回 CPU
        }
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(source, dest));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(source, dest), overwrite: true);
    }

    /// <summary>删除组件文件。模型删单个文件，引擎删整个目录。</summary>
    public void Remove(ComponentItem item)
    {
        var target = Path.Combine(_baseDir, item.Target);

        if (string.Equals(item.Kind, "engine", StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            return;
        }

        var file = Path.Combine(target, item.CheckFile);
        if (File.Exists(file)) File.Delete(file);
    }
}
