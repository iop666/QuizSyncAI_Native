using System.Text.Json.Nodes;
using QuizSync.Server.Core.Protocol;
using QuizSync.Server.Core.Ws;

namespace QuizSync.Server.Core.Tasks;

/// <summary>
/// 生产识别器：**把作业派发给 Windows 客户端的 Provider 角色，等它回包**。
///
/// Server 自己不做 AI（钥匙串在客户端上），所以这里没有「兜底识别」这条路：
///
/// | 情况 | 结果 |
/// |---|---|
/// | 没有 Provider 在线 | **立刻**失败 `provider_offline`（不挂死、**绝不回退 fixture**） |
/// | 等到 `deadline_ms` 还没回 | `ai_timeout` |
/// | 派发中 Provider 掉线 | `provider_offline`（不等自然超时） |
/// | Provider 回 `ok:false` | 用它给的 `error_code`；**不在协议 v1 码表里就落成 `internal`** |
/// | Provider 回 `ok:true` 但 `questions` 为空 | `no_question_found` |
/// | 回包里题目字段缺项 | 按协议列默认值补（`type` 缺 → `unknown`，`confidence` 缺 → 0.5），**不做映射/白名单** |
///
/// 为什么「没有 Provider 在线」必须立刻失败而不是排队等：手机端提交后 90 秒才报错，用户会以为
/// 主机在忙；而真相是**电脑端根本没开**。立刻回错才能让端侧给出「请在电脑上打开并连接」的文案。
/// </summary>
public sealed class ProviderAnalyzer(
    ProviderChannel providers, IClock clock, int timeoutMs = ProviderAnalyzer.DefaultTimeoutMs) : IAnalyzer
{
    /// <summary>协议冻结的默认等回包时限（`recognition_job.deadline_ms`）。</summary>
    public const int DefaultTimeoutMs = 90_000;

    /// <summary>
    /// 协议 v1 的失败码表（`docs/protocol.md` §3.4）。Provider 回的码不在这张表里就落成 `internal`：
    /// 否则手机会拿到一个它不认识的码，端侧按码分支的文案全部落空。
    /// </summary>
    private static readonly HashSet<string> KnownErrorCodes = new(StringComparer.Ordinal)
    {
        "ai_timeout", "ai_auth", "ai_rate_limited", "ai_bad_response", "ai_quota_exceeded",
        "no_question_found", "internal",
    };

    private readonly ProviderChannel _providers = providers;
    private readonly IClock _clock = clock;

    public int TimeoutMs { get; } = timeoutMs;

    public async Task<AnalyzeOutcome> AnalyzeAsync(AnalyzeRequest request, CancellationToken ct)
    {
        var jobId = Guid.NewGuid().ToString();
        var job = new JsonObject
        {
            ["type"] = "recognition_job",
            ["job_id"] = jobId,
            ["task_id"] = request.TaskId,
            ["session_id"] = request.SessionId,
            ["collection_id"] = request.CollectionId,
            ["image_hashes"] = new JsonArray([.. request.ImageHashes.Select(h => (JsonNode)JsonValue.Create(h)!)]),
            ["deadline_ms"] = TimeoutMs,
            ["created_at"] = _clock.NowMs,
        };

        var outcome = await _providers.DispatchAsync(job, jobId, TimeoutMs, ct).ConfigureAwait(false);
        return outcome.Status switch
        {
            DispatchStatus.NoProvider => Fail(
                ApiError.ProviderOffline, "电脑端没有在线的识别 Provider（请打开电脑端并确认已连接）"),
            DispatchStatus.Timeout => Fail(
                "ai_timeout", $"Provider {TimeoutMs} ms 内没有回包"),
            DispatchStatus.Disconnected => Fail(
                ApiError.ProviderOffline, "Provider 在识别过程中断开"),
            _ => ParseReply(outcome.Frame),
        };
    }

    private static AnalyzeOutcome Fail(string code, string message) => new([], ErrorCode: code, ErrorMessage: message);

    /// <summary>解析 `recognition_result`。字段名就是协议 `questions` 表的列名（不做改名、不做映射）。</summary>
    private static AnalyzeOutcome ParseReply(JsonObject? frame)
    {
        if (frame is null)
        {
            return Fail(ApiError.Internal, "Provider 回包不是 JSON 对象");
        }

        if (!OptionalBool(frame, "ok"))
        {
            var reported = frame["error_code"]?.ToString();
            var code = reported is not null && KnownErrorCodes.Contains(reported) ? reported : ApiError.Internal;
            var message = frame["error_message"]?.ToString();
            if (string.IsNullOrWhiteSpace(message))
            {
                message = reported is null ? "Provider 报告失败但没给原因" : $"Provider 报告失败：{reported}";
            }

            return Fail(code, message);
        }

        var questions = ParseQuestions(frame["questions"]);
        if (questions.Count == 0)
        {
            return Fail("no_question_found", "Provider 没有识别到题目");
        }

        return new AnalyzeOutcome(
            questions,
            FromCache: OptionalBool(frame, "from_cache"),
            AiProvider: frame["ai_provider"]?.ToString(),
            AiModel: frame["ai_model"]?.ToString(),
            // 这是 Provider 自己那一次 AI 调用的耗时（不是主机等回包的耗时），照它报的记。
            LatencyMs: OptionalInt(frame, "latency_ms"));
    }

    private static List<AnalyzedQuestion> ParseQuestions(JsonNode? node)
    {
        var questions = new List<AnalyzedQuestion>();
        if (node is not JsonArray array)
        {
            return questions;
        }

        for (var index = 0; index < array.Count; index++)
        {
            if (array[index] is not JsonObject item)
            {
                continue;
            }

            questions.Add(new AnalyzedQuestion(
                // Provider 从 0 起给；缺项时用数组下标（数组本身就是顺序，不另发明一套序号）。
                // 会话里已有题目时由 TaskService 统一顺延（baseOrdinal），这里一律 0 起。
                Ordinal: OptionalInt(item, "ordinal") ?? index,
                Stem: item["stem"]?.ToString() ?? string.Empty,
                // `type` **原样落库**：协议取值 single|multi|judge|blank|subjective，列上没有 CHECK，
                // 映射一层只会多一个出错的地方。缺项/空串时用 schema 的列默认值 unknown。
                Type: TextOr(item, "type", "unknown"),
                AnswerText: Text(item, "answer_text"),
                Analysis: item["analysis"]?.ToString() ?? string.Empty,
                Confidence: OptionalDouble(item, "confidence") ?? 0.5,
                QuestionNo: Text(item, "question_no"),
                Material: item["material"]?.ToString() ?? string.Empty,
                // 这三个是**JSON 文本列**（手机端自己解析 `options_json` 做标绿），
                // 所以原样透传字符串，不在服务端解析再序列化。
                OptionsJson: Text(item, "options_json"),
                ChoiceJson: Text(item, "choice_json"),
                NeedReview: OptionalInt(item, "need_review") ?? 0,
                AnswerInImage: OptionalInt(item, "answer_in_image") ?? 0,
                Incomplete: OptionalInt(item, "incomplete") ?? 0,
                AnswerGuessed: OptionalInt(item, "answer_guessed") ?? 0,
                WarningsJson: Text(item, "warnings_json")));
        }

        return questions;
    }

    /// <summary>字符串字段：非字符串一律当缺项（**不抛异常**：Provider 的一个坏字段不该让整单失败）。</summary>
    private static string? Text(JsonObject obj, string key) =>
        obj[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string TextOr(JsonObject obj, string key, string fallback)
    {
        var text = Text(obj, key);
        return string.IsNullOrEmpty(text) ? fallback : text;
    }

    private static bool OptionalBool(JsonObject obj, string key) =>
        obj[key] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    private static int? OptionalInt(JsonObject obj, string key)
    {
        if (obj[key] is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<int>(out var number))
        {
            return number;
        }

        if (value.TryGetValue<long>(out var wide))
        {
            return (int)Math.Clamp(wide, int.MinValue, int.MaxValue);
        }

        return value.TryGetValue<double>(out var real)
            ? (int)Math.Clamp(real, int.MinValue, int.MaxValue)
            : null;
    }

    private static double? OptionalDouble(JsonObject obj, string key)
    {
        if (obj[key] is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<double>(out var real))
        {
            return real;
        }

        return value.TryGetValue<long>(out var wide) ? wide : null;
    }
}
