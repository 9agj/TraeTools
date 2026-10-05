using System;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace TraeTools.Views;

/// <summary>粘贴 GitHub PAT 的输入窗口：返回 true 表示已提交有效文本（访问 PatText 获取）。</summary>
public partial class PatInputWindow : Window
{
    public string? PatText { get; private set; }

    public PatInputWindow()
    {
        InitializeComponent();
    }

    private void OnOpenGithubClick(object? sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://github.com/settings/personal-access-tokens/new") { UseShellExecute = true }); }
        catch { /* 打开失败忽略 */ }
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        var value = PatBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            PatBox.Text = "";
            PatBox.Focus();
            return;
        }
        PatText = value;
        Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}
