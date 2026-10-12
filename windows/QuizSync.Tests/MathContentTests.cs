using Xunit;

using QuizSync.Core;

namespace QuizSync.Tests;

/// <summary>
/// 内容解析的回归（对齐 1.1.0 的 `math_text.dart`，规则逐条见桌面规格 §4.4）。
///
/// 每条断言都对应一次**用户看得见的问题**：`$…$` 原样显示、`ZnCO3` 没有小标、
/// 材料里的表格挤成一团、公式里 `Fe2+` 被判成普通文字…… 这些在界面上只表现为
/// 「显示得怪」，不会有任何报错，所以必须靠断言钉住。
/// </summary>
public class MathContentTests
{
    [Fact]
    public void FourDelimitersAreRecognised()
    {
        var inlineParen = MathContent.Parse("电荷量 \\(q=1\\) 的粒子", 100);
        var paragraph = Assert.IsType<MathContent.Paragraph>(inlineParen[0]).Block;
        Assert.Contains(paragraph.Runs, run => run is MathContent.MathRun { Latex: "q=1" });

        var inlineDollar = MathContent.Parse("设 $x=1$ 则", 100);
        Assert.Contains(
            Assert.IsType<MathContent.Paragraph>(inlineDollar[0]).Block.Runs,
            run => run is MathContent.MathRun);

        var displayDollar = MathContent.Parse("如下：$$a^2+b^2=c^2$$ 完", 100);
        Assert.Contains(displayDollar, block => block is MathContent.Formula { Block.Latex: "a^2+b^2=c^2" });

        var displayBracket = MathContent.Parse("推导：\\[E=mc^2\\]", 100);
        Assert.Contains(displayBracket, block => block is MathContent.Formula { Block.Latex: "E=mc^2" });
    }

    [Fact]
    public void LongInlineFormulaBecomesItsOwnBlock()
    {
        var blocks = MathContent.Parse("由 $v=v_0+at=2+3=14$ 可得", 10);
        Assert.Contains(blocks, block => block is MathContent.Formula);
    }

    [Fact]
    public void InlineLimitFollowsTheSpec()
    {
        // 1.1.0：maxInline = (可用宽度 / 11).clamp(24, 96)
        Assert.Equal(24, MathContent.MaxInlineChars(100));
        Assert.Equal(58, MathContent.MaxInlineChars(640));
        Assert.Equal(96, MathContent.MaxInlineChars(4000));
    }

    [Theory]
    [InlineData("ZnCO3", true)]
    [InlineData("H2O", true)]
    [InlineData("Fe2+", true)]
    [InlineData("e-", true)]
    [InlineData("CO2", true)]
    [InlineData("pH", false)]      // 桌面规格点名的反例
    [InlineData("MPa", false)]     // 同上
    [InlineData("2026", false)]    // 同上
    [InlineData("NaCl", false)]    // 没有数字或电荷 → 按 1.1.0 不算化学式
    [InlineData("mg", false)]
    public void ChemicalFormulaRuleMatches110(string text, bool expected) =>
        Assert.Equal(expected, MathContent.IsChemicalFormula(text));

    [Fact]
    public void ChemistrySpansPutDigitsDownAndChargeUp()
    {
        var spans = MathContent.ChemistrySpans("ZnCO3");
        Assert.Equal(MathContent.Script.Sub, spans[^1].Script);
        Assert.Equal("3", spans[^1].Text);
        Assert.Equal("ZnCO", string.Concat(spans.Take(spans.Count - 1).Select(span => span.Text)));

        var charged = MathContent.ChemistrySpans("Fe2+");
        Assert.Equal(MathContent.Script.Sub, charged[1].Script);
        Assert.Equal(MathContent.Script.Super, charged[2].Script);
    }

    [Fact]
    public void MarkdownTableIsParsedWithHeader()
    {
        var table = MathContent.Parse("| 项目 | 数值 |\n|---|---|\n| 电压 | 3.7 |\n| 电流 | 0.5 |")
            .OfType<MathContent.Table>().Single().Block;
        Assert.True(table.Header);
        Assert.Equal(3, table.Rows.Count);
        Assert.Equal(["项目", "数值"], table.Rows[0]);
    }

    [Fact]
    public void SpaceAlignedTableIsParsedWithoutHeader()
    {
        var table = MathContent.Parse("光照强度/klx    0    2    4\nCO2变化量/mg    +44    +8    -22")
            .OfType<MathContent.Table>().Single().Block;
        Assert.False(table.Header);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal(4, table.Rows[0].Count);
    }

    [Fact]
    public void SingleLineWithSpacesIsNotATable() =>
        Assert.DoesNotContain(MathContent.Parse("这 是 一 句 话"), block => block is MathContent.Table);

    [Fact]
    public void SymbolsAndScriptsSurviveTheLatexSubset()
    {
        var text = MathContent.Flatten(MathContent.ParseLatex("3\\times 10^{8}\\ \\mathrm{m/s}"));
        Assert.Contains('×', text);
        Assert.Contains('8', text);
        Assert.Contains("m/s", text);

        var spans = MathContent.ParseLatex("v_0^2");
        Assert.Contains(spans, span => span is { Text: "0", Script: MathContent.Script.Sub });
        Assert.Contains(spans, span => span is { Text: "2", Script: MathContent.Script.Super });
    }

    [Fact]
    public void BrokenFormulaDegradesToSourceInsteadOfDroppingContent()
    {
        // 模型截断公式是常事：绝不能把内容吞掉（全项目的硬规矩）
        var text = MathContent.Flatten(MathContent.ParseLatex("\\frac{a}{"));
        Assert.Contains("a", text, StringComparison.Ordinal);

        var unknown = MathContent.Flatten(MathContent.ParseLatex("\\foobar x"));
        Assert.Equal("foobar x", unknown);
    }

    [Fact]
    public void BinaryOperatorsGetSpacing()
    {
        // 安卓端实测过：逐字符拼出来的公式一个空格都没有，看起来就是「字挨在一起」
        var text = MathContent.Flatten(MathContent.ParseLatex("f(1)=1^2-2\\times1+3"));
        Assert.Contains("=", text);
        Assert.Contains(MathContent.ThinSpace, text);
    }
}
