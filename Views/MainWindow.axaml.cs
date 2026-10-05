using System;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Platform;
using Avalonia.Threading;
using TraeTools.ViewModels;

namespace TraeTools.Views;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _autoCheckinTimer;
    private TrayIcon? _trayIcon;
    /// <summary>true=用户从托盘「退出」真正退出；false=关闭窗口仅最小化到托盘。 </summary>
    private bool _realExit;
    /// <summary>计时器重入守卫：上一轮任务（可能数十秒）未结束前跳过本轮 Tick（#33）。</summary>
    private bool _tickBusy;
    private readonly WindowNotificationManager _notify;

    public MainWindow()
    {
        _notify = new WindowNotificationManager(this) { Position = NotificationPosition.BottomRight, MaxItems = 3 };
        InitializeComponent();

        // 任务栏窗口图标：复用托盘同款 avalonia-logo.ico（缺省时任务栏不显示图标）
        try
        {
            using var iconStream = AssetLoader.Open(new Uri("avares://TraeTools/Assets/avalonia-logo.ico"));
            Icon = new WindowIcon(iconStream);
        }
        catch { /* 图标加载失败不影响主程序 */ }

        // 自动签到定时器：每 30 秒检查一次，到点且当天未签时自动签到
        _autoCheckinTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        // 防重入：上一轮未结束则跳过本轮，避免长任务在常驻托盘场景下叠加（#33）
        _autoCheckinTimer.Tick += async (_, _) =>
        {
            if (_tickBusy) return;
            _tickBusy = true;
            try
            {
                if (DataContext is MainViewModel vm)
                {
                    await vm.AutoCheckinIfDue();
                    await vm.RefreshSnapshotIfNeeded();
                }
            }
            finally { _tickBusy = false; }
        };

        BuildTray();
        Closing += OnWindowClosing;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        OnOpenedEulaCheck();
        _autoCheckinTimer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _autoCheckinTimer.Stop();
        _trayIcon?.Dispose();
        base.OnClosed(e);
    }

    /// <summary>关闭窗口默认最小化到托盘（与 TraeCheckin 一致）；仅托盘「退出」真正退出。</summary>
    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_realExit) return;
        e.Cancel = true;
        Hide();
        _notify.Show(new Notification("TraeTools", "已最小化到托盘，后台继续自动签到", NotificationType.Information, TimeSpan.FromSeconds(2)));
    }

    private void BuildTray()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://TraeTools/Assets/avalonia-logo.ico"));
            var menu = new NativeMenu();
            var show = new NativeMenuItem("显示主窗口");
            show.Click += (_, _) => ShowAndActivate();
            var checkin = new NativeMenuItem("立即签到");
            checkin.Click += async (_, _) => await TrayCheckinAsync();
            var exit = new NativeMenuItem("退出");
            exit.Click += (_, _) =>
            {
                _realExit = true;
                Close();
            };
            menu.Items.Add(show);
            menu.Items.Add(checkin);
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(exit);

            _trayIcon = new TrayIcon
            {
                Icon = new WindowIcon(stream),
                ToolTipText = "TraeTools 签到 + 账号切换",
                Menu = menu,
                IsVisible = true
            };
            // 左键单击托盘图标 → 显示/激活主窗口
            _trayIcon.Clicked += (_, _) => ShowAndActivate();
        }
        catch
        {
            // 托盘初始化失败（如系统不支持）不影响主程序
        }
    }

    private void ShowAndActivate()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private async Task TrayCheckinAsync()
    {
        if (DataContext is MainViewModel vm)
        {
            bool ok = await vm.QuickCheckinAsync();
            _notify.Show(new Notification(
                ok ? "签到成功" : "签到失败",
                ok ? "今日积分已到账（会员含连签加成）" : "请检查网络或 Token 是否有效",
                ok ? NotificationType.Success : NotificationType.Error));
        }
    }

    /// <summary>
    /// 首次启动弹出免责声明协议（EULA）。同意标记独立存放于 %APPDATA%\TraeTools\eula_accepted，
    /// 与 TraeCheckin 的已同意配置互相独立，保证本程序首次启动必定展示协议；
    /// 不同意则直接退出。
    /// </summary>
    private async void OnOpenedEulaCheck()
    {
        var flag = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TraeTools", "eula_accepted");
        if (System.IO.File.Exists(flag)) return;

        var eula = new EulaWindow();
        bool agreed = await eula.ShowDialog<bool>(this);
        if (agreed)
        {
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(flag)!);
                System.IO.File.WriteAllText(flag, DateTime.UtcNow.ToString("o"));
            }
            catch { /* 标记写失败下次仍会弹，可接受 */ }
        }
        else
        {
            _realExit = true;
            Close();
        }
    }
}


