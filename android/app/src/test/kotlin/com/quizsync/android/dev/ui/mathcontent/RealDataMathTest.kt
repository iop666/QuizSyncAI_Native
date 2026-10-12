package com.quizsync.android.dev.ui.mathcontent

import kotlin.test.Test
import kotlin.test.assertTrue

/**
 * 用**用户真题原文**跑安卓解析层 —— 与 `windows/QuizSync.Tests/RealDataMathTests.cs`
 * 是同一批输入（双端一致性的人工对照）。
 *
 * 原文逐字取自主机库里的那道数列大题（`tools/audit_data_authenticity.py` 报的"真 1~2 条"之一），
 * **不是重写的近似版** —— 近似版会把真实的怪写法（省略花括号的 `\mathbb N`、无空格的下标）
 * 过滤掉，而那正是要验的东西。
 *
 * ⚠️ 这里只验**解析**。渲染在 `ui/components/MathText.kt`（Compose 层），
 * 只能用真机截图看观感 —— 我读不了图片。
 */
class RealDataMathTest {

    /** 真实题干（原样，含 `$` 定界与省略花括号的 `\mathbb N^*`）。 */
    private val realStem = """
        已知数列 ${'$'}\{a_n\}${'$'} 满足 ${'$'}a_1=1,\ a_{n+1}=2a_n+2^n\ (n\in\mathbb N^*)${'$'}。
        (1) 证明 ${'$'}\{\frac{a_n}{2^n}\}${'$'} 是等差数列，并求 ${'$'}a_n${'$'}；
        (2) 设 ${'$'}b_n=n a_n${'$'}，求数列 ${'$'}\{b_n\}${'$'} 的前 ${'$'}n${'$'} 项和 ${'$'}S_n${'$'}。
    """.trimIndent()

    /** 真实答案里最关键的一句：**两个分数相减**，且分母各带指数。 */
    private val nestedFraction = """\frac{a_{n+1}}{2^{n+1}}-\frac{a_n}{2^n}=\frac12"""

    @Test
    fun `嵌套分数解析成分数结构而不是一行文字`() {
        val pieces = parseLatex(nestedFraction)
        val fractions = pieces.filterIsInstance<MathFraction>()
        assertTrue(fractions.size >= 3, "三个分数都应解析为分数结构，实际 ${fractions.size} 个")
        val first = fractions.first()
        val numerator = first.numerator.joinToString("") { it.text }
        assertTrue(numerator.contains("a"), "分子应含 a，实际「$numerator」")
    }

    @Test
    fun `真题题干解析后不丢内容`() {
        val flat = flattenToText(parseLatex(realStem))
        // 铁律：不支持就降级成源码显示，**绝不吞内容**。关键 token 必须还在。
        listOf("已知数列", "等差", "a", "n", "2", "S").forEach { token ->
            assertTrue(flat.contains(token), "展平后缺少「$token」。完整展平：$flat")
        }
    }

    @Test
    fun `省略花括号的 mathbb 也要留住字母`() {
        // 真实数据里写的是 `\mathbb N^*`（LaTeX 允许省略花括号）。
        // Windows 侧曾因此吞掉字母 N（第 18 轮抓出并修复）；这条守着安卓侧不重蹈覆辙。
        val flat = flattenToText(parseLatex("""n\in\mathbb N^*"""))
        assertTrue(
            flat.contains('N') || flat.contains('ℕ'),
            "`\\mathbb N` 的字母被吞了（既无 N 也无 ℕ）。实际：$flat",
        )
    }

    /**
     * 把解析结果展平成纯文本，便于断言"有没有丢内容"。
     *
     * ⚠️ 安卓的 `MathPiece` **只有两个子类型**：`MathSpans` 与 `MathFraction`
     * （上下标不是结构节点，而是**展平成带 `Script` 的 `MathSpan`**）。
     * 这与 C# 侧不同 —— 那边保留了 `MathRoot` / `MathBigOperator` / `MathScripted` 深层结构。
     * **两端解析模型不一致**这件事本身值得记（见 docs/checklist-android-parity.md）。
     */
    private fun flattenToText(pieces: List<MathPiece>): String = pieces.joinToString("") { piece ->
        when (piece) {
            is MathFraction -> flattenSpans(piece.numerator) + "/" + flattenSpans(piece.denominator)
            is MathSpans -> flattenSpans(piece.spans)
            else -> ""
        }
    }

    private fun flattenSpans(spans: List<MathSpan>): String = spans.joinToString("") { it.text }
}
