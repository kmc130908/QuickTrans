using System.Runtime.InteropServices;
using WinForms = System.Windows.Forms;

namespace QuickTrans.Services;

/// <summary>
/// 监听系统剪贴板更新（WM_CLIPBOARDUPDATE），实现可选的「快速连按两次 Ctrl+C 触发翻译」。
/// 采用轻量 Win32 NativeWindow 接收消息，不触发 WPF D3D 初始化，零轮询、零额外内存开销。
/// </summary>
public sealed class ClipboardWatcher : IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private sealed class MessageSink : WinForms.NativeWindow
    {
        private readonly Action<WinForms.Message> _onMessage;

        public MessageSink(Action<WinForms.Message> onMessage)
        {
            _onMessage = onMessage;
            CreateHandle(new WinForms.CreateParams
            {
                Caption = "QuickTransClipboardSink",
                Parent = new IntPtr(-3), // HWND_MESSAGE
            });
        }

        protected override void WndProc(ref WinForms.Message m)
        {
            _onMessage(m);
            base.WndProc(ref m);
        }
    }

    private MessageSink? _sink;
    private bool _listening;
    private long _lastTick;
    private string _lastText = "";
    private readonly uint _selfPid = (uint)Environment.ProcessId;

    /// <summary>软件自身读写剪贴板（模拟 Ctrl+C、复制译文、替换粘贴）期间屏蔽触发。</summary>
    private static long _suppressUntilTick;

    public static void SuppressFor(int ms = 700)
    {
        var until = Environment.TickCount64 + ms;
        var prev = Volatile.Read(ref _suppressUntilTick);
        if (until > prev) Volatile.Write(ref _suppressUntilTick, until);
    }

    /// <summary>检测到用户快速连按两次 Ctrl+C 时触发，参数为剪贴板文本与前台来源窗口句柄。</summary>
    public event Action<string, IntPtr>? DoubleCopyDetected;

    public void SetEnabled(bool enabled)
    {
        if (enabled == _listening) return;

        if (enabled)
        {
            _sink ??= new MessageSink(OnMessage);
            _listening = AddClipboardFormatListener(_sink.Handle);
            _lastTick = 0;
            _lastText = "";
        }
        else
        {
            if (_sink != null && _listening && _sink.Handle != IntPtr.Zero)
                RemoveClipboardFormatListener(_sink.Handle);
            _listening = false;
            _lastTick = 0;
            _lastText = "";
        }
    }

    private void OnMessage(WinForms.Message m)
    {
        if (m.Msg == WM_CLIPBOARDUPDATE && _listening)
            OnClipboardUpdate();
    }

    private void OnClipboardUpdate()
    {
        var now = Environment.TickCount64;
        if (now < Volatile.Read(ref _suppressUntilTick)) return;

        // 前台是本软件自身时不触发（避免在浮窗/设置里复制触发循环）
        var fg = GetForegroundWindow();
        if (fg != IntPtr.Zero)
        {
            GetWindowThreadProcessId(fg, out var pid);
            if (pid == _selfPid) return;
        }

        var text = TextGrabber.TryReadClipboardText()?.Trim();
        if (string.IsNullOrWhiteSpace(text)) return;

        var delta = now - _lastTick;
        // 间隔设 80–500ms：既过滤 Word/Chrome 单次复制连续写两次剪贴板（<50ms），
        // 又覆盖人手连按两次 Ctrl+C 的自然节奏。
        if (delta >= 80 && delta <= 500 && string.Equals(text, _lastText, StringComparison.Ordinal))
        {
            _lastTick = 0;
            _lastText = "";
            DoubleCopyDetected?.Invoke(text, fg);
            return;
        }

        _lastTick = now;
        _lastText = text;
    }

    public void Dispose()
    {
        SetEnabled(false);
        if (_sink != null)
        {
            if (_sink.Handle != IntPtr.Zero) _sink.DestroyHandle();
            _sink = null;
        }
    }
}
