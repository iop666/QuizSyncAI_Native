using System.Text;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Documents;
using QuizSync.App.Theme;
using QuizSync.Core;

namespace QuizSync.App.Controls;

/// <summary>
/// 题目内容的**渲染层**（Windows 端）：规则对齐 1.1.0 的 `MathText`（桌面规格 §4.4）。
///
/// 与安卓端的差别要说清楚：Compose 那边有 `BaselineShift`，能做**真正的**上下标；
/// WinUI 的 `TextBlock` 里没有等价的基线偏移，所以这里用 **Unicode 上下标字符**
/// （`H₂O`、`Fe²⁺`、`e⁻`、`x²`、`10⁸`、`aₙ`）—— 这是没有数学排版引擎时唯一既好看又不引依赖的做法。
/// 字符表里没有的写法（生僻字母、复杂上标）按 `^(…)` 原样留在行内，不吞内容。
///
/// 还没做的（明确记账）：
/// * **分数不画分数线**：`\frac{a}{b}` 降级成 `a/b`（1.1.0 是 `flutter_math_fork` 真排版）；
///   真正的公式排版在 `NATIVE-2.0-PLAN` §7 里是 WebView2 + KaTeX 那条路，不在本轮；
/// * 块级公式**不居中排版**，只做「独占一行 + 可横向滚动 + 稍大字号」；
/// * 表格已经画成真表格（表头/边框/内边距照规格）。
/// </summary>
public static class MathTextBlock
{
    /// <summary>Unicode 上标字符表。</summary>
    private static readonly Dictionary<char, char> Superscripts = new()
    {
        ['0'] = '⁰', ['1'] = '¹', ['2'] = '²', ['3'] = '³', ['4'] = '⁴',
        ['5'] = '⁵', ['6'] = '⁶', ['7'] = '⁷', ['8'] = '⁸', ['9'] = '⁹',
        ['+'] = '⁺', ['-'] = '⁻', ['−'] = '⁻', ['='] = '⁼', ['('] = '⁽', [')'] = '⁾',
        ['a'] = 'ᵃ', ['b'] = 'ᵇ', ['c'] = 'ᶜ', ['d'] = 'ᵈ', ['e'] = 'ᵉ', ['f'] = 'ᶠ',
        ['g'] = 'ᵍ', ['h'] = 'ʰ', ['i'] = 'ⁱ', ['j'] = 'ʲ', ['k'] = 'ᵏ', ['l'] = 'ˡ',
        ['m'] = 'ᵐ', ['n'] = 'ⁿ', ['o'] = 'ᵒ', ['p'] = 'ᵖ', ['r'] = 'ʳ', ['s'] = 'ˢ',
        ['t'] = 'ᵗ', ['u'] = 'ᵘ', ['v'] = 'ᵛ', ['w'] = 'ʷ', ['x'] = 'ˣ', ['y'] = 'ʸ',
        ['z'] = 'ᶻ',
    };

    /// <summary>Unicode 下标字符表。</summary>
    private static readonly Dictionary<char, char> Subscripts = new()
    {
        ['0'] = '₀', ['1'] = '₁', ['2'] = '₂', ['3'] = '₃', ['4'] = '₄',
        ['5'] = '₅', ['6'] = '₆', ['7'] = '₇', ['8'] = '₈', ['9'] = '₉',
        ['+'] = '₊', ['-'] = '₋', ['−'] = '₋', ['='] = '₌', ['('] = '₍', [')'] = '₎',
        ['a'] = 'ₐ', ['e'] = 'ₑ', ['h'] = 'ₕ', ['i'] = 'ᵢ', ['j'] = 'ⱼ', ['k'] = 'ₖ',
        ['l'] = 'ₗ', ['m'] = 'ₘ', ['n'] = 'ₙ', ['o'] = 'ₒ', ['p'] = 'ₚ', ['r'] = 'ᵣ',
        ['s'] = 'ₛ', ['t'] = 'ₜ', ['u'] = 'ᵤ', ['v'] = 'ᵥ', ['x'] = 'ₓ',
    };

    /// <summary>
    /// 建一个渲染好的元素：纯文本走单个 `TextBlock`（与以前完全一致），
    /// 有公式/化学式/表格时返回一个 `StackPanel`。
    /// </summary>
    public static FrameworkElement Create(
        string text,
        double fontSize,
        Brush foreground,
        int weight,
        double lineHeight,
        double availableWidth = 640)
    {
        if (!NeedsRichText(text))
        {
            return Plain(text, fontSize, foreground, weight, lineHeight);
        }

        var panel = new StackPanel();
        foreach (var block in MathContent.Parse(text, MathContent.MaxInlineChars(availableWidth)))
        {
            switch (block)
            {
                case MathContent.Paragraph paragraph:
                    panel.Children.Add(InlineParagraph(paragraph.Block, fontSize, foreground, weight, lineHeight));
                    break;

                case MathContent.Formula formula:
                    panel.Children.Add(FormulaBlock(formula.Block, fontSize, foreground, weight));
                    break;

                case MathContent.Table table:
                    panel.Children.Add(TableBlock(table.Block, fontSize, foreground));
                    break;
            }
        }

        return panel;
    }

    /// <summary>有公式 / 表格 / 化学式才走富渲染（1.1.0 的纯文本快路径）。</summary>
    public static bool NeedsRichText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Contains('$', StringComparison.Ordinal)
            || text.Contains("\\(", StringComparison.Ordinal)
            || text.Contains("\\[", StringComparison.Ordinal)
            || text.Contains("\\frac", StringComparison.Ordinal)
            || MathContent.HasInlineFormatting(text)
            || LooksLikeTable(text);
    }

    private static bool LooksLikeTable(string text)
    {
        var lines = text.Split('\n');
        if (lines.Length < 2)
        {
            return false;
        }

        if (lines.Any(line => line.Trim().StartsWith('|') && line.Trim().EndsWith('|')))
        {
            return true;
        }

        return lines.Count(line => line.Trim().Contains("  ", StringComparison.Ordinal)) >= 2;
    }

    private static TextBlock Plain(string text, double fontSize, Brush foreground, int weight, double lineHeight) =>
        new()
        {
            Text = text,
            FontFamily = UiFont.For(weight),
            FontSize = fontSize,
            Foreground = foreground,
            FontWeight = weight >= 700 ? FontWeights.Bold
                : weight >= 600 ? FontWeights.SemiBold
                : weight >= 500 ? FontWeights.Medium
                : FontWeights.Normal,
            LineHeight = fontSize * lineHeight,
            TextWrapping = TextWrapping.Wrap,
        };

    /// <summary>一段行内内容：文字（化学式小标）+ 行内公式，合成一个 `TextBlock`。</summary>
    private static TextBlock InlineParagraph(
        MathContent.ParagraphBlock block, double fontSize, Brush foreground, int weight, double lineHeight)
    {
        var text = new TextBlock
        {
            FontFamily = UiFont.For(weight),
            FontSize = fontSize,
            Foreground = foreground,
            FontWeight = weight >= 700 ? FontWeights.Bold
                : weight >= 600 ? FontWeights.SemiBold
                : weight >= 500 ? FontWeights.Medium
                : FontWeights.Normal,
            LineHeight = fontSize * lineHeight,
            TextWrapping = TextWrapping.Wrap,
        };

        foreach (var run in block.Runs)
        {
            switch (run)
            {
                case MathContent.TextRun plain:
                    foreach (var span in MathContent.ChemistrySpans(plain.Text))
                    {
                        text.Inlines.Add(new Run { Text = Scripted(span.Text, span.Script) });
                    }

                    break;

                case MathContent.MathRun math:
                    foreach (var span in MathContent.ParseLatex(math.Latex))
                    {
                        text.Inlines.Add(new Run { Text = Scripted(span.Text, span.Script) });
                    }

                    break;
            }
        }

        return text;
    }

    /// <summary>块级公式：独占一行、居中、**可横向滚动**（再长也不会被裁掉）。</summary>
    private static ScrollViewer FormulaBlock(
        MathContent.FormulaBlock block, double fontSize, Brush foreground, int weight)
    {
        var line = new TextBlock
        {
            Text = MathContent.Flatten(MathContent.ParseLatex(block.Latex)),
            FontFamily = UiFont.For(weight),
            FontSize = fontSize * 1.08,
            Foreground = foreground,
            FontWeight = weight >= 700 ? FontWeights.Bold
                : weight >= 600 ? FontWeights.SemiBold
                : weight >= 500 ? FontWeights.Medium
                : FontWeights.Normal,
            TextWrapping = TextWrapping.NoWrap,
        };

        return new ScrollViewer
        {
            Content = line,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Auto,
            Margin = new Thickness(0, 6, 0, 6),
        };
    }

    /// <summary>表格：真表格（边框 / 内边距 / 表头样式照桌面规格 §4.4）。</summary>
    private static ScrollViewer TableBlock(MathContent.TableBlock block, double fontSize, Brush foreground)
    {
        var columns = block.Rows.Max(row => row.Count);
        var grid = new Grid();
        for (var i = 0; i < columns; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        var stroke = ShellTheme.Brush("QsOutlineBrush", null);
        var headerBackground = ShellTheme.Brush("QsSurfaceHighestBrush", null);

        for (var row = 0; row < block.Rows.Count; row++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var column = 0; column < block.Rows[row].Count; column++)
            {
                var cell = new Border
                {
                    BorderBrush = stroke,
                    BorderThickness = new Thickness(0.6),
                    Background = block.Header && row == 0 ? headerBackground : null,
                    Padding = new Thickness(8, 4, 8, 4),
                    Child = new TextBlock
                    {
                        Text = block.Rows[row][column],
                        FontFamily = UiFont.For(block.Header && row == 0 ? 600 : 400),
                        FontSize = fontSize * 0.95,
                        Foreground = foreground,
                        FontWeight = block.Header && row == 0 ? FontWeights.SemiBold : FontWeights.Normal,
                        TextWrapping = TextWrapping.Wrap,
                    },
                };
                Grid.SetRow(cell, row);
                Grid.SetColumn(cell, column);
                grid.Children.Add(cell);
            }
        }

        return new ScrollViewer
        {
            Content = grid,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 6, 0, 6),
        };
    }

    /// <summary>把一段文字按上下标换成 Unicode 字符；换不了的**原样保留**（不吞内容）。</summary>
    public static string Scripted(string text, MathContent.Script script)
    {
        if (script == MathContent.Script.Normal)
        {
            return text;
        }

        var table = script == MathContent.Script.Super ? Superscripts : Subscripts;
        var output = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            output.Append(table.TryGetValue(character, out var mapped) ? mapped : character);
        }

        return output.ToString();
    }
}
