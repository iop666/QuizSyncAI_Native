package com.quizsync.android.dev.ui

import kotlin.test.Test
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/**
 * 结果页标题栏的**版式策略**（用户 2026-09-28 第四条）：
 * 「默认手机显示状态下删除结果页中字号加减的选项（平板等横屏保留）」。
 *
 * 判据是**窗口宽度 ≥ 600dp**，不是方向 —— 这一组断言把两端的真实数值钉住：
 *  * 手机（1440px / density 3.0）竖屏 = 480dp → 不显示；
 *  * 平板（1800px / density 2.5）竖屏 = 720dp → 显示（用户说的"平板等横屏保留"里，
 *    平板即使竖着也放得下，没理由藏起来）；
 *  * 手机横屏 = 3200px / 3.0 ≈ 1067dp → 显示。
 *
 * 阈值本身（600dp）取的是 Material 的 compact/medium 分界，和上面三个实际值都留了余量：
 * 最窄的"要显示"的场景是 720dp，最宽的"不显示"的场景是 480dp。
 */
class ResultScreenPolicyTest {

    @Test
    fun `手机竖屏 480dp 不显示字号加减`() {
        assertFalse(qsShowsFontActions(480f), "480dp = 本机手机竖屏，用户口径就是这一档要删掉")
    }

    @Test
    fun `刚好到门槛就显示`() {
        assertTrue(qsShowsFontActions(600f))
        assertFalse(qsShowsFontActions(599.9f))
    }

    @Test
    fun `平板竖屏 720dp 显示`() {
        assertTrue(qsShowsFontActions(720f), "平板（1800px / 2.5）竖屏也要保留 —— 空间是够的")
    }

    @Test
    fun `手机横屏 1067dp 显示`() {
        assertTrue(qsShowsFontActions(1067f))
    }
}
