using System.Text.Json;

namespace TraeCheckin;

/// <summary>
/// 登录 token 判定工具。
/// 用于避免登录框误读 localStorage 中残留的失效 token。
/// </summary>
public static class TokenUtils
{
    /// <summary>
    /// 判断是否接受当前读到的 token。
    /// 仅当「当前 token 有效（非空且长度足够）」且「与初始 token 不同」时返回 true，
    /// 即真正重新登录产生了新 token。
    /// </summary>
    public static bool ShouldAcceptNewToken(string? initialToken, string? currentToken)
    {
        if (string.IsNullOrEmpty(currentToken) || currentToken.Length < 40) return false;
        return !string.Equals(initialToken, currentToken, StringComparison.Ordinal);
    }

    /// <summary>
    /// 从 JWT（Cloud-IDE-Token）payload 的 data.id 中解析账号唯一标识。
    /// data.id 是 16 位数字账号 ID，跨登录会话恒定；解析失败或缺失返回 null。
    /// </summary>
    public static string? ParseAccountUid(string? jwt)
    {
        if (string.IsNullOrEmpty(jwt)) return null;
        var parts = jwt.Split('.');
        if (parts.Length < 2) return null;
        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        switch (payload.Length % 4)
        {
            case 2: payload += "=="; break;
            case 3: payload += "="; break;
        }
        try
        {
            var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var data) &&
                data.ValueKind == JsonValueKind.Object &&
                data.TryGetProperty("id", out var id) &&
                id.ValueKind == JsonValueKind.String)
            {
                var uid = id.GetString();
                return string.IsNullOrEmpty(uid) ? null : uid;
            }
        }
        catch
        {
            // 解析失败（非 JWT 或格式异常）视为无法识别
        }
        return null;
    }
}
