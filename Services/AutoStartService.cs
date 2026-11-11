using System.Diagnostics;
using Microsoft.Win32;

namespace QuickTrans.Services;

/// <summary>
/// 开机自启通过 HKCU 的 Run 键实现：写注册表比丢快捷方式到启动目录更规范，
/// 能查询、能删除，也不会在用户目录里留垃圾。
/// </summary>
public static class AutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "QuickTrans";

    /// <summary>当前是否已经登记开机自启。</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            var value = key?.GetValue(ValueName) as string;
            return !string.IsNullOrWhiteSpace(value);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>开启或关闭开机自启，返回实际操作是否成功。</summary>
    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key == null) return false;

            if (enabled)
            {
                var exePath = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exePath)) return false;

                // 路径带空格必须加引号，否则 Windows 会把前半截当程序名
                key.SetValue(ValueName, $"\"{exePath}\"");
            }
            else
            {
                key.DeleteValue(ValueName, false);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>让注册表内容和配置保持一致（程序启动时调用）。</summary>
    public static void Sync(bool desired)
    {
        if (desired == IsEnabled()) return;
        SetEnabled(desired);
    }
}
