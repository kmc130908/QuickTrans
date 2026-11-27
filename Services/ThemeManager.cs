using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace QuickTrans.Services;

/// <summary>
/// 浅色 / 深色 / 跟随系统主题配色。纯内存颜色常量，零额外依赖与运行开销。
/// </summary>
public static class ThemeManager
{
    public sealed record Palette(
        bool IsDark,
        Color WindowBg,
        Color SidebarBg,
        Color CardBg,
        Color SubPanelBg,
        Color Border,
        Color TextPrimary,
        Color TextSecondary,
        Color TextMuted,
        Color Accent,
        Color AccentSoftBg,
        Color PillBg,
        Color PillHoverBg,
        Color InputBg,
        Color Divider);

    /// <summary>主进程通知已打开的子进程窗口即时刷新主题/透明度的自定义消息。</summary>
    public const int WM_QT_REFRESH_THEME = 0x8000 + 101;

    /// <summary>主进程通知已打开的翻译浮窗更新附属窗口句柄（wParam: 1=设置页, 2=历史窗; lParam: HWND 或 0）。</summary>
    public const int WM_QT_SET_AUX_HWND = 0x8000 + 102;

    public static int ToCode(string? theme)
    {
        if (string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase)) return 1;
        if (string.Equals(theme, "dark", StringComparison.OrdinalIgnoreCase)) return 2;
        return 0;
    }

    public static string FromCode(int code) => code switch
    {
        1 => "light",
        2 => "dark",
        _ => "system",
    };

    public static readonly Palette Light = new(
        IsDark: false,
        WindowBg: Color.FromRgb(0xF5, 0xF6, 0xFA),
        SidebarBg: Color.FromRgb(0xEE, 0xF0, 0xF6),
        CardBg: Color.FromRgb(0xFF, 0xFF, 0xFF),
        SubPanelBg: Color.FromRgb(0xF2, 0xF4, 0xF8),
        Border: Color.FromRgb(0xD4, 0xD8, 0xE4),
        TextPrimary: Color.FromRgb(0x14, 0x15, 0x1A),
        TextSecondary: Color.FromRgb(0x3A, 0x3D, 0x4A),
        TextMuted: Color.FromRgb(0x64, 0x68, 0x76),
        Accent: Color.FromRgb(0x2F, 0x54, 0xA8),
        AccentSoftBg: Color.FromRgb(0xE6, 0xEE, 0xFD),
        PillBg: Color.FromRgb(0xEB, 0xED, 0xF4),
        PillHoverBg: Color.FromRgb(0xDF, 0xE3, 0xEE),
        InputBg: Color.FromRgb(0xFF, 0xFF, 0xFF),
        Divider: Color.FromRgb(0xE2, 0xE5, 0xEE));

    public static readonly Palette Dark = new(
        IsDark: true,
        WindowBg: Color.FromRgb(0x16, 0x18, 0x1E),
        SidebarBg: Color.FromRgb(0x12, 0x14, 0x19),
        CardBg: Color.FromRgb(0x1C, 0x1F, 0x28),
        SubPanelBg: Color.FromRgb(0x26, 0x2A, 0x36),
        Border: Color.FromRgb(0x3F, 0x46, 0x58),
        TextPrimary: Color.FromRgb(0xF6, 0xF8, 0xFD),
        TextSecondary: Color.FromRgb(0xD6, 0xDC, 0xE8),
        TextMuted: Color.FromRgb(0xA6, 0xAE, 0xC2),
        Accent: Color.FromRgb(0x7D, 0xA8, 0xFF),
        AccentSoftBg: Color.FromRgb(0x27, 0x3A, 0x63),
        PillBg: Color.FromRgb(0x31, 0x36, 0x46),
        PillHoverBg: Color.FromRgb(0x3E, 0x45, 0x58),
        InputBg: Color.FromRgb(0x19, 0x1C, 0x24),
        Divider: Color.FromRgb(0x39, 0x40, 0x52));

    public static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", false);
            var val = key?.GetValue("AppsUseLightTheme");
            return val is int i && i == 0;
        }
        catch
        {
            return false;
        }
    }

    public static Palette Resolve(string? theme)
    {
        if (string.Equals(theme, "dark", StringComparison.OrdinalIgnoreCase)) return Dark;
        if (string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase)) return Light;
        return IsSystemDark() ? Dark : Light;
    }

    public static SolidColorBrush Brush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public static SolidColorBrush Brush(byte alpha, Color c)
    {
        var b = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
        b.Freeze();
        return b;
    }

    /// <summary>按用户设定的缩放倍率刷新窗口动态字号资源（0.85–1.35）。</summary>
    public static void ApplyFontScale(ResourceDictionary resources, double scale)
    {
        var s = Math.Clamp(scale <= 0 ? 1.0 : scale, 0.85, 1.35);
        resources["FontSizeXs"] = Math.Round(11.5 * s, 1);
        resources["FontSizeHint"] = Math.Round(12.0 * s, 1);
        resources["FontSizeSm"] = Math.Round(12.5 * s, 1);
        resources["FontSizeBase"] = Math.Round(13.5 * s, 1);
        resources["FontSizeSection"] = Math.Round(14.5 * s, 1);
        resources["FontSizeHeader"] = Math.Round(20.0 * s, 1);
    }
}
