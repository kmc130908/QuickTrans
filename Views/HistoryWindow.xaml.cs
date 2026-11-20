using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using QuickTrans.Models;
using QuickTrans.Services;

namespace QuickTrans.Views;

public partial class HistoryWindow : Window
{
    private ThemeManager.Palette _theme;
    private List<HistoryItem> _all = new();
    private HwndSource? _hwndSource;

    public HistoryWindow(string? themeSetting = "system")
    {
        InitializeComponent();
        _theme = ThemeManager.Resolve(themeSetting);
        ApplyTheme(themeSetting);
        Reload();

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            _hwndSource = HwndSource.FromHwnd(hwnd);
            _hwndSource?.AddHook(WndProc);
        };
        Closed += (_, _) =>
        {
            if (_hwndSource != null)
            {
                _hwndSource.RemoveHook(WndProc);
                _hwndSource = null;
            }
        };
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == ThemeManager.WM_QT_REFRESH_THEME)
        {
            var code = wParam.ToInt32();
            var theme = code < 0 ? AppConfig.Load().Theme : ThemeManager.FromCode(code);
            ApplyTheme(theme);
            Render();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void ApplyTheme(string? themeSetting)
    {
        _theme = ThemeManager.Resolve(themeSetting);
        Background = ThemeManager.Brush(_theme.WindowBg);
        SearchBorder.Background = ThemeManager.Brush(_theme.InputBg);
        SearchBorder.BorderBrush = ThemeManager.Brush(_theme.Border);
        SearchBox.Foreground = ThemeManager.Brush(_theme.TextPrimary);
        SearchBox.CaretBrush = ThemeManager.Brush(_theme.TextPrimary);
        SearchPlaceholder.Foreground = ThemeManager.Brush(_theme.TextMuted);

        ClearAllButton.Background = ThemeManager.Brush(_theme.PillBg);
        ClearAllButton.Foreground = ThemeManager.Brush(_theme.TextSecondary);
        CloseBtn.Background = ThemeManager.Brush(_theme.PillBg);
        CloseBtn.Foreground = ThemeManager.Brush(_theme.TextPrimary);
        StatusText.Foreground = ThemeManager.Brush(_theme.TextMuted);
    }

    private void Reload()
    {
        _all = HistoryService.Load();
        Render();
    }

    private void Render()
    {
        ListPanel.Children.Clear();
        var kw = SearchBox.Text?.Trim() ?? "";
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(kw) ? Visibility.Visible : Visibility.Collapsed;

        var filtered = string.IsNullOrEmpty(kw)
            ? _all
            : _all.Where(x =>
                x.Source.Contains(kw, StringComparison.OrdinalIgnoreCase)
                || x.Result.Contains(kw, StringComparison.OrdinalIgnoreCase)).ToList();

        StatusText.Text = string.IsNullOrEmpty(kw)
            ? $"共 {_all.Count} 条记录（最多保留 100 条）"
            : $"匹配 {filtered.Count} / {_all.Count} 条";

        if (filtered.Count == 0)
        {
            ListPanel.Children.Add(new TextBlock
            {
                Text = _all.Count == 0 ? "还没有翻译历史记录" : "没有找到匹配的历史记录",
                FontSize = 13,
                Foreground = ThemeManager.Brush(_theme.TextMuted),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 60, 0, 0),
            });
            return;
        }

        foreach (var item in filtered)
            ListPanel.Children.Add(BuildCard(item));
    }

    private Border BuildCard(HistoryItem item)
    {
        var stack = new StackPanel();

        // 顶部元信息 + 操作
        var top = new Grid();
        var meta = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        var modeText = item.Mode switch
        {
            "dict" => "词典",
            "polish" => "润色",
            "explain" => "解释",
            _ => item.Target,
        };
        meta.Children.Add(new Border
        {
            Background = ThemeManager.Brush(_theme.AccentSoftBg),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(7, 2, 7, 2),
            Child = new TextBlock
            {
                Text = modeText,
                FontSize = 10.5,
                Foreground = ThemeManager.Brush(_theme.Accent),
            },
        });
        meta.Children.Add(new TextBlock
        {
            Text = $"{item.Time:MM-dd HH:mm}  ·  {item.Engine}",
            FontSize = 10.5,
            Foreground = ThemeManager.Brush(_theme.TextMuted),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        top.Children.Add(meta);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var copyRes = MakeMiniBtn("复制译文");
        copyRes.Click += (_, _) =>
        {
            try
            {
                ClipboardWatcher.SuppressFor(700);
                Clipboard.SetText(item.Result);
                StatusText.Text = "已复制译文";
            }
            catch { }
        };
        var copySrc = MakeMiniBtn("复制原文");
        copySrc.Margin = new Thickness(6, 0, 0, 0);
        copySrc.Click += (_, _) =>
        {
            try
            {
                ClipboardWatcher.SuppressFor(700);
                Clipboard.SetText(item.Source);
                StatusText.Text = "已复制原文";
            }
            catch { }
        };
        var del = MakeMiniBtn("删除");
        del.Margin = new Thickness(6, 0, 0, 0);
        del.Click += (_, _) =>
        {
            HistoryService.Delete(item.Id);
            Reload();
        };

        actions.Children.Add(copyRes);
        actions.Children.Add(copySrc);
        actions.Children.Add(del);
        top.Children.Add(actions);
        stack.Children.Add(top);

        // 原文
        stack.Children.Add(new TextBlock
        {
            Text = item.Source,
            FontSize = 12,
            Foreground = ThemeManager.Brush(_theme.TextSecondary),
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 54,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 8, 0, 0),
        });

        // 分隔线
        stack.Children.Add(new Border
        {
            Height = 1,
            Background = ThemeManager.Brush(_theme.Divider),
            Margin = new Thickness(0, 7, 0, 7),
        });

        // 译文
        stack.Children.Add(new TextBlock
        {
            Text = item.Result,
            FontSize = 13.5,
            Foreground = ThemeManager.Brush(_theme.TextPrimary),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 21,
        });

        return new Border
        {
            Background = ThemeManager.Brush(_theme.CardBg),
            BorderBrush = ThemeManager.Brush(_theme.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 11, 14, 12),
            Margin = new Thickness(0, 0, 0, 10),
            Child = stack,
        };
    }

    private Button MakeMiniBtn(string text) => new()
    {
        Content = text,
        Style = (Style)FindResource("PillBtn"),
        Background = ThemeManager.Brush(_theme.PillBg),
        Foreground = ThemeManager.Brush(_theme.TextSecondary),
        Padding = new Thickness(8, 3, 8, 3),
        FontSize = 11,
    };

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => Render();

    private void OnClearAllClick(object sender, RoutedEventArgs e)
    {
        if (_all.Count == 0) return;
        var ok = MessageBox.Show(this, "确定清空全部翻译历史记录？", "清空历史",
            MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (ok != MessageBoxResult.OK) return;
        HistoryService.Clear();
        Reload();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
