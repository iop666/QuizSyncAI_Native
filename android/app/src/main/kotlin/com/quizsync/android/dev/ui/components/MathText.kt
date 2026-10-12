package com.quizsync.android.dev.ui.components

import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.background
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.font.FontStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.BaselineShift
import androidx.compose.ui.text.withStyle
import androidx.compose.ui.unit.TextUnit
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.em
import androidx.compose.ui.unit.sp
import com.quizsync.android.dev.ui.mathcontent.ContentBlock
import com.quizsync.android.dev.ui.mathcontent.FormulaBlock
import com.quizsync.android.dev.ui.mathcontent.InlineRun
import com.quizsync.android.dev.ui.mathcontent.MathFraction
import com.quizsync.android.dev.ui.mathcontent.MathPiece
import com.quizsync.android.dev.ui.mathcontent.MathRun
import com.quizsync.android.dev.ui.mathcontent.MathSpan
import com.quizsync.android.dev.ui.mathcontent.MathSpans
import com.quizsync.android.dev.ui.mathcontent.ParagraphBlock
import com.quizsync.android.dev.ui.mathcontent.Script
import com.quizsync.android.dev.ui.mathcontent.TableBlock
import com.quizsync.android.dev.ui.mathcontent.TextRun
import com.quizsync.android.dev.ui.mathcontent.chemistrySpans
import com.quizsync.android.dev.ui.mathcontent.hasInlineFormatting
import com.quizsync.android.dev.ui.mathcontent.mathDelimiterRegex
import com.quizsync.android.dev.ui.mathcontent.parseContentBlocks
import com.quizsync.android.dev.ui.mathcontent.parseLatex

/**
 * 题目内容渲染（**安卓端的 `MathText`**，对应 1.1.0 的 `packages/quizsync_ui/lib/src/math_text.dart`）。
 *
 * 用户为这件事报过四轮（M6 / M20 / M21 / M22）：公式整段显示成源码、
 * 材料里的化学式没有小标、表格挤成一团。这一版解决**看得懂**，不追求排版等价：
 *
 * | 输入 | 以前 | 现在 |
 * |---|---|---|
 * | `$f(x)=x^2-2x+3$` | 原样带 `$` 与 `^` | `f(x)=x²−2x+3`（上标真的抬高） |
 * | `\(L=0.5\ \mathrm{m}\)` | 原样（LaTeX 原生定界符不认） | `L=0.5 m` |
 * | `\frac{a}{b}` | 原样 | 画出分数线（分子在上、分母在下） |
 * | `ZnCO3`、`Fe2+`、`e-` | 普通文字 | 下标/上标正确（ZnCO₃、Fe²⁺、e⁻） |
 * | 空格对齐表 / Markdown 表 | 挤成一行 | 真表格 |
 * | `$$…$$` | 混在行内 | 独立成行、居中、可横向滚动 |
 *
 * **降级规则（硬规矩）**：解析不了的东西一律**按原文显示**，绝不吞内容 ——
 * 少一层排版可以接受，丢一道题的题干不行。
 *
 * 大公式（矩阵、分段函数、根式嵌套）这一版仍按源码显示：完整 LaTeX 排版需要 KaTeX，
 * 那是 `NATIVE-2.0-PLAN` §7 里两端一起走的路，不在本轮。
 */
/** 公式片段的字距（0.07 em ≈ 16sp 下 1.1px）：让公式与汉字之间的观感一致。 */
private val MATH_LETTER_SPACING = 0.07f.em

@Composable
fun MathText(
    text: String,
    fontSize: Int,
    modifier: Modifier = Modifier,
    color: Color = Color.Unspecified,
    fontWeight: FontWeight? = null,
    lineHeight: TextUnit? = null,
) {
    // 快路径：既没有公式、也没有化学式小标与表格 —— 就是一个普通 Text，
    // 选择/查找/无障碍行为与以前完全一致，也不多一层布局。
    val needsWork = mathDelimiterRegex.containsMatchIn(text) ||
        hasInlineFormatting(text) ||
        text.contains("\n") && looksLikeTable(text)

    if (!needsWork) {
        Text(
            text = text,
            modifier = modifier,
            fontSize = fontSize.sp,
            color = color,
            fontWeight = fontWeight,
            lineHeight = lineHeight ?: (fontSize * 1.5).sp,
        )
        return
    }

    // ⚠️ **必须 `remember`**：这两个解析是**纯函数**（输入相同结果必然相同），
    // 而 `MathText` 会因为上层任何一次重组被重新执行 —— 不缓存的话，**每一次重组都把整段
    // LaTeX / 化学式重新解析一遍**。真机实测（这块 144Hz 平板，只有一道题的会话）：
    // 改字号这种"整列表重新排版"的动作，UI 线程会出现 **44–57ms 的尖刺**，
    // 与转场动画叠在一起就是用户说的"动画太卡了"（`dumpsys gfxinfo` 直方图取证）。
    val blocks = remember(text) { parseContentBlocks(text, maxInlineChars = 40) }
    Column(modifier = modifier) {
        blocks.forEach { block ->
            when (block) {
                is ParagraphBlock -> InlineContent(block.runs, fontSize, color, fontWeight, lineHeight)
                is FormulaBlock -> FormulaContent(block.latex, fontSize, color)
                is TableBlock -> TableContent(block)
            }
        }
    }
}

/** 粗略判断「这段文字里像是有表格」（用于快路径判定，真判定在 parseContentBlocks 里）。 */
private fun looksLikeTable(text: String): Boolean {
    val lines = text.split("\n")
    if (lines.size < 2) return false
    if (lines.any { it.trim().startsWith("|") && it.trim().endsWith("|") }) return true
    return lines.count { Regex("""\s{2,}""").containsMatchIn(it.trim()) } >= 2
}

/**
 * 一段行内内容。
 *
 * 两条路线：
 * * **没有分数**（绝大多数）：拼成一个 `AnnotatedString` 交给一个 `Text` —— 换行、两端对齐、
 *   无障碍全都正常，也不会有布局开销；
 * * **有分数**：分数要画分数线，只能用组合布局（`FlowRow`），
 *   每个片段是一个子项（长文本片段仍能自己换行）。
 */
@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun InlineContent(
    runs: List<InlineRun>,
    fontSize: Int,
    color: Color,
    fontWeight: FontWeight?,
    lineHeight: TextUnit?,
) {
    // 同上：**纯函数 + 每次重组都会跑**，所以按 `runs` 缓存（`remember` 用结构相等比较，
    // 内容一样的 list 会命中）。
    val pieces = remember(runs) {
        runs.flatMap { run ->
            when (run) {
                is TextRun -> listOf<MathPiece>(MathSpans(chemistrySpans(run.text)))
                is MathRun -> parseLatex(run.latex)
            }
        }
    }

    val hasFraction = pieces.any { it is MathFraction }
    if (!hasFraction) {
        Text(
            text = annotated(pieces, fontSize),
            fontSize = fontSize.sp,
            color = color,
            fontWeight = fontWeight,
            lineHeight = lineHeight ?: (fontSize * 1.5).sp,
        )
        return
    }

    FlowRow(
        horizontalArrangement = Arrangement.Start,
        verticalArrangement = Arrangement.Center,
    ) {
        pieces.forEach { piece ->
            when (piece) {
                is MathSpans -> Text(
                    text = annotated(listOf(piece), fontSize),
                    fontSize = fontSize.sp,
                    color = color,
                    fontWeight = fontWeight,
                    lineHeight = lineHeight ?: (fontSize * 1.5).sp,
                )

                is MathFraction -> FractionView(piece, fontSize, color, fontWeight)
            }
        }
    }
}

/** 块级公式：独立成行、居中、**可横向滚动**（再长的公式也不会被裁掉）。 */
@Composable
private fun FormulaContent(latex: String, fontSize: Int, color: Color) {
    val pieces = remember(latex) { parseLatex(latex) }
    val hasFraction = pieces.any { it is MathFraction }

    Box(
        modifier = Modifier
            .fillMaxWidth()
            .padding(vertical = 6.dp),
        contentAlignment = Alignment.Center,
    ) {
        if (!hasFraction) {
            Text(
                text = annotated(pieces, fontSize),
                fontSize = (fontSize * 1.08f).sp,
                color = color,
                lineHeight = (fontSize * 1.7f).sp,
                modifier = Modifier.horizontalScroll(rememberScrollState()),
            )
        } else {
            Row(
                modifier = Modifier.horizontalScroll(rememberScrollState()),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                pieces.forEach { piece ->
                    when (piece) {
                        is MathSpans -> Text(
                            text = annotated(listOf(piece), fontSize),
                            fontSize = (fontSize * 1.08f).sp,
                            color = color,
                        )

                        is MathFraction -> FractionView(piece, fontSize, color, null)
                    }
                }
            }
        }
    }
}

/** 分数：分子 / 分数线 / 分母（分数线用一条 1dp 的实线画）。 */
@Composable
private fun FractionView(
    fraction: MathFraction,
    fontSize: Int,
    color: Color,
    fontWeight: FontWeight?,
) {
    val textColor = if (color == Color.Unspecified) MaterialTheme.colorScheme.onSurface else color
    Column(
        horizontalAlignment = Alignment.CenterHorizontally,
        modifier = Modifier.padding(horizontal = 2.dp),
    ) {
        Text(
            text = spansToAnnotated(fraction.numerator, (fontSize * 0.82f).sp),
            fontSize = (fontSize * 0.82f).sp,
            color = textColor,
            fontWeight = fontWeight,
        )
        Box(
            modifier = Modifier
                .padding(vertical = 1.dp)
                .height(1.dp)
                .fractionRuleWidth()
                .background(textColor),
        )
        Text(
            text = spansToAnnotated(fraction.denominator, (fontSize * 0.82f).sp),
            fontSize = (fontSize * 0.82f).sp,
            color = textColor,
            fontWeight = fontWeight,
        )
    }
}

/** 分数线的宽度跟着内容走（`fillMaxWidth` 会拉满整行，很难看）。 */
private fun Modifier.fractionRuleWidth(): Modifier = this.then(Modifier.width(28.dp))

/** 把片段序列拼成 `AnnotatedString`（上下标用 `BaselineShift`）。 */
private fun annotated(pieces: List<MathPiece>, fontSize: Int): AnnotatedString = buildAnnotatedString {
    pieces.forEach { piece ->
        when (piece) {
            is MathSpans -> append(piece.spans, fontSize.sp)
            is MathFraction ->
                // 走到这里说明调用方已经处理过分数；兜底成 a/b 形式，绝不静默丢内容。
                append(spansToAnnotated(fractionFallback(piece), fontSize.sp))
        }
    }
}

private fun fractionFallback(fraction: MathFraction): List<MathSpan> = listOf(
    MathSpan(
        fraction.numerator.joinToString("") { it.text } + "/" +
            fraction.denominator.joinToString("") { it.text },
    ),
)

/**
 * 片段 → `AnnotatedString`。
 *
 * ⚠️ **上下标必须同时缩字号**（只设 `BaselineShift` 会把全尺寸的字往上顶）：
 * 第一版这里漏了 `fontSize`，于是 `x^2` 的上标和正文一样大、还压着后面的字，
 * 整段看起来「挤到一起」（用户当场反馈）。`spansToAnnotated` 里有缩、这个重载里没有 ——
 * 同一件事两份实现就会这样，所以两处都按 0.72 缩。
 */
private fun androidx.compose.ui.text.AnnotatedString.Builder.append(
    spans: List<MathSpan>,
    baseSize: androidx.compose.ui.unit.TextUnit,
) {
    spans.forEach { span ->
        var style = SpanStyle(
            fontStyle = if (span.italic) FontStyle.Italic else FontStyle.Normal,
            letterSpacing = MATH_LETTER_SPACING,
        )
        when (span.script) {
            Script.Normal -> Unit
            Script.Super -> style = style.copy(
                baselineShift = BaselineShift.Superscript,
                fontSize = baseSize * 0.72f,
            )

            Script.Sub -> style = style.copy(
                baselineShift = BaselineShift.Subscript,
                fontSize = baseSize * 0.72f,
            )
        }
        withStyle(style) { append(span.text) }
    }
}

/** 片段 → `AnnotatedString`（上下标字号按 0.72 缩，是教科书的常见比例）。 */
private fun spansToAnnotated(spans: List<MathSpan>, baseSize: TextUnit): AnnotatedString =
    buildAnnotatedString {
        spans.forEach { span ->
            // 字距：MiSans 的拉丁字形窄，公式与旁边的全角汉字一比就糊成一团
            // （真机 ASCII 色块图实测：汉字之间有 2 格空隙，公式是连成一片的长块）。
            var style = SpanStyle(
                fontStyle = if (span.italic) FontStyle.Italic else FontStyle.Normal,
                letterSpacing = MATH_LETTER_SPACING,
            )
            when (span.script) {
                Script.Normal -> Unit
                Script.Super -> style = style.copy(
                    baselineShift = BaselineShift.Superscript,
                    fontSize = baseSize * 0.72f,
                )

                Script.Sub -> style = style.copy(
                    baselineShift = BaselineShift.Subscript,
                    fontSize = baseSize * 0.72f,
                )
            }

            withStyle(style) { append(span.text) }
        }
    }

/** 表格：逐行逐格画（表头加粗，行间一条浅分隔线）。 */
@Composable
private fun TableContent(table: TableBlock) {
    val outline = MaterialTheme.colorScheme.outlineVariant
    val onSurface = MaterialTheme.colorScheme.onSurface

    Column(
        modifier = Modifier
            .fillMaxWidth()
            .padding(vertical = 6.dp),
    ) {
        table.rows.forEachIndexed { rowIndex, cells ->
            Row(modifier = Modifier.fillMaxWidth()) {
                cells.forEach { cell ->
                    Text(
                        text = cell,
                        modifier = Modifier
                            .weight(1f)
                            .padding(vertical = 5.dp, horizontal = 4.dp),
                        fontSize = 13.sp,
                        color = onSurface,
                        fontWeight = if (table.header && rowIndex == 0) FontWeight.SemiBold else null,
                    )
                }
                // 表格列数不一致时补齐空格，保证每行高度一致
                repeat((table.rows.maxOf { it.size }) - cells.size) {
                    Spacer(Modifier.weight(1f))
                }
            }
            if (rowIndex < table.rows.size - 1) {
                Box(
                    modifier = Modifier
                        .fillMaxWidth()
                        .height(1.dp)
                        .background(outline),
                )
            }
        }
    }
}

/** 供上层判断「这段文本要不要走富渲染」（与 1.1.0 的 `hasInlineFormatting` 同义）。 */
fun needsRichText(text: String): Boolean =
    mathDelimiterRegex.containsMatchIn(text) || hasInlineFormatting(text)
