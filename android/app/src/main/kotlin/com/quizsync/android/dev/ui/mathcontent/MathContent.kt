// 题目内容的结构化解析：**纯数据 + 纯函数**，不依赖 Compose，可直接跑 JVM 单测。
//
// 规则逐条来自 1.1.0 的 `packages/quizsync_ui/lib/src/math_text.dart`（那个文件是 M6/M20/M21/M22
// 四轮用户反馈的产物，注释里写清了每一处为什么）：
//  * 四种公式定界符：`$$…$$` / `\[…\]`（块级）与 `$…$` / `\(…\)`（行内）——
//    M20 之前只认 `$…$`，而模型对物理题吐的是 LaTeX 原生的 `\(…\)`，**整段显示成源码**；
//  * 裸化学式（`ZnCO3` / `Fe2+` / `e-`）按元素符号切分，数字下沉成下标、电荷上浮成上标 ——
//    M21：材料里的化学式显示成普通文字，用户报「小标没显示」；
//  * 表格：材料里的「空格对齐表」与 Markdown 竖线表都画成真表格 ——
//    比例字体下空格对齐的列会挤成一团；
//  * 过长的行内公式搬成块级（行内公式是**不可断行**的整体，太长会把整段挤出屏幕）；
//  * `⇌`（化学平衡箭头）随包字体里没有这个字形，当行内公式画。
package com.quizsync.android.dev.ui.mathcontent

/** 一段内容块。 */
sealed interface ContentBlock

/** 普通段落：由若干行内片段组成。 */
data class ParagraphBlock(val runs: List<InlineRun>) : ContentBlock

/** 独立成行的公式（可横向滚动）。 */
data class FormulaBlock(val latex: String) : ContentBlock

/** 表格（`header = true` 表示 Markdown 表，第一行是表头）。 */
data class TableBlock(val rows: List<List<String>>, val header: Boolean) : ContentBlock

/** 行内片段。 */
sealed interface InlineRun

/** 普通文字（渲染时还会做化学式小标）。 */
data class TextRun(val text: String) : InlineRun

/** 行内公式（已去掉定界符）。 */
data class MathRun(val latex: String) : InlineRun

/** 公式里的一段文字（含上/下标）。 */
enum class Script { Normal, Super, Sub }

/**
 * 公式里的一个最小片段。
 *
 * 为什么不用现成的数学排版库：Compose 上没有等价物（Flutter 侧是 `flutter_math_fork`），
 * 而按 2.0 的计划（`NATIVE-2.0-PLAN` §7）两端要走 KaTeX —— 那是**另一轮**的事。
 * 这一轮先把「看得懂」做出来：符号替换 + 上下标 + 分数线，剩下的按源码显示（绝不吞内容）。
 */
data class MathSpan(
    val text: String,
    val script: Script = Script.Normal,
    /** 变量用斜体、数字与算符用正体（教科书的写法）。 */
    val italic: Boolean = false,
)

/** 公式里的一「块」：要么是一串片段，要么是一个分数（分数线要画出来）。 */
sealed interface MathPiece

data class MathSpans(val spans: List<MathSpan>) : MathPiece

data class MathFraction(val numerator: List<MathSpan>, val denominator: List<MathSpan>) : MathPiece

// ============================================================================
// 定界符
// ============================================================================

/**
 * 四种定界符，**顺序有意义**：`$$…$$` 必须排在 `$…$` 前面，否则 `$$x$$` 会被拆成两段空公式；
 * `\[…\]` / `\(…\)` 同理要先于任何单字符形式尝试。
 */
val mathDelimiterRegex = Regex(
    """\$\$[\s\S]+?\$\$""" +          // $$…$$ 块级
        """|\\\[[\s\S]+?\\]""" +      // \[…\] 块级
        """|\$[^$\n]+?\$""" +         // $…$ 行内
        """|\\\([\s\S]+?\\\)""",      // \(…\) 行内
)

/** 块级定界符（`$$…$$` / `\[…\]`）。 */
fun isDisplayDelimiter(raw: String): Boolean =
    raw.startsWith("\$\$") || raw.startsWith("\\[")

/** 去掉定界符：两字符的（`$$`、`\[`、`\(`）去 2，单字符的（`$`）去 1。 */
fun latexOf(raw: String): String =
    if (isDisplayDelimiter(raw) || raw.startsWith("\\(")) raw.substring(2, raw.length - 2)
    else raw.substring(1, raw.length - 1)

// ============================================================================
// 解析：文本 → 内容块
// ============================================================================

/**
 * 把一段文本切成「段落 / 公式块 / 表格块」。
 *
 * [maxInlineChars] 是行内公式的长度上限（调用方按可用宽度算），超过就搬成块级公式。
 */
fun parseContentBlocks(text: String, maxInlineChars: Int = 40): List<ContentBlock> {
    val blocks = mutableListOf<ContentBlock>()
    val paragraph = mutableListOf<String>()

    fun flushParagraph() {
        if (paragraph.isEmpty()) return
        blocks += paragraphBlocks(paragraph.joinToString("\n"), maxInlineChars)
        paragraph.clear()
    }

    val lines = text.split("\n")
    var index = 0
    while (index < lines.size) {
        val markdown = markdownTableAt(lines, index)
        if (markdown != null) {
            flushParagraph()
            blocks += markdown.first
            index = markdown.second
            continue
        }

        val spaceAligned = spaceTableAt(lines, index)
        if (spaceAligned != null) {
            flushParagraph()
            blocks += spaceAligned.first
            index = spaceAligned.second
            continue
        }

        paragraph += lines[index]
        index++
    }
    flushParagraph()

    // 全空文本也要给出一块，避免调用方拿到空列表。
    if (blocks.isEmpty()) blocks += ParagraphBlock(listOf(TextRun("")))
    return blocks
}

/** 段落 → 「段落块 + 块级公式块」交替序列。 */
private fun paragraphBlocks(text: String, maxInlineChars: Int): List<ContentBlock> {
    val out = mutableListOf<ContentBlock>()
    val runs = mutableListOf<InlineRun>()
    var position = 0

    fun flushRuns() {
        if (runs.isEmpty()) return
        out += ParagraphBlock(runs.toList())
        runs.clear()
    }

    for (match in mathDelimiterRegex.findAll(text)) {
        val raw = match.value
        val latex = latexOf(raw)
        if (match.range.first > position) {
            runs += TextRun(text.substring(position, match.range.first))
        }

        if (isDisplayDelimiter(raw) || latex.length > maxInlineChars) {
            flushRuns()
            out += FormulaBlock(latex)
        } else {
            runs += MathRun(latex)
        }

        position = match.range.last + 1
    }

    if (position < text.length) runs += TextRun(text.substring(position))
    flushRuns()
    if (out.isEmpty()) out += ParagraphBlock(listOf(TextRun(text)))
    return out
}

// ============================================================================
// 表格
// ============================================================================

private val pipeRow = Regex("""^\s*\|.*\|\s*$""")
private val pipeSeparator = Regex("""^\s*\|[\s:|-]+\|\s*$""")
private val multiSpace = Regex("""\s{2,}""")

/** Markdown 竖线表（`| a | b |` + `|---|---|`）。 */
private fun markdownTableAt(lines: List<String>, start: Int): Pair<TableBlock, Int>? {
    if (start + 1 >= lines.size) return null
    if (!pipeRow.matches(lines[start])) return null
    if (!pipeSeparator.matches(lines[start + 1])) return null

    val rows = mutableListOf(splitPipe(lines[start]))
    var index = start + 2
    while (index < lines.size && pipeRow.matches(lines[index])) {
        rows += splitPipe(lines[index])
        index++
    }

    if (rows.first().size < 2) return null
    return TableBlock(rows, header = true) to index
}

private fun splitPipe(line: String): List<String> {
    val trimmed = line.trim()
    val body = if (trimmed.startsWith("|")) trimmed.substring(1) else trimmed
    val inner = if (body.endsWith("|")) body.dropLast(1) else body
    return inner.split("|").map { it.trim() }
}

/**
 * 空格对齐表：连续 ≥2 行，每行被 2+ 空格切成 ≥3 段且段数相同。
 *
 * 用户真实数据里的样子（生物题材料）：
 * ```
 * 光照强度/klx    0    2    4    6    8
 * CO₂变化量/mg    +44    +8    -22    -44    -44
 * ```
 */
private fun spaceTableAt(lines: List<String>, start: Int): Pair<TableBlock, Int>? {
    if (start >= lines.size) return null
    val first = spaceCells(lines[start]) ?: return null

    val rows = mutableListOf(first)
    var index = start + 1
    while (index < lines.size) {
        val cells = spaceCells(lines[index])
        if (cells == null || cells.size != first.size) break
        rows += cells
        index++
    }

    if (rows.size < 2) return null
    return TableBlock(rows, header = false) to index
}

private fun spaceCells(line: String): List<String>? {
    val trimmed = line.trim()
    if (trimmed.length > 160) return null
    val cells = multiSpace.split(trimmed)
    if (cells.size < 3) return null
    if (cells.any { it.isBlank() }) return null
    return cells
}

// ============================================================================
// 化学式
// ============================================================================

/** 元素符号表（判化学式用）。宁可漏判也不要误判普通英文缩写。 */
private val elements = setOf(
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
)

/**
 * 化学式候选：元素符号 + 数字（可带结尾电荷），且不紧贴英文字母。
 *
 * 三个后顾/前瞻的用意（1.1.0 的注释逐条抄下来，别"顺手简化"）：
 *  * `2H2O-4e-` 里 `H2O` 后面那个 `-` 是**减号**（紧跟 4），不能当电荷；
 *  * `Fe-Cu`（紧跟 C）、`e-mail`（紧跟 m）、`1e-5`（紧跟 5）同理；
 *  * `e-` / `e+` 是单独的电子写法（小写 e），AI 在离子方程式里就是这么写的。
 */
private val chemistryRegex = Regex(
    """(?<![A-Za-z])""" +
        """(?:[A-Z][a-z]?(?:[A-Z][a-z]?|\d+)*(?:[+\u2212-])?""" +
        """|e[+\u2212-])""" +
        """(?![\dA-Za-z])""",
)

private val chemistryPart = Regex("""[A-Za-z][a-z]?|\d+|[+\u2212-]""")

/** `⇌`（U+21CC）随包字体里没有这个字形，当行内公式画。 */
const val EQUILIBRIUM = "⇌"

/** 这段文字里有没有需要特殊渲染的东西（化学式小标或化学平衡箭头）。 */
fun hasInlineFormatting(text: String): Boolean {
    if (text.contains(EQUILIBRIUM)) return true
    return chemistryRegex.findAll(text).any { isChemicalFormula(it.value) }
}

/**
 * 这一串是不是**化学式**（而不是普通英文缩写）。
 *
 * 判据（与 1.1.0 一致）：把元素符号/数字/电荷切开，**每一段都要能被解释**，
 * 且**至少有一个元素符号**、切出的段数 >= 2。
     *
     * 注意是「一个元素符号」就够：`Fe2+` / `Cl-` / `O2` 都只含一个元素，但它们是最常见的写法
     * （第一版我写成「至少两个元素」，把 `Fe2+` 直接挡掉了 —— 单测当场抓出来）。
     * 而「段数 >= 2」这一条挡住的是 `No` / `In` / `As` 这类既是元素符号又是英文单词的东西。
 */
fun isChemicalFormula(raw: String): Boolean {
    if (raw.length < 2) return false
    if (raw == "e-" || raw == "e+") return true

    val parts = chemistryPart.findAll(raw).map { it.value }.toList()
    if (parts.size < 2) return false

    var elementCount = 0
    var numberOrCharge = false
    for (part in parts) {
        when {
            part.first().isDigit() -> numberOrCharge = true                    // 下标数字
            part == "+" || part == "-" || part == "\u2212" -> numberOrCharge = true  // 电荷
            part in elements -> elementCount++
            // 小写开头的双字母（`mg`、`ml`）不是元素符号 -> 不是化学式
            else -> return false
        }
    }

    // 1.1.0 的判据（桌面规格 §4.4 逐字）：**至少 1 个元素/电子 且 至少 1 个数字或电荷**。
    // 只判「有元素」太宽：NaCl / CO 这类没有数字的写法会被加上化学小标，
    // 而它们在题目里就是普通写法（1.1.0 明确把 pH / MPa / 2026 列为反例）。
    return elementCount >= 1 && numberOrCharge
}

/** 普通文字 → 化学式小标片段（`ZnCO3` → Zn C O₃、`Fe2+` → Fe²⁺、`e-` → e⁻）。 */
fun chemistrySpans(text: String): List<MathSpan> {
    val out = mutableListOf<MathSpan>()
    var position = 0

    for (match in chemistryRegex.findAll(text)) {
        val raw = match.value
        if (!isChemicalFormula(raw)) continue
        if (match.range.first > position) {
            out += MathSpan(text.substring(position, match.range.first))
        }

        for (part in chemistryPart.findAll(raw)) {
            val piece = part.value
            when {
                piece.first().isDigit() -> out += MathSpan(piece, Script.Sub)
                piece == "+" -> out += MathSpan("+", Script.Super)
                piece == "-" || piece == "\u2212" -> out += MathSpan("\u2212", Script.Super)
                else -> out += MathSpan(piece)
            }
        }

        position = match.range.last + 1
    }

    if (position < text.length) out += MathSpan(text.substring(position))
    return out
}

// ============================================================================
// LaTeX 子集 → 片段
// ============================================================================

/**
 * 常见符号与算符。**只做「看得懂」**：替换成 Unicode 字形，不追求排版等价。
 * 不认识的命令去掉反斜杠按名字显示 —— 宁可少一层信息，也不要留一堆 `\foo`。
 */
private val latexSymbols = mapOf(
    "times" to "×", "div" to "÷", "pm" to "±", "mp" to "∓", "cdot" to "·",
    "leq" to "≤", "le" to "≤", "geq" to "≥", "ge" to "≥", "neq" to "≠", "ne" to "≠",
    "approx" to "≈", "equiv" to "≡", "propto" to "∝", "sim" to "∼",
    "rightarrow" to "→", "to" to "→", "leftarrow" to "←", "leftrightarrow" to "↔",
    "Rightarrow" to "⇒", "Leftarrow" to "⇐", "Leftrightarrow" to "⇔",
    "uparrow" to "↑", "downarrow" to "↓", "infty" to "∞", "partial" to "∂",
    "nabla" to "∇", "sum" to "∑", "prod" to "∏", "int" to "∫", "sqrt" to "√",
    "angle" to "∠", "perp" to "⊥", "parallel" to "∥", "triangle" to "△",
    "degree" to "°", "circ" to "∘", "prime" to "′", "ldots" to "…", "cdots" to "⋯",
    "alpha" to "α", "beta" to "β", "gamma" to "γ", "delta" to "δ", "epsilon" to "ε",
    "varepsilon" to "ε", "zeta" to "ζ", "eta" to "η", "theta" to "θ", "iota" to "ι",
    "kappa" to "κ", "lambda" to "λ", "mu" to "μ", "nu" to "ν", "xi" to "ξ",
    "pi" to "π", "rho" to "ρ", "sigma" to "σ", "tau" to "τ", "upsilon" to "υ",
    "phi" to "φ", "varphi" to "φ", "chi" to "χ", "psi" to "ψ", "omega" to "ω",
    "Gamma" to "Γ", "Delta" to "Δ", "Theta" to "Θ", "Lambda" to "Λ", "Xi" to "Ξ",
    "Pi" to "Π", "Sigma" to "Σ", "Phi" to "Φ", "Psi" to "Ψ", "Omega" to "Ω",
)

/** 纯排版命令：去掉（它们不该出现在正文里）。 */
private val latexIgnore = setOf(
    "left", "right", "displaystyle", "textstyle", "limits", "nolimits",
    "mathrm", "mathbf", "mathit", "text", "operatorname", "quad", "qquad",
    ",", ";", ":", "!", " ", "thinspace", "enspace",
)

/**
 * LaTeX（子集）→ 片段序列。
 *
 * 支持：`^{…}`/`^x`/`_{…}`/`_x` 上下标、`\frac{a}{b}` 分数、`\sqrt{x}` 根号、
 * 上面那张符号表、`\text{…}` 之类的排版命令（内容原样保留）。
 * **不支持的一律降级为源码文本** —— 绝不吞掉内容（这是全项目的一条硬规矩）。
 */
/** 细空格（U+2009）：LaTeX 在二元运算符两侧用的就是它。 */
const val THIN_SPACE = "\u2009"

/** 发丝空格（U+200A）：上标后面那点空隙（LaTeX 的 italic correction 作用等价）。 */
const val HAIR_SPACE = "\u200A"

/** 需要两侧留白的二元运算符（与 LaTeX 的 \mathbin 一致）。 */
private val binaryOperators = setOf(
    "=", "+", "\u2212", "-", "\u00B1", "\u2213", "\u00D7", "\u00F7",
    "\u2264", "\u2265", "\u2260", "\u2248", "\u2261", "\u221D", "\u223C",
    "\u2192", "\u2190", "\u2194", "\u21D2", "\u21D0", "\u21D4",
)

/**
 * 给公式片段补上**排版间距**。
 *
 * 为什么必须有这一步（用户当场反馈「字挨得太近、上标压着后面的字」）：
 * 逐字符拼出来的公式里，运算符两侧一个空格都没有 —— 而 LaTeX 的 `f(1)=1^2-2\times1+3`
 * 两侧本来就有中等空格、`\times` 两侧有细空格，上标后面还有一点 italic correction。
 * 这些**不是字符、是排版规则**，所以解析完必须再走一遍。
 *
 * 一元符号（行首、或紧跟另一个运算符/左括号，例如 `-2x`）**不加左侧空格** ——
 * 加了会变成 `- 2x`，那在数学里是另一个意思。
 */
internal fun spaceOutMath(spans: List<MathSpan>): List<MathSpan> {
    val out = mutableListOf<MathSpan>()
    for (span in spans) {
        val isBinary = span.script == Script.Normal && span.text in binaryOperators
        val isSign = span.text == "+" || span.text == "\u2212" || span.text == "-"
        val previous = out.lastOrNull()?.text
        val unary = isSign && (
            previous == null || previous.endsWith("(") || previous in binaryOperators ||
                previous == THIN_SPACE
            )

        if (isBinary && !unary) out += MathSpan(THIN_SPACE)
        // 上标前面也留一丝：`x^2` 的 2 会顶到 x 上面，不留缝会连成一片
        out += span
        if (isBinary) out += MathSpan(THIN_SPACE)
        if (span.script == Script.Super) out += MathSpan(HAIR_SPACE)
    }
    return out
}

/** 把间距规则作用到整串片段上（分数里已经拍平成文本，不再处理）。 */
internal fun spaceOutPieces(pieces: List<MathPiece>): List<MathPiece> = pieces.map { piece ->
    if (piece is MathSpans) MathSpans(spaceOutMath(piece.spans)) else piece
}

fun parseLatex(latex: String): List<MathPiece> {
    val pieces = mutableListOf<MathPiece>()
    val spanBuffer = mutableListOf<MathSpan>()
    var index = 0

    fun flushSpans() {
        if (spanBuffer.isEmpty()) return
        pieces += MathSpans(spanBuffer.toList())
        spanBuffer.clear()
    }

    while (index < latex.length) {
        val char = latex[index]
        when {
            char == '\\' -> {
                val name = readCommandName(latex, index + 1)
                if (name.isEmpty()) {
                    // `\ ` 这种单字符命令：当空格
                    spanBuffer += MathSpan(" ")
                    index += 1
                    continue
                }

                when {
                    name == "frac" || name == "dfrac" || name == "tfrac" -> {
                        val numerator = readGroup(latex, index + 1 + name.length)
                        val denominator = numerator?.let { readGroup(latex, it.second) }
                        if (numerator != null && denominator != null) {
                            flushSpans()
                            pieces += MathFraction(
                                numerator = listOf(flattenToSpans(parseLatex(numerator.first))),
                                denominator = listOf(flattenToSpans(parseLatex(denominator.first))),
                            )
                            index = denominator.second
                            continue
                        }
                        // 参数不全（模型截断）：原样吐出来，别吞
                        spanBuffer += MathSpan("\\$name")
                        index += 1 + name.length
                        continue
                    }

                    name == "sqrt" -> {
                        val group = readGroup(latex, index + 1 + name.length)
                        spanBuffer += MathSpan("√")
                        if (group != null) {
                            spanBuffer += flattenToSpans(parseLatex(group.first))
                            index = group.second
                            continue
                        }
                        index += 1 + name.length
                        continue
                    }

                    name in latexIgnore -> {
                        val group = readGroup(latex, index + 1 + name.length)
                        if (group != null && (name == "text" || name == "mathrm" || name == "mathbf" ||
                                name == "mathit" || name == "operatorname")
                        ) {
                            spanBuffer += MathSpan(group.first)
                            index = group.second
                            continue
                        }
                        index += 1 + name.length
                        continue
                    }

                    else -> {
                        val symbol = latexSymbols[name]
                        if (symbol != null) {
                            spanBuffer += MathSpan(symbol)
                        } else {
                            // 不认识的命令：去掉反斜杠按名字显示（少一层噪音，不吞信息）
                            spanBuffer += MathSpan(name)
                        }
                        index += 1 + name.length
                        continue
                    }
                }
            }

            char == '^' || char == '_' -> {
                val script = if (char == '^') Script.Super else Script.Sub
                val group = readGroup(latex, index + 1)
                if (group != null) {
                    spanBuffer += MathSpan(group.first, script, italic = false)
                    index = group.second
                } else {
                    index += 1
                }
                continue
            }

            char == '{' || char == '}' -> {
                index += 1
                continue
            }

            char == '~' -> {
                spanBuffer += MathSpan("\u00A0")
                index += 1
            }

            else -> {
                // **不要斜体**：随包字体 MiSans 没有斜体字面，Android 只能做**合成倾斜**
                // （`textSkewX(-0.25)`）：字形向右斜出去约 1/4 字宽，而**前进宽度不变** ——
                // 后面的字就被压住了。用户两次反馈「公式挤到一起 / 上标压着后面的字」，
                // 补了运算符间距仍然挤，就是这个原因（变量全是字母，每个都在斜）。
                // 代价是变量用正体，与教科书的斜体不同 —— 但清晰优先。
                spanBuffer += MathSpan(char.toString())
                index += 1
            }
        }
    }

    flushSpans()
    if (pieces.isEmpty()) pieces += MathSpans(listOf(MathSpan(latex)))
    return spaceOutPieces(pieces)
}

/** 把嵌套的分数拍平成一串片段（分数里套分数时只保留内层文字，降级但不丢内容）。 */
private fun flattenToSpans(pieces: List<MathPiece>): MathSpan {
    val text = pieces.joinToString("") { piece ->
        when (piece) {
            is MathSpans -> piece.spans.joinToString("") { it.text }
            is MathFraction ->
                flattenToSpans(listOf(MathSpans(piece.numerator))).text +
                    "/" + flattenToSpans(listOf(MathSpans(piece.denominator))).text
        }
    }
    return MathSpan(text)
}

/** 读命令名（`\times` → `times`；`\ ` → 空）。 */
private fun readCommandName(latex: String, start: Int): String {
    var index = start
    while (index < latex.length && latex[index].isLetter()) index++
    return latex.substring(start, index)
}

/**
 * 读一个参数：`{…}` 或单个字符。
 * 返回（内容, 结束下标）；读不到返回 null（模型偶尔会把公式截断）。
 */
private fun readGroup(latex: String, start: Int): Pair<String, Int>? {
    var index = start
    while (index < latex.length && latex[index] == ' ') index++
    if (index >= latex.length) return null

    if (latex[index] == '{') {
        var depth = 0
        val content = StringBuilder()
        var cursor = index
        while (cursor < latex.length) {
            val char = latex[cursor]
            when {
                char == '{' -> {
                    depth++
                    if (depth > 1) content.append(char)
                }

                char == '}' -> {
                    depth--
                    if (depth == 0) return content.toString() to (cursor + 1)
                    content.append(char)
                }

                else -> content.append(char)
            }

            cursor++
        }

        return null // 括号没闭合：当作读不到
    }

    return latex[index].toString() to (index + 1)
}
