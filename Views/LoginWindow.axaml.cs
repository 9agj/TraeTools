using System;
using System.Diagnostics;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using TraeCheckin;
using TraeTools.Services.Login;
using TraeTools.ViewModels;

namespace TraeTools.Views;

/// <summary>
/// 登录窗口：优先使用内嵌 WebView2 自动登录（自动读取 token + session），
/// 也保留手动粘贴 Cloud-IDE-Token 的备选方案。
/// </summary>
public partial class LoginWindow : Window
{
    private readonly TraeAccount? _account;
    private bool _busy;

    public LoginWindow()
    {
        InitializeComponent();
    }

    public LoginWindow(TraeAccount? account)
        : this()
    {
        _account = account;
    }

    /// <summary>内嵌 WebView2 自动登录：独立 STA 线程宿主，自动读取 Cloud-IDE-Token 与 X-Cloudide-Session。</summary>
    private async void OnAutoLoginClick(object? sender, RoutedEventArgs e)
    {
        if (_busy || _account == null) return;
        if (string.IsNullOrEmpty(_account.Token))
        {
            // 首次登录：为账号准备独立的 WebView2 用户数据目录（账号间登录态隔离）
            _ = _account;
        }

        // 未安装 WebView2 运行库时内嵌登录不可用，明确提示改用下方手动粘贴方案
        try
        {
            _ = Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch
        {
            AutoStatus.Text = "未检测到 WebView2 运行库，请使用下方「手动方案」粘贴 Cloud-IDE-Token";
            return;
        }

        _busy = true;
        AutoStatus.Text = "正在启动浏览器…";
        try
        {
            string webViewDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TraeTools", "WebView", "account_" + (_account.Id.Length > 12 ? _account.Id[..12] : _account.Id));

            // 接口探索已完成；false=正常登录（自动识别并关闭窗口）。需要再抓包时改为 true
            var result = await WebViewLoginHost.RunAsync(webViewDir, _account.Token, debugCapture: false);

            if (result == null)
            {
                AutoStatus.Text = "未获取到 Token（已取消或登录未完成）";
                return;
            }

            _account.Token = result.Token;
            _account.Session = result.Session;
            _account.TokenUpdatedAt = DateTime.Now;
            var uid = TokenUtils.ParseAccountUid(result.Token);
            if (!string.IsNullOrEmpty(uid)) _account.AccountUid = uid;
            MainViewModel.AppConfig?.Save();
            Close(true);
        }
        catch (Exception ex)
        {
            AutoStatus.Text = "自动登录失败：" + ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    private void OnOpenBrowserClick(object? sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://www.trae.cn") { UseShellExecute = true }); }
        catch { /* 打开失败忽略 */ }
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        var pasted = TokenBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(pasted))
        {
            StatusText.Text = "请先粘贴 Cloud-IDE-Token";
            return;
        }
        var initial = _account?.Token;
        if (!TokenUtils.ShouldAcceptNewToken(initial, pasted))
        {
            StatusText.Text = "Token 无效（长度不足或与当前账号相同），请重新复制";
            return;
        }
        if (_account == null)
        {
            StatusText.Text = "无当前账号，无法保存";
            return;
        }
        _account.Token = pasted;
        _account.TokenUpdatedAt = DateTime.Now;
        var uid = TokenUtils.ParseAccountUid(pasted);
        if (!string.IsNullOrEmpty(uid)) _account.AccountUid = uid;
        MainViewModel.AppConfig?.Save();
        Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);
}