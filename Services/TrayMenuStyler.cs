using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Drawing = System.Drawing;
using Drawing2D = System.Drawing.Drawing2D;
using WinForms = System.Windows.Forms;
using WpfColor = System.Windows.Media.Color;

namespace QuickTrans.Services;

public enum TrayIconKind
{
    None,
    Translate,
    Ocr,
    Replace,
    Input,
    History,
    Model,
    Api,
    Device,
    Version,
    Update,
    Exit,
}

public sealed record TrayMenuItemTag(TrayIconKind Icon, bool IsDanger = false, bool IsAccent = false);

/// <summary>
/// 轻量 GDI+ 托盘右键菜单渲染器：Windows 11 Fluent 卡片质感、矢量化图标、
/// 圆角悬停高亮胶囊、右侧快捷键徽章与深浅主题自适应。
/// 零额外窗口或 WPF 资源开销，保持主进程低待机内存。
/// </summary>
public sealed class FluentTrayMenuRenderer : WinForms.ToolStripRenderer
{
    public ThemeManager.Palette Palette { get; private set; }

    public FluentTrayMenuRenderer(ThemeManager.Palette palette)
    {
        Palette = palette;
    }

    public void UpdatePalette(ThemeManager.Palette palette) => Palette = palette;

    internal Drawing.Color SurfaceColor => Palette.IsDark
        ? Drawing.Color.FromArgb(0x1E, 0x21, 0x2B)
        : Drawing.Color.FromArgb(0xFC, 0xFD, 0xFF);

    private Drawing.Color HoverFillColor(bool isDanger)
    {
        if (isDanger)
            return Palette.IsDark
                ? Drawing.Color.FromArgb(0x3D, 0x23, 0x29)
                : Drawing.Color.FromArgb(0xFD, 0xEC, 0xEC);

        return Palette.IsDark
            ? Drawing.Color.FromArgb(0x2A, 0x31, 0x44)
            : Drawing.Color.FromArgb(0xED, 0xF2, 0xFC);
    }

    private Drawing.Color HoverBorderColor(bool isDanger)
    {
        if (isDanger)
            return Palette.IsDark
                ? Drawing.Color.FromArgb(0x5A, 0x2E, 0x37)
                : Drawing.Color.FromArgb(0xF6, 0xD0, 0xD0);

        return Palette.IsDark
            ? Drawing.Color.FromArgb(0x3B, 0x46, 0x60)
            : Drawing.Color.FromArgb(0xD8, 0xE4, 0xFA);
    }

    private Drawing.Color DangerTextColor => Palette.IsDark
        ? Drawing.Color.FromArgb(0xFF, 0x7A, 0x7A)
        : Drawing.Color.FromArgb(0xC8, 0x32, 0x32);

    internal static Drawing.Color ToGdi(WpfColor c) => Drawing.Color.FromArgb(c.A, c.R, c.G, c.B);

    protected override void OnRenderToolStripBackground(WinForms.ToolStripRenderEventArgs e)
    {
        using var bg = new Drawing.SolidBrush(SurfaceColor);
        e.Graphics.FillRectangle(bg, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(WinForms.ToolStripRenderEventArgs e)
    {
        // Windows 11 原生 DWMWA_WINDOW_CORNER_PREFERENCE + DWMWA_BORDER_COLOR 自带圆角细边框；
        // Win10 及更早系统回退到 1px 平直细边框。
        if (TrayMenuStyler.SupportsWin11MenuFrame) return;

        using var pen = new Drawing.Pen(ToGdi(Palette.Border), 1f);
        var r = e.AffectedBounds;
        e.Graphics.DrawRectangle(pen, r.X, r.Y, Math.Max(0, r.Width - 1), Math.Max(0, r.Height - 1));
    }

    protected override void OnRenderImageMargin(WinForms.ToolStripRenderEventArgs e)
    {
        // 移除 WinForms 默认灰底竖条，保持整张菜单统一背景面
    }

    protected override void OnRenderSeparator(WinForms.ToolStripSeparatorRenderEventArgs e)
    {
        var dpi = Math.Max(1f, e.Graphics.DpiX / 96f);
        var inset = (int)Math.Round(10 * dpi);
        var y = e.Item.Height / 2;
        var x1 = inset;
        var x2 = Math.Max(x1 + 1, e.Item.Width - inset);
        using var pen = new Drawing.Pen(ToGdi(Palette.Divider), 1f);
        e.Graphics.DrawLine(pen, x1, y, x2, y);
    }

    protected override void OnRenderMenuItemBackground(WinForms.ToolStripItemRenderEventArgs e)
    {
        var g = e.Graphics;
        var dpi = Math.Max(1f, g.DpiX / 96f);
        var prevSmooth = g.SmoothingMode;
        g.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias;

        var tag = e.Item.Tag as TrayMenuItemTag;
        var isHovered = e.Item.Selected && e.Item.Enabled;

        if (isHovered)
        {
            var padX = 4f * dpi;
            var padY = 1.5f * dpi;
            var rect = new Drawing.RectangleF(
                padX,
                padY,
                Math.Max(4f, e.Item.Width - padX * 2f),
                Math.Max(4f, e.Item.Height - padY * 2f));
            var radius = 6f * dpi;

            using var path = CreateRoundedRect(rect, radius);
            using var fill = new Drawing.SolidBrush(HoverFillColor(tag?.IsDanger == true));
            using var border = new Drawing.Pen(HoverBorderColor(tag?.IsDanger == true), 1f);
            g.FillPath(fill, path);
            g.DrawPath(border, path);
        }

        if (tag != null && tag.Icon != TrayIconKind.None)
        {
            var iconColor = !e.Item.Enabled
                ? ToGdi(Palette.TextMuted)
                : tag.IsDanger && isHovered
                    ? DangerTextColor
                    : tag.IsAccent || isHovered
                        ? ToGdi(Palette.Accent)
                        : ToGdi(Palette.TextSecondary);

            var slotWidth = (int)Math.Round(20 * dpi);
            var iconSize = 13f * dpi;
            var cx = 6.5f * dpi + slotWidth * 0.5f;
            var cy = e.Item.Height * 0.5f;
            var iconRect = new Drawing.RectangleF(cx - iconSize * 0.5f, cy - iconSize * 0.5f, iconSize, iconSize);
            DrawVectorIcon(g, tag.Icon, iconRect, iconColor, 1.45f * dpi);
        }

        g.SmoothingMode = prevSmooth;
    }

    protected override void OnRenderItemCheck(WinForms.ToolStripItemImageRenderEventArgs e)
    {
        var g = e.Graphics;
        var dpi = Math.Max(1f, g.DpiX / 96f);
        var prevSmooth = g.SmoothingMode;
        g.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias;

        var boxSize = 16f * dpi;
        var cx = e.ImageRectangle.Left + e.ImageRectangle.Width * 0.5f;
        var cy = e.Item.Height * 0.5f;
        var rect = new Drawing.RectangleF(cx - boxSize * 0.5f, cy - boxSize * 0.5f, boxSize, boxSize);

        using var pill = CreateRoundedRect(rect, 4.5f * dpi);
        using var fill = new Drawing.SolidBrush(ToGdi(Palette.AccentSoftBg));
        g.FillPath(fill, pill);

        using var pen = new Drawing.Pen(ToGdi(Palette.Accent), 1.75f * dpi)
        {
            StartCap = Drawing2D.LineCap.Round,
            EndCap = Drawing2D.LineCap.Round,
            LineJoin = Drawing2D.LineJoin.Round,
        };
        var x = rect.X;
        var y = rect.Y;
        var s = rect.Width;
        g.DrawLines(pen, new[]
        {
            new Drawing.PointF(x + s * 0.25f, y + s * 0.52f),
            new Drawing.PointF(x + s * 0.44f, y + s * 0.72f),
            new Drawing.PointF(x + s * 0.76f, y + s * 0.30f),
        });

        g.SmoothingMode = prevSmooth;
    }

    protected override void OnRenderArrow(WinForms.ToolStripArrowRenderEventArgs e)
    {
        var g = e.Graphics;
        var dpi = Math.Max(1f, g.DpiX / 96f);
        var prevSmooth = g.SmoothingMode;
        g.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias;

        var arrowColor = e.Item?.Selected == true && e.Item.Enabled
            ? ToGdi(Palette.Accent)
            : ToGdi(Palette.TextMuted);

        var r = e.ArrowRectangle;
        var cx = r.Left + r.Width * 0.5f;
        var cy = r.Top + r.Height * 0.5f;
        var halfW = 2.4f * dpi;
        var halfH = 4.2f * dpi;

        using var pen = new Drawing.Pen(arrowColor, 1.5f * dpi)
        {
            StartCap = Drawing2D.LineCap.Round,
            EndCap = Drawing2D.LineCap.Round,
            LineJoin = Drawing2D.LineJoin.Round,
        };
        g.DrawLines(pen, new[]
        {
            new Drawing.PointF(cx - halfW, cy - halfH),
            new Drawing.PointF(cx + halfW, cy),
            new Drawing.PointF(cx - halfW, cy + halfH),
        });

        g.SmoothingMode = prevSmooth;
    }

    protected override void OnRenderItemText(WinForms.ToolStripItemTextRenderEventArgs e)
    {
        var g = e.Graphics;
        var dpi = Math.Max(1f, g.DpiX / 96f);
        var tag = e.Item.Tag as TrayMenuItemTag;
        var isHovered = e.Item.Selected && e.Item.Enabled;

        // 判断当前绘制的是否为右侧快捷键/版本号标签
        var isShortcutBadge = e.Item is WinForms.ToolStripMenuItem mi
            && !string.IsNullOrEmpty(mi.ShortcutKeyDisplayString)
            && string.Equals(e.Text, mi.ShortcutKeyDisplayString, StringComparison.Ordinal)
            && !string.Equals(e.Text, mi.Text, StringComparison.Ordinal);

        if (isShortcutBadge && !string.IsNullOrEmpty(e.Text))
        {
            var prevSmooth = g.SmoothingMode;
            g.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias;

            var badgeFontSize = Math.Max(7.6f, (e.TextFont?.Size ?? 9.4f) * 0.85f);
            using var badgeFont = new Drawing.Font("Segoe UI", badgeFontSize, Drawing.FontStyle.Regular);
            var textSize = WinForms.TextRenderer.MeasureText(
                g, e.Text, badgeFont,
                new Drawing.Size(int.MaxValue, int.MaxValue),
                WinForms.TextFormatFlags.NoPadding | WinForms.TextFormatFlags.SingleLine);

            var padX = (int)Math.Round(6 * dpi);
            var padY = (int)Math.Round(2.5f * dpi);
            var badgeW = textSize.Width + padX * 2;
            var badgeH = textSize.Height + padY * 2;
            var rightInset = (int)Math.Round(2 * dpi);
            var badgeX = Math.Max(e.TextRectangle.Left, e.TextRectangle.Right - badgeW - rightInset);
            var badgeY = Math.Max(1, (e.Item.Height - badgeH) / 2);
            var badgeRect = new Drawing.Rectangle(badgeX, badgeY, badgeW, badgeH);

            var badgeBg = isHovered
                ? ToGdi(Palette.AccentSoftBg)
                : ToGdi(Palette.PillBg);
            var badgeFg = !e.Item.Enabled
                ? ToGdi(Palette.TextMuted)
                : isHovered
                    ? ToGdi(Palette.Accent)
                    : ToGdi(Palette.TextSecondary);

            using var path = CreateRoundedRect(badgeRect, 4.5f * dpi);
            using var fill = new Drawing.SolidBrush(badgeBg);
            g.FillPath(fill, path);

            WinForms.TextRenderer.DrawText(
                g,
                e.Text,
                badgeFont,
                badgeRect,
                badgeFg,
                WinForms.TextFormatFlags.HorizontalCenter
                | WinForms.TextFormatFlags.VerticalCenter
                | WinForms.TextFormatFlags.SingleLine
                | WinForms.TextFormatFlags.NoPrefix);

            g.SmoothingMode = prevSmooth;
            return;
        }

        var isChecked = e.Item is WinForms.ToolStripMenuItem checkedMi && checkedMi.Checked;
        var textColor = !e.Item.Enabled
            ? ToGdi(Palette.TextMuted)
            : tag?.IsDanger == true && isHovered
                ? DangerTextColor
                : tag?.IsAccent == true || (isChecked && e.Item.Enabled)
                    ? ToGdi(Palette.Accent)
                    : ToGdi(Palette.TextPrimary);

        var rect = new Drawing.Rectangle(
            e.TextRectangle.Left,
            0,
            Math.Max(1, e.TextRectangle.Width),
            e.Item.Height);

        WinForms.TextRenderer.DrawText(
            g,
            e.Text,
            e.TextFont,
            rect,
            textColor,
            WinForms.TextFormatFlags.Left
            | WinForms.TextFormatFlags.VerticalCenter
            | WinForms.TextFormatFlags.SingleLine
            | WinForms.TextFormatFlags.EndEllipsis
            | WinForms.TextFormatFlags.NoPrefix);
    }

    private static Drawing2D.GraphicsPath CreateRoundedRect(Drawing.RectangleF rect, float radius)
    {
        var d = Math.Min(radius * 2f, Math.Min(rect.Width, rect.Height));
        var path = new Drawing2D.GraphicsPath();
        if (d <= 0.5f)
        {
            path.AddRectangle(rect);
            return path;
        }

        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static void DrawVectorIcon(
        Drawing.Graphics g, TrayIconKind kind, Drawing.RectangleF r, Drawing.Color color, float stroke)
    {
        using var pen = new Drawing.Pen(color, stroke)
        {
            StartCap = Drawing2D.LineCap.Round,
            EndCap = Drawing2D.LineCap.Round,
            LineJoin = Drawing2D.LineJoin.Round,
        };

        float X(float t) => r.X + r.Width * t;
        float Y(float t) => r.Y + r.Height * t;

        switch (kind)
        {
            case TrayIconKind.Translate:
                g.DrawLine(pen, X(0.10f), Y(0.24f), X(0.58f), Y(0.24f));
                g.DrawLine(pen, X(0.34f), Y(0.12f), X(0.34f), Y(0.24f));
                g.DrawLine(pen, X(0.48f), Y(0.24f), X(0.16f), Y(0.62f));
                g.DrawLine(pen, X(0.22f), Y(0.36f), X(0.50f), Y(0.60f));
                g.DrawLines(pen, new[]
                {
                    new Drawing.PointF(X(0.54f), Y(0.88f)),
                    new Drawing.PointF(X(0.73f), Y(0.44f)),
                    new Drawing.PointF(X(0.92f), Y(0.88f)),
                });
                g.DrawLine(pen, X(0.61f), Y(0.73f), X(0.85f), Y(0.73f));
                break;

            case TrayIconKind.Ocr:
                // 四个取景角标 + 中心点
                g.DrawLines(pen, new[] { new Drawing.PointF(X(0.12f), Y(0.34f)), new Drawing.PointF(X(0.12f), Y(0.12f)), new Drawing.PointF(X(0.34f), Y(0.12f)) });
                g.DrawLines(pen, new[] { new Drawing.PointF(X(0.66f), Y(0.12f)), new Drawing.PointF(X(0.88f), Y(0.12f)), new Drawing.PointF(X(0.88f), Y(0.34f)) });
                g.DrawLines(pen, new[] { new Drawing.PointF(X(0.12f), Y(0.66f)), new Drawing.PointF(X(0.12f), Y(0.88f)), new Drawing.PointF(X(0.34f), Y(0.88f)) });
                g.DrawLines(pen, new[] { new Drawing.PointF(X(0.66f), Y(0.88f)), new Drawing.PointF(X(0.88f), Y(0.88f)), new Drawing.PointF(X(0.88f), Y(0.66f)) });
                using (var dotBrush = new Drawing.SolidBrush(color))
                {
                    var d = r.Width * 0.24f;
                    g.FillEllipse(dotBrush, X(0.5f) - d * 0.5f, Y(0.5f) - d * 0.5f, d, d);
                }
                break;

            case TrayIconKind.Replace:
                g.DrawLine(pen, X(0.14f), Y(0.33f), X(0.84f), Y(0.33f));
                g.DrawLines(pen, new[] { new Drawing.PointF(X(0.67f), Y(0.16f)), new Drawing.PointF(X(0.86f), Y(0.33f)), new Drawing.PointF(X(0.67f), Y(0.50f)) });
                g.DrawLine(pen, X(0.16f), Y(0.68f), X(0.86f), Y(0.68f));
                g.DrawLines(pen, new[] { new Drawing.PointF(X(0.33f), Y(0.51f)), new Drawing.PointF(X(0.14f), Y(0.68f)), new Drawing.PointF(X(0.33f), Y(0.85f)) });
                break;

            case TrayIconKind.Input:
                using (var box = CreateRoundedRect(new Drawing.RectangleF(X(0.12f), Y(0.18f), r.Width * 0.76f, r.Height * 0.64f), r.Width * 0.14f))
                    g.DrawPath(pen, box);
                g.DrawLine(pen, X(0.30f), Y(0.40f), X(0.70f), Y(0.40f));
                g.DrawLine(pen, X(0.30f), Y(0.60f), X(0.56f), Y(0.60f));
                break;

            case TrayIconKind.History:
                g.DrawArc(pen, X(0.12f), Y(0.12f), r.Width * 0.76f, r.Height * 0.76f, -155, 290);
                g.DrawLines(pen, new[] { new Drawing.PointF(X(0.10f), Y(0.24f)), new Drawing.PointF(X(0.14f), Y(0.44f)), new Drawing.PointF(X(0.33f), Y(0.38f)) });
                g.DrawLines(pen, new[] { new Drawing.PointF(X(0.52f), Y(0.28f)), new Drawing.PointF(X(0.52f), Y(0.54f)), new Drawing.PointF(X(0.68f), Y(0.64f)) });
                break;

            case TrayIconKind.Model:
                using (var chip = CreateRoundedRect(new Drawing.RectangleF(X(0.24f), Y(0.24f), r.Width * 0.52f, r.Height * 0.52f), r.Width * 0.10f))
                    g.DrawPath(pen, chip);
                g.DrawRectangle(pen, X(0.40f), Y(0.40f), r.Width * 0.20f, r.Height * 0.20f);
                g.DrawLine(pen, X(0.38f), Y(0.10f), X(0.38f), Y(0.24f));
                g.DrawLine(pen, X(0.62f), Y(0.10f), X(0.62f), Y(0.24f));
                g.DrawLine(pen, X(0.38f), Y(0.76f), X(0.38f), Y(0.90f));
                g.DrawLine(pen, X(0.62f), Y(0.76f), X(0.62f), Y(0.90f));
                g.DrawLine(pen, X(0.10f), Y(0.38f), X(0.24f), Y(0.38f));
                g.DrawLine(pen, X(0.10f), Y(0.62f), X(0.24f), Y(0.62f));
                g.DrawLine(pen, X(0.76f), Y(0.38f), X(0.90f), Y(0.38f));
                g.DrawLine(pen, X(0.76f), Y(0.62f), X(0.90f), Y(0.62f));
                break;

            case TrayIconKind.Api:
                g.DrawEllipse(pen, X(0.12f), Y(0.12f), r.Width * 0.76f, r.Height * 0.76f);
                g.DrawLine(pen, X(0.13f), Y(0.50f), X(0.87f), Y(0.50f));
                g.DrawEllipse(pen, X(0.33f), Y(0.12f), r.Width * 0.34f, r.Height * 0.76f);
                break;

            case TrayIconKind.Device:
                g.DrawLines(pen, new[]
                {
                    new Drawing.PointF(X(0.56f), Y(0.10f)),
                    new Drawing.PointF(X(0.20f), Y(0.56f)),
                    new Drawing.PointF(X(0.50f), Y(0.56f)),
                    new Drawing.PointF(X(0.44f), Y(0.90f)),
                    new Drawing.PointF(X(0.80f), Y(0.44f)),
                    new Drawing.PointF(X(0.50f), Y(0.44f)),
                    new Drawing.PointF(X(0.56f), Y(0.10f)),
                });
                break;

            case TrayIconKind.Version:
                g.DrawEllipse(pen, X(0.14f), Y(0.14f), r.Width * 0.72f, r.Height * 0.72f);
                g.DrawLine(pen, X(0.50f), Y(0.46f), X(0.50f), Y(0.72f));
                using (var dot = new Drawing.SolidBrush(color))
                {
                    var d = Math.Max(1.8f, r.Width * 0.13f);
                    g.FillEllipse(dot, X(0.50f) - d * 0.5f, Y(0.31f) - d * 0.5f, d, d);
                }
                break;

            case TrayIconKind.Update:
                g.DrawLine(pen, X(0.50f), Y(0.14f), X(0.50f), Y(0.68f));
                g.DrawLines(pen, new[] { new Drawing.PointF(X(0.29f), Y(0.48f)), new Drawing.PointF(X(0.50f), Y(0.69f)), new Drawing.PointF(X(0.71f), Y(0.48f)) });
                g.DrawLine(pen, X(0.18f), Y(0.86f), X(0.82f), Y(0.86f));
                break;

            case TrayIconKind.Exit:
                g.DrawLines(pen, new[]
                {
                    new Drawing.PointF(X(0.46f), Y(0.16f)),
                    new Drawing.PointF(X(0.18f), Y(0.16f)),
                    new Drawing.PointF(X(0.18f), Y(0.84f)),
                    new Drawing.PointF(X(0.46f), Y(0.84f)),
                });
                g.DrawLine(pen, X(0.40f), Y(0.50f), X(0.86f), Y(0.50f));
                g.DrawLines(pen, new[] { new Drawing.PointF(X(0.67f), Y(0.32f)), new Drawing.PointF(X(0.86f), Y(0.50f)), new Drawing.PointF(X(0.67f), Y(0.68f)) });
                break;
        }
    }
}

/// <summary>
/// 统一应用 Windows 11 原生 DWM 圆角与边框颜色、菜单项间距、字体大小。
/// </summary>
public static class TrayMenuStyler
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWCP_ROUND = 2;

    private static readonly ConditionalWeakTable<WinForms.ToolStripDropDown, ThemeHolder> HookedMenus = new();

    private sealed class ThemeHolder
    {
        public ThemeManager.Palette Palette;
        public Drawing.Font? Font;
        public ThemeHolder(ThemeManager.Palette palette) => Palette = palette;
    }

    public static bool SupportsWin11MenuFrame => Environment.OSVersion.Version.Build >= 22000;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    public static void StyleMenu(
        WinForms.ToolStripDropDown menu,
        FluentTrayMenuRenderer renderer,
        ThemeManager.Palette palette,
        double fontScale)
    {
        menu.Renderer = renderer;
        menu.DropShadowEnabled = !SupportsWin11MenuFrame;
        menu.Padding = new WinForms.Padding(5, 6, 5, 6);
        menu.BackColor = renderer.SurfaceColor;

        if (menu is WinForms.ToolStripDropDownMenu ddm)
        {
            ddm.ShowImageMargin = true;
            ddm.ShowCheckMargin = false;
        }

        var clamped = Math.Clamp(fontScale <= 0 ? 1.0 : fontScale, 0.92, 1.15);
        var pt = (float)Math.Round(9.4 * clamped, 1);

        if (!HookedMenus.TryGetValue(menu, out var holder))
        {
            holder = new ThemeHolder(palette);
            HookedMenus.Add(menu, holder);
            menu.HandleCreated += (_, _) => ApplyDwm(menu, holder.Palette);
            menu.Disposed += (_, _) =>
            {
                try { holder.Font?.Dispose(); } catch { }
                holder.Font = null;
            };
        }
        else
        {
            holder.Palette = palette;
        }

        if (holder.Font == null || Math.Abs(holder.Font.Size - pt) > 0.05f)
        {
            var oldFont = holder.Font;
            holder.Font = new Drawing.Font("Microsoft YaHei UI", pt, Drawing.FontStyle.Regular);
            menu.Font = holder.Font;
            try { oldFont?.Dispose(); } catch { }
        }
        else if (!ReferenceEquals(menu.Font, holder.Font))
        {
            menu.Font = holder.Font;
        }

        StyleItems(menu.Items);

        if (menu.IsHandleCreated)
            ApplyDwm(menu, palette);
    }

    public static void StyleItems(WinForms.ToolStripItemCollection items)
    {
        foreach (WinForms.ToolStripItem item in items)
        {
            if (item is WinForms.ToolStripSeparator sep)
            {
                sep.Margin = new WinForms.Padding(0, 4, 0, 4);
                sep.AutoSize = false;
                sep.Height = 9;
            }
            else if (item is WinForms.ToolStripMenuItem mi)
            {
                mi.Padding = new WinForms.Padding(5, 5, 10, 5);
                mi.Margin = new WinForms.Padding(0, 1, 0, 1);
            }
        }
    }

    public static void ApplyDwm(WinForms.ToolStripDropDown menu, ThemeManager.Palette palette)
    {
        try
        {
            if (!menu.IsHandleCreated) return;
            var hwnd = menu.Handle;
            if (hwnd == IntPtr.Zero) return;

            var dark = palette.IsDark ? 1 : 0;
            _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

            if (SupportsWin11MenuFrame)
            {
                var corner = DWMWCP_ROUND;
                _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

                var c = palette.Border;
                var colorRef = c.R | (c.G << 8) | (c.B << 16);
                _ = DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref colorRef, sizeof(int));
            }
        }
        catch { }
    }
}
