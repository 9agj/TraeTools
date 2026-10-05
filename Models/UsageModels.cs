using System;
using System.Collections.Generic;

namespace TraeTools.Models;

/// <summary>一页用量查询结果。</summary>
public sealed class UsagePageResult
{
    public int Total { get; set; }
    public List<UsageSessionRecord> Items { get; } = new();
}

/// <summary>
/// 一次会话的用量记录（对应 /trae/api/v1/pay/query_user_usage_group_by_session 响应的每个元素）。
/// </summary>
public sealed class UsageSessionRecord
{
    public string SessionId { get; set; } = "";
    /// <summary>服务端 Unix 秒（会话产生时间）。</summary>
    public long UsageTime { get; set; }
    /// <summary>模型展示名（如 DeepSeek-V4-Flash 正式版）。</summary>
    public string ModelName { get; set; } = "";
    /// <summary>Max / 空字符串。</summary>
    public string Mode { get; set; } = "";
    public bool UseMaxMode { get; set; }
    /// <summary>本次会话消耗积分。</summary>
    public double CreditsFloat { get; set; }
    /// <summary>折合金额（元）。</summary>
    public double CostMoneyFloat { get; set; }
    public long InputToken { get; set; }
    public long OutputToken { get; set; }
    /// <summary>缓存命中（读）输入 token，扣费低于全价。</summary>
    public long CacheReadToken { get; set; }
    /// <summary>缓存写入 token。</summary>
    public long CacheWriteToken { get; set; }
    /// <summary>产品端（如 [2]）。</summary>
    public List<int> ProductTypeList { get; set; } = new();
    /// <summary>用户首条消息预览。</summary>
    public string UserInputPreview { get; set; } = "";
    /// <summary>本地抓取时间。</summary>
    public DateTime FetchedAt { get; set; } = DateTime.Now;

    /// <summary>展示用：本地时间。</summary>
    public DateTime UsageDateTime => DateTimeOffset.FromUnixTimeSeconds(UsageTime).ToLocalTime().DateTime;
    /// <summary>展示用：时间文本。</summary>
    public string TimeText => UsageDateTime.ToString("MM-dd HH:mm");
    /// <summary>展示用：模型名 + 模式。</summary>
    public string ModelText => string.IsNullOrEmpty(Mode) ? ModelName : $"{ModelName}（{Mode}）";
    /// <summary>展示用：输入+输出。</summary>
    public long TotalToken => checked(InputToken + OutputToken);
    /// <summary>缓存命中率（按输入侧计算，输入为 0 时按总 token 兜底）。</summary>
    public double CacheHitRate
    {
        get
        {
            long baseTokens = InputToken > 0 ? InputToken : TotalToken;
            return baseTokens > 0 ? CacheReadToken * 100.0 / baseTokens : 0;
        }
    }

    /// <summary>展示用：入/出/缓存命中的一行概览（缓存为 0 时省略）。</summary>
    public string TokenBrief
    {
        get
        {
            var s = $"入 {FormatTokens(InputToken)} / 出 {FormatTokens(OutputToken)}";
            if (CacheReadToken > 0)
                s += $" / 缓存命中 {CacheHitRate:0}%";
            return s;
        }
    }

    /// <summary>千分位缩写（1.5M / 12.3K）。</summary>
    private static string FormatTokens(long n)
        => n >= 1_000_000 ? $"{n / 1_000_000.0:0.#}M"
         : n >= 1_000 ? $"{n / 1_000.0:0.#}K"
         : n.ToString();
}

/// <summary>资格包过期记录（对应 /trae/api/v2/pay/expired_ents 响应元素）。</summary>
public sealed class ExpiredEnt
{
    public string Name { get; set; } = "";
    public double CreditsLimit { get; set; }
    /// <summary>过期时间（Unix 毫秒）。</summary>
    public long ExpireTimeMs { get; set; }
}

/// <summary>即将过期的提醒条目（展示用）。</summary>
public sealed class ExpiringEntItem
{
    public string Name { get; set; } = "";
    public double CreditsLimit { get; set; }
    public long ExpireTimeMs { get; set; }

    /// <summary>距今天数（向上取整）；&lt;=0 表示今天。</summary>
    public int DaysLeft
    {
        get
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var msLeft = ExpireTimeMs - now;
            if (msLeft <= 0) return 0;
            return (int)Math.Ceiling(msLeft / 86_400_000.0);
        }
    }

    public string CountdownText => DaysLeft <= 0 ? "今天过期" : $"{DaysLeft} 天后过期";
    public string CreditsText => $"{(long)CreditsLimit} 积分";
}

/// <summary>模型维度的聚合统计。</summary>
public sealed class ModelUsageStat
{
    public string ModelName { get; set; } = "";
    public int SessionCount { get; set; }
    public long TotalInput { get; set; }
    public long TotalOutput { get; set; }
    public long TotalCacheRead { get; set; }
    public double TotalCredits { get; set; }
    public double CacheHitRate
    {
        get
        {
            long baseTokens = TotalInput > 0 ? TotalInput : (TotalInput + TotalOutput);
            return baseTokens > 0 ? TotalCacheRead * 100.0 / baseTokens : 0;
        }
    }

    /// <summary>展示用：token 一行概览。</summary>
    public string TokenBrief => $"入 {FormatTokens(TotalInput)} / 出 {FormatTokens(TotalOutput)}";

    /// <summary>展示用：积分 + 命中率一行。</summary>
    public string DetailText => $"共 {TotalCredits:0.#} 积分 · 缓存命中 {CacheHitRate:0}%";

    private static string FormatTokens(long n)
        => n >= 1_000_000 ? $"{n / 1_000_000.0:0.#}M"
         : n >= 1_000 ? $"{n / 1_000.0:0.#}K"
         : n.ToString();
}