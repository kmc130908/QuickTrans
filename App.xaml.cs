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
    private SpeechService? _speech;
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

        // 首次运行且一个模型都没有时，主动引导去组件管理里下载，
        // 只提示一次，之后由用户在托盘菜单里自己进。
        if (!_config.ComponentHintShown && _engine != null && _engine.ScanModels().Count == 0)
        {
            _config.ComponentHintShown = true;
            _config.Save();
            Dispatcher.BeginInvoke(new Action(OpenComponentManager),
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
        inputItem.Click += async (_, _) => await OpenInputPopupAsync();

        var settingsItem = new WinForms.ToolStripMenuItem("设置…");
        settingsItem.Click += (_, _) => OpenSettings();

        var componentsItem = new WinForms.ToolStripMenuItem("组件管理（引擎/模型）…");
        componentsItem.Click += (_, _) => OpenComponentManager();

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
        var popup = new PopupWindow();
        popup.ApplyOpacity(_config.PopupOpacity);

        popup.LanguageRequested += async lang => await OnLanguageRequestedAsync(lang);
        popup.ManualTextSubmitted += async text => await OnManualSubmitAsync(text);
        popup.OcrRequested += async () => await StartOcrAsync();

        popup.SettingsRequested += () =>
        {
            if (_popup is { IsVisible: true } p)
            {
                p.Close();

                // 浮窗是被动关闭的无焦点窗口，立刻弹设置会被它的关闭流程抢走焦点。
                // 延迟一拍，等浮窗真正销毁后再开。
                var timer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(150),
                };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    OpenSettings();
                };
                timer.Start();
            }
            else
            {
                Dispatcher.BeginInvoke(new Action(OpenSettings),
                    System.Windows.Threading.DispatcherPriority.Background);
            }
        };

        popup.SpeakRequested += async text =>
        {
            try
            {
                var lang = TranslationService.IsMostlyChinese(text) ? "zh" : "en";
                await (_speech ??= new SpeechService()).SpeakAsync(text, lang, _config);
            }
            catch (Exception ex)
            {
                Log("speak failed: " + ex.Message);
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

    private async Task OpenInputPopupAsync()
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
            await foreach (var piece in TranslationService.TranslateAsync(source, _config, _engine!.BaseUrl, target, ct))
                _popup.AppendChunk(piece);

            Log("translate done");
            _engine?.MarkUsed();
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

            await foreach (var piece in TranslationService.TranslateAsync(_currentSource, _config, _engine!.BaseUrl, target, ct))
                _popup.AppendChunk(piece);

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
        var oldStyle = _config.TrayIconStyle;
        var oldVariant = _config.EngineVariant;

        var win = new SettingsWindow(_config, _engine!);
        win.ShowDialog();
        RegisterHotkeys();

        // 换过图标样式就立刻应用，不用重启
        if (_config.TrayIconStyle != oldStyle && _tray != null)
        {
            var previous = _tray.Icon;
            _tray.Icon = CreateTrayIcon(_config.TrayIconStyle);
            try { previous?.Dispose(); } catch { /* 旧图标可能已被系统引用，忽略 */ }
        }

        // 换了引擎目录：先卸掉当前模型，下次翻译用新引擎重新加载
        if (!string.Equals(_config.EngineVariant, oldVariant, StringComparison.OrdinalIgnoreCase))
        {
            Log($"engine variant -> {_config.EngineVariant}");
            _engine?.Unload();
            _engine?.SwitchEngine(_config.EngineVariant);
            Log($"engine dir now {_engine?.EngineDir}");
        }

        // 自启开关和注册表对齐
        AutoStartService.Sync(_config.AutoStart);
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
