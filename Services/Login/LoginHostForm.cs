using System.Drawing;
using System.Text;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace TraeTools.Services.Login;

/// <summary>
/// WinForms 内嵌 WebView2 登录窗口（移植自 TraeCheckin.LoginForm）。
/// 用户在此完成登录后，自动从 localStorage 读取 Cloud-IDE-Token，
/// 并从 CookieManager 读取 X-Cloudide-Session（约 14 天有效）一并返回。
///
/// debugCapture 为临时诊断模式：监听页面发出的全部 *.trae.cn JSON 接口（含 usage 页数据），
/// 拿到 token 后不立即关闭，等待数据加载完成再把请求/响应全量落盘到
/// %LOCALAPPDATA%\TraeTools\Capture\usage_capture_&lt;时间戳&gt;.txt，用于逆向确认接口结构。
/// </summary>
public sealed class LoginHostForm : Form
{
    private readonly string _userDataDir;
    private readonly string? _initialToken;
    private readonly bool _debugCapture;
    private WebView2? _webView;
    private bool _tokenObtained;

    /// <summary>登录成功后获得的 Token；未取得或用户关闭为 null/空。</summary>
    public string? ResultToken { get; private set; }
    /// <summary>X-Cloudide-Session Cookie 值（可能为 null）。</summary>
    public string? ResultSession { get; private set; }

    // ---- 临时抓包：捕获项与状态 ----
    private readonly object _captureLock = new();
    private readonly List<string> _captured = new();
    private readonly Dictionary<string, string> _requestBodies = new();   // uri -> POST 请求体
    private System.Windows.Forms.Timer? _captureUiTimer;
    private System.Windows.Forms.Timer? _autoCloseTimer;
    private int _idleSeconds;
    private Label? _captureStatusLabel;
    private const int AutoCloseIdleSeconds = 15;   // 不再有新请求后等待秒数
    private const int MaxBodyBytes = 200 * 1024;   // 单个响应体截断上限
    private const int MaxEntries = 500;            // 最多保留的抓包条目数

    // 抓包导航预设
    private const string HomeUrl = "https://www.trae.cn/";
    private const string DashboardUrl = "https://www.trae.cn/dashboard";
    private const string UsageUrl = "https://www.trae.cn/dashboard#usage";
    private TextBox? _addressBox;

    public LoginHostForm(string userDataDir, string? initialToken, bool debugCapture = false)
    {
        _userDataDir = userDataDir;
        _initialToken = initialToken;
        _debugCapture = debugCapture;

        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);

        Text = "登录 TRAE 账号" + (_debugCapture ? "（抓包模式）" : "");
        Width = 900;
        Height = 700;
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        MaximizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;

        var btnClose = new Button { Text = "关闭窗口", Dock = DockStyle.Right, Width = 110, Margin = new Padding(8) };
        btnClose.Click += (_, _) => Close();

        var top = new Panel { Dock = DockStyle.Top, Height = 44 };
        top.Controls.Add(btnClose);
        var lbl = new Label
        {
            Text = "请在弹出的窗口内登录 TRAE（手机号+验证码）。登录成功后本窗口会自动识别并关闭。",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(10, 0, 0, 0)
        };
        top.Controls.Add(lbl);

        if (_debugCapture)
        {
            // 地址栏 + 快捷导航（方便逛遍各页面，把所有接口都抓下来）
            var nav = new Panel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(6, 4, 6, 2) };
            var flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Left,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            foreach (var (name, url) in new[] { ("首页", HomeUrl), ("仪表盘", DashboardUrl), ("用量", UsageUrl) })
            {
                var b = new Button { Text = name, AutoSize = true, Margin = new Padding(0, 2, 6, 0) };
                b.Click += (_, _) => NavigateTo(url);
                flow.Controls.Add(b);
            }
            var go = new Button { Text = "前往", AutoSize = true, Dock = DockStyle.Right };
            go.Click += (_, _) => NavigateTo(_addressBox?.Text ?? DashboardUrl);
            _addressBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Text = DashboardUrl,
                Font = new Font("Microsoft YaHei UI", 9f),
                Margin = new Padding(0, 1, 0, 1)
            };
            _addressBox.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) NavigateTo(_addressBox.Text); };
            nav.Controls.Add(flow);
            nav.Controls.Add(go);
            nav.Controls.Add(_addressBox);
            Controls.Add(nav);

            _captureStatusLabel = new Label
            {
                Text = "抓包监控中：捕获 0 条接口",
                Dock = DockStyle.Bottom,
                Height = 26,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.SeaGreen,
                Padding = new Padding(10, 0, 0, 0)
            };
            Controls.Add(_captureStatusLabel);
        }

        Controls.Add(top);
        Load += OnLoad;
    }

    private async void OnLoad(object? sender, EventArgs e)
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, _userDataDir);
            _webView = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(_webView);
            _webView.BringToFront();
            await _webView.EnsureCoreWebView2Async(env);
            _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _webView.NavigationCompleted += OnNavigationCompleted;
            if (_debugCapture)
            {
                _webView.CoreWebView2.WebResourceResponseReceived += OnWebResourceResponseReceived;
                const string filter = "https://api.trae.cn/*";   // 只监听业务 API 的 XHR 以补抓请求体
                try { _webView.CoreWebView2.AddWebResourceRequestedFilter(filter, CoreWebView2WebResourceContext.XmlHttpRequest); }
                catch { /* 过滤注册失败则只抓响应 */ }
                _webView.CoreWebView2.WebResourceRequested += OnWebResourceRequested;
                _captureUiTimer = new System.Windows.Forms.Timer { Interval = 1000 };
                _captureUiTimer.Tick += (_, _) => UpdateCaptureStatus();
                _captureUiTimer.Start();
            }
            NavigateTo(_addressBox?.Text ?? UsageUrl);

            var timer = new System.Windows.Forms.Timer { Interval = 1500 };
            timer.Tick += async (_, _) => await TryReadToken();
            timer.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show("初始化浏览器失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess) _ = TryReadToken();
    }

    /// <summary>地址栏/快捷按钮导航（自动补全协议头并同步地址栏）。</summary>
    private void NavigateTo(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url.Trim();
        try
        {
            if (_webView?.CoreWebView2 != null)
            {
                if (_addressBox != null) _addressBox.Text = url;
                _webView.CoreWebView2.Navigate(url);
            }
        }
        catch { /* 导航失败忽略 */ }
    }

    private async Task TryReadToken()
    {
        if (_tokenObtained || _webView?.CoreWebView2 == null) return;
        try
        {
            var result = await _webView.CoreWebView2.ExecuteScriptAsync(
                "(function(){try{var t=localStorage.getItem('Cloud-IDE-Token');return t?t:'';}catch(e){return '';}})()");
            var token = result.Trim('"');

            bool accepted = TraeCheckin.TokenUtils.ShouldAcceptNewToken(_initialToken, token);
            if (!accepted && !_debugCapture) return;   // 抓包模式放宽：只要页面里有 token 就开始等待 usage 数据
            if (string.IsNullOrEmpty(token)) return;

            string? session = null;
            try
            {
                var cookies = await _webView.CoreWebView2.CookieManager.GetCookiesAsync("https://www.trae.cn/");
                foreach (var c in cookies)
                {
                    if (c.Name == "X-Cloudide-Session")
                    {
                        session = c.Value;
                        break;
                    }
                }
            }
            catch { /* 读取 Cookie 失败保持 null，仍可登录 */ }

            _tokenObtained = true;
            ResultToken = token;
            ResultSession = session;

            if (_debugCapture)
            {
                // 抓包模式：不立即关闭，等 usage 页数据拉完；倒计时 15 秒无新请求则关闭
                if (_captureStatusLabel != null)
                    _captureStatusLabel.Text = "已获取 Token，等待 usage 数据加载（15 秒内无新请求自动关闭）…";
                _autoCloseTimer = new System.Windows.Forms.Timer { Interval = 1000 };
                _autoCloseTimer.Tick += (_, _) =>
                {
                    _idleSeconds++;
                    if (_idleSeconds >= AutoCloseIdleSeconds) Close();
                };
                _autoCloseTimer.Start();
            }
            else
            {
                Close();
            }
        }
        catch { /* 页面尚未就绪，等待下个时钟周期 */ }
    }

    // ---- 临时抓包逻辑 ----

    private static bool IsTraeJson(Uri uri, string contentType)
    {
        var host = uri.Host;
        if (!(host.Equals("api.trae.cn", StringComparison.OrdinalIgnoreCase)
              || host.Equals("www.trae.cn", StringComparison.OrdinalIgnoreCase)
              || host.EndsWith(".trae.cn", StringComparison.OrdinalIgnoreCase)))
            return false;
        return contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase)
               || contentType.Contains("+json", StringComparison.OrdinalIgnoreCase);
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        if (!_debugCapture) return;
        try
        {
            var stream = args.Request.Content;
            if (stream == null) return;
            _ = ReadRequestBodyAsync(args.Request.Uri, stream);
        }
        catch { /* 请求体读取失败不影响 */ }
    }

    private async Task ReadRequestBodyAsync(string uri, Stream stream)
    {
        try
        {
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            lock (_captureLock)
            {
                _requestBodies[uri] = body;
            }
        }
        catch { /* 忽略 */ }
    }

    private void OnWebResourceResponseReceived(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs args)
    {
        if (!_debugCapture) return;
        try
        {
            var uri = new Uri(args.Request.Uri);
            var ct = args.Response.Headers.GetHeader("content-type") ?? "";
            if (!IsTraeJson(uri, ct)) return;

            // 有新请求：重置空闲倒计时，给页面加载留时间
            _idleSeconds = 0;

            var sb = new StringBuilder();
            sb.AppendLine($"[{DateTime.Now:HH:mm:ss}] {args.Request.Method} {args.Request.Uri}");
            foreach (var h in args.Request.Headers)
            {
                // 请求头完整保留（含 Authorization），便于确认 usage 接口的认证方式
                sb.AppendLine($"> {h.Key}: {h.Value}");
            }
            sb.AppendLine($"< HTTP {args.Response.StatusCode}  content-type: {ct}");
            // 把该 URL 已抓到的请求体拼进条目（同一 URL 多次请求只保留最后一次，够用）
            string requestBody;
            lock (_captureLock)
            {
                _requestBodies.TryGetValue(args.Request.Uri, out var body);
                requestBody = body ?? "";
            }
            if (requestBody.Length > 0)
                sb.AppendLine($"> REQUEST-BODY: {requestBody}");

            _ = ReadBodyAsync(args, sb);
        }
        catch { /* 单个请求解析失败不影响其它 */ }
    }

    private async Task ReadBodyAsync(CoreWebView2WebResourceResponseReceivedEventArgs args, StringBuilder sb)
    {
        try
        {
            using var stream = await args.Response.GetContentAsync();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var chars = new char[MaxBodyBytes];
            int read = await reader.ReadBlockAsync(chars, 0, chars.Length);
            var body = new string(chars, 0, read);
            sb.AppendLine(body);
            // 异步回落不在 UI 线程：锁内追加，避免竞态
            lock (_captureLock)
            {
                _captured.Add(sb.ToString() + "\n----");
            }
        }
        catch
        {
            lock (_captureLock)
            {
                _captured.Add(sb.ToString() + "\n<响应体读取失败>\n----");
            }
        }
        finally
        {
            TryBeginInvokeUpdateStatus();
        }
    }

    private void TryBeginInvokeUpdateStatus()
    {
        if (IsDisposed || _captureStatusLabel == null) return;
        try { BeginInvoke(new Action(UpdateCaptureStatus)); } catch { /* 窗口已关闭 */ }
    }

    private void UpdateCaptureStatus()
    {
        if (_captureStatusLabel == null) return;
        lock (_captureLock) _captureStatusLabel.Text = $"抓包监控中：捕获 {_captured.Count} 条接口";
    }

    /// <summary>窗口关闭前把抓包结果落盘。</summary>
    private void DumpCapture()
    {
        if (!_debugCapture) return;
        string[] lines;
        lock (_captureLock)
        {
            lines = _captured.TakeLast(MaxEntries).ToArray();
        }
        if (lines.Length == 0) return;
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TraeTools", "Capture");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"usage_capture_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            File.WriteAllLines(path, lines, Encoding.UTF8);
        }
        catch { /* 落盘失败不阻塞关闭 */ }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        DumpCapture();
        base.OnFormClosing(e);
    }
}