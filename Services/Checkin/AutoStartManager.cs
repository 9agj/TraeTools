using Microsoft.Win32;

namespace TraeCheckin;

/// <summary>
/// 开机自启动：通过注册表 HKCU\Software\Microsoft\Windows\CurrentVersion\Run
/// 写入当前程序 exe 的完整路径（带引号）。
/// </summary>
internal static class AutoStartManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TraeTools";

    /// <summary>当前进程可执行文件的完整路径。</summary>
    private static string ExecutablePath => Environment.ProcessPath ?? "";

    /// <summary>是否已注册开机自启动。</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) != null;
        }
        catch { return false; }
    }

    /// <summary>开启或关闭开机自启动。</summary>
    public static void SetEnabled(bool enabled)
    {
        try
        {
            if (enabled)
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
                key.SetValue(ValueName, $@"""{ExecutablePath}""");
            }
            else
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                key?.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch { /* 静默失败 */ }
    }
}
