using System.Text;
using System.Text.Json;

namespace TraeCheckin;

/// <summary>
/// 飞书自定义机器人推送：把签到结果主动通知到飞书群。
/// 使用 text 消息格式，webhook 为机器人的完整地址（open.feishu.cn/open-apis/bot/v2/hook/xxx）。
/// </summary>
public static class FeishuNotifier
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>发送一条纯文本消息。webhook 为空时静默跳过；返回是否发送成功。</summary>
    public static async Task<bool> SendTextAsync(string? webhook, string text)
    {
        if (string.IsNullOrWhiteSpace(webhook)) return false;
        try
        {
            var body = JsonSerializer.Serialize(new { msg_type = "text", content = new { text } });
            using var req = new HttpRequestMessage(HttpMethod.Post, webhook);
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await Http.SendAsync(req);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
