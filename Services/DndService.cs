using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using QuickTrans.Models;

namespace QuickTrans.Services;

public sealed record RunningAppInfo(string ProcessName, string WindowTitle)
{
    public string DisplayLabel => string.IsNullOrWhiteSpace(WindowTitle)
        ? $"{ProcessName}.exe"
        : $"{TrimTitle(WindowTitle)}  ({ProcessName}.exe)";

    private static string TrimTitle(string s) => s.Length > 34 ? s[..34] + "…" : s;
}

/// <summary>
/// 免打扰检测服务：支持「全屏游戏屏蔽」「全屏视频屏蔽」「应用黑名单」三项独立控制。
/// 纯按需 Win32 查询（仅在热键按下瞬间执行，零后台轮询、零常驻内存与 CPU 开销）。
/// </summary>
public static class DndService
{
    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const int WS_CAPTION = 0x00C00000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int pquns);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    // 浏览器与常见视频播放器：全屏时按「全屏视频」归类
    private static readonly HashSet<string> VideoOrBrowserProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "arc", "waterfox", "librewolf",
        "360chrome", "360chromex", "360se", "qqbrowser", "quark", "ucbrowser", "sogouexplorer",
        "potplayermini64", "potplayermini", "potplayer", "vlc", "mpv", "mpvnet",
        "mpc-hc64", "mpc-hc", "mpc-be64", "mpc-be", "kodi", "wmplayer", "video.ui",
        "microsoft.media.player", "bilibili", "iqiyi", "qyplayer", "qqlive", "tenvideo",
        "youkumedia", "youku", "dandanplay", "zyplayer", "douyin", "plex", "jellyfinmediaplayer",
        "emby.theater", "netflix",
    };

    // 常见桌面壳层与生产力工具：即便按 F11 全屏也排除出「全屏游戏」，避免误伤写代码/办公
    private static readonly HashSet<string> ShellOrProductivityProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "quicktrans", "explorer", "searchhost", "startmenuexperiencehost", "shellexperiencehost",
        "textinputhost", "applicationframehost", "lockapp", "systemsettings",
        "code", "cursor", "devenv", "rider64", "idea64", "pycharm64", "webstorm64", "clion64",
        "notepad", "notepad++", "sublime_text", "typora", "obsidian", "notion",
        "wps", "et", "wpp", "winword", "excel", "powerpnt", "onenote", "acrobat", "foxitpdfreader",
        "windowsterminal", "wt", "cmd", "powershell", "pwsh", "mstsc",
    };

    private static readonly HashSet<string> ShellWindowClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "Windows.UI.Core.CoreWindow", "DV2ControlHost",
    };

    /// <summary>规范化进程名：去路径、去 .exe 后缀、去首尾空白。</summary>
    public static string NormalizeProcessName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var s = raw.Trim().Trim('"');
        try
        {
            if (s.Contains('\\') || s.Contains('/'))
                s = Path.GetFileName(s);
        }
        catch { }

        if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            s = s[..^4];
        return s.Trim();
    }

    /// <summary>
    /// 判断当前前台环境是否应屏蔽触发热键。
    /// 返回 true 时，reason 给出具体命中原因（用于日志排查）。
    /// </summary>
    public static bool ShouldBlock(AppConfig cfg, out string reason)
    {
        reason = "";
        var hasBlacklist = cfg.DndBlacklistEnabled && cfg.DndBlacklistProcesses is { Count: > 0 };
        if (!cfg.DndFullscreenGame && !cfg.DndFullscreenVideo && !hasBlacklist)
            return false;

        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;

        var procName = GetProcessName(fg);
        if (string.IsNullOrEmpty(procName)) return false;
        if (string.Equals(procName, "quicktrans", StringComparison.OrdinalIgnoreCase)) return false;

        // 1. 应用黑名单（无论是否全屏都屏蔽）
        if (hasBlacklist)
        {
            foreach (var entry in cfg.DndBlacklistProcesses)
            {
                var norm = NormalizeProcessName(entry);
                if (!string.IsNullOrEmpty(norm) && string.Equals(norm, procName, StringComparison.OrdinalIgnoreCase))
                {
                    reason = $"黑名单程序 ({procName})";
                    return true;
                }
            }
        }

        // 2. 全屏游戏 / 全屏视频分项判断
        if (!cfg.DndFullscreenGame && !cfg.DndFullscreenVideo)
            return false;

        var cls = GetWindowClass(fg);
        if (ShellWindowClasses.Contains(cls)) return false;

        var isD3dExclusive = false;
        try
        {
            if (SHQueryUserNotificationState(out var quns) == 0 && quns == QUNS_RUNNING_D3D_FULL_SCREEN)
                isD3dExclusive = true;
        }
        catch { }

        var coversMonitor = false;
        var isBorderless = false;
        if (GetWindowRect(fg, out var rect))
        {
            var mon = MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (mon != IntPtr.Zero && GetMonitorInfo(mon, ref mi))
            {
                var m = mi.rcMonitor;
                coversMonitor = rect.Left <= m.Left + 2
                                && rect.Top <= m.Top + 2
                                && rect.Right >= m.Right - 2
                                && rect.Bottom >= m.Bottom - 2;
            }
            var style = GetWindowLong(fg, GWL_STYLE);
            isBorderless = (style & WS_CAPTION) != WS_CAPTION;
        }

        var kind = ClassifyFullscreen(procName, coversMonitor, isBorderless, isD3dExclusive);
        if (kind == FullscreenKind.Video && cfg.DndFullscreenVideo)
        {
            reason = $"全屏视频 ({procName})";
            return true;
        }
        if (kind == FullscreenKind.Game && cfg.DndFullscreenGame)
        {
            reason = $"全屏游戏 ({procName})";
            return true;
        }

        return false;
    }

    public enum FullscreenKind { None, Game, Video }

    /// <summary>纯逻辑分类函数，便于单元测试与回归校验。</summary>
    public static FullscreenKind ClassifyFullscreen(
        string processName, bool coversMonitor, bool isBorderless, bool isD3dExclusive)
    {
        var proc = NormalizeProcessName(processName);
        if (string.IsNullOrEmpty(proc)) return FullscreenKind.None;

        // 浏览器 / 视频播放器全屏 → 归类为全屏视频（即便用了 D3D 全屏播放）
        if (VideoOrBrowserProcesses.Contains(proc))
            return (coversMonitor || isD3dExclusive) ? FullscreenKind.Video : FullscreenKind.None;

        // 独占 D3D/Vulkan 全屏 → 全屏游戏
        if (isD3dExclusive)
            return ShellOrProductivityProcesses.Contains(proc) ? FullscreenKind.None : FullscreenKind.Game;

        // 无边框全屏覆盖整块屏幕且非桌面/办公软件 → 全屏游戏
        if (coversMonitor && isBorderless && !ShellOrProductivityProcesses.Contains(proc))
            return FullscreenKind.Game;

        return FullscreenKind.None;
    }

    /// <summary>扫描当前桌面正在运行的可见用户程序，供设置页黑名单一键勾选。</summary>
    public static List<RunningAppInfo> GetRunningApps()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;

            var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            if ((ex & WS_EX_TOOLWINDOW) != 0) return true;

            var title = GetWindowTitle(hwnd);
            if (string.IsNullOrWhiteSpace(title)) return true;

            var cls = GetWindowClass(hwnd);
            if (ShellWindowClasses.Contains(cls)) return true;

            var proc = GetProcessName(hwnd);
            if (string.IsNullOrEmpty(proc)) return true;
            if (string.Equals(proc, "quicktrans", StringComparison.OrdinalIgnoreCase)
                || string.Equals(proc, "explorer", StringComparison.OrdinalIgnoreCase)
                || string.Equals(proc, "searchhost", StringComparison.OrdinalIgnoreCase)
                || string.Equals(proc, "textinputhost", StringComparison.OrdinalIgnoreCase)
                || string.Equals(proc, "shellexperiencehost", StringComparison.OrdinalIgnoreCase)
                || string.Equals(proc, "startmenuexperiencehost", StringComparison.OrdinalIgnoreCase)
                || string.Equals(proc, "applicationframehost", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!map.ContainsKey(proc))
                map[proc] = title;

            return true;
        }, IntPtr.Zero);

        return map
            .Select(kv => new RunningAppInfo(kv.Key, kv.Value))
            .OrderBy(a => a.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string GetProcessName(IntPtr hwnd)
    {
        try
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return "";
            using var p = Process.GetProcessById((int)pid);
            return NormalizeProcessName(p.ProcessName);
        }
        catch
        {
            return "";
        }
    }

    private static string GetWindowClass(IntPtr hwnd)
    {
        var sb = new StringBuilder(128);
        return GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        var sb = new StringBuilder(128);
        return GetWindowText(hwnd, sb, sb.Capacity) > 0 ? sb.ToString().Trim() : "";
    }
}
