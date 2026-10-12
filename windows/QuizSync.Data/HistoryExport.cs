using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using QuizSync.Core;

namespace QuizSync.Data;

/// <summary>
/// 导出用的**纯逻辑**：JSON 组装 + 日志脱敏 + 时间格式化（不碰文件系统、不弹对话框）。
///
/// **为什么放在 `QuizSync.Data` 而不是 `QuizSync.App`**：`QuizSync.Tests` 不引用 App
/// （那是 WinUI exe，`dotnet test` 不构建它）—— 放 App 就没有测试兜底。而这两件事都
/// **错了很难看**：导出的 JSON 结构一漂，下游脚本全废；日志里带出 API Key 或设备令牌，
/// 是安全红线（`SPEC.md` §2.3 明写「导出日志：不含 API Key 与 Token」）。
/// </summary>
public static partial class HistoryExport
{
    /// <summary>一条待导出的记录 = 会话 + 它的题目 + （可选）所属合集名。</summary>
    public sealed record Item(RecordedSession Session, IReadOnlyList<Question> Questions, string? Collection);

    /// <summary>毫秒时间戳 → 本地时间 `yyyy-MM-dd HH:mm`（与界面的 `UiText.FormatDateMinute` 同格式）。</summary>
    public static string FormatTime(long unixMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(unixMs)
            .ToLocalTime()
            .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>
    /// 单次识别的 Markdown（1.1.0 的「导出本次」）。合集 / 全部历史导出都复用它。
    ///
    /// `localDeviceId` 用来把「来源」写成「本机」而不是一条设备 id（`UiText.SourceLabel`）。
    /// </summary>
    public static string SessionMarkdown(RecordedSession session, IReadOnlyList<Question> questions, string localDeviceId)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(questions);

        var builder = new StringBuilder();
        builder.Append("# 识别结果 ").AppendLine(UiText.FormatDateMinute(session.CreatedAt));
        builder.AppendLine();
        builder.Append("- 状态：").AppendLine(UiText.Status(session.Status, dark: false).Label);
        builder.Append("- 来源：").AppendLine(UiText.SourceLabel(session.SourceDevice, localDeviceId));
        if (!string.IsNullOrWhiteSpace(session.AiModel))
        {
            builder.Append("- 模型：").AppendLine(session.AiModel);
        }

        builder.AppendLine();
        foreach (var question in questions)
        {
            builder.Append("## ").Append(question.Ordinal + 1).Append(". ");
            builder.AppendLine(string.IsNullOrWhiteSpace(question.QuestionNo) ? string.Empty : question.QuestionNo);
            builder.AppendLine();
            builder.AppendLine(question.Stem);
            builder.AppendLine();

            foreach (var option in question.Options)
            {
                var mark = question.Answer.MatchedOptionLabels(question.Options).Contains(option.Label) ? " ✅" : string.Empty;
                builder.Append("- ").Append(option.Label).Append(". ").Append(option.Text).AppendLine(mark);
            }

            var answer = !string.IsNullOrWhiteSpace(question.AnswerText)
                ? question.AnswerText!
                : string.Join(" ", question.Choice);
            builder.Append("**答案：**").AppendLine(string.IsNullOrWhiteSpace(answer) ? "（AI 未给出）" : answer);

            if (!string.IsNullOrWhiteSpace(question.Analysis))
            {
                builder.AppendLine();
                builder.Append("**解析：**").AppendLine(question.Analysis);
            }

            builder.AppendLine();
        }

        builder.AppendLine("---");
        builder.AppendLine();
        builder.AppendLine(UiText.Disclaimer);
        return builder.ToString();
    }

    /// <summary>合集导出：一条标题 + 逐条会话（按识别顺序，调用方排好序）。</summary>
    public static string CollectionMarkdown(
        string collectionName, IReadOnlyList<Item> items, string localDeviceId)
    {
        ArgumentNullException.ThrowIfNull(items);

        var builder = new StringBuilder();
        builder.Append("# ").AppendLine(collectionName);
        builder.AppendLine();
        builder.Append("共 ").Append(items.Count).AppendLine(" 条识别记录。");
        builder.AppendLine();

        foreach (var item in items)
        {
            builder.AppendLine(SessionMarkdown(item.Session, item.Questions, localDeviceId));
            builder.AppendLine();
        }

        return builder.ToString();
    }

    /// <summary>「导出全部历史」的 Markdown（含未分类；每条前面标出它属于哪个合集）。</summary>
    public static string AllHistoryMarkdown(
        IReadOnlyList<Item> items, string localDeviceId, long exportedAt)
    {
        ArgumentNullException.ThrowIfNull(items);

        var builder = new StringBuilder();
        builder.AppendLine("# QuizSync AI 识别历史");
        builder.AppendLine();
        builder.Append("导出时间：").AppendLine(FormatTime(exportedAt));
        builder.Append("共 ").Append(items.Count).AppendLine(" 条识别记录（按识别顺序）。");
        builder.AppendLine();

        foreach (var item in items)
        {
            if (!string.IsNullOrWhiteSpace(item.Collection))
            {
                builder.Append("> 合集：").AppendLine(item.Collection);
                builder.AppendLine();
            }

            builder.AppendLine(SessionMarkdown(item.Session, item.Questions, localDeviceId));
            builder.AppendLine();
        }

        return builder.ToString();
    }

    /// <summary>
    /// 「导出全部历史」的 JSON（`SPEC.md` §2.3 的 Markdown / JSON 两选一的后者）。
    ///
    /// 形状：`{ exported_at, app, session_count, sessions: [ { …, questions: [ … ] } ] }`。
    /// **空串与空数组一律照写**（不省略键）：读的人不用逐字段判 `null`，
    /// 这也是「形状稳定」比「体积小」更重要的地方。
    /// </summary>
    public static string AllHistoryJson(IReadOnlyList<Item> items, long exportedAt)
    {
        ArgumentNullException.ThrowIfNull(items);

        var sessions = new JsonArray();
        foreach (var item in items)
        {
            var session = item.Session;

            var questions = new JsonArray();
            foreach (var question in item.Questions)
            {
                questions.Add(new JsonObject
                {
                    ["ordinal"] = question.Ordinal,
                    ["question_no"] = question.QuestionNo,
                    ["type"] = question.Type.Wire(),
                    ["stem"] = question.Stem,
                    ["material"] = question.Material,
                    ["options"] = new JsonArray([.. question.Options.Select(option => (JsonNode)new JsonObject
                    {
                        ["label"] = option.Label,
                        ["text"] = option.Text,
                    })]),
                    ["choice"] = new JsonArray([.. question.Choice.Select(choice => (JsonNode)JsonValue.Create(choice))]),
                    ["answer_text"] = question.AnswerText,
                    ["analysis"] = question.Analysis,
                    ["confidence"] = question.Confidence,
                    ["need_review"] = question.NeedReview,
                    ["incomplete"] = question.Incomplete,
                    ["answer_guessed"] = question.AnswerGuessed,
                    ["answer_in_image"] = question.AnswerInImage,
                    ["warnings"] = new JsonArray([.. question.Warnings.Select(w => (JsonNode)JsonValue.Create(w))]),
                });
            }

            sessions.Add(new JsonObject
            {
                ["session_id"] = session.SessionId,
                ["collection"] = item.Collection,
                ["created_at"] = session.CreatedAt,
                ["created_at_local"] = FormatTime(session.CreatedAt),
                ["status"] = session.Status,
                ["source_device"] = session.SourceDevice,
                ["image_hash"] = session.ImageHash,
                ["ai_provider"] = session.AiProvider,
                ["ai_model"] = session.AiModel,
                ["latency_ms"] = session.LatencyMs,
                ["cached"] = session.Cached,
                ["error_code"] = session.ErrorCode,
                ["error_message"] = session.ErrorMessage,
                ["question_count"] = questions.Count,
                ["questions"] = questions,
            });
        }

        var root = new JsonObject
        {
            ["exported_at"] = exportedAt,
            ["exported_at_local"] = FormatTime(exportedAt),
            ["app"] = "QuizSync AI 2.0",
            ["session_count"] = sessions.Count,
            ["sessions"] = sessions,
        };

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// 日志脱敏。**导出前必须过这一道**：日志是给用户贴出来求助的，
    /// 里面一旦带出 API Key 或设备令牌就等于泄漏。
    ///
    /// 规则（宁可多抹一点）：`api_key` / `token` / `authorization` / `secret` 这类键后面的值一律替换；
    /// `sk-…` 形态的 Key；独立的 64 位十六进制串（本项目的设备令牌就是 64 位 hex）。
    /// </summary>
    public static string RedactSecrets(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        // 注意替换串里**不能出现 `$2`**：那等于把抹掉的值又原样贴回来（测试当场抓到的 bug）。
        var redacted = SecretKeyValue().Replace(text, "$1(已脱敏)");
        redacted = SkKey().Replace(redacted, "sk-(已脱敏)");
        redacted = LongHex().Replace(redacted, "(64 位令牌已脱敏)");
        return redacted;
    }

    // `"api_key": "sk-…"` → `"api_key": (已脱敏)`；值一直取到引号/空白/逗号为止。
    // 值为 `Bearer <token>` / `Basic <token>` 这种带方案前缀的写法也一起吃掉（否则只抹掉 `Bearer`，令牌还在）。
    [GeneratedRegex(
        "(?i)([\"']?(?:api[_-]?key|access[_-]?token|refresh[_-]?token|device[_-]?token|token|authorization|secret|password)[\"']?\\s*[:=]\\s*[\"']?)(?:(?:Bearer|Basic)\\s+)?([^\\s\"',;}]+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex SecretKeyValue();

    [GeneratedRegex("sk-[A-Za-z0-9_\\-]{8,}", RegexOptions.CultureInvariant)]
    private static partial Regex SkKey();

    [GeneratedRegex("\\b[0-9a-fA-F]{64}\\b", RegexOptions.CultureInvariant)]
    private static partial Regex LongHex();
}
