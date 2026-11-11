using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace QuickTrans.Views;

public partial class ScreenSelector : Window
{
    private Point _start;
    private bool _dragging;
    private bool _done;

    /// <summary>选中的屏幕区域（物理像素）。取消时为 null。</summary>
    public Int32Rect? SelectedRegion { get; private set; }

    public ScreenSelector()
    {
        InitializeComponent();

        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;
        App.Log($"selector ctor: {Width}x{Height}");

        MouseLeftButtonDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseUp;
        KeyDown += OnKeyDown;

        Loaded += (_, _) =>
        {
            App.Log($"selector loaded: {Width}x{Height} at ({Left},{Top})");
            Activate();
            Focus();
            Keyboard.Focus(this);
        };

        Closed += (_, _) => App.Log($"selector closed: region={SelectedRegion}");
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _start = e.GetPosition(this);
        _dragging = true;
        HintBox.Visibility = Visibility.Collapsed;

        SelectionRect.Visibility = Visibility.Visible;
        Canvas.SetLeft(SelectionRect, _start.X);
        Canvas.SetTop(SelectionRect, _start.Y);
        SelectionRect.Width = 0;
        SelectionRect.Height = 0;

        CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;

        var p = e.GetPosition(this);
        var x = Math.Min(p.X, _start.X);
        var y = Math.Min(p.Y, _start.Y);

        Canvas.SetLeft(SelectionRect, x);
        Canvas.SetTop(SelectionRect, y);
        SelectionRect.Width = Math.Abs(p.X - _start.X);
        SelectionRect.Height = Math.Abs(p.Y - _start.Y);
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();

        var p = e.GetPosition(this);
        var x = Math.Min(p.X, _start.X);
        var y = Math.Min(p.Y, _start.Y);
        var w = Math.Abs(p.X - _start.X);
        var h = Math.Abs(p.Y - _start.Y);

        if (w < 6 || h < 6)
        {
            Close();
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var scaleX = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
        var scaleY = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;

        SelectedRegion = new Int32Rect(
            (int)Math.Round(x * scaleX),
            (int)Math.Round(y * scaleY),
            (int)Math.Round(w * scaleX),
            (int)Math.Round(h * scaleY));

        _done = true;
        Close();
    }

    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _done = true;
            Close();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (!_done) SelectedRegion = null;
    }
}
