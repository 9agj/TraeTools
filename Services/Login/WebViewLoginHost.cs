using System.Windows.Forms;

namespace TraeTools.Services.Login;

/// <summary>
/// 在独立 STA 线程运行 WinForms 内嵌 WebView2 登录窗口，避免与 Avalonia 主线程相互阻塞。
/// 返回 null 表示用户取消/未取得 token。
/// </summary>
public static class WebViewLoginHost
{
    public sealed record LoginResult(string Token, string? Session);

    public static Task<LoginResult?> RunAsync(string userDataDir, string? initialToken, bool debugCapture = false)
    {
        var tcs = new TaskCompletionSource<LoginResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
            }
            catch { /* 已在其它线程初始化时忽略 */ }

            string? token = null;
            string? session = null;
            try
            {
                using var form = new LoginHostForm(userDataDir, initialToken, debugCapture);
                form.FormClosing += (_, _) =>
                {
                    if (!string.IsNullOrEmpty(form.ResultToken))
                    {
                        token = form.ResultToken;
                        session = form.ResultSession;
                    }
                };
                Application.Run(form);
            }
            catch
            {
                token = null;
            }
            finally
            {
                tcs.TrySetResult(!string.IsNullOrEmpty(token) ? new LoginResult(token, session) : null);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Name = "TraeTools.WebViewLogin";
        thread.IsBackground = true;
        thread.Start();
        return tcs.Task;
    }
}