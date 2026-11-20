using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace QuickTrans.Views;

public partial class ScreenSelector : Window
{
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("imm32.dll")]
    private static extern IntPtr ImmAssociateContext(IntPtr hWnd, IntPtr hIMC);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    private Point _start;
    private bool _dragging;
    private bool _done;

    /// <summary>选中的屏幕区域（物理像素，支持多显示器与负坐标副屏）。取消时为 null。</summary>
    public Int32Rect? SelectedRegion { get; private set; }

    public ScreenSelector()
    {
        InitializeComponent();

        // 覆盖全部显示器组成的虚拟桌面（包括副屏与不同缩放比例）
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
        App.Log($"selector ctor virtual: ({Left},{Top}) {Width}x{Height}");

        MouseLeftButtonDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseUp;
        KeyDown += OnKeyDown;

        SourceInitialized += (_, _) =>
        {
            // 截图框选不需要打字，禁用 IME 防止触发第三方输入法注入
            InputMethod.SetIsInputMethodEnabled(this, false);
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                ImmAssociateContext(hwnd, IntPtr.Zero);

                // 用物理像素把窗口铺满所有屏幕，解决多屏混合 DPI 下 WPF VirtualScreen 偏移/裁切问题
                var vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
                var vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
                var vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
                var vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
                if (vw > 0 && vh > 0)
                    SetWindowPos(hwnd, IntPtr.Zero, vx, vy, vw, vh, SWP_NOZORDER | SWP_NOACTIVATE);
            }
        };

        Loaded += (_, _) =>
        {
            PositionHintNearCursorScreen();
            App.Log($"selector loaded: {ActualWidth}x{ActualHeight} at ({Left},{Top})");
            Activate();
            Focus();
            Keyboard.Focus(this);
        };

        Closed += (_, _) => App.Log($"selector closed: region={SelectedRegion}");
    }

    /// <summary>把顶部提示条放到鼠标当前所在屏幕的中上方，双屏时不会被边框截断。</summary>
    private void PositionHintNearCursorScreen()
    {
        try
        {
            GetCursorPos(out var cur);
            var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(cur.X, cur.Y));
            var bounds = screen.Bounds;
            var topLeft = PointFromScreen(new Point(bounds.Left, bounds.Top));
            var topRight = PointFromScreen(new Point(bounds.Right, bounds.Top));
            var screenDipW = Math.Max(200, topRight.X - topLeft.X);
            var hintW = HintBox.ActualWidth > 0 ? HintBox.ActualWidth : 240;

            Canvas.SetLeft(HintBox, topLeft.X + (screenDipW - hintW) / 2.0);
            Canvas.SetTop(HintBox, topLeft.Y + 48);
        }
        catch
        {
            Canvas.SetLeft(HintBox, Math.Max(40, (ActualWidth - 240) / 2.0));
            Canvas.SetTop(HintBox, 48);
        }
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

        // PointToScreen 直接换算成 Win32 物理像素坐标，跨多屏和混合 DPI 均精确对齐
        var p1 = PointToScreen(new Point(x, y));
        var p2 = PointToScreen(new Point(x + w, y + h));
        var pxW = (int)Math.Round(p2.X - p1.X);
        var pxH = (int)Math.Round(p2.Y - p1.Y);
        if (pxW < 4 || pxH < 4)
        {
            Close();
            return;
        }

        SelectedRegion = new Int32Rect(
            (int)Math.Round(p1.X),
            (int)Math.Round(p1.Y),
            pxW,
            pxH);

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
