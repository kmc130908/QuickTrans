using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace QuickTrans.Services;

/// <summary>
/// 多窗口防遮挡定位与可靠前置显示工具：
/// 在浮窗、设置页、历史窗同时打开时，自动为新窗口挑选与已有窗口重叠面积最小的位置，
/// 并防止后台子进程拉起窗口时闪烁或掉到其他程序后面。
/// </summary>
public static class WindowPlacer
{
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

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

    public static Int32Rect? TryGetRectPx(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd) || !IsWindowVisible(hwnd) || IsIconic(hwnd))
            return null;
        if (!GetWindowRect(hwnd, out var r)) return null;
        var w = r.Right - r.Left;
        var h = r.Bottom - r.Top;
        return w > 10 && h > 10 ? new Int32Rect(r.Left, r.Top, w, h) : null;
    }

    public static string SerializeRects(IEnumerable<Int32Rect> rects) =>
        string.Join(";", rects.Select(r => $"{r.X},{r.Y},{r.Width},{r.Height}"));

    public static List<Int32Rect> ParseRects(string? raw)
    {
        var list = new List<Int32Rect>();
        if (string.IsNullOrWhiteSpace(raw)) return list;
        foreach (var part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var nums = part.Split(',');
            if (nums.Length == 4
                && int.TryParse(nums[0], out var x)
                && int.TryParse(nums[1], out var y)
                && int.TryParse(nums[2], out var w)
                && int.TryParse(nums[3], out var h)
                && w > 10 && h > 10)
            {
                list.Add(new Int32Rect(x, y, w, h));
            }
        }
        return list;
    }

    /// <summary>
    /// 在目标显示器工作区内，为即将显示的窗口选一个与 avoidPx 重叠面积最小的位置。
    /// 若没有需要避让的窗口，保持默认居中。
    /// </summary>
    public static void PlaceAvoiding(Window win, IReadOnlyList<Int32Rect> avoidPx)
    {
        if (avoidPx.Count == 0)
        {
            win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }

        // 优先在第一个已有窗口（通常是浮窗）所在的显示器上排布
        var anchor = avoidPx[0];
        var probe = new POINT { X = anchor.X + anchor.Width / 2, Y = anchor.Y + anchor.Height / 2 };
        var mon = MonitorFromPoint(probe, MONITOR_DEFAULTTONEAREST);
        if (mon == IntPtr.Zero)
        {
            GetCursorPos(out probe);
            mon = MonitorFromPoint(probe, MONITOR_DEFAULTTONEAREST);
        }

        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(mon, ref mi))
        {
            win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }

        var scale = 1.0;
        try
        {
            if (GetDpiForMonitor(mon, 0, out var dx, out _) == 0 && dx > 0)
                scale = dx / 96.0;
        }
        catch { }

        var work = mi.rcWork;
        var winW = (int)Math.Round((double.IsNaN(win.Width) || win.Width <= 0 ? 600 : win.Width) * scale);
        var winH = (int)Math.Round((double.IsNaN(win.Height) || win.Height <= 0 ? 540 : win.Height) * scale);
        var pad = (int)Math.Round(16 * scale);

        int ClampX(int x) => Math.Max(work.Left + pad, Math.Min(x, Math.Max(work.Left + pad, work.Right - winW - pad)));
        int ClampY(int y) => Math.Max(work.Top + pad, Math.Min(y, Math.Max(work.Top + pad, work.Bottom - winH - pad)));

        var candidates = new List<(int X, int Y)>();

        // 1. 相对每个已有窗口的右、左、下、上侧
        foreach (var r in avoidPx)
        {
            candidates.Add((ClampX(r.X + r.Width + pad), ClampY(r.Y)));
            candidates.Add((ClampX(r.X - winW - pad), ClampY(r.Y)));
            candidates.Add((ClampX(r.X + r.Width + pad), ClampY(work.Top + (work.Bottom - work.Top - winH) / 2)));
            candidates.Add((ClampX(r.X - winW - pad), ClampY(work.Top + (work.Bottom - work.Top - winH) / 2)));
            candidates.Add((ClampX(r.X), ClampY(r.Y + r.Height + pad)));
            candidates.Add((ClampX(r.X), ClampY(r.Y - winH - pad)));
        }

        // 2. 工作区四个角与左右半区居中（屏幕偏小时保证互相错开、绝不完全重叠）
        var midY = ClampY(work.Top + (work.Bottom - work.Top - winH) / 2);
        var leftX = ClampX(work.Left + pad);
        var rightX = ClampX(work.Right - winW - pad);
        var topY = ClampY(work.Top + pad);
        var bottomY = ClampY(work.Bottom - winH - pad);

        candidates.Add((rightX, midY));
        candidates.Add((leftX, midY));
        candidates.Add((rightX, topY));
        candidates.Add((leftX, topY));
        candidates.Add((rightX, bottomY));
        candidates.Add((leftX, bottomY));

        var centerX = work.Left + (work.Right - work.Left - winW) / 2;
        var centerY = work.Top + (work.Bottom - work.Top - winH) / 2;

        long bestScore = long.MaxValue;
        (int X, int Y) best = (ClampX(centerX), ClampY(centerY));

        foreach (var (cx, cy) in candidates)
        {
            var rect = new Int32Rect(cx, cy, winW, winH);
            long overlap = 0;
            foreach (var a in avoidPx)
            {
                var ox = Math.Max(0, Math.Min(rect.X + rect.Width, a.X + a.Width) - Math.Max(rect.X, a.X));
                var oy = Math.Max(0, Math.Min(rect.Y + rect.Height, a.Y + a.Height) - Math.Max(rect.Y, a.Y));
                overlap += (long)ox * oy;
            }

            // 重叠面积为主（权重极高），重叠相同时偏好看上去居中的位置
            var dist = Math.Abs(cx - centerX) + Math.Abs(cy - centerY);
            var score = overlap * 1000L + dist;
            if (score < bestScore)
            {
                bestScore = score;
                best = (cx, cy);
            }
        }

        win.WindowStartupLocation = WindowStartupLocation.Manual;
        win.Left = best.X / scale;
        win.Top = best.Y / scale;
    }

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>
    /// 可靠将新启动的子进程窗口展示到前台且严格保持非置顶状态（WS_EX_TOPMOST=false），
    /// 避免在无焦点浮窗触发时闪烁掉到后方，也不会出现意外残留置顶压住浮窗的问题。
    /// </summary>
    public static void SurfaceWindow(Window win)
    {
        if (win.WindowState == WindowState.Minimized)
            win.WindowState = WindowState.Normal;

        win.Topmost = false;
        win.Show();
        var hwnd = new WindowInteropHelper(win).Handle;
        if (hwnd != IntPtr.Zero)
        {
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            SetForegroundWindow(hwnd);
        }
        win.Activate();
        win.Focus();
    }
}
