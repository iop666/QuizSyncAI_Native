using System.Security.Cryptography;
using System.Text;

namespace QuizSync.Server.Core;

/// <summary>
/// 配对码与令牌的生成 / 校验。
///
/// 这些形态是**协议契约**（见 <c>spec/03-auth-pairing.md</c>）：
/// 配对码 6 位十进制、令牌 32 随机字节的 64 位小写十六进制、服务端只存
/// <c>sha256(令牌)</c>，明文令牌只在配对响应里出现一次。
/// </summary>
public static class Tokens
{
    public const int PairingCodeDigits = 6;
    public const int TokenBytes = 32;

    /// <summary>6 位十进制配对码（含前导零；用密码学随机源，不用 Random）。</summary>
    public static string NewPairingCode()
    {
        Span<byte> buf = stackalloc byte[4];
        RandomNumberGenerator.Fill(buf);
        var value = BitConverter.ToUInt32(buf) % 1_000_000u;
        return value.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>32 随机字节的十六进制（小写，64 位）。</summary>
    public static string NewToken()
    {
        Span<byte> buf = stackalloc byte[TokenBytes];
        RandomNumberGenerator.Fill(buf);
        return Convert.ToHexStringLower(buf);
    }

    public static string Sha256Hex(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public static string Sha256Hex(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>是不是规范形态的图片 hash（`^[0-9a-f]{64}$`）。</summary>
    public static bool IsImageHash(string value)
    {
        if (value.Length != 64)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        return true;
    }
}
