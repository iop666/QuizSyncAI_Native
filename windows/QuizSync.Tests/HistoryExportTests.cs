using QuizSync.Core;
using QuizSync.Data;
using Xunit;

namespace QuizSync.Tests;

/// <summary>
/// 导出的**纯组装**（`HistoryExport`）：JSON 结构 + 日志脱敏。
///
/// 两件事都**错了很难看**，所以必须有测试兜底：
/// * JSON 结构一漂，下游脚本（用户拿去做题库、别人写的解析器）全部失效；
/// * 日志里带出 API Key 或设备令牌是**安全红线**（`SPEC.md` §2.3：导出日志「不含 API Key 与 Token」）。
///
/// 这一组测试同时钉住「**不要过度脱敏**」：正常的日志行一个字都不许动 ——
/// 把有用的信息也抹掉，等于让这份日志失去意义。
/// </summary>
public sealed class HistoryExportTests
{
    private static RecordedSession Session(string id = "s1") =>
        new(id, "hash-1", "windows-desktop", "done", 1, 1_700_000_000_000, "openai-compatible", "deepseek-flash", 2381, false, "c1");

    private static Question Question(int ordinal = 0) => new()
    {
        QuestionId = $"q{ordinal}",
        SessionId = "s1",
        Ordinal = ordinal,
        QuestionNo = "第 7 题",
        Stem = "下列关于加速度的说法正确的是？",
        Material = "材料段落",
        Type = QuestionType.SingleChoice,
        Options = [new Option("A", "加速度是矢量"), new Option("B", "加速度越大速度一定越大")],
        Choice = ["A"],
        AnswerText = null,
        Analysis = "加速度描述速度变化的快慢。",
        Confidence = 0.99,
        NeedReview = false,
        Incomplete = false,
        AnswerGuessed = false,
        AnswerInImage = false,
        Warnings = ["模型截断"],
        CreatedAt = 1_700_000_000_000,
        UpdatedAt = 1_700_000_000_000,
        UpdatedBy = "windows-desktop",
    };

    [Fact]
    public void 全部历史的JSON结构逐字段可读()
    {
        var items = new List<HistoryExport.Item> { new(Session(), [Question()], "端到端验收") };

        var json = HistoryExport.AllHistoryJson(items, 1_700_000_100_000);
        var root = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();

        Assert.Equal(1, root["session_count"]!.GetValue<int>());
        Assert.Equal("QuizSync AI 2.0", root["app"]!.GetValue<string>());

        var session = root["sessions"]!.AsArray()[0]!.AsObject();
        Assert.Equal("s1", session["session_id"]!.GetValue<string>());
        Assert.Equal("端到端验收", session["collection"]!.GetValue<string>());
        Assert.Equal("done", session["status"]!.GetValue<string>());
        Assert.Equal(2381, session["latency_ms"]!.GetValue<int>());

        var question = session["questions"]!.AsArray()[0]!.AsObject();
        Assert.Equal("第 7 题", question["question_no"]!.GetValue<string>());
        Assert.Equal("single", question["type"]!.GetValue<string>());
        Assert.Equal("下列关于加速度的说法正确的是？", question["stem"]!.GetValue<string>());
        Assert.Equal(2, question["options"]!.AsArray().Count);
        Assert.Equal("A", question["options"]!.AsArray()[0]!["label"]!.GetValue<string>());
        Assert.Equal("A", question["choice"]!.AsArray()[0]!.GetValue<string>());
        Assert.Equal(0.99, question["confidence"]!.GetValue<double>(), 3);
        Assert.Equal("模型截断", question["warnings"]!.AsArray()[0]!.GetValue<string>());
    }

    [Fact]
    public void 没有记录时也给出合法JSON而不是空字符串()
    {
        var root = System.Text.Json.Nodes.JsonNode
            .Parse(HistoryExport.AllHistoryJson([], 1_700_000_100_000))!.AsObject();

        Assert.Equal(0, root["session_count"]!.GetValue<int>());
        Assert.Empty(root["sessions"]!.AsArray());
    }

    [Theory]
    // ① JSON 里的 Key
    [InlineData("{\"api_key\": \"sk-abcdefghijklmnop\"}", "sk-abcdefghijklmnop")]
    // ② HTTP 头（带 Bearer 方案前缀 —— 只抹掉 Bearer 等于没抹）
    [InlineData("Authorization: Bearer 9f8e7d6c5b4a3210", "9f8e7d6c5b4a3210")]
    // ③ 键值形态的令牌
    [InlineData("token=abcdef123456", "abcdef123456")]
    // ④ 独立的 64 位十六进制串（本项目的设备令牌就是这个形态）
    [InlineData("device 4f3c2b1a0918273645abcdef0123456789abcdef0123456789abcdef01234567 paired",
        "4f3c2b1a0918273645abcdef0123456789abcdef0123456789abcdef01234567")]
    public void 导出日志会把密钥与令牌抹掉(string line, string secret)
    {
        var redacted = HistoryExport.RedactSecrets(line);

        Assert.DoesNotContain(secret, redacted);
        Assert.Contains("已脱敏", redacted);
    }

    [Fact]
    public void 导出日志不会把正常内容也抹掉()
    {
        const string line = "2026-09-28 21:03:11 识别完成，用时 2381 ms，题目数 3，模型 deepseek-flash";
        Assert.Equal(line, HistoryExport.RedactSecrets(line));
    }

    [Fact]
    public void 全部历史Markdown含合集标注与逐题内容()
    {
        var items = new List<HistoryExport.Item>
        {
            new(Session(), [Question()], "端到端验收"),
            new(Session("s2"), [], null), // 未分类、且没有题目
        };

        var text = HistoryExport.AllHistoryMarkdown(items, "windows-desktop", 1_700_000_100_000);

        Assert.Contains("# QuizSync AI 识别历史", text, StringComparison.Ordinal);
        Assert.Contains("> 合集：端到端验收", text, StringComparison.Ordinal);
        Assert.Contains("## 1. 第 7 题", text, StringComparison.Ordinal);
        Assert.Contains("**答案：**A", text, StringComparison.Ordinal);
        Assert.Contains("**解析：**加速度描述速度变化的快慢。", text, StringComparison.Ordinal);
        // 命中的那个选项带 ✅、没命中的不带 —— 标绿规则在导出里也要成立。
        Assert.Contains("- A. 加速度是矢量 ✅", text, StringComparison.Ordinal);
        Assert.Contains("- B. 加速度越大速度一定越大", text, StringComparison.Ordinal);
        Assert.DoesNotContain("- B. 加速度越大速度一定越大 ✅", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 合集导出只有该合集的记录且带合集标题()
    {
        var items = new List<HistoryExport.Item> { new(Session(), [Question()], "端到端验收") };

        var text = HistoryExport.CollectionMarkdown("端到端验收", items, "windows-desktop");

        Assert.StartsWith("# 端到端验收", text, StringComparison.Ordinal);
        Assert.Contains("共 1 条识别记录。", text, StringComparison.Ordinal);
        Assert.Contains("# 识别结果", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 时间格式化与界面同格式()
    {
        // 只断言形状（yyyy-MM-dd HH:mm）：具体值取决于本机时区，断言绝对值会变成「换个时区就红」。
        var text = HistoryExport.FormatTime(1_700_000_000_000);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}$", text);
    }
}
