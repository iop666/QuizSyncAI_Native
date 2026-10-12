using System.Text;
using System.Text.RegularExpressions;

namespace QuizSync.Core;

/// <summary>
/// 题目内容的结构化解析（Windows 端）。规则逐条对齐 1.1.0 的 `packages/quizsync_ui/lib/src/math_text.dart`
/// —— 那份文件是双端共用的，桌面规格 §4.4 把它逐条列了出来：
///
/// | 规则 | 1.1.0 |
/// |---|---|
/// | 四种定界符 | `$$…$$` / `\[…\]`（块级）、`$…$` / `\(…\)`（行内） |
/// | 行内公式何时升块级 | 长度 &gt; `maxInlineChars`，`maxInline = (可用宽度 / 11).clamp(24, 96)` |
/// | 化学式判据 | 元素符号 + 数字（±电荷）切分；**至少 1 个元素/电子 且 至少 1 个数字或电荷**（挡住 `pH` / `MPa` / `2026`） |
/// | 化学式小标 | 字母段合并；数字下标、电荷上标 |
/// | 表格 | Markdown 竖线表 + 空格对齐表（≥2 行、每行被 ≥2 空格切成 ≥3 段且段数相同） |
/// | 渲染失败 | 等宽字体原样显示 LaTeX 源码（**绝不吞内容**） |
///
/// 与安卓端 `ui/mathcontent/MathContent.kt` 是同一套规则的两种语言实现 ——
/// 两边都在，是因为 2.0 把 Flutter 共享包拆掉了；规则**只有这一份文字描述**（上面那张表）。
/// </summary>
public static partial class MathContent
{
    /// <summary>块级公式（`$$…$$` / `\[…\]`）。</summary>
    public sealed record FormulaBlock(string Latex);

    /// <summary>表格（`Header = true` 表示 Markdown 表，首行是表头）。</summary>
    public sealed record TableBlock(IReadOnlyList<IReadOnlyList<string>> Rows, bool Header);

    /// <summary>普通段落：文字与行内公式交替。</summary>
    public sealed record ParagraphBlock(IReadOnlyList<InlineRun> Runs);

    public abstract record InlineRun;

    public sealed record TextRun(string Text) : InlineRun;

    public sealed record MathRun(string Latex) : InlineRun;

    /// <summary>内容块（段落 / 块级公式 / 表格）。</summary>
    public abstract record ContentBlock;

    public sealed record Paragraph(ParagraphBlock Block) : ContentBlock;

    public sealed record Formula(FormulaBlock Block) : ContentBlock;

    public sealed record Table(TableBlock Block) : ContentBlock;

    /// <summary>上下标类型。</summary>
    public enum Script
    {
        Normal,
        Super,
        Sub,
    }

    /// <summary>公式里的一段文字。</summary>
    public sealed record MathSpan(string Text, Script Script = Script.Normal);

    // ========================================================================
    // 定界符
    // ========================================================================

    /// <summary>
    /// 四种定界符，**顺序有意义**：`$$…$$` 必须排在 `$…$` 前，否则 `$$x$$` 会被拆成两段空公式。
    /// </summary>
    [GeneratedRegex(@"\$\$[\s\S]+?\$\$|\\\[[\s\S]+?\\\]|\$[^$\n]+?\$|\\\([\s\S]+?\\\)")]
    private static partial Regex MathDelimiter();

    public static bool IsDisplayDelimiter(string raw) =>
        raw.StartsWith("$$", StringComparison.Ordinal) || raw.StartsWith("\\[", StringComparison.Ordinal);

    /// <summary>去掉定界符：两字符的（`$$`/`\[`/`\(`）去 2，单字符的（`$`）去 1。</summary>
    public static string LatexOf(string raw) =>
        IsDisplayDelimiter(raw) || raw.StartsWith("\\(", StringComparison.Ordinal)
            ? raw[2..^2]
            : raw[1..^1];

    /// <summary>行内公式的长度上限（1.1.0：`(可用宽度 / 11).clamp(24, 96)`）。</summary>
    public static int MaxInlineChars(double availableWidth) =>
        (int)Math.Clamp(availableWidth / 11.0, 24, 96);

    // ========================================================================
    // 解析
    // ========================================================================

    /// <summary>把一段文本切成「段落 / 块级公式 / 表格」。</summary>
    public static IReadOnlyList<ContentBlock> Parse(string text, int maxInlineChars = 40)
    {
        ArgumentNullException.ThrowIfNull(text);

        var blocks = new List<ContentBlock>();
        var paragraph = new List<string>();

        void FlushParagraph()
        {
            if (paragraph.Count == 0)
            {
                return;
            }

            blocks.AddRange(ParagraphBlocks(string.Join('\n', paragraph), maxInlineChars));
            paragraph.Clear();
        }

        var lines = text.Split('\n');
        var index = 0;
        while (index < lines.Length)
        {
            var markdown = MarkdownTableAt(lines, index);
            if (markdown is not null)
            {
                FlushParagraph();
                blocks.Add(new Table(markdown.Value.Block));
                index = markdown.Value.Next;
                continue;
            }

            var spaced = SpaceTableAt(lines, index);
            if (spaced is not null)
            {
                FlushParagraph();
                blocks.Add(new Table(spaced.Value.Block));
                index = spaced.Value.Next;
                continue;
            }

            paragraph.Add(lines[index]);
            index++;
        }

        FlushParagraph();
        if (blocks.Count == 0)
        {
            blocks.Add(new Paragraph(new ParagraphBlock([new TextRun(string.Empty)])));
        }

        return blocks;
    }

    /// <summary>段落 → 「段落块 + 块级公式块」交替序列。</summary>
    private static List<ContentBlock> ParagraphBlocks(string text, int maxInlineChars)
    {
        var blocks = new List<ContentBlock>();
        var runs = new List<InlineRun>();
        var position = 0;

        void FlushRuns()
        {
            if (runs.Count == 0)
            {
                return;
            }

            blocks.Add(new Paragraph(new ParagraphBlock([.. runs])));
            runs.Clear();
        }

        foreach (Match match in MathDelimiter().Matches(text))
        {
            var raw = match.Value;
            var latex = LatexOf(raw);
            if (match.Index > position)
            {
                runs.Add(new TextRun(text[position..match.Index]));
            }

            if (IsDisplayDelimiter(raw) || latex.Length > maxInlineChars)
            {
                FlushRuns();
                blocks.Add(new Formula(new FormulaBlock(latex)));
            }
            else
            {
                runs.Add(new MathRun(latex));
            }

            position = match.Index + match.Length;
        }

        if (position < text.Length)
        {
            runs.Add(new TextRun(text[position..]));
        }

        FlushRuns();
        if (blocks.Count == 0)
        {
            blocks.Add(new Paragraph(new ParagraphBlock([new TextRun(text)])));
        }

        return blocks;
    }

    // ========================================================================
    // 表格
    // ========================================================================

    [GeneratedRegex(@"^\s*\|.*\|\s*$")]
    private static partial Regex PipeRow();

    [GeneratedRegex(@"^\s*\|[\s:|-]+\|\s*$")]
    private static partial Regex PipeSeparator();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultiSpace();

    private static (TableBlock Block, int Next)? MarkdownTableAt(string[] lines, int start)
    {
        if (start + 1 >= lines.Length)
        {
            return null;
        }

        if (!PipeRow().IsMatch(lines[start]) || !PipeSeparator().IsMatch(lines[start + 1]))
        {
            return null;
        }

        var rows = new List<IReadOnlyList<string>> { SplitPipe(lines[start]) };
        var index = start + 2;
        while (index < lines.Length && PipeRow().IsMatch(lines[index]))
        {
            rows.Add(SplitPipe(lines[index]));
            index++;
        }

        return rows[0].Count < 2 ? null : (new TableBlock(rows, Header: true), index);
    }

    private static List<string> SplitPipe(string line)
    {
        var trimmed = line.Trim();
        var body = trimmed.StartsWith('|') ? trimmed[1..] : trimmed;
        var inner = body.EndsWith('|') ? body[..^1] : body;
        return [.. inner.Split('|').Select(cell => cell.Trim())];
    }

    /// <summary>空格对齐表：连续 ≥2 行，每行被 2+ 空格切成 ≥3 段且段数相同。</summary>
    private static (TableBlock Block, int Next)? SpaceTableAt(string[] lines, int start)
    {
        if (start >= lines.Length)
        {
            return null;
        }

        var first = SpaceCells(lines[start]);
        if (first is null)
        {
            return null;
        }

        var rows = new List<IReadOnlyList<string>> { first };
        var index = start + 1;
        while (index < lines.Length)
        {
            var cells = SpaceCells(lines[index]);
            if (cells is null || cells.Count != first.Count)
            {
                break;
            }

            rows.Add(cells);
            index++;
        }

        return rows.Count < 2 ? null : (new TableBlock(rows, Header: false), index);
    }

    private static List<string>? SpaceCells(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length > 160)
        {
            return null;
        }

        var cells = MultiSpace().Split(trimmed).ToList();
        return cells.Count < 3 || cells.Any(cell => cell.Length == 0) ? null : cells;
    }

    // ========================================================================
    // 化学式
    // ========================================================================

    private static readonly HashSet<string> Elements = new(StringComparer.Ordinal)
    {
        "H", "He", "Li", "Be", "B", "C", "N", "O", "F", "Ne", "Na", "Mg", "Al",
        "Si", "P", "S", "Cl", "Ar", "K", "Ca", "Sc", "Ti", "V", "Cr", "Mn", "Fe",
        "Co", "Ni", "Cu", "Zn", "Ga", "Ge", "As", "Se", "Br", "Kr", "Rb", "Sr",
        "Y", "Zr", "Nb", "Mo", "Tc", "Ru", "Rh", "Pd", "Ag", "Cd", "In", "Sn",
        "Sb", "Te", "I", "Xe", "Cs", "Ba", "La", "Ce", "Pr", "Nd", "Pm", "Sm",
        "Eu", "Gd", "Tb", "Dy", "Ho", "Er", "Tm", "Yb", "Lu", "Hf", "Ta", "W",
        "Re", "Os", "Ir", "Pt", "Au", "Hg", "Tl", "Pb", "Bi", "Po", "At", "Rn",
        "Fr", "Ra", "Ac", "Th", "Pa", "U", "Np", "Pu", "Am", "Cm", "Bk", "Cf",
        "Es", "Fm", "Md", "No", "Lr", "Rf", "Db", "Sg", "Bh", "Hs", "Mt", "Ds",
        "Rg", "Cn", "Nh", "Fl", "Mc", "Lv", "Ts", "Og",
    };

    /// <summary>化学式候选：元素符号 + 数字（可带结尾电荷），且不紧贴英文字母。</summary>
    [GeneratedRegex(@"(?<![A-Za-z])(?:[A-Z][a-z]?(?:[A-Z][a-z]?|\d+)*(?:[+\u2212-])?|e[+\u2212-])(?![\dA-Za-z])")]
    private static partial Regex ChemistryCandidate();

    [GeneratedRegex(@"[A-Za-z][a-z]?|\d+|[+\u2212-]")]
    private static partial Regex ChemistryPart();

    /// <summary>`⇌`（U+21CC）随包字体里没有这个字形，当行内公式画。</summary>
    public const string Equilibrium = "⇌";

    /// <summary>这段文字里有没有需要特殊渲染的东西（化学式小标或化学平衡箭头）。</summary>
    public static bool HasInlineFormatting(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Contains(Equilibrium, StringComparison.Ordinal)
            || ChemistryCandidate().Matches(text).Any(match => IsChemicalFormula(match.Value));
    }

    /// <summary>
    /// 这一串是不是**化学式**（1.1.0 的判据，桌面规格 §4.4 逐字）：
    /// 元素符号 + 数字（±电荷）切分后，**至少 1 个元素/电子**，**且至少 1 个数字或电荷**。
    ///
    /// 后一条是关键：它挡住 `pH`、`MPa`、`2026` 这类看着像公式的普通写法。
    /// 代价是 `NaCl`（没有数字）不会被判成化学式 —— 这是 1.1.0 的既定行为，照做。
    /// </summary>
    public static bool IsChemicalFormula(string raw)
    {
        if (raw.Length < 2)
        {
            return false;
        }

        if (raw is "e-" or "e+")
        {
            return true;
        }

        var parts = ChemistryPart().Matches(raw).Select(match => match.Value).ToList();
        if (parts.Count < 2)
        {
            return false;
        }

        var hasElement = false;
        var hasNumberOrCharge = false;
        foreach (var part in parts)
        {
            if (char.IsAsciiDigit(part[0]))
            {
                hasNumberOrCharge = true;
            }
            else if (part is "+" or "-" or "\u2212")
            {
                hasNumberOrCharge = true;
            }
            else if (Elements.Contains(part))
            {
                hasElement = true;
            }
            else
            {
                return false;
            }
        }

        return hasElement && hasNumberOrCharge;
    }

    /// <summary>普通文字 → 化学式小标片段（`ZnCO3` → Zn C O₃、`Fe2+` → Fe²⁺、`e-` → e⁻）。</summary>
    public static IReadOnlyList<MathSpan> ChemistrySpans(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var spans = new List<MathSpan>();
        var position = 0;

        foreach (Match match in ChemistryCandidate().Matches(text))
        {
            var raw = match.Value;
            if (!IsChemicalFormula(raw))
            {
                continue;
            }

            if (match.Index > position)
            {
                spans.Add(new MathSpan(text[position..match.Index]));
            }

            var group = new StringBuilder();
            foreach (Match part in ChemistryPart().Matches(raw))
            {
                var piece = part.Value;
                if (char.IsAsciiDigit(piece[0]))
                {
                    // 数字：先冲掉前面攒的字母段（1.1.0：字母段合并成一个 Text）
                    FlushGroup();
                    spans.Add(new MathSpan(piece, Script.Sub));
                }
                else if (piece is "+" or "-" or "\u2212")
                {
                    FlushGroup();
                    spans.Add(new MathSpan(piece == "+" ? "+" : "\u2212", Script.Super));
                }
                else
                {
                    group.Append(piece);
                }
            }

            FlushGroup();
            position = match.Index + match.Length;

            void FlushGroup()
            {
                if (group.Length == 0)
                {
                    return;
                }

                spans.Add(new MathSpan(group.ToString()));
                group.Clear();
            }
        }

        if (position < text.Length)
        {
            spans.Add(new MathSpan(text[position..]));
        }

        return spans;
    }

    // ========================================================================
    // LaTeX 子集 → 片段
    // ========================================================================

    private static readonly Dictionary<string, string> LatexSymbols = new(StringComparer.Ordinal)
    {
        ["times"] = "×", ["div"] = "÷", ["pm"] = "±", ["mp"] = "∓", ["cdot"] = "·",
        ["leq"] = "≤", ["le"] = "≤", ["geq"] = "≥", ["ge"] = "≥", ["neq"] = "≠", ["ne"] = "≠",
        ["approx"] = "≈", ["equiv"] = "≡", ["propto"] = "∝", ["sim"] = "∼",
        ["rightarrow"] = "→", ["to"] = "→", ["leftarrow"] = "←", ["leftrightarrow"] = "↔",
        ["Rightarrow"] = "⇒", ["Leftarrow"] = "⇐", ["Leftrightarrow"] = "⇔",
        ["uparrow"] = "↑", ["downarrow"] = "↓", ["infty"] = "∞", ["partial"] = "∂",
        ["nabla"] = "∇", ["sum"] = "∑", ["prod"] = "∏", ["int"] = "∫", ["sqrt"] = "√",
        ["angle"] = "∠", ["perp"] = "⊥", ["parallel"] = "∥", ["triangle"] = "△",
        ["degree"] = "°", ["circ"] = "∘", ["prime"] = "′", ["ldots"] = "…", ["cdots"] = "⋯",
        ["alpha"] = "α", ["beta"] = "β", ["gamma"] = "γ", ["delta"] = "δ", ["epsilon"] = "ε",
        ["varepsilon"] = "ε", ["zeta"] = "ζ", ["eta"] = "η", ["theta"] = "θ", ["iota"] = "ι",
        ["kappa"] = "κ", ["lambda"] = "λ", ["mu"] = "μ", ["nu"] = "ν", ["xi"] = "ξ",
        ["pi"] = "π", ["rho"] = "ρ", ["sigma"] = "σ", ["tau"] = "τ", ["upsilon"] = "υ",
        ["phi"] = "φ", ["varphi"] = "φ", ["chi"] = "χ", ["psi"] = "ψ", ["omega"] = "ω",
        ["Gamma"] = "Γ", ["Delta"] = "Δ", ["Theta"] = "Θ", ["Lambda"] = "Λ", ["Xi"] = "Ξ",
        ["Pi"] = "Π", ["Sigma"] = "Σ", ["Phi"] = "Φ", ["Psi"] = "Ψ", ["Omega"] = "Ω",
    };

    private static readonly HashSet<string> LatexIgnore = new(StringComparer.Ordinal)
    {
        "left", "right", "displaystyle", "textstyle", "limits", "nolimits",
        "mathrm", "mathbf", "mathit", "text", "operatorname", "quad", "qquad",
    };

    /// <summary>需要两侧留白的二元运算符（LaTeX 的 `\mathbin`）。</summary>
    private static readonly HashSet<string> BinaryOperators = new(StringComparer.Ordinal)
    {
        "=", "+", "\u2212", "-", "\u00B1", "\u2213", "\u00D7", "\u00F7",
        "\u2264", "\u2265", "\u2260", "\u2248", "\u2261", "\u221D", "\u223C",
        "\u2192", "\u2190", "\u2194", "\u21D2", "\u21D0", "\u21D4",
    };

    /// <summary>细空格与发丝空格（LaTeX 的排版间距不是字符，是规则）。</summary>
    public const string ThinSpace = "\u2009";

    public const string HairSpace = "\u200A";

    /// <summary>
    /// LaTeX（子集）→ 片段序列。支持 `^{}`/`_` 上下标、`\frac`、`\sqrt`、上面那张符号表、
    /// `\text{…}` 之类的排版命令；**不支持的按源码显示，绝不吞内容**。
    /// </summary>
    public static IReadOnlyList<MathSpan> ParseLatex(string latex)
    {
        ArgumentNullException.ThrowIfNull(latex);

        var spans = new List<MathSpan>();
        var index = 0;

        while (index < latex.Length)
        {
            var character = latex[index];
            switch (character)
            {
                case '\\':
                {
                    var name = ReadCommandName(latex, index + 1);
                    if (name.Length == 0)
                    {
                        spans.Add(new MathSpan(" "));
                        index++;
                        continue;
                    }

                    if (name is "frac" or "dfrac" or "tfrac")
                    {
                        var numerator = ReadGroup(latex, index + 1 + name.Length);
                        var denominator = numerator is null ? null : ReadGroup(latex, numerator.Value.Next);
                        if (numerator is not null && denominator is not null)
                        {
                            // 没有数学排版引擎：分数按 `a/b` 显示（内容一个都不丢）
                            spans.Add(new MathSpan(Flatten(ParseLatex(numerator.Value.Text)) + "/" +
                                                   Flatten(ParseLatex(denominator.Value.Text))));
                            index = denominator.Value.Next;
                            continue;
                        }

                        spans.Add(new MathSpan("\\" + name));
                        index += 1 + name.Length;
                        continue;
                    }

                    if (name == "sqrt")
                    {
                        var group = ReadGroup(latex, index + 1 + name.Length);
                        spans.Add(new MathSpan("√"));
                        if (group is not null)
                        {
                            spans.AddRange(ParseLatex(group.Value.Text));
                            index = group.Value.Next;
                            continue;
                        }

                        index += 1 + name.Length;
                        continue;
                    }

                    if (LatexIgnore.Contains(name))
                    {
                        var group = ReadGroup(latex, index + 1 + name.Length);
                        if (group is not null && name is "text" or "mathrm" or "mathbf" or "mathit" or "operatorname")
                        {
                            spans.Add(new MathSpan(group.Value.Text));
                            index = group.Value.Next;
                            continue;
                        }

                        index += 1 + name.Length;
                        continue;
                    }

                    spans.Add(new MathSpan(LatexSymbols.TryGetValue(name, out var symbol) ? symbol : name));
                    index += 1 + name.Length;
                    continue;
                }

                case '^':
                case '_':
                {
                    var script = character == '^' ? Script.Super : Script.Sub;
                    var group = ReadGroup(latex, index + 1);
                    if (group is not null)
                    {
                        spans.Add(new MathSpan(group.Value.Text, script));
                        index = group.Value.Next;
                    }
                    else
                    {
                        index++;
                    }

                    continue;
                }

                case '{':
                case '}':
                    index++;
                    continue;

                default:
                    spans.Add(new MathSpan(character.ToString()));
                    index++;
                    continue;
            }
        }

        if (spans.Count == 0)
        {
            spans.Add(new MathSpan(latex));
        }

        return SpaceOut(spans);
    }

    /// <summary>
    /// 给公式片段补**排版间距**：二元运算符两侧细空格、上标后发丝空格。
    /// 逐字符拼出来的公式一个空格都没有，看起来就是「字挨在一起」（安卓端实测修过这条）。
    /// </summary>
    private static List<MathSpan> SpaceOut(IReadOnlyList<MathSpan> spans)
    {
        var output = new List<MathSpan>();
        foreach (var span in spans)
        {
            var isBinary = span.Script == Script.Normal && BinaryOperators.Contains(span.Text);
            var isSign = span.Text is "+" or "-" or "\u2212";
            var previous = output.Count == 0 ? null : output[^1].Text;
            var unary = isSign && (previous is null || previous.EndsWith('(') ||
                                   BinaryOperators.Contains(previous) || previous == ThinSpace);

            if (isBinary && !unary)
            {
                output.Add(new MathSpan(ThinSpace));
            }

            output.Add(span);

            if (isBinary)
            {
                output.Add(new MathSpan(ThinSpace));
            }

            if (span.Script == Script.Super)
            {
                output.Add(new MathSpan(HairSpace));
            }
        }

        return output;
    }

    /// <summary>把片段拍平成纯文本（没有排版引擎时的降级表示）。</summary>
    public static string Flatten(IReadOnlyList<MathSpan> spans) =>
        string.Concat(spans.Select(span => span.Text));

    private static string ReadCommandName(string latex, int start)
    {
        var index = start;
        while (index < latex.Length && char.IsAsciiLetter(latex[index]))
        {
            index++;
        }

        return latex[start..index];
    }

    /// <summary>读一个参数：`{…}` 或单个字符；读不到返回 null（模型偶尔会把公式截断）。</summary>
    private static (string Text, int Next)? ReadGroup(string latex, int start)
    {
        var index = start;
        while (index < latex.Length && latex[index] == ' ')
        {
            index++;
        }

        if (index >= latex.Length)
        {
            return null;
        }

        if (latex[index] != '{')
        {
            return (latex[index].ToString(), index + 1);
        }

        var depth = 0;
        var content = new StringBuilder();
        for (var cursor = index; cursor < latex.Length; cursor++)
        {
            var character = latex[cursor];
            if (character == '{')
            {
                depth++;
                if (depth > 1)
                {
                    content.Append(character);
                }
            }
            else if (character == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return (content.ToString(), cursor + 1);
                }

                content.Append(character);
            }
            else
            {
                content.Append(character);
            }
        }

        return null;
    }
}
