using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QuickTrans.Models;
using QuickTrans.Services;

namespace QuickTrans.Views;

/// <summary>
/// 组件管理：列出可下载的 GPU 引擎和模型，支持下载、删除、查看进度。
/// 首次启动发现缺组件时也会自动打开。
/// </summary>
public partial class ComponentWindow : Window
{
    private readonly ComponentManager _manager;
    private readonly ComponentManifest _manifest;
    private readonly Dictionary<string, ProgressBar> _bars = new();
    private readonly Dictionary<string, Button> _buttons = new();
    private readonly Dictionary<string, TextBlock> _states = new();

    public ComponentWindow(ComponentManager manager)
    {
        InitializeComponent();
        _manager = manager;
        _manifest = DefaultManifest.Create();
        BuildList();
    }

    /// <summary>
    /// 安装向导里勾选了「下载 GPU 引擎 / 模型」时，由 App 带参数启动本窗口，
    /// 构造时传入组件 id，窗口显示后自动开始下载。
    /// </summary>
    public ComponentWindow(ComponentManager manager, string[] autoStartIds) : this(manager)
    {
        if (autoStartIds.Length == 0) return;

        Loaded += async (_, _) =>
        {
            foreach (var id in autoStartIds)
            {
                var item = _manifest.Components.FirstOrDefault(c =>
                    string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
                if (item == null || _manager.IsInstalled(item)) continue;

                await ToggleAsync(item);
            }
        };
    }

    private void BuildList()
    {
        ListPanel.Children.Clear();

        foreach (var group in _manifest.Components.GroupBy(c => c.Kind))
        {
            var title = group.Key == "engine" ? "推理引擎" : "翻译模型";
            ListPanel.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x5A, 0x5A, 0x64)),
                Margin = new Thickness(2, 6, 0, 8),
            });

            foreach (var item in group)
                ListPanel.Children.Add(BuildCard(item));

            ListPanel.Children.Add(new Border { Height = 10 });
        }

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
            Foreground = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1E)),
            VerticalAlignment = VerticalAlignment.Center,
        });
        nameRow.Children.Add(new TextBlock
        {
            Text = item.SizeText,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x93)),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        left.Children.Add(nameRow);

        left.Children.Add(new TextBlock
        {
            Text = item.Description,
            FontSize = 11.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x6B, 0x75)),
            Margin = new Thickness(0, 5, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        });

        var state = new TextBlock
        {
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x93)),
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
            Background = new SolidColorBrush(Color.FromRgb(0xE4, 0xE4, 0xEA)),
            Foreground = new SolidColorBrush(Color.FromRgb(0x3B, 0x5B, 0xA8)),
        };
        left.Children.Add(bar);
        _bars[item.Id] = bar;

        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        var button = new Button
        {
            Content = "下载",
            Padding = new Thickness(16, 5, 16, 5),
            FontSize = 12,
            Cursor = System.Windows.Input.Cursors.Hand,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Color.FromRgb(0x3B, 0x5B, 0xA8)),
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
            Background = Brushes.White,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 0, 0, 8),
            Child = grid,
        };
    }

    private void RefreshStates()
    {
        foreach (var item in _manifest.Components)
        {
            var installed = _manager.IsInstalled(item);
            if (_buttons.TryGetValue(item.Id, out var button))
            {
                button.Content = installed ? "删除" : "下载";
                button.Background = new SolidColorBrush(installed
                    ? Color.FromRgb(0xF2, 0xF2, 0xF5)
                    : Color.FromRgb(0x3B, 0x5B, 0xA8));
                button.Foreground = installed
                    ? new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x44))
                    : Brushes.White;
            }

            if (_states.TryGetValue(item.Id, out var state))
                state.Text = installed ? "✓ 已安装" : "未安装";
        }
    }

    private async Task ToggleAsync(ComponentItem item)
    {
        if (_manager.IsInstalled(item))
        {
            var ok = MessageBox.Show(this,
                $"确定删除「{item.Name}」？\n\n删除后需要重新下载才能使用。",
                "确认删除", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (ok != MessageBoxResult.OK) return;

            try
            {
                _manager.Remove(item);
                StatusText.Text = $"已删除 {item.Name}";
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
