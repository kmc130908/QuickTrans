using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace QuickTrans.Views;

/// <summary>
/// 基于 24×24 矢量路径坐标系的清晰图标控件，自动继承父级 TextElement.Foreground，
/// 在任意 DPI（100%–200%+）和深浅主题下均保持锐利边缘。
/// </summary>
public sealed class VectorIcon : FrameworkElement
{
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(VectorIcon),
        new FrameworkPropertyMetadata(
            Brushes.Black,
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.Inherits));

    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(Geometry), typeof(VectorIcon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(VectorIcon),
        new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(VectorIcon),
        new FrameworkPropertyMetadata(1.75, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillIconProperty = DependencyProperty.Register(
        nameof(FillIcon), typeof(bool), typeof(VectorIcon),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public Geometry? Data
    {
        get => (Geometry?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public bool FillIcon
    {
        get => (bool)GetValue(FillIconProperty);
        set => SetValue(FillIconProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var s = double.IsNaN(Size) || Size <= 0 ? 16.0 : Size;
        return new Size(s, s);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var geom = Data;
        if (geom == null) return;

        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        var side = Math.Min(w, h);
        var scale = side / 24.0;
        var dx = (w - side) * 0.5;
        var dy = (h - side) * 0.5;

        var brush = (Brush?)GetValue(TextElement.ForegroundProperty) ?? Brushes.Black;

        dc.PushTransform(new TranslateTransform(dx, dy));
        dc.PushTransform(new ScaleTransform(scale, scale));
        try
        {
            if (FillIcon)
            {
                dc.DrawGeometry(brush, null, geom);
            }
            else
            {
                var pen = new Pen(brush, StrokeThickness)
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round,
                    LineJoin = PenLineJoin.Round,
                };
                if (pen.CanFreeze) pen.Freeze();
                dc.DrawGeometry(null, pen, geom);
            }
        }
        finally
        {
            dc.Pop();
            dc.Pop();
        }
    }
}
