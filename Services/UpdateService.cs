using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace QuickTrans.Services;

/// <summary>
/// 检查软件本身有没有新版本。版本信息放在仓库根目录的 version.json，
/// 通过 jsDelivr CDN 读取（raw.githubusercontent 在国内经常不通）。
///
/// 兼容新旧两代客户端：
/// - 旧版只认 url（指向直接可执行的 QuickTrans.exe）
/// - 新版优先认 zipUrl / liteZipUrl（压缩包体积小得多），下载后自动解压出 QuickTrans.exe，
///   缺失时自动回落到 url。若 version.json 提供了 sha256 字段，会做完整性校验。
/// </summary>
public sealed class UpdateService
{
    // jsDelivr 在国内基本可达，raw 作为兜底
    private static readonly string[] ManifestUrls =
    {
        "https://cdn.jsdelivr.net/gh/kmc130908/QuickTrans@main/version.json",
        "https://raw.githubusercontent.com/kmc130908/QuickTrans/main/version.json",
    };

    // 复用静态 HttpClient，避免每次检查/下载创建新连接池；超时按请求单独控制
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(12);

    /// <summary>当前程序是否为依赖系统 .NET 8 运行时的轻量版。</summary>
    public static bool IsLiteBuild =>
#if FRAMEWORK_DEPENDENT
        true;
#else
        false;
#endif

    public sealed record UpdateInfo(string Version, string Url, string Notes, string? Sha256 = null);

    /// <summary>当前程序版本，取自程序集（由 csproj 的 Version 决定）。</summary>
    public static string CurrentVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v == null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    /// <summary>有新版本返回信息，已是最新或检查失败返回 null。</summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        foreach (var url in ManifestUrls)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(CheckTimeout);

                var json = await Http.GetStringAsync(url, cts.Token);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var ver = GetString(root, "version");
                var notes = GetString(root, "notes") ?? "";
                var (link, sha256) = SelectDownload(root);

                if (string.IsNullOrWhiteSpace(ver) || string.IsNullOrWhiteSpace(link))
                    continue;

                if (!Version.TryParse(ver, out var remote)) continue;
                if (!Version.TryParse(CurrentVersion, out var local)) continue;

                if (remote > local)
                    return new UpdateInfo(ver, link, notes, sha256);
            }
            catch
            {
                // 换下一个源
            }
        }

        return null;
    }

    /// <summary>
    /// 按当前版本形态挑最省流量的下载地址：
    /// - 轻量版：liteZipUrl → liteUrl → zipUrl → url
    /// - 标准版：zipUrl → url
    /// </summary>
    private static (string? Url, string? Sha256) SelectDownload(JsonElement root)
    {
        if (IsLiteBuild)
        {
            var liteZip = GetString(root, "liteZipUrl");
            if (!string.IsNullOrWhiteSpace(liteZip))
                return (liteZip, GetString(root, "liteZipSha256"));

            var liteExe = GetString(root, "liteUrl");
            if (!string.IsNullOrWhiteSpace(liteExe))
                return (liteExe, GetString(root, "liteSha256"));
        }

        var zip = GetString(root, "zipUrl");
        if (!string.IsNullOrWhiteSpace(zip))
            return (zip, GetString(root, "zipSha256"));

        return (GetString(root, "url"), GetString(root, "sha256"));
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static bool IsZipUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return uri.AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
        return url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 下载新版本到当前目录的 QuickTrans.exe.new。
    /// 支持直接下载 exe，也支持下载 zip 并自动解压出里面的 QuickTrans.exe。
    /// 下次启动或点「重启并更新」时由 ApplyAndRestart 完成替换。
    /// </summary>
    public async Task<string> DownloadAsync(
        UpdateInfo info, IProgress<(long Received, long Total)>? progress, CancellationToken ct = default)
    {
        var target = Path.Combine(AppContext.BaseDirectory, "QuickTrans.exe.new");
        var expectZip = IsZipUrl(info.Url);
        var temp = expectZip
            ? Path.Combine(AppContext.BaseDirectory, "QuickTrans.exe.zip.part")
            : target + ".part";

        await DownloadFileWithResumeAsync(info.Url, temp, progress, ct);

        // 若 version.json 提供了 SHA256，校验下载文件完整性
        if (!string.IsNullOrWhiteSpace(info.Sha256))
        {
            var actual = await ComputeSha256Async(temp, ct);
            if (!actual.Equals(info.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(temp); } catch { }
                throw new InvalidOperationException("下载的文件校验失败，可能不完整或已损坏，已删除。请重试。");
            }
        }

        if (expectZip || StartsWithBytes(temp, (byte)'P', (byte)'K'))
        {
            ExtractExeFromZip(temp, target);
            try { File.Delete(temp); } catch { }
        }
        else
        {
            if (!IsValidExeFile(temp))
            {
                try { File.Delete(temp); } catch { }
                throw new InvalidOperationException("下载的文件不是有效的可执行程序，已删除。请重试。");
            }
            File.Move(temp, target, overwrite: true);
        }

        return target;
    }

    private static async Task DownloadFileWithResumeAsync(
        string url, string tempPath, IProgress<(long Received, long Total)>? progress, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var existing = File.Exists(tempPath) ? new FileInfo(tempPath).Length : 0;

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (existing > 0)
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);

            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            // 已下好的 .part 比远端文件还大（换包或旧残片）：删掉重下
            if (existing > 0 && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                try { File.Delete(tempPath); } catch { }
                continue;
            }

            if (existing > 0 && response.StatusCode == HttpStatusCode.OK)
            {
                existing = 0;
                try { File.Delete(tempPath); } catch { }
            }
            else
            {
                response.EnsureSuccessStatusCode();
            }

            var contentLen = response.Content.Headers.ContentLength;
            var total = contentLen.HasValue ? contentLen.Value + existing : 0;

            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var dest = new FileStream(tempPath, FileMode.Append, FileAccess.Write, FileShare.None,
                             bufferSize: 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long received = existing;
                var lastReport = DateTime.UtcNow;

                while (true)
                {
                    var read = await source.ReadAsync(buffer, ct);
                    if (read <= 0) break;
                    await dest.WriteAsync(buffer.AsMemory(0, read), ct);
                    received += read;

                    if ((DateTime.UtcNow - lastReport).TotalMilliseconds >= 100)
                    {
                        lastReport = DateTime.UtcNow;
                        progress?.Report((received, total));
                    }
                }

                progress?.Report((received, total));

                if (total > 0 && received < total)
                    throw new IOException($"下载未完成（{received}/{total} 字节），请重试续传");
            }

            return;
        }
    }

    private static void ExtractExeFromZip(string zipPath, string targetExePath)
    {
        var extracting = targetExePath + ".extracting";
        try
        {
            using (var archive = ZipFile.OpenRead(zipPath))
            {
                var entry = archive.Entries.FirstOrDefault(e =>
                                string.Equals(Path.GetFileName(e.FullName), "QuickTrans.exe", StringComparison.OrdinalIgnoreCase))
                            ?? archive.Entries.FirstOrDefault(e =>
                                e.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

                if (entry == null)
                    throw new InvalidOperationException("更新压缩包里找不到 QuickTrans.exe");

                entry.ExtractToFile(extracting, overwrite: true);
            }

            if (!IsValidExeFile(extracting))
            {
                try { File.Delete(extracting); } catch { }
                throw new InvalidOperationException("解压出的更新文件无效");
            }

            File.Move(extracting, targetExePath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(extracting)) File.Delete(extracting); } catch { }
            throw;
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(fs, ct);
        return Convert.ToHexString(hash);
    }

    private static bool StartsWithBytes(string path, byte b0, byte b1)
    {
        try
        {
            using var fs = File.OpenRead(path);
            return fs.ReadByte() == b0 && fs.ReadByte() == b1;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>校验文件是有效的 Windows PE 可执行文件（同时兼容轻量版与标准版体积）。</summary>
    private static bool IsValidExeFile(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length < 256 * 1024) return false;
            return StartsWithBytes(path, (byte)'M', (byte)'Z');
        }
        catch
        {
            return false;
        }
    }

    /// <summary>启动时顺手清理上次更新留下的 .old 备份。</summary>
    public static void CleanupOldExe()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe)) return;
            var old = exe + ".old";
            if (File.Exists(old)) File.Delete(old);
        }
        catch { /* 删不掉不影响启动 */ }
    }

    /// <summary>
    /// 用新下载的 exe 替换当前程序并重启。
    ///
    /// Windows 不允许替换正在运行的 exe，所以这里启动一个 cmd 等本进程退出后再操作。
    /// 步骤：等 2 秒 → 当前 exe 改名 .old → .new 改成正式名 → 启动 → 删 .old。
    /// 任何一步失败，.old 都还在，用户可以手动恢复。
    /// </summary>
    public static bool ApplyAndRestart()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe)) return false;

            var newExe = Path.Combine(AppContext.BaseDirectory, "QuickTrans.exe.new");
            if (!IsValidExeFile(newExe)) return false;

            var old = exe + ".old";
            var script =
                $"timeout /t 2 /nobreak >nul & " +
                $"move /y \"{exe}\" \"{old}\" && " +
                $"move /y \"{newExe}\" \"{exe}\" && " +
                $"(start \"\" \"{exe}\" & del /f /q \"{old}\")";

            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c " + script)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory,
            };

            System.Diagnostics.Process.Start(psi);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>是否有已经下载好、等待替换的新版本。</summary>
    public static bool HasPendingUpdate()
    {
        var newExe = Path.Combine(AppContext.BaseDirectory, "QuickTrans.exe.new");
        return IsValidExeFile(newExe);
    }
}
