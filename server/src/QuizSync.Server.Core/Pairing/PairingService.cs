using System.Text.Json.Nodes;
using QuizSync.Server.Core.Auth;
using QuizSync.Server.Core.Protocol;
using QuizSync.Server.Core.Storage;

namespace QuizSync.Server.Core.Pairing;

/// <summary>配对请求（`spec/03-auth-pairing.md` §3）。</summary>
public sealed record PairRequest(
    string? Code, string? DeviceId, string? DeviceName, string? Platform, string? AppVersion)
{
    public static PairRequest FromJson(JsonNode? node)
    {
        var obj = node as JsonObject;
        return new PairRequest(
            Code: obj?["code"]?.GetValue<string>(),
            DeviceId: obj?["device_id"]?.GetValue<string>(),
            DeviceName: obj?["device_name"]?.GetValue<string>(),
            Platform: obj?["platform"]?.GetValue<string>(),
            AppVersion: obj?["app_version"]?.GetValue<string>());
    }

    /// <summary>四个必填字段都非空才继续（`app_version` 不校验，与 v1 一致）。</summary>
    public bool IsValid =>
        !string.IsNullOrEmpty(Code) &&
        !string.IsNullOrEmpty(DeviceId) &&
        !string.IsNullOrEmpty(DeviceName) &&
        !string.IsNullOrEmpty(Platform);
}

/// <summary>配对结果：要么签发令牌，要么是错误（错误体恒为 `ApiError` 形态）。</summary>
public sealed record PairOutcome(int Status, string? Token, string? DeviceId, bool AlreadyPaired, JsonObject Body);

/// <summary>
/// 配对状态机（v1 语义，逐条对 <c>spec/03-auth-pairing.md</c>）：
///
/// 1. **锁定**优先：锁定期内一律 429 `rate_limited`（**连正确的码也拒**）；
/// 2. 滑窗：每分钟最多 5 次尝试，**成功也计数**；
/// 3. 码错误 → 401 `invalid_code`（失败计数 +1，累计 10 次 → 锁 60 秒并清零计数）；
/// 4. 码过期（**码比对之后**才判）→ 401 `code_expired`，`retry_after_seconds` =
///    已过期秒数（clamp 0–60，注意这不是「多久后可重试」）；
/// 5. 同 `device_id` 重新配对 → **409 + `already_paired: true`，但照样发新令牌**
///    （旧令牌立刻作废）；
/// 6. 失败计数与锁定**落库**（`settings` 表），重启不清零。
/// </summary>
public sealed class PairingService
{
    public const string SettingFailCount = "pair_fail_count";
    public const string SettingLockedUntil = "pair_locked_until";

    private readonly DeviceRepository _devices;
    private readonly IClock _clock;
    private readonly PairingOptions _options;
    private readonly Lock _gate = new();
    private readonly Queue<long> _attempts = new();

    private string _code;
    private long _expiresAt;
    private int _failCount;
    private long _lockedUntil;

    public PairingService(DeviceRepository devices, IClock clock, PairingOptions options)
    {
        _devices = devices;
        _clock = clock;
        _options = options;
        _code = Tokens.NewPairingCode();
        _expiresAt = clock.NowMs + options.TtlMs;
        LoadGuard();
    }

    /// <summary>当前配对码（UI 展示 / 二维码用）。</summary>
    public string Code
    {
        get
        {
            lock (_gate)
            {
                return _code;
            }
        }
    }

    public long ExpiresAtMs
    {
        get
        {
            lock (_gate)
            {
                return _expiresAt;
            }
        }
    }

    /// <summary>刷新配对码（UI 一键刷新）：换码并**重置有效期**。</summary>
    public string RefreshCode()
    {
        lock (_gate)
        {
            _code = Tokens.NewPairingCode();
            _expiresAt = _clock.NowMs + _options.TtlMs;
            return _code;
        }
    }

    public PairOutcome Pair(PairRequest request)
    {
        var now = _clock.NowMs;

        lock (_gate)
        {
            // 1) 锁定期。
            if (now < _lockedUntil)
            {
                return Failure(429, ApiError.RateLimited, "失败次数过多已锁定",
                    (int)Math.Ceiling((_lockedUntil - now) / 1000.0));
            }

            // 2) 滑窗（先清 60 秒前的记录，再判上限）。
            while (_attempts.Count > 0 && now - _attempts.Peek() > _options.WindowMs)
            {
                _attempts.Dequeue();
            }

            if (_attempts.Count >= _options.AttemptsPerWindow)
            {
                var retryAfter = (int)Math.Ceiling((_options.WindowMs - (now - _attempts.Peek())) / 1000.0);
                return Failure(429, ApiError.RateLimited, "尝试过于频繁", retryAfter);
            }

            _attempts.Enqueue(now);

            // 3) 码比对。
            if (!string.Equals(request.Code, _code, StringComparison.Ordinal))
            {
                return Failure(401, ApiError.InvalidCode, "配对码错误", null, countFailure: true);
            }

            // 4) 过期（在码比对之后判，与 v1 一致）。
            if (now > _expiresAt)
            {
                return Failure(401, ApiError.CodeExpired, "配对码已过期",
                    (int)Math.Clamp((now - _expiresAt) / 1000, 0, 60), countFailure: true);
            }

            // 5) 发令牌。
            var existing = _devices.Find(request.DeviceId!);
            var token = Tokens.NewToken();
            _devices.Upsert(new DeviceRecord(
                DeviceId: request.DeviceId!,
                Name: request.DeviceName!,
                Platform: request.Platform!,
                TokenHash: Tokens.Sha256Hex(token),
                PairedAt: existing?.PairedAt ?? now,
                LastSeenAt: now,
                RevokedAt: null,
                AppVersion: request.AppVersion));

            _failCount = 0;
            PersistGuard();

            var body = new JsonObject
            {
                ["token"] = token,
                ["server_device_id"] = _options.ServerDeviceId,
                ["server_name"] = _options.ServerName,
                ["protocol_version"] = Protocol.ProtocolVersion.V1,
            };

            if (existing is not null)
            {
                body["already_paired"] = true;
            }

            return new PairOutcome(existing is not null ? 409 : 200, token, request.DeviceId, existing is not null, body);
        }
    }

    private PairOutcome Failure(int status, string code, string message, int? retryAfter, bool countFailure = false)
    {
        if (countFailure)
        {
            _failCount++;
            if (_failCount >= _options.FailureLockThreshold)
            {
                _lockedUntil = _clock.NowMs + _options.LockoutMs;
                _failCount = 0;
            }

            PersistGuard();
        }

        return new PairOutcome(status, null, null, false, ApiError.Body(code, message, retryAfter));
    }

    private void LoadGuard()
    {
        // M47：失败计数与锁定期跨重启保留（否则「连错十次锁一分钟」重启即可绕过）。
        // 锁定已过期时两者一起归零（与 Dart 版一致）。
        var lockedUntil = ParseLong(_devices.GetSetting(SettingLockedUntil));
        if (lockedUntil > _clock.NowMs)
        {
            _lockedUntil = lockedUntil;
            _failCount = (int)ParseLong(_devices.GetSetting(SettingFailCount));
        }
        else
        {
            _lockedUntil = 0;
            _failCount = 0;
        }
    }

    private void PersistGuard()
    {
        _devices.SetSetting(SettingFailCount, _failCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _devices.SetSetting(SettingLockedUntil, _lockedUntil.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static long ParseLong(string? text) =>
        long.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : 0;
}

/// <summary>配对的数值口径（默认值 = 协议冻结值，见 `spec/03-auth-pairing.md` §10）。</summary>
public sealed record PairingOptions
{
    public long TtlMs { get; init; } = 5 * 60 * 1000;

    public long WindowMs { get; init; } = 60 * 1000;

    public int AttemptsPerWindow { get; init; } = 5;

    public int FailureLockThreshold { get; init; } = 10;

    public long LockoutMs { get; init; } = 60 * 1000;

    public required string ServerDeviceId { get; init; }

    public required string ServerName { get; init; }
}
