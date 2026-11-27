using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;

namespace QuickTrans.Services;

/// <summary>
/// 界面渲染品质与流畅度辅助：
/// 1. 独立 UI 子进程启用 DirectX 硬件加速与系统刷新率同步动画帧率（240Hz/144Hz/120Hz/60Hz），
///    常驻主进程继续保持 SoftwareOnly 以守住约 18MB 待机内存。
/// 2. HiDPI 下自动切换 TextFormattingMode=Ideal 与 ClearType 文本渲染，消除高分屏锯齿与模糊。
/// 3. 同步 Windows 11 标题栏深浅模式与背景融合色（DWM Immersive Dark Mode + Caption Color）。
/// 4. 提供跟随显示器刷新率的高帧率平滑滚轮滚动控制器。
/// </summary>
public static class UiQualityHelper
{
    private const int ENUM_CURRENT_SETTINGS = -1;
    private const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1;

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_CAPTION_COLOR = 35;

    private const int VK_LBUTTON = 0x01;
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int HTCAPTION = 2;
    private const int WM_MOVING = 0x0216;
    private const int WM_ENTERSIZEMOVE = 0x0231;
    private const int WM_EXITSIZEMOVE = 0x0232;
    private const int WM_CANCELMODE = 0x001F;
    private const int WM_SETICON = 0x0080;

    private static readonly Dictionary<Window, int> PendingWindowIconStyles = new();
    private static readonly Dictionary<Window, (IntPtr Big, IntPtr Small)> WindowHicons = new();

    private static readonly string[] UiChildFlags =
    {
        "--settings-child",
        "--welcome-child",
        "--components-child",
        "--input-child",
        "--translate-child",
        "--ocr-child",
        "--history-child",
    };

    private static readonly Dictionary<Window, (bool IsDark, Color? CaptionColor)> PendingDwmThemes = new();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string? lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr CopyIcon(IntPtr picon);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    public static bool IsUiChildProcess(string[] args)
    {
        foreach (var arg in args)
        {
            foreach (var flag in UiChildFlags)
            {
                if (string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    /// <summary>查询当前已连接显示器的最高刷新率（Hz），范围限制在 60–240。</summary>
    public static int GetMaxDisplayRefreshRate()
    {
        var maxHz = 0;
        try
        {
            var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            for (uint i = 0; EnumDisplayDevices(null, i, ref dd, 0); i++)
            {
                if ((dd.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0)
                {
                    var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
                    if (EnumDisplaySettings(dd.DeviceName, ENUM_CURRENT_SETTINGS, ref dm) && dm.dmDisplayFrequency > maxHz)
                        maxHz = dm.dmDisplayFrequency;
                }
                dd.cb = Marshal.SizeOf<DISPLAY_DEVICE>();
            }

            if (maxHz <= 1)
            {
                var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
                if (EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref dm))
                    maxHz = dm.dmDisplayFrequency;
            }
        }
        catch { }

        if (maxHz < 60) return 60;
        if (maxHz > 240) return 240;
        return maxHz;
    }

    /// <summary>
    /// 按进程角色配置渲染模式与帧率：
    /// UI 子进程使用硬件加速 + 系统刷新率；常驻主进程保持软件渲染以维持低内存占用。
    /// </summary>
    public static void ConfigureProcessRendering(string[] args)
    {
        if (IsUiChildProcess(args))
        {
            RenderOptions.ProcessRenderMode = RenderMode.Default;
            var hz = GetMaxDisplayRefreshRate();
            try
            {
                Timeline.DesiredFrameRateProperty.OverrideMetadata(
                    typeof(Timeline),
                    new FrameworkPropertyMetadata { DefaultValue = hz });
            }
            catch { }
        }
        else
        {
            // 纯 2D 常驻主进程无需 GPU 硬件渲染，软件渲染可减少约 5–11MB 常驻内存
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        }
    }

    /// <summary>为窗口绑定 HiDPI 清晰文本模式、高质量缩放及 DWM 标题栏主题同步。</summary>
    public static void AttachWindowQuality(Window win, bool applyDwmTitleBar = true)
    {
        RenderOptions.SetBitmapScalingMode(win, BitmapScalingMode.HighQuality);
        TextOptions.SetTextRenderingMode(win, TextRenderingMode.ClearType);
        TextOptions.SetTextHintingMode(win, TextHintingMode.Fixed);
        ApplyDpiTextMode(win);

        HwndSource? hookSource = null;
        var inSizeMove = false;
        var mouseMove = false;

        IntPtr QualityWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_NCLBUTTONDOWN && wParam.ToInt32() == HTCAPTION)
            {
                if ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) == 0)
                {
                    handled = true;
                    return IntPtr.Zero;
                }
            }
            else if (msg == WM_ENTERSIZEMOVE)
            {
                inSizeMove = true;
                mouseMove = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
            }
            else if (msg == WM_EXITSIZEMOVE)
            {
                inSizeMove = false;
                mouseMove = false;
            }
            else if (msg == WM_MOVING && inSizeMove && mouseMove)
            {
                if ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) == 0)
                {
                    inSizeMove = false;
                    mouseMove = false;
                    ReleaseCapture();
                    PostMessage(hwnd, WM_CANCELMODE, IntPtr.Zero, IntPtr.Zero);
                }
            }
            return IntPtr.Zero;
        }

        win.SourceInitialized += (_, _) =>
        {
            ApplyDpiTextMode(win);
            if (applyDwmTitleBar)
            {
                if (PendingDwmThemes.TryGetValue(win, out var pending))
                    ApplyDwmAttributes(win, pending.IsDark, pending.CaptionColor);

                var hwnd = new WindowInteropHelper(win).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    hookSource = HwndSource.FromHwnd(hwnd);
                    hookSource?.AddHook(QualityWndProc);
                }
            }

            if (PendingWindowIconStyles.TryGetValue(win, out var iconStyle))
                ApplyWin32WindowIcons(win, iconStyle);
        };

        win.DpiChanged += (_, _) => ApplyDpiTextMode(win);

        win.Closed += (_, _) =>
        {
            PendingDwmThemes.Remove(win);
            PendingWindowIconStyles.Remove(win);
            ReleaseWindowHicons(win);
            if (hookSource != null)
            {
                hookSource.RemoveHook(QualityWndProc);
                hookSource = null;
            }
        };
    }

    private static void ApplyDpiTextMode(Window win)
    {
        try
        {
            var scale = VisualTreeHelper.GetDpi(win).DpiScaleX;
            TextOptions.SetTextFormattingMode(
                win,
                scale > 1.05 ? TextFormattingMode.Ideal : TextFormattingMode.Display);
        }
        catch
        {
            TextOptions.SetTextFormattingMode(win, TextFormattingMode.Ideal);
        }
    }

    /// <summary>设置窗口标题栏的 Windows 11 深浅色模式与自定义标题栏底色。</summary>
    public static void ApplyDwmTheme(Window win, bool isDark, Color? captionColor = null)
    {
        PendingDwmThemes[win] = (isDark, captionColor);
        ApplyDwmAttributes(win, isDark, captionColor);
    }

    private static void ApplyDwmAttributes(Window win, bool isDark, Color? captionColor)
    {
        try
        {
            var hwnd = new WindowInteropHelper(win).Handle;
            if (hwnd == IntPtr.Zero) return;

            var dark = isDark ? 1 : 0;
            if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref dark, sizeof(int));

            if (captionColor.HasValue)
            {
                var c = captionColor.Value;
                var colorRef = c.R | (c.G << 8) | (c.B << 16);
                DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref colorRef, sizeof(int));
            }
        }
        catch { }
    }

    /// <summary>打开嵌入的图标资源流（tray1..tray4）。</summary>
    public static Stream? OpenBrandIconStream(int style)
    {
        var s = Math.Clamp(style, 1, 4);
        foreach (var path in new[]
        {
            $"pack://application:,,,/assets/tray{s}.ico",
            $"pack://application:,,,/QuickTrans;component/assets/tray{s}.ico",
        })
        {
            try
            {
                var res = Application.GetResourceStream(new Uri(path, UriKind.Absolute));
                if (res?.Stream != null) return res.Stream;
            }
            catch { }
        }
        return null;
    }

    /// <summary>加载适合界面展示或 WPF Window.Icon 的清晰矢量图标帧。</summary>
    public static BitmapSource? LoadBrandIconBitmap(int style, int preferredMaxSize = 64)
    {
        try
        {
            using var stream = OpenBrandIconStream(style);
            if (stream == null) return null;
            var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames
                .OrderByDescending(f => f.PixelWidth)
                .FirstOrDefault(f => f.PixelWidth <= preferredMaxSize)
                ?? decoder.Frames.OrderByDescending(f => f.PixelWidth).FirstOrDefault();
            if (frame != null && frame.CanFreeze) frame.Freeze();
            return frame;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>同步窗口 WPF Icon 与 Win32 任务栏/标题栏大小图标（WM_SETICON）。</summary>
    public static void ApplyWindowIcon(Window win, int style)
    {
        var s = Math.Clamp(style, 1, 4);
        PendingWindowIconStyles[win] = s;

        var bmp = LoadBrandIconBitmap(s, 64);
        if (bmp != null) win.Icon = bmp;

        ApplyWin32WindowIcons(win, s);
    }

    private static void ApplyWin32WindowIcons(Window win, int style)
    {
        try
        {
            var hwnd = new WindowInteropHelper(win).Handle;
            if (hwnd == IntPtr.Zero) return;

            using var s1 = OpenBrandIconStream(style);
            using var s2 = OpenBrandIconStream(style);
            if (s1 == null || s2 == null) return;

            using var bigSrc = new Drawing.Icon(s1, new Drawing.Size(48, 48));
            using var smallSrc = new Drawing.Icon(s2, new Drawing.Size(16, 16));
            var hBig = CopyIcon(bigSrc.Handle);
            var hSmall = CopyIcon(smallSrc.Handle);

            var old = WindowHicons.TryGetValue(win, out var prev) ? prev : (IntPtr.Zero, IntPtr.Zero);
            WindowHicons[win] = (hBig, hSmall);

            if (hBig != IntPtr.Zero) SendMessage(hwnd, WM_SETICON, new IntPtr(1), hBig);
            if (hSmall != IntPtr.Zero) SendMessage(hwnd, WM_SETICON, IntPtr.Zero, hSmall);

            if (old.Item1 != IntPtr.Zero) DestroyIcon(old.Item1);
            if (old.Item2 != IntPtr.Zero) DestroyIcon(old.Item2);
        }
        catch { }
    }

    private static void ReleaseWindowHicons(Window win)
    {
        if (!WindowHicons.Remove(win, out var pair)) return;
        try
        {
            if (pair.Big != IntPtr.Zero) DestroyIcon(pair.Big);
            if (pair.Small != IntPtr.Zero) DestroyIcon(pair.Small);
        }
        catch { }
    }

    /// <summary>用物理屏幕坐标判定鼠标指针是否位于元素矩形内（跨 Popup HWND 与 CaptureMode 均可靠）。</summary>
    public static bool IsCursorPhysicallyInside(FrameworkElement? el)
    {
        if (el == null || !el.IsVisible || el.ActualWidth <= 0 || el.ActualHeight <= 0)
            return false;
        try
        {
            if (!GetCursorPos(out var cur)) return false;
            var tl = el.PointToScreen(new Point(0, 0));
            var br = el.PointToScreen(new Point(el.ActualWidth, el.ActualHeight));
            var minX = Math.Min(tl.X, br.X);
            var maxX = Math.Max(tl.X, br.X);
            var minY = Math.Min(tl.Y, br.Y);
            var maxY = Math.Max(tl.Y, br.Y);
            return cur.X >= minX && cur.X <= maxX && cur.Y >= minY && cur.Y <= maxY;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>判定节点是否为指定父元素（视觉树或逻辑树）的后代。</summary>
    public static bool IsDescendantOf(DependencyObject? node, DependencyObject? ancestor)
    {
        if (node == null || ancestor == null) return false;
        var cur = node;
        var guard = 0;
        while (cur != null && guard++ < 64)
        {
            if (ReferenceEquals(cur, ancestor)) return true;
            DependencyObject? next = null;
            if (cur is Visual or System.Windows.Media.Media3D.Visual3D)
                next = VisualTreeHelper.GetParent(cur);
            next ??= LogicalTreeHelper.GetParent(cur);
            cur = next;
        }
        return false;
    }

    /// <summary>为 ScrollViewer 挂载基于显示器 VSync 帧率的平滑滚动控制器。</summary>
    public static SmoothScrollController AttachSmoothScroll(
        ScrollViewer sv, bool absorbAtBoundary = false, double wheelFactor = 1.05)
        => new(sv, absorbAtBoundary, wheelFactor);
}

/// <summary>
/// 跟随显示器 VSync（CompositionTarget.Rendering）的阻尼平滑滚动控制器；
/// 滚动结束后自动取消订阅，空闲时 CPU 占用为 0。
/// </summary>
public sealed class SmoothScrollController
{
    private readonly ScrollViewer _sv;
    private readonly bool _absorbAtBoundary;
    private readonly double _wheelFactor;
    private double _target;
    private double _current;
    private bool _animating;
    private long _lastTick;

    public SmoothScrollController(ScrollViewer sv, bool absorbAtBoundary = false, double wheelFactor = 1.05)
    {
        _sv = sv;
        _absorbAtBoundary = absorbAtBoundary;
        _wheelFactor = wheelFactor;
        _sv.PreviewMouseWheel += OnPreviewMouseWheel;
        _sv.Unloaded += (_, _) => Stop();
    }

    public void ScrollBy(double deltaPx)
    {
        var max = Math.Max(0, _sv.ScrollableHeight);
        if (max <= 0) return;

        if (!_animating)
        {
            _current = _sv.VerticalOffset;
            _target = _current;
        }

        _target = Math.Clamp(_target + deltaPx, 0, max);
        Start();
    }

    public void JumpToTop()
    {
        Stop();
        _target = 0;
        _current = 0;
        _sv.ScrollToTop();
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta == 0) return;

        if (_absorbAtBoundary)
        {
            e.Handled = true;
            if (_sv.ScrollableHeight > 0.5)
                ScrollBy(-e.Delta * _wheelFactor);
            return;
        }

        if (e.OriginalSource is DependencyObject src)
        {
            // 若页面内有已展开的 ComboBox，优先按指针所在区域判定：
            // 1) 在下拉浮层内 → 放行给下拉列表自己滚动；
            // 2) 在已展开的框体本身上 → 拦截但不关闭也不翻页，防止误改或错位；
            // 3) 在外部空白区域 → 先收起下拉再平滑翻页。
            var openCombo = FindOpenAncestorComboBox(src);
            if (openCombo != null)
            {
                var popup = openCombo.Template?.FindName("PART_Popup", openCombo) as Popup;
                if (UiQualityHelper.IsDescendantOf(src, popup?.Child)
                    || UiQualityHelper.IsCursorPhysicallyInside(popup?.Child as FrameworkElement))
                    return;

                if (UiQualityHelper.IsCursorPhysicallyInside(openCombo))
                {
                    e.Handled = true;
                    return;
                }

                openCombo.IsDropDownOpen = false;
                e.Handled = true;
                ScrollBy(-e.Delta * _wheelFactor);
                return;
            }

            // 若事件源自内层可滚动列表（例如模型列表/接口列表/运行程序列表）且该列表在当前方向上还能滚，则交由内层处理
            if (ShouldLetInnerScroll(src, e.Delta))
                return;
        }

        e.Handled = true;
        ScrollBy(-e.Delta * _wheelFactor);
    }

    private static ComboBox? FindOpenAncestorComboBox(DependencyObject? node)
    {
        var guard = 0;
        while (node != null && guard++ < 48)
        {
            if (node is ComboBox cb && cb.IsDropDownOpen)
                return cb;
            node = GetParent(node);
        }
        return null;
    }

    private bool ShouldLetInnerScroll(DependencyObject source, int delta)
    {
        var node = source;
        while (node != null && !ReferenceEquals(node, _sv))
        {
            if (node is ScrollViewer inner && inner.ScrollableHeight > 0.5)
            {
                if (delta > 0 && inner.VerticalOffset > 0.5) return true;
                if (delta < 0 && inner.VerticalOffset < inner.ScrollableHeight - 0.5) return true;
            }
            node = GetParent(node);
        }
        return false;
    }

    private static DependencyObject? GetParent(DependencyObject obj)
    {
        if (obj is Visual or System.Windows.Media.Media3D.Visual3D)
            return VisualTreeHelper.GetParent(obj) ?? LogicalTreeHelper.GetParent(obj);
        return LogicalTreeHelper.GetParent(obj);
    }

    private void Start()
    {
        if (_animating) return;
        _animating = true;
        _lastTick = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += OnRendering;
    }

    private void Stop()
    {
        if (!_animating) return;
        _animating = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!_sv.IsLoaded)
        {
            Stop();
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var dt = (now - _lastTick) / (double)Stopwatch.Frequency;
        _lastTick = now;
        dt = Math.Clamp(dt, 0.001, 0.05);

        var max = Math.Max(0, _sv.ScrollableHeight);
        if (_target > max) _target = max;

        var diff = _target - _current;
        if (Math.Abs(diff) < 0.12)
        {
            _current = _target;
            _sv.ScrollToVerticalOffset(_current);
            Stop();
            return;
        }

        var factor = 1.0 - Math.Exp(-dt * 8.8);
        _current += diff * factor;
        _sv.ScrollToVerticalOffset(_current);
    }
}
