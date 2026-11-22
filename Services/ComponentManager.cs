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

    /// <summary>
    /// 查找组件对应的已安装文件路径。
    /// 对于模型：先按精确 CheckFile 查 models 目录，再按同系列前缀匹配 models 目录里的任意量化 .gguf（如 Q4_K_M / Q8_0）以及用户手动添加的外部模型路径。
    /// </summary>
    public string? FindInstalledFile(ComponentItem item, IEnumerable<string>? extraModelPaths = null)
    {
        if (string.IsNullOrWhiteSpace(item.CheckFile)) return null;
        var target = Path.Combine(_baseDir, item.Target);
        var exact = Path.Combine(target, item.CheckFile);
        if (File.Exists(exact)) return exact;

        if (!string.Equals(item.Kind, "model", StringComparison.OrdinalIgnoreCase))
            return null;

        bool Matches(string fileName)
        {
            if (string.Equals(fileName, item.CheckFile, StringComparison.OrdinalIgnoreCase))
                return true;
            if (item.MatchPrefixes is { Length: > 0 })
            {
                foreach (var p in item.MatchPrefixes)
                {
                    if (!string.IsNullOrWhiteSpace(p)
                        && fileName.Contains(p, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            return false;
        }

        try
        {
            if (Directory.Exists(target))
            {
                foreach (var f in Directory.GetFiles(target, "*.gguf", SearchOption.TopDirectoryOnly))
                {
                    if (Matches(Path.GetFileName(f)))
                        return f;
                }
            }
        }
        catch { }

        if (extraModelPaths != null)
        {
            foreach (var p in extraModelPaths)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(p) && File.Exists(p) && Matches(Path.GetFileName(p)))
                        return p;
                }
                catch { }
            }
        }

        return null;
    }

    /// <summary>组件是否已经装好（支持自动识别 models 目录同系列量化模型与外部路径）。</summary>
    public bool IsInstalled(ComponentItem item, IEnumerable<string>? extraModelPaths = null) =>
        FindInstalledFile(item, extraModelPaths) != null;

    /// <summary>判断给定路径是否位于安装目录的 models 文件夹内（删除操作仅允许删 models 内部文件，绝不误删用户外部文件）。</summary>
    public bool IsInsideModelsDir(string filePath)
    {
        try
        {
            var full = Path.GetFullPath(filePath);
            var dir = Path.GetFullPath(ModelsDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(dir, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
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
                await DownloadBestAsync(url, temp, item.SizeBytes, progress, ct);
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

        try { if (File.Exists(temp + ".chunks")) File.Delete(temp + ".chunks"); } catch { }

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

    private const int ParallelChunks = 4;
    private const long ParallelThresholdBytes = 8L * 1024 * 1024;

    /// <summary>
    /// 优先探测服务器是否支持 HTTP Range 并发下载：支持且文件 ≥ 8MB 时使用 4 线程分片下载（提速 2–4 倍，支持断点续传）；
    /// 不支持时平滑回落单线程断点续传。
    /// </summary>
    private async Task DownloadBestAsync(
        string url, string tempPath, long expectedSize,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var metaPath = tempPath + ".chunks";

        // 如果上次遗留了单线程 .part（没有 .chunks 元数据），继续单线程续传避免浪费已下流量
        var hasLegacySinglePart = File.Exists(tempPath) && !File.Exists(metaPath) && new FileInfo(tempPath).Length > 0;
        if (!hasLegacySinglePart)
        {
            var (supportsRange, remoteTotal) = await ProbeRangeSupportAsync(url, ct);
            if (supportsRange && remoteTotal >= ParallelThresholdBytes)
            {
                await DownloadParallelAsync(url, tempPath, metaPath, remoteTotal, progress, ct);
                return;
            }
        }

        await DownloadWithResumeAsync(url, tempPath, expectedSize, progress, ct);
    }

    private async Task<(bool SupportsRange, long TotalBytes)> ProbeRangeSupportAsync(string url, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.PartialContent
                && resp.Content.Headers.ContentRange?.Length is long total
                && total > 0)
            {
                return (true, total);
            }
            return (false, resp.Content.Headers.ContentLength ?? 0);
        }
        catch
        {
            return (false, 0);
        }
    }

    private async Task DownloadParallelAsync(
        string url, string tempPath, string metaPath, long totalBytes,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var chunkCount = ParallelChunks;
        var chunkSize = totalBytes / chunkCount;
        var starts = new long[chunkCount];
        var ends = new long[chunkCount];
        var done = new long[chunkCount];

        for (var i = 0; i < chunkCount; i++)
        {
            starts[i] = i * chunkSize;
            ends[i] = i == chunkCount - 1 ? totalBytes - 1 : (i + 1) * chunkSize - 1;
        }

        // 尝试加载上次多线程断点进度
        var resumed = false;
        if (File.Exists(tempPath) && File.Exists(metaPath) && new FileInfo(tempPath).Length == totalBytes)
        {
            try
            {
                var lines = File.ReadAllLines(metaPath);
                if (lines.Length == chunkCount + 1
                    && long.TryParse(lines[0], out var savedTotal)
                    && savedTotal == totalBytes)
                {
                    var valid = true;
                    for (var i = 0; i < chunkCount; i++)
                    {
                        var maxLen = ends[i] - starts[i] + 1;
                        if (long.TryParse(lines[i + 1], out var d) && d >= 0 && d <= maxLen)
                            done[i] = d;
                        else { valid = false; break; }
                    }
                    resumed = valid;
                }
            }
            catch { resumed = false; }
        }

        if (!resumed)
        {
            Array.Clear(done, 0, chunkCount);
            await using (var initFs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
            {
                initFs.SetLength(totalBytes);
            }
        }

        long totalReceived = done.Sum();
        var metaGate = new object();
        var lastReportTicks = 0L;
        var lastMetaTicks = 0L;

        void PersistMeta()
        {
            lock (metaGate)
            {
                try
                {
                    var lines = new string[chunkCount + 1];
                    lines[0] = totalBytes.ToString();
                    for (var i = 0; i < chunkCount; i++)
                        lines[i + 1] = Volatile.Read(ref done[i]).ToString();
                    File.WriteAllLines(metaPath, lines);
                }
                catch { }
            }
        }

        void ReportNow(bool force = false)
        {
            var now = Environment.TickCount64;
            if (!force && now - Volatile.Read(ref lastReportTicks) < 120) return;
            Volatile.Write(ref lastReportTicks, now);
            progress?.Report(new DownloadProgress(Interlocked.Read(ref totalReceived), totalBytes, $"多线程下载中 ({chunkCount}x)"));

            if (force || now - Volatile.Read(ref lastMetaTicks) >= 600)
            {
                Volatile.Write(ref lastMetaTicks, now);
                PersistMeta();
            }
        }

        ReportNow(force: true);

        async Task DownloadChunkAsync(int idx)
        {
            var chunkLen = ends[idx] - starts[idx] + 1;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var curDone = Volatile.Read(ref done[idx]);
                if (curDone >= chunkLen) return;

                try
                {
                    var rangeStart = starts[idx] + curDone;
                    var rangeEnd = ends[idx];
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(rangeStart, rangeEnd);
                    using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                    if (resp.StatusCode != System.Net.HttpStatusCode.PartialContent)
                        throw new IOException($"分片请求未返回 206（状态码 {(int)resp.StatusCode}）");

                    await using var netStream = await resp.Content.ReadAsStreamAsync(ct);
                    await using var fs = new FileStream(tempPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite,
                        bufferSize: 81920, useAsync: true);
                    fs.Seek(rangeStart, SeekOrigin.Begin);

                    var buf = new byte[81920];
                    while (true)
                    {
                        var remaining = chunkLen - Volatile.Read(ref done[idx]);
                        if (remaining <= 0) break;
                        var toRead = (int)Math.Min(buf.Length, remaining);
                        var n = await netStream.ReadAsync(buf.AsMemory(0, toRead), ct);
                        if (n <= 0) break;

                        await fs.WriteAsync(buf.AsMemory(0, n), ct);
                        Interlocked.Add(ref done[idx], n);
                        Interlocked.Add(ref totalReceived, n);
                        ReportNow();
                    }

                    if (Volatile.Read(ref done[idx]) >= chunkLen)
                        return;

                    throw new IOException($"分片 {idx + 1} 连接提前中断");
                }
                catch (OperationCanceledException)
                {
                    PersistMeta();
                    throw;
                }
                catch
                {
                    PersistMeta();
                    if (attempt >= 2) throw;
                    await Task.Delay(500 * (attempt + 1), ct);
                }
            }
        }

        try
        {
            var tasks = Enumerable.Range(0, chunkCount).Select(DownloadChunkAsync).ToArray();
            await Task.WhenAll(tasks);
        }
        finally
        {
            PersistMeta();
        }

        var finalReceived = Interlocked.Read(ref totalReceived);
        progress?.Report(new DownloadProgress(finalReceived, totalBytes, "下载完成"));
        if (finalReceived < totalBytes)
            throw new IOException($"下载不完整（{finalReceived}/{totalBytes} 字节），请重试续传");
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

    /// <summary>删除组件文件。模型仅删除 models 目录内匹配到的文件，引擎删整个目录。</summary>
    public void Remove(ComponentItem item)
    {
        var target = Path.Combine(_baseDir, item.Target);

        if (string.Equals(item.Kind, "engine", StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            return;
        }

        var matched = FindInstalledFile(item);
        if (matched != null && IsInsideModelsDir(matched) && File.Exists(matched))
            File.Delete(matched);
    }
}
