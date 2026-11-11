using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media.Imaging;

namespace QuickTrans.Services;

/// <summary>
/// 读取当前选中文本。优先 UI Automation（无副作用），失败降级到模拟 Ctrl+C。
/// </summary>
public static class TextGrabber
{
    private const byte VK_CONTROL = 0x11;
    private const byte VK_C = 0x43;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll")]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll")]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    [DllImport("user32.dll")]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll")]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll")]
    private static extern uint EnumClipboardFormats(uint format);

    /// <summary>剪贴板里当前有多少种数据格式，用于判断"有内容但读不出"的情况。</summary>
    private static int CountClipboardFormats()
    {
        try
        {
            if (!OpenClipboard(IntPtr.Zero)) return -1;
            try
            {
                uint fmt = 0;
                var n = 0;
                while (true)
                {
                    fmt = EnumClipboardFormats(fmt);
                    if (fmt == 0) break;
                    n++;
                    if (n > 64) break;   // 防御异常循环
                }
                return n;
            }
            finally
            {
                CloseClipboard();
            }
        }
        catch
        {
            return -1;
        }
    }

    private const uint GMEM_MOVEABLE = 0x0002;
    private const uint CF_UNICODETEXT = 13;

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    /// <summary>上一次的取词方式，供界面显示诊断信息。</summary>
    public static string LastMethod { get; private set; } = "";

    /// <summary>选中文字在屏幕上的矩形（物理像素）。拿不到时为 null。</summary>
    public static Int32Rect? LastSelectionRect { get; private set; }

    /// <summary>控制台类窗口，Ctrl+C 会触发中断，必须排除。</summary>
    private static readonly string[] ConsoleClasses =
    {
        "ConsoleWindowClass",
        "CASCADIA_HOSTING_WINDOW_CLASS",
        "PseudoConsoleWindow",
        "mintty",
        "PuTTY",
    };

    public static string? Grab(string mode)
    {
        mode ??= "auto";
        LastSelectionRect = null;

        if (!string.Equals(mode, "clipboard", StringComparison.OrdinalIgnoreCase))
        {
            // UIA 跨进程调用偶尔会卡几百毫秒甚至更久，放到后台线程并限时等待，
            // 超时就先去走剪贴板，避免热键按下去半天没反应。
            // 首次调用某些应用要初始化辅助功能桥，给更宽的超时。
            string? viaUia = null;
            try
            {
                var uiaSw = Stopwatch.StartNew();
                var task = Task.Run(TryUia);
                if (task.Wait(800)) viaUia = task.Result;
                else LastMethod = "UIA 超时";
                App.Log($"  uia: {uiaSw.ElapsedMilliseconds}ms result={(viaUia?.Length ?? 0)}");
            }
            catch
            {
                viaUia = null;
            }

            if (!string.IsNullOrWhiteSpace(viaUia))
            {
                LastMethod = "UIA";
                return viaUia;
            }

            if (string.Equals(mode, "uia", StringComparison.OrdinalIgnoreCase))
            {
                LastMethod = "UIA 未取到";
                return null;
            }
        }

        var viaClip = TryClipboard();
        if (viaClip != null) LastMethod = "剪贴板";
        else if (string.IsNullOrEmpty(LastMethod) || LastMethod == "UIA 超时") LastMethod = "取词失败";
        return viaClip;
    }

    private static string? TryUia()
    {
        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused == null) return null;

            if (focused.GetCurrentPropertyValue(AutomationElement.IsPasswordProperty) is bool isPassword && isPassword)
                return null;

            // 焦点可能落在编辑控件的子元素上（WPF 的 TextBoxView、浏览器的 contentEditable 内层），
            // 直接拿不到 TextPattern。沿父链向上找，直到某个祖先支持。
            var element = focused;
            TextPattern? tp = null;
            while (element != null)
            {
                if (element.TryGetCurrentPattern(TextPattern.Pattern, out var raw))
                {
                    tp = (TextPattern)raw;
                    break;
                }
                try { element = TreeWalker.ControlViewWalker.GetParent(element); }
                catch { element = null; }
            }

            if (tp == null) return null;
            var ranges = tp.GetSelection();
            if (ranges.Length == 0) return null;

            var sb = new StringBuilder();
            var union = new Int32Rect();
            var hasRect = false;

            foreach (var r in ranges)
            {
                try { sb.Append(r.GetText(-1)); }
                catch { /* 个别 range 取不到，跳过 */ }

                // 顺手把选区在屏幕上的位置记下来，供浮窗定位用
                try
                {
                    foreach (var box in r.GetBoundingRectangles())
                    {
                        var rr = new Int32Rect(
                            (int)Math.Round(box.X),
                            (int)Math.Round(box.Y),
                            (int)Math.Round(box.Width),
                            (int)Math.Round(box.Height));

                        if (rr.Width <= 0 || rr.Height <= 0) continue;

                        if (!hasRect)
                        {
                            union = rr;
                            hasRect = true;
                        }
                        else
                        {
                            var left = Math.Min(union.X, rr.X);
                            var top = Math.Min(union.Y, rr.Y);
                            var right = Math.Max(union.X + union.Width, rr.X + rr.Width);
                            var bottom = Math.Max(union.Y + union.Height, rr.Y + rr.Height);
                            union = new Int32Rect(left, top, right - left, bottom - top);
                        }
                    }
                }
                catch { /* 某些控件不支持取边界矩形 */ }
            }

            LastSelectionRect = hasRect ? union : null;

            var text = sb.ToString().Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch
        {
            // UIA 跨进程调用受限、控件不支持、COM 超时等，统一降级
            return null;
        }
    }

    private static string? TryClipboard()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd != IntPtr.Zero && IsConsoleWindow(hwnd)) return null;

        var total = Stopwatch.StartNew();
        var backup = BackupClipboard();

        // 备份为空有两种可能：剪贴板真空，或者有内容但格式我们读不出来
        // （RTF、专业软件的自定义对象等）。后者一旦 Ctrl+C 覆盖就永久丢失，
        // 所以这种情况宁可不翻译，也要保住用户原来的剪贴板内容。
        if (!backup.HasAnything && CountClipboardFormats() > 0)
        {
            LastMethod = "剪贴板有内容但格式不支持，已跳过以保护原内容";
            App.Log("  clip: unknown format present, skip to avoid data loss");
            return null;
        }

        var needRestore = false;

        try
        {
            var before = GetClipboardSequenceNumber();
            SendCtrlC();

            var sw = Stopwatch.StartNew();
            var changed = false;
            while (sw.ElapsedMilliseconds < 250)
            {
                Thread.Sleep(8);
                if (GetClipboardSequenceNumber() != before)
                {
                    changed = true;
                    break;
                }
            }

            if (!changed)
            {
                // 剪贴板压根没被动过，不需要还原——省掉最耗时的一步
                App.Log($"  clip: no change, skip restore ({total.ElapsedMilliseconds}ms)");
                return null;
            }

            needRestore = true;

            // Chromium 系应用异步写剪贴板，稍等数据落盘
            Thread.Sleep(25);

            var text = FastClipboardText()?.Trim();
            App.Log($"  clip: read {text?.Length ?? 0} chars at {total.ElapsedMilliseconds}ms");
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        finally
        {
            if (needRestore)
            {
                RestoreClipboard(backup);
                App.Log($"  clip: restored at {total.ElapsedMilliseconds}ms");
            }
        }
    }

    private static bool IsConsoleWindow(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        if (GetClassName(hwnd, sb, sb.Capacity) <= 0) return false;
        var cls = sb.ToString();
        foreach (var c in ConsoleClasses)
        {
            if (string.Equals(cls, c, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static void SendCtrlC()
    {
        keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
        Thread.Sleep(8);
        keybd_event(VK_C, 0, 0, UIntPtr.Zero);
        Thread.Sleep(8);
        keybd_event(VK_C, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        Thread.Sleep(8);
        keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    private sealed class ClipBackup
    {
        public string? Text;
        public BitmapSource? Image;
        public string[]? Files;

        public bool HasAnything => Text != null || Image != null || Files != null;
    }

    private static ClipBackup BackupClipboard()
    {
        var b = new ClipBackup();

        // 优先用 Win32 直接读：拿不到就立刻返回，不走 WPF Clipboard 的内部重试，
        // 那个重试在剪贴板被别的进程占着时会拖两三秒。
        b.Text = FastClipboardText();

        if (b.Text == null)
        {
            try
            {
                if (Clipboard.ContainsImage()) b.Image = Clipboard.GetImage();
                else if (Clipboard.ContainsFileDropList())
                {
                    var list = Clipboard.GetFileDropList();
                    if (list.Count > 0)
                    {
                        b.Files = new string[list.Count];
                        list.CopyTo(b.Files, 0);
                    }
                }
            }
            catch
            {
                // 读不到就当作没有可备份的内容
            }
        }

        return b;
    }

    /// <summary>快速读剪贴板文本，失败立即返回 null，不重试。</summary>
    private static string? FastClipboardText()
    {
        try
        {
            if (!IsClipboardFormatAvailable(CF_UNICODETEXT)) return null;
            if (!OpenClipboard(IntPtr.Zero)) return null;

            try
            {
                var handle = GetClipboardData(CF_UNICODETEXT);
                if (handle == IntPtr.Zero) return null;

                var ptr = GlobalLock(handle);
                if (ptr == IntPtr.Zero) return null;

                try
                {
                    return Marshal.PtrToStringUni(ptr);
                }
                finally
                {
                    GlobalUnlock(handle);
                }
            }
            finally
            {
                CloseClipboard();
            }
        }
        catch
        {
            return null;
        }
    }

    private static void RestoreClipboard(ClipBackup b)
    {
        if (!b.HasAnything) return;

        if (b.Text != null)
        {
            // 纯文本走 Win32，抢不到剪贴板就快速失败，不做多次重试
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (SetClipboardTextFast(b.Text)) return;
                Thread.Sleep(30);
            }
            return;
        }

        try
        {
            if (b.Image != null)
            {
                Clipboard.SetImage(b.Image);
            }
            else if (b.Files != null)
            {
                var data = new DataObject();
                data.SetData(DataFormats.FileDrop, b.Files);
                Clipboard.SetDataObject(data, true);
            }
        }
        catch
        {
            // 图片/文件恢复失败不影响主流程
        }
    }

    private static bool SetClipboardTextFast(string text)
    {
        try
        {
            if (!OpenClipboard(IntPtr.Zero)) return false;

            try
            {
                EmptyClipboard();

                var bytes = (text.Length + 1) * 2;
                var handle = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)(uint)bytes);
                if (handle == IntPtr.Zero) return false;

                var ptr = GlobalLock(handle);
                if (ptr == IntPtr.Zero)
                {
                    GlobalFree(handle);
                    return false;
                }

                try
                {
                    var chars = text.ToCharArray();
                    Marshal.Copy(chars, 0, ptr, chars.Length);
                    Marshal.WriteInt16(ptr, chars.Length * 2, 0);
                }
                finally
                {
                    GlobalUnlock(handle);
                }

                if (SetClipboardData(CF_UNICODETEXT, handle) == IntPtr.Zero)
                {
                    GlobalFree(handle);
                    return false;
                }

                return true;
            }
            finally
            {
                CloseClipboard();
            }
        }
        catch
        {
            return false;
        }
    }
}
