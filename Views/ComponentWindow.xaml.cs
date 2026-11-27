using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QuickTrans.Models;
using QuickTrans.Services;

namespace QuickTrans.Views;

/// <summary>
/// 模型下载与管理：列出推荐下载的离线翻译模型，自动识别 models 目录及自定义路径中已有的同系列模型，
/// 支持断点续传、SHA256/GGUF 完整性校验与删除。
/// </summary>
public partial class ComponentWindow : Window
{
    private readonly ComponentManager _manager;
    private readonly ComponentManifest _manifest;
    private readonly List<string> _extraModelPaths;
    private readonly ThemeManager.Palette _theme;
    private readonly Dictionary<string, ProgressBar> _bars = new();
    private readonly Dictionary<string, Button> _buttons = new();
    private readonly Dictionary<string, TextBlock> _states = new();

    public ComponentWindow(ComponentManager manager, IEnumerable<string>? extraModelPaths = null, string? themeSetting = null)
    {
        InitializeComponent();
        UiQualityHelper.AttachWindowQuality(this);
        UiQualityHelper.AttachSmoothScroll(ComponentScroll);
        _manager = manager;
        _manifest = DefaultManifest.Create();

        var cfg = AppConfig.Load();
        ThemeManager.ApplyFontScale(Resources, cfg.UiFontScale);
        UiQualityHelper.ApplyWindowIcon(this, cfg.TrayIconStyle);
        _extraModelPaths = (extraModelPaths ?? cfg.CustomModelPaths ?? new List<string>()).ToList();
        _theme = ThemeManager.Resolve(themeSetting ?? cfg.Theme);

        // 当前版本统一内置 Vulkan + CPU 通用引擎，无需再向用户展示引擎下载管理项
        _manifest.Components.RemoveAll(c => string.Equals(c.Kind, "engine", StringComparison.OrdinalIgnoreCase));

        ApplyTheme();
        BuildList();
    }

    /// <summary>
    /// 安装向导里勾选了要下载的模型时，由 App 带参数启动本窗口，显示后自动开始下载。
    /// </summary>
    public ComponentWindow(ComponentManager manager, string[] autoStartIds, IEnumerable<string>? extraModelPaths = null, string? themeSetting = null)
        : this(manager, extraModelPaths, themeSetting)
    {
        if (autoStartIds.Length == 0) return;

        Loaded += async (_, _) =>
        {
            foreach (var id in autoStartIds)
            {
                var item = _manifest.Components.FirstOrDefault(c =>
                    string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
                if (item == null || _manager.IsInstalled(item, _extraModelPaths)) continue;

                await ToggleAsync(item);
            }
        };
    }

    private void ApplyTheme()
    {
        Background = ThemeManager.Brush(_theme.WindowBg);
        UiQualityHelper.ApplyDwmTheme(this, _theme.IsDark, _theme.WindowBg);
        HeaderIconBadge.Background = ThemeManager.Brush(_theme.AccentSoftBg);
        HeaderIcon.SetValue(System.Windows.Documents.TextElement.ForegroundProperty, ThemeManager.Brush(_theme.Accent));
        TitleText.Foreground = ThemeManager.Brush(_theme.TextPrimary);
        HintText.Foreground = ThemeManager.Brush(_theme.TextSecondary);
        FooterBorder.Background = ThemeManager.Brush(_theme.WindowBg);
        StatusText.Foreground = ThemeManager.Brush(_theme.TextMuted);
        CloseBtn.Background = ThemeManager.Brush(_theme.PillBg);
        CloseBtn.Foreground = ThemeManager.Brush(_theme.TextPrimary);
    }

    private void BuildList()
    {
        ListPanel.Children.Clear();

        ListPanel.Children.Add(new TextBlock
        {
            Text = "推荐离线翻译模型",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeManager.Brush(_theme.TextSecondary),
            Margin = new Thickness(2, 4, 0, 8),
        });

        foreach (var item in _manifest.Components)
            ListPanel.Children.Add(BuildCard(item));

        RefreshStates();
    }

    private Border BuildCard(ComponentItem item)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel();
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
        nameRow.Children.Add(new TextBlock
        {
            Text = item.Name,
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeManager.Brush(_theme.TextPrimary),
            VerticalAlignment = VerticalAlignment.Center,
        });
        nameRow.Children.Add(new TextBlock
        {
            Text = item.SizeText,
            FontSize = 11,
            Foreground = ThemeManager.Brush(_theme.TextMuted),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        left.Children.Add(nameRow);

        left.Children.Add(new TextBlock
        {
            Text = item.Description,
            FontSize = 11.5,
            Foreground = ThemeManager.Brush(_theme.TextSecondary),
            Margin = new Thickness(0, 5, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        });

        var state = new TextBlock
        {
            FontSize = 11,
            Foreground = ThemeManager.Brush(_theme.TextMuted),
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        left.Children.Add(state);
        _states[item.Id] = state;

        var bar = new ProgressBar
        {
            Height = 3,
            Margin = new Thickness(0, 8, 0, 0),
            Minimum = 0,
            Maximum = 100,
            Visibility = Visibility.Collapsed,
            Background = ThemeManager.Brush(_theme.Border),
            Foreground = ThemeManager.Brush(_theme.Accent),
        };
        left.Children.Add(bar);
        _bars[item.Id] = bar;

        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        var button = new Button
        {
            Content = "下载",
            Style = (Style)FindResource("PillBtn"),
            Padding = new Thickness(16, 6, 16, 6),
            FontSize = 12,
            Background = ThemeManager.Brush(_theme.Accent),
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 0, 0),
        };
        button.Click += async (_, _) => await ToggleAsync(item);
        _buttons[item.Id] = button;

        Grid.SetColumn(button, 1);
        grid.Children.Add(button);

        return new Border
        {
            Background = ThemeManager.Brush(_theme.CardBg),
            BorderBrush = ThemeManager.Brush(_theme.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 0, 0, 10),
            Child = grid,
        };
    }

    private void RefreshStates()
    {
        foreach (var item in _manifest.Components)
        {
            var matched = _manager.FindInstalledFile(item, _extraModelPaths);
            var installed = matched != null;
            var insideModels = matched != null && _manager.IsInsideModelsDir(matched);

            if (_buttons.TryGetValue(item.Id, out var button))
            {
                if (installed && insideModels)
                {
                    button.Content = "删除";
                    button.IsEnabled = true;
                    button.Background = ThemeManager.Brush(_theme.PillBg);
                    button.Foreground = ThemeManager.Brush(_theme.TextPrimary);
                }
                else if (installed)
                {
                    button.Content = "已存在";
                    button.IsEnabled = false;
                    button.Background = ThemeManager.Brush(_theme.PillBg);
                    button.Foreground = ThemeManager.Brush(_theme.TextMuted);
                }
                else
                {
                    button.Content = "下载";
                    button.IsEnabled = true;
                    button.Background = ThemeManager.Brush(_theme.Accent);
                    button.Foreground = Brushes.White;
                }
            }

            if (_states.TryGetValue(item.Id, out var state))
            {
                if (!installed)
                {
                    state.Text = "未安装";
                    state.Foreground = ThemeManager.Brush(_theme.TextMuted);
                }
                else
                {
                    var fileName = Path.GetFileName(matched!);
                    state.Text = insideModels
                        ? (string.Equals(fileName, item.CheckFile, StringComparison.OrdinalIgnoreCase)
                            ? "✓ 已安装（models 文件夹）"
                            : $"✓ 已安装（已识别：{fileName}）")
                        : $"✓ 已添加（外部路径：{fileName}）";
                    state.Foreground = ThemeManager.Brush(_theme.Accent);
                }
            }
        }
    }

    private async Task ToggleAsync(ComponentItem item)
    {
        var matched = _manager.FindInstalledFile(item, _extraModelPaths);
        if (matched != null)
        {
            if (!_manager.IsInsideModelsDir(matched)) return;

            var fileName = Path.GetFileName(matched);
            var ok = MessageBox.Show(this,
                $"确定删除 models 目录中的「{fileName}」？\n\n删除后需要重新下载才能使用。",
                "确认删除", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (ok != MessageBoxResult.OK) return;

            try
            {
                _manager.Remove(item);
                StatusText.Text = $"已删除 {fileName}";
            }
            catch (Exception ex)
            {
                StatusText.Text = "删除失败：" + ex.Message;
            }
            RefreshStates();
            return;
        }

        if (!_buttons.TryGetValue(item.Id, out var button)) return;
        var bar = _bars[item.Id];
        var stateText = _states[item.Id];

        button.IsEnabled = false;
        button.Content = "下载中";
        bar.Visibility = Visibility.Visible;
        bar.Value = 0;

        var progress = new Progress<DownloadProgress>(p =>
        {
            bar.Value = p.Percent;
            stateText.Text = p.Total > 0
                ? $"{p.Phase}  {p.Received / 1024.0 / 1024:F0} / {p.Total / 1024.0 / 1024:F0} MB（{p.Percent:F0}%）"
                : p.Phase;
        });

        try
        {
            await _manager.DownloadAsync(item, progress);
            StatusText.Text = $"{item.Name} 安装完成";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "下载已取消，已下载的部分会保留，下次可继续";
        }
        catch (Exception ex)
        {
            StatusText.Text = "下载失败：" + ex.Message;
        }
        finally
        {
            button.IsEnabled = true;
            bar.Visibility = Visibility.Collapsed;
            RefreshStates();
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
