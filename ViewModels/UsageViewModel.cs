using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TraeCheckin;
using TraeTools.Models;
using TraeTools.Services.Usage;

namespace TraeTools.ViewModels;

/// <summary>
/// 用量统计页：按激活账号展示逐会话用量（模型 / token / 缓存命中 / 积分）。
/// 数据来源 = 本地持久化（usage_&lt;accId&gt;.jsonl） + 逆向 usage 接口增量拉取。
/// </summary>
public partial class UsageViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _currentAccount = "未添加账号";

    [ObservableProperty]
    private string _dataRangeText = "暂无本地记录";

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private bool _isLoading;

    // ---- 汇总卡片 ----
    [ObservableProperty]
    private int _totalSessions;

    [ObservableProperty]
    private string _totalCreditsText = "0";

    [ObservableProperty]
    private string _totalTokensText = "0";

    [ObservableProperty]
    private string _totalTokensDetail = "";

    [ObservableProperty]
    private string _cacheHitText = "0";

    [ObservableProperty]
    private double _cacheHitRate;

    // ---- 积分快过期提醒 ----
    [ObservableProperty]
    private bool _hasExpiring;

    [ObservableProperty]
    private string _expiringTotalText = "";

    public ObservableCollection<ExpiringEntItem> ExpiringItems { get; } = new();
    public ObservableCollection<UsageSessionRecord> Sessions { get; } = new();
    public ObservableCollection<ModelUsageStat> ModelStats { get; } = new();

    private const int MaxSessionsShown = 500;   // 列表展示上限（汇总仍按全量）
    private const int FetchDays = 7;
    private const int ExpireWarnDays = 7;       // 提前多少天提醒积分过期

    public UsageViewModel()
    {
        Reload();
    }

    /// <summary>账号切换/进入页面时同步展示（读本地，不主动拉接口）。只跟随全局激活账号。</summary>
    public void Reload()
    {
        var cfg = MainViewModel.AppConfig;
        var acc = cfg?.Accounts.FirstOrDefault(a => a.Id == cfg.ActiveAccountId)
                  ?? cfg?.Accounts.FirstOrDefault();
        if (acc == null)
        {
            CurrentAccount = "未添加账号";
            Sessions.Clear();
            ModelStats.Clear();
            TotalSessions = 0;
            return;
        }
        CurrentAccount = string.IsNullOrEmpty(acc.Name)
            ? $"账号@{(acc.AccountUid ?? acc.Id.Substring(0, 6))}"
            : acc.Name;
        LoadFromStore(acc.Id);
    }

    /// <summary>真实拉取：近 7 天分页 → 增量落盘 → 刷新展示。</summary>
    [RelayCommand]
    private async Task Refresh()
    {
        var cfg = MainViewModel.AppConfig;
        var acc = cfg?.Accounts.FirstOrDefault(a => a.Id == cfg.ActiveAccountId)
                  ?? cfg?.Accounts.FirstOrDefault();
        if (acc == null || MainViewModel.UsageApi == null)
        {
            StatusMessage = "未添加账号或服务未初始化";
            return;
        }

        if (IsLoading) return;
        IsLoading = true;
        StatusMessage = "正在拉取用量数据…";
        var accName = string.IsNullOrEmpty(acc.Name) ? (acc.Id.Length > 6 ? acc.Id[..6] : acc.Id) : acc.Name!;
        AccountHelpers.AppLog("usage", accName, $"开始拉取用量数据（近 {FetchDays} 天）");
        try
        {
            bool valid = await AccountHelpers.EnsureValidTokenAsync(acc);
            if (!valid)
            {
                AccountHelpers.AppLog("usage", accName, "Token 已失效且无法换新");
                StatusMessage = "Token 已失效且无法换新，请重新登录";
                return;
            }

            var start = DateTime.Now.AddDays(-FetchDays);
            var end = DateTime.Now;
            var fetched = await MainViewModel.UsageApi.FetchAllAsync(acc.Token ?? "", acc.Session, start, end);
            if (fetched.Count == 0 && !string.IsNullOrEmpty(MainViewModel.UsageApi.LastError))
            {
                AccountHelpers.AppLog("usage", accName, $"拉取失败：{MainViewModel.UsageApi.LastError}");
                StatusMessage = $"拉取失败：{MainViewModel.UsageApi.LastError}";
                return;
            }

            UsageStore.Merge(acc.Id, fetched);
            LoadFromStore(acc.Id);

            // 积分快过期提醒（读接口，不落地）
            var expired = await MainViewModel.UsageApi.GetExpiredEntsAsync(acc.Token ?? "", acc.Session);
            UpdateExpiring(expired);

            AccountHelpers.AppLog("usage", accName, $"拉取完成：{fetched.Count} 条新会话");
            StatusMessage = fetched.Count > 0
                ? $"已同步 {fetched.Count} 条新会话（近 {FetchDays} 天）"
                : "没有新增会话";
        }
        catch (Exception ex)
        {
            AccountHelpers.AppLog("usage", accName, $"拉取异常：{ex.Message}");
            StatusMessage = "拉取异常：" + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>从本地读取该账号记录并重建列表 + 汇总 + 模型分布。</summary>
    private void LoadFromStore(string accountId)
    {
        var all = UsageStore.LoadAll(accountId);

        // ---- 汇总 ----
        TotalSessions = all.Count;
        double credits = all.Sum(r => r.CreditsFloat);
        TotalCreditsText = credits.ToString("0.#");
        long input = all.Sum(r => r.InputToken);
        long output = all.Sum(r => r.OutputToken);
        long cache = all.Sum(r => r.CacheReadToken);
        TotalTokensText = FormatTokens(input + output);
        TotalTokensDetail = $"入 {FormatTokens(input)} / 出 {FormatTokens(output)}";
        CacheHitText = FormatTokens(cache);
        CacheHitRate = input > 0 ? cache * 100.0 / input : (cache > 0 ? 100 : 0);

        if (all.Count == 0)
        {
            DataRangeText = "暂无本地记录（点「拉取用量」同步）";
            Sessions.Clear();
            ModelStats.Clear();
            return;
        }

        var min = DateTimeOffset.FromUnixTimeSeconds(all.Min(r => r.UsageTime)).ToLocalTime();
        var max = DateTimeOffset.FromUnixTimeSeconds(all.Max(r => r.UsageTime)).ToLocalTime();
        DataRangeText = $"本地记录 {min:MM-dd} ~ {max:MM-dd}，共 {all.Count} 次会话";

        // ---- 会话列表（最新 N 条） ----
        Sessions.Clear();
        foreach (var r in all.Take(MaxSessionsShown))
            Sessions.Add(r);

        // ---- 模型分布 ----
        ModelStats.Clear();
        foreach (var g in all.GroupBy(r => r.ModelText).OrderByDescending(g => g.Sum(r => r.TotalToken)))
        {
            ModelStats.Add(new ModelUsageStat
            {
                ModelName = g.Key,
                SessionCount = g.Count(),
                TotalInput = g.Sum(r => r.InputToken),
                TotalOutput = g.Sum(r => r.OutputToken),
                TotalCacheRead = g.Sum(r => r.CacheReadToken),
                TotalCredits = g.Sum(r => r.CreditsFloat),
            });
        }
    }

    private static string FormatTokens(long n)
    {
        return n >= 1_000_000 ? $"{n / 1_000_000.0:0.#}M"
            : n >= 1_000 ? $"{n / 1_000.0:0.#}K"
            : n.ToString();
    }

    /// <summary>从过期资格包列表里筛出「近 N 天即将过期」的，刷新提醒卡片。</summary>
    private void UpdateExpiring(List<ExpiredEnt> expired)
    {
        ExpiringItems.Clear();
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        double total = 0;
        foreach (var e in expired)
        {
            if (e.ExpireTimeMs <= nowMs) continue;          // 已过期的不提醒
            if (e.CreditsLimit <= 0) continue;
            var item = new ExpiringEntItem
            {
                Name = e.Name,
                CreditsLimit = e.CreditsLimit,
                ExpireTimeMs = e.ExpireTimeMs,
            };
            if (item.DaysLeft > ExpireWarnDays) continue;   // 提前量之外的不提醒
            ExpiringItems.Add(item);
            total += e.CreditsLimit;
        }
        HasExpiring = ExpiringItems.Count > 0;
        ExpiringTotalText = HasExpiring ? $"共 {total:0} 积分即将过期" : "";
    }
}