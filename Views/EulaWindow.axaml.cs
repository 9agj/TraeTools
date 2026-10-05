using Avalonia.Controls;
using Avalonia.Interactivity;

namespace TraeTools.Views;

/// <summary>
/// 首次启动的最终用户许可协议弹窗（Avalonia 版）。
/// ShowDialog&lt;bool&gt; 返回值：true=同意并继续，false=不同意并退出。
/// </summary>
public partial class EulaWindow : Window
{
    public EulaWindow()
    {
        InitializeComponent();
        // 勾选同意后才允许点「同意并继续」
        AgreeCheck.IsCheckedChanged += (_, _) =>
        {
            AgreeButton.IsEnabled = AgreeCheck.IsChecked == true;
        };
    }

    private void OnAgreeClick(object? sender, RoutedEventArgs e)
        => Close(true);

    private void OnDeclineClick(object? sender, RoutedEventArgs e)
        => Close(false);
}
