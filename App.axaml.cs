using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using TraeTools.ViewModels;
using TraeTools.Views;

namespace TraeTools;

public partial class App : Application
{
    /// <summary>系统托盘图标全局引用（TrayIcon 无源头引用会被 GC 回收，故存于此）。</summary>
    public static TrayIcon? TrayRef;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainViewModel(),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}