using QuizSync.Server.Core.Storage;

namespace QuizSync.Server.Core.Auth;

public enum AuthStatus
{
    /// <summary>没有 Authorization 头（或没有 Bearer 令牌）。</summary>
    Missing,

    /// <summary>令牌认不出（或不是 64 位十六进制那种形态）。</summary>
    Invalid,

    /// <summary>令牌有效，但设备已吊销。</summary>
    Revoked,

    Valid,
}

public sealed record AuthResult(AuthStatus Status, DeviceRecord? Device)
{
    public string? DeviceId => Device?.DeviceId;
}

/// <summary>
/// Bearer 令牌鉴权：`Authorization: Bearer &lt;token&gt;` → `sha256(token)` 查设备表。
///
/// 令牌**明文从不落库**，只在配对响应里出现一次（见 <c>spec/03-auth-pairing.md</c>）。
/// `Bearer ` 前缀区分大小写（v1 行为）；头名按不区分大小写读取。
/// </summary>
public sealed class BearerAuthenticator(DeviceRepository devices)
{
    private readonly DeviceRepository _devices = devices;

    public AuthResult Authenticate(string? authorizationHeader)
    {
        var token = ParseBearer(authorizationHeader);
        if (token is null)
        {
            return new AuthResult(AuthStatus.Missing, null);
        }

        if (token.Length != 64 || !Tokens.IsImageHash(token))
        {
            // 形态不对就不查库：令牌一定是 64 位小写十六进制。
            return new AuthResult(AuthStatus.Invalid, null);
        }

        var device = _devices.FindByTokenHash(Tokens.Sha256Hex(token));
        if (device is null)
        {
            return new AuthResult(AuthStatus.Invalid, null);
        }

        return device.IsRevoked
            ? new AuthResult(AuthStatus.Revoked, device)
            : new AuthResult(AuthStatus.Valid, device);
    }

    /// <summary>取出 Bearer 令牌；没有头 / 不是 Bearer / 令牌为空都返回 null（= Missing）。</summary>
    public static string? ParseBearer(string? header)
    {
        if (string.IsNullOrEmpty(header))
        {
            return null;
        }

        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var token = header[prefix.Length..].Trim();
        return token.Length == 0 ? null : token;
    }
}
