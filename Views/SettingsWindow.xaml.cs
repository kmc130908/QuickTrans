using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using QuickTrans.Models;
using QuickTrans.Services;

namespace QuickTrans.Views;

public sealed class ModelRow
{
    public string DisplayName { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public string Badge { get; init; } = "";
    public Visibility BadgeVisibility { get; init; } = Visibility.Collapsed;
    public string FilePath { get; init; } = "";
}

public sealed class LangOption
{
    public string Name { get; set; } = "";
    public bool IsSelected { get; set; }
}

public partial class SettingsWindow : Window
{
    private readonly AppConfig _config;
    private readonly EngineManager _engine;
    private readonly ObservableCollection<ModelRow> _rows = new();
    private readonly List<LangOption> _langOptions = new();
    private bool _gpuOk;

    private static readonly string[] Languages =
    {
        "简体中文", "繁体中文", "英语", "日语", "韩语", "法语", "德语",
        "西班牙语", "俄语", "葡萄牙语", "意大利语", "阿拉伯语", "泰语", "越南语",
    };

    private static readonly (string Value, string Label)[] GrabModes =
    {
        ("auto", "自动（先 UIA，失败降级剪贴板）"),
        ("uia", "仅 UI Automation"),
        ("clipboard", "仅剪贴板"),
    };

    private static readonly string[] MinuteOptions = { "0.5", "1", "2", "3", "5", "10", "15", "20", "30" };

    public SettingsWindow(AppConfig config, EngineManager engine)
    {
        InitializeComponent();

        _config = config;
        _engine = engine;

        ModelList.ItemsSource = _rows;

        LoadUi();
        RefreshModels();

        // 初始化完成后再挂监听，否则 LoadUi 赋值会被当成用户改动
        HookDirtyTracking();
        SetDirty(false);

        // 记录打开设置时的配置状态，点取消时用它还原
        _snapshot = _config.Clone();

        Dispatcher.BeginInvoke(new Action(() =>
        {
            // XAML 里的普通订阅拿不到已被内部处理掉的滚轮事件，
            // 这里用 handledEventsToo=true 再挂一遍，确保滑块和下拉框不会因为滚轮改值。
            foreach (var element in FindAllScrollWheelSensitive(this))
            {
                element.AddHandler(UIElement.PreviewMouseWheelEvent,
                    new System.Windows.Input.MouseWheelEventHandler(OnComboWheel), true);
            }

            // 初始化过程中的假改动一并清掉
            SetDirty(false);
        }), System.Windows.Threading.DispatcherPriority.Loaded);

        CheckEngineAsync();

        // 布局和数据绑定完成前会产生一些假改动事件（Slider 微调、列表自动选中），
        // 等界面彻底稳定后再把"有修改"状态清零。
        Loaded += (_, _) =>
        {
            // 从浮窗（ShowActivated=false 的无焦点窗口）打开时，系统可能不给焦点。
            // 这里只做激活，不闪 Topmost —— 置顶状态由 OpenSettings 统一管理：
            // 打开设置时它会把本窗口设为 Topmost、把浮窗取消置顶，
            // 关掉后再还原。闪一下反而会让本窗口掉到浮窗下面。
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (WindowState == WindowState.Minimized)
                        WindowState = WindowState.Normal;

                    if (!IsVisible) Show();
                    Activate();
                    Focus();
                }
                catch { /* 窗口已在关闭流程中，忽略 */ }
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);

            Dispatcher.BeginInvoke(new Action(() =>
            {
                SetDirty(false);
                SaveHint.Text = "";
            }), System.Windows.Threading.DispatcherPriority.ContextIdle);
        };
    }

    /// <summary>按接口类型调整表单。</summary>
    private void ApplyApiTypeLayout()
    {
        if (ApiUrlLabel == null || ApiHint == null) return;

        var idx = ApiTypeBox.SelectedIndex;
        var needsUrl = idx == 0;      // 只有 OpenAI 兼容需要 Base URL
        var needsKey = idx != 3;      // 谷歌免费端点不需要密钥
        var needsModel = idx == 0;

        ApiUrlLabel.Visibility = needsUrl ? Visibility.Visible : Visibility.Collapsed;
        ApiUrlBox.Visibility = needsUrl ? Visibility.Visible : Visibility.Collapsed;
        ApiModelLabel.Visibility = needsModel ? Visibility.Visible : Visibility.Collapsed;
        ApiModelBox.Visibility = needsModel ? Visibility.Visible : Visibility.Collapsed;
        ApiKeyLabel.Visibility = needsKey ? Visibility.Visible : Visibility.Collapsed;
        ApiKeyBox.Visibility = needsKey ? Visibility.Visible : Visibility.Collapsed;
        ApiKeyHelpLink.Visibility = needsKey ? Visibility.Visible : Visibility.Collapsed;

        ApiKeyLabel.Text = idx switch
        {
            1 => "appid:密钥",
            2 => "appKey:appSecret",
            _ => "API Key",
        };

        ApiKeyBox.ToolTip = idx switch
        {
            1 => "格式 appid:密钥，例如 20240101000000000:AbCdEfGhIjKlMnOp",
            2 => "格式 appKey:appSecret",
            _ => null,
        };

        ApiHint.Text = idx switch
        {
            1 => "百度翻译不需要 Base URL。控制台能查到 APP ID 和密钥，按「appid:密钥」填在上一栏。",
            2 => "有道智云按「appKey:appSecret」填在密钥栏，不需要 Base URL。",
            3 => "谷歌免费端点不需要密钥，但国内直连通常不通，需要系统级代理。",
            _ => "填任何 OpenAI 兼容接口都行：DeepSeek、智谱、DeepL 代理、公司网关。",
        };
    }

    private void OnApiTypeChanged(object sender, SelectionChangedEventArgs e) => ApplyApiTypeLayout();

    private void OnAutoCloseChanged(object sender, RoutedEventArgs e) => ApplyAutoCloseState();

    /// <summary>关掉自动消失后，秒数输入框就失去意义了，一并置灰。</summary>
    private void ApplyAutoCloseState()
    {
        if (AutoCloseSecondsBox == null) return;

        var on = AutoClosePopupBox.IsChecked == true;
        AutoCloseSecondsBox.IsEnabled = on;
        AutoCloseSecondsBox.Opacity = on ? 1.0 : 0.45;
    }

    /// <summary>打开当前接口类型的密钥申请页。</summary>
    private void OnApiKeyHelpClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var url = _config.ApiType switch
        {
            "baidu" => "https://fanyi-api.baidu.com/product/11",
            "youdao" => "https://ai.youdao.com/console/#/service-singleton/text-translation",
            "google" => "",
            _ => "https://platform.openai.com/api-keys",
        };

        if (string.IsNullOrWhiteSpace(url)) return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
            {
                UseShellExecute = true,
            });
        }
        catch { /* 打不开浏览器就算了 */ }
    }

    /// <summary>切换引擎版本时同步设备选项：CPU 引擎没有 CUDA，只能跑 CPU。</summary>
    private void OnEngineVariantChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        ApplyEngineVariantState();
    }

    /// <summary>CPU 引擎时禁用 GPU 单选，并强制选回 CPU。</summary>
    private void ApplyEngineVariantState()
    {
        var isCpuEngine = EngineVariantBox.SelectedIndex == 1;
        GpuRadio.IsEnabled = !isCpuEngine;

        if (isCpuEngine && GpuRadio.IsChecked == true)
            CpuRadio.IsChecked = true;
    }

    private UpdateService.UpdateInfo? _pendingUpdate;
    private bool _updateDownloaded;

    /// <summary>
    /// 更新按钮的三态：检查 → 有新版则下载 → 下载完则重启更新。
    /// </summary>
    private async void OnCheckUpdateClick(object sender, RoutedEventArgs e)
    {
        // 状态三：已经下载好了，重启完成更新
        if (_updateDownloaded)
        {
            var ok = MessageBox.Show(this,
                "点击确定后软件会关闭，自动替换为新版本并重新启动。\n\n" +
                "如果替换失败，安装目录里会保留 QuickTrans.exe.old，可以手动改回来。",
                "重启并更新", MessageBoxButton.OKCancel, MessageBoxImage.Question);

            if (ok != MessageBoxResult.OK) return;

            if (UpdateService.ApplyAndRestart())
            {
                Application.Current.Shutdown();
            }
            else
            {
                UpdateHint.Text = "启动更新程序失败，请手动下载覆盖。";
            }
            return;
        }

        // 状态二：已发现新版，开始下载
        if (_pendingUpdate != null)
        {
            await DownloadUpdateAsync(_pendingUpdate);
            return;
        }

        // 状态一：检查
        CheckUpdateButton.IsEnabled = false;
        CheckUpdateButton.Content = "检查中…";
        UpdateHint.Text = "";

        try
        {
            var info = await new UpdateService().CheckAsync();
            if (info == null)
            {
                UpdateHint.Text = $"已是最新版本（{UpdateService.CurrentVersion}）";
                CheckUpdateButton.Content = "检查更新";
            }
            else
            {
                _pendingUpdate = info;
                UpdateHint.Text = $"发现新版本 {info.Version}。{info.Notes}";
                CheckUpdateButton.Content = "下载更新";
            }
        }
        catch (Exception ex)
        {
            UpdateHint.Text = "检查失败：" + ex.Message;
            CheckUpdateButton.Content = "检查更新";
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    /// <summary>下载新版本的 exe，下载完按钮变成「重启并更新」。</summary>
    private async Task DownloadUpdateAsync(UpdateService.UpdateInfo info)
    {
        CheckUpdateButton.IsEnabled = false;
        CheckUpdateButton.Content = "下载中…";

        var progress = new Progress<(long Received, long Total)>(p =>
        {
            UpdateHint.Text = p.Total > 0
                ? $"正在下载 {info.Version}：{p.Received / 1024.0 / 1024:F0} / {p.Total / 1024.0 / 1024:F0} MB"
                : "正在下载…";
        });

        try
        {
            await new UpdateService().DownloadAsync(info, progress);
            _updateDownloaded = true;
            UpdateHint.Text = $"新版本 {info.Version} 已下载完成，点「重启并更新」生效。";
            CheckUpdateButton.Content = "重启并更新";
        }
        catch (Exception ex)
        {
            UpdateHint.Text = "下载失败：" + ex.Message + "（已下载部分会保留，可重试）";
            CheckUpdateButton.Content = "重新下载";
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    /// <summary>打开组件管理窗口。</summary>
    private void OnOpenComponentsClick(object sender, RoutedEventArgs e)
    {
        var win = new ComponentWindow(new ComponentManager(AppContext.BaseDirectory))
        {
            Owner = this,
        };
        win.ShowDialog();
    }

    private void OnTtsRateChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateTtsRateLabel();

    private void UpdateTtsRateLabel()
    {
        if (TtsRateLabel == null) return;

        var v = (int)Math.Round(TtsRateSlider.Value);
        TtsRateLabel.Text = v switch
        {
            0 => "常速",
            < -25 => $"很慢 ({v}%)",
            < 0 => $"稍慢 ({v}%)",
            > 50 => $"很快 (+{v}%)",
            _ => $"稍快 (+{v}%)",
        };
    }

    // ---------- 多套接口配置 ----------

    private bool _loadingProfiles;

    private void RefreshProfileList(string? selectName = null)
    {
        _loadingProfiles = true;
        var names = _config.ApiProfiles.Select(p => p.Name).ToList();
        ProfileBox.ItemsSource = names;

        var target = selectName ?? _config.ActiveProfile;
        if (!string.IsNullOrEmpty(target) && names.Contains(target))
            ProfileBox.SelectedItem = target;

        // 名称框跟着选中项走
        var selected = ProfileBox.SelectedItem as string;
        ProfileNameBox.Text = _config.ApiProfiles
            .FirstOrDefault(p => p.Name == selected)?.Name ?? "";

        _loadingProfiles = false;
    }

    private void LoadProfileIntoForm(ApiProfile p)
    {
        _loadingProfiles = true;
        ApiTypeBox.SelectedIndex = p.Type switch
        {
            "baidu" => 1,
            "youdao" => 2,
            "google" => 3,
            _ => 0,
        };
        ApiUrlBox.Text = p.BaseUrl;
        ApiKeyBox.Text = p.ApiKey;
        ApiModelBox.Text = p.Model;
        ProfileNameBox.Text = p.Name;
        _loadingProfiles = false;

        ApplyApiTypeLayout();
    }

    /// <summary>把表单内容写回配置字段（不含 profiles 列表本身）。</summary>
    private void ApplyFormToConfig()
    {
        _config.ApiType = ApiTypeBox.SelectedIndex switch
        {
            1 => "baidu",
            2 => "youdao",
            3 => "google",
            _ => "openai",
        };
        _config.ApiBaseUrl = ApiUrlBox.Text.Trim();
        _config.ApiKey = ApiKeyBox.Text.Trim();
        _config.ApiModel = ApiModelBox.Text.Trim();
    }

    private void OnProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingProfiles || _suppressDirty) return;
        if (ProfileBox.SelectedItem is not string name) return;

        var p = _config.ApiProfiles.FirstOrDefault(x => x.Name == name);
        if (p == null) return;

        LoadProfileIntoForm(p);

        // 切换即生效，不用再点保存
        ApplyFormToConfig();
        _config.ActiveProfile = p.Name;
        _config.Save();

        SetDirty(false);
        SaveHint.Text = $"已切换到「{p.Name}」";
        SaveHint.Foreground = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x2E, 0x8B, 0x57));
    }

    private void OnNewProfileClick(object sender, RoutedEventArgs e)
    {
        var n = 1;
        while (_config.ApiProfiles.Any(p => p.Name == $"接口 {n}")) n++;

        var profile = new ApiProfile { Name = $"接口 {n}" };
        _config.ApiProfiles.Add(profile);
        _config.Save();

        RefreshProfileList(profile.Name);
        LoadProfileIntoForm(profile);
        SetDirty(false);
        SaveHint.Text = "已新建，填好后点保存";
    }

    private void OnDeleteProfileClick(object sender, RoutedEventArgs e)
    {
        if (ProfileBox.SelectedItem is not string name) return;
        var p = _config.ApiProfiles.FirstOrDefault(x => x.Name == name);
        if (p == null) return;

        _config.ApiProfiles.Remove(p);
        if (_config.ActiveProfile == p.Name) _config.ActiveProfile = "";
        _config.Save();

        RefreshProfileList();
        SetDirty(false);
        SaveHint.Text = $"已删除「{name}」";
    }

    private bool _dirty;

    /// <summary>回滚时重新填充控件会触发各种变更事件，这期间不标记为"有改动"。</summary>
    private bool _suppressDirty;

    /// <summary>打开设置页那一刻的配置快照，点取消时回滚到这里。</summary>
    private AppConfig? _snapshot;
    private void SetDirty(bool dirty)
    {
        if (_suppressDirty) return;

        _dirty = dirty;
        SaveButton.IsEnabled = dirty;

        if (dirty)
        {
            SaveHint.Text = "有未保存的修改";
            SaveHint.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x8A, 0x8A, 0x93));
        }
        else if (SaveHint.Text == "有未保存的修改")
        {
            SaveHint.Text = "";
        }
    }

    /// <summary>
    /// 在窗口层挂路由事件，捕获子控件改动。
    /// 注意要按事件源过滤：ListBox 的选中项变化也会冒泡 Selector.SelectionChanged，
    /// 不清掉的话初始化和列表刷新都会被误判成"用户改了设置"。
    /// </summary>
    private void HookDirtyTracking()
    {
        // ComboBox 内部也有个 TextBox，布局测量时会触发 TextChanged，
        // 滚动页面就被误判成用户改动了，这里要把它的内部控件排除掉
        AddHandler(System.Windows.Controls.TextBox.TextChangedEvent,
            new TextChangedEventHandler((_, e) =>
            {
                if (e.OriginalSource is TextBox tb && FindAncestor<ComboBox>(tb) != null) return;
                SetDirty(true);
            }), true);

        AddHandler(System.Windows.Controls.Primitives.Selector.SelectionChangedEvent,
            new SelectionChangedEventHandler((_, e) =>
            {
                if (e.OriginalSource is ComboBox) SetDirty(true);
            }), true);

        AddHandler(System.Windows.Controls.Primitives.ToggleButton.CheckedEvent,
            new RoutedEventHandler((_, e) =>
            {
                if (e.OriginalSource is CheckBox or RadioButton) SetDirty(true);
            }), true);

        AddHandler(System.Windows.Controls.Primitives.ToggleButton.UncheckedEvent,
            new RoutedEventHandler((_, e) =>
            {
                if (e.OriginalSource is CheckBox or RadioButton) SetDirty(true);
            }), true);

        // 滑块：滚轮经过、布局重算都会改 Value，只有用户按住拖动才是真的想改。
        AddHandler(System.Windows.Controls.Primitives.RangeBase.ValueChangedEvent,
            new RoutedPropertyChangedEventHandler<double>((_, _) =>
            {
                if (System.Windows.Input.Mouse.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
                    SetDirty(true);
            }), true);
    }

    private static IEnumerable<UIElement> FindAllScrollWheelSensitive(DependencyObject root)
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);

            if (child is ComboBox or Slider)
                yield return (UIElement)child;

            foreach (var nested in FindAllScrollWheelSensitive(child))
                yield return nested;
        }
    }

    /// <summary>
    /// 下拉框和滑块都会响应滚轮改值，用户只是想翻页却把设置改了。
    /// 在控件上截住，把滚动转给外层。
    /// </summary>
    private void OnComboWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (MainScroll != null)
            MainScroll.ScrollToVerticalOffset(MainScroll.VerticalOffset - e.Delta / 3.0);
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node != null)
        {
            if (node is T match) return match;

            DependencyObject? parent;
            try
            {
                parent = System.Windows.Media.VisualTreeHelper.GetParent(node);
            }
            catch
            {
                parent = null;
            }

            if (parent == null && node is System.Windows.FrameworkContentElement fce)
                parent = fce.Parent;

            if (parent == null && node is System.Windows.ContentElement ce)
                parent = System.Windows.ContentOperations.GetParent(ce);

            node = parent;
        }

        return null;
    }

    private void LoadUi()
    {
        LocalRadio.IsChecked = !string.Equals(_config.Engine, "api", StringComparison.OrdinalIgnoreCase);
        ApiRadio.IsChecked = string.Equals(_config.Engine, "api", StringComparison.OrdinalIgnoreCase);

        ApiTypeBox.SelectedIndex = string.Equals(_config.ApiType, "baidu", StringComparison.OrdinalIgnoreCase) ? 1
            : string.Equals(_config.ApiType, "youdao", StringComparison.OrdinalIgnoreCase) ? 2
            : string.Equals(_config.ApiType, "google", StringComparison.OrdinalIgnoreCase) ? 3
            : 0;
        ApiUrlBox.Text = _config.ApiBaseUrl;
        ApiKeyBox.Text = _config.ApiKey;
        ApiModelBox.Text = _config.ApiModel;

        // 老版本只存了一组配置，第一次运行时把它转成一条 profile
        if (_config.ApiProfiles.Count == 0 &&
            (!string.IsNullOrWhiteSpace(_config.ApiBaseUrl) || !string.IsNullOrWhiteSpace(_config.ApiKey)))
        {
            _config.ApiProfiles.Add(new ApiProfile
            {
                Name = "默认",
                Type = _config.ApiType,
                BaseUrl = _config.ApiBaseUrl,
                ApiKey = _config.ApiKey,
                Model = _config.ApiModel,
            });
            _config.ActiveProfile = "默认";
            _config.Save();
        }

        RefreshProfileList();
        ApplyApiTypeLayout();

        // 语音朗读
        TtsEnabledBox.IsChecked = _config.TtsEnabled;

        var voices = SpeechService.GetSystemVoices();
        var zhVoiceNames = voices.Where(v => v.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
                                 .Select(v => v.Name).ToList();
        var enVoiceNames = voices.Where(v => v.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase))
                                 .Select(v => v.Name).ToList();

        TtsVoiceZhBox.ItemsSource = zhVoiceNames;
        TtsVoiceZhBox.SelectedItem = zhVoiceNames.FirstOrDefault(n => n == _config.TtsVoiceZh)
                                     ?? zhVoiceNames.FirstOrDefault();

        TtsVoiceEnBox.ItemsSource = enVoiceNames;
        TtsVoiceEnBox.SelectedItem = enVoiceNames.FirstOrDefault(n => n == _config.TtsVoiceEn)
                                     ?? enVoiceNames.FirstOrDefault();

        if (zhVoiceNames.Count == 0)
        {
            TtsHint.Text = "系统里没有中文语音包，请到「设置 → 时间和语言 → 语音」里添加。";
            TtsVoiceZhBox.IsEnabled = false;
        }

        TtsRateSlider.Value = Math.Clamp(_config.TtsRatePercent, -50, 100);
        UpdateTtsRateLabel();

        // 关于与更新
        VersionText.Text = UpdateService.CurrentVersion;
        ShowSettingsOnStartupBox.IsChecked = _config.ShowSettingsOnStartup;
        UpdateHint.Text = "";

        HotkeyBox.Text = _config.Hotkey;
        OcrHotkeyBox.Text = _config.OcrHotkey;

        LanguageBox.ItemsSource = Languages;
        LanguageBox.SelectedItem = Languages.Contains(_config.TargetLanguage) ? _config.TargetLanguage : Languages[0];

        GrabModeBox.ItemsSource = GrabModes.Select(g => g.Label).ToList();
        var idx = Array.FindIndex(GrabModes, g => g.Value == _config.GrabMode);
        GrabModeBox.SelectedIndex = idx >= 0 ? idx : 0;

        AutoDirectionBox.IsChecked = _config.AutoDirection;

        GpuRadio.IsChecked = _config.UseGpu;
        CpuRadio.IsChecked = !_config.UseGpu;

        AutoStartBox.IsChecked = AutoStartService.IsEnabled();
        EngineVariantBox.SelectedIndex = string.Equals(_config.EngineVariant, "cpu", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        ApplyEngineVariantState();

        // 卸载策略
        switch (_config.UnloadPolicy)
        {
            case "onPopupClose":
                UnloadOnCloseRadio.IsChecked = true;
                break;
            case "afterMinutes":
                UnloadIdleRadio.IsChecked = true;
                break;
            default:
                KeepLoadedRadio.IsChecked = true;
                break;
        }

        UnloadMinutesBox.ItemsSource = MinuteOptions;
        UnloadMinutesBox.Text = _config.UnloadMinutes.ToString("0.0#", CultureInfo.InvariantCulture);
        UnloadMinutesBox.IsEnabled = UnloadIdleRadio.IsChecked == true;

        OpacitySlider.Value = Math.Clamp(_config.PopupOpacity, 0.3, 1.0);
        OpacityLabel.Text = $"{OpacitySlider.Value * 100:F0}%";

        IconStyleBox.SelectedIndex = Math.Clamp(_config.TrayIconStyle - 1, 0, 3);

        AutoClosePopupBox.IsChecked = _config.PopupAutoClose;
        AutoCloseSecondsBox.Text = _config.PopupAutoCloseSeconds.ToString();
        AutoCloseHoverBox.IsChecked = _config.PopupAutoClosePauseOnHover;
        TimedCloseBox.IsChecked = _config.PopupTimedCloseEnabled;
        TimedCloseSecondsBox.Text = _config.PopupTimedCloseSeconds.ToString();
        ApplyAutoCloseState();

        // 常用语言
        _langOptions.Clear();
        foreach (var lang in Languages)
        {
            _langOptions.Add(new LangOption
            {
                Name = lang,
                IsSelected = _config.QuickLanguages.Contains(lang),
            });
        }
        LangCheckList.ItemsSource = _langOptions;
    }

    private async void CheckEngineAsync()
    {
        if (!_engine.EngineReady)
        {
            EngineStatus.Text = $"⚠ 引擎缺失：{_engine.EngineDir} 下找不到 llama-server.exe";
            EngineStatus.Foreground = System.Windows.Media.Brushes.Firebrick;
            return;
        }

        // 每次都重新检测：引擎版本（gpu/cpu）切换后 GPU 能力会变，
        // 缓存旧结果会导致「切回 GPU 引擎后仍显示未检测到 GPU」。
        EngineStatus.Text = "正在检测显卡…";
        _gpuOk = await Task.Run(() => EngineManager.DetectGpu(_engine.EngineDir));

        // 只有一套引擎时不提供切换；引擎没有 CUDA 时不提供设备选择
        var (hasGpuEngine, hasCpuEngine) = EngineManager.DetectVariants(_engine.BaseDir);
        var canSwitch = hasGpuEngine && hasCpuEngine;
        EngineVariantPanel.Visibility = canSwitch ? Visibility.Visible : Visibility.Collapsed;
        EngineVariantHint.Visibility = canSwitch ? Visibility.Visible : Visibility.Collapsed;

        // GPU 能力决定设备选择是否可见：不管之前是什么状态，都按当前结果重设，
        // 避免「切到 CPU 引擎隐藏了设备选择、切回 GPU 引擎却不恢复」的问题。
        DevicePanel.Visibility = _gpuOk ? Visibility.Visible : Visibility.Collapsed;
        GpuRadio.IsEnabled = _gpuOk;
        if (!_gpuOk)
        {
            CpuRadio.IsChecked = true;
        }

        // 纯 CPU 精简版：只有一套引擎且它没有 CUDA，不是"检测不到显卡"
        var (_, hasCpuEngineDir) = EngineManager.DetectVariants(_engine.BaseDir);
        if (!_gpuOk && !hasCpuEngineDir)
        {
            EngineStatus.Text = $"✓ 引擎就绪（CPU 精简版）· 模型目录：{_engine.ModelsDir}";
            EngineStatus.Foreground = System.Windows.Media.Brushes.SeaGreen;
            return;
        }

        EngineStatus.Text = _gpuOk
            ? $"✓ 引擎就绪，已检测到 GPU 加速 · 模型目录：{_engine.ModelsDir}"
            : $"⚠ 引擎就绪，但未检测到 GPU，将使用 CPU 推理（会慢很多）· 模型目录：{_engine.ModelsDir}";
        EngineStatus.Foreground = _gpuOk
            ? System.Windows.Media.Brushes.SeaGreen
            : System.Windows.Media.Brushes.DarkOrange;
    }

    private void RefreshModels(string? highlight = null)
    {
        var current = highlight ?? _config.SelectedModel;
        var models = _engine.ScanModels();
        var loadedPath = _engine.LoadedModelPath;

        _rows.Clear();
        foreach (var m in models)
        {
            var isCurrent = string.Equals(m.DisplayName, current, StringComparison.OrdinalIgnoreCase);
            var isLoaded = loadedPath != null
                           && string.Equals(Path.GetFullPath(loadedPath), Path.GetFullPath(m.FilePath), StringComparison.OrdinalIgnoreCase);

            var badge = isLoaded ? "已加载"
                : isCurrent ? "默认"
                : "";

            var note = (m.DisplayName.Contains("2Bit", StringComparison.OrdinalIgnoreCase)
                        || m.DisplayName.Contains("1.25", StringComparison.OrdinalIgnoreCase))
                ? " · 需腾讯专用内核，本引擎无法加载"
                : "";

            _rows.Add(new ModelRow
            {
                DisplayName = m.DisplayName,
                Subtitle = $"{m.SizeText} · {m.Quant}{note}",
                Badge = badge,
                BadgeVisibility = string.IsNullOrEmpty(badge) ? Visibility.Collapsed : Visibility.Visible,
                FilePath = m.FilePath,
            });
        }

        if (_rows.Count == 0)
        {
            _rows.Add(new ModelRow
            {
                DisplayName = "还没有模型",
                Subtitle = "把 .gguf 文件放进 models 文件夹，然后点右上角刷新",
            });
        }
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        RefreshModels();
        SaveHint.Text = $"扫描完成，共 {_engine.ScanModels().Count} 个模型";
    }

    private void OnOpenModelsFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_engine.ModelsDir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_engine.ModelsDir}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SaveHint.Text = "打开文件夹失败：" + ex.Message;
        }
    }

    private async void OnUseModelClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not ModelRow row) return;
        if (string.IsNullOrEmpty(row.FilePath)) return;

        _config.SelectedModel = row.DisplayName;
        _config.Engine = "local";
        LocalRadio.IsChecked = true;

        SaveHint.Text = $"正在加载 {row.DisplayName}（首次加载需要几秒）…";
        btn.IsEnabled = false;

        try
        {
            var layers = _config.UseGpu ? Math.Max(1, _config.GpuLayers) : 0;
            var (ok, msg) = await _engine.LoadAsync(row.FilePath, layers, _config.ContextSize);
            SaveHint.Text = ok
                ? $"✓ {row.DisplayName} 已加载，可以直接划词了"
                : "加载失败：" + msg;
        }
        catch (Exception ex)
        {
            SaveHint.Text = "加载出错：" + ex.Message;
        }
        finally
        {
            btn.IsEnabled = true;
            RefreshModels();
        }
    }

    private void OnEngineChanged(object sender, RoutedEventArgs e) { }

    private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OpacityLabel != null)
            OpacityLabel.Text = $"{e.NewValue * 100:F0}%";
    }

    private void OnUnloadPolicyChanged(object sender, RoutedEventArgs e)
    {
        if (UnloadMinutesBox != null)
            UnloadMinutesBox.IsEnabled = UnloadIdleRadio.IsChecked == true;
    }

    private void OnHotkeyKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        e.Handled = true;

        if (sender is not TextBox box) return;

        var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
        if (key is System.Windows.Input.Key.LeftCtrl or System.Windows.Input.Key.RightCtrl
            or System.Windows.Input.Key.LeftAlt or System.Windows.Input.Key.RightAlt
            or System.Windows.Input.Key.LeftShift or System.Windows.Input.Key.RightShift
            or System.Windows.Input.Key.LWin or System.Windows.Input.Key.RWin
            or System.Windows.Input.Key.None)
            return;

        var parts = new List<string>();
        var mods = System.Windows.Input.Keyboard.Modifiers;
        if (mods.HasFlag(System.Windows.Input.ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(System.Windows.Input.ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(System.Windows.Input.ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(System.Windows.Input.ModifierKeys.Windows)) parts.Add("Win");

        if (parts.Count == 0)
        {
            SaveHint.Text = "至少要带一个修饰键，例如 Ctrl、Alt";
            return;
        }

        parts.Add(key.ToString());
        box.Text = string.Join("+", parts);
        SaveHint.Text = "";
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        // 名称查重放在最前面，免得后面写了一半才发现冲突
        var nameInput = ProfileNameBox.Text?.Trim() ?? "";
        if (ProfileBox.SelectedItem is string currentName && !string.IsNullOrEmpty(nameInput))
        {
            var duplicated = _config.ApiProfiles
                .Any(p => !string.Equals(p.Name, currentName, StringComparison.Ordinal)
                          && string.Equals(p.Name, nameInput, StringComparison.Ordinal));
            if (duplicated)
            {
                SaveHint.Text = $"名称「{nameInput}」已经被另一套接口占用";
                SaveHint.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xC0, 0x39, 0x2B));
                return;
            }
        }

        var hotkey = HotkeyBox.Text?.Trim() ?? "";
        if (!HotkeyManager.TryParse(hotkey, out _, out _, out var err))
        {
            SaveHint.Text = "划词热键无效：" + err;
            return;
        }

        var ocrHotkey = OcrHotkeyBox.Text?.Trim() ?? "";
        if (!HotkeyManager.TryParse(ocrHotkey, out _, out _, out var ocrErr))
        {
            SaveHint.Text = "截图热键无效：" + ocrErr;
            return;
        }

        if (string.Equals(hotkey, ocrHotkey, StringComparison.OrdinalIgnoreCase))
        {
            SaveHint.Text = "两个热键不能相同";
            return;
        }

        _config.Hotkey = hotkey;
        _config.OcrHotkey = ocrHotkey;
        _config.Engine = ApiRadio.IsChecked == true ? "api" : "local";

        ApplyFormToConfig();

        // 把改动写回当前选中的那套配置
        if (ProfileBox.SelectedItem is string activeName)
        {
            var active = _config.ApiProfiles.FirstOrDefault(p => p.Name == activeName);
            if (active != null)
            {
                active.Type = _config.ApiType;
                active.BaseUrl = _config.ApiBaseUrl;
                active.ApiKey = _config.ApiKey;
                active.Model = _config.ApiModel;

                // 名称改了就当重命名处理
                var renamed = !string.IsNullOrEmpty(nameInput) &&
                              !string.Equals(active.Name, nameInput, StringComparison.Ordinal);
                if (renamed) active.Name = nameInput;

                _config.ActiveProfile = active.Name;

                if (renamed)
                {
                    _loadingProfiles = true;
                    RefreshProfileList(active.Name);
                    _loadingProfiles = false;
                }
            }
        }

        _config.TargetLanguage = LanguageBox.SelectedItem as string ?? "简体中文";
        _config.AutoDirection = AutoDirectionBox.IsChecked == true;
        _config.PopupOpacity = Math.Clamp(OpacitySlider.Value, 0.3, 1.0);
        _config.TrayIconStyle = IconStyleBox.SelectedIndex + 1;
        _config.PopupAutoClose = AutoClosePopupBox.IsChecked == true;
        _config.PopupAutoCloseSeconds = int.TryParse(AutoCloseSecondsBox.Text.Trim(), out var sec)
            ? Math.Clamp(sec, 0, 600)
            : 5;
        _config.PopupAutoClosePauseOnHover = AutoCloseHoverBox.IsChecked == true;
        _config.PopupTimedCloseEnabled = TimedCloseBox.IsChecked == true;
        _config.PopupTimedCloseSeconds = int.TryParse(TimedCloseSecondsBox.Text.Trim(), out var tsec)
            ? Math.Clamp(tsec, 1, 3600)
            : 30;
        _config.AutoStart = AutoStartBox.IsChecked == true;

        // 语音朗读
        _config.TtsEnabled = TtsEnabledBox.IsChecked == true;
        _config.ShowSettingsOnStartup = ShowSettingsOnStartupBox.IsChecked == true;
        _config.TtsRatePercent = (int)Math.Round(TtsRateSlider.Value);

        if (TtsVoiceZhBox.SelectedItem is string zhVoice) _config.TtsVoiceZh = zhVoice;
        if (TtsVoiceEnBox.SelectedItem is string enVoice) _config.TtsVoiceEn = enVoice;

        // 面板被隐藏时（只有一套引擎）不去动原有选择
        if (EngineVariantPanel.Visibility == Visibility.Visible)
            _config.EngineVariant = EngineVariantBox.SelectedIndex == 1 ? "cpu" : "gpu";

        var deviceChanged = _config.UseGpu != (GpuRadio.IsChecked == true);
        _config.UseGpu = DevicePanel.Visibility == Visibility.Visible
            ? GpuRadio.IsChecked == true
            : false;

        var gi = GrabModeBox.SelectedIndex;
        _config.GrabMode = gi >= 0 && gi < GrabModes.Length ? GrabModes[gi].Value : "auto";

        // 卸载策略
        if (KeepLoadedRadio.IsChecked == true)
        {
            _config.UnloadPolicy = "never";
        }
        else if (UnloadOnCloseRadio.IsChecked == true)
        {
            _config.UnloadPolicy = "onPopupClose";
        }
        else
        {
            _config.UnloadPolicy = "afterMinutes";
            var raw = UnloadMinutesBox.Text?.Trim() ?? "";
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var mins)
                || double.TryParse(raw, out mins))
            {
                // 0.5 分钟为单位，向上取整到最近的一档
                _config.UnloadMinutes = Math.Max(0.5, Math.Round(mins * 2, MidpointRounding.AwayFromZero) / 2);
            }
            else
            {
                _config.UnloadMinutes = 5;
            }
        }

        // 常用语言
        var selected = _langOptions.Where(o => o.IsSelected).Select(o => o.Name).ToList();
        if (selected.Count == 0) selected.Add(_config.TargetLanguage);
        _config.QuickLanguages = selected;

        _config.Save();

        // 浮窗的自动关闭参数是静态属性，创建浮窗时才读一次。
        // 保存设置后立刻同步，让已显示的浮窗也立即生效。
        QuickTrans.Views.PopupWindow.AutoCloseEnabled = _config.PopupAutoClose;
        QuickTrans.Views.PopupWindow.AutoCloseSeconds = _config.PopupAutoCloseSeconds;
        QuickTrans.Views.PopupWindow.AutoClosePauseOnHover = _config.PopupAutoClosePauseOnHover;
        QuickTrans.Views.PopupWindow.TimedCloseEnabled = _config.PopupTimedCloseEnabled;
        QuickTrans.Views.PopupWindow.TimedCloseSeconds = _config.PopupTimedCloseSeconds;

        // 换了推理设备就先把当前模型卸掉，下次翻译会用新设备重新加载
        if (deviceChanged && _engine.IsRunning) _engine.Unload();

        SetDirty(false);
        SaveHint.Text = "✓ 已保存，设置立即生效";
        SaveHint.Foreground = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x2E, 0x8B, 0x57));

        // 保存后把快照也刷新，之后点取消撤回的是这次保存之后的变化
        _snapshot = _config.Clone();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        // 有未保存的改动：先撤回，窗口留着；再点一次才关。
        if (_dirty)
        {
            UndoChanges();
            return;
        }

        DialogResult = false;
        Close();
    }

    /// <summary>把配置和界面一起回滚到打开设置时的状态。</summary>
    private void UndoChanges()
    {
        if (_snapshot == null)
        {
            DialogResult = false;
            Close();
            return;
        }

        _config.CopyFrom(_snapshot);
        _config.Save();

        // 重新填充控件，让界面显示回滚后的值
        _suppressDirty = true;
        try
        {
            LoadUi();
            RefreshModels();
        }
        finally
        {
            _suppressDirty = false;
        }

        SetDirty(false);
        SaveHint.Text = "已撤销未保存的修改";
        SaveHint.Foreground = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x8A, 0x8A, 0x93));
    }
}
