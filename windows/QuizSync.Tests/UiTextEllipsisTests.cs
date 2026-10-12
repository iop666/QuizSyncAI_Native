using QuizSync.Core;
using Xunit;

namespace QuizSync.Tests;

/// <summary>
/// `UiText.Ellipsis` —— 搜索结果那一行摘要用的截断。
///
/// 为什么值得单独测：它是**界面文案的边界处理**，出错的形态都很隐蔽
/// （换行把列表行高撑乱、截断后丢字符、空串变成 "…"），
/// 而且它在控件树里看起来永远「有个字符串」，不测就发现不了。
/// </summary>
public sealed class UiTextEllipsisTests
{
    [Fact]
    public void 空与空白输入都返回空串()
    {
        Assert.Equal(string.Empty, UiText.Ellipsis(null, 10));
        Assert.Equal(string.Empty, UiText.Ellipsis(string.Empty, 10));
        // 只有空白也当空：否则列表里会出现一行「   」看起来像渲染故障。
        Assert.Equal(string.Empty, UiText.Ellipsis("   \t  ", 10));
    }

    [Fact]
    public void 不超过上限时原样返回()
    {
        Assert.Equal("加速度", UiText.Ellipsis("加速度", 10));
        // 正好等于上限也不该截（截了会平白多一个省略号）。
        Assert.Equal("12345", UiText.Ellipsis("12345", 5));
    }

    [Fact]
    public void 超长时截到上限并补省略号()
    {
        var result = UiText.Ellipsis("关于加速度的说法正确的是", 6);
        // 保留 max-1 个字符 + 一个省略号 = 恰好 max 个字符宽。
        Assert.Equal(6, result.Length);
        Assert.EndsWith("…", result, StringComparison.Ordinal);
        Assert.StartsWith("关于加速度", result, StringComparison.Ordinal);
    }

    [Fact]
    public void 换行被压成空格()
    {
        // 题干里带换行；摘要里若保留换行会把那一行的高度撑成两行。
        var result = UiText.Ellipsis("第一行\r\n第二行\n第三行", 50);
        Assert.DoesNotContain("\n", result, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", result, StringComparison.Ordinal);
        Assert.Equal("第一行 第二行 第三行", result);
    }

    [Fact]
    public void 上限小于等于一时不会算出负数长度()
    {
        // max <= 1 时截断没有意义（连省略号都放不下），应当返回压平后的原文而不是抛异常。
        Assert.Equal("加速度", UiText.Ellipsis("加速度", 1));
        Assert.Equal("加速度", UiText.Ellipsis("加速度", 0));
    }
}
