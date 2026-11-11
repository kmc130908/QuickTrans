using System.Runtime.InteropServices;
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
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int VK_ESCAPE = 0x1B;

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
    private bool _hasResult;
    private bool _positioned;
    private bool _inputMode;

    private bool _dragging;
    private int _dragScreenX;
    private int _dragScreenY;
    private int _dragRectLeft;
    private int _dragRectTop;
    private bool _userMoved;

    /// <summary>选中文字的屏幕矩形，有值就贴着它显示，否则跟随鼠标。</summary>
    private Int32Rect? _anchorRect;

    private static PopupWindow? _current;
    private static IntPtr _hookHandle = IntPtr.Zero;
    private static LowLevelKeyboardProc? _hookProc;

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

    public PopupWindow()
    {
        InitializeComponent();

        _autoCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _autoCloseTimer.Tick += (_, _) =>
        {
            _autoCloseTimer.Stop();
            if (IsMouseOver || _inputMode) return;
            Close();
        };

        MouseEnter += (_, _) => _autoCloseTimer.Stop();
        MouseLeave += (_, _) => { if (!_inputMode) _autoCloseTimer.Start(); };

        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) => { Reposition(true); _positioned = true; };
        SizeChanged += (_, _) => { if (_positioned && !_userMoved) Reposition(); };
        Closed += (_, _) =>
        {
            _autoCloseTimer.Stop();
            UninstallEscHook();
            if (ReferenceEquals(_current, this)) _current = null;
        };

        // 拖动：按在非控件区域时整体移动窗口
        MouseLeftButtonDown += OnDragStart;
        MouseMove += OnDragMove;
        MouseLeftButtonUp += OnDragEnd;
    }

    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (IsInteractive(e.OriginalSource as DependencyObject)) return;

        _dragging = true;
        _userMoved = true;

        // 全程用物理像素：GetCursorPos 取鼠标、GetWindowRect 取窗口，
        // 拖动时直接 SetWindowPos，不经过 WPF 的 DIP 换算，避免缩放导致的偏移
        GetCursorPos(out var pt);
        _dragScreenX = pt.X;
        _dragScreenY = pt.Y;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (GetWindowRect(hwnd, out var rect))
        {
            _dragRectLeft = rect.Left;
            _dragRectTop = rect.Top;
        }
        else
        {
            _dragging = false;
            return;
        }

        CaptureMouse();
        Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;

        GetCursorPos(out var pt);
        var hwnd = new WindowInteropHelper(this).Handle;

        SetWindowPos(hwnd, IntPtr.Zero,
            _dragRectLeft + (pt.X - _dragScreenX),
            _dragRectTop + (pt.Y - _dragScreenY),
            0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    private void OnDragEnd(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        Cursor = Cursors.Arrow;
    }

    /// <summary>点到按钮、输入框、滚动条时不算拖动。</summary>
    private static bool IsInteractive(DependencyObject? obj)
    {
        while (obj != null)
        {
            if (obj is System.Windows.Controls.Primitives.ButtonBase) return true;
            if (obj is TextBox) return true;
            if (obj is System.Windows.Controls.Primitives.ScrollBar) return true;
            if (obj is ScrollViewer) return true;
            obj = VisualTreeHelper.GetParent(obj);
        }
        return false;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        // 关键：不抢焦点，否则目标窗口的选区会立刻消失
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);

        InstallEscHook();
    }

    /// <summary>装一个低级键盘钩子，让窗口即使没有焦点也能用 Esc 关闭。</summary>
    private void InstallEscHook()
    {
        if (_hookHandle != IntPtr.Zero) return;

        _current = this;
        _hookProc = HookProc;
        _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, GetModuleHandle(null), 0);
    }

    private static void UninstallEscHook()
    {
        if (_hookHandle == IntPtr.Zero) return;

        UnhookWindowsHookEx(_hookHandle);
        _hookHandle = IntPtr.Zero;
        _hookProc = null;
    }

    private static IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == (IntPtr)WM_KEYDOWN)
        {
            var vk = Marshal.ReadInt32(lParam);
            if (vk == VK_ESCAPE)
            {
                var target = _current;
                if (target != null)
                {
                    target.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try { target.Close(); } catch { /* 窗口可能已经关了 */ }
                    }));

                    // 吞掉这次 Esc，避免同时触发别的程序
                    return (IntPtr)1;
                }
            }
        }

        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
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

    public void AppendChunk(string piece)
    {
        // Hy-MT2 偶尔把换行输出成字面量 /nt/n，顺手清掉
        piece = piece.Replace("/nt/n", "\n").Replace("/n", "\n");
        ResultText.Text += piece;
        _hasResult = true;
        ResultScroll.ScrollToEnd();
    }

    public void Finish()
    {
        // 通用模型（Qwen3 等）偶尔把指令复述成开头的套话，这里剥掉
        var cleaned = StripPreamble(ResultText.Text);
        if (!string.Equals(cleaned, ResultText.Text, StringComparison.Ordinal))
        {
            ResultText.Text = cleaned;
            if (string.IsNullOrWhiteSpace(cleaned)) _hasResult = false;
        }

        StatusText.Text = _hasResult ? $"完成 · {ResultText.Text.Length} 字" : "没有返回内容";
        CopyButton.Visibility = _hasResult ? Visibility.Visible : Visibility.Collapsed;
        if (!_inputMode) _autoCloseTimer.Start();
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
        SettingsRequested?.Invoke();
    }

    private void OnSpeakClick(object sender, RoutedEventArgs e)
    {
        if (!_hasResult) return;
        SpeakRequested?.Invoke(ResultText.Text);
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
