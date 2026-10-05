using System.Net.Http;
using System.Text;
using System.Text.Json;
using TraeTools.Models;

namespace TraeTools.Services.Usage;

/// <summary>
/// Trae 用量统计客户端（逆向自 https://www.trae.cn/dashboard#usage 页面）。
///
/// 已有认证基础（与签到接口一致）：
///   authorization: Cloud-IDE-JWT &lt;token&gt;
///   cookie: X-Cloudide-Session=&lt;session&gt;
///   referer/origin: https://www.trae.cn/
///
/// 核心接口：POST /trae/api/v1/pay/query_user_usage_group_by_session
/// 请求体：{"start_time":…,"end_time":…,"page_size":20,"page_num":1,"usage_type":[7]}
/// 响应：user_usage_group_by_sessions[]，每元素是一次会话（模型/token/缓存命中/积分），分页。
/// </summary>
public sealed class TraeUsageClient
{
    private const string BaseUrl = "https://api.trae.cn";

    private readonly HttpClient _http;

    public string? LastError { get; private set; }

    public TraeUsageClient()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("TraeTools/1.0");
    }

    /// <summary>拉取一页「按会话聚合的用量」。</summary>
    public async Task<UsagePageResult> GetUsageSessionsAsync(
        string token, string? session,
        long startSeconds, long endSeconds,
        int pageNum, int pageSize = 20)
    {
        var result = new UsagePageResult();
        var body = $"{{\"start_time\":{startSeconds},\"end_time\":{endSeconds},\"page_size\":{pageSize},\"page_num\":{pageNum},\"usage_type\":[7]}}";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/trae/api/v1/pay/query_user_usage_group_by_session");
            req.Headers.TryAddWithoutValidation("Authorization", "Cloud-IDE-JWT " + token);
            if (!string.IsNullOrEmpty(session))
                req.Headers.TryAddWithoutValidation("Cookie", "X-Cloudide-Session=" + session);
            req.Headers.TryAddWithoutValidation("Referer", "https://www.trae.cn/");
            req.Headers.TryAddWithoutValidation("Origin", "https://www.trae.cn");
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            var json = await resp.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(json))
            {
                LastError = $"HTTP {(int)resp.StatusCode} 空响应";
                return result;
            }

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("total", out var totalEl))
            {
                LastError = "响应缺少 total 字段（可能接口失效/风控）";
                return result;
            }
            result.Total = GetInt(totalEl);

            if (!doc.RootElement.TryGetProperty("user_usage_group_by_sessions", out var arr) ||
                arr.ValueKind != JsonValueKind.Array)
            {
                LastError = "响应缺少 user_usage_group_by_sessions 字段";
                return result;
            }

            foreach (var el in arr.EnumerateArray())
                result.Items.Add(ParseSession(el));
            return result;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return result;
        }
    }

    /// <summary>整段拉取近 n 天内全部会话（自动翻页，最多 maxPages 页防止失控）。</summary>
    public async Task<List<UsageSessionRecord>> FetchAllAsync(
        string token, string? session, DateTime start, DateTime end,
        int pageSize = 20, int maxPages = 20)
    {
        var items = new List<UsageSessionRecord>();
        long startSec = new DateTimeOffset(start.ToLocalTime()).ToUnixTimeSeconds();
        long endSec = new DateTimeOffset(end.ToLocalTime()).ToUnixTimeSeconds();
        int page = 1, total = 1;
        while (page <= maxPages && (items.Count < total || total <= 0))
        {
            var r = await GetUsageSessionsAsync(token, session, startSec, endSec, page, pageSize);
            if (r.Items.Count > 0)
            {
                items.AddRange(r.Items);
                total = r.Total;
            }
            else
            {
                // 该页为空但 total 只来自第一页；若第二页起为空视为翻到底
                if (page == 1) break;
                break;
            }
            if (r.Items.Count < pageSize) break;   // 未满页 = 已到底
            page++;
        }
        return items;
    }

    /// <summary>查询资格包过期记录（含临期与已过期，供「积分快过期提醒」）。</summary>
    public async Task<List<ExpiredEnt>> GetExpiredEntsAsync(string token, string? session)
    {
        var list = new List<ExpiredEnt>();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/trae/api/v2/pay/expired_ents");
            req.Headers.TryAddWithoutValidation("Authorization", "Cloud-IDE-JWT " + token);
            if (!string.IsNullOrEmpty(session))
                req.Headers.TryAddWithoutValidation("Cookie", "X-Cloudide-Session=" + session);
            req.Headers.TryAddWithoutValidation("Referer", "https://www.trae.cn/");
            req.Headers.TryAddWithoutValidation("Origin", "https://www.trae.cn");
            req.Content = new StringContent("{}", Encoding.UTF8, "application/json");

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            var json = await resp.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(json)) return list;
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("expired_ent_list", out var arr) ||
                arr.ValueKind != JsonValueKind.Array)
                return list;
            foreach (var el in arr.EnumerateArray())
            {
                var ent = new ExpiredEnt
                {
                    Name = GetString(el, "name") ?? "",
                    CreditsLimit = GetDouble(el, "credits_limit", 0),
                    ExpireTimeMs = GetLong(el, "expire_time_ms"),
                };
                if (!string.IsNullOrEmpty(ent.Name) || ent.CreditsLimit > 0)
                    list.Add(ent);
            }
            return list;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return list;
        }
    }

    /// <summary>尽力解析单个会话元素；缺字段给默认值，不抛异常。</summary>
    private static UsageSessionRecord ParseSession(JsonElement el)
    {
        var rec = new UsageSessionRecord
        {
            SessionId = GetString(el, "session_id") ?? "",
            UsageTime = GetLong(el, "usage_time"),
            ModelName = GetString(el, "model_name") ?? "",
            Mode = GetString(el, "mode") ?? "",
            UseMaxMode = GetBool(el, "use_max_mode", false),
            CreditsFloat = GetDouble(el, "credits_float", GetDouble(el, "amount_float", 0)),
            CostMoneyFloat = GetDouble(el, "cost_money_float", 0),
            UserInputPreview = GetString(el, "user_input_preview") ?? "",
        };
        if (el.TryGetProperty("extra_info", out var extra) && extra.ValueKind == JsonValueKind.Object)
        {
            rec.InputToken = GetLong(extra, "input_token");
            rec.OutputToken = GetLong(extra, "output_token");
            rec.CacheReadToken = GetLong(extra, "cache_read_token");
            rec.CacheWriteToken = GetLong(extra, "cache_write_token");
        }
        if (el.TryGetProperty("product_type_list", out var pt) && pt.ValueKind == JsonValueKind.Array)
        {
            foreach (var v in pt.EnumerateArray())
            {
                if (v.TryGetInt32(out var p)) rec.ProductTypeList.Add(p);
            }
        }
        rec.FetchedAt = DateTime.Now;
        return rec;
    }

    private static string? GetString(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long GetLong(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l)) return l;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var l2)) return l2;
        return 0;
    }

    private static double GetDouble(JsonElement el, string name, double def = 0)
    {
        if (!el.TryGetProperty(name, out var v)) return def;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return d;
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), out var d2)) return d2;
        return def;
    }

    private static int GetInt(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var i)) return i;
        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out var i2)) return i2;
        return 0;
    }

    private static bool GetBool(JsonElement el, string name, bool def)
    {
        if (!el.TryGetProperty(name, out var v)) return def;
        return (v.ValueKind == JsonValueKind.True) || (v.ValueKind == JsonValueKind.False && v.GetBoolean());
    }
}