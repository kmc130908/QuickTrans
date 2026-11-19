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
    private EngineManager? _engine;
    private AppIpcServer? _ipc;
    private Process? _settingsProc;
    private IntPtr _settingsChildHwnd;
    private Process? _inputProc;
    private IntPtr _inputChildHwnd;
    private Process? _componentProc;
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

        var inputItem = new WinForms.ToolStripMenuItem("手动输入翻译");
        inputItem.Click += (_, _) => OpenInputPopup();

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
        menu.Items.Add(inputItem);
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
            Log("ocr hotkey FAILED: " + _ocrHotkey.LastError);
        else Log("ocr hotkey: " + _config.OcrHotkey);
    }

    private async void OnTranslateHotkey(object? sender, EventArgs e)
    {
        Log("translate hotkey pressed");
        if (_inputProc is { HasExited: false })
        {
            CloseInputChild();
            return;
        }
        if (_popup is { IsVisible: true })
        {
            _popup.Close();
            _popup = null;
            _cts?.Cancel();
            return;
        }
        await TranslateSelectionAsync();
    }

    private async void OnOcrHotkey(object? sender, EventArgs e)
    {
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
        popup.ApplyOpacity(_config.PopupOpacity);

        popup.LanguageRequested += async lang => await OnLanguageRequestedAsync(lang);
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
        return popup;
    }

    private async Task TranslateSelectionAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();

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

    /// <summary>翻译一段确定的文本。forcedTarget 为空时按自动方向决定。</summary>
    private async Task TranslateTextAsync(string source, string? forcedTarget = null, bool fromInput = false)
    {
        if (_popup == null)
        {
            _popup = CreatePopup();
            _popup.Show();
        }

        _currentSource = source;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        var target = TranslationService.ResolveTarget(source, _config, forcedTarget);
        _currentTarget = target;

        // 全程用局部变量引用“这一次”的浮窗：后台线程和 await 之后再读 _popup 字段，
        // 可能已经被关掉置空（空引用），或换成了下一次翻译的新浮窗（旧译文串进新窗口）。
        var popup = _popup;

        popup.ConfigureLanguages(_config.QuickLanguages, target);

        if (fromInput) popup.StartManualTranslate(EngineLabel(), target);
        else popup.ShowSource(source, EngineLabel());

        popup.SetStatus($"翻译为 {target} …");

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

            Log($"translate start -> {target}");
            popup.StartStreaming();

            // 把翻译数据的接收放到后台线程：await foreach 在后台消费 token，
            // 只往浮窗的线程安全缓冲区塞文字，不碰任何 UI 控件。
            // UI 线程由浮窗自己的 DispatcherTimer 每 100ms 刷一次，
            // 这样鼠标拖动、点击按钮全程零卡顿。
            var baseUrl = _engine!.BaseUrl;
            await Task.Run(async () =>
            {
                await foreach (var piece in TranslationService.TranslateAsync(
                    source, _config, baseUrl, target, ct))
                {
                    popup.AppendChunk(piece);
                }
            }, ct);

            Log("translate done");
            _engine?.MarkUsed();

            // 回 UI 线程收尾
            if (!ct.IsCancellationRequested) popup.Finish();
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
        popup.ConfigureLanguages(_config.QuickLanguages, target);
        popup.StartTranslate(target);

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var source = _currentSource;

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
                    source, _config, baseUrl, target, ct))
                {
                    popup.AppendChunk(piece);
                }
            }, ct);

            _engine?.MarkUsed();
            if (!ct.IsCancellationRequested) popup.Finish();
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

            // 向主进程查询当前已加载的模型路径，用于显示「已加载」角标
            string? loadedPath = null;
            var status = await AppIpcClient.SendAsync("GET_STATUS", connectTimeoutMs: 1000);
            if (status != null && status.StartsWith("OK\t", StringComparison.Ordinal))
            {
                var p = status[3..];
                if (!string.IsNullOrWhiteSpace(p)) loadedPath = p;
            }

            var win = new SettingsWindow(cfg, engine, childMode: true, loadedModelPath: loadedPath)
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
            };
            MainWindow = win;
            win.Closed += (_, _) =>
            {
                engine.Dispose();
                Shutdown();
            };
            win.Show();
            win.Activate();

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
            popup.ApplyOpacity(_config.PopupOpacity);
            popup.ApplyTtsVisibility(_config.TtsEnabled);
            popup.ConfigureLanguages(_config.QuickLanguages, _currentTarget);

            async Task RunTranslateAsync(string source, string? forcedTarget)
            {
                // 每次翻译前重读一次配置，保证在设置页刚改完的接口/模型立即生效
                _config = AppConfig.Load();
                popup.ApplyOpacity(_config.PopupOpacity);
                popup.ApplyTtsVisibility(_config.TtsEnabled);

                _currentSource = source;
                _cts?.Cancel();
                _cts = new CancellationTokenSource();
                var ct = _cts.Token;

                var target = TranslationService.ResolveTarget(source, _config, forcedTarget);
                _currentTarget = target;

                popup.ConfigureLanguages(_config.QuickLanguages, target);
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

                    popup.SetStatus($"翻译为 {target} …");
                    popup.StartStreaming();

                    await Task.Run(async () =>
                    {
                        await foreach (var piece in TranslationService.TranslateAsync(
                            source, _config, baseUrl, target, ct))
                        {
                            popup.AppendChunk(piece);
                        }
                    }, ct);

                    if (!string.Equals(_config.Engine, "api", StringComparison.OrdinalIgnoreCase))
                        _ = AppIpcClient.SendAsync("MARK_USED");

                    if (!ct.IsCancellationRequested) popup.Finish();
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

            popup.SettingsRequested += () => _ = AppIpcClient.SendAsync("OPEN_SETTINGS");

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
                    _popup?.SetSettingsWindow(hwnd);
                });
            }
            return "OK";
        }

        if (command.StartsWith("INPUT_HWND\t", StringComparison.Ordinal))
        {
            if (long.TryParse(command["INPUT_HWND\t".Length..], out var raw))
            {
                var hwnd = new IntPtr(raw);
                await Dispatcher.InvokeAsync(() => _inputChildHwnd = hwnd);
            }
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

        _config.CopyFrom(AppConfig.Load());

        // 同步浮窗静态与实例参数
        PopupWindow.AutoCloseEnabled = _config.PopupAutoClose;
        PopupWindow.AutoCloseSeconds = _config.PopupAutoCloseSeconds;
        PopupWindow.AutoClosePauseOnHover = _config.PopupAutoClosePauseOnHover;
        PopupWindow.TimedCloseEnabled = _config.PopupTimedCloseEnabled;
        PopupWindow.TimedCloseSeconds = _config.PopupTimedCloseSeconds;
        _popup?.ApplyOpacity(_config.PopupOpacity);
        _popup?.ApplyTtsVisibility(_config.TtsEnabled);

        if (!string.Equals(_config.Hotkey, oldHotkey, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(_config.OcrHotkey, oldOcrHotkey, StringComparison.OrdinalIgnoreCase))
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
            if (_settingsChildHwnd != IntPtr.Zero)
            {
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

        var popupTopmost = _popup?.Topmost ?? false;
        if (_popup != null) _popup.Topmost = false;
        _popup?.SlowStreaming();

        try
        {
            var proc = Process.Start(new ProcessStartInfo(exe, "--settings-child")
            {
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory,
            });
            if (proc == null)
            {
                if (_popup != null) _popup.Topmost = popupTopmost;
                _popup?.ResumeStreaming();
                return;
            }

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

                    if (_popup != null) _popup.Topmost = popupTopmost;
                    _popup?.ResumeStreaming();
                    _popup?.SetSettingsWindow(IntPtr.Zero);
                    ApplyReloadedConfig();
                }));
            });
        }
        catch (Exception ex)
        {
            Log("OpenSettings child failed, fallback in-process: " + ex.Message);
            if (_popup != null) _popup.Topmost = popupTopmost;
            _popup?.ResumeStreaming();
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

        var popupTopmost = _popup?.Topmost ?? false;
        if (_popup != null) _popup.Topmost = false;
        _popup?.SlowStreaming();

        var win = new SettingsWindow(_config, _engine!)
        {
            Owner = null,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Topmost = true,
        };
        _settingsWin = win;

        win.Closed += (_, _) =>
        {
            _settingsWin = null;
            win.Topmost = false;
            if (_popup != null) _popup.Topmost = popupTopmost;
            _popup?.ResumeStreaming();
            _popup?.SetSettingsWindow(IntPtr.Zero);
            ApplyReloadedConfig();
        };

        win.Show();
        var settingsHwnd = new WindowInteropHelper(win).Handle;
        _popup?.SetSettingsWindow(settingsHwnd);
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
        try { if (_componentProc is { HasExited: false }) _componentProc.Kill(); } catch { }
        try { _componentProc?.Dispose(); } catch { }
        _componentProc = null;
        try { _speech?.Stop(); } catch { }
        try { _speech?.Dispose(); } catch { }
        _engine?.Dispose();
        _hotkey?.Dispose();
        _ocrHotkey?.Dispose();
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
