using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace QuickTrans.Views;

public partial class PopupWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_ASYNCWINDOWPOS = 0x4000;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

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

    private static readonly Brush ActiveLangBg = new SolidColorBrush(Color.FromRgb(0x3B, 0x5B, 0xA8));
    private static readonly Brush ActiveLangFg = Brushes.White;
    private static readonly Brush IdleLangBg = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF5));
    private static readonly Brush IdleLangFg = new SolidColorBrush(Color.FromRgb(0x5A, 0x5A, 0x64));

    private readonly DispatcherTimer _autoCloseTimer;
    private readonly DispatcherTimer _foregroundCheckTimer;
    private readonly DispatcherTimer _timedCloseTimer;
    private bool _hasResult;
    private bool _closed;
    private bool _positioned;
    private bool _inputMode;

    /// <summary>取字来源窗口句柄：前台窗口切走它就启动自动关闭倒计时。</summary>
    private IntPtr _sourceHwnd = IntPtr.Zero;

    /// <summary>设置页窗口句柄：前台切到设置页不算失焦。</summary>
    private IntPtr _settingsHwnd = IntPtr.Zero;

    private bool _userMoved;

    /// <summary>选中文字的屏幕矩形，有值就贴着它显示，否则跟随鼠标。</summary>
    private Int32Rect? _anchorRect;

    private static readonly string[] AllLanguages =
    {
        "简体中文", "繁体中文", "英语", "日语", "韩语", "法语", "德语",
        "西班牙语", "俄语", "葡萄牙语", "意大利语", "阿拉伯语", "泰语", "越南语",
    };

    private List<string> _languages = new();
    private List<string> _editingLanguages = new();
    private string _currentTarget = "简体中文";

    /// <summary>用户在浮窗上增删了常用语言。</summary>
    public event Action<List<string>>? QuickLanguagesChanged;

    /// <summary>用户点了某个语言按钮。</summary>
    public event Action<string>? LanguageRequested;

    /// <summary>手动输入模式下按了回车。</summary>
    public event Action<string>? ManualTextSubmitted;

    /// <summary>点了截图按钮。</summary>
    public event Action? OcrRequested;

    /// <summary>用户点了浮窗上的齿轮，请求打开设置。</summary>
    public event Action? SettingsRequested;

    /// <summary>用户点了喇叭，请求朗读当前译文。</summary>
    public event Action<string>? SpeakRequested;

    /// <summary>当前译文文本，供朗读使用。</summary>
    public string CurrentResult => ResultText.Text;

    /// <summary>浮窗失去焦点后是否自动关闭，由设置页控制。</summary>
    public static bool AutoCloseEnabled { get; set; } = true;

    /// <summary>自动关闭前等待的秒数。0 表示切走立即关闭。</summary>
    public static int AutoCloseSeconds { get; set; } = 5;

    /// <summary>切走后鼠标停在浮窗上是否暂停倒计时。</summary>
    public static bool AutoClosePauseOnHover { get; set; } = true;

    /// <summary>定时自动关闭是否开启（不管切不切窗口，到时自动关）。</summary>
    public static bool TimedCloseEnabled { get; set; } = false;

    /// <summary>定时自动关闭的秒数。</summary>
    public static int TimedCloseSeconds { get; set; } = 30;

    public PopupWindow()
    {
        InitializeComponent();

        _autoCloseTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(Math.Max(0, AutoCloseSeconds)),
        };
        _autoCloseTimer.Tick += (_, _) =>
        {
            _autoCloseTimer.Stop();

            // 用户关掉了自动消失：计时到点也不关
            if (!AutoCloseEnabled) return;
            if (_inputMode) return;
            // 鼠标悬停暂停：按设置决定，勾选才暂停
            if (IsMouseOver && AutoClosePauseOnHover) return;
            Close();
        };

        // 前台窗口检测：每 300ms 看前台窗口是否还是取字来源窗口。
        // 只有切到别的窗口（前台离开来源窗口）才启动自动关闭倒计时。
        // 继续操作来源窗口（滚动/点击）不算失焦，浮窗一直保留。
        _foregroundCheckTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(300),
        };
        _foregroundCheckTimer.Tick += (_, _) => CheckForeground();
        _foregroundCheckTimer.Start();

        // 定时自动关闭：翻译完成后倒计时，到点自动关（不管切不切窗口、
        // 鼠标在不在浮窗上）。切走窗口时由切走关闭规则接管（更快触发）。
        _timedCloseTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(Math.Max(1, TimedCloseSeconds)),
        };
        _timedCloseTimer.Tick += (_, _) =>
        {
            _timedCloseTimer.Stop();
            if (!TimedCloseEnabled) return;
            if (_inputMode) return;
            Close();
        };

        MouseEnter += (_, _) => _autoCloseTimer.Stop();

        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) => { Reposition(true); _positioned = true; };
        SizeChanged += (_, _) => { if (_positioned && !_userMoved) Reposition(); };
        Closed += (_, _) =>
        {
            _closed = true;
            _autoCloseTimer.Stop();
            _foregroundCheckTimer.Stop();
            _timedCloseTimer.Stop();

            // 译文还在流式输出时浮窗被关掉（切走自动关闭、定时关闭、Ctrl+Q、再次截图…）很常见。
            // 渲染定时器不停的话会一直挂在 Dispatcher 上：每 100ms 空转一次，
            // 并且让整个已关闭的浮窗（含可视树和译文）无法被回收，关一次漏一个。
            if (_renderTimer != null)
            {
                _renderTimer.Stop();
                _renderTimer = null;
            }

            lock (_pendingLock)
            {
                _pendingText.Clear();
                _pendingText.Capacity = 16;
                _carry = "";
            }
        };

        // 拖动用 AddHandler + handledEventsToo:true 注册：
        // ScrollViewer（译文装在它里面）会把 MouseLeftButtonDown 标记为已处理，
        // 普通 += 收不到，表现就是点译文区域拖不动。handledEventsToo 强制接收。
        AddHandler(Mouse.MouseDownEvent, new MouseButtonEventHandler(OnDragStart), true);
    }

    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (IsInteractive(e.OriginalSource as DependencyObject)) return;

        App.Log($"drag start: src={e.OriginalSource?.GetType().Name}");
        _userMoved = true;
        Cursor = Cursors.SizeAll;

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            App.Log("drag: DragMove threw InvalidOperation");
        }
        catch (Exception ex)
        {
            App.Log("drag: DragMove threw " + ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            Cursor = Cursors.Arrow;
            FlushPending(force: true);
        }

        e.Handled = true;
    }

    /// <summary>点到按钮、输入框、滚动条时不算拖动。</summary>
    private static bool IsInteractive(DependencyObject? obj)
    {
        var guard = 0;

        while (obj != null && guard++ < 32)
        {
            if (obj is System.Windows.Controls.Primitives.ButtonBase) return true;
            if (obj is TextBox) return true;
            if (obj is System.Windows.Controls.Primitives.ScrollBar) return true;

            // 译文是逐块 Inlines.Add 进去的，点在文字上时 OriginalSource 是 Run。
            // Run 属于 ContentElement 不是 Visual，直接丢给 VisualTreeHelper
            // 会抛 InvalidOperationException，整个拖动流程就断了——表现就是
            // 翻译出文字后按哪儿都拖不动。
            try
            {
                obj = obj is System.Windows.Media.Visual
                    ? VisualTreeHelper.GetParent(obj)
                    : LogicalTreeHelper.GetParent(obj);
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        // 关键：不抢焦点，否则目标窗口的选区会立刻消失
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    /// <summary>记录取字来源窗口句柄，前台窗口切走它才启动自动关闭。</summary>
    public void SetSourceWindow(IntPtr sourceHwnd)
    {
        _sourceHwnd = sourceHwnd;
        App.Log($"source window set: {sourceHwnd}");
    }

    /// <summary>设置页窗口句柄：前台切到设置页不算失焦。</summary>
    public void SetSettingsWindow(IntPtr settingsHwnd) => _settingsHwnd = settingsHwnd;

    /// <summary>
    /// 启动定时自动关闭（不管切不切窗口，到时自动关）。
    /// 翻译开始时调用。悬停时暂停计时，移开继续。
    /// </summary>
    public void StartTimedClose()
    {
        if (!TimedCloseEnabled) return;

        _timedCloseTimer.Interval = TimeSpan.FromSeconds(Math.Max(1, TimedCloseSeconds));
        _timedCloseTimer.Stop();
        _timedCloseTimer.Start();
    }

    /// <summary>
    /// 检测前台窗口是否还是取字来源窗口。
    /// - 前台还是来源窗口（或设置页、或浮窗自身）→ 不算失焦，停倒计时，浮窗保留
    /// - 前台切到别的窗口 → 启动自动关闭倒计时（0 秒即立刻关）
    /// - 来源窗口被关闭 → 立即关闭浮窗
    /// </summary>
    private bool _lostFocusOnce;

    private void CheckForeground()
    {
        // 手动输入模式不自动关闭（用户要在别的窗口打字）
        if (_inputMode)
        {
            _autoCloseTimer.Stop();
            _lostFocusOnce = false;
            return;
        }

        // 没有记录到来源窗口（取词时前台窗口没拿到）→ 不启用自动关闭
        if (_sourceHwnd == IntPtr.Zero)
        {
            _autoCloseTimer.Stop();
            _lostFocusOnce = false;
            return;
        }

        // 来源窗口已经不存在（被关闭）→ 立即关浮窗
        if (!IsWindow(_sourceHwnd))
        {
            App.Log($"source window closed: {_sourceHwnd} -> close popup");
            try { Close(); } catch { }
            return;
        }

        var fg = GetForegroundWindow();

        // 前台还是来源窗口 → 没失焦
        if (fg == _sourceHwnd)
        {
            _autoCloseTimer.Stop();
            _lostFocusOnce = false;
            return;
        }

        // 前台是设置页 → 不算失焦
        if (_settingsHwnd != IntPtr.Zero && fg == _settingsHwnd)
        {
            _autoCloseTimer.Stop();
            _lostFocusOnce = false;
            return;
        }

        // 前台是浮窗自身 → 不算失焦
        var hwnd = new WindowInteropHelper(this).Handle;
        if (fg == hwnd)
        {
            _autoCloseTimer.Stop();
            _lostFocusOnce = false;
            return;
        }

        // 鼠标在浮窗上：按设置决定是否暂停倒计时。
        // 但 0 秒（切走立即关）时强制忽略悬停暂停——0 秒就是严格立即关。
        if (AutoCloseSeconds > 0 && IsMouseOver && AutoClosePauseOnHover)
        {
            _autoCloseTimer.Stop();
            _lostFocusOnce = false;
            return;
        }

        // 前台切到了别的窗口 → 失焦。
        App.Log($"fg!=source: fg={fg} source={_sourceHwnd} hover={IsMouseOver}");

        // 防抖：连续两次检测到切走才确认，避免前台窗口瞬时抖动
        if (!_lostFocusOnce)
        {
            _lostFocusOnce = true;
            return;
        }

        if (!AutoCloseEnabled) return;

        if (AutoCloseSeconds <= 0)
        {
            // 0 秒 = 确认切走后立即关闭
            App.Log("close: 0s immediate");
            try { Close(); } catch { }
            return;
        }

        // 只在定时器未运行时才设置 Interval 并启动（避免反复重置倒计时）
        if (!_autoCloseTimer.IsEnabled)
        {
            _autoCloseTimer.Interval = TimeSpan.FromSeconds(Math.Max(0, AutoCloseSeconds));
            _autoCloseTimer.Start();
        }
    }

    /// <summary>
    /// 设置浮窗背景的透明度。只改背景色的 alpha，文字保持完全不透明，
    /// 这样能看清背后的原文，又不会把译文看得发虚。
    /// </summary>
    public void ApplyOpacity(double value)
    {
        var v = Math.Clamp(value, 0.3, 1.0);
        var alpha = (byte)Math.Round(v * 255);

        RootBorder.Background = new SolidColorBrush(Color.FromArgb(alpha, 0xFF, 0xFF, 0xFF));

        // 原文/输入框的底衬略深一点，否则完全跟着主背景走会看不出层次
        var panelAlpha = (byte)Math.Round(Math.Min(1.0, v + 0.15) * 255);
        SourcePanel.Background = new SolidColorBrush(Color.FromArgb(panelAlpha, 0xF2, 0xF2, 0xF5));
        InputPanel.Background = new SolidColorBrush(Color.FromArgb(panelAlpha, 0xF2, 0xF2, 0xF5));
    }

    /// <summary>生成语言按钮。current 决定哪个高亮。</summary>
    public void ConfigureLanguages(IEnumerable<string> languages, string current)
    {
        _languages = languages.ToList();
        _currentTarget = current;

        LangPanel.Children.Clear();
        foreach (var lang in _languages)
        {
            var isActive = string.Equals(lang, current, StringComparison.Ordinal);
            var btn = new Button
            {
                Content = ShortLabel(lang),
                Tag = lang,
                FontSize = 11,
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(0, 0, 5, 0),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Background = isActive ? ActiveLangBg : IdleLangBg,
                Foreground = isActive ? ActiveLangFg : IdleLangFg,
                ToolTip = "翻译为" + lang,
            };
            btn.Click += (_, _) => LanguageRequested?.Invoke(lang);
            LangPanel.Children.Add(btn);
        }

        // 末尾的加号，用来直接编辑常用语言
        var edit = new Button
        {
            Content = "＋",
            FontSize = 11,
            Padding = new Thickness(7, 3, 7, 3),
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Background = IdleLangBg,
            Foreground = IdleLangFg,
            ToolTip = "增删浮窗上显示的语言",
        };
        edit.Click += (_, _) => ToggleLanguageEditor();
        LangPanel.Children.Add(edit);
    }

    private void ToggleLanguageEditor()
    {
        if (LangEditPanel.Visibility == Visibility.Visible)
        {
            CancelLanguageEdit();
            return;
        }

        // 编辑期间改的是副本，点确定才生效
        _editingLanguages = _languages.ToList();
        BuildLanguageEditor();
        LangEditPanel.Visibility = Visibility.Visible;
    }

    private void BuildLanguageEditor()
    {
        LangEditList.Items.Clear();

        foreach (var lang in AllLanguages)
        {
            var enabled = _editingLanguages.Contains(lang);
            var btn = new Button
            {
                Content = (enabled ? "✓ " : "") + lang,
                Tag = lang,
                FontSize = 11.5,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 6, 6),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Background = enabled ? ActiveLangBg : IdleLangBg,
                Foreground = enabled ? ActiveLangFg : IdleLangFg,
            };
            btn.Click += (_, _) => ToggleLanguage(lang);
            LangEditList.Items.Add(btn);
        }
    }

    private void ToggleLanguage(string lang)
    {
        if (_editingLanguages.Contains(lang))
        {
            if (_editingLanguages.Count <= 1) return; // 至少留一个
            _editingLanguages.Remove(lang);
        }
        else
        {
            _editingLanguages.Add(lang);
        }

        BuildLanguageEditor();
    }

    private void OnCancelLangEditClick(object sender, RoutedEventArgs e) => CancelLanguageEdit();

    private void CancelLanguageEdit()
    {
        LangEditPanel.Visibility = Visibility.Collapsed;
        _editingLanguages.Clear();
    }

    private void OnConfirmLangEditClick(object sender, RoutedEventArgs e)
    {
        if (_editingLanguages.Count == 0) return;

        _languages = _editingLanguages.ToList();
        ConfigureLanguages(_languages, _currentTarget);
        LangEditPanel.Visibility = Visibility.Collapsed;
        QuickLanguagesChanged?.Invoke(_languages.ToList());
    }

    private static string ShortLabel(string lang) => lang switch
    {
        "简体中文" => "中",
        "繁体中文" or "繁體中文" => "繁",
        "英语" => "英",
        "日语" => "日",
        "韩语" => "韩",
        "法语" => "法",
        "德语" => "德",
        "西班牙语" => "西",
        "俄语" => "俄",
        _ => lang.Length > 2 ? lang[..2] : lang,
    };

    public void ShowSource(string source, string engineLabel)
    {
        _inputMode = false;
        SourcePanel.Visibility = Visibility.Visible;
        InputPanel.Visibility = Visibility.Collapsed;
        SourceText.Text = source;
        EngineText.Text = engineLabel;
        ResultText.Text = "";
        ResultText.Foreground = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1E));
        StatusText.Text = "翻译中…";
        FooterPanel.Visibility = Visibility.Visible;
        CopyButton.Visibility = Visibility.Collapsed;
        _hasResult = false;
    }

    /// <summary>手动输入模式的翻译：输入框保持可见，用户改完可以再提交。</summary>
    public void StartManualTranslate(string engineLabel, string targetLabel)
    {
        _inputMode = true;
        SourcePanel.Visibility = Visibility.Collapsed;
        InputPanel.Visibility = Visibility.Visible;
        EngineText.Text = engineLabel;
        ResultText.Text = "";
        ResultText.Foreground = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1E));
        StatusText.Text = $"翻译为 {targetLabel} …";
        FooterPanel.Visibility = Visibility.Visible;
        CopyButton.Visibility = Visibility.Collapsed;
        _hasResult = false;
    }

    /// <summary>切到手动输入模式：拿掉 NOACTIVATE，允许用户直接打字。</summary>
    public void EnterInputMode(string hint)
    {
        _inputMode = true;
        _autoCloseTimer.Stop();

        SourcePanel.Visibility = Visibility.Collapsed;
        InputPanel.Visibility = Visibility.Visible;
        // 输入模式下也把语言行露出来，打字前就能选目标语言
        FooterPanel.Visibility = Visibility.Visible;
        CopyButton.Visibility = Visibility.Collapsed;
        ResultText.Text = "";
        StatusText.Text = hint;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, ex & ~WS_EX_NOACTIVATE);
            SetForegroundWindow(hwnd);
        }

        // 窗口刚拿回激活权时键盘焦点还没落下来，延后一拍再聚焦输入框
        Dispatcher.BeginInvoke(new Action(() =>
        {
            Activate();
            InputBox.Focus();
            Keyboard.Focus(InputBox);
            InputBox.CaretIndex = InputBox.Text?.Length ?? 0;
        }), DispatcherPriority.Input);
    }

    public bool IsInputMode => _inputMode;

    public void LeaveInputMode()
    {
        _inputMode = false;
        InputPanel.Visibility = Visibility.Collapsed;
        SourcePanel.Visibility = Visibility.Visible;

        // 回到不抢焦点的状态，避免后面点浮窗把目标窗口的选区弄丢
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        }
    }

    public void StartTranslate(string targetLabel)
    {
        ResultText.Text = "";
        ResultText.Foreground = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1E));
        StatusText.Text = $"翻译为 {targetLabel} …";
        FooterPanel.Visibility = Visibility.Visible;
        CopyButton.Visibility = Visibility.Collapsed;
        _hasResult = false;
        if (!_inputMode) _autoCloseTimer.Stop();
    }

    private readonly StringBuilder _pendingText = new();
    private readonly object _pendingLock = new();
    private System.Windows.Threading.DispatcherTimer? _renderTimer;

    /// <summary>
    /// 流式追加。**在后台线程调用**：只把内容塞进线程安全缓冲区，不碰任何 UI 控件。
    /// 真正刷新交给 UI 线程的 DispatcherTimer（每 100ms 一拍）做，
    /// 这样翻译数据的接收完全不影响 UI 线程——鼠标拖动、点击零卡顿。
    /// </summary>
    public void AppendChunk(string piece)
    {
        if (piece.Length == 0) return;

        lock (_pendingLock)
        {
            // Hy-MT2 偶尔把换行输出成字面量 /nt/n。以前直接全局替换 "/n"，
            // 会把 "yes/no"、"/news"、"and/not" 这类正常文字也破坏掉（变成 yes⏎o）。
            // 现在只替换后面不跟字母数字的 /n；被切在两个片段中间的 "/n" 先留到下一片段再判断。
            var text = _carry + piece;
            _carry = "";

            var hold = TrailingHold(text);
            if (hold > 0)
            {
                _carry = text[^hold..];
                text = text[..^hold];
            }

            if (text.Length > 0) _pendingText.Append(FixNewlines(text));
        }
        _hasResult = true;
    }

    private string _carry = "";
    private static readonly System.Text.RegularExpressions.Regex NewlineLiteral =
        new(@"/nt/n|/n(?![A-Za-z0-9_])");

    private static string FixNewlines(string s) =>
        s.Contains("/n", StringComparison.Ordinal) ? NewlineLiteral.Replace(s, "\n") : s;

    /// <summary>末尾如果是 "/nt/n" 的不完整前缀，返回需要暂存的字符数。</summary>
    private static int TrailingHold(string s)
    {
        if (s.EndsWith("/nt/n", StringComparison.Ordinal)) return 0;   // 已经是完整的，直接替换
        foreach (var p in new[] { "/nt/", "/nt", "/n", "/" })
            if (s.EndsWith(p, StringComparison.Ordinal)) return p.Length;
        return 0;
    }

    /// <summary>翻译结束：把暂存的末尾字符按“已到结尾”处理后放进缓冲区。</summary>
    private void FlushCarry()
    {
        lock (_pendingLock)
        {
            if (_carry.Length == 0) return;
            _pendingText.Append(FixNewlines(_carry));
            _carry = "";
        }
    }

    /// <summary>开始/重启渲染定时器。翻译开始时调用，UI 线程每 100ms 拉一次数据刷屏。</summary>
    public void StartStreaming()
    {
        if (_closed) return;   // 已关闭的浮窗不能再建渲染定时器，否则又会挂在 Dispatcher 上漏掉
        if (_renderTimer == null)
        {
            _renderTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100),
            };
            _renderTimer.Tick += (_, _) => FlushPending();
        }
        _renderTimer.Stop();
        _renderTimer.Start();
    }

    /// <summary>
    /// 设置窗口打开时调用：把刷屏频率从 100ms 降到 500ms。
    /// 不是完全暂停——那样译文会"冻结"到设置关闭。降低频率既能把 UI 线程
    /// 让给设置窗口的首渲染（消除卡顿），又让译文继续缓慢流动。
    /// </summary>
    public void SlowStreaming()
    {
        if (_renderTimer != null) _renderTimer.Interval = TimeSpan.FromMilliseconds(500);
    }

    /// <summary>恢复流式刷屏到 100ms，并把积压内容补上。</summary>
    public void ResumeStreaming()
    {
        FlushPending(force: true);
        if (_renderTimer != null) _renderTimer.Interval = TimeSpan.FromMilliseconds(100);
        _renderTimer?.Start();
    }

    /// <summary>把缓冲区内容写进界面。force 用于拖动结束/翻译结束时强制刷新。</summary>
    private void FlushPending(bool force = false)
    {
        // 翻译现在在后台线程，UI 线程空闲。拖动（DragMove 阻塞）期间
        // 也照常刷屏——不再用 _dragging 拦，否则拖动时译文动画会暂停，
        // 松开才一次性补上。
        string? s = null;
        lock (_pendingLock)
        {
            if (_pendingText.Length > 0)
            {
                s = _pendingText.ToString();
                _pendingText.Clear();
            }
        }

        if (string.IsNullOrEmpty(s)) return;

        // 用 Inlines.Add 增量追加，而不是 ResultText.Text += s：
        // Text += 每次都对整段译文做全量重排，译文越长越卡，流式动画掉帧。
        // Inlines.Add 只排版新增的 Run，WPF 增量更新，动画顺滑。
        ResultText.Inlines.Add(new System.Windows.Documents.Run(s));

        // 只在译文确实超出可视区时才滚到底，避免每次 flush 都触发
        // ScrollToEnd 的布局测量，减少掉帧。
        if (ResultScroll.ExtentHeight > ResultScroll.ViewportHeight + 4)
            ResultScroll.ScrollToEnd();
    }

    public void Finish()
    {
        // 翻译完成后才启动定时自动关闭（如果开启了）
        StartTimedClose();

        // 停止渲染定时器，把缓冲区剩余内容一次性刷出来
        _renderTimer?.Stop();
        FlushCarry();
        FlushPending(force: true);

        // 通用模型（Qwen3 等）偶尔把指令复述成开头的套话，这里剥掉
        var cleaned = StripPreamble(ResultText.Text);
        if (!string.Equals(cleaned, ResultText.Text, StringComparison.Ordinal))
        {
            ResultText.Text = cleaned;
            if (string.IsNullOrWhiteSpace(cleaned)) _hasResult = false;
        }

        StatusText.Text = _hasResult ? $"完成 · {ResultText.Text.Length} 字" : "没有返回内容";
        CopyButton.Visibility = _hasResult ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>去掉模型在译文前附加的常见套话。</summary>
    private static string StripPreamble(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        var t = text.TrimStart();
        string[] preambles =
        {
            "请注意，您仅需输出翻译结果，无需任何解释",
            "请注意，只需输出翻译结果",
            "请注意，只输出译文",
            "以下是翻译结果",
            "以下是译文",
            "以下是翻译",
            "翻译结果如下",
            "译文如下",
            "The translation is:",
        };

        foreach (var p in preambles)
        {
            if (t.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            {
                return t[p.Length..].TrimStart('：', ':', '，', ',', '。', '.', ' ', '\r', '\n', '\u201c', '"');
            }
        }

        return text;
    }

    public void SetStatus(string text) => StatusText.Text = text;

    public void ShowError(string message)
    {
        ResultText.Text = message;
        ResultText.Foreground = Brushes.Firebrick;
        StatusText.Text = "失败";
        CopyButton.Visibility = Visibility.Collapsed;
        _autoCloseTimer.Stop();
    }

    private void OnInputKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // 只认 Ctrl+Enter 提交（中文输入法激活时也可靠）。
        // 普通 Enter 让 TextBox 正常换行，这样粘贴多段文本时换行不会被吞掉。
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            e.Handled = true;
            SubmitInput();
        }
    }

    private void OnInputSubmitClick(object sender, RoutedEventArgs e) => SubmitInput();

    private void SubmitInput()
    {
        var text = InputBox.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;
        ManualTextSubmitted?.Invoke(text);
    }

    private void OnOcrClick(object sender, RoutedEventArgs e) => OcrRequested?.Invoke();

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        App.Log("settings button clicked");
        SettingsRequested?.Invoke();
    }

    private void OnSpeakClick(object sender, RoutedEventArgs e)
    {
        if (!_hasResult) return;
        SpeakRequested?.Invoke(ResultText.Text);
    }

    /// <summary>设置喇叭按钮的朗读中状态（App 层朗读开始/停止时回调）。</summary>
    public void SetSpeaking(bool speaking)
    {
        // 朗读中高亮，提示用户再点一下停止
        SpeakButton.Opacity = speaking ? 1.0 : 0.55;
        SpeakButton.ToolTip = speaking ? "停止朗读" : "朗读译文";
    }

    /// <summary>根据配置决定是否显示喇叭按钮。</summary>
    public void ApplyTtsVisibility(bool enabled)
    {
        SpeakButton.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (!_hasResult) return;
        try
        {
            Clipboard.SetText(ResultText.Text);
            StatusText.Text = "已复制";
        }
        catch
        {
            StatusText.Text = "复制失败";
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
        base.OnKeyDown(e);
    }

    /// <summary>把浮窗定位到选中文字的旁边。传 null 则回到跟随鼠标。</summary>
    public void SetAnchor(Int32Rect? rect)
    {
        _anchorRect = rect;
        if (_positioned) Reposition(true);
    }

    /// <summary>
    /// 摆放窗口。resetPosition 为 true 时按锚点（选区或光标）重新计算位置；
    /// 为 false 时只把窗口拉回屏幕内、保持当前左上角——这样展开面板、译文变长都不会乱跳。
    /// </summary>
    private void Reposition(bool resetPosition = false)
    {
        if (!IsVisible) return;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        GetCursorPos(out var cursor);

        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        // 优先按选区所在屏幕算边界，没有选区时按光标所在屏幕
        var probe = _anchorRect.HasValue
            ? new POINT { X = _anchorRect.Value.X, Y = _anchorRect.Value.Y }
            : cursor;
        var mon = MonitorFromPoint(probe, MONITOR_DEFAULTTONEAREST);
        if (!GetMonitorInfo(mon, ref mi)) return;

        var work = mi.rcWork;
        var dpi = GetDpiForWindow(hwnd);
        var scale = dpi == 0 ? 1.0 : dpi / 96.0;

        var w = (int)Math.Ceiling(ActualWidth * scale);
        var h = (int)Math.Ceiling(ActualHeight * scale);

        int x, y;

        if (resetPosition)
        {
            if (_anchorRect.HasValue)
            {
                var a = _anchorRect.Value;

                // 默认贴在选区左下方，别盖住选中的字
                x = a.X;
                y = a.Y + a.Height + 10;

                if (y + h > work.Bottom)
                {
                    // 下方放不下就翻到选区上方
                    y = a.Y - h - 10;
                }

                if (x + w > work.Right) x = a.X + a.Width - w;
            }
            else
            {
                x = cursor.X + 14;
                y = cursor.Y + 18;

                if (x + w > work.Right) x = cursor.X - w - 14;
                if (y + h > work.Bottom) y = cursor.Y - h - 18;
            }
        }
        else
        {
            // 保持当前位置，只在越界时做最小修正
            if (!GetWindowRect(hwnd, out var current)) return;
            x = current.Left;
            y = current.Top;
        }

        x = Math.Max(work.Left + 4, Math.Min(x, Math.Max(work.Left + 4, work.Right - w - 4)));
        y = Math.Max(work.Top + 4, Math.Min(y, Math.Max(work.Top + 4, work.Bottom - h - 4)));

        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }
}
