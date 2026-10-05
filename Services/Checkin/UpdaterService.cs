using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace TraeTools.Services;

/// <summary>
/// 应用内自更新：从 GitHub Releases 查询最新正式版，下载 exe 到本地，
/// 并通过一个独立 PowerShell 脚本在旧进程退出后自动覆盖替换并重启新版。
/// </summary>
public static class UpdaterService
{
    public const string Repo = "star620/TraeTools";
    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        // GitHub REST API 对缺 User-Agent 的匿名请求会 403，必须带上
        h.DefaultRequestHeaders.UserAgent.ParseAdd($"TraeTools/{CurrentVersion}");
        return h;
    }

    /// <summary>运行中程序的版本号。统一从 AppVersion（version.json 优先，InformationalVersion 兜底）读取，
    /// 与"关于"页展示保持同源，避免出现"显示 v1.0.2.2 / 却判 v1.0.2.0 为旧"的错位。</summary>
    public static Version CurrentVersion
    {
        get
        {
            var v = AppVersion.Version?.Trim() ?? "";
            // GitHub tag 常见形态："v1.0.2.2" / "1.0.2.2" / "1.0.2.2-beta"。先把前导 v 去掉。
            if (v.Length > 0 && (v[0] == 'v' || v[0] == 'V')) v = v[1..];
            // 仅取主版本号段（-xxx 后缀截断）
            var dash = v.IndexOfAny(new[] { '-', '+' });
            if (dash >= 0) v = v[..dash];
            return Version.TryParse(v, out var ver) ? ver : new Version(1, 0, 0);
        }
    }

    public sealed record ReleaseInfo(string Tag, Version Version, string ExeUrl, long Size);

    // GitHub 匿名 API 每小时最多 60 次；自动检查若紧跟频繁启动会很快耗尽配额（返回 403）。
    // 用 1 小时磁盘缓存：自动（静默）检查在窗口内直接沿用上次结论，不反复打 API。
    private static readonly TimeSpan CacheWindow = TimeSpan.FromHours(1);
    private static string CacheFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TraeTools", "update_check_cache.txt");

    /// <summary>远程 release 中发现比本地更新的版本时返回其信息；否则返回 null。</summary>
    public static async Task<ReleaseInfo?> GetNewerAsync(CancellationToken ct = default, bool useCache = true)
    {
        // 自动检查 + 1 小时内刚查过 → 不联网，沿用"已是最新"结论
        if (useCache && IsCacheFresh()) return null;
        try
        {
            return await FetchLatestAsync(ct);
        }
        finally
        {
            TouchCache();   // 无论成败都记录本次检查，避免高频启动反复打 API
        }
    }

    private static bool IsCacheFresh()
    {
        try
        {
            if (!File.Exists(CacheFile)) return false;
            var ts = long.Parse(File.ReadAllText(CacheFile).Trim());
            if (ts <= 0) return false;
            var last = new DateTime(ts, DateTimeKind.Utc);
            return DateTime.UtcNow - last < CacheWindow;
        }
        catch { return false; }
    }

    private static void TouchCache()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CacheFile)!);
            File.WriteAllText(CacheFile, DateTime.UtcNow.Ticks.ToString());
        }
        catch { /* 缓存写失败不影响更新功能 */ }
    }

    private static async Task<ReleaseInfo?> FetchLatestAsync(CancellationToken ct)
    {
        using var resp = await Http.GetAsync(
            $"https://api.github.com/repos/{Repo}/releases/latest",
            HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            if ((int)resp.StatusCode == 403 &&
                resp.Headers.TryGetValues("X-RateLimit-Reset", out var resetVals) &&
                long.TryParse(resetVals.FirstOrDefault(), out var resetUnix))
            {
                var wait = DateTimeOffset.FromUnixTimeSeconds(resetUnix).ToLocalTime();
                throw new HttpRequestException(
                    $"GitHub 更新限流（每小时最多 60 次），请到 {wait:HH:mm} 后再检查更新。");
            }
            throw new HttpRequestException($"更新检查失败：HTTP {(int)resp.StatusCode}");
        }
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("tag_name", out var tag) ||
            !TryParseTag(tag.GetString() ?? "", out var ver))
            return null;
        if (ver <= CurrentVersion) return null;   // 本地已是最新（>= 远程）

        string? exeUrl = null; long size = 0;
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var a in assets.EnumerateArray())
            {
                if (string.Equals(a.GetProperty("name").GetString(), "TraeTools.exe", StringComparison.OrdinalIgnoreCase))
                {
                    exeUrl = a.GetProperty("browser_download_url").GetString();
                    size = a.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                    break;
                }
            }
        }
        return exeUrl == null ? null : new ReleaseInfo(tag.GetString()!, ver, exeUrl, size);
    }

    /// <summary>把 "v1.2.3" / "1.2.3" / "1.2.3-beta" 解析为 Version；失败返回 false。</summary>
    public static bool TryParseTag(string tag, out Version ver)
    {
        ver = new Version();
        var s = tag.Trim();
        if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V')) s = s[1..];
        var parts = s.Split('.');
        if (parts.Length < 2) return false;
        var items = new int[4];
        for (var i = 0; i < 4; i++)
        {
            var part = i < parts.Length ? parts[i].Split('-', '+')[0] : "0";
            if (!int.TryParse(part, out items[i])) return false;
        }
        ver = new Version(items[0], items[1], items[2], items[3]);
        return true;
    }

    /// <summary>下载 exe 到 destDir\TraeTools.update.exe 并返回完整路径；sink 用于汇报进度。</summary>
    public static async Task<string> DownloadAsync(string url, string destDir,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(destDir);
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? 0;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        var dest = Path.Combine(destDir, "TraeTools.update.exe");
        await using var fs = File.Create(dest);
        var buf = new byte[81920];
        long done = 0; int read;
        while ((read = await src.ReadAsync(buf.AsMemory(0, buf.Length), ct)) > 0)
        {
            await fs.WriteAsync(buf.AsMemory(0, read), ct);
            done += read;
            if (total > 0) progress?.Report((int)(done * 100 / total));
        }
        return dest;
    }

    /// <summary>
    /// 用独立 PowerShell 进程异步应用更新：反复重试直到旧进程退出（文件解锁）后
    /// 覆盖目标 exe、清理临时 update，并启动新版。
    /// </summary>
    public static void ApplyInBackground(string updateExe, string targetExe)
    {
        var ps1 = Path.Combine(Path.GetTempPath(), $"apply_update_{Guid.NewGuid():N}.ps1");
        var body = $@"
`$src = ""{updateExe}""
`$dst = ""{targetExe}""
`$tries = 60
for (`$i = 0; `$i -lt `$tries; `$i++) {{
  try {{ Copy-Item -Path `$src -Destination `$dst -Force -ErrorAction Stop; break }}
  catch {{ Start-Sleep -Milliseconds 200 }}
}}
Remove-Item -Path `$src -Force -ErrorAction SilentlyContinue
Start-Process -FilePath `$dst
Remove-Item -Path `$MyInvocation.MyCommand.Path -Force -ErrorAction SilentlyContinue
";
        File.WriteAllText(ps1, body, new UTF8Encoding(true));

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{ps1}\"",
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
            UseShellExecute = false
        };
        Process.Start(psi);
    }
}