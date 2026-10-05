using System.Text.Json;
using TraeTools.Models;

namespace TraeTools.Services.Usage;

/// <summary>
/// 用量记录本地持久化：%APPDATA%\TraeTools\data\usage_&lt;accountId&gt;.jsonl，每条一行。
/// 与签到历史文件同目录，按账号隔离。
/// 每次抓取把新会话追加写入，读取时按 SessionId 去重（保留最新）、按时间倒序。
/// </summary>
public static class UsageStore
{
    private static readonly object IoLock = new();
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private static string PathFor(string accountId) => System.IO.Path.Combine(
        TraeTools.Services.DataPaths.DataDir, $"usage_{accountId}.jsonl");

    /// <summary>读取某账号全部本地记录（按 UsageTime 倒序，SessionId 去重保留最新）。</summary>
    public static List<UsageSessionRecord> LoadAll(string accountId)
    {
        var list = new List<UsageSessionRecord>();
        lock (IoLock)
        {
            var path = PathFor(accountId);
            if (!File.Exists(path)) return list;
            try
            {
                foreach (var line in File.ReadAllLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var rec = JsonSerializer.Deserialize<UsageSessionRecord>(line, JsonOpts);
                        if (rec != null && !string.IsNullOrEmpty(rec.SessionId)) list.Add(rec);
                    }
                    catch { /* 跳过损坏行 */ }
                }
            }
            catch { /* 读取失败返回已解析部分 */ }
        }

        // 按 SessionId 去重，保留 FetchedAt 最新的一条
        var dedup = new Dictionary<string, UsageSessionRecord>();
        foreach (var r in list)
        {
            if (!dedup.TryGetValue(r.SessionId, out var old) || r.FetchedAt > old.FetchedAt)
                dedup[r.SessionId] = r;
        }
        return dedup.Values.OrderByDescending(r => r.UsageTime).ToList();
    }

    /// <summary>把新抓取的会话增量写入（按 SessionId 去重，仅追加缺失项）。</summary>
    public static void Merge(string accountId, IEnumerable<UsageSessionRecord> fetched)
    {
        _ = LoadAll(accountId);   // 触发一遍解析（含去重），随后基于最新快照计算缺失项
        var existing = LoadAll(accountId);
        var known = new HashSet<string>(existing.Select(e => e.SessionId));

        var toAppend = fetched
            .Where(r => !string.IsNullOrEmpty(r.SessionId) && known.Add(r.SessionId))
            .OrderBy(r => r.UsageTime)
            .ToList();
        if (toAppend.Count == 0) return;

        lock (IoLock)
        {
            try
            {
                var path = PathFor(accountId);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                using var sw = File.AppendText(path);
                foreach (var r in toAppend)
                {
                    sw.WriteLine(JsonSerializer.Serialize(r, JsonOpts));
                }
            }
            catch { /* 写入失败不影响本次展示 */ }
        }
    }
}