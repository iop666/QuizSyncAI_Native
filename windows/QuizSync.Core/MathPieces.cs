using System.Text;

namespace QuizSync.Core;

/// <summary>
/// 公式的**排版树**与解析（Windows 端）。
///
/// 为什么需要它（而不是把 LaTeX 摊平成一行文字）：
/// 用户贴来的真题里有 `\frac{a_{n+1}}{2^{n+1}}-\frac{a_n}{2^n}=\frac12` ——
/// **嵌套分数**。摊平成 `a/B` 之后，数学上能读、看起来却完全不是公式；
/// `\sum_{k=1}^{n}` 的上下限、`\sqrt{}` 的根号、`\mathbb N` 的黑板粗体同理。
/// 所以解析结果必须是**结构**（分子/分母、底/指数、上限/下限），由渲染层落成真的排版。
///
/// 这条路的取舍（与 WebView2 + KaTeX 相比）：
/// * ✅ 无额外依赖、离线可用、随窗口缩放都清晰（本机**没有 WebView2 运行时**，
///   而那是个约 150MB 的独立运行时，交付物不该为公式背这个包袱）；
/// * ❌ 只覆盖 **LaTeX 子集**（中学范围的分数/根号/上下标/大型运算符/希腊字母/黑板粗体），
///   矩阵、分段函数、复杂对齐等不支持 —— 不支持的一律**降级为源码显示，绝不吞内容**。
/// </summary>
public static partial class MathContent
{
    /// <summary>排版树的节点。</summary>
    public abstract record MathPiece;

    /// <summary>一段普通文字（符号已替换、上下标已展开为结构）。</summary>
    public sealed record MathText(string Text, Script Script = Script.Normal) : MathPiece;

    /// <summary>分数：真分数线，分子在上、分母在下。</summary>
    public sealed record MathFraction(IReadOnlyList<MathPiece> Numerator, IReadOnlyList<MathPiece> Denominator) : MathPiece;

    /// <summary>根号：`\sqrt{x}`（根指数暂不支持，按平方根画）。</summary>
    public sealed record MathRoot(IReadOnlyList<MathPiece> Body) : MathPiece;

    /// <summary>大型运算符（`\sum` / `\int` / `\prod`）：符号 + 上限/下限（真上下位置）。</summary>
    public sealed record MathBigOperator(string Symbol, IReadOnlyList<MathPiece>? Lower, IReadOnlyList<MathPiece>? Upper) : MathPiece;

    /// <summary>上下标挂在**结构体**上（分数/根号/大型运算符的右上角）。</summary>
    public sealed record MathScripted(MathPiece Base, IReadOnlyList<MathPiece>? Sub, IReadOnlyList<MathPiece>? Sup) : MathPiece;

    /// <summary>`\mathbb` 的黑板粗体字母（Unicode 有现成字符，直接用，不必画）。</summary>
    // 值用 string：𝔸 / 𝕀 / 𝟘 这类黑板粗体在 U+1D400 区，是**代理对**，char 字面量放不下（CS1012）
    private static readonly Dictionary<char, string> BlackboardBold = new()
    {
        ['A'] = "𝔸", ['B'] = "𝔹", ['C'] = "ℂ", ['D'] = "𝔻", ['E'] = "𝔼", ['F'] = "𝔽", ['G'] = "𝔾",
        ['H'] = "ℍ", ['I'] = "𝕀", ['J'] = "𝕁", ['K'] = "𝕂", ['L'] = "𝕃", ['M'] = "𝕄", ['N'] = "ℕ",
        ['O'] = "𝕆", ['P'] = "ℙ", ['Q'] = "ℚ", ['R'] = "ℝ", ['S'] = "𝕊", ['T'] = "𝕋", ['U'] = "𝕌",
        ['V'] = "𝕍", ['W'] = "𝕎", ['X'] = "𝕏", ['Y'] = "𝕐", ['Z'] = "ℤ",
        ['0'] = "𝟘", ['1'] = "𝟙", ['2'] = "𝟚", ['3'] = "𝟛", ['4'] = "𝟜", ['5'] = "𝟝", ['6'] = "𝟞",
        ['7'] = "𝟟", ['8'] = "𝟠", ['9'] = "𝟡",
    };

    /// <summary>大型运算符（自带上下限位置）。</summary>
    private static readonly Dictionary<string, string> BigOperators = new(StringComparer.Ordinal)
    {
        ["sum"] = "∑", ["prod"] = "∏", ["int"] = "∫", ["iint"] = "∬", ["oint"] = "∮",
        ["lim"] = "lim", ["bigcup"] = "⋃", ["bigcap"] = "⋂",
    };

    /// <summary>LaTeX 子集 → 排版树。</summary>
    public static IReadOnlyList<MathPiece> ParseLatexPieces(string latex)
    {
        ArgumentNullException.ThrowIfNull(latex);
        var cursor = 0;
        return ParseSequence(latex, ref cursor, stopAtBrace: false);
    }

    /// <summary>
    /// 解析一段序列，直到字符串结尾或（`stopAtBrace` 时）遇到未配对的 `}`。
    /// 连续的上下标会挂到**前一个节点**上（`x^2_1`、`\sum_{k=1}^n` 都是这个形态）。
    /// </summary>
    /// <summary>
    /// 取「一个 token」当命令参数（LaTeX 允许省略花括号：`\mathbb N`、`\text a`、`\mathbb\alpha`）。
    /// 命令后的空格按 LaTeX 规则吃掉；取不到就返回 null，调用方照旧降级、**不吞内容**。
    ///
    /// 为什么需要它：真实数据里就有 `\mathbb N^*` —— 少了这条，`N` 会在渲染时消失
    /// （`\mathbb` 的 `N` 与后面的 `^*` 上标一起看起来就是"这题印错了"）。
    /// </summary>
    private static IReadOnlyList<MathPiece>? TakeSingleToken(string latex, ref int cursor)
    {
        var index = cursor;
        while (index < latex.Length && latex[index] == ' ')
        {
            index++;
        }

        if (index >= latex.Length)
        {
            return null;
        }

        if (latex[index] == '\\')
        {
            // 一个命令（`\mathbb\alpha`）：连反斜杠带字母取出来，交回正常解析路径。
            var start = index;
            index++;
            while (index < latex.Length && char.IsAsciiLetter(latex[index]))
            {
                index++;
            }

            var command = latex[start..index];
            cursor = index;
            return ParseLatexPieces(command);
        }

        // 一个字符（代理对算一个，免得把 emoji/生僻字劈成两半）
        var length = char.IsHighSurrogate(latex[index]) && index + 1 < latex.Length ? 2 : 1;
        var single = latex.Substring(index, length);
        cursor = index + length;
        return ParseLatexPieces(single);
    }

    private static List<MathPiece> ParseSequence(string latex, ref int cursor, bool stopAtBrace)
    {
        var pieces = new List<MathPiece>();

        while (cursor < latex.Length)
        {
            var character = latex[cursor];

            if (character == '}')
            {
                if (stopAtBrace)
                {
                    cursor++;
                    break;
                }

                cursor++;
                continue;
            }

            if (character == '{')
            {
                // 裸分组：内容并入当前序列（`{a_n}` 的花括号不显示，与 LaTeX 一致）
                cursor++;
                pieces.AddRange(ParseSequence(latex, ref cursor, stopAtBrace: true));
                continue;
            }

            if (character == '\\')
            {
                var nameStart = cursor + 1;
                var nameEnd = nameStart;
                while (nameEnd < latex.Length && char.IsAsciiLetter(latex[nameEnd]))
                {
                    nameEnd++;
                }

                var name = latex[nameStart..nameEnd];

                // `\{` / `\}` / `\,` 这类：反斜杠后不是字母
                if (name.Length == 0)
                {
                    var escaped = nameEnd < latex.Length ? latex[nameEnd] : ' ';
                    pieces.Add(new MathText(escaped switch
                    {
                        '{' => "{",
                        '}' => "}",
                        '%' => "%",
                        '&' => "&",
                        '#' => "#",
                        '_' => "_",
                        ',' or ';' or ':' or ' ' => ThinSpace,
                        _ => escaped.ToString(),
                    }));
                    cursor = nameEnd + 1;
                    continue;
                }

                cursor = nameEnd;

                if (name is "frac" or "dfrac" or "tfrac")
                {
                    var numerator = TakeGroup(latex, ref cursor);
                    var denominator = TakeGroup(latex, ref cursor);
                    if (numerator is not null && denominator is not null)
                    {
                        pieces.Add(new MathFraction(numerator, denominator));
                    }
                    else
                    {
                        // 公式被截断（模型偶尔会）—— 显示出来，别把内容丢了
                        pieces.Add(new MathText("\\" + name));
                    }

                    continue;
                }

                if (name == "sqrt")
                {
                    var body = TakeGroup(latex, ref cursor);
                    pieces.Add(body is null ? new MathText("√") : new MathRoot(body));
                    continue;
                }

                if (name is "mathbb" or "mathbf" or "mathrm" or "text" or "operatorname" or "mathit")
                {
                    var group = TakeGroup(latex, ref cursor)
                        // ⚠️ LaTeX **允许省略花括号**：用户真题里写的就是 `\mathbb N^*`（不是 `\mathbb{N}`）。
                        //    原实现只认花括号 → `\mathbb N` 取不到参数 → `Blackboard("")` = **空串，N 被吞掉**，
                        //    违反"绝不吞内容"（这条是拿真实数据跑出来的，见 `RealDataMathTests`）。
                        ?? TakeSingleToken(latex, ref cursor);
                    var content = group is null ? string.Empty : FlattenPieces(group);
                    pieces.Add(new MathText(name == "mathbb" ? Blackboard(content) : content));
                    continue;
                }

                if (BigOperators.TryGetValue(name, out var big))
                {
                    // 大型运算符：上下限紧随其后（`\sum_{k=1}^{n}`），也允许没有
                    var node = new MathBigOperator(big, null, null);
                    var lower = (IReadOnlyList<MathPiece>?)null;
                    var upper = (IReadOnlyList<MathPiece>?)null;
                    while (cursor < latex.Length && (latex[cursor] == '_' || latex[cursor] == '^'))
                    {
                        var isLower = latex[cursor] == '_';
                        cursor++;
                        var group = TakeGroup(latex, ref cursor) ?? [];
                        if (isLower)
                        {
                            lower = group;
                        }
                        else
                        {
                            upper = group;
                        }
                    }

                    pieces.Add(node with { Lower = lower, Upper = upper });
                    continue;
                }

                if (LatexIgnore.Contains(name))
                {
                    _ = TakeGroup(latex, ref cursor);
                    continue;
                }

                pieces.Add(new MathText(LatexSymbols.TryGetValue(name, out var symbol) ? symbol : name));
                continue;
            }

            if (character is '^' or '_')
            {
                var isSuper = character == '^';
                cursor++;
                var group = TakeGroup(latex, ref cursor);
                var basePiece = pieces.Count > 0 ? pieces[^1] : new MathText(string.Empty);
                if (pieces.Count > 0)
                {
                    pieces.RemoveAt(pieces.Count - 1);
                }

                var existing = basePiece as MathScripted;
                pieces.Add(new MathScripted(
                    existing?.Base ?? basePiece,
                    isSuper ? existing?.Sub : group,
                    isSuper ? group : existing?.Sup));
                continue;
            }

            // 普通字符：连续字母/数字并成一段（排版更紧凑，也少些节点）
            var runStart = cursor;
            while (cursor < latex.Length && latex[cursor] is not ('\\' or '{' or '}' or '^' or '_'))
            {
                cursor++;
            }

            var run = latex[runStart..cursor];
            if (run.Length > 0)
            {
                pieces.Add(new MathText(LatexSymbols.TryGetValue(run, out var whole) ? whole : run));
            }
        }

        return pieces;
    }

    /// <summary>读一个参数：`{…}`（可嵌套）或单个字符。</summary>
    private static List<MathPiece>? TakeGroup(string latex, ref int cursor)
    {
        while (cursor < latex.Length && latex[cursor] == ' ')
        {
            cursor++;
        }

        if (cursor >= latex.Length)
        {
            return null;
        }

        if (latex[cursor] != '{')
        {
            var single = latex[cursor];
            cursor++;
            return [new MathText(single.ToString())];
        }

        cursor++;
        return ParseSequence(latex, ref cursor, stopAtBrace: true);
    }

    /// <summary>把排版树拍平成纯文本（降级显示、以及 `\text{…}` 之类的内部取值）。</summary>
    public static string FlattenPieces(IReadOnlyList<MathPiece> pieces)
    {
        var text = new StringBuilder();
        foreach (var piece in pieces)
        {
            switch (piece)
            {
                case MathText plain:
                    text.Append(plain.Text);
                    break;
                case MathFraction fraction:
                    text.Append(FlattenPieces(fraction.Numerator)).Append('/').Append(FlattenPieces(fraction.Denominator));
                    break;
                case MathRoot root:
                    text.Append('√').Append(FlattenPieces(root.Body));
                    break;
                case MathBigOperator big:
                    if (big.Lower is not null)
                    {
                        text.Append(FlattenPieces(big.Lower));
                    }

                    text.Append(big.Symbol);
                    if (big.Upper is not null)
                    {
                        text.Append('^').Append(FlattenPieces(big.Upper));
                    }

                    break;
                case MathScripted scripted:
                    text.Append(FlattenPieces([scripted.Base]));
                    if (scripted.Sub is not null)
                    {
                        text.Append('_').Append(FlattenPieces(scripted.Sub));
                    }

                    if (scripted.Sup is not null)
                    {
                        text.Append('^').Append(FlattenPieces(scripted.Sup));
                    }

                    break;
            }
        }

        return text.ToString();
    }

    private static string Blackboard(string text) =>
        string.Concat(text.Select(character =>
            BlackboardBold.TryGetValue(character, out var bold) ? bold : character.ToString()));
}
