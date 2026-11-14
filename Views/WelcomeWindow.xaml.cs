using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace QuickTrans.Views;

/// <summary>
/// 首次使用的三页引导：划词翻译 / 截图与手输 / 模型与加速。
/// 只在第一次启动时出现，看完或跳过都算已读。
/// </summary>
public partial class WelcomeWindow : Window
{
    private const int PageCount = 3;
    private int _page;

    public WelcomeWindow()
    {
        InitializeComponent();
        BuildDots();
        ShowPage(0);
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
