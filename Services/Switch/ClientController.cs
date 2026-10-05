using System.Diagnostics;

namespace TraeSwitch.Services;

public interface IClientController
{
    void KillAll();
    void Launch();
    bool IsRunning();
}

/// <summary>真实进程控制：按进程名结束全部实例并重新拉起客户端。</summary>
public sealed class ClientController(string processName, string exePath) : IClientController
{
    public void KillAll()
    {
        // 拿到的 Process 对象使用后必须 Dispose，否则常驻托盘下句柄持续累积（#33）
        Process[] procs;
        try { procs = Process.GetProcessesByName(processName); }
        catch { return; }
        foreach (var p in procs)
        {
            try { p.Kill(entireProcessTree: true); p.WaitForExit(10_000); } catch { /* 已退出则忽略 */ }
            finally { try { p.Dispose(); } catch { /* 忽略 */ } }
        }
    }

    public void Launch()
    {
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            throw new FileNotFoundException("找不到客户端：" + exePath);
        Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true });
    }

    public bool IsRunning() => GetProcessCount() > 0;

    /// <summary>查询进程数并立即释放全部 Process 句柄（GetProcessesByName 返回的对象必须 Dispose）。</summary>
    private int GetProcessCount()
    {
        try
        {
            Process[] procs = Process.GetProcessesByName(processName);
            int n = procs.Length;
            foreach (var p in procs) try { p.Dispose(); } catch { /* 忽略 */ }
            return n;
        }
        catch { return 0; }
    }
}
