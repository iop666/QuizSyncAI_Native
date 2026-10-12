using System.Text.Json.Nodes;
using QuizSync.Core;

namespace QuizSync.Provider;

/// <summary>
/// Provider ↔ 主机之间那几帧 JSON 的**唯一构造处**。
///
/// 为什么要把它们从 <see cref="ProviderService"/> 里抽出来：这几帧是**跨仓库的线协议**
/// （主机在 `QuizSyncServer`，字段名写在协议 `questions` 表的列上），
/// 而「键名写错一个」的症状是**界面上某功能像没做，全程没有任何报错** ——
/// 这一程已经因此吃过一次亏（`options_json` / `choice_json` 没落库 → 标绿与材料折叠全失效）。
/// 抽成纯函数之后，测试可以逐字段断言这帧 JSON，不用起 WebSocket、不用真 AI。
///
/// 帧的语义见主机仓库 README 的「Provider 派发」一节；`questions[]` 的形状由
/// <see cref="Question.ToProtocolJson"/> 决定（键名 = 协议 `questions` 表的列名）。
/// </summary>
public static class ProviderProtocol
{
    /// <summary>连上后的自述：主机靠 `role` 认出「这台会做识别」，靠后两个字段回答 `/info` 的 `ai_configured`。</summary>
    public static JsonObject Hello(AiConfig config) => new()
    {
        ["type"] = "hello",
        ["device_id"] = HostSession.ProviderDeviceId,
        ["app_version"] = "1.1.0-native",
        ["platform"] = "windows",
        ["role"] = "provider",
        ["ai_configured"] = AiSettings.IsUsable(config),
        ["ai_provider"] = config.ProviderId,
        ["ai_model"] = config.Model,
    };

    /// <summary>识别成功：题目数组 + 这次调用用的是哪个模型 / 命中缓存没有。</summary>
    public static JsonObject Result(string jobId, AiConfig config, AnalysisOutcome outcome) => new()
    {
        ["type"] = "recognition_result",
        ["job_id"] = jobId,
        ["ok"] = true,
        ["ai_provider"] = config.ProviderId,
        ["ai_model"] = config.Model,
        ["latency_ms"] = outcome.LatencyMs,
        ["from_cache"] = outcome.FromCache,
        ["questions"] = new JsonArray([.. outcome.Questions.Select(question => (JsonNode)question.ToProtocolJson())]),
    };

    /// <summary>
    /// 识别失败：**必须带真实错误码**。
    ///
    /// 为什么不「回一个空题目数组了事」：那会被主机记成 `no_question_found`（「没识别到题」），
    /// 而真因可能是 Key 过期或限流 —— 用户看到的是「这个软件识别不出来」，永远查不到真正的原因。
    /// 错误码取值与 `ai-contract.md` / 协议 §3.4 的码表一致。
    /// </summary>
    public static JsonObject Failure(string jobId, string errorCode, string message) => new()
    {
        ["type"] = "recognition_result",
        ["job_id"] = jobId,
        ["ok"] = false,
        ["error_code"] = errorCode,
        ["error_message"] = message,
    };
}
