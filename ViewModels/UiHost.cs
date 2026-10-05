using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace TraeTools.ViewModels;

/// <summary>解析当前主窗口，供页面 VM 弹模态窗口（ShowDialog 需要 owner）。</summary>
internal static class UiHost
{
    public static Window? MainWindow
        => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
}