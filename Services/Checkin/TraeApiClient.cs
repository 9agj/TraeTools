using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TraeCheckin;

/// <summary>
/// 签到状态响应（/trae/api/v2/ug/checkin_credits/status）
/// </summary>
[JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
public class CheckinStatus
{
    public bool enable { get; set; }
    public bool checked_in { get; set; }
    public double credits { get; set; }
    /// <summary>连续/额外签到奖励（接口动态返回，如基础 150 + 连签 50）。</summary>
    public double extra_credits { get; set; }
    public int code { get; set; }
    public string? message { get; set; }
}

/// <summary>
/// Trae 云 API 客户端：封装签到状态、执行签到、剩余积分查询三个接口。
/// </summary>
public class TraeApiClient
{
    private const string BaseUrl = "https://api.trae.cn";

    private readonly HttpClient _http;

    public string? LastError { get; private set; }

    public TraeApiClient()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("TraeCheckin/1.0");
    }

    /// <summary>构造每请求的认证头。</summary>
    internal HttpRequestMessage BuildRequest(HttpMethod method, string path, string? token, string deviceId, string? body)
    {
        var req = new HttpRequestMessage(method, BaseUrl + path);
        if (!string.IsNullOrEmpty(token))
            req.Headers.TryAddWithoutValidation("Authorization", "Cloud-IDE-JWT " + token);
        req.Headers.TryAddWithoutValidation("X-User-Region", "cn");
        // 风控关键：x-device-id 必须是 16 位数字 Aha 设备号；使用 GUID/UUID 会触发 9074（"参与用户太多"）
        req.Headers.TryAddWithoutValidation("x-device-id", deviceId);
        if (body != null)
        {
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }
        return req;
    }

    /// <summary>查询今日签到状态与单日奖励。</summary>
    public async Task<CheckinStatus?> GetStatusAsync(string token, string deviceId)
    {
        using var req = BuildRequest(HttpMethod.Post, "/trae/api/v2/ug/checkin_credits/status", token, deviceId, "{}");
        return await SendAsync<CheckinStatus>(req);
    }

    /// <summary>执行每日签到。</summary>
    public async Task<CheckinStatus?> ClaimAsync(string token, string deviceId)
    {
        using var req = BuildRequest(HttpMethod.Post, "/trae/api/v2/ug/checkin_credits/claim", token, deviceId, "{}");
        return await SendAsync<CheckinStatus>(req);
    }

    /// <summary>
    /// 用 X-Cloudide-Session 会话 Cookie 换取全新 JWT（网页端续期接口）。
    /// 该接口仅需 Cookie 认证，无需 Authorization 头，返回 8 小时有效的新 token。
    /// </summary>
    public async Task<string?> GetUserTokenAsync(string session)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/cloudide/api/v3/common/GetUserToken");
            req.Headers.TryAddWithoutValidation("Cookie", "X-Cloudide-Session=" + session);
            req.Headers.TryAddWithoutValidation("Referer", "https://www.trae.cn/");
            req.Headers.TryAddWithoutValidation("Origin", "https://www.trae.cn");
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("Result", out var result) &&
                result.TryGetProperty("Token", out var tok) &&
                tok.ValueKind == JsonValueKind.String)
            {
                return tok.GetString();
            }
            LastError = "GetUserToken 响应缺少 Result.Token";
            return null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return null;
        }
    }

    /// <summary>查询剩余积分（汇总所有资格包的剩余额度）。</summary>
    public async Task<double> GetRemainingCreditsAsync(string token, string deviceId)
    {
        using var req = BuildRequest(HttpMethod.Post, "/trae/api/v2/pay/user_current_entitlement_list", token, deviceId, "{}");
        try
        {
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("user_entitlement_pack_list", out var packs) &&
                packs.ValueKind == JsonValueKind.Array)
            {
                // 空列表 = 接口返回了异常结构（如未登录/风控），视为解析失败返回 -1，
                // 避免把「解析失败」误当「剩余 0 分」把界面积分清零。
                if (packs.GetArrayLength() == 0)
                {
                    LastError = "积分包列表为空，可能会话失效";
                    return -1;
                }
                double remaining = 0;
                foreach (var p in packs.EnumerateArray())
                {
                    double limit = 0;
                    if (p.TryGetProperty("entitlement_base_info", out var info) &&
                        info.TryGetProperty("quota", out var quota) &&
                        quota.TryGetProperty("credits_limit", out var cl))
                    {
                        if (cl.ValueKind == JsonValueKind.Number) limit = cl.GetDouble();
                        else if (cl.ValueKind == JsonValueKind.String) double.TryParse(cl.GetString(), out limit);
                    }
                    double used = 0;
                    if (p.TryGetProperty("usage", out var usage) &&
                        usage.TryGetProperty("credits_amount", out var ua))
                    {
                        if (ua.ValueKind == JsonValueKind.Number) used = ua.GetDouble();
                        else if (ua.ValueKind == JsonValueKind.String) double.TryParse(ua.GetString(), out used);
                    }
                    var rem = limit - used;
                    if (rem < 0) rem = 0;
                    remaining += rem;
                }
                return remaining;
            }
            LastError = "响应缺少 user_entitlement_pack_list 字段";
            return -1;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return -1;
        }
    }

    private async Task<T?> SendAsync<T>(HttpRequestMessage req) where T : class
    {
        try
        {
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            var json = await resp.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(json))
            {
                LastError = $"HTTP {(int)resp.StatusCode} 空响应";
                return null;
            }
            var obj = JsonSerializer.Deserialize<T>(json);
            if (obj == null) LastError = "无法解析响应";
            return obj;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return null;
        }
    }

    // ==================== 账号资料 / 学生认证（逆向自 www.trae.cn/dashboard） ====================

    /// <summary>临时构造「业务 API」请求头（JWT + Session Cookie + 页面来源），与 usage 接口同款。</summary>
    private HttpRequestMessage BuildWebRequest(HttpMethod method, string path, string? token, string? session, string? body)
    {
        var req = new HttpRequestMessage(method, BaseUrl + path);
        if (!string.IsNullOrEmpty(token))
            req.Headers.TryAddWithoutValidation("Authorization", "Cloud-IDE-JWT " + token);
        if (!string.IsNullOrEmpty(session))
            req.Headers.TryAddWithoutValidation("Cookie", "X-Cloudide-Session=" + session);
        req.Headers.TryAddWithoutValidation("Referer", "https://www.trae.cn/");
        req.Headers.TryAddWithoutValidation("Origin", "https://www.trae.cn");
        if (body != null)
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return req;
    }

    /// <summary>拉取账号资料（/cloudide/api/v3/trae/GetUserInfo）：UID/昵称/脱敏手机号/头像。</summary>
    public async Task<TraeUserProfile?> GetUserProfileAsync(string token, string? session)
    {
        try
        {
            using var req = BuildWebRequest(HttpMethod.Post, "/cloudide/api/v3/trae/GetUserInfo", token, session, "{}");
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            var json = await resp.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(json)) { LastError = "GetUserInfo 空响应"; return null; }
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("Result", out var r) || r.ValueKind != JsonValueKind.Object)
            {
                LastError = "GetUserInfo 缺少 Result";
                return null;
            }
            return new TraeUserProfile
            {
                UserId = GetString(r, "UserID"),
                ScreenName = GetString(r, "ScreenName"),
                MobileMasked = GetString(r, "NonPlainTextMobile"),
                AvatarUrl = GetString(r, "AvatarUrl"),
                TenantId = GetString(r, "TenantID"),
            };
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return null;
        }
    }

    /// <summary>查询学生认证状态（/trae/api/v3/student_verification/status）：status==1 表示已认证。</summary>
    public async Task<int> GetStudentStatusAsync(string token, string? session)
    {
        try
        {
            using var req = BuildWebRequest(HttpMethod.Post, "/trae/api/v3/student_verification/status", token, session, "{}");
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            var json = await resp.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(json)) return 0;
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("status", out var s) && s.TryGetInt32(out var v) ? v : 0;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return 0;
        }
    }

    private static string? GetString(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>Trae 账号资料（GetUserInfo 结果）。</summary>
public sealed class TraeUserProfile
{
    public string? UserId { get; set; }
    public string? ScreenName { get; set; }
    public string? MobileMasked { get; set; }
    public string? AvatarUrl { get; set; }
    public string? TenantId { get; set; }
}
