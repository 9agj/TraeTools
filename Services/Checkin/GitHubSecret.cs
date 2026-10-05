using System.Text;
using Sodium;

namespace TraeCheckin;

/// <summary>
/// GitHub Actions secret 加密工具。
/// GitHub 要求用 libsodium 的 crypto_box_seal（X25519 + XSalsa20-Poly1305）
/// 对 secret 明文加密后再提交，本类用 Sodium.Core 的 SealedPublicKeyBox 实现。
/// </summary>
public static class GitHubSecret
{
    /// <summary>
    /// 用仓库公开密钥（base64 编码的 32 字节 X25519 公钥）加密 secret 明文，
    /// 返回 base64 编码的密文（可直接作为 PUT secret 的 encrypted_value）。
    /// </summary>
    public static string Encrypt(string secretValue, string base64PublicKey)
    {
        var publicKey = Convert.FromBase64String(base64PublicKey);
        var message = Encoding.UTF8.GetBytes(secretValue);
        var sealedBox = SealedPublicKeyBox.Create(message, publicKey);
        return Convert.ToBase64String(sealedBox);
    }
}
