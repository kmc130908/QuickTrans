using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QuickTrans.Services;

namespace QuickTrans.Views;

/// <summary>
/// 首次使用的三页引导：划词翻译 / 截图与手输 / 模型与加速。
/// 只在第一次启动时出现，看完或跳过都算已读；也可在设置页随时重新打开。
/// </summary>
public partial class WelcomeWindow : Window
{
    private const int PageCount = 3;
    private int _page;

    public WelcomeWindow()
    {
        InitializeComponent();
        ApplyEngineHint();
        BuildDots();
        ShowPage(0);
    }

    /// <summary>根据当前内置引擎类型（Vulkan / CUDA / CPU）动态展示加速说明。</summary>
    private void ApplyEngineHint()
    {
        var backend = EngineManager.GetGpuBackendKind(Path.Combine(AppContext.BaseDirectory, "engine"));
        if (string.Equals(backend, "Vulkan", StringComparison.OrdinalIgnoreCase))
        {
            AccelTitle.Text = "⚡  已内置 Vulkan 通用 GPU 加速";
            AccelDesc.Text = "NVIDIA / AMD / Intel 显卡均可自动加速，无独显或想省显存时也可在设置里切回 CPU，无需再下 GPU 引擎。不想下模型也可直接用在线 API。";
        }
        else if (string.Equals(backend, "CUDA", StringComparison.OrdinalIgnoreCase))
        {
            AccelTitle.Text = "⚡  已内置 CUDA GPU 加速";
            AccelDesc.Text = "检测到 NVIDIA 显卡会自动启用 GPU 加速，也可在设置里随时切回 CPU。不想下模型也可直接配置在线翻译接口。";
        }
        else
        {
            AccelTitle.Text = "⚡  GPU 加速或在线接口（可选）";
            AccelDesc.Text = "当前为纯 CPU 精简版，装完即可用；想要更快可在组件管理里补充 GPU 引擎，或在「设置 → 在线接口」填入 DeepSeek / 百度 / 有道 / 谷歌接口。";
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
                Background = new SolidColorBrush(Color.FromRgb(0xD8, 0xD8, 0xDE)),
            });
        }
    }

    private void ShowPage(int index)
    {
        _page = index;

        Page1.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        Page2.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        Page3.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;

        PrevButton.Visibility = index == 0 ? Visibility.Collapsed : Visibility.Visible;
        SkipButton.Visibility = index == PageCount - 1 ? Visibility.Collapsed : Visibility.Visible;
        NextButton.Content = index == PageCount - 1 ? "开始使用" : "下一步";

        for (var i = 0; i < DotPanel.Children.Count; i++)
        {
            if (DotPanel.Children[i] is Border dot)
            {
                dot.Background = new SolidColorBrush(i == index
                    ? Color.FromRgb(0x3B, 0x5B, 0xA8)
                    : Color.FromRgb(0xD8, 0xD8, 0xDE));
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
