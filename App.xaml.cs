using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using QuickTrans.Models;
using QuickTrans.Services;
using QuickTrans.Views;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace QuickTrans;

public partial class App : System.Windows.Application
{
    static App()
    {
        // 纯 2D 文字浮窗与设置表单无需 GPU 硬件渲染，软件渲染可减少约 5–11MB 常驻内存
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
    }

    private Mutex? _mutex;
    private bool _ownsMutex;
    private WinForms.NotifyIcon? _tray;
    private HotkeyManager? _hotkey;
    private HotkeyManager? _ocrHotkey;
    private HotkeyManager? _replaceHotkey;
    private ClipboardWatcher? _clipboardWatcher;
    private EngineManager? _engine;
    private AppIpcServer? _ipc;
    private Process? _settingsProc;
    private IntPtr _settingsChildHwnd;
    private Process? _inputProc;
    private IntPtr _inputChildHwnd;
    private Process? _popupProc;
    private IntPtr _popupChildHwnd;
    private string? _pendingTranslateJobJson;
    private Process? _componentProc;
    private Process? _historyProc;
    private IntPtr _historyChildHwnd;
    private AppConfig _config = null!;
    private PopupWindow? _popup;
    private SettingsWindow? _settingsWin;
    private SpeechService? _speech;
    private WinForms.ToolStripMenuItem? _updateItem;
    private UpdateService.UpdateInfo? _pendingUpdate;
    private CancellationTokenSource? _cts;
    private System.Windows.Threading.DispatcherTimer? _idleTimer;
    private string _currentSource = "";
    private string _currentTarget = "";
    private string _currentMode = "translate";

    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "quicktrans.log");

    public static void Log(string message)
    {
        try
        {
            File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
        }
        catch { /* 日志写不进不影响运行 */ }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log("FATAL: " + args.ExceptionObject);
        DispatcherUnhandledException += (_, args) =>
        {
            Log("DISPATCHER: " + args.Exception);
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log("TASK: " + args.Exception);
            args.SetObserved();
        };

        // 子进程模式：设置页 / 首次引导 / 组件管理在独立短生命周期进程里运行。
        // 窗口关闭后进程直接退出，系统注入的输入法（IME）、D3D11 显卡驱动与语音库内存
        // 会 100% 归还操作系统，不污染常驻主进程。
        if (e.Args.Any(a => a.Equals("--settings-child", StringComparison.OrdinalIgnoreCase)))
        {
            RunSettingsChild();
            return;
        }
        if (e.Args.Any(a => a.Equals("--welcome-child", StringComparison.OrdinalIgnoreCase)))
        {
            RunWelcomeChild();
            return;
        }
        if (e.Args.Any(a => a.Equals("--components-child", StringComparison.OrdinalIgnoreCase)))
        {
            RunComponentsChild();
            return;
        }
        if (e.Args.Any(a => a.Equals("--input-child", StringComparison.OrdinalIgnoreCase)))
        {
            RunInputChild();
            return;
        }
        if (e.Args.Any(a => a.Equals("--translate-child", StringComparison.OrdinalIgnoreCase)))
        {
            RunPopupChild(ocrMode: false);
            return;
        }
        if (e.Args.Any(a => a.Equals("--ocr-child", StringComparison.OrdinalIgnoreCase)))
        {
            RunPopupChild(ocrMode: true);
            return;
        }
        if (e.Args.Any(a => a.Equals("--history-child", StringComparison.OrdinalIgnoreCase)))
        {
            RunHistoryChild();
            return;
        }

        Log("=== startup ===");

        _mutex = new Mutex(true, @"Local\QuickTrans_SingleInstance", out var isNew);
        if (!isNew)
        {
            WinForms.MessageBox.Show("QuickTrans 已经在运行了，看任务栏右下角的托盘图标。",
                "QuickTrans", WinForms.MessageBoxButtons.OK, WinForms.MessageBoxIcon.Information);
            Shutdown();
            return;
        }

        _ownsMutex = true;

        _config = AppConfig.Load();
        Log($"config: hotkey={_config.Hotkey} ocr={_config.OcrHotkey} engine={_config.Engine} " +
            $"model={_config.SelectedModel} unload={_config.UnloadPolicy}/{_config.UnloadMinutes}min");

        // 上次被强杀/崩溃时可能留下占显存的 llama-server，启动先清一遍
        var orphans = EngineManager.KillOrphanServers();
        if (orphans > 0) Log($"cleaned {orphans} orphan llama-server process(es)");

        _engine = new EngineManager(AppContext.BaseDirectory, _config.EngineVariant);

        // 配置里要求自启就把注册表补上，取消勾选则清掉
        AutoStartService.Sync(_config.AutoStart);

        Log($"engine variant={_engine.EngineVariant} dir={_engine.EngineDir} " +
            $"gpu={_config.UseGpu} autostart={_config.AutoStart}");

        BuildTray();
        RegisterHotkeys();
        SetupIdleTimer();
        _ipc = new AppIpcServer(HandleIpcAsync);

        if (_config.PrewarmModel && string.Equals(_config.Engine, "local", StringComparison.OrdinalIgnoreCase))
            _ = PrewarmAsync();

        if (e.Args.Any(a => a.Equals("--settings", StringComparison.OrdinalIgnoreCase)))
        {
            Dispatcher.BeginInvoke(new Action(OpenSettings),
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        Log("startup complete");

        // 后台检查新版本，失败静默
        UpdateService.CleanupOldExe();
        _ = CheckUpdateAsync();

        // 上次下载了新版本但没重启：提醒一句，更新入口在设置页
        if (UpdateService.HasPendingUpdate())
        {
            try
            {
                _tray?.ShowBalloonTip(8000, "新版本已下载",
                    "打开设置 → 关于与更新 → 重启并更新，即可完成升级。",
                    WinForms.ToolTipIcon.Info);
            }
            catch { }
        }

        // 安装向导里勾选了要下载的组件时，程序带 --install-components 参数启动，
        // 这里直接打开组件窗口并自动开始下载，跳过欢迎页（安装时已经看过说明）。
        var autoComponents = ParseComponentArg();
        var interrupted = false;    // 已经弹过窗口的话，就不再弹设置页，避免叠在一起

        if (autoComponents.Length > 0)
        {
            interrupted = true;
            _config.WelcomeShown = true;
            _config.ComponentHintShown = true;
            _config.Save();
            Dispatcher.BeginInvoke(new Action(() => OpenComponentManager(autoComponents)),
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
        // 首次使用引导：优先于其他弹窗，只出现一次（在独立子进程里运行，关掉后不留驻内存）
        else if (!_config.WelcomeShown)
        {
            interrupted = true;
            _config.WelcomeShown = true;
            _config.Save();
            var needGuideAfterWelcome = !_config.ComponentHintShown && _engine != null
                && (_engine.ScanModels().Count == 0 || (HasNvidiaGpu() && !HasGpuEngine()));
            if (needGuideAfterWelcome)
            {
                _config.ComponentHintShown = true;
                _config.Save();
            }

            Dispatcher.BeginInvoke(new Action(async () =>
            {
                await RunChildWindowAndWaitAsync("--welcome-child");
                if (needGuideAfterWelcome) OpenComponentManager();
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        // 首次运行引导：
        //  - 一个模型都没有 → 必然要引导
        //  - 有 NVIDIA 显卡但装的是 CPU 引擎 → 提示可以下 GPU 引擎
        // 只提示一次，之后用户自己从托盘菜单进。
        var guided = false;
        if (!interrupted && !_config.ComponentHintShown && _engine != null)
        {
            var needModel = _engine.ScanModels().Count == 0;
            var canUpgradeGpu = HasNvidiaGpu() && !HasGpuEngine();

            if (needModel || canUpgradeGpu)
            {
                guided = true;
                _config.ComponentHintShown = true;
                _config.Save();
                Dispatcher.BeginInvoke(new Action(() => OpenComponentManager()),
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
        }

        // 手动启动（不是开机自启）时弹一下设置页，让用户确认软件确实起来了。
        // 如果刚弹过组件引导，就不重复弹，避免两个窗口叠在一起。
        if (!guided && !interrupted && !AutoStartService.IsAutoStartLaunch() && _config.ShowSettingsOnStartup)
        {
            Dispatcher.BeginInvoke(new Action(OpenSettings),
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
    }

    private string EngineLabel()
    {
        if (string.Equals(_config.Engine, "api", StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrWhiteSpace(_config.ApiModel) ? "在线接口" : $"在线 · {_config.ApiModel}";

        var name = string.IsNullOrWhiteSpace(_config.SelectedModel) ? "本地模型" : _config.SelectedModel;
        if (name.Length > 26) name = name[..26] + "…";
        return "本地 · " + name;
    }

    // ---------- 托盘 ----------

    private void BuildTray()
    {
        _tray = new WinForms.NotifyIcon
        {
            Icon = CreateTrayIcon(_config.TrayIconStyle),
            Text = "QuickTrans · 划词翻译",
            Visible = true,
        };

        var menu = new WinForms.ContextMenuStrip();

        var translateItem = new WinForms.ToolStripMenuItem($"翻译选中文本  ({_config.Hotkey})");
        translateItem.Click += async (_, _) => await TranslateSelectionAsync();

        var ocrItem = new WinForms.ToolStripMenuItem($"截图翻译  ({_config.OcrHotkey})");
        ocrItem.Click += async (_, _) => await StartOcrAsync();

        var repKey = string.IsNullOrWhiteSpace(_config.ReplaceHotkey) ? "Alt+R" : _config.ReplaceHotkey;
        var replaceItem = new WinForms.ToolStripMenuItem($"替换翻译  ({repKey})");
        replaceItem.Click += async (_, _) => await ReplaceSelectionAsync();

        var inputItem = new WinForms.ToolStripMenuItem("手动输入翻译");
        inputItem.Click += (_, _) => OpenInputPopup();

        var historyItem = new WinForms.ToolStripMenuItem("翻译历史…");
        historyItem.Click += (_, _) => OpenHistory();

        var settingsItem = new WinForms.ToolStripMenuItem("设置…");
        settingsItem.Click += (_, _) => OpenSettings();

        var componentsItem = new WinForms.ToolStripMenuItem("组件管理（引擎/模型）…");
        componentsItem.Click += (_, _) => OpenComponentManager();

        _updateItem = new WinForms.ToolStripMenuItem("发现新版本…")
        {
            Visible = false,
            ForeColor = System.Drawing.Color.FromArgb(0x3B, 0x5B, 0xA8),
        };
        _updateItem.Click += (_, _) => OpenSettings();

        var modelMenu = new WinForms.ToolStripMenuItem("切换本地模型");
        modelMenu.DropDownOpening += (_, _) => FillModelMenu(modelMenu);

        var exitItem = new WinForms.ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => ExitApp();

        menu.Items.Add(translateItem);
        menu.Items.Add(ocrItem);
        menu.Items.Add(replaceItem);
        menu.Items.Add(inputItem);
        menu.Items.Add(historyItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(modelMenu);
        menu.Items.Add(componentsItem);
        menu.Items.Add(_updateItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => OpenSettings();
    }

    private void FillModelMenu(WinForms.ToolStripMenuItem parent)
    {
        parent.DropDownItems.Clear();
        var models = _engine?.ScanModels() ?? new List<ModelInfo>();

        if (models.Count == 0)
        {
            parent.DropDownItems.Add(new WinForms.ToolStripMenuItem("（models 目录里还没有模型）") { Enabled = false });
            return;
        }

        foreach (var m in models)
        {
            var item = new WinForms.ToolStripMenuItem($"{m.DisplayName}  ·  {m.SizeText}")
            {
                Checked = string.Equals(m.DisplayName, _config.SelectedModel, StringComparison.OrdinalIgnoreCase),
            };
            var captured = m;
            item.Click += async (_, _) =>
            {
                _config.SelectedModel = captured.DisplayName;
                _config.Engine = "local";
                _config.Save();
                await PrewarmAsync();
            };
            parent.DropDownItems.Add(item);
        }
    }

    private static Drawing.Icon CreateTrayIcon(int style)
    {
        // 优先用打包进资源的图标，换样式只要换编号
        try
        {
            var uri = new Uri($"pack://application:,,,/assets/tray{style}.ico", UriKind.Absolute);
            var res = GetResourceStream(uri);
            if (res?.Stream != null)
            {
                using var stream = res.Stream;
                return new Drawing.Icon(stream);
            }
        }
        catch
        {
            // 资源缺失时退回下面手绘的图标
        }

        using var bmp = new Drawing.Bitmap(32, 32);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(Drawing.Color.Transparent);
            using var brush = new Drawing.SolidBrush(Drawing.Color.FromArgb(59, 91, 168));
            g.FillEllipse(brush, 0, 0, 31, 31);
            using var font = new Drawing.Font("Microsoft YaHei UI", 15, Drawing.FontStyle.Bold, Drawing.GraphicsUnit.Pixel);
            using var fmt = new Drawing.StringFormat
            {
                Alignment = Drawing.StringAlignment.Center,
                LineAlignment = Drawing.StringAlignment.Center,
            };
            g.DrawString("译", font, Drawing.Brushes.White, new Drawing.RectangleF(0, 1, 32, 32), fmt);
        }

        var handle = bmp.GetHicon();
        try
        {
            using var tmp = Drawing.Icon.FromHandle(handle);
            return (Drawing.Icon)tmp.Clone();
        }
        finally
        {
            // GetHicon 创建的句柄归调用方释放；Clone 已经复制了一份，原句柄不销毁就会泄漏
            DestroyIcon(handle);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    // ---------- 热键 ----------

    private void RegisterHotkeys()
    {
        _hotkey ??= new HotkeyManager();
        _hotkey.Pressed -= OnTranslateHotkey;
        _hotkey.Pressed += OnTranslateHotkey;
        if (!_hotkey.Register(_config.Hotkey))
        {
            Log("translate hotkey FAILED: " + _hotkey.LastError);
            _tray?.ShowBalloonTip(4000, "热键注册失败",
                $"{_config.Hotkey} 被其他程序占用，请到设置里换一个。", WinForms.ToolTipIcon.Warning);
        }
        else Log("translate hotkey: " + _config.Hotkey);

        _ocrHotkey ??= new HotkeyManager();
        _ocrHotkey.Pressed -= OnOcrHotkey;
        _ocrHotkey.Pressed += OnOcrHotkey;
        if (!_ocrHotkey.Register(_config.OcrHotkey))
        {
            Log("ocr hotkey FAILED: " + _ocrHotkey.LastError);
            _tray?.ShowBalloonTip(4000, "截图热键注册失败",
                $"{_config.OcrHotkey} 被其他程序占用，请到设置里换一个。", WinForms.ToolTipIcon.Warning);
        }
        else Log("ocr hotkey: " + _config.OcrHotkey);

        var repKey = string.IsNullOrWhiteSpace(_config.ReplaceHotkey) ? "Alt+R" : _config.ReplaceHotkey;
        _replaceHotkey ??= new HotkeyManager();
        _replaceHotkey.Pressed -= OnReplaceHotkey;
        _replaceHotkey.Pressed += OnReplaceHotkey;
        if (!_replaceHotkey.Register(repKey))
        {
            Log("replace hotkey FAILED: " + _replaceHotkey.LastError);
            _tray?.ShowBalloonTip(4000, "替换翻译热键注册失败",
                $"{repKey} 被其他程序占用，请到设置 → 常规与热键里更换。", WinForms.ToolTipIcon.Warning);
        }
        else Log("replace hotkey: " + repKey);

        _clipboardWatcher ??= new ClipboardWatcher();
        _clipboardWatcher.DoubleCopyDetected -= OnDoubleCopyDetected;
        _clipboardWatcher.DoubleCopyDetected += OnDoubleCopyDetected;
        _clipboardWatcher.SetEnabled(_config.DoubleCtrlCEnabled);
    }

    private async void OnDoubleCopyDetected(string text, IntPtr sourceHwnd)
    {
        if (DndService.ShouldBlock(_config, out var reason))
        {
            Log("dnd blocked (double Ctrl+C): " + reason);
            return;
        }
        Log($"double Ctrl+C detected: len={text.Length} hwnd={sourceHwnd}");
        if (_config.SmartJoinLines) text = TextGrabber.CleanLineBreaks(text);
        if (string.IsNullOrWhiteSpace(text)) return;

        if (_config.PopupIsolateProcess && !string.IsNullOrWhiteSpace(Environment.ProcessPath))
        {
            if (_popup is { IsVisible: true }) { _popup.Close(); _popup = null; }
            if (_popupProc is { HasExited: false }) ClosePopupChild();
            if (!string.Equals(_config.Engine, "api", StringComparison.OrdinalIgnoreCase)
                && _engine != null && !_engine.IsReady)
                _ = PrewarmAsync();
            LaunchTranslateChild(text, sourceHwnd, null);
            return;
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _popup?.Close();
        _popup = CreatePopup();
        _popup.Show();
        _popup.SetSourceWindow(sourceHwnd);
        _popup.SetAnchor(null);
        await TranslateTextAsync(text);
    }

    private async void OnReplaceHotkey(object? sender, EventArgs e)
    {
        if (DndService.ShouldBlock(_config, out var reason))
        {
            Log("dnd blocked (replace): " + reason);
            return;
        }
        Log("replace hotkey pressed");
        await ReplaceSelectionAsync();
    }

    /// <summary>
    /// 一键替换翻译（快捷键触发）：取当前选中文本 → 翻译 → 直接粘贴替换原窗口选区。
    /// 若翻译完成前用户已切走窗口，则把译文写入剪贴板并气泡提示，不误贴到其它窗口。
    /// </summary>
    private async Task ReplaceSelectionAsync()
    {
        string? text;
        IntPtr sourceHwnd;
        try
        {
            text = TextGrabber.Grab(_config.GrabMode);
            sourceHwnd = TextGrabber.LastSourceHwnd;
        }
        catch (Exception ex)
        {
            Log("replace grab failed: " + ex.Message);
            return;
        }

        if (string.IsNullOrWhiteSpace(text)) return;
        if (_config.SmartJoinLines) text = TextGrabber.CleanLineBreaks(text);

        var target = TranslationService.ResolveTarget(text, _config);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var ct = cts.Token;

        try
        {
            var baseUrl = "";
            if (!string.Equals(_config.Engine, "api", StringComparison.OrdinalIgnoreCase))
            {
                var ready = await EnsureEngineAsync(ct);
                if (!ready.Ok)
                {
                    try { _tray?.ShowBalloonTip(3500, "替换翻译失败", ready.Message, WinForms.ToolTipIcon.Warning); } catch { }
                    return;
                }
                baseUrl = _engine!.BaseUrl;
            }

            var sb = new System.Text.StringBuilder();
            await Task.Run(async () =>
            {
                await foreach (var piece in TranslationService.TranslateAsync(text, _config, baseUrl, target, ct))
                    sb.Append(piece);
            }, ct);

            _engine?.MarkUsed();

            var translated = PopupWindow.StripPreamble(
                System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"/nt/n|/n(?![A-Za-z0-9_])", "\n")).Trim();
            if (string.IsNullOrWhiteSpace(translated)) return;

            if (_config.HistoryEnabled)
                HistoryService.Add(text, translated, target, "translate", EngineLabel());

            var pasted = await Task.Run(() => TextGrabber.PasteToWindow(sourceHwnd, translated));
            Log($"replace done: pasted={pasted} len={translated.Length}");
            if (!pasted)
            {
                try
                {
                    ClipboardWatcher.SuppressFor(700);
                    Clipboard.SetText(translated);
                    _tray?.ShowBalloonTip(3000, "译文已复制", "原窗口已不可用，译文已放入剪贴板。", WinForms.ToolTipIcon.Info);
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Log("replace translate failed: " + ex.Message);
            try { _tray?.ShowBalloonTip(3500, "替换翻译失败", ex.Message, WinForms.ToolTipIcon.Warning); } catch { }
        }
    }

    private async void OnTranslateHotkey(object? sender, EventArgs e)
    {
        Log("translate hotkey pressed");
        if (_inputProc is { HasExited: false })
        {
            CloseInputChild();
            return;
        }
        if (_popupProc is { HasExited: false })
        {
            ClosePopupChild();
            return;
        }
        if (_popup is { IsVisible: true })
        {
            _popup.Close();
            _popup = null;
            _cts?.Cancel();
            return;
        }
        if (DndService.ShouldBlock(_config, out var reason))
        {
            Log("dnd blocked (translate): " + reason);
            return;
        }
        await TranslateSelectionAsync();
    }

    private async void OnOcrHotkey(object? sender, EventArgs e)
    {
        if (DndService.ShouldBlock(_config, out var reason))
        {
            Log("dnd blocked (ocr): " + reason);
            return;
        }
        Log("ocr hotkey pressed");
        await StartOcrAsync();
    }

    // ---------- 卸载策略 ----------

    private void SetupIdleTimer()
    {
        _idleTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(15),
        };
        _idleTimer.Tick += (_, _) => CheckIdleUnload();
        _idleTimer.Start();
    }

    private void CheckIdleUnload()
    {
        if (_engine == null || !_engine.IsRunning) return;
        if (!string.Equals(_config.UnloadPolicy, "afterMinutes", StringComparison.OrdinalIgnoreCase)) return;

        var idle = _engine.IdleFor;
        if (!idle.HasValue) return;

        var limit = TimeSpan.FromMinutes(Math.Max(0.5, _config.UnloadMinutes));
        if (idle.Value < limit) return;

        Log($"idle unload: {idle.Value.TotalMinutes:F1} min >= {limit.TotalMinutes:F1} min");
        _engine.Unload();
    }

    private void OnPopupClosed()
    {
        if (string.Equals(_config.UnloadPolicy, "onPopupClose", StringComparison.OrdinalIgnoreCase))
        {
            if (_engine is { IsRunning: true })
            {
                Log("unload on popup close");
                _engine.Unload();
            }
        }
    }

    // ---------- 主流程 ----------

    private PopupWindow CreatePopup()
    {
        // 把自动关闭设置传给浮窗（静态属性，构造时读）
        PopupWindow.AutoCloseEnabled = _config.PopupAutoClose;
        PopupWindow.AutoCloseSeconds = _config.PopupAutoCloseSeconds;
        PopupWindow.AutoClosePauseOnHover = _config.PopupAutoClosePauseOnHover;
        PopupWindow.TimedCloseEnabled = _config.PopupTimedCloseEnabled;
        PopupWindow.TimedCloseSeconds = _config.PopupTimedCloseSeconds;

        var popup = new PopupWindow();
        popup.ApplyTheme(_config.Theme, _config.PopupOpacity);
        popup.ConfigureMode(_currentMode, TranslationService.SupportsPromptModes(_config));

        popup.LanguageRequested += async lang => await OnLanguageRequestedAsync(lang);
        popup.ModeRequested += async mode => await OnModeRequestedAsync(mode);
        popup.HistoryRequested += () => OpenHistory();
        popup.ManualTextSubmitted += async text => await OnManualSubmitAsync(text);
        popup.OcrRequested += async () => await StartOcrAsync();

        popup.SettingsRequested += () =>
        {
            Log("SettingsRequested fired");
            try
            {
                OpenSettings();
                Log("OpenSettings returned");
            }
            catch (Exception ex)
            {
                Log("open settings failed: " + ex);
            }
        };

        popup.SpeakRequested += async text =>
        {
            try
            {
                // 正在朗读时再点一下就是停止
                if (_speech is { IsSpeaking: true })
                {
                    _speech.Stop();
                    return;
                }

                var lang = TranslationService.IsMostlyChinese(text) ? "zh" : "en";
                var speech = _speech ??= new SpeechService();
                speech.Stopped -= OnSpeechStopped;
                speech.Stopped += OnSpeechStopped;
                popup.SetSpeaking(true);
                await speech.SpeakAsync(text, lang, _config);
            }
            catch (Exception ex)
            {
                Log("speak failed: " + ex.Message);
                popup.SetSpeaking(false);
            }
        };

        popup.QuickLanguagesChanged += langs =>
        {
            _config.QuickLanguages = langs;
            _config.Save();
            Log("quick languages updated: " + string.Join(",", langs));
        };

        popup.Closed += (_, _) =>
        {
            // 关闭浮窗就停止朗读，否则声音会一直响
            try { _speech?.Stop(); } catch { }

            if (ReferenceEquals(_popup, popup))
            {
                // 浮窗被关掉（手动关、自动关闭、Esc）就没人接收译文了，
                // 终止还在后台跑的翻译，别白白占用 CPU/GPU 直到译完。
                _cts?.Cancel();
                _popup = null;
                OnPopupClosed();
            }
        };

        popup.ApplyTtsVisibility(_config.TtsEnabled);
        popup.ApplyHotkeyHints(_config.OcrHotkey, _config.ReplaceHotkey);
        popup.ReplaceRequested += (translated, sourceHwnd) =>
        {
            popup.Close();
            _ = Task.Run(() => TextGrabber.PasteToWindow(sourceHwnd, translated));
        };
        return popup;
    }

    private async Task TranslateSelectionAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();

        // 极低待机内存模式：取词后把浮窗、流式渲染与朗读放进独立短生命周期子进程，
        // 关窗即退出进程，WPF 渲染缓存与系统组件内存 100% 归还系统，主程序稳在 ~20MB。
        if (_config.PopupIsolateProcess && !string.IsNullOrWhiteSpace(Environment.ProcessPath))
        {
            if (_popup is { IsVisible: true })
            {
                _popup.Close();
                _popup = null;
            }
            if (_popupProc is { HasExited: false }) ClosePopupChild();

            string? isolatedText;
            try
            {
                Log("grab start, mode=" + _config.GrabMode);
                isolatedText = TextGrabber.Grab(_config.GrabMode);
                Log($"grab done: via={TextGrabber.LastMethod} len={isolatedText?.Length ?? 0}");
            }
            catch (Exception ex)
            {
                Log("grab threw: " + ex);
                isolatedText = null;
            }

            if (string.IsNullOrWhiteSpace(isolatedText))
            {
                OpenInputPopup("没有选中文本，输入后按 Ctrl+Enter 翻译");
                return;
            }
            if (_config.SmartJoinLines) isolatedText = TextGrabber.CleanLineBreaks(isolatedText);

            // 模型若未就绪，与拉起浮窗子进程并行预加载，减少冷启动总等待时间
            if (!string.Equals(_config.Engine, "api", StringComparison.OrdinalIgnoreCase)
                && _engine != null && !_engine.IsReady)
            {
                _ = PrewarmAsync();
            }

            LaunchTranslateChild(isolatedText, TextGrabber.LastSourceHwnd, TextGrabber.LastSelectionRect);
            return;
        }

        _popup?.Close();
        _popup = CreatePopup();
        _popup.Show();
        _popup.ShowSource("正在读取选中文本…", EngineLabel());

        string? text;
        try
        {
            Log("grab start, mode=" + _config.GrabMode);
            text = TextGrabber.Grab(_config.GrabMode);
            Log($"grab done: via={TextGrabber.LastMethod} len={text?.Length ?? 0}");
        }
        catch (Exception ex)
        {
            Log("grab threw: " + ex);
            _popup.ShowError("取词出错：" + ex.Message);
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            // 没取到就转成手动输入（在独立子进程里运行，输入法与语音库关闭即彻底释放，不污染主进程）
            _popup.Close();
            _popup = null;
            OpenInputPopup("没有选中文本，输入后按 Ctrl+Enter 翻译");
            return;
        }
        if (_config.SmartJoinLines) text = TextGrabber.CleanLineBreaks(text);

        // 记录取字来源窗口：前台窗口切走它才启动自动关闭倒计时
        _popup.SetSourceWindow(TextGrabber.LastSourceHwnd);

        // 有选区矩形就把浮窗贴到选中文字旁边，比跟着鼠标准
        var anchor = TextGrabber.LastSelectionRect;
        if (anchor.HasValue && anchor.Value.Width > 4 && anchor.Value.Height > 4)
        {
            Log($"anchor ok: {anchor.Value.X},{anchor.Value.Y} {anchor.Value.Width}x{anchor.Value.Height}");
            _popup.SetAnchor(anchor);
        }
        else
        {
            Log($"anchor missing: {(anchor.HasValue
                ? $"{anchor.Value.X},{anchor.Value.Y} {anchor.Value.Width}x{anchor.Value.Height}"
                : "null")} -> 退回跟随光标");
            _popup.SetAnchor(null);
        }

        await TranslateTextAsync(text);
    }

    private void OpenInputPopup(string hint = "输入后按 Ctrl+Enter 翻译")
    {
        if (_popup is { IsVisible: true })
        {
            _cts?.Cancel();
            _popup.Close();
            _popup = null;
        }

        if (_inputProc is { HasExited: false })
        {
            if (_inputChildHwnd != IntPtr.Zero)
            {
                ShowWindow(_inputChildHwnd, SW_RESTORE);
                SetForegroundWindow(_inputChildHwnd);
            }
            else
            {
                BringProcessWindowToFront(_inputProc);
            }
            return;
        }

        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe))
        {
            // 拿不到自身路径时退回同进程模式
            _popup = CreatePopup();
            _popup.Show();
            _currentSource = "";
            _currentTarget = _config.TargetLanguage;
            _popup.ConfigureLanguages(_config.QuickLanguages, _currentTarget);
            _popup.EnterInputMode(hint);
            return;
        }

        try
        {
            var hintB64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(hint));
            var proc = Process.Start(new ProcessStartInfo(exe, $"--input-child --input-hint={hintB64}")
            {
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory,
            });
            if (proc == null) return;

            _inputProc = proc;
            _inputChildHwnd = IntPtr.Zero;
            Log($"OpenInputPopup: child pid={proc.Id}");

            _ = proc.WaitForExitAsync().ContinueWith(_ =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (ReferenceEquals(_inputProc, proc))
                    {
                        _inputProc = null;
                        _inputChildHwnd = IntPtr.Zero;
                    }
                    try { proc.Dispose(); } catch { }
                }));
            });
        }
        catch (Exception ex)
        {
            Log("open input child failed: " + ex.Message);
        }
    }

    private void CloseInputChild()
    {
        if (_inputChildHwnd != IntPtr.Zero)
        {
            PostMessage(_inputChildHwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            return;
        }
        try { if (_inputProc is { HasExited: false }) _inputProc.Kill(); } catch { }
    }

    private async Task OnManualSubmitAsync(string text)
    {
        if (_popup == null) return;
        // 输入框保持可见可编辑，改完可以再提交；沿用用户在浮窗上选的目标语言
        var target = string.IsNullOrWhiteSpace(_currentTarget) ? null : _currentTarget;
        await TranslateTextAsync(text, forcedTarget: target, fromInput: true);
    }

    private static string FormatModeStatus(string mode, string target, bool isDict) =>
        isDict ? "词典查询 …"
        : string.Equals(mode, "polish", StringComparison.OrdinalIgnoreCase) ? "正在润色 …"
        : string.Equals(mode, "explain", StringComparison.OrdinalIgnoreCase) ? "正在解释 …"
        : $"翻译为 {target} …";

    /// <summary>翻译一段确定的文本。forcedTarget 为空时按自动方向决定。</summary>
    private async Task TranslateTextAsync(string source, string? forcedTarget = null, bool fromInput = false)
    {
        if (_popup == null)
        {
            _popup = CreatePopup();
            _popup.Show();
        }

        _currentSource = source;
        if (!fromInput) _currentMode = "translate";

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        var target = TranslationService.ResolveTarget(source, _config, forcedTarget);
        _currentTarget = target;

        var supportsModes = TranslationService.SupportsPromptModes(_config);
        var effectiveMode = supportsModes ? _currentMode : "translate";
        var isDict = _config.WordDictEnabled
                     && supportsModes
                     && string.Equals(effectiveMode, "translate", StringComparison.OrdinalIgnoreCase)
                     && TranslationService.IsSingleEnglishWord(source);

        // 全程用局部变量引用“这一次”的浮窗：后台线程和 await 之后再读 _popup 字段，
        // 可能已经被关掉置空（空引用），或换成了下一次翻译的新浮窗（旧译文串进新窗口）。
        var popup = _popup;

        popup.ConfigureLanguages(_config.QuickLanguages, target);
        popup.ConfigureMode(effectiveMode, supportsModes);

        if (fromInput) popup.StartManualTranslate(EngineLabel(), target);
        else popup.ShowSource(source, EngineLabel());

        popup.SetStatus(FormatModeStatus(effectiveMode, target, isDict));

        try
        {
            if (!string.Equals(_config.Engine, "api", StringComparison.OrdinalIgnoreCase))
            {
                var ready = await EnsureEngineAsync(ct);
                if (!ready.Ok)
                {
                    if (!ct.IsCancellationRequested) popup.ShowError(ready.Message);
                    return;
                }
            }

            Log($"translate start -> {target} mode={effectiveMode} dict={isDict}");
            popup.StartStreaming();

            // 把翻译数据的接收放到后台线程：await foreach 在后台消费 token，
            // 只往浮窗的线程安全缓冲区塞文字，不碰任何 UI 控件。
            // UI 线程由浮窗自己的 DispatcherTimer 每 100ms 刷一次，
            // 这样鼠标拖动、点击按钮全程零卡顿。
            var baseUrl = _engine!.BaseUrl;
            await Task.Run(async () =>
            {
                await foreach (var piece in TranslationService.TranslateAsync(
                    source, _config, baseUrl, target, ct, effectiveMode, isDict))
                {
                    popup.AppendChunk(piece);
                }
            }, ct);

            Log("translate done");
            _engine?.MarkUsed();

            // 回 UI 线程收尾
            if (!ct.IsCancellationRequested)
            {
                popup.Finish();
                if (_config.HistoryEnabled && !string.IsNullOrWhiteSpace(popup.CurrentResult))
                    HistoryService.Add(source, popup.CurrentResult, target, isDict ? "dict" : effectiveMode, EngineLabel());
            }
        }
        catch (OperationCanceledException)
        {
            // 被新的翻译取代或浮窗已关闭：忽略。
            // 但如果不是我们主动取消的，那就是在线接口超时——不提示的话浮窗会一直停在“翻译中…”
            if (!ct.IsCancellationRequested) popup.ShowError("翻译超时，请检查网络或接口设置后重试。");
        }
        catch (Exception ex)
        {
            Log("translate failed: " + ex.Message);
            if (!ct.IsCancellationRequested) popup.ShowError("翻译失败：" + ex.Message);
        }
    }

    private async Task OnModeRequestedAsync(string mode)
    {
        _currentMode = mode;
        if (string.IsNullOrWhiteSpace(_currentSource))
        {
            _popup?.SetStatus(mode switch
            {
                "polish" => "润色模式 · 输入后按 Ctrl+Enter",
                "explain" => "解释模式 · 输入后按 Ctrl+Enter",
                _ => $"将翻译为 {_currentTarget} · 输入后按 Ctrl+Enter",
            });
            return;
        }
        await RetranslateAsync(_currentTarget);
    }

    /// <summary>
    /// 用户点了浮窗上的语言按钮。已经有译文就重译；
    /// 手动输入模式下还没提交，就只记下目标语言，等用户打完字再用。
    /// </summary>
    private async Task OnLanguageRequestedAsync(string target)
    {
        if (string.IsNullOrWhiteSpace(_currentSource))
        {
            _currentTarget = target;
            _popup?.ConfigureLanguages(_config.QuickLanguages, target);
            _popup?.SetStatus($"将翻译为 {target} · 输入后回车");
            return;
        }

        await RetranslateAsync(target);
    }

    private async Task RetranslateAsync(string target)
    {
        if (string.IsNullOrWhiteSpace(_currentSource)) return;

        // 语言按钮只是临时切换，不改默认设置，避免把默认目标语言带偏
        _currentTarget = target;

        var popup = _popup;
        if (popup == null) return;

        var source = _currentSource;
        var supportsModes = TranslationService.SupportsPromptModes(_config);
        var effectiveMode = supportsModes ? _currentMode : "translate";
        var isDict = _config.WordDictEnabled
                     && supportsModes
                     && string.Equals(effectiveMode, "translate", StringComparison.OrdinalIgnoreCase)
                     && TranslationService.IsSingleEnglishWord(source);

        popup.ConfigureLanguages(_config.QuickLanguages, target);
        popup.ConfigureMode(effectiveMode, supportsModes);
        popup.StartTranslate(target);
        popup.SetStatus(FormatModeStatus(effectiveMode, target, isDict));

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            if (!string.Equals(_config.Engine, "api", StringComparison.OrdinalIgnoreCase))
            {
                var ready = await EnsureEngineAsync(ct);
                if (!ready.Ok)
                {
                    if (!ct.IsCancellationRequested) popup.ShowError(ready.Message);
                    return;
                }
            }

            popup.StartStreaming();

            var baseUrl = _engine!.BaseUrl;
            await Task.Run(async () =>
            {
                await foreach (var piece in TranslationService.TranslateAsync(
                    source, _config, baseUrl, target, ct, effectiveMode, isDict))
                {
                    popup.AppendChunk(piece);
                }
            }, ct);

            _engine?.MarkUsed();
            if (!ct.IsCancellationRequested)
            {
                popup.Finish();
                if (_config.HistoryEnabled && !string.IsNullOrWhiteSpace(popup.CurrentResult))
                    HistoryService.Add(source, popup.CurrentResult, target, isDict ? "dict" : effectiveMode, EngineLabel());
            }
        }
        catch (OperationCanceledException)
        {
            if (!ct.IsCancellationRequested) popup.ShowError("翻译超时，请检查网络或接口设置后重试。");
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested) popup.ShowError("翻译失败：" + ex.Message);
        }
    }

    private async Task StartOcrAsync()
    {
        // 极低待机内存模式：截图框选、WinRT OCR 与译文浮窗全部在独立子进程里执行，
        // 关窗后 OCR 语言包、位图与 WPF 缓存立即彻底释放。
        if (_config.PopupIsolateProcess && !string.IsNullOrWhiteSpace(Environment.ProcessPath))
        {
            if (_popup is { IsVisible: true })
            {
                _cts?.Cancel();
                _popup.Close();
                _popup = null;
            }
            if (_popupProc is { HasExited: false }) ClosePopupChild();
            if (_inputProc is { HasExited: false }) CloseInputChild();

            LaunchPopupChildProcess("--ocr-child");
            return;
        }

        // 选择区域时先把浮窗收起来，免得挡住要识别的内容
        var previousSource = _currentSource;
        if (_popup is { IsVisible: true })
        {
            _cts?.Cancel();   // 旧浮窗上还在进行的翻译不再有人接收，别让它继续往后写
            _popup.Close();
            _popup = null;
        }
        _currentSource = previousSource;

        Log("showing selector");
        var selector = new ScreenSelector();
        selector.ShowDialog();
        var region = selector.SelectedRegion;
        Log($"selector returned: {(region.HasValue ? $"{region.Value.Width}x{region.Value.Height}" : "null")}");

        if (region == null)
        {
            Log("ocr cancelled");
            return;
        }

        _popup = CreatePopup();
        _popup.Show();
        _popup.ShowSource("正在识别截图…", "OCR");

        Log($"ocr region: {region.Value.X},{region.Value.Y} {region.Value.Width}x{region.Value.Height}");

        var (ok, text, error) = await OcrService.RecognizeRegionAsync(
            region.Value.X, region.Value.Y, region.Value.Width, region.Value.Height);

        if (!ok)
        {
            Log("ocr failed: " + error);
            _popup.ShowError(error);
            return;
        }

        Log($"ocr ok, {text.Length} chars");
        if (_config.SmartJoinLines) text = TextGrabber.CleanLineBreaks(text);
        await TranslateTextAsync(text);
    }

    private async Task<(bool Ok, string Message)> EnsureEngineAsync(CancellationToken ct)
    {
        if (_engine == null) return (false, "引擎未初始化");
        // 用 IsReady 而不是 IsRunning：进程刚启动、模型还在加载时 IsRunning 已经为 true，
        // 此时直接发请求会失败；走 LoadAsync 会排队等加载完成再返回。
        if (_engine.IsReady)
        {
            _engine.MarkUsed();
            return (true, "就绪");
        }

        var models = _engine.ScanModels();
        if (models.Count == 0)
            return (false, "models 目录里没有 .gguf 模型。\n\n把一个 GGUF 文件放进 models 文件夹，然后在设置里点刷新。");

        var model = models.FirstOrDefault(m =>
                        string.Equals(m.DisplayName, _config.SelectedModel, StringComparison.OrdinalIgnoreCase))
                    ?? models[0];

        if (!string.Equals(model.DisplayName, _config.SelectedModel, StringComparison.OrdinalIgnoreCase))
        {
            _config.SelectedModel = model.DisplayName;
            _config.Save();
        }

        _popup?.SetStatus($"正在加载 {model.DisplayName} …");
        var useGpu = _config.UseGpu;
        if (useGpu && !_engine.SupportsGpu)
        {
            // 精简版引擎没有 GPU 后端，或系统未检测到可用 GPU，这里直接说明白并切回 CPU
            Log("配置要求 GPU，但当前引擎未检测到可用 GPU（CUDA/Vulkan），自动改用 CPU");
            useGpu = false;
        }

        var layers = useGpu ? Math.Max(1, _config.GpuLayers) : 0;
        return await _engine.LoadAsync(model.FilePath, layers, _config.ContextSize, ct);
    }

    private async Task PrewarmAsync()
    {
        try
        {
            var result = await EnsureEngineAsync(CancellationToken.None);
            Log(result.Ok ? "prewarm ok" : "prewarm failed: " + result.Message);
        }
        catch (Exception ex)
        {
            Log("prewarm threw: " + ex.Message);
        }
    }

    // ---------- 设置 ----------

    /// <summary>启动后延迟检查新版本，有更新就点亮托盘菜单项。</summary>
    private async Task CheckUpdateAsync()
    {
        try
        {
            await Task.Delay(6000);

            var info = await new UpdateService().CheckAsync();
            if (info == null) return;

            _pendingUpdate = info;
            if (_updateItem != null)
            {
                _updateItem.Text = $"发现新版本 {info.Version}…";
                _updateItem.Visible = true;
            }

            try
            {
                _tray?.ShowBalloonTip(6000, "QuickTrans 有新版本",
                    $"当前 {UpdateService.CurrentVersion} → {info.Version}，右键托盘图标可更新。",
                    WinForms.ToolTipIcon.Info);
            }
            catch { /* 气泡提示失败不影响主流程 */ }

            Log($"update available: {UpdateService.CurrentVersion} -> {info.Version}");
        }
        catch
        {
            // 检查失败静默忽略，不能因为更新检查影响正常使用
        }
    }

    /// <summary>打开新版本的下载页。</summary>
    private void OpenUpdatePage()
    {
        var url = _pendingUpdate?.Url;
        if (string.IsNullOrWhiteSpace(url)) return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log("open update page failed: " + ex.Message);
        }
    }

    /// <summary>解析安装向导传入的 --install-components=a,b 参数。</summary>
    private static string[] ParseComponentArg()
    {
        const string prefix = "--install-components=";
        try
        {
            foreach (var arg in Environment.GetCommandLineArgs())
            {
                if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return arg[prefix.Length..]
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                }
            }
        }
        catch { /* 参数异常按没传处理 */ }

        return Array.Empty<string>();
    }

    /// <summary>系统里有没有 NVIDIA 显卡（有的话值得引导用户下载 GPU 引擎）。</summary>
    private static bool HasNvidiaGpu()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(
                "nvidia-smi", "--query-gpu=name --format=csv,noheader")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return false;
            var output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(3000))
            {
                try { p.Kill(); } catch { }
                return false;
            }
            return !string.IsNullOrWhiteSpace(output);
        }
        catch
        {
            return false;   // 没装驱动或没有 nvidia-smi
        }
    }

    /// <summary>当前引擎目录里有没有 GPU 后端（CUDA / Vulkan / ROCm），即是否已经是 GPU 版。</summary>
    private static bool HasGpuEngine() =>
        EngineManager.HasGpuBackend(Path.Combine(AppContext.BaseDirectory, "engine"));

    // ---------- 子进程窗口与跨进程同步 ----------

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const int SW_RESTORE = 9;
    private const uint WM_CLOSE = 0x0010;

    private async void RunSettingsChild()
    {
        try
        {
            var cfg = AppConfig.Load();
            var engine = new EngineManager(AppContext.BaseDirectory, cfg.EngineVariant);

            // 向主进程查询已加载模型路径 + 已有窗口矩形（用于智能避让定位，避免遮挡浮窗/历史窗）
            string? loadedPath = null;
            var status = await AppIpcClient.SendAsync("GET_STATUS", connectTimeoutMs: 1000);
            if (status != null && status.StartsWith("OK\t", StringComparison.Ordinal))
            {
                var p = status[3..];
                if (!string.IsNullOrWhiteSpace(p)) loadedPath = p;
            }

            var rectsReply = await AppIpcClient.SendAsync("GET_WINDOW_RECTS\tsettings", connectTimeoutMs: 600);
            var avoidRects = rectsReply != null && rectsReply.StartsWith("OK\t", StringComparison.Ordinal)
                ? WindowPlacer.ParseRects(rectsReply[3..])
                : new List<Int32Rect>();

            var win = new SettingsWindow(cfg, engine, childMode: true, loadedModelPath: loadedPath);
            WindowPlacer.PlaceAvoiding(win, avoidRects);
            MainWindow = win;
            win.Closed += (_, _) =>
            {
                engine.Dispose();
                Shutdown();
            };
            WindowPlacer.SurfaceWindow(win);

            // 把精确的设置窗口句柄上报主进程（避免 Process.MainWindowHandle 误拿到输入法隐藏窗）
            var hwnd = new WindowInteropHelper(win).Handle;
            if (hwnd != IntPtr.Zero)
                _ = AppIpcClient.SendAsync($"SETTINGS_HWND\t{hwnd.ToInt64()}");
        }
        catch (Exception ex)
        {
            Log("settings-child failed: " + ex);
            Shutdown();
        }
    }

    private void RunWelcomeChild()
    {
        try
        {
            var win = new WelcomeWindow();
            win.Closed += (_, _) => Shutdown();
            win.Show();
            win.Activate();
        }
        catch
        {
            Shutdown();
        }
    }

    private void RunComponentsChild()
    {
        try
        {
            var auto = ParseComponentArg();
            var mgr = new ComponentManager(AppContext.BaseDirectory);
            var win = auto.Length > 0
                ? new ComponentWindow(mgr, auto)
                : new ComponentWindow(mgr);
            MainWindow = win;
            win.Closed += (_, _) => Shutdown();
            win.Show();
            win.Activate();
        }
        catch
        {
            Shutdown();
        }
    }

    private async void RunHistoryChild()
    {
        try
        {
            var cfg = AppConfig.Load();
            var rectsReply = await AppIpcClient.SendAsync("GET_WINDOW_RECTS\thistory", connectTimeoutMs: 600);
            var avoidRects = rectsReply != null && rectsReply.StartsWith("OK\t", StringComparison.Ordinal)
                ? WindowPlacer.ParseRects(rectsReply[3..])
                : new List<Int32Rect>();

            var win = new HistoryWindow(cfg.Theme);
            WindowPlacer.PlaceAvoiding(win, avoidRects);
            MainWindow = win;
            win.Closed += (_, _) => Shutdown();
            WindowPlacer.SurfaceWindow(win);

            var hwnd = new WindowInteropHelper(win).Handle;
            if (hwnd != IntPtr.Zero)
                _ = AppIpcClient.SendAsync($"HISTORY_HWND\t{hwnd.ToInt64()}");
        }
        catch (Exception ex)
        {
            Log("history-child failed: " + ex);
            Shutdown();
        }
    }

    /// <summary>
    /// 手动输入模式子进程：承载带键盘焦点的输入浮窗，输入法与朗读语音库只加载在本进程内；
    /// 关闭浮窗即退出进程，100% 释放输入法与显卡驱动内存，同时复用主进程持有的本地模型。
    /// </summary>
    private void RunInputChild()
    {
        try
        {
            _config = AppConfig.Load();
            _currentSource = "";
            _currentTarget = _config.TargetLanguage;
            _currentMode = "translate";

            var hint = "输入后按 Ctrl+Enter 翻译";
            const string prefix = "--input-hint=";
            foreach (var arg in Environment.GetCommandLineArgs())
            {
                if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        hint = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(arg[prefix.Length..]));
                    }
                    catch { }
                    break;
                }
            }

            PopupWindow.AutoCloseEnabled = false;
            PopupWindow.TimedCloseEnabled = false;

            var popup = new PopupWindow();
            MainWindow = popup;
            popup.ApplyTheme(_config.Theme, _config.PopupOpacity);
            popup.ApplyTtsVisibility(_config.TtsEnabled);
            popup.ApplyHotkeyHints(_config.OcrHotkey, _config.ReplaceHotkey);
            popup.ConfigureLanguages(_config.QuickLanguages, _currentTarget);
            popup.ConfigureMode(_currentMode, TranslationService.SupportsPromptModes(_config));

            async Task RunTranslateAsync(string source, string? forcedTarget)
            {
                // 每次翻译前重读一次配置，保证在设置页刚改完的接口/模型立即生效
                _config = AppConfig.Load();
                popup.ApplyTheme(_config.Theme, _config.PopupOpacity);
                popup.ApplyTtsVisibility(_config.TtsEnabled);

                _currentSource = source;
                _cts?.Cancel();
                _cts = new CancellationTokenSource();
                var ct = _cts.Token;

                var target = TranslationService.ResolveTarget(source, _config, forcedTarget);
                _currentTarget = target;

                var supportsModes = TranslationService.SupportsPromptModes(_config);
                var effectiveMode = supportsModes ? _currentMode : "translate";
                var isDict = _config.WordDictEnabled
                             && supportsModes
                             && string.Equals(effectiveMode, "translate", StringComparison.OrdinalIgnoreCase)
                             && TranslationService.IsSingleEnglishWord(source);

                popup.ConfigureLanguages(_config.QuickLanguages, target);
                popup.ConfigureMode(effectiveMode, supportsModes);
                popup.StartManualTranslate(EngineLabel(), target);

                try
                {
                    var baseUrl = "";
                    if (!string.Equals(_config.Engine, "api", StringComparison.OrdinalIgnoreCase))
                    {
                        popup.SetStatus("正在准备本地模型…");
                        var reply = await AppIpcClient.SendAsync("ENSURE_ENGINE", connectTimeoutMs: 2000, ct);
                        if (reply != null && reply.StartsWith("OK\t", StringComparison.Ordinal))
                        {
                            baseUrl = reply[3..];
                        }
                        else
                        {
                            var msg = reply != null && reply.StartsWith("ERR\t", StringComparison.Ordinal)
                                ? reply[4..]
                                : "无法连接到主程序，请重试。";
                            if (!ct.IsCancellationRequested) popup.ShowError(msg);
                            return;
                        }
                    }

                    popup.SetStatus(FormatModeStatus(effectiveMode, target, isDict));
                    popup.StartStreaming();

                    await Task.Run(async () =>
                    {
                        await foreach (var piece in TranslationService.TranslateAsync(
                            source, _config, baseUrl, target, ct, effectiveMode, isDict))
                        {
                            popup.AppendChunk(piece);
                        }
                    }, ct);

                    if (!string.Equals(_config.Engine, "api", StringComparison.OrdinalIgnoreCase))
                        _ = AppIpcClient.SendAsync("MARK_USED");

                    if (!ct.IsCancellationRequested)
                    {
                        popup.Finish();
                        if (_config.HistoryEnabled && !string.IsNullOrWhiteSpace(popup.CurrentResult))
                            HistoryService.Add(source, popup.CurrentResult, target, isDict ? "dict" : effectiveMode, EngineLabel());
                    }
                }
                catch (OperationCanceledException)
                {
                    if (!ct.IsCancellationRequested) popup.ShowError("翻译超时，请检查网络或接口设置后重试。");
                }
                catch (Exception ex)
                {
                    if (!ct.IsCancellationRequested) popup.ShowError("翻译失败：" + ex.Message);
                }
            }

            popup.ManualTextSubmitted += async text =>
            {
                var target = string.IsNullOrWhiteSpace(_currentTarget) ? null : _currentTarget;
                await RunTranslateAsync(text, target);
            };

            popup.LanguageRequested += async lang =>
            {
                if (string.IsNullOrWhiteSpace(_currentSource))
                {
                    _currentTarget = lang;
                    popup.ConfigureLanguages(_config.QuickLanguages, lang);
                    popup.SetStatus($"将翻译为 {lang} · 输入后按 Ctrl+Enter 翻译");
                    return;
                }
                await RunTranslateAsync(_currentSource, lang);
            };

            popup.ModeRequested += async mode =>
            {
                _currentMode = mode;
                if (string.IsNullOrWhiteSpace(_currentSource))
                {
                    popup.SetStatus(mode switch
                    {
                        "polish" => "润色模式 · 输入后按 Ctrl+Enter",
                        "explain" => "解释模式 · 输入后按 Ctrl+Enter",
                        _ => $"将翻译为 {_currentTarget} · 输入后按 Ctrl+Enter",
                    });
                    return;
                }
                await RunTranslateAsync(_currentSource, _currentTarget);
            };

            popup.HistoryRequested += () =>
            {
                popup.BeginOpeningAuxWindow();
                _ = AppIpcClient.SendAsync("OPEN_HISTORY");
            };

            popup.SpeakRequested += async text =>
            {
                try
                {
                    if (_speech is { IsSpeaking: true })
                    {
                        _speech.Stop();
                        return;
                    }
                    var lang = TranslationService.IsMostlyChinese(text) ? "zh" : "en";
                    var speech = _speech ??= new SpeechService();
                    speech.Stopped -= OnChildSpeechStopped;
                    speech.Stopped += OnChildSpeechStopped;
                    popup.SetSpeaking(true);
                    await speech.SpeakAsync(text, lang, _config);
                }
                catch (Exception ex)
                {
                    Log("input-child speak failed: " + ex.Message);
                    popup.SetSpeaking(false);
                }

                void OnChildSpeechStopped()
                {
                    try
                    {
                        if (Dispatcher.CheckAccess()) popup.SetSpeaking(false);
                        else Dispatcher.BeginInvoke(new Action(() => { try { popup.SetSpeaking(false); } catch { } }));
                    }
                    catch { }
                }
            };

            popup.QuickLanguagesChanged += langs =>
            {
                _config.QuickLanguages = langs;
                _config.Save();
                _ = AppIpcClient.SendAsync("RELOAD_CONFIG");
            };

            popup.SettingsRequested += () =>
            {
                popup.BeginOpeningAuxWindow();
                _ = AppIpcClient.SendAsync("OPEN_SETTINGS");
            };

            popup.OcrRequested += () =>
            {
                _ = AppIpcClient.SendAsync("START_OCR");
                popup.Close();
            };

            popup.Closed += async (_, _) =>
            {
                _cts?.Cancel();
                try { _speech?.Stop(); } catch { }
                try { _speech?.Dispose(); } catch { }
                try { await AppIpcClient.SendAsync("INPUT_CLOSED", connectTimeoutMs: 400); } catch { }
                Shutdown();
            };

            popup.Show();
            popup.EnterInputMode(hint);

            var hwnd = new WindowInteropHelper(popup).Handle;
            if (hwnd != IntPtr.Zero)
                _ = AppIpcClient.SendAsync($"INPUT_HWND\t{hwnd.ToInt64()}");
        }
        catch (Exception ex)
        {
            Log("input-child failed: " + ex);
            Shutdown();
        }
    }

    private sealed record TranslateJobPayload(
        string Text, long SourceHwnd, bool HasAnchor, int AnchorX, int AnchorY, int AnchorW, int AnchorH);

    private void LaunchTranslateChild(string text, IntPtr sourceHwnd, Int32Rect? anchor)
    {
        var hasAnchor = anchor.HasValue && anchor.Value.Width > 4 && anchor.Value.Height > 4;
        var payload = new TranslateJobPayload(
            text,
            sourceHwnd.ToInt64(),
            hasAnchor,
            hasAnchor ? anchor!.Value.X : 0,
            hasAnchor ? anchor!.Value.Y : 0,
            hasAnchor ? anchor!.Value.Width : 0,
            hasAnchor ? anchor!.Value.Height : 0);

        _pendingTranslateJobJson = System.Text.Json.JsonSerializer.Serialize(payload);
        LaunchPopupChildProcess("--translate-child");
    }

    private void LaunchPopupChildProcess(string args)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe)) return;

        try
        {
            var proc = Process.Start(new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory,
            });
            if (proc == null) return;

            _popupProc = proc;
            _popupChildHwnd = IntPtr.Zero;
            Log($"LaunchPopupChild({args}): pid={proc.Id}");

            _ = proc.WaitForExitAsync().ContinueWith(_ =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (ReferenceEquals(_popupProc, proc))
                    {
                        _popupProc = null;
                        _popupChildHwnd = IntPtr.Zero;
                    }
                    try { proc.Dispose(); } catch { }
                }));
            });
        }
        catch (Exception ex)
        {
            Log($"launch popup child ({args}) failed: " + ex.Message);
        }
    }

    private void ClosePopupChild()
    {
        if (_popupChildHwnd != IntPtr.Zero)
        {
            PostMessage(_popupChildHwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            return;
        }
        try { if (_popupProc is { HasExited: false }) _popupProc.Kill(); } catch { }
    }

    /// <summary>
    /// 独立子进程模式下的划词翻译 / 截图 OCR 浮窗：
    /// 完整复刻主进程的浮窗体验，关窗即退出进程释放全部 WPF/OCR/朗读内存，
    /// 并通过本地 IPC 复用主进程持有的本地模型。
    /// </summary>
    private async void RunPopupChild(bool ocrMode)
    {
        try
        {
            _config = AppConfig.Load();
            _currentSource = "";
            _currentTarget = _config.TargetLanguage;

            Int32Rect? ocrRegion = null;
            TranslateJobPayload? job = null;

            if (ocrMode)
            {
                var selector = new ScreenSelector();
                selector.ShowDialog();
                ocrRegion = selector.SelectedRegion;
                if (ocrRegion == null)
                {
                    Shutdown();
                    return;
                }
            }
            else
            {
                var raw = await AppIpcClient.SendAsync("FETCH_TRANSLATE_JOB", connectTimeoutMs: 2000);
                if (string.IsNullOrWhiteSpace(raw) || !raw.StartsWith("OK\t", StringComparison.Ordinal))
                {
                    Shutdown();
                    return;
                }
                job = System.Text.Json.JsonSerializer.Deserialize<TranslateJobPayload>(raw[3..]);
                if (job == null || string.IsNullOrWhiteSpace(job.Text))
                {
                    Shutdown();
                    return;
                }
            }

            PopupWindow.AutoCloseEnabled = _config.PopupAutoClose;
            PopupWindow.AutoCloseSeconds = _config.PopupAutoCloseSeconds;
            PopupWindow.AutoClosePauseOnHover = _config.PopupAutoClosePauseOnHover;
            PopupWindow.TimedCloseEnabled = _config.PopupTimedCloseEnabled;
            PopupWindow.TimedCloseSeconds = _config.PopupTimedCloseSeconds;

            var popup = new PopupWindow();
            MainWindow = popup;
            _currentMode = "translate";
            popup.ApplyTheme(_config.Theme, _config.PopupOpacity);
            popup.ApplyTtsVisibility(_config.TtsEnabled);
            popup.ApplyHotkeyHints(_config.OcrHotkey, _config.ReplaceHotkey);
            popup.ConfigureMode(_currentMode, TranslationService.SupportsPromptModes(_config));
            popup.ReplaceRequested += (translated, sourceHwnd) =>
            {
                // 子进程关窗后再同步粘贴，确保原窗口重新接管粘贴事件后进程才退出
                popup.Hide();
                _ = Task.Run(() =>
                {
                    TextGrabber.PasteToWindow(sourceHwnd, translated);
                    Dispatcher.BeginInvoke(new Action(() => popup.Close()));
                });
            };

            async Task RunTranslateAsync(string source, string? forcedTarget, bool isRetranslate)
            {
                _config = AppConfig.Load();
                popup.ApplyTheme(_config.Theme, _config.PopupOpacity);
                popup.ApplyTtsVisibility(_config.TtsEnabled);

                _currentSource = source;
                if (!isRetranslate) _currentMode = "translate";
                _cts?.Cancel();
                _cts = new CancellationTokenSource();
                var ct = _cts.Token;

                var target = TranslationService.ResolveTarget(source, _config, forcedTarget);
                _currentTarget = target;

                var supportsModes = TranslationService.SupportsPromptModes(_config);
                var effectiveMode = supportsModes ? _currentMode : "translate";
                var isDict = _config.WordDictEnabled
                             && supportsModes
                             && string.Equals(effectiveMode, "translate", StringComparison.OrdinalIgnoreCase)
                             && TranslationService.IsSingleEnglishWord(source);

                popup.ConfigureLanguages(_config.QuickLanguages, target);
                popup.ConfigureMode(effectiveMode, supportsModes);
                if (isRetranslate) popup.StartTranslate(target);
                else popup.ShowSource(source, EngineLabel());

                try
                {
                    var baseUrl = "";
                    if (!string.Equals(_config.Engine, "api", StringComparison.OrdinalIgnoreCase))
                    {
                        popup.SetStatus("正在准备本地模型…");
                        var reply = await AppIpcClient.SendAsync("ENSURE_ENGINE", connectTimeoutMs: 2000, ct);
                        if (reply != null && reply.StartsWith("OK\t", StringComparison.Ordinal))
                        {
                            baseUrl = reply[3..];
                        }
                        else
                        {
                            var msg = reply != null && reply.StartsWith("ERR\t", StringComparison.Ordinal)
                                ? reply[4..]
                                : "无法连接到主程序，请重试。";
                            if (!ct.IsCancellationRequested) popup.ShowError(msg);
                            return;
                        }
                    }

                    popup.SetStatus(FormatModeStatus(effectiveMode, target, isDict));
                    popup.StartStreaming();

                    await Task.Run(async () =>
                    {
                        await foreach (var piece in TranslationService.TranslateAsync(
                            source, _config, baseUrl, target, ct, effectiveMode, isDict))
                        {
                            popup.AppendChunk(piece);
                        }
                    }, ct);

                    if (!string.Equals(_config.Engine, "api", StringComparison.OrdinalIgnoreCase))
                        _ = AppIpcClient.SendAsync("MARK_USED");

                    if (!ct.IsCancellationRequested)
                    {
                        popup.Finish();
                        if (_config.HistoryEnabled && !string.IsNullOrWhiteSpace(popup.CurrentResult))
                            HistoryService.Add(source, popup.CurrentResult, target, isDict ? "dict" : effectiveMode, EngineLabel());
                    }
                }
                catch (OperationCanceledException)
                {
                    if (!ct.IsCancellationRequested) popup.ShowError("翻译超时，请检查网络或接口设置后重试。");
                }
                catch (Exception ex)
                {
                    if (!ct.IsCancellationRequested) popup.ShowError("翻译失败：" + ex.Message);
                }
            }

            popup.LanguageRequested += async lang =>
            {
                if (string.IsNullOrWhiteSpace(_currentSource)) return;
                await RunTranslateAsync(_currentSource, lang, isRetranslate: true);
            };

            popup.ModeRequested += async mode =>
            {
                _currentMode = mode;
                if (string.IsNullOrWhiteSpace(_currentSource)) return;
                await RunTranslateAsync(_currentSource, _currentTarget, isRetranslate: true);
            };

            popup.HistoryRequested += () =>
            {
                popup.BeginOpeningAuxWindow();
                _ = AppIpcClient.SendAsync("OPEN_HISTORY");
            };

            popup.SpeakRequested += async text =>
            {
                try
                {
                    if (_speech is { IsSpeaking: true })
                    {
                        _speech.Stop();
                        return;
                    }
                    var lang = TranslationService.IsMostlyChinese(text) ? "zh" : "en";
                    var speech = _speech ??= new SpeechService();
                    speech.Stopped -= OnPopupSpeechStopped;
                    speech.Stopped += OnPopupSpeechStopped;
                    popup.SetSpeaking(true);
                    await speech.SpeakAsync(text, lang, _config);
                }
                catch (Exception ex)
                {
                    Log("popup-child speak failed: " + ex.Message);
                    popup.SetSpeaking(false);
                }

                void OnPopupSpeechStopped()
                {
                    try
                    {
                        if (Dispatcher.CheckAccess()) popup.SetSpeaking(false);
                        else Dispatcher.BeginInvoke(new Action(() => { try { popup.SetSpeaking(false); } catch { } }));
                    }
                    catch { }
                }
            };

            popup.QuickLanguagesChanged += langs =>
            {
                _config.QuickLanguages = langs;
                _config.Save();
                _ = AppIpcClient.SendAsync("RELOAD_CONFIG");
            };

            popup.SettingsRequested += () =>
            {
                popup.BeginOpeningAuxWindow();
                _ = AppIpcClient.SendAsync("OPEN_SETTINGS");
            };

            popup.OcrRequested += () =>
            {
                _ = AppIpcClient.SendAsync("START_OCR");
                popup.Close();
            };

            popup.Closed += async (_, _) =>
            {
                _cts?.Cancel();
                try { _speech?.Stop(); } catch { }
                try { _speech?.Dispose(); } catch { }
                try { await AppIpcClient.SendAsync("POPUP_CLOSED", connectTimeoutMs: 400); } catch { }
                Shutdown();
            };

            popup.Show();
            var hwnd = new WindowInteropHelper(popup).Handle;
            if (hwnd != IntPtr.Zero)
                _ = AppIpcClient.SendAsync($"POPUP_HWND\t{hwnd.ToInt64()}");

            if (ocrMode && ocrRegion.HasValue)
            {
                popup.ShowSource("正在识别截图…", "OCR");
                var r = ocrRegion.Value;
                var (ok, text, error) = await OcrService.RecognizeRegionAsync(r.X, r.Y, r.Width, r.Height);
                if (!ok)
                {
                    popup.ShowError(error);
                    return;
                }
                if (_config.SmartJoinLines) text = TextGrabber.CleanLineBreaks(text);
                await RunTranslateAsync(text, null, isRetranslate: false);
            }
            else if (job != null)
            {
                if (job.SourceHwnd != 0) popup.SetSourceWindow(new IntPtr(job.SourceHwnd));
                if (job.HasAnchor) popup.SetAnchor(new Int32Rect(job.AnchorX, job.AnchorY, job.AnchorW, job.AnchorH));
                else popup.SetAnchor(null);
                await RunTranslateAsync(job.Text, null, isRetranslate: false);
            }
        }
        catch (Exception ex)
        {
            Log("popup-child failed: " + ex);
            Shutdown();
        }
    }

    private static async Task RunChildWindowAndWaitAsync(string args)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe)) return;
        try
        {
            using var proc = Process.Start(new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory,
            });
            if (proc != null) await proc.WaitForExitAsync();
        }
        catch { }
    }

    private async Task<string> HandleIpcAsync(string command)
    {
        if (command == "GET_STATUS")
            return "OK\t" + (_engine?.LoadedModelPath ?? "");

        if (command.StartsWith("SETTINGS_HWND\t", StringComparison.Ordinal))
        {
            if (long.TryParse(command["SETTINGS_HWND\t".Length..], out var raw))
            {
                var hwnd = new IntPtr(raw);
                await Dispatcher.InvokeAsync(() =>
                {
                    _settingsChildHwnd = hwnd;
                    NotifyPopupAuxHwnd(1, hwnd);
                });
            }
            return "OK";
        }

        if (command.StartsWith("INPUT_HWND\t", StringComparison.Ordinal))
        {
            if (long.TryParse(command["INPUT_HWND\t".Length..], out var raw))
            {
                var hwnd = new IntPtr(raw);
                await Dispatcher.InvokeAsync(() =>
                {
                    _inputChildHwnd = hwnd;
                    if (_settingsChildHwnd != IntPtr.Zero)
                        PostMessage(hwnd, ThemeManager.WM_QT_SET_AUX_HWND, new IntPtr(1), _settingsChildHwnd);
                    if (_historyChildHwnd != IntPtr.Zero)
                        PostMessage(hwnd, ThemeManager.WM_QT_SET_AUX_HWND, new IntPtr(2), _historyChildHwnd);
                });
            }
            return "OK";
        }

        if (command.StartsWith("POPUP_HWND\t", StringComparison.Ordinal))
        {
            if (long.TryParse(command["POPUP_HWND\t".Length..], out var raw))
            {
                var hwnd = new IntPtr(raw);
                await Dispatcher.InvokeAsync(() =>
                {
                    _popupChildHwnd = hwnd;
                    if (_settingsChildHwnd != IntPtr.Zero)
                        PostMessage(hwnd, ThemeManager.WM_QT_SET_AUX_HWND, new IntPtr(1), _settingsChildHwnd);
                    if (_historyChildHwnd != IntPtr.Zero)
                        PostMessage(hwnd, ThemeManager.WM_QT_SET_AUX_HWND, new IntPtr(2), _historyChildHwnd);
                });
            }
            return "OK";
        }

        if (command.StartsWith("HISTORY_HWND\t", StringComparison.Ordinal))
        {
            if (long.TryParse(command["HISTORY_HWND\t".Length..], out var raw))
            {
                var hwnd = new IntPtr(raw);
                await Dispatcher.InvokeAsync(() =>
                {
                    _historyChildHwnd = hwnd;
                    NotifyPopupAuxHwnd(2, hwnd);
                });
            }
            return "OK";
        }

        if (command.StartsWith("PREVIEW_THEME\t", StringComparison.Ordinal))
        {
            var parts = command.Split('\t');
            if (parts.Length >= 3
                && double.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var op))
            {
                var theme = parts[1];
                await Dispatcher.InvokeAsync(() => BroadcastThemePreviewToWindows(theme, op));
            }
            return "OK";
        }

        if (command == "FETCH_TRANSLATE_JOB")
        {
            var json = Interlocked.Exchange(ref _pendingTranslateJobJson, null);
            return string.IsNullOrEmpty(json) ? "ERR" : "OK\t" + json;
        }

        if (command == "GET_SETTINGS_HWND")
            return "OK\t" + _settingsChildHwnd.ToInt64();

        if (command.StartsWith("GET_WINDOW_RECTS\t", StringComparison.Ordinal))
        {
            var caller = command["GET_WINDOW_RECTS\t".Length..].Trim().ToLowerInvariant();
            return await Dispatcher.InvokeAsync(() =>
            {
                var rects = CollectOpenWindowRects(caller);
                return "OK\t" + WindowPlacer.SerializeRects(rects);
            });
        }

        if (command == "POPUP_CLOSED")
        {
            await Dispatcher.InvokeAsync(OnPopupClosed);
            return "OK";
        }

        if (command == "ENSURE_ENGINE")
        {
            return await Dispatcher.InvokeAsync(async () =>
            {
                var ready = await EnsureEngineAsync(CancellationToken.None);
                return ready.Ok ? "OK\t" + _engine!.BaseUrl : "ERR\t" + ready.Message;
            }).Task.Unwrap();
        }

        if (command == "MARK_USED")
        {
            _engine?.MarkUsed();
            return "OK";
        }

        if (command == "INPUT_CLOSED")
        {
            await Dispatcher.InvokeAsync(OnPopupClosed);
            return "OK";
        }

        if (command == "OPEN_SETTINGS")
        {
            await Dispatcher.InvokeAsync(OpenSettings);
            return "OK";
        }

        if (command == "OPEN_HISTORY")
        {
            await Dispatcher.InvokeAsync(OpenHistory);
            return "OK";
        }

        if (command == "START_OCR")
        {
            await Dispatcher.InvokeAsync(() => _ = StartOcrAsync());
            return "OK";
        }

        if (command == "RELOAD_CONFIG")
        {
            await Dispatcher.InvokeAsync(ApplyReloadedConfig);
            return "OK";
        }

        if (command.StartsWith("LOAD_MODEL\t", StringComparison.Ordinal))
        {
            var filePath = command["LOAD_MODEL\t".Length..];
            return await Dispatcher.InvokeAsync(async () =>
            {
                ApplyReloadedConfig();
                if (_engine == null) return "ERR\t引擎未初始化";

                var useGpu = _config.UseGpu && _engine.SupportsGpu;
                var layers = useGpu ? Math.Max(1, _config.GpuLayers) : 0;
                var (ok, msg) = await _engine.LoadAsync(filePath, layers, _config.ContextSize);
                return ok ? "OK\t" + filePath : "ERR\t" + msg;
            }).Task.Unwrap();
        }

        if (command == "APPLY_UPDATE")
        {
            return await Dispatcher.InvokeAsync(() =>
            {
                if (!UpdateService.ApplyAndRestart()) return "ERR";
                Dispatcher.BeginInvoke(new Action(ExitApp),
                    System.Windows.Threading.DispatcherPriority.Background);
                return "OK";
            });
        }

        return "ERR\tunknown";
    }

    /// <summary>设置子进程写盘后，由主进程重载 config.json 并让所有改动立即生效。</summary>
    private void ApplyReloadedConfig()
    {
        var oldStyle = _config.TrayIconStyle;
        var oldVariant = _config.EngineVariant;
        var oldUseGpu = _config.UseGpu;
        var oldHotkey = _config.Hotkey;
        var oldOcrHotkey = _config.OcrHotkey;
        var oldReplaceHotkey = _config.ReplaceHotkey;
        var oldDoubleCtrlC = _config.DoubleCtrlCEnabled;

        _config.CopyFrom(AppConfig.Load());

        // 同步浮窗静态与实例参数
        PopupWindow.AutoCloseEnabled = _config.PopupAutoClose;
        PopupWindow.AutoCloseSeconds = _config.PopupAutoCloseSeconds;
        PopupWindow.AutoClosePauseOnHover = _config.PopupAutoClosePauseOnHover;
        PopupWindow.TimedCloseEnabled = _config.PopupTimedCloseEnabled;
        PopupWindow.TimedCloseSeconds = _config.PopupTimedCloseSeconds;
        _popup?.ApplyTheme(_config.Theme, _config.PopupOpacity);
        _popup?.ApplyTtsVisibility(_config.TtsEnabled);
        _popup?.ApplyHotkeyHints(_config.OcrHotkey, _config.ReplaceHotkey);
        _popup?.ConfigureMode(_currentMode, TranslationService.SupportsPromptModes(_config));
        BroadcastConfigReloadToChildren();

        if (!string.Equals(_config.Hotkey, oldHotkey, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(_config.OcrHotkey, oldOcrHotkey, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(_config.ReplaceHotkey, oldReplaceHotkey, StringComparison.OrdinalIgnoreCase)
            || _config.DoubleCtrlCEnabled != oldDoubleCtrlC)
        {
            RegisterHotkeys();
        }

        if (_config.TrayIconStyle != oldStyle && _tray != null)
        {
            var previous = _tray.Icon;
            _tray.Icon = CreateTrayIcon(_config.TrayIconStyle);
            try { previous?.Dispose(); } catch { }
        }

        if (!string.Equals(_config.EngineVariant, oldVariant, StringComparison.OrdinalIgnoreCase))
        {
            Log($"engine variant -> {_config.EngineVariant}");
            _engine?.Unload();
            _engine?.SwitchEngine(_config.EngineVariant);
            Log($"engine dir now {_engine?.EngineDir}");
        }
        else if (_config.UseGpu != oldUseGpu && _engine is { IsRunning: true })
        {
            Log($"useGpu -> {_config.UseGpu}, unload current model");
            _engine.Unload();
        }

        AutoStartService.Sync(_config.AutoStart);
    }

    private static void BringProcessWindowToFront(Process proc)
    {
        try
        {
            proc.Refresh();
            var hwnd = proc.MainWindowHandle;
            if (hwnd != IntPtr.Zero)
            {
                ShowWindow(hwnd, SW_RESTORE);
                SetForegroundWindow(hwnd);
            }
        }
        catch { }
    }

    private List<Int32Rect> CollectOpenWindowRects(string excludeKind)
    {
        var list = new List<Int32Rect>();

        // 优先放浮窗矩形，作为显示器选择与避让的首选锚点
        if (excludeKind != "popup")
        {
            if (_popup is { IsVisible: true })
            {
                var r = WindowPlacer.TryGetRectPx(new WindowInteropHelper(_popup).Handle);
                if (r.HasValue) list.Add(r.Value);
            }
            if (_popupChildHwnd != IntPtr.Zero)
            {
                var r = WindowPlacer.TryGetRectPx(_popupChildHwnd);
                if (r.HasValue) list.Add(r.Value);
            }
            if (_inputChildHwnd != IntPtr.Zero)
            {
                var r = WindowPlacer.TryGetRectPx(_inputChildHwnd);
                if (r.HasValue) list.Add(r.Value);
            }
        }

        if (excludeKind != "settings")
        {
            if (_settingsWin is { IsVisible: true })
            {
                var r = WindowPlacer.TryGetRectPx(new WindowInteropHelper(_settingsWin).Handle);
                if (r.HasValue) list.Add(r.Value);
            }
            if (_settingsChildHwnd != IntPtr.Zero)
            {
                var r = WindowPlacer.TryGetRectPx(_settingsChildHwnd);
                if (r.HasValue) list.Add(r.Value);
            }
        }

        if (excludeKind != "history" && _historyChildHwnd != IntPtr.Zero)
        {
            var r = WindowPlacer.TryGetRectPx(_historyChildHwnd);
            if (r.HasValue) list.Add(r.Value);
        }

        return list;
    }

    /// <summary>向已打开的浮窗和历史窗实时推送主题与透明度预览（无需等下次打开）。</summary>
    private void BroadcastThemePreviewToWindows(string theme, double opacity)
    {
        var op = Math.Clamp(opacity, 0.3, 1.0);
        _popup?.ApplyTheme(theme, op);

        var wParam = new IntPtr(ThemeManager.ToCode(theme));
        var lParam = new IntPtr((int)Math.Round(op * 100));
        if (_popupChildHwnd != IntPtr.Zero) PostMessage(_popupChildHwnd, ThemeManager.WM_QT_REFRESH_THEME, wParam, lParam);
        if (_inputChildHwnd != IntPtr.Zero) PostMessage(_inputChildHwnd, ThemeManager.WM_QT_REFRESH_THEME, wParam, lParam);
        if (_historyChildHwnd != IntPtr.Zero) PostMessage(_historyChildHwnd, ThemeManager.WM_QT_REFRESH_THEME, wParam, lParam);
    }

    /// <summary>通知所有已打开的子进程窗口按最新 config.json 重载主题/透明度/热键提示。</summary>
    private void BroadcastConfigReloadToChildren()
    {
        var reloadFlag = new IntPtr(-1);
        if (_popupChildHwnd != IntPtr.Zero) PostMessage(_popupChildHwnd, ThemeManager.WM_QT_REFRESH_THEME, reloadFlag, IntPtr.Zero);
        if (_inputChildHwnd != IntPtr.Zero) PostMessage(_inputChildHwnd, ThemeManager.WM_QT_REFRESH_THEME, reloadFlag, IntPtr.Zero);
        if (_historyChildHwnd != IntPtr.Zero) PostMessage(_historyChildHwnd, ThemeManager.WM_QT_REFRESH_THEME, reloadFlag, IntPtr.Zero);
    }

    /// <summary>同步设置页（kind=1）或历史窗（kind=2）句柄到所有已打开的翻译浮窗，统一覆盖与防自动关闭逻辑。</summary>
    private void NotifyPopupAuxHwnd(int kind, IntPtr hwnd)
    {
        if (kind == 1) _popup?.SetSettingsWindow(hwnd);
        else if (kind == 2) _popup?.SetHistoryWindow(hwnd);

        var kindPtr = new IntPtr(kind);
        if (_popupChildHwnd != IntPtr.Zero) PostMessage(_popupChildHwnd, ThemeManager.WM_QT_SET_AUX_HWND, kindPtr, hwnd);
        if (_inputChildHwnd != IntPtr.Zero) PostMessage(_inputChildHwnd, ThemeManager.WM_QT_SET_AUX_HWND, kindPtr, hwnd);
    }

    /// <summary>打开翻译历史窗口（独立子进程运行，关闭后不留驻内存）。</summary>
    private void OpenHistory()
    {
        if (_historyProc is { HasExited: false })
        {
            try { AllowSetForegroundWindow(_historyProc.Id); } catch { }
            if (_historyChildHwnd != IntPtr.Zero)
            {
                NotifyPopupAuxHwnd(2, _historyChildHwnd);
                ShowWindow(_historyChildHwnd, SW_RESTORE);
                SetForegroundWindow(_historyChildHwnd);
            }
            else
            {
                BringProcessWindowToFront(_historyProc);
            }
            return;
        }

        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe))
        {
            _popup?.BeginOpeningAuxWindow();
            var win = new HistoryWindow(_config.Theme);
            WindowPlacer.PlaceAvoiding(win, CollectOpenWindowRects("history"));
            win.Closed += (_, _) => NotifyPopupAuxHwnd(2, IntPtr.Zero);
            WindowPlacer.SurfaceWindow(win);
            var hw = new WindowInteropHelper(win).Handle;
            if (hw != IntPtr.Zero) NotifyPopupAuxHwnd(2, hw);
            return;
        }

        _popup?.BeginOpeningAuxWindow();

        try
        {
            var proc = Process.Start(new ProcessStartInfo(exe, "--history-child")
            {
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory,
            });
            if (proc == null)
            {
                NotifyPopupAuxHwnd(2, IntPtr.Zero);
                return;
            }

            try { AllowSetForegroundWindow(proc.Id); } catch { }
            _historyProc = proc;
            _historyChildHwnd = IntPtr.Zero;
            Log($"OpenHistory: child pid={proc.Id}");

            _ = proc.WaitForExitAsync().ContinueWith(_ =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (ReferenceEquals(_historyProc, proc))
                    {
                        _historyProc = null;
                        _historyChildHwnd = IntPtr.Zero;
                    }
                    try { proc.Dispose(); } catch { }
                    NotifyPopupAuxHwnd(2, IntPtr.Zero);
                }));
            });
        }
        catch (Exception ex)
        {
            Log("open history child failed: " + ex.Message);
            NotifyPopupAuxHwnd(2, IntPtr.Zero);
        }
    }

    /// <summary>打开组件管理窗口（独立子进程运行，关闭后不留驻内存）。</summary>
    private void OpenComponentManager(string[]? autoIds = null)
    {
        if (_componentProc is { HasExited: false })
        {
            BringProcessWindowToFront(_componentProc);
            return;
        }

        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe))
        {
            var win = autoIds is { Length: > 0 }
                ? new ComponentWindow(new ComponentManager(AppContext.BaseDirectory), autoIds)
                : new ComponentWindow(new ComponentManager(AppContext.BaseDirectory));
            win.ShowDialog();
            _engine?.MarkUsed();
            return;
        }

        var args = "--components-child";
        if (autoIds is { Length: > 0 })
            args += " --install-components=" + string.Join(",", autoIds);

        try
        {
            var proc = Process.Start(new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory,
            });
            _componentProc = proc;
            if (proc != null)
            {
                _ = proc.WaitForExitAsync().ContinueWith(_ =>
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (ReferenceEquals(_componentProc, proc)) _componentProc = null;
                        try { proc.Dispose(); } catch { }
                        _engine?.MarkUsed();
                    }));
                });
            }
        }
        catch (Exception ex)
        {
            Log("open components child failed: " + ex.Message);
        }
    }

    private void OpenSettings()
    {
        Log("OpenSettings enter");

        // 已经在独立子进程里开着就拉到前面，别开第二个
        if (_settingsProc is { HasExited: false })
        {
            Log("OpenSettings: child already open, activate");
            try { AllowSetForegroundWindow(_settingsProc.Id); } catch { }
            if (_settingsChildHwnd != IntPtr.Zero)
            {
                NotifyPopupAuxHwnd(1, _settingsChildHwnd);
                ShowWindow(_settingsChildHwnd, SW_RESTORE);
                SetForegroundWindow(_settingsChildHwnd);
            }
            else
            {
                BringProcessWindowToFront(_settingsProc);
            }
            return;
        }

        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe))
        {
            OpenSettingsInProcessFallback();
            return;
        }

        _popup?.BeginOpeningAuxWindow();

        try
        {
            var proc = Process.Start(new ProcessStartInfo(exe, "--settings-child")
            {
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory,
            });
            if (proc == null)
            {
                NotifyPopupAuxHwnd(1, IntPtr.Zero);
                return;
            }

            try { AllowSetForegroundWindow(proc.Id); } catch { }
            _settingsProc = proc;
            _settingsChildHwnd = IntPtr.Zero;
            Log($"OpenSettings: child pid={proc.Id}");

            _ = proc.WaitForExitAsync().ContinueWith(_ =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (ReferenceEquals(_settingsProc, proc))
                    {
                        _settingsProc = null;
                        _settingsChildHwnd = IntPtr.Zero;
                    }
                    try { proc.Dispose(); } catch { }

                    NotifyPopupAuxHwnd(1, IntPtr.Zero);
                    ApplyReloadedConfig();
                }));
            });
        }
        catch (Exception ex)
        {
            Log("OpenSettings child failed, fallback in-process: " + ex.Message);
            NotifyPopupAuxHwnd(1, IntPtr.Zero);
            OpenSettingsInProcessFallback();
        }
    }

    private void OpenSettingsInProcessFallback()
    {
        if (_settingsWin is { IsVisible: true })
        {
            _settingsWin.Activate();
            return;
        }

        _popup?.BeginOpeningAuxWindow();

        var win = new SettingsWindow(_config, _engine!)
        {
            Owner = null,
        };
        WindowPlacer.PlaceAvoiding(win, CollectOpenWindowRects("settings"));
        win.ThemePreviewRequested += BroadcastThemePreviewToWindows;
        _settingsWin = win;

        win.Closed += (_, _) =>
        {
            _settingsWin = null;
            NotifyPopupAuxHwnd(1, IntPtr.Zero);
            ApplyReloadedConfig();
        };

        WindowPlacer.SurfaceWindow(win);
        var settingsHwnd = new WindowInteropHelper(win).Handle;
        NotifyPopupAuxHwnd(1, settingsHwnd);
    }

    /// <summary>朗读结束（播放完或手动停止），恢复喇叭按钮状态。</summary>
    private void OnSpeechStopped()
    {
        // MediaPlayer 的播放结束事件来自后台线程，改按钮必须回到 UI 线程，
        // 否则会抛跨线程异常被吞掉，喇叭按钮播完后一直停在“朗读中”的高亮状态。
        try
        {
            if (Dispatcher.CheckAccess()) _popup?.SetSpeaking(false);
            else Dispatcher.BeginInvoke(new Action(() => { try { _popup?.SetSpeaking(false); } catch { } }));
        }
        catch { }
    }

    /// <summary>释放所有共享资源，退出和正常清理都走这里，避免遗漏或重复。</summary>
    private void CleanupResources()
    {
        _idleTimer?.Stop();
        try { _ipc?.Dispose(); } catch { }
        _ipc = null;
        try { if (_settingsProc is { HasExited: false }) _settingsProc.Kill(); } catch { }
        try { _settingsProc?.Dispose(); } catch { }
        _settingsProc = null;
        try { if (_inputProc is { HasExited: false }) _inputProc.Kill(); } catch { }
        try { _inputProc?.Dispose(); } catch { }
        _inputProc = null;
        try { if (_popupProc is { HasExited: false }) _popupProc.Kill(); } catch { }
        try { _popupProc?.Dispose(); } catch { }
        _popupProc = null;
        try { if (_componentProc is { HasExited: false }) _componentProc.Kill(); } catch { }
        try { _componentProc?.Dispose(); } catch { }
        _componentProc = null;
        try { if (_historyProc is { HasExited: false }) _historyProc.Kill(); } catch { }
        try { _historyProc?.Dispose(); } catch { }
        _historyProc = null;
        try { _speech?.Stop(); } catch { }
        try { _speech?.Dispose(); } catch { }
        _engine?.Dispose();
        _hotkey?.Dispose();
        _ocrHotkey?.Dispose();
        _replaceHotkey?.Dispose();
        _clipboardWatcher?.Dispose();
    }

    private void ExitApp()
    {
        _cts?.Cancel();
        CleanupResources();

        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _cts?.Cancel();
        CleanupResources();
        _tray?.Dispose();

        // 只有真正持有互斥体的实例才能释放，否则会抛 ApplicationException
        if (_ownsMutex)
        {
            try { _mutex?.ReleaseMutex(); } catch { /* 已释放或非持有者 */ }
        }

        base.OnExit(e);
    }
}
