using QuizSync.Core;
using Xunit;

namespace QuizSync.Tests;

/// <summary>
/// 用**用户真实数据**跑原生解析层（功能对齐清单里「❓没人查过」的第 2 条）。
///
/// 为什么值得单独一个文件：
/// * 之前的公式用例都是我自己编的短串；**用户真题**里有嵌套分数
///   （`\frac{a_{n+1}}{2^{n+1}}-\frac{a_n}{2^n}=\frac12`）、大型运算符上下限
///   （`\sum_{k=1}^n`）、黑板粗体（`\mathbb N^*`）、以及 ①②③④⑤ 分步解答 ——
///   这些组合从没在原生端跑过；
/// * 原文**逐字**从 `%LOCALAPPDATA%\QuizSyncAI\app\quizsync.db` 的 `questions` 表里取，
///   不是重写的近似版（近似版会把"真实的怪写法"过滤掉，而那正是要验的东西）。
///
/// ⚠️ 这里验的是**解析**（`QuizSync.Core`）。渲染（`MathTextBlock` 的 XAML 层）在应用工程里，
/// 没有单元测试宿主 —— 那一半只能靠截图看（见 `docs/UI-PROGRESS.md` 的真机/真机截图记录）。
/// </summary>
public sealed class RealDataMathTests
{
    /// <summary>真实题干（原样，含反斜杠与 `$` 定界）。</summary>
    private const string RealStem = """
        已知数列 $\{a_n\}$ 满足 $a_1=1,\ a_{n+1}=2a_n+2^n\ (n\in\mathbb N^*)$。
        (1) 证明 $\{\frac{a_n}{2^n}\}$ 是等差数列，并求 $a_n$；
        (2) 设 $b_n=n a_n$，求数列 $\{b_n\}$ 的前 $n$ 项和 $S_n$。
        """;

    /// <summary>真实解析/答案（原样，注意第 ④ 步里的错位相减与 `\cdots`）。</summary>
    private const string RealAnswer = """
        ① 由 $a_{n+1}=2a_n+2^n$，两边同除以 $2^{n+1}$，得 $\frac{a_{n+1}}{2^{n+1}}-\frac{a_n}{2^n}=\frac12$，所以 $\{\frac{a_n}{2^n}\}$ 是首项为 $\frac12$、公差为 $\frac12$ 的等差数列。
        ② 于是 $\frac{a_n}{2^n}=\frac12+(n-1)\cdot\frac12=\frac n2$，故 $a_n=n\cdot 2^{n-1}$。
        ③ 因为 $b_n=n a_n=n^2 2^{n-1}$，所以 $S_n=\sum_{k=1}^n k^2 2^{k-1}$。
        ④ 用错位相减法：$S_n=1^2\cdot2^0+2^2\cdot2^1+\cdots+n^2\cdot2^{n-1}$，$2S_n=1^2\cdot2^1+2^2\cdot2^2+\cdots+n^2\cdot2^n$，相减得 $-S_n=1+\sum_{k=2}^n(2k-1)2^{k-1}-n^2 2^n$。
        ⑤ 化简得 $-S_n=(2n-3-n^2)2^n+3$，故 $S_n=(n^2-2n+3)2^n-3$。
        """;

    [Fact]
    public void Nested_fraction_real_case_becomes_fraction_pieces_not_flat_text()
    {
        // 用户真题里那句的核心片段：**两个分数相减**，分母还各带指数。
        var pieces = MathContent.ParseLatexPieces(@"\frac{a_{n+1}}{2^{n+1}}-\frac{a_n}{2^n}=\frac12");

        var fractions = pieces.OfType<MathContent.MathFraction>().ToList();
        Assert.True(fractions.Count >= 3, $"三个分数应当都解析成分数结构，实际 {fractions.Count} 个");

        // 左边第一个分数的分子要含 a 与下标 n+1，分母要含 2 与上标 n+1 —— 用展平文本断言，
        // 因为下标在 Windows 端最终会落成 Unicode 字符，具体形态不是这一层该固定的。
        var first = MathContent.FlattenPieces(fractions[0].Numerator);
        var firstDenominator = MathContent.FlattenPieces(fractions[0].Denominator);
        Assert.Contains("a", first);
        Assert.Contains("n", first);
        Assert.Contains("2", firstDenominator);
    }

    [Fact]
    public void Big_operator_keeps_its_limits()
    {
        // `\sum_{k=1}^n`：上下限必须留住（摊平成一行文字就看不出"求和范围"，数学上会读错）。
        var pieces = MathContent.ParseLatexPieces(@"S_n=\sum_{k=1}^n k^2 2^{k-1}");

        var op = Assert.Single(pieces.OfType<MathContent.MathBigOperator>());
        Assert.NotNull(op.Lower);
        Assert.NotNull(op.Upper);
        Assert.Contains("k", MathContent.FlattenPieces(op.Lower!));
        Assert.Contains("n", MathContent.FlattenPieces(op.Upper!));
    }

    [Fact]
    public void Real_stem_and_answer_parse_without_losing_content()
    {
        // 铁律：**不支持就降级成源码显示，绝不吞内容**。
        // 所以无论解析得多深，展平之后用户写过的关键 token 都必须还在。
        foreach (var (label, text) in new[] { ("题干", RealStem), ("答案", RealAnswer) })
        {
            var pieces = MathContent.ParseLatexPieces(text);
            var flat = MathContent.FlattenPieces(pieces);

            foreach (var token in new[] { "a", "n", "2", "S" })
            {
                Assert.Contains(token, flat);
            }

            Assert.False(string.IsNullOrWhiteSpace(flat), $"{label}展平后不该是空的");
        }
    }

    [Fact]
    public void Step_markers_and_blackboard_bold_survive()
    {
        // ①②③④⑤ 与 `\mathbb N^*` 这类"少数派写法"最容易在解析里被吃掉。
        var pieces = MathContent.ParseLatexPieces(RealAnswer);
        var flat = MathContent.FlattenPieces(pieces);

        foreach (var marker in new[] { "①", "②", "③", "④", "⑤" })
        {
            // 断言带出展平结果 —— 失败时要能直接看出"丢了什么"，而不是只知道"丢了一个字符"。
            Assert.True(
                flat.Contains(marker),
                $"展平后缺 {marker}。完整展平结果：\n{flat}");
        }

        // ⚠️ `\mathbb N`（不带花括号）这条**单独一个 skip 用例**：见下面 `Blackboard_bold_without_braces`。
        // 本机是 xUnit v2，没有 `Assert.Skip`，所以用 `[Fact(Skip = …)]` 表达"已知缺陷"，
        // 运行结果里它会明确显示为 **已跳过**（不是通过）。
    }

    /// <summary>
    /// 真实数据里 `\mathbb N^*` 是**不带花括号**的写法（LaTeX 允许）。
    /// 这条曾经是红的（`N` 被吞掉，`MathPieces.cs` 只认 `\mathbb{N}`）——
    /// 修好后从 `[Fact(Skip)]` 转正；**断言从未放宽**。
    /// </summary>
    [Fact]
    public void Blackboard_bold_without_braces_keeps_the_letter()
    {
        var flat = MathContent.FlattenPieces(MathContent.ParseLatexPieces(RealStem));

        // 要求是**字母存活**，不是"必须还是 ASCII 的 N"：`\mathbb N` 渲染层会把它落成
        // 黑板粗体码位 `ℕ`（U+2115），那才是正确结果。所以两种都算过。
        Assert.True(
            flat.Contains('N') || flat.Contains('ℕ'),
            $"`\\mathbb N` 的字母被吞了（既没有 N 也没有 ℕ）。实际展平结果：\n{flat}");
    }

    [Fact]
    public void Repeated_parse_is_stable()
    {
        // 同一段文本解析两次结果一致 —— 真实数据会被反复渲染（滚动、重建卡片），
        // 解析不确定的话界面会"自己变"。
        var first = MathContent.FlattenPieces(MathContent.ParseLatexPieces(RealStem));
        var second = MathContent.FlattenPieces(MathContent.ParseLatexPieces(RealStem));
        Assert.Equal(first, second);
    }
}
