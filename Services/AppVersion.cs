using System;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace TraeTools.Services;

/// <summary>
/// 应用版本信息读取（版本号为单一来源 = version.json）。
///
/// 读取优先级：
///   1) exe 内嵌的 version.json 资源（单文件/自包含发布时 exe 自带真实版本，天然一致）
///   2) exe 同目录的外置 version.json（用于小版本热修/临时覆盖，可选）
///   3) 程序集 InformationalVersion（编译时由 csproj 从 version.json 同步，最后兜底）
/// </summary>
public static class AppVersion
{
    private static readonly Lazy<AppVersionInfo> _info = new(Load);

    public static string Display => _info.Value.Display;
    public static string Tag => _info.Value.Tag;
    public static string Version => _info.Value.Version;
    public static string Channel => _info.Value.Channel;
    public static string BuildDate => _info.Value.BuildDate;
    public static DateTime? BuildDateTime => _info.Value.BuildDateTime;

    private static AppVersionInfo Load()
    {
        // 1) 优先读取 exe 内嵌的 version.json 资源（发布时与 csproj 同步，保证单文件分发版本正确）
        try
        {
            var asm = Assembly.GetEntryAssembly();
            if (asm != null)
            {
                var resName = Array.Find(asm.GetManifestResourceNames(), r => r.EndsWith(".version.json", StringComparison.OrdinalIgnoreCase));
                if (resName != null)
                {
                    using var stream = asm.GetManifestResourceStream(resName);
                    if (stream != null)
                    {
                        using var reader = new StreamReader(stream);
                        var parsed = ParseJson(reader.ReadToEnd());
                        if (parsed != null) return parsed;
                    }
                }
            }
        }
        catch { /* 内嵌读取失败则回退下一步 */ }

        // 2) 读取 exe 同目录的外置 version.json（可选热修覆盖）
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "version.json");
            if (File.Exists(path))
            {
                var parsed = ParseJson(File.ReadAllText(path));
                if (parsed != null) return parsed;
            }
        }
        catch { /* JSON 解析失败回退程序集版本 */ }

        // 3) 回退到程序集 InformationalVersion
        var asmVer = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "1.0.0";
        return new AppVersionInfo(asmVer, "stable", "", null);
    }

    /// <summary>解析 version.json 文本；version 缺失/空则返回 null 表示不可用。</summary>
    private static AppVersionInfo? ParseJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var version = root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(version)) return null;
        var channel = root.TryGetProperty("channel", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString() ?? "stable" : "stable";
        var buildDate = root.TryGetProperty("buildDate", out var d) && d.ValueKind == JsonValueKind.String
            ? d.GetString() ?? "" : "";
        DateTime? buildDt = null;
        if (DateTime.TryParse(buildDate, out var parsed)) buildDt = parsed;
        return new AppVersionInfo(version, channel, buildDate, buildDt);
    }

    private sealed record AppVersionInfo(string Version, string Channel, string BuildDate, DateTime? BuildDateTime)
    {
        /// <summary>展示用：v1.0.2.2</summary>
        public string Display => $"v{Version}";

        /// <summary>Git tag 格式：v1.0.2.2</summary>
        public string Tag => $"v{Version}";
    }
}