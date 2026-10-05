using Avalonia.Controls;
using Avalonia.Input;
using TraeTools.ViewModels;

namespace TraeTools.Views;

public partial class DashboardView : UserControl
{
    public DashboardView()
    {
        InitializeComponent();
    }

    /// <summary>趋势点命中：即时更新悬浮冒泡（替代有延迟的原生 ToolTip）。</summary>
    private void TrendHoverEnter(object? sender, PointerEventArgs e)
    {
        if (sender is Control c && c.DataContext is DashboardViewModel.TrendPoint p && DataContext is DashboardViewModel vm)
            vm.SetTrendHover(p);
    }

    private void TrendHoverExit(object? sender, PointerEventArgs e)
    {
        if (DataContext is DashboardViewModel vm) vm.SetTrendHover(null);
    }
}
