using System.IO;
using System.Threading;
using System.Windows;
using QuickTrans.Models;
using QuickTrans.Services;
using QuickTrans.Views;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace QuickTrans;

public partial class App : System.Windows.Application
{
    private Mutex? _mutex;
    private bool _ownsMutex;
    private WinForms.NotifyIcon? _tray;
    private HotkeyManager? _hotkey;
    private HotkeyManager? _ocrHotkey;
    private EngineManager? _engine;
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

        if (_config.PrewarmModel && string.Equals(_config.Engine, "local", StringComparison.OrdinalIgnoreCase))
            _ = PrewarmAsync();

        if (e.Args.Any(a => a.Equals("--settings", StringComparison.OrdinalIgnoreCase)))
        {
            Dispatcher.BeginInvoke(new Action(OpenSettings),
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        Log("startup complete");

        // 后台检查新版本，失败静默
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
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var w = new ComponentWindow(
                    new ComponentManager(AppContext.BaseDirectory), autoComponents);
                w.ShowDialog();
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
        // 首次使用引导：优先于其他弹窗，只出现一次
        else if (!_config.WelcomeShown)
        {
            interrupted = true;
            _config.WelcomeShown = true;
            _config.Save();
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var w = new WelcomeWindow();
                w.ShowDialog();
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        // 首次运行引导：
        //  - 一个模型都没有 → 必然要引导
        //  - 有 NVIDIA 显卡但装的是 CPU 引擎 → 提示可以下 GPU 引擎
        // 只提示一次，之后用户自己从托盘菜单进。
        var guided = false;
        if (!_config.ComponentHintShown && _engine != null)
        {
            var needModel = _engine.ScanModels().Count == 0;
            var canUpgradeGpu = HasNvidiaGpu() && !HasCudaEngine();

            if (needModel || canUpgradeGpu)
            {
                guided = true;
                _config.ComponentHintShown = true;
                _config.Save();
                Dispatcher.BeginInvoke(new Action(OpenComponentManager),
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
        using var tmp = Drawing.Icon.FromHandle(handle);
        return (Drawing.Icon)tmp.Clone();
    }

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
            // 没取到就转成手动输入，用户可以直接打字
            _currentSource = "";
            _currentTarget = _config.TargetLanguage;
            _popup.ConfigureLanguages(_config.QuickLanguages, _currentTarget);
            _popup.EnterInputMode("没有选中文本，输入后回车");
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

    private void OpenInputPopup()
    {
        if (_popup is { IsVisible: true })
        {
            _popup.Close();
            _popup = null;
        }

        _popup = CreatePopup();
        _popup.Show();
        _currentSource = "";
        _currentTarget = _config.TargetLanguage;
        _popup.ConfigureLanguages(_config.QuickLanguages, _currentTarget);
        _popup.EnterInputMode("输入后回车翻译");
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

        _popup.ConfigureLanguages(_config.QuickLanguages, target);

        if (fromInput) _popup.StartManualTranslate(EngineLabel(), target);
        else _popup.ShowSource(source, EngineLabel());

        _popup.SetStatus($"翻译为 {target} …");

        try
        {
            if (!string.Equals(_config.Engine, "api", StringComparison.OrdinalIgnoreCase))
            {
                var ready = await EnsureEngineAsync(ct);
                if (!ready.Ok)
                {
                    _popup.ShowError(ready.Message);
                    return;
                }
            }

            Log($"translate start -> {target}");
            _popup.StartStreaming();

            // 把翻译数据的接收放到后台线程：await foreach 在后台消费 token，
            // 只往浮窗的线程安全缓冲区塞文字，不碰任何 UI 控件。
            // UI 线程由浮窗自己的 DispatcherTimer 每 100ms 刷一次，
            // 这样鼠标拖动、点击按钮全程零卡顿。
            await Task.Run(async () =>
            {
                await foreach (var piece in TranslationService.TranslateAsync(
                    source, _config, _engine!.BaseUrl, target, ct))
                {
                    _popup.AppendChunk(piece);
                }
            }, ct);

            Log("translate done");
            _engine?.MarkUsed();

            // 回 UI 线程收尾
            _popup.Finish();
        }
        catch (OperationCanceledException)
        {
            // 被新的翻译取代，忽略
        }
        catch (Exception ex)
        {
            Log("translate failed: " + ex.Message);
            _popup.ShowError("翻译失败：" + ex.Message);
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

        if (_popup == null) return;
        _popup.ConfigureLanguages(_config.QuickLanguages, target);
        _popup.StartTranslate(target);

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
                    _popup.ShowError(ready.Message);
                    return;
                }
            }

            _popup.StartStreaming();

            await Task.Run(async () =>
            {
                await foreach (var piece in TranslationService.TranslateAsync(
                    _currentSource, _config, _engine!.BaseUrl, target, ct))
                {
                    _popup.AppendChunk(piece);
                }
            }, ct);

            _engine?.MarkUsed();
            _popup.Finish();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _popup.ShowError("翻译失败：" + ex.Message);
        }
    }

    private async Task StartOcrAsync()
    {
        // 选择区域时先把浮窗收起来，免得挡住要识别的内容
        var previousSource = _currentSource;
        if (_popup is { IsVisible: true })
        {
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
        if (_engine.IsRunning)
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
            // 精简版引擎没有 CUDA 组件，即使传 -ngl 999 也只会静默跑 CPU，这里直接说明白
            Log("配置要求 GPU，但当前引擎没有 CUDA 组件，自动改用 CPU");
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

    /// <summary>当前引擎目录里有没有 CUDA 后端，即是否已经是 GPU 版。</summary>
    private static bool HasCudaEngine() =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "engine", "ggml-cuda.dll"));

    /// <summary>打开组件管理窗口（下载 GPU 引擎、模型）。</summary>
    private void OpenComponentManager()
    {
        var win = new ComponentWindow(new ComponentManager(AppContext.BaseDirectory))
        {
            Owner = null,
        };
        win.ShowDialog();

        // 刚下完模型可能立刻可用，刷新一下引擎状态
        _engine?.MarkUsed();
    }

    private void OpenSettings()
    {
        Log("OpenSettings enter");
        // 已经开着就拉到前面，别开第二个
        if (_settingsWin is { IsVisible: true })
        {
            Log("OpenSettings: already open, activate");
            _settingsWin.Activate();
            return;
        }

        var oldStyle = _config.TrayIconStyle;
        var oldVariant = _config.EngineVariant;

        var popupTopmost = _popup?.Topmost ?? false;
        if (_popup != null) _popup.Topmost = false;

        // 打开设置窗口前，把浮窗刷屏降到 500ms：给设置窗口首渲染让出
        // UI 线程（消除卡顿），同时译文仍在缓慢流动，不会冻结。
        _popup?.SlowStreaming();

        var win = new SettingsWindow(_config, _engine!)
        {
            Owner = null,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            // 设置窗口也 Topmost：浮窗是 Topmost 的，两个都置顶时后 Show 的在上，
            // 这样设置窗口才能盖住浮窗。关闭时在 Closed 里还原为普通层级。
            Topmost = true,
        };
        _settingsWin = win;
        Log("OpenSettings: window created");

        win.Closed += (_, _) =>
        {
            _settingsWin = null;
            win.Topmost = false;   // 关闭后取消置顶，别一直盖着别的程序

            if (_popup != null) _popup.Topmost = popupTopmost;
            _popup?.ResumeStreaming();   // 恢复浮窗刷屏，补上暂停期间的内容
            _popup?.SetSettingsWindow(IntPtr.Zero);   // 清空设置页句柄
            RegisterHotkeys();

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

            AutoStartService.Sync(_config.AutoStart);
        };

        win.Show();
        Log("OpenSettings: shown");

        // 把设置页句柄告诉浮窗：前台切到设置页不算浮窗失焦
        var settingsHwnd = new System.Windows.Interop.WindowInteropHelper(win).Handle;
        _popup?.SetSettingsWindow(settingsHwnd);

        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                win.Activate();
                win.Topmost = true;
                win.Topmost = false;
                win.Activate();
            }
            catch { }
        }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    /// <summary>朗读结束（播放完或手动停止），恢复喇叭按钮状态。</summary>
    private void OnSpeechStopped()
    {
        try { _popup?.SetSpeaking(false); } catch { }
    }

    /// <summary>释放所有共享资源，退出和正常清理都走这里，避免遗漏或重复。</summary>
    private void CleanupResources()
    {
        _idleTimer?.Stop();
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
