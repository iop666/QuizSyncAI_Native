using System.Text.Json.Nodes;

namespace QuizSync.Core;

/// <summary>一次 AI 请求的完整描述（**构造与发送分离**：构造可以离线单测）。</summary>
public sealed record AiHttpRequest(string Url, IReadOnlyDictionary<string, string> Headers, JsonObject Body);

/// <summary>三个真实 provider 的请求构造 + 响应文本抽取（`ai-contract.md` 第 1 节）。</summary>
public static class AiProviders
{
    public const string OpenAiCompatible = "openai-compatible";
    public const string Anthropic = "anthropic";
    public const string Gemini = "gemini";

    public const string OpenAiDefaultBaseUrl = "https://api.openai.com/v1";
    public const string AnthropicDefaultBaseUrl = "https://api.anthropic.com";
    public const string GeminiDefaultBaseUrl = "https://generativelanguage.googleapis.com";

    /// <summary>支持的 provider id（注册表覆盖的这三家）。</summary>
    public static readonly string[] Ids = [OpenAiCompatible, Anthropic, Gemini];

    private static string Base(string configured, string fallback) =>
        string.IsNullOrEmpty(configured) ? fallback : configured;

    /// <summary>
    /// 按 provider 构造请求。多页就是**同一条消息里按顺序放多张图**。
    /// </summary>
    public static AiHttpRequest BuildRequest(
        string providerId, IReadOnlyList<byte[]> jpegBytesList, string prompt, AiConfig config) => providerId switch
    {
        Anthropic => BuildAnthropic(jpegBytesList, prompt, config),
        Gemini => BuildGemini(jpegBytesList, prompt, config),
        _ => BuildOpenAiCompatible(jpegBytesList, prompt, config),
    };

    private static AiHttpRequest BuildOpenAiCompatible(
        IReadOnlyList<byte[]> pages, string prompt, AiConfig config)
    {
        var content = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = prompt } };
        foreach (var page in pages)
        {
            content.Add(new JsonObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JsonObject { ["url"] = $"data:image/jpeg;base64,{Convert.ToBase64String(page)}" },
            });
        }

        var body = new JsonObject
        {
            ["model"] = config.Model,
            // **必须给输出预算**（第 105 轮实测抓到的 bug）：`deepseek-flash` 这类推理模型
            // 会先把预算花在 `reasoning_content` 上，预算不够时 `content` 返回**空串**、
            // `finish_reason` 是 `length` —— 而 **HTTP 仍是 200、耗时只有 1 秒多**，
            // 于是解析器找不到题目，全链路报 `no_question_found`（「没识别到题目」），
            // 把人引去怀疑截图与模型。修好后实测：同一张图 6.1 秒、识别出 1 道题、题干一字不差。
            //
            // 第 168 轮再往上抬到 16384：4096 对**真实截图**（一道大题 + 解析）仍然会撞上限
            // （用户那次两次调用分别是 19.5 秒 / 21.0 秒 —— 思考把 4096 全吃掉了，
            // 参见 docs/DECISIONS.md 第 168 轮）。模型的输出上限是 393216，
            // 抬高上限**不会多花钱**（只按真正生成的 token 计费）。
            ["max_tokens"] = 16384,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = content }),
        };

        return new AiHttpRequest(
            $"{Base(config.BaseUrl, OpenAiDefaultBaseUrl)}/chat/completions",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Authorization"] = $"Bearer {config.ApiKey}",
                ["Content-Type"] = "application/json",
            },
            body);
    }

    private static AiHttpRequest BuildAnthropic(
        IReadOnlyList<byte[]> pages, string prompt, AiConfig config)
    {
        // 注意顺序：Anthropic 的图片块在**前**、文本在后（与 OpenAI 兼容相反）。
        var content = new JsonArray();
        foreach (var page in pages)
        {
            content.Add(new JsonObject
            {
                ["type"] = "image",
                ["source"] = new JsonObject
                {
                    ["type"] = "base64",
                    ["media_type"] = "image/jpeg",
                    ["data"] = Convert.ToBase64String(page),
                },
            });
        }

        content.Add(new JsonObject { ["type"] = "text", ["text"] = prompt });

        var body = new JsonObject
        {
            ["model"] = config.Model,
            ["max_tokens"] = 4096,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = content }),
        };

        return new AiHttpRequest(
            $"{Base(config.BaseUrl, AnthropicDefaultBaseUrl)}/v1/messages",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["x-api-key"] = config.ApiKey,
                ["anthropic-version"] = "2023-06-01",
                ["Content-Type"] = "application/json",
            },
            body);
    }

    private static AiHttpRequest BuildGemini(
        IReadOnlyList<byte[]> pages, string prompt, AiConfig config)
    {
        var parts = new JsonArray();
        foreach (var page in pages)
        {
            parts.Add(new JsonObject
            {
                ["inline_data"] = new JsonObject
                {
                    ["mime_type"] = "image/jpeg",
                    ["data"] = Convert.ToBase64String(page),
                },
            });
        }

        parts.Add(new JsonObject { ["text"] = prompt });

        var body = new JsonObject
        {
            ["contents"] = new JsonArray(new JsonObject { ["parts"] = parts }),
        };

        return new AiHttpRequest(
            $"{Base(config.BaseUrl, GeminiDefaultBaseUrl)}/v1beta/models/{config.Model}:generateContent?key={config.ApiKey}",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["Content-Type"] = "application/json" },
            body);
    }

    /// <summary>
    /// 「信封」的判读结果：正文 / 这个响应体是不是认得出的信封 / 有没有被输出预算截断。
    /// </summary>
    public sealed record AiEnvelope(string Text, bool IsEnvelope, bool Truncated);

    /// <summary>
    /// 判读一次响应体：认得出的信封（OpenAI 兼容 / Anthropic / Gemini）给正文，
    /// 顺带看出**有没有被输出预算截断**；认不出来就 `IsEnvelope = false`（上层原样返回原文）。
    ///
    /// ⚠️ 为什么必须有这一层（第 168 轮，用户报「windows 端 api 识别等十几秒然后失败」）：
    /// `deepseek-flash` 这类推理模型会先把预算花在 `reasoning_content` 上；预算不够时
    /// `content` 是**空串**、`finish_reason == "length"`，而 **HTTP 仍然是 200**。
    /// 旧实现在「抽不出正文」时把**整个信封**当成模型正文返回，`AnalysisEngine` 又把它交给
    /// `ResponseParser` —— 信封本身就是合法 JSON、里面当然没有 `questions`，于是全链路报
    /// `no_question_found`（用户看到「未识别到题目」，还会以为是自己截图或模型不行）。
    /// 实测复现：同一把 Key、同一个模型，把 `max_tokens` 压到 16 就能稳定拿到这个形态
    /// （`content` 空、`reasoning_content` 有内容、`finish_reason=length`）。
    /// </summary>
    public static AiEnvelope ReadEnvelope(JsonNode? raw)
    {
        if (raw is not JsonObject data)
        {
            return new AiEnvelope(string.Empty, false, false);
        }

        // OpenAI 兼容。
        if (data["choices"] is JsonArray { Count: > 0 } choices && choices[0] is JsonObject first)
        {
            var truncated = first["finish_reason"]?.ToString() == "length";
            if (first["message"] is JsonObject message)
            {
                switch (message["content"])
                {
                    case JsonValue value when value.TryGetValue<string>(out var text):
                        return new AiEnvelope(text, true, truncated);
                    case JsonArray parts:
                        return new AiEnvelope(
                            string.Concat(parts.OfType<JsonObject>().Select(p => p["text"]?.ToString() ?? string.Empty)),
                            true,
                            truncated);
                    default:
                        return new AiEnvelope(string.Empty, true, truncated);
                }
            }

            return new AiEnvelope(string.Empty, true, truncated);
        }

        // Anthropic（`stop_reason: max_tokens`）。
        if (data["content"] is JsonArray blocks)
        {
            var truncated = data["stop_reason"]?.ToString() == "max_tokens";
            return new AiEnvelope(
                string.Concat(blocks.OfType<JsonObject>()
                    .Where(b => b["type"]?.ToString() == "text")
                    .Select(b => b["text"]?.ToString() ?? string.Empty)),
                true,
                truncated);
        }

        // Gemini（`finishReason: MAX_TOKENS`）。
        if (data["candidates"] is JsonArray { Count: > 0 } candidates && candidates[0] is JsonObject candidate)
        {
            var truncated = candidate["finishReason"]?.ToString() is "MAX_TOKENS";
            if (candidate["content"] is JsonObject candidateContent && candidateContent["parts"] is JsonArray geminiParts)
            {
                return new AiEnvelope(
                    string.Concat(geminiParts.OfType<JsonObject>().Select(p => p["text"]?.ToString() ?? string.Empty)),
                    true,
                    truncated);
            }

            return new AiEnvelope(string.Empty, true, truncated);
        }

        return new AiEnvelope(string.Empty, false, false);
    }

    /// <summary>
    /// 从各家响应体里抽出文本：OpenAI 兼容 `choices[0].message.content`（字符串或内容块数组）、
    /// Anthropic `content[]` 里 `type=text` 的块、Gemini `candidates[0].content.parts[]`。
    /// 认不出来就返回空串（由上层按「解析失败」处理）。
    /// </summary>
    public static string ExtractContentText(JsonNode? raw) => ReadEnvelope(raw).Text;
}
