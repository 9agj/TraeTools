using Avalonia;
using System;
using System.Threading;

namespace TraeTools;

sealed class Program
{
    /// <summary>单实例互斥名（与项目名一致，避免多实例各自签到/切换冲突）。</summary>
    private const string MutexName = @"Local\TraeTools.SingleInstance";

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        using var mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            // 已有实例在运行，直接退出本实例（TraeTools 为单实例工具）
            return;
        }
        // 旧版 TraeCheckin / TraeSwitch 数据迁移到统一根 %APPDATA%\TraeTools（幂等，保留旧目录）
        TraeTools.Services.DataPaths.Migrate();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
