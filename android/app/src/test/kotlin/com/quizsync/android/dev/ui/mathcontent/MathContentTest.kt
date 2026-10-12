package com.quizsync.android.dev.ui.mathcontent

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

/**
 * 内容解析的回归（纯 JVM，不需要设备）。
 *
 * 这一层值得单测的理由：它的每条规则都对应一次**用户报过的问题**
 * （M6 只认 `$…$` → 物理题的 `\(…\)` 整段显示成源码；M21 化学式没有小标；
 * M21 材料里的表格挤成一团），而解析错了在界面上只表现为「显示得怪」，
 * 不会有任何报错 —— 正是那种必须靠断言钉住的东西。
 */
class MathContentTest {

    // ---------------------------------------------------------------- 定界符

    @Test
    fun four_delimiters_are_recognised() {
        // `$…$` 与 `\(…\)` 行内；`$$…$$` 与 `\[…\]` 块级（M20：模型对物理题吐的是后者）
        val blocks = parseContentBlocks("电荷量 \\(q=1\\ \\mathrm{C}\\) 的粒子", maxInlineChars = 100)
        val runs = (blocks.single() as ParagraphBlock).runs
        assertTrue(runs.any { it is MathRun && it.latex == "q=1\\ \\mathrm{C}" }, "行内 \\(…\\) 没被认出来：$runs")

        val dollars = parseContentBlocks("设 \$x=1\$ 则", maxInlineChars = 100)
        assertTrue((dollars.single() as ParagraphBlock).runs.any { it is MathRun }, "行内 \$…\$ 没被认出来")

        val display = parseContentBlocks("如下：\$\$a^2+b^2=c^2\$\$ 完", maxInlineChars = 100)
        assertTrue(display.any { it is FormulaBlock && it.latex == "a^2+b^2=c^2" }, "块级 \$\$…\$\$ 没被认出来")

        val latexDisplay = parseContentBlocks("推导：\\[E=mc^2\\]", maxInlineChars = 100)
        assertTrue(latexDisplay.any { it is FormulaBlock && it.latex == "E=mc^2" }, "块级 \\[…\\] 没被认出来")
    }

    @Test
    fun long_inline_formula_is_moved_to_its_own_block() {
        // 行内公式是**不可断行**的整体：太长会把整段挤出屏幕，必须搬成块级
        val text = "由 \$v=v_0+at=2+3\\times 4=14\\ \\mathrm{m/s}\$ 可得"
        val blocks = parseContentBlocks(text, maxInlineChars = 10)
        assertTrue(blocks.any { it is FormulaBlock }, "过长的行内公式没有搬成块级：$blocks")
    }

    @Test
    fun plain_text_has_no_math_runs() {
        val blocks = parseContentBlocks("这是一段没有公式的题干。")
        val runs = (blocks.single() as ParagraphBlock).runs
        assertEquals(1, runs.size)
        assertTrue(runs.single() is TextRun)
    }

    // ---------------------------------------------------------------- 化学式

    @Test
    fun common_chemistry_forms_are_detected() {
        assertTrue(isChemicalFormula("ZnCO3"), "ZnCO3 应当判成化学式")
        assertTrue(isChemicalFormula("Fe2+"), "Fe2+ 应当判成化学式")
        assertTrue(isChemicalFormula("H2O"), "H2O 应当判成化学式")
        assertTrue(isChemicalFormula("e-"), "电子 e- 应当判成化学式")
    }

    @Test
    fun ordinary_words_are_not_chemistry() {
        // 宁可漏判也不要误判：这些是普通英文/缩写，判成化学式会让正文出现乱七八糟的小标
        assertTrue(!isChemicalFormula("mg"), "mg 不是元素符号（Mg 才是）")
        assertTrue(!isChemicalFormula("Email"))
        assertTrue(!isChemicalFormula("A"))
    }

    @Test
    fun chemistry_spans_put_digits_down_and_charge_up() {
        val spans = chemistrySpans("ZnCO3")
        assertEquals(listOf("Zn", "C", "O", "3"), spans.map { it.text })
        assertEquals(Script.Normal, spans[0].script)
        assertEquals(Script.Sub, spans.last().script, "下标数字没有下沉")

        val charged = chemistrySpans("Fe2+")
        assertEquals(Script.Sub, charged[1].script, "Fe2+ 里的 2 应当是下标")
        assertEquals(Script.Super, charged[2].script, "Fe2+ 里的 + 应当是上标")

        val electron = chemistrySpans("2H2O-4e-")
        // `H2O` 后面那个 `-` 是**减号**（紧跟 4），只有结尾的 `e-` 才是电子
        val e = electron.first { it.text == "e" }
        assertTrue(electron[electron.indexOf(e) + 1].script == Script.Super, "e- 的负号没有上浮")
    }

    @Test
    fun has_inline_formatting_catches_chemistry_and_equilibrium() {
        assertTrue(hasInlineFormatting("ZnCO3 与 Fe2+ 反应"))
        assertTrue(hasInlineFormatting("平衡：A ⇌ B"))
        assertTrue(!hasInlineFormatting("普通一句话"))
    }

    // ---------------------------------------------------------------- 表格

    @Test
    fun markdown_table_is_parsed_with_header() {
        val text = "| 项目 | 数值 |\n|---|---|\n| 电压 | 3.7 |\n| 电流 | 0.5 |"
        val table = parseContentBlocks(text).filterIsInstance<TableBlock>().single()
        assertTrue(table.header)
        assertEquals(3, table.rows.size)
        assertEquals(listOf("项目", "数值"), table.rows.first())
    }

    @Test
    fun space_aligned_table_is_parsed_without_header() {
        // 用户真实数据（生物题材料）：列之间是 2+ 空格
        val text = "光照强度/klx    0    2    4\nCO₂变化量/mg    +44    +8    -22"
        val table = parseContentBlocks(text).filterIsInstance<TableBlock>().single()
        assertTrue(!table.header, "空格对齐表没有表头语义，不该加粗第一行")
        assertEquals(2, table.rows.size)
        assertEquals(4, table.rows.first().size)
    }

    @Test
    fun single_line_with_spaces_is_not_a_table() {
        assertTrue(parseContentBlocks("这 是 一 句 话").none { it is TableBlock })
    }

    // ---------------------------------------------------------------- LaTeX 子集

    @Test
    fun symbols_and_operators_are_substituted() {
        val pieces = parseLatex("3\\times 10^{8}\\ \\mathrm{m/s}")
        val text = pieces.filterIsInstance<MathSpans>().flatMap { it.spans }.joinToString("") { it.text }
        assertTrue(text.contains("×"), "\\times 没有替换成 ×：$text")
        assertTrue(text.contains("8"), "上标内容丢了：$text")
        assertTrue(text.contains("m/s"), "\\mathrm 的内容应当原样保留：$text")
    }

    @Test
    fun superscript_and_subscript_get_their_script() {
        val pieces = parseLatex("v_0^2")
        val spans = pieces.filterIsInstance<MathSpans>().flatMap { it.spans }
        assertTrue(spans.any { it.text == "0" && it.script == Script.Sub }, "下标没认出来：$spans")
        assertTrue(spans.any { it.text == "2" && it.script == Script.Super }, "上标没认出来：$spans")
    }

    @Test
    fun fraction_becomes_a_piece_and_keeps_both_sides() {
        val pieces = parseLatex("\\frac{a+b}{2}")
        val fraction = pieces.filterIsInstance<MathFraction>().single()
        val numerator = fraction.numerator.joinToString("") { it.text }.filter { !it.isWhitespace() }
        // 排版会在运算符两侧插细空格（U+2009），断言前先去掉空白
        assertEquals("a+b", numerator)
        assertEquals("2", fraction.denominator.joinToString("") { it.text }.trim())
    }

    @Test
    fun broken_fraction_degrades_to_source_instead_of_dropping_content() {
        // 模型把公式截断是常事：绝不能把内容吞掉（全项目的硬规矩）
        val pieces = parseLatex("\\frac{a}{")
        val text = pieces.filterIsInstance<MathSpans>().flatMap { it.spans }.joinToString("") { it.text }
        assertTrue(text.contains("a"), "参数不全时内容被吞了：$text")
    }

    @Test
    fun unknown_command_keeps_its_name_without_the_backslash() {
        val pieces = parseLatex("\\foobar x")
        val text = pieces.filterIsInstance<MathSpans>().flatMap { it.spans }.joinToString("") { it.text }
        assertEquals("foobar x", text)
    }

    @Test
    fun chemical_formula_needs_a_digit_or_charge() {
        // 1.1.0 的判据：至少 1 个元素/电子 **且** 至少 1 个数字或电荷。
        // 反例里的 NaCl / CO 是这次收紧才挡住的（原来只判「有元素」会把它们当化学式）。
        assertTrue(isChemicalFormula("H2O"))
        assertTrue(isChemicalFormula("ZnCO3"))
        assertTrue(isChemicalFormula("Fe2+"))
        assertTrue(isChemicalFormula("e-"))
        assertTrue(!isChemicalFormula("NaCl"), "没有数字或电荷，按 1.1.0 不算化学式")
        assertTrue(!isChemicalFormula("CO"), "同上")
        assertTrue(!isChemicalFormula("pH"))
        assertTrue(!isChemicalFormula("MPa"))
        assertTrue(!isChemicalFormula("2026"))
    }
}
