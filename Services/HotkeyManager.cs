using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace QuickTrans.Services;

/// <summary>
/// 全局热键。用 message-only 窗口接收 WM_HOTKEY，不依赖主窗口是否存在。
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int MOD_ALT = 0x0001;
    private const int MOD_CONTROL = 0x0002;
    private const int MOD_SHIFT = 0x0004;
    private const int MOD_WIN = 0x0008;
    private const int MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly HwndSource _source;
    private readonly int _id = 0x5154;
    private bool _registered;

    public event EventHandler? Pressed;

    public string? LastError { get; private set; }
    public string CurrentHotkey { get; private set; } = "";

    public HotkeyManager()
    {
        var p = new HwndSourceParameters("QuickTransHotkeySink")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE
        };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == _id)
        {
            Pressed?.Invoke(this, EventArgs.Empty);
            handled = true;
        }
        return IntPtr.Zero;
    }

    public bool Register(string hotkey)
    {
        Unregister();

        if (!TryParse(hotkey, out var mods, out var vk, out var parseError))
        {
            LastError = parseError;
            return false;
        }

        if (!RegisterHotKey(_source.Handle, _id, mods | MOD_NOREPEAT, vk))
        {
            LastError = "热键被其他程序占用，换一个组合试试";
            return false;
        }

        _registered = true;
        CurrentHotkey = hotkey;
        LastError = null;
        return true;
    }

    public void Unregister()
    {
        if (!_registered) return;
        UnregisterHotKey(_source.Handle, _id);
        _registered = false;
    }

    /// <summary>解析 "Ctrl+Shift+Q" 这类字符串。</summary>
    public static bool TryParse(string? hotkey, out int modifiers, out int vk, out string error)
    {
        modifiers = 0;
        vk = 0;
        error = "";

        if (string.IsNullOrWhiteSpace(hotkey))
        {
            error = "热键不能为空";
            return false;
        }

        var parts = hotkey.Split('+', StringSplitOptions.RemoveEmptyEntries);
        var hasKey = false;

        foreach (var raw in parts)
        {
            var p = raw.Trim();
            switch (p.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= MOD_CONTROL;
                    break;
                case "alt":
                    modifiers |= MOD_ALT;
                    break;
                case "shift":
                    modifiers |= MOD_SHIFT;
                    break;
                case "win":
                case "windows":
                    modifiers |= MOD_WIN;
                    break;
                default:
                    if (hasKey)
                    {
                        error = "只能包含一个主键";
                        return false;
                    }
                    if (!Enum.TryParse<System.Windows.Forms.Keys>(p, true, out var key)
                        || key == System.Windows.Forms.Keys.None)
                    {
                        error = $"无法识别按键「{p}」";
                        return false;
                    }
                    vk = (int)key;
                    hasKey = true;
                    break;
            }
        }

        if (!hasKey)
        {
            error = "缺少主键，例如 Ctrl+Q 里的 Q";
            return false;
        }

        return true;
    }

    public void Dispose()
    {
        Unregister();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
