using System.IO;
using System.Windows;
using System.Windows.Controls;
using QuickTrans.Models;
using QuickTrans.Services;

namespace QuickTrans.Views;

/// <summary>
/// 首次使用的四页引导：核心触发 / 词典与效率功能 / 主题与免打扰 / 模型与接口。
/// 只在第一次启动时出现，看完或跳过都算已读；也可在设置页随时重新打开。
/// </summary>
public partial class WelcomeWindow : Window
{
    private const int PageCount = 4;
    private int _page;
    private readonly ThemeManager.Palette _theme;

    public WelcomeWindow(string? themeSetting = null)
    {
        InitializeComponent();
        _theme = ThemeManager.Resolve(themeSetting ?? AppConfig.Load().Theme);
        ApplyTheme();
        ApplyEngineHint();
        BuildDots();
        ShowPage(0);
    }

    private void ApplyTheme()
    {
        Resources["BrushWindowBg"] = ThemeManager.Brush(_theme.WindowBg);
        Resources["BrushCardBg"] = ThemeManager.Brush(_theme.CardBg);
        Resources["BrushBorder"] = ThemeManager.Brush(_theme.Border);
        Resources["BrushTextPrimary"] = ThemeManager.Brush(_theme.TextPrimary);
        Resources["BrushTextSecondary"] = ThemeManager.Brush(_theme.TextSecondary);
        Resources["BrushTextMuted"] = ThemeManager.Brush(_theme.TextMuted);
        Resources["BrushAccent"] = ThemeManager.Brush(_theme.Accent);
        Resources["BrushPillBg"] = ThemeManager.Brush(_theme.PillBg);
    }

    /// <summary>根据当前内置引擎类型（Vulkan / CUDA / CPU）动态展示加速说明。</summary>
    private void ApplyEngineHint()
    {
        var backend = EngineManager.GetGpuBackendKind(Path.Combine(AppContext.BaseDirectory, "engine"));
        if (string.Equals(backend, "Vulkan", StringComparison.OrdinalIgnoreCase))
        {
            AccelTitle.Text = "⚡  已内置 Vulkan 通用 GPU + CPU 引擎";
            AccelDesc.Text = "NVIDIA / AMD / Intel 显卡均可自动加速，无需再下载引擎；切回 CPU 模式时显存占用为 0 MB，支持闲置自动卸载模型。";
        }
        else if (string.Equals(backend, "CUDA", StringComparison.OrdinalIgnoreCase))
        {
            AccelTitle.Text = "⚡  已内置 CUDA GPU 加速";
            AccelDesc.Text = "检测到 NVIDIA 显卡会自动启用 GPU 加速，也可在设置或托盘菜单里随时切回 CPU（0 显存占用）。";
        }
        else
        {
            AccelTitle.Text = "⚡  内置 CPU 推理引擎";
            AccelDesc.Text = "当前为纯 CPU 引擎，装完即可离线使用；支持闲置自动卸载模型释放内存。";
        }
    }

    private void BuildDots()
    {
        DotPanel.Children.Clear();
        for (var i = 0; i < PageCount; i++)
        {
            DotPanel.Children.Add(new Border
            {
                Width = 7,
                Height = 7,
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(0, 0, 6, 0),
                Background = ThemeManager.Brush(_theme.Border),
            });
        }
    }

    private void ShowPage(int index)
    {
        _page = index;

        Page1.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        Page2.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        Page3.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        Page4.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;

        PrevButton.Visibility = index == 0 ? Visibility.Collapsed : Visibility.Visible;
        SkipButton.Visibility = index == PageCount - 1 ? Visibility.Collapsed : Visibility.Visible;
        NextButton.Content = index == PageCount - 1 ? "开始使用" : "下一步";

        for (var i = 0; i < DotPanel.Children.Count; i++)
        {
            if (DotPanel.Children[i] is Border dot)
            {
                dot.Background = i == index
                    ? ThemeManager.Brush(_theme.Accent)
                    : ThemeManager.Brush(_theme.Border);
            }
        }
    }

    private void OnPrevClick(object sender, RoutedEventArgs e) => ShowPage(Math.Max(0, _page - 1));

    private void OnNextClick(object sender, RoutedEventArgs e)
    {
        if (_page >= PageCount - 1)
        {
            Close();
            return;
        }
        ShowPage(_page + 1);
    }

    private void OnSkipClick(object sender, RoutedEventArgs e) => Close();
}
