using System.Text.Json.Nodes;

namespace QuizSync.Server.Core;

/// <summary>
/// `/info` 的响应构造。
///
/// 两个版本形态**必须分开写**：v1 的字段名与取值是冻结契约（老客户端照着解析），
/// v2 可以增字段但不能改已有语义。见 <c>spec/04-http-api.md</c> 与
/// <c>spec/11-capabilities.md</c>。
/// </summary>
public static class ServerInfo
{
    /// <summary>v1 自述的能力清单（顺序不是契约，客户端只判存在性）。</summary>
    public static readonly string[] V1Capabilities =
        ["analyze", "sync", "image_fetch", "collections", "multipage"];

    /// <summary>
    /// v1 `/info`。
    ///
    /// `ai_configured` 与 `active_collection_*` **是参数，不是常量**：
    /// - `ai_configured` 以前恒等于启动参数，而 2.0 里钥匙串在 Windows 客户端上 ——
    ///   有没有配 AI 只有**在线的 Provider** 知道，所以有 Provider 时用它的自述值，
    ///   没有时退回启动参数（测试与一致性向量靠这个退回值保持绿）；
    /// - `active_collection_*` 以前硬编码 null。协议 §3.1 规定它为 null 时安卓端
    ///   **不得发起识别**，于是手机永远显示「请先在电脑上选择任务合集」—— 与真人
    ///   已经在电脑上选好合集的事实相反，是纯粹的假信息。
    /// </summary>
    public static JsonObject ForV1(
        ServerOptions options, bool aiConfigured, string? activeCollectionId, string? activeCollectionName) => new()
    {
        ["device_id"] = options.DeviceId,
        ["device_name"] = options.ServerName,
        ["platform"] = options.Platform,
        ["protocol_version"] = ServerOptions.V1ProtocolVersion,
        ["app_version"] = options.AppVersion,
        ["ai_configured"] = aiConfigured,
        ["active_collection_id"] = activeCollectionId,
        ["active_collection_name"] = activeCollectionName,
        ["capabilities"] = new JsonArray([.. V1Capabilities.Select(c => (JsonNode)JsonValue.Create(c)!)]),
    };

    /// <summary>
    /// v2 `/info`：v1 的字段 + v2 的加法字段。
    ///
    /// `provider_online` / `provider_model` 是**纯加法**：`ai_configured` 说的是「配没配 key」
    /// （能力），这两项说的是「现在有没有人在线」（进程状态）。分开是因为 `ai_configured`
    /// 在没有 Provider 时会退回启动参数，光看它分不清「在线且说没配 key」与「根本不在线」。
    /// **只加在 v2**：v1 的 `/info` 是冻结契约。
    /// </summary>
    public static JsonObject ForV2(
        ServerOptions options, bool aiConfigured, string? activeCollectionId, string? activeCollectionName,
        bool providerOnline, string? providerModel) => new()
    {
        ["device_id"] = options.DeviceId,
        ["device_name"] = options.ServerName,
        ["platform"] = options.Platform,
        ["protocol_version"] = ServerOptions.ProtocolVersion,
        ["app_version"] = options.AppVersion,
        ["ai_configured"] = aiConfigured,
        ["active_collection_id"] = activeCollectionId,
        ["active_collection_name"] = activeCollectionName,
        ["capabilities"] = new JsonArray([.. V1Capabilities.Select(c => (JsonNode)JsonValue.Create(c)!)]),
        // v2 新增：Host 明确自述「同时支持 v1 兼容层多久」，便于客户端规划升级。
        ["v1_compat"] = true,
        ["provider_online"] = providerOnline,
        ["provider_model"] = providerModel,
    };

    public static JsonObject Health(ServerOptions options, TimeSpan uptime) => new()
    {
        ["status"] = "ok",
        ["protocol_version"] = ServerOptions.ProtocolVersion,
        ["app_version"] = options.AppVersion,
        ["uptime_ms"] = (long)uptime.TotalMilliseconds,
    };
}
