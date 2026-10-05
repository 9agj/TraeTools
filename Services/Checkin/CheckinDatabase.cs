using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TraeCheckin;

/// <summary>
/// 签到数据 SQLite 存储。替代原 history_*.txt / credits_total_*.txt 文本文件，
/// 支持按日期、账号、积分三维度查询与聚合。
/// 数据库文件：%APPDATA%\TraeTools\data\checkin.db
/// </summary>
public class CheckinDatabase : IDisposable
{
    private readonly SqliteConnection _conn;

    // ---- 表结构 ----
    // checkin_record: 每次签到一条记录
    //   id          INTEGER PRIMARY KEY AUTOINCREMENT
    //   date        TEXT    -- yyyy-MM-dd
    //   time        TEXT    -- HH:mm
    //   account_id  TEXT    -- TraeAccount.Id
    //   account_name TEXT   -- 签到时的展示名
    //   credits     REAL    -- 本次获得积分
    //   is_member   INTEGER -- 是否会员（0/1）
    //
    // credits_snapshot: 每日剩余积分快照（趋势图用）
    //   id          INTEGER PRIMARY KEY AUTOINCREMENT
    //   date        TEXT    -- yyyy-MM-dd
    //   account_id  TEXT    -- TraeAccount.Id
    //   remaining   REAL    -- 剩余积分

    public CheckinDatabase()
    {
        var dbPath = Path.Combine(TraeTools.Services.DataPaths.DataDir, "checkin.db");

        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        InitTables();
    }

    private void InitTables()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS checkin_record (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                date        TEXT    NOT NULL,
                time        TEXT    NOT NULL,
                account_id  TEXT    NOT NULL,
                account_name TEXT   NOT NULL DEFAULT '',
                credits     REAL   NOT NULL DEFAULT 0,
                is_member   INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_checkin_date ON checkin_record(date);
            CREATE INDEX IF NOT EXISTS idx_checkin_account ON checkin_record(account_id);

            CREATE TABLE IF NOT EXISTS credits_snapshot (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                date        TEXT    NOT NULL,
                account_id  TEXT    NOT NULL,
                remaining   REAL   NOT NULL DEFAULT 0
            );
            CREATE UNIQUE INDEX IF NOT EXISTS idx_snapshot_date_account ON credits_snapshot(date, account_id);
            """;
        cmd.ExecuteNonQuery();
    }

    // ==================== 写入 ====================

    /// <summary>记录一次签到。</summary>
    public void InsertCheckin(DateTime when, string accountId, string accountName, double credits, bool isMember)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO checkin_record (date, time, account_id, account_name, credits, is_member)
            VALUES ($date, $time, $accountId, $accountName, $credits, $isMember);
            """;
        cmd.Parameters.AddWithValue("$date", when.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("$time", when.ToString("HH:mm"));
        cmd.Parameters.AddWithValue("$accountId", accountId);
        cmd.Parameters.AddWithValue("$accountName", accountName ?? "");
        cmd.Parameters.AddWithValue("$credits", credits);
        cmd.Parameters.AddWithValue("$isMember", isMember ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    /// <summary>记录每日剩余积分快照（同一天同一账号只保留最新值）。</summary>
    public void InsertSnapshot(string accountId, double remaining)
    {
        using var cmd = _conn.CreateCommand();
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        cmd.CommandText = """
            INSERT INTO credits_snapshot (date, account_id, remaining)
            VALUES ($date, $accountId, $remaining)
            ON CONFLICT(date, account_id) DO UPDATE SET remaining = $remaining;
            """;
        cmd.Parameters.AddWithValue("$date", today);
        cmd.Parameters.AddWithValue("$accountId", accountId);
        cmd.Parameters.AddWithValue("$remaining", remaining);
        cmd.ExecuteNonQuery();
    }

    // ==================== 查询 ====================

    /// <summary>获取所有签到过的日期集合（日历打点 / 连签计算用）。</summary>
    public HashSet<DateTime> GetSignedDates()
    {
        var result = new HashSet<DateTime>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT date FROM checkin_record;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (DateTime.TryParse(reader.GetString(0), out var dt))
                result.Add(dt.Date);
        }
        return result;
    }

    /// <summary>获取今日某账号实际获得的积分（今日奖励展示用；无记录返回 0）。</summary>
    public double GetTodayCredits(string accountId)
    {
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT COALESCE(SUM(credits), 0)
                FROM checkin_record
                WHERE date = $today AND account_id = $accountId;
                """;
            cmd.Parameters.AddWithValue("$today", DateTime.Today.ToString("yyyy-MM-dd"));
            cmd.Parameters.AddWithValue("$accountId", accountId);
            var v = cmd.ExecuteScalar();
            if (v == null || v == DBNull.Value) return 0;
            return Convert.ToDouble(v);
        }
        catch { return 0; }
    }

    /// <summary>获取今日所有账号实际获得的总积分（今日奖励统计用；无记录返回 0）。</summary>
    public double GetTodayTotalCredits()
    {
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT COALESCE(SUM(credits), 0)
                FROM checkin_record
                WHERE date = $today;
                """;
            cmd.Parameters.AddWithValue("$today", DateTime.Today.ToString("yyyy-MM-dd"));
            var v = cmd.ExecuteScalar();
            if (v == null || v == DBNull.Value) return 0;
            return Convert.ToDouble(v);
        }
        catch { return 0; }
    }

    /// <summary>获取某月签到记录（日历/记录列表用），按时间倒序。</summary>
    public List<CheckinRecordDto> GetRecords(int? year = null, int? month = null, int limit = 50)
    {
        var result = new List<CheckinRecordDto>();
        using var cmd = _conn.CreateCommand();

        if (year.HasValue && month.HasValue)
        {
            var prefix = $"{year:D4}-{month:D2}";
            cmd.CommandText = $"""
                SELECT date, time, account_name, credits, is_member
                FROM checkin_record
                WHERE date LIKE $prefix
                ORDER BY date DESC, time DESC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$prefix", prefix + "%");
        }
        else
        {
            cmd.CommandText = $"""
                SELECT date, time, account_name, credits, is_member
                FROM checkin_record
                ORDER BY date DESC, time DESC
                LIMIT $limit;
                """;
        }
        cmd.Parameters.AddWithValue("$limit", limit);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new CheckinRecordDto
            {
                Date = reader.GetString(0),
                Time = reader.GetString(1),
                AccountName = reader.GetString(2),
                Credits = reader.GetDouble(3),
                IsMember = reader.GetInt32(4) == 1,
            });
        }
        return result;
    }

    /// <summary>按日期聚合：每天所有账号获得的总积分（统计页用）。</summary>
    public List<DailyTotalDto> GetDailyTotals(int days = 30)
    {
        var result = new List<DailyTotalDto>();
        using var cmd = _conn.CreateCommand();
        var since = DateTime.Today.AddDays(-days).ToString("yyyy-MM-dd");
        cmd.CommandText = """
            SELECT date, SUM(credits) as total, COUNT(*) as count
            FROM checkin_record
            WHERE date >= $since
            GROUP BY date
            ORDER BY date ASC;
            """;
        cmd.Parameters.AddWithValue("$since", since);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new DailyTotalDto
            {
                Date = reader.GetString(0),
                TotalCredits = reader.GetDouble(1),
                CheckinCount = reader.GetInt32(2),
            });
        }
        return result;
    }

    /// <summary>按日期+账号聚合：某天每个账号各获得多少积分。</summary>
    public List<AccountDailyDto> GetAccountDailyTotals(string date)
    {
        var result = new List<AccountDailyDto>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT account_id, account_name, SUM(credits) as total
            FROM checkin_record
            WHERE date = $date
            GROUP BY account_id
            ORDER BY total DESC;
            """;
        cmd.Parameters.AddWithValue("$date", date);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new AccountDailyDto
            {
                AccountId = reader.GetString(0),
                AccountName = reader.GetString(1),
                TotalCredits = reader.GetDouble(2),
            });
        }
        return result;
    }

    /// <summary>获取某账号的剩余积分趋势（仪表盘折线图用），取最近 N 条。</summary>
    public List<(DateTime Date, double Remaining)> GetSnapshotTrend(string accountId, int take = 14)
    {
        var result = new List<(DateTime, double)>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT date, remaining
            FROM credits_snapshot
            WHERE account_id = $accountId
            ORDER BY date DESC
            LIMIT $take;
            """;
        cmd.Parameters.AddWithValue("$accountId", accountId);
        cmd.Parameters.AddWithValue("$take", take);

        using var reader = cmd.ExecuteReader();
        var rows = new List<(DateTime Date, double Remaining)>();
        while (reader.Read())
        {
            if (DateTime.TryParse(reader.GetString(0), out var dt))
                rows.Add((dt, reader.GetDouble(1)));
        }
        rows.Reverse(); // 改为时间正序
        return rows;
    }

    /// <summary>获取某月的签到统计汇总（账号维度）：每个账号在该月签了多少天、总积分。</summary>
    public List<MonthlyAccountStatDto> GetMonthlyStats(int year, int month)
    {
        var result = new List<MonthlyAccountStatDto>();
        var prefix = $"{year:D4}-{month:D2}";
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT account_id, account_name,
                   COUNT(DISTINCT date) as signed_days,
                   SUM(credits) as total_credits
            FROM checkin_record
            WHERE date LIKE $prefix
            GROUP BY account_id
            ORDER BY total_credits DESC;
            """;
        cmd.Parameters.AddWithValue("$prefix", prefix + "%");

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new MonthlyAccountStatDto
            {
                AccountId = reader.GetString(0),
                AccountName = reader.GetString(1),
                SignedDays = reader.GetInt32(2),
                TotalCredits = reader.GetDouble(3),
            });
        }
        return result;
    }

    // ==================== 旧数据迁移 ====================

    /// <summary>从旧版 history_*.txt 导入数据到数据库（幂等：按日期+账号+积分去重）。</summary>
    public int MigrateFromHistoryFiles(string historyDir)
    {
        int imported = 0;
        try
        {
            var files = Directory.GetFiles(historyDir, "history_*.txt")
                .Where(f => !System.Text.RegularExpressions.Regex.IsMatch(
                    Path.GetFileName(f), @"^history_[0-9a-fA-F]{32}\.txt$")); // 跳过旧版 per-account 文件

            foreach (var file in files)
            {
                foreach (var line in File.ReadAllLines(file))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var parts = line.Split('|', StringSplitOptions.TrimEntries);
                    if (parts.Length < 4) continue;

                    // 解析：yyyy-MM-dd HH:mm | name | 每日签到 | +150
                    if (!DateTime.TryParse(parts[0], out var dt)) continue;
                    var name = parts[1];
                    var creditsStr = parts[3].TrimStart('+');
                    if (!double.TryParse(creditsStr, out var credits) || credits <= 0) continue;

                    // 用 name 当 accountId（旧数据没有 accountId，用 name 去重）
                    // 注意：真实签到行 account_id 是账号 Guid、account_name 是昵称，故须按「日期+昵称」判重，
                    // 否则会与真实账号行重复导入（启动迁移每次都会重新加回）。
                    if (NameExistsOnDate(dt.ToString("yyyy-MM-dd"), name))
                        continue;

                    InsertCheckin(dt, name, name, credits, false);
                    imported++;
                }
            }
        }
        catch { /* 迁移失败不影响正常使用 */ }
        return imported;
    }

    /// <summary>从旧版 credits_total_*.txt 导入快照数据。</summary>
    public int MigrateFromSnapshotFiles(string dataDir)
    {
        int imported = 0;
        try
        {
            foreach (var file in Directory.GetFiles(dataDir, "credits_total_*.txt"))
            {
                // 从文件名提取 accountId：credits_total_{accountId}.txt
                var accountId = Path.GetFileNameWithoutExtension(file)
                    .Replace("credits_total_", "");

                foreach (var line in File.ReadAllLines(file))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var parts = line.Split(',');
                    if (parts.Length < 2) continue;
                    if (!DateTime.TryParse(parts[0], out var dt)) continue;
                    if (!double.TryParse(parts[1], out var remaining)) continue;

                    // 直接 INSERT OR REPLACE
                    using var cmd = _conn.CreateCommand();
                    cmd.CommandText = """
                        INSERT INTO credits_snapshot (date, account_id, remaining)
                        VALUES ($date, $accountId, $remaining)
                        ON CONFLICT(date, account_id) DO UPDATE SET remaining = $remaining;
                        """;
                    cmd.Parameters.AddWithValue("$date", dt.ToString("yyyy-MM-dd"));
                    cmd.Parameters.AddWithValue("$accountId", accountId);
                    cmd.Parameters.AddWithValue("$remaining", remaining);
                    cmd.ExecuteNonQuery();
                    imported++;
                }
            }
        }
        catch { /* 迁移失败不影响 */ }
        return imported;
    }

    /// <summary>判断某账号昵称在指定日期是否已有签到记录（避免旧文件以昵称当 id 与真实账号行重复导入）。</summary>
    private bool NameExistsOnDate(string date, string accountName)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM checkin_record
            WHERE date = $date AND account_name = $accountName;
            """;
        cmd.Parameters.AddWithValue("$date", date);
        cmd.Parameters.AddWithValue("$accountName", accountName);
        return (long)cmd.ExecuteScalar()! > 0;
    }

    public void Dispose()
    {
        _conn?.Dispose();
    }

    // ==================== DTO ====================

    public class CheckinRecordDto
    {
        public string Date { get; set; } = "";
        public string Time { get; set; } = "";
        public string AccountName { get; set; } = "";
        public double Credits { get; set; }
        public bool IsMember { get; set; }
    }

    public class DailyTotalDto
    {
        public string Date { get; set; } = "";
        public double TotalCredits { get; set; }
        public int CheckinCount { get; set; }
    }

    public class AccountDailyDto
    {
        public string AccountId { get; set; } = "";
        public string AccountName { get; set; } = "";
        public double TotalCredits { get; set; }
    }

    public class MonthlyAccountStatDto
    {
        public string AccountId { get; set; } = "";
        public string AccountName { get; set; } = "";
        public int SignedDays { get; set; }
        public double TotalCredits { get; set; }
    }
}
