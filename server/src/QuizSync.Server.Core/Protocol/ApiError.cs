using System.Text.Json.Nodes;

namespace QuizSync.Server.Core.Protocol;

/// <summary>
/// 统一的错误体：`{code, message, retry_after_seconds}`。
///
/// **`retry_after_seconds` 键恒在**（不适用时为 `null`），且**不用 `Retry-After` 头**
/// —— 这是 v1 冻结的形态（见 <c>spec/09-errors.md</c>）。客户端分支只看 `code`。
/// </summary>
public static class ApiError
{
    public const string Unauthorized = "unauthorized";
    public const string Revoked = "revoked";
    public const string VersionMismatch = "version_mismatch";
    public const string InvalidCode = "invalid_code";
    public const string CodeExpired = "code_expired";
    public const string RateLimited = "rate_limited";
    public const string InvalidRequest = "invalid_request";
    public const string TooManyPages = "too_many_pages";
    public const string NoActiveCollection = "no_active_collection";
    public const string QueueFull = "queue_full";
    public const string PayloadTooLarge = "payload_too_large";
    public const string NotFound = "not_found";
    public const string Internal = "internal";

    /// <summary>
    /// 派发失败：**没有 Provider 在线**（Windows 端没连、或连了但没自述 `role: "provider"`）。
    ///
    /// 这一码**不在协议 v1 的 `tasks`/`sessions.error_code` 码表里**（那张表是
    /// `ai_timeout|ai_auth|ai_rate_limited|ai_bad_response|ai_quota_exceeded|no_question_found|internal`）
    /// —— 它是 2.0 自己加的：主机不自己做 AI，没人在线时只能这么报。
    /// 老客户端会落到「未知错误」的默认文案，但**绝不退回 fixture 假装成功**：
    /// 让用户看到一条明确的失败，比让他看到「第 1 题（fixture）」这种胡话要好得多。
    /// </summary>
    public const string ProviderOffline = "provider_offline";

    public static JsonObject Body(string code, string message, int? retryAfterSeconds = null) => new()
    {
        ["code"] = code,
        ["message"] = message,
        ["retry_after_seconds"] = retryAfterSeconds,
    };
}
