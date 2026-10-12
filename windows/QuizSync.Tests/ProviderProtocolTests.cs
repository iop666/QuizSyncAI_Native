using System.Text.Json.Nodes;
using QuizSync.Core;
using QuizSync.Provider;
using Xunit;

namespace QuizSync.Tests;

/// <summary>
/// Provider 回给主机的那几帧 JSON 的**形状**回归。
///
/// 为什么值得单独钉（这一程真实吃过一次亏）：`questions[]` 的键名就是协议 `questions` 表的列名，
/// 主机拿到之后**原样落库**，手机再从库里读出来渲染 —— 标绿靠
/// `choice_json ∩ options_json[].label`、材料折叠靠 `material`、标黄靠 `need_review`/`confidence`。
/// 键名少一个 / 拼错一个 / 把 JSON 文本写成嵌套对象，症状全都是
/// 「界面上这一版功能像没做」，而**两端都不会有任何报错**。
/// </summary>
public sealed class ProviderProtocolTests
{
    private static readonly AiConfig Config = new()
    {
        ProviderId = "openai-compatible",
        BaseUrl = "https://api.deepseek.com",
        ApiKey = "sk-test",
        Model = "deepseek-flash",
    };

    private static Question SampleQuestion(QuestionType type = QuestionType.SingleChoice) => new()
    {
        QuestionId = "q-1",
        SessionId = "s-1",
        Ordinal = 2,
        QuestionNo = "3",
        Stem = "下列说法正确的是",
        Material = "阅读下面的材料……",
        Type = type,
        Options = [new Option("A", "甲"), new Option("B", "乙")],
        Choice = ["A"],
        AnswerText = null,
        Analysis = "因为甲对。",
        Confidence = 0.92,
        NeedReview = true,
        AnswerInImage = false,
        Incomplete = true,
        AnswerGuessed = true,
        Warnings = ["题目不全，已在卡片上标黄"],
        UpdatedBy = "windows-provider",
    };

    /// <summary>键名逐个等于协议 `questions` 表的列名（去掉由持有方分配的 id 与时间戳/时钟列）。</summary>
    [Fact]
    public void Protocol_json_uses_the_schema_column_names()
    {
        var json = SampleQuestion().ToProtocolJson();

        string[] expected =
        [
            "ordinal", "question_no", "stem", "material", "type", "options_json", "choice_json",
            "answer_text", "analysis", "confidence", "need_review", "answer_in_image", "incomplete",
            "answer_guessed", "warnings_json",
        ];

        Assert.Equal(expected.OrderBy(k => k, StringComparer.Ordinal), json.Select(pair => pair.Key).OrderBy(k => k, StringComparer.Ordinal));
    }

    /// <summary>布尔列必须落成 0/1 整数：表里那几列是 `INTEGER` + `CHECK (col IN (0, 1))`。</summary>
    [Fact]
    public void Boolean_columns_are_zero_or_one()
    {
        var json = SampleQuestion().ToProtocolJson();

        Assert.Equal(1, json["need_review"]!.GetValue<int>());
        Assert.Equal(1, json["incomplete"]!.GetValue<int>());
        Assert.Equal(1, json["answer_guessed"]!.GetValue<int>());
        Assert.Equal(0, json["answer_in_image"]!.GetValue<int>());
    }

    /// <summary>`options_json` 里的 `label` 必须留着 —— 丢了它，标绿就永远算不出来
    /// （判定是 `answer.choice ∩ options[].label`，不是「选项文本里有没有那个字母」）。</summary>
    [Fact]
    public void Options_json_keeps_labels_so_highlighting_can_work()
    {
        var json = SampleQuestion().ToProtocolJson();

        var options = JsonNode.Parse(json["options_json"]!.GetValue<string>())!.AsArray();
        Assert.Equal(2, options.Count);
        Assert.Equal("A", options[0]!["label"]!.GetValue<string>());
        Assert.Equal("甲", options[0]!["text"]!.GetValue<string>());

        var choice = JsonNode.Parse(json["choice_json"]!.GetValue<string>())!.AsArray();
        Assert.Equal(["A"], choice.Select(item => item!.GetValue<string>()));

        // 标绿的完整判据（Core 里那份实现）：交集非空。
        var labels = options.Select(o => o!["label"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        Assert.Contains(choice[0]!.GetValue<string>(), labels);
    }

    /// <summary>题型走的是**协议冻结的五个字符串**，不是枚举名、也不是 `single_choice`。</summary>
    [Theory]
    [InlineData(QuestionType.SingleChoice, "single")]
    [InlineData(QuestionType.MultiChoice, "multi")]
    [InlineData(QuestionType.Judge, "judge")]
    [InlineData(QuestionType.Blank, "blank")]
    [InlineData(QuestionType.Subjective, "subjective")]
    public void Type_values_are_the_frozen_wire_strings(QuestionType type, string expected)
    {
        Assert.Equal(expected, SampleQuestion(type).ToProtocolJson()["type"]!.GetValue<string>());
    }

    [Fact]
    public void Result_frame_carries_questions_and_model()
    {
        var outcome = new AnalysisOutcome([SampleQuestion()], FromCache: true, LatencyMs: 1234);

        var frame = ProviderProtocol.Result("job-1", Config, outcome);

        Assert.Equal("recognition_result", frame["type"]!.GetValue<string>());
        Assert.Equal("job-1", frame["job_id"]!.GetValue<string>());
        Assert.True(frame["ok"]!.GetValue<bool>());
        Assert.Equal("deepseek-flash", frame["ai_model"]!.GetValue<string>());
        Assert.Equal(1234, frame["latency_ms"]!.GetValue<int>());
        Assert.True(frame["from_cache"]!.GetValue<bool>());

        var questions = frame["questions"]!.AsArray();
        Assert.Single(questions);
        Assert.Equal("下列说法正确的是", questions[0]!["stem"]!.GetValue<string>());
        Assert.Equal(2, questions[0]!["ordinal"]!.GetValue<int>());
    }

    /// <summary>失败帧必须带**真实**错误码：回一个空题目列表会被记成「没识别到题」，
    /// 真因（Key 过期、限流）就永远查不到了。</summary>
    [Fact]
    public void Failure_frame_carries_the_real_error_code()
    {
        var frame = ProviderProtocol.Failure("job-9", "ai_auth", "API Key 无效或无权限");

        Assert.False(frame["ok"]!.GetValue<bool>());
        Assert.Equal("ai_auth", frame["error_code"]!.GetValue<string>());
        Assert.Equal("API Key 无效或无权限", frame["error_message"]!.GetValue<string>());
        Assert.Null(frame["questions"]);
    }

    /// <summary>自述帧决定主机 `/info` 怎么回答 `ai_configured`（安卓端据此决定要不要发起识别）。</summary>
    [Fact]
    public void Hello_declares_the_provider_role_and_whether_ai_is_usable()
    {
        var configured = ProviderProtocol.Hello(Config);
        Assert.Equal("provider", configured["role"]!.GetValue<string>());
        Assert.Equal("windows-provider", configured["device_id"]!.GetValue<string>());
        Assert.True(configured["ai_configured"]!.GetValue<bool>());

        // 只有模型名、没有 Key（用户把 Key 清空之后的状态）→ 必须自述「不可用」。
        var empty = ProviderProtocol.Hello(Config with { ApiKey = string.Empty });
        Assert.False(empty["ai_configured"]!.GetValue<bool>());
    }
}
