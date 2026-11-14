using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace QuickTrans.Services;

/// <summary>
/// 检查软件本身有没有新版本。版本信息放在仓库根目录的 version.json，
/// 通过 jsDelivr CDN 读取（raw.githubusercontent 在国内经常不通）。
/// </summary>
public sealed class UpdateService
{
    // jsDelivr 在国内基本可达，raw 作为兜底
    private static readonly string[] ManifestUrls =
    {
        "https://cdn.jsdelivr.net/gh/kmc130908/QuickTrans@main/version.json",
        "https://raw.githubusercontent.com/kmc130908/QuickTrans/main/version.json",
    };

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(12) };

    public sealed record UpdateInfo(string Version, string Url, string Notes);

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
                var json = await _http.GetStringAsync(url, ct);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var ver = root.TryGetProperty("version", out var v) ? v.GetString() : null;
                var link = root.TryGetProperty("url", out var u) ? u.GetString() : null;
                var notes = root.TryGetProperty("notes", out var n) ? n.GetString() : "";

                if (string.IsNullOrWhiteSpace(ver) || string.IsNullOrWhiteSpace(link))
                    continue;

                if (!Version.TryParse(ver, out var remote)) continue;
                if (!Version.TryParse(CurrentVersion, out var local)) continue;

                if (remote > local)
                    return new UpdateInfo(ver, link, notes ?? "");
            }
            catch
            {
                // 换下一个源
            }
        }

        return null;
    }

    /// <summary>
    /// 下载新版本到当前目录的 QuickTrans.exe.new。
    /// 下次启动时由 ApplyPendingUpdate 完成替换。
    /// </summary>
    public async Task<string> DownloadAsync(
        UpdateInfo info, IProgress<(long Received, long Total)>? progress, CancellationToken ct = default)
    {
        var target = Path.Combine(AppContext.BaseDirectory, "QuickTrans.exe.new");
        var temp = target + ".part";

        using var request = new HttpRequestMessage(HttpMethod.Get, info.Url);
        var existing = File.Exists(temp) ? new FileInfo(temp).Length : 0;
        if (existing > 0)
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (existing > 0 && response.StatusCode == System.Net.HttpStatusCode.OK)
        {
            existing = 0;
            try { File.Delete(temp); } catch { }
        }
        else
        {
            response.EnsureSuccessStatusCode();
        }

        var total = (response.Content.Headers.ContentLength ?? 0) + existing;

        await using (var source = await response.Content.ReadAsStreamAsync(ct))
        await using (var dest = new FileStream(temp, FileMode.Append, FileAccess.Write, FileShare.None,
                         bufferSize: 81920, useAsync: true))
        {
            var buffer = new byte[81920];
            long received = existing;
            while (true)
            {
                var read = await source.ReadAsync(buffer, ct);
                if (read <= 0) break;
                await dest.WriteAsync(buffer.AsMemory(0, read), ct);
                received += read;
                progress?.Report((received, total));
            }
        }

        File.Move(temp, target, overwrite: true);
        return target;
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
            if (!File.Exists(newExe)) return false;

            // 新文件太小说明下载不完整，别拿它去覆盖
            if (new FileInfo(newExe).Length < 10 * 1024 * 1024) return false;

            var old = exe + ".old";
            var script =
                $"timeout /t 2 /nobreak >nul & " +
                $"move /y \"{exe}\" \"{old}\" & " +
                $"move /y \"{newExe}\" \"{exe}\" & " +
                $"start \"\" \"{exe}\" & " +
                $"del \"{old}\"";

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
        try
        {
            return File.Exists(newExe) && new FileInfo(newExe).Length > 10 * 1024 * 1024;
        }
        catch
        {
            return false;
        }
    }
}
