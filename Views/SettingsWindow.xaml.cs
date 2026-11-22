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
    public Visibility RemoveVisibility { get; init; } = Visibility.Collapsed;
    public Visibility UseVisibility { get; init; } = Visibility.Visible;
    public bool IsCustomPath { get; init; }
    public string FilePath { get; init; } = "";
}

public sealed class ApiProfileRow
{
    public string Name { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public Visibility ActiveVisibility { get; init; } = Visibility.Collapsed;
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
    private readonly bool _childMode;
    private string? _loadedModelPathOverride;
    private readonly ObservableCollection<ModelRow> _rows = new();
    private readonly ObservableCollection<ApiProfileRow> _profileRows = new();
    private readonly List<LangOption> _langOptions = new();
    private readonly List<string> _blacklist = new();
    private bool _gpuOk;
    private ThemeManager.Palette _theme = ThemeManager.Light;
    private int _activeTab;
    private string? _editingProfileName;
    private bool _creatingNewProfile;

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

    public SettingsWindow(AppConfig config, EngineManager engine, bool childMode = false, string? loadedModelPath = null)
    {
        InitializeComponent();

        _config = config;
        _engine = engine;
        _childMode = childMode;
        _loadedModelPathOverride = loadedModelPath;

        ModelList.ItemsSource = _rows;
        ApiProfileList.ItemsSource = _profileRows;

        LoadUi();
        RefreshModels();

        // 初始化完成后再挂监听，否则 LoadUi 赋值会被当成用户改动
        SelectTab(0);
        HookDirtyTracking();
        SetDirty(false);

        // 记录打开设置时的配置状态，点取消时用它还原
        _snapshot = _config.Clone();

        Dispatcher.BeginInvoke(new Action(() =>
        {
            AttachWheelHandlers(PageGeneral);

            // 初始化过程中的假改动一并清掉
            SetDirty(false);
        }), System.Windows.Threading.DispatcherPriority.Loaded);

        CheckEngineAsync();

        // 布局和数据绑定完成前会产生一些假改动事件（Slider 微调、列表自动选中），
        // 等界面彻底稳定后再把"有修改"状态清零。
        Loaded += (_, _) =>
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                SetDirty(false);
                SaveHint.Text = "";
            }), System.Windows.Threading.DispatcherPriority.ContextIdle);
        };
    }

    // ---------- 左侧导航分栏 ----------

    private readonly HashSet<UIElement> _wheelAttached = new();

    private void OnNavClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && int.TryParse(btn.Tag?.ToString(), out var idx))
            SelectTab(idx);
    }

    private void SelectTab(int index)
    {
        _activeTab = index;
        var pages = new FrameworkElement[] { PageGeneral, PageModel, PageApi, PagePopup, PageDnd, PageAbout };
        var navs = new Button[] { NavGeneral, NavModel, NavApi, NavPopup, NavDnd, NavAbout };

        var activeBg = ThemeManager.Brush(_theme.AccentSoftBg);
        var activeFg = ThemeManager.Brush(_theme.Accent);
        var idleFg = ThemeManager.Brush(_theme.TextSecondary);

        for (var i = 0; i < pages.Length; i++)
        {
            var on = i == index;
            pages[i].Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            navs[i].Background = on ? activeBg : System.Windows.Media.Brushes.Transparent;
            navs[i].Foreground = on ? activeFg : idleFg;
            navs[i].FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }

        if (index == 4) RefreshRunningApps();

        MainScroll?.ScrollToTop();
        if (index >= 0 && index < pages.Length)
        {
            var page = pages[index];
            Dispatcher.BeginInvoke(new Action(() => AttachWheelHandlers(page)),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    private string SelectedThemeValue() => ThemeBox.SelectedIndex switch
    {
        1 => "light",
        2 => "dark",
        _ => "system",
    };

    private void ApplyTheme(string? themeSetting)
    {
        _theme = ThemeManager.Resolve(themeSetting);
        Resources["BrushWindowBg"] = ThemeManager.Brush(_theme.WindowBg);
        Resources["BrushSidebarBg"] = ThemeManager.Brush(_theme.SidebarBg);
        Resources["BrushCardBg"] = ThemeManager.Brush(_theme.CardBg);
        Resources["BrushSubPanelBg"] = ThemeManager.Brush(_theme.SubPanelBg);
        Resources["BrushBorder"] = ThemeManager.Brush(_theme.Border);
        Resources["BrushTextPrimary"] = ThemeManager.Brush(_theme.TextPrimary);
        Resources["BrushTextSecondary"] = ThemeManager.Brush(_theme.TextSecondary);
        Resources["BrushTextMuted"] = ThemeManager.Brush(_theme.TextMuted);
        Resources["BrushAccent"] = ThemeManager.Brush(_theme.Accent);
        Resources["BrushAccentSoft"] = ThemeManager.Brush(_theme.AccentSoftBg);
        Resources["BrushPillBg"] = ThemeManager.Brush(_theme.PillBg);
        Resources["BrushInputBg"] = ThemeManager.Brush(_theme.InputBg);

        if (NavGeneral != null)
        {
            var navs = new Button[] { NavGeneral, NavModel, NavApi, NavPopup, NavDnd, NavAbout };
            var activeBg = ThemeManager.Brush(_theme.AccentSoftBg);
            var activeFg = ThemeManager.Brush(_theme.Accent);
            var idleFg = ThemeManager.Brush(_theme.TextSecondary);
            for (var i = 0; i < navs.Length; i++)
            {
                var on = i == _activeTab;
                navs[i].Background = on ? activeBg : System.Windows.Media.Brushes.Transparent;
                navs[i].Foreground = on ? activeFg : idleFg;
            }
        }
    }

    // ---------- 免打扰与黑名单 ----------

    private void RefreshRunningApps()
    {
        if (RunningAppsPanel == null) return;
        RunningAppsPanel.Children.Clear();

        var apps = DndService.GetRunningApps();
        if (apps.Count == 0)
        {
            RunningAppsPanel.Children.Add(new TextBlock
            {
                Text = "未检测到其他运行中的桌面窗口，可在下方手动输入进程名。",
                FontSize = 11.5,
                Foreground = (System.Windows.Media.Brush)FindResource("BrushTextMuted"),
            });
            return;
        }

        foreach (var app in apps)
        {
            var proc = app.ProcessName;
            var isBlocked = _blacklist.Any(x => string.Equals(x, proc, StringComparison.OrdinalIgnoreCase));
            var cb = new CheckBox
            {
                Content = app.DisplayLabel,
                IsChecked = isBlocked,
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 6),
                Tag = proc,
            };
            cb.Checked += (_, _) =>
            {
                AddBlacklistEntry(proc, refreshRunning: false);
            };
            cb.Unchecked += (_, _) =>
            {
                RemoveBlacklistEntry(proc, refreshRunning: false);
            };
            RunningAppsPanel.Children.Add(cb);
        }
    }

    private void RenderBlacklistTags()
    {
        if (BlacklistTagsPanel == null || BlacklistSummaryText == null) return;
        BlacklistTagsPanel.Children.Clear();

        BlacklistSummaryText.Text = _blacklist.Count == 0
            ? "已加入黑名单的程序（当前为空）"
            : $"已加入黑名单的程序（共 {_blacklist.Count} 个，点击可移除）";

        foreach (var proc in _blacklist)
        {
            var captured = proc;
            var btn = new Button
            {
                Content = $"{proc}.exe  ✕",
                Style = (Style)FindResource("FlatButton"),
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 6, 6),
                FontSize = 11.5,
                ToolTip = "从黑名单中移除 " + proc,
            };
            btn.Click += (_, _) => RemoveBlacklistEntry(captured, refreshRunning: true);
            BlacklistTagsPanel.Children.Add(btn);
        }
    }

    private void AddBlacklistEntry(string raw, bool refreshRunning)
    {
        var norm = DndService.NormalizeProcessName(raw);
        if (string.IsNullOrEmpty(norm)) return;
        if (string.Equals(norm, "quicktrans", StringComparison.OrdinalIgnoreCase))
        {
            SaveHint.Text = "不能把 QuickTrans 自身加入黑名单";
            return;
        }

        if (!_blacklist.Any(x => string.Equals(x, norm, StringComparison.OrdinalIgnoreCase)))
        {
            _blacklist.Add(norm);
            RenderBlacklistTags();
            if (refreshRunning) RefreshRunningApps();
            SetDirty(true);
        }
    }

    private void RemoveBlacklistEntry(string proc, bool refreshRunning)
    {
        var removed = _blacklist.RemoveAll(x => string.Equals(x, proc, StringComparison.OrdinalIgnoreCase));
        if (removed > 0)
        {
            RenderBlacklistTags();
            if (refreshRunning) RefreshRunningApps();
            SetDirty(true);
        }
    }

    private void OnRefreshRunningAppsClick(object sender, RoutedEventArgs e)
    {
        RefreshRunningApps();
        SaveHint.Text = "已刷新当前运行程序列表";
    }

    private void OnAddBlacklistClick(object sender, RoutedEventArgs e)
    {
        var raw = BlacklistInputBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(raw)) return;
        AddBlacklistEntry(raw, refreshRunning: true);
        BlacklistInputBox.Text = "";
    }

    private void OnBlacklistInputKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            e.Handled = true;
            OnAddBlacklistClick(sender, e);
        }
    }

    /// <summary>同进程回退模式下用于实时预览主题/透明度。</summary>
    public event Action<string, double>? ThemePreviewRequested;

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        var theme = SelectedThemeValue();
        ApplyTheme(theme);
        BroadcastThemePreview(theme, OpacitySlider?.Value ?? _config.PopupOpacity);
    }

    private void BroadcastThemePreview(string theme, double opacity)
    {
        if (_suppressDirty) return;
        var op = Math.Clamp(opacity, 0.3, 1.0);
        if (_childMode)
        {
            _ = AppIpcClient.SendAsync($"PREVIEW_THEME\t{theme}\t{op.ToString("0.00", CultureInfo.InvariantCulture)}");
        }
        else
        {
            ThemePreviewRequested?.Invoke(theme, op);
        }
    }

    private void AttachWheelHandlers(DependencyObject root)
    {
        foreach (var element in FindAllScrollWheelSensitive(root))
        {
            if (_wheelAttached.Add(element))
            {
                element.AddHandler(UIElement.PreviewMouseWheelEvent,
                    new System.Windows.Input.MouseWheelEventHandler(OnComboWheel), true);
            }
        }
    }

    /// <summary>按接口类型调整表单与指引。</summary>
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
            _ => "填写对应平台申请的 API Key（通常以 sk- 开头）",
        };

        ApiKeyHelpLink.Text = idx switch
        {
            1 => "前往百度翻译开放平台获取 APP ID 与密钥 →",
            2 => "前往有道智云控制台获取 appKey 与 appSecret →",
            _ => "前往获取 API Key（OpenAI 控制台）→",
        };

        ApiHint.Text = idx switch
        {
            1 => "百度翻译不需要 Base URL。在控制台查看 APP ID 与密钥，按「appid:密钥」格式填在上方。",
            2 => "有道智云不需要 Base URL。在控制台查看应用 ID 与密钥，按「appKey:appSecret」格式填在上方。",
            3 => "谷歌免费端点不需要密钥，直接命名保存即可；国内直连通常不通，需系统代理。",
            _ => "支持所有 OpenAI 兼容接口，常见 Base URL 与模型名参考：\n" +
                 "• DeepSeek：https://api.deepseek.com/v1  ｜  deepseek-chat\n" +
                 "• 智谱 AI：https://open.bigmodel.cn/api/paas/v4  ｜  glm-4-flash\n" +
                 "• 通义千问：https://dashscope.aliyuncs.com/compatible-mode/v1  ｜  qwen-turbo\n" +
                 "• OpenAI：https://api.openai.com/v1  ｜  gpt-4o-mini",
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

    /// <summary>根据当前下拉框选中的接口类型，打开对应的密钥申请页。</summary>
    private void OnApiKeyHelpClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var url = ApiTypeBox.SelectedIndex switch
        {
            1 => "https://fanyi-api.baidu.com/product/11",
            2 => "https://ai.youdao.com/console/#/service-singleton/text-translation",
            3 => "",
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

            if (_childMode)
            {
                // 子进程模式下由主进程发起替换并退出，避免主进程仍占着 QuickTrans.exe 导致替换失败
                var reply = await AppIpcClient.SendAsync("APPLY_UPDATE");
                if (string.Equals(reply, "OK", StringComparison.Ordinal))
                {
                    Close();
                }
                else
                {
                    UpdateHint.Text = "启动更新程序失败，请手动下载覆盖。";
                }
                return;
            }

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
                ? $"正在下载 {info.Version}：{p.Received / 1024.0 / 1024:F1} / {p.Total / 1024.0 / 1024:F1} MB"
                : $"正在下载 {info.Version}：{p.Received / 1024.0 / 1024:F1} MB";
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

    /// <summary>打开模型下载与管理窗口。</summary>
    private void OnOpenComponentsClick(object sender, RoutedEventArgs e)
    {
        var win = new ComponentWindow(
            new ComponentManager(AppContext.BaseDirectory),
            _config.CustomModelPaths,
            SelectedThemeValue())
        {
            Owner = this,
        };
        win.ShowDialog();
        RefreshModels();
    }

    /// <summary>重新打开首次使用引导教程。</summary>
    private void OnOpenWelcomeClick(object sender, RoutedEventArgs e)
    {
        var win = new WelcomeWindow(SelectedThemeValue())
        {
            Owner = this,
        };
        win.ShowDialog();
    }

    /// <summary>打开翻译历史记录窗口。</summary>
    private void OnOpenHistoryClick(object sender, RoutedEventArgs e)
    {
        if (_childMode)
        {
            _ = AppIpcClient.SendAsync("OPEN_HISTORY");
            return;
        }

        var win = new HistoryWindow(SelectedThemeValue())
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

    // ---------- 多套接口配置（可视化列表 + 草稿新建，不保存不落盘） ----------

    private bool _loadingProfiles;

    private static string FormatApiTypeLabel(string? type) => type?.ToLowerInvariant() switch
    {
        "baidu" => "百度翻译",
        "youdao" => "有道智云",
        "google" => "谷歌翻译",
        _ => "OpenAI 兼容",
    };

    private static string BuildProfileSubtitle(ApiProfile p)
    {
        var label = FormatApiTypeLabel(p.Type);
        if (string.Equals(p.Type, "openai", StringComparison.OrdinalIgnoreCase))
        {
            var extra = !string.IsNullOrWhiteSpace(p.Model) ? p.Model : p.BaseUrl;
            return string.IsNullOrWhiteSpace(extra) ? label : $"{label} · {extra}";
        }
        return label;
    }

    private void RefreshProfileList(string? selectName = null)
    {
        _loadingProfiles = true;
        _profileRows.Clear();

        foreach (var p in _config.ApiProfiles)
        {
            var isActive = string.Equals(p.Name, _config.ActiveProfile, StringComparison.Ordinal);
            _profileRows.Add(new ApiProfileRow
            {
                Name = p.Name,
                Subtitle = BuildProfileSubtitle(p),
                ActiveVisibility = isActive ? Visibility.Visible : Visibility.Collapsed,
            });
        }

        ApiProfileEmptyHint.Visibility = _profileRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var target = selectName ?? (_creatingNewProfile ? null : _editingProfileName ?? _config.ActiveProfile);
        var match = !string.IsNullOrEmpty(target)
            ? _profileRows.FirstOrDefault(r => string.Equals(r.Name, target, StringComparison.Ordinal))
            : null;
        ApiProfileList.SelectedItem = match;

        if (!_creatingNewProfile)
        {
            if (match != null)
            {
                _editingProfileName = match.Name;
                ApiEditorTitle.Text = $"编辑接口：{match.Name}";
            }
            else if (_config.ApiProfiles.Count == 0)
            {
                _editingProfileName = null;
                ApiEditorTitle.Text = "新建接口（填好参数后点右下角「保存」加入列表）";
            }
            else
            {
                _editingProfileName = null;
                ApiEditorTitle.Text = "接口配置";
            }
        }

        _loadingProfiles = false;
    }

    private void LoadProfileIntoForm(ApiProfile p)
    {
        _loadingProfiles = true;
        _suppressDirty = true;
        try
        {
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
            ApplyApiTypeLayout();
        }
        finally
        {
            _suppressDirty = false;
            _loadingProfiles = false;
        }
    }

    private string CurrentFormApiType() => ApiTypeBox.SelectedIndex switch
    {
        1 => "baidu",
        2 => "youdao",
        3 => "google",
        _ => "openai",
    };

    /// <summary>把表单内容写回配置字段（不含 profiles 列表本身）。</summary>
    private void ApplyFormToConfig()
    {
        _config.ApiType = CurrentFormApiType();
        _config.ApiBaseUrl = ApiUrlBox.Text.Trim();
        _config.ApiKey = ApiKeyBox.Text.Trim();
        _config.ApiModel = ApiModelBox.Text.Trim();
    }

    private void OnProfileListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingProfiles || _suppressDirty) return;
        if (ApiProfileList.SelectedItem is not ApiProfileRow row) return;

        var p = _config.ApiProfiles.FirstOrDefault(x => string.Equals(x.Name, row.Name, StringComparison.Ordinal));
        if (p == null) return;

        // 选中已保存接口：放弃未保存的新建草稿，加载该接口到编辑区
        _creatingNewProfile = false;
        _editingProfileName = p.Name;
        ApiEditorTitle.Text = $"编辑接口：{p.Name}";
        LoadProfileIntoForm(p);
    }

    private void OnUseProfileRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not ApiProfileRow row) return;
        var p = _config.ApiProfiles.FirstOrDefault(x => string.Equals(x.Name, row.Name, StringComparison.Ordinal));
        if (p == null) return;

        _creatingNewProfile = false;
        _editingProfileName = p.Name;
        LoadProfileIntoForm(p);

        _config.Engine = "api";
        _config.ActiveProfile = p.Name;
        _config.ApiType = p.Type;
        _config.ApiBaseUrl = p.BaseUrl;
        _config.ApiKey = p.ApiKey;
        _config.ApiModel = p.Model;
        _suppressDirty = true;
        try { ApiRadio.IsChecked = true; }
        finally { _suppressDirty = false; }

        _config.Save();
        NotifyMainReload();
        RefreshProfileList(p.Name);

        SetDirty(false);
        SaveHint.Text = $"✓ 已切换到在线接口「{p.Name}」";
        SaveHint.Foreground = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x2E, 0x8B, 0x57));
        _snapshot = _config.Clone();
    }

    private void OnDeleteProfileRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not ApiProfileRow row) return;
        var p = _config.ApiProfiles.FirstOrDefault(x => string.Equals(x.Name, row.Name, StringComparison.Ordinal));
        if (p == null) return;

        _config.ApiProfiles.Remove(p);
        if (string.Equals(_config.ActiveProfile, p.Name, StringComparison.Ordinal))
            _config.ActiveProfile = _config.ApiProfiles.FirstOrDefault()?.Name ?? "";

        // 删掉最后一套接口后，顶层 ApiType/ApiKey 仍留着已删除的地址和密钥，
        // 界面显示空白、翻译却还在用旧配置。这里一并清干净。
        if (string.IsNullOrWhiteSpace(_config.ActiveProfile))
        {
            _config.ApiType = "openai";
            _config.ApiBaseUrl = "";
            _config.ApiKey = "";
            _config.ApiModel = "";
        }
        if (string.Equals(_editingProfileName, p.Name, StringComparison.Ordinal))
            _editingProfileName = null;

        _config.Save();
        NotifyMainReload();

        RefreshProfileList();
        if (_config.ApiProfiles.Count > 0)
        {
            var next = _config.ApiProfiles.FirstOrDefault(x => x.Name == _config.ActiveProfile) ?? _config.ApiProfiles[0];
            _editingProfileName = next.Name;
            LoadProfileIntoForm(next);
            RefreshProfileList(next.Name);
        }
        else
        {
            ClearApiEditorForm("");
        }

        SetDirty(false);
        SaveHint.Text = $"已删除「{row.Name}」";
        _snapshot = _config.Clone();
    }

    private void ClearApiEditorForm(string suggestedName)
    {
        _loadingProfiles = true;
        _suppressDirty = true;
        try
        {
            ProfileNameBox.Text = suggestedName;
            ApiTypeBox.SelectedIndex = 0;
            ApiUrlBox.Text = "";
            ApiKeyBox.Text = "";
            ApiModelBox.Text = "";
            ApplyApiTypeLayout();
        }
        finally
        {
            _suppressDirty = false;
            _loadingProfiles = false;
        }
    }

    /// <summary>
    /// 进入新建接口草稿状态：绝不提前写入配置列表或落盘，只有用户填完并点「保存」后才真正加入列表。
    /// </summary>
    private void OnNewProfileClick(object sender, RoutedEventArgs e)
    {
        var n = 1;
        while (_config.ApiProfiles.Any(p => p.Name == $"接口 {n}")) n++;

        _creatingNewProfile = true;
        _editingProfileName = null;

        _loadingProfiles = true;
        ApiProfileList.SelectedItem = null;
        _loadingProfiles = false;

        ClearApiEditorForm($"接口 {n}");
        ApiEditorTitle.Text = "新建接口（填好参数后点右下角「保存」加入列表）";
        SaveHint.Text = "正在新建接口，未点「保存」不会添加到列表";
        SaveHint.Foreground = ThemeManager.Brush(_theme.TextMuted);
        ProfileNameBox.Focus();
        ProfileNameBox.SelectAll();
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
                if (ReferenceEquals(e.OriginalSource, BlacklistInputBox)) return;
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

        _creatingNewProfile = false;
        if (_config.ApiProfiles.Count > 0)
        {
            var initial = _config.ApiProfiles.FirstOrDefault(p => p.Name == _config.ActiveProfile)
                          ?? _config.ApiProfiles[0];
            _editingProfileName = initial.Name;
            LoadProfileIntoForm(initial);
            RefreshProfileList(initial.Name);
        }
        else
        {
            _editingProfileName = null;
            RefreshProfileList();
            ApplyApiTypeLayout();
        }

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
        VersionText.Text = UpdateService.IsLiteBuild
            ? $"{UpdateService.CurrentVersion}（轻量版）"
            : UpdateService.CurrentVersion;
        ShowSettingsOnStartupBox.IsChecked = _config.ShowSettingsOnStartup;

        if (UpdateService.HasPendingUpdate())
        {
            _updateDownloaded = true;
            CheckUpdateButton.Content = "重启并更新";
            UpdateHint.Text = "新版本已下载完成，点「重启并更新」生效。";
        }
        else
        {
            _updateDownloaded = false;
            CheckUpdateButton.Content = "检查更新";
            UpdateHint.Text = "";
        }

        HotkeyBox.Text = _config.Hotkey;
        OcrHotkeyBox.Text = _config.OcrHotkey;
        ReplaceHotkeyBox.Text = string.IsNullOrWhiteSpace(_config.ReplaceHotkey) ? "Alt+R" : _config.ReplaceHotkey;
        DoubleCtrlCBox.IsChecked = _config.DoubleCtrlCEnabled;
        SmartJoinLinesBox.IsChecked = _config.SmartJoinLines;
        WordDictBox.IsChecked = _config.WordDictEnabled;
        HistoryEnabledBox.IsChecked = _config.HistoryEnabled;

        ThemeBox.SelectedIndex = string.Equals(_config.Theme, "light", StringComparison.OrdinalIgnoreCase) ? 1
            : string.Equals(_config.Theme, "dark", StringComparison.OrdinalIgnoreCase) ? 2
            : 0;
        ApplyTheme(_config.Theme);

        LanguageBox.ItemsSource = Languages;
        LanguageBox.SelectedItem = Languages.Contains(_config.TargetLanguage) ? _config.TargetLanguage : Languages[0];

        GrabModeBox.ItemsSource = GrabModes.Select(g => g.Label).ToList();
        var idx = Array.FindIndex(GrabModes, g => g.Value == _config.GrabMode);
        GrabModeBox.SelectedIndex = idx >= 0 ? idx : 0;

        AutoDirectionBox.IsChecked = _config.AutoDirection;

        GpuRadio.IsChecked = _config.UseGpu;
        CpuRadio.IsChecked = !_config.UseGpu;

        AutoStartBox.IsChecked = AutoStartService.IsEnabled();
        PrewarmModelBox.IsChecked = _config.PrewarmModel;
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
        PopupIsolateBox.IsChecked = _config.PopupIsolateProcess;
        ApplyAutoCloseState();

        // 免打扰与黑名单
        DndGameBox.IsChecked = _config.DndFullscreenGame;
        DndVideoBox.IsChecked = _config.DndFullscreenVideo;
        DndBlacklistEnabledBox.IsChecked = _config.DndBlacklistEnabled;
        _blacklist.Clear();
        if (_config.DndBlacklistProcesses != null)
        {
            foreach (var p in _config.DndBlacklistProcesses)
            {
                var norm = DndService.NormalizeProcessName(p);
                if (!string.IsNullOrEmpty(norm) && !_blacklist.Any(x => string.Equals(x, norm, StringComparison.OrdinalIgnoreCase)))
                    _blacklist.Add(norm);
            }
        }
        RenderBlacklistTags();
        RefreshRunningApps();

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

        // 每次都重新检测：引擎版本（gpu/cpu）切换后 GPU 能力会变（底层按引擎目录和文件版本缓存）。
        EngineStatus.Text = "正在检测显卡…";
        var (gpuOk, backend, deviceName) = await Task.Run(() => EngineManager.DetectGpuInfo(_engine.EngineDir));
        _gpuOk = gpuOk;

        // 只有一套引擎时不提供切换；引擎没有可用 GPU 时不提供设备选择
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
            // 这是程序自己切回 CPU，不是用户改动：不要因此把设置页标成“有未保存的修改”。
            var prev = _suppressDirty;
            _suppressDirty = true;
            try { CpuRadio.IsChecked = true; }
            finally { _suppressDirty = prev; }
        }

        // 纯 CPU 精简版：引擎目录里本来就没有 GPU 后端，不是"检测不到显卡"
        if (!_gpuOk && !EngineManager.HasGpuBackend(_engine.EngineDir))
        {
            EngineStatus.Text = $"✓ 引擎就绪（CPU 精简版）· 模型目录：{_engine.ModelsDir}";
            EngineStatus.Foreground = System.Windows.Media.Brushes.SeaGreen;
            return;
        }

        if (_gpuOk)
        {
            var gpuLabel = string.IsNullOrWhiteSpace(deviceName)
                ? $"{backend} GPU 加速"
                : $"{backend} GPU：{deviceName}";
            EngineStatus.Text = $"✓ 引擎就绪（{gpuLabel}）· 模型目录：{_engine.ModelsDir}";
            EngineStatus.Foreground = System.Windows.Media.Brushes.SeaGreen;
        }
        else
        {
            EngineStatus.Text = $"⚠ 引擎就绪（{backend}），但未检测到可用 GPU，将使用 CPU 推理 · 模型目录：{_engine.ModelsDir}";
            EngineStatus.Foreground = System.Windows.Media.Brushes.DarkOrange;
        }
    }

    private void RefreshModels(string? highlight = null)
    {
        var current = highlight ?? _config.SelectedModel;
        var models = _engine.ScanModels(_config.CustomModelPaths);
        var loadedPath = _childMode ? _loadedModelPathOverride : _engine.LoadedModelPath;

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

            var loc = m.IsCustomPath
                ? $" · 外部：{Path.GetDirectoryName(m.FilePath)}"
                : "";

            _rows.Add(new ModelRow
            {
                DisplayName = m.DisplayName,
                Subtitle = $"{m.SizeText} · {m.Quant}{loc}{note}",
                Badge = badge,
                BadgeVisibility = string.IsNullOrEmpty(badge) ? Visibility.Collapsed : Visibility.Visible,
                RemoveVisibility = m.IsCustomPath ? Visibility.Visible : Visibility.Collapsed,
                UseVisibility = Visibility.Visible,
                IsCustomPath = m.IsCustomPath,
                FilePath = m.FilePath,
            });
        }

        if (_rows.Count == 0)
        {
            _rows.Add(new ModelRow
            {
                DisplayName = "还没有模型",
                Subtitle = "点上方「下载模型…」一键下载，或「添加本地模型…」选择电脑任意位置的 .gguf 文件",
                UseVisibility = Visibility.Collapsed,
            });
        }
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        RefreshModels();
        SaveHint.Text = $"扫描完成，共 {_engine.ScanModels(_config.CustomModelPaths).Count} 个模型";
    }

    /// <summary>手动选择电脑内任意位置的 .gguf 模型文件，加入自定义模型列表（不复制、不移动原文件）。</summary>
    private void OnAddCustomModelClick(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择本地 GGUF 模型文件",
            Filter = "GGUF 模型文件 (*.gguf)|*.gguf|所有文件 (*.*)|*.*",
            Multiselect = true,
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) != true || dlg.FileNames.Length == 0) return;

        var added = 0;
        string? lastErr = null;
        var modelsDirFull = Path.GetFullPath(_engine.ModelsDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        foreach (var raw in dlg.FileNames)
        {
            var full = Path.GetFullPath(raw);
            var check = EngineManager.ValidateGgufHeader(full);
            if (!check.Ok)
            {
                lastErr = $"{Path.GetFileName(full)}：{check.Reason}";
                continue;
            }

            // 已经在默认 models 目录下的无需重复添加
            if (full.StartsWith(modelsDirFull, StringComparison.OrdinalIgnoreCase))
            {
                added++;
                continue;
            }

            if (!_config.CustomModelPaths.Any(p => string.Equals(Path.GetFullPath(p), full, StringComparison.OrdinalIgnoreCase)))
            {
                _config.CustomModelPaths.Add(full);
                added++;
            }
        }

        if (added > 0)
        {
            AppConfig.Update(c => c.CustomModelPaths = new List<string>(_config.CustomModelPaths));
            NotifyMainReload();
            RefreshModels();
            _snapshot = _config.Clone();
            SaveHint.Text = lastErr == null
                ? $"✓ 已添加 {added} 个本地模型，可直接点「使用」加载"
                : $"已添加 {added} 个模型（部分无效：{lastErr}）";
            SaveHint.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x2E, 0x8B, 0x57));
        }
        else if (lastErr != null)
        {
            SaveHint.Text = "添加失败：" + lastErr;
            SaveHint.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xC0, 0x39, 0x2B));
        }
        else
        {
            SaveHint.Text = "所选模型已在列表中";
        }
    }

    /// <summary>按需扫描常见模型目录（LM Studio / GPT4All / 下载文件夹 / 缓存），零常驻占用。</summary>
    private async void OnAutoScanModelsClick(object sender, RoutedEventArgs e)
    {
        AutoScanModelsButton.IsEnabled = false;
        SaveHint.Text = "正在扫描常见模型目录…";
        try
        {
            var modelsDir = _engine.ModelsDir;
            var existing = _config.CustomModelPaths.ToList();
            var found = await Task.Run(() => EngineManager.DiscoverCommonModels(modelsDir, existing));

            if (found.Count == 0)
            {
                SaveHint.Text = "常见目录中未发现新的 .gguf 模型（可点「添加模型…」手动选择）";
                SaveHint.Foreground = ThemeManager.Brush(_theme.TextMuted);
                return;
            }

            foreach (var p in found)
            {
                if (!_config.CustomModelPaths.Any(x => string.Equals(Path.GetFullPath(x), Path.GetFullPath(p), StringComparison.OrdinalIgnoreCase)))
                    _config.CustomModelPaths.Add(p);
            }

            AppConfig.Update(c => c.CustomModelPaths = new List<string>(_config.CustomModelPaths));
            NotifyMainReload();
            RefreshModels();
            _snapshot = _config.Clone();

            SaveHint.Text = $"✓ 自动扫描新增 {found.Count} 个本地模型";
            SaveHint.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x2E, 0x8B, 0x57));
        }
        catch (Exception ex)
        {
            SaveHint.Text = "扫描出错：" + ex.Message;
        }
        finally
        {
            AutoScanModelsButton.IsEnabled = true;
        }
    }

    /// <summary>仅从自定义列表中移除外部模型记录，不删除硬盘上的模型文件。</summary>
    private void OnRemoveCustomModelClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not ModelRow row || !row.IsCustomPath) return;

        var removed = _config.CustomModelPaths.RemoveAll(p =>
            string.Equals(Path.GetFullPath(p), Path.GetFullPath(row.FilePath), StringComparison.OrdinalIgnoreCase));
        if (removed > 0)
        {
            AppConfig.Update(c => c.CustomModelPaths = new List<string>(_config.CustomModelPaths));
            NotifyMainReload();
            RefreshModels();
            _snapshot = _config.Clone();
            SaveHint.Text = $"已从列表移除「{row.DisplayName}」（原文件未删除）";
        }
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
            if (_childMode)
            {
                // 通知主进程加载模型，由主进程持有 llama-server（关掉设置页后模型继续可用）。
                // 只回写模型相关字段，避免把别处刚保存的设置整份覆盖。
                AppConfig.Update(c =>
                {
                    c.SelectedModel = row.DisplayName;
                    c.Engine = "local";
                });
                var reply = await AppIpcClient.SendAsync("LOAD_MODEL\t" + row.FilePath, connectTimeoutMs: 2000);
                if (reply != null && reply.StartsWith("OK\t", StringComparison.Ordinal))
                {
                    _loadedModelPathOverride = row.FilePath;
                    SaveHint.Text = $"✓ {row.DisplayName} 已加载，可以直接划词了";
                }
                else
                {
                    var err = reply != null && reply.StartsWith("ERR\t", StringComparison.Ordinal)
                        ? reply[4..]
                        : "无法连接到主程序，请重试";
                    SaveHint.Text = "加载失败：" + err;
                }
            }
            else
            {
                var layers = _config.UseGpu ? Math.Max(1, _config.GpuLayers) : 0;
                var (ok, msg) = await _engine.LoadAsync(row.FilePath, layers, _config.ContextSize);
                SaveHint.Text = ok
                    ? $"✓ {row.DisplayName} 已加载，可以直接划词了"
                    : "加载失败：" + msg;
            }
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
        if (IsLoaded && !_suppressDirty)
            BroadcastThemePreview(SelectedThemeValue(), e.NewValue);
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
        // 判断当前接口表单是在编辑已有配置，还是提交新建草稿
        var nameInput = ProfileNameBox.Text?.Trim() ?? "";
        var formType = CurrentFormApiType();
        var formUrl = ApiUrlBox.Text.Trim();
        var formKey = ApiKeyBox.Text.Trim();
        var formModel = ApiModelBox.Text.Trim();
        var formHasContent = formType == "google"
            ? (!string.IsNullOrWhiteSpace(nameInput) && (_creatingNewProfile || _activeTab == 2))
            : (!string.IsNullOrWhiteSpace(formUrl) || !string.IsNullOrWhiteSpace(formKey));

        var shouldCreateProfile = (_creatingNewProfile || _editingProfileName == null) && formHasContent;

        // 名称查重放在最前面，免得后面写了一半才发现冲突
        if (shouldCreateProfile)
        {
            if (string.IsNullOrWhiteSpace(nameInput))
            {
                var n = 1;
                while (_config.ApiProfiles.Any(p => p.Name == $"接口 {n}")) n++;
                nameInput = $"接口 {n}";
            }
            var duplicated = _config.ApiProfiles
                .Any(p => string.Equals(p.Name, nameInput, StringComparison.Ordinal));
            if (duplicated)
            {
                SaveHint.Text = $"名称「{nameInput}」已经被另一套接口占用，请换一个名称";
                SaveHint.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xC0, 0x39, 0x2B));
                return;
            }
        }
        else if (_editingProfileName != null && !string.IsNullOrEmpty(nameInput))
        {
            var duplicated = _config.ApiProfiles
                .Any(p => !string.Equals(p.Name, _editingProfileName, StringComparison.Ordinal)
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

        var replaceHotkey = ReplaceHotkeyBox.Text?.Trim() ?? "";
        if (!HotkeyManager.TryParse(replaceHotkey, out _, out _, out var repErr))
        {
            SaveHint.Text = "替换翻译热键无效：" + repErr;
            return;
        }

        if (string.Equals(hotkey, ocrHotkey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(hotkey, replaceHotkey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(ocrHotkey, replaceHotkey, StringComparison.OrdinalIgnoreCase))
        {
            SaveHint.Text = "三个快捷键不能重复";
            return;
        }

        _config.Hotkey = hotkey;
        _config.OcrHotkey = ocrHotkey;
        _config.ReplaceHotkey = replaceHotkey;
        _config.DoubleCtrlCEnabled = DoubleCtrlCBox.IsChecked == true;
        _config.SmartJoinLines = SmartJoinLinesBox.IsChecked == true;
        _config.WordDictEnabled = WordDictBox.IsChecked == true;
        _config.HistoryEnabled = HistoryEnabledBox.IsChecked == true;
        _config.Theme = SelectedThemeValue();
        _config.Engine = ApiRadio.IsChecked == true ? "api" : "local";

        if (shouldCreateProfile)
        {
            var created = new ApiProfile
            {
                Name = nameInput,
                Type = formType,
                BaseUrl = formUrl,
                ApiKey = formKey,
                Model = formModel,
            };
            _config.ApiProfiles.Add(created);
            _config.ActiveProfile = created.Name;
            ApplyFormToConfig();
            _creatingNewProfile = false;
            _editingProfileName = created.Name;
            RefreshProfileList(created.Name);
        }
        else if (_editingProfileName != null)
        {
            var active = _config.ApiProfiles.FirstOrDefault(p => p.Name == _editingProfileName);
            if (active != null)
            {
                active.Type = formType;
                active.BaseUrl = formUrl;
                active.ApiKey = formKey;
                active.Model = formModel;

                var renamed = !string.IsNullOrEmpty(nameInput)
                              && !string.Equals(active.Name, nameInput, StringComparison.Ordinal);
                if (renamed) active.Name = nameInput;

                if (string.Equals(_config.ActiveProfile, _editingProfileName, StringComparison.Ordinal)
                    || string.IsNullOrWhiteSpace(_config.ActiveProfile))
                {
                    _config.ActiveProfile = active.Name;
                    ApplyFormToConfig();
                }

                _editingProfileName = active.Name;
                RefreshProfileList(active.Name);
            }
        }
        else if (_creatingNewProfile)
        {
            // 点了新建但没填任何内容：自动放弃空白草稿，不污染已保存接口列表
            _creatingNewProfile = false;
            RefreshProfileList();
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
        _config.PopupIsolateProcess = PopupIsolateBox.IsChecked == true;
        _config.AutoStart = AutoStartBox.IsChecked == true;
        _config.PrewarmModel = PrewarmModelBox.IsChecked == true;

        // 语音朗读
        _config.TtsEnabled = TtsEnabledBox.IsChecked == true;
        _config.ShowSettingsOnStartup = ShowSettingsOnStartupBox.IsChecked == true;
        _config.TtsRatePercent = (int)Math.Round(TtsRateSlider.Value);

        if (TtsVoiceZhBox.SelectedItem is string zhVoice) _config.TtsVoiceZh = zhVoice;
        if (TtsVoiceEnBox.SelectedItem is string enVoice) _config.TtsVoiceEn = enVoice;

        // 面板被隐藏时（只有一套引擎）不去动原有选择
        if (EngineVariantPanel.Visibility == Visibility.Visible)
            _config.EngineVariant = EngineVariantBox.SelectedIndex == 1 ? "cpu" : "gpu";

        var oldUseGpu = _config.UseGpu;
        if (DevicePanel.Visibility == Visibility.Visible)
        {
            _config.UseGpu = GpuRadio.IsChecked == true;
        }
        else if (!EngineManager.HasGpuBackend(_engine.EngineDir))
        {
            // 纯 CPU 引擎：确实没有 GPU 可用
            _config.UseGpu = false;
        }
        // 其它情况（引擎带 GPU 后端、只是本次探测失败/超时）保留原值，
        // 避免一次探测失败就把 GPU 永久关掉。

        var deviceChanged = _config.UseGpu != oldUseGpu;

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

        // 免打扰与黑名单
        _config.DndFullscreenGame = DndGameBox.IsChecked == true;
        _config.DndFullscreenVideo = DndVideoBox.IsChecked == true;
        _config.DndBlacklistEnabled = DndBlacklistEnabledBox.IsChecked == true;
        _config.DndBlacklistProcesses = _blacklist.ToList();

        // 常用语言
        var selected = _langOptions.Where(o => o.IsSelected).Select(o => o.Name).ToList();
        if (selected.Count == 0) selected.Add(_config.TargetLanguage);
        _config.QuickLanguages = selected;

        _config.Save();

        if (_childMode)
        {
            // 通知主进程立刻重载 config.json（同步热键、托盘图标、浮窗参数、推理设备等）
            NotifyMainReload();
        }
        else
        {
            // 浮窗的自动关闭参数是静态属性，创建浮窗时才读一次。
            // 保存设置后立刻同步，让已显示的浮窗也立即生效。
            QuickTrans.Views.PopupWindow.AutoCloseEnabled = _config.PopupAutoClose;
            QuickTrans.Views.PopupWindow.AutoCloseSeconds = _config.PopupAutoCloseSeconds;
            QuickTrans.Views.PopupWindow.AutoClosePauseOnHover = _config.PopupAutoClosePauseOnHover;
            QuickTrans.Views.PopupWindow.TimedCloseEnabled = _config.PopupTimedCloseEnabled;
            QuickTrans.Views.PopupWindow.TimedCloseSeconds = _config.PopupTimedCloseSeconds;

            // 换了推理设备就先把当前模型卸掉，下次翻译会用新设备重新加载
            if (deviceChanged && _engine.IsRunning) _engine.Unload();
        }

        SetDirty(false);
        SaveHint.Text = "✓ 已保存，设置立即生效";
        SaveHint.Foreground = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x2E, 0x8B, 0x57));

        // 保存后把快照也刷新，之后点取消撤回的是这次保存之后的变化
        _snapshot = _config.Clone();
    }

    private void NotifyMainReload()
    {
        if (_childMode) _ = AppIpcClient.SendAsync("RELOAD_CONFIG");
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        // 有未保存的改动：先撤回，窗口留着；再点一次才关。
        if (_dirty)
        {
            UndoChanges();
            return;
        }

        try { DialogResult = false; } catch { /* 非模态 Show() 打开时不能设 DialogResult */ }
        Close();
    }

    /// <summary>把配置和界面一起回滚到打开设置时的状态。</summary>
    private void UndoChanges()
    {
        if (_snapshot == null)
        {
            try { DialogResult = false; } catch { }
            Close();
            return;
        }

        _config.CopyFrom(_snapshot);
        _config.Save();
        NotifyMainReload();

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
