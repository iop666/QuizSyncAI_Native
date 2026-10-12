package com.quizsync.android.dev.ui.designsystem

import androidx.compose.animation.EnterTransition
import androidx.compose.animation.ExitTransition
import com.quizsync.android.dev.state.NavDirection
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertSame

/**
 * 预测式返回的两条**规则**（用户 2026-09-28 报的两个 bug）。
 *
 * 这里只钉规则本身 —— 跑得快、不依赖设备。真正"哪一页被组合出来"的接线由
 * `androidTest` 的 `QuizRouteStackInstrumentedTest` 钉，端到端的观感由真机帧analysis 钉。
 * 三层各有各的职责，别指望其中一个覆盖全部。
 */
class QsStackTransitionsTest {

    // ========================================================================
    // 规则 A：手势走完的出栈**一格动画都不许再播**（bug ②："松手后又返回了一次"）
    // ========================================================================

    @Test
    fun `手势提交的出栈是零时长转场`() {
        val transform = qsStackTransform(NavDirection.PopGesture)

        // 再播一遍 enter / exit 正是用户看到"回弹 + 重复位移"的原因：
        // 手势已经把这台戏演完了，这里必须什么都不做。
        assertSame(
            EnterTransition.None,
            transform.targetContentEnter,
            "手势提交的 pop 不能再播一次「上一页进场」",
        )
        assertSame(
            ExitTransition.None,
            transform.initialContentExit,
            "手势提交的 pop 不能再播一次「当前页退出」",
        )
    }

    @Test
    fun `普通出栈仍然播完整的退出与进场`() {
        val transform = qsStackTransform(NavDirection.Pop)

        // 顶部返回键 / 系统返回键没有手势进度可跟，必须由时间驱动演完整段。
        assertEquals(
            false,
            transform.targetContentEnter == EnterTransition.None,
            "普通返回不能退化成瞬间切换（那等于把动画删了）",
        )
        assertEquals(
            false,
            transform.initialContentExit == ExitTransition.None,
            "普通返回不能退化成瞬间切换（那等于把动画删了）",
        )
    }

    @Test
    fun `三种方向都关掉尺寸变换`() {
        // `sizeTransform` 会在转场结束那一刻重新量内容尺寸 —— 表现是"动画放完又跳一下"。
        // 三个分支都必须关掉（这条以前漏过，是用户报「动画结束后又跳一下」的根因）。
        NavDirection.entries.forEach { nav ->
            assertEquals(null, qsStackTransform(nav).sizeTransform, "$nav 忘了关 sizeTransform")
        }
    }

    // ========================================================================
    // 规则 B：一页的横向位置由"它在不在栈里"决定，不由动画时序决定（bug ②的兜底）
    // ========================================================================

    @Test
    fun `栈顶页跟着手势走，取消时沿原路回去`() {
        // 拖动过程中：手指拖到哪，它退到哪。
        assertEquals(0f, qsStackPageOffsetFactor(inStack = true, gesture = 0f, gestureCommitted = false))
        assertEquals(0.5f, qsStackPageOffsetFactor(inStack = true, gesture = 0.5f, gestureCommitted = false))
        assertEquals(1f, qsStackPageOffsetFactor(inStack = true, gesture = 1f, gestureCommitted = false))
        // 越界的进度不能让页面飞出屏幕外面去（手指回拉时进度会被夹住）。
        assertEquals(1f, qsStackPageOffsetFactor(inStack = true, gesture = 1.4f, gestureCommitted = false))
        assertEquals(0f, qsStackPageOffsetFactor(inStack = true, gesture = -0.3f, gestureCommitted = false))
    }

    @Test
    fun `手势提交后已经出栈的那一页必须留在屏幕外`() {
        // ⚠️ 这条是"松手后闪一下"的兜底：`AnimatedContent` 要等转场结束才把旧内容移出组合，
        // 中间至少还有一帧。那一帧进度已经被归零了，如果按"进度 × 屏宽"摆位，
        // 它就会**跳回屏幕中间**盖住刚露出来的上一页。
        assertEquals(
            1f,
            qsStackPageOffsetFactor(inStack = false, gesture = 0f, gestureCommitted = true),
            "手势提交的那一帧，出栈页必须仍在屏幕外",
        )
        // 普通返回时相反：位置留给时间动画（退出动画的终值也是整屏宽，两者终态一致）。
        assertEquals(
            0f,
            qsStackPageOffsetFactor(inStack = false, gesture = 0f, gestureCommitted = false),
            "普通返回的退场必须交给 AnimatedContent 的时间动画",
        )
    }

    // ========================================================================
    // 规则 C：**甩 vs 慢拖**（用户 2026-09-28：「过快就没有动画，缓慢就有」）
    // ========================================================================

    @Test
    fun `慢拖的间隔不算甩`() {
        // 真机实测（`adb logcat -s QuizSync`，144Hz 平板每帧约 7ms）：
        // 一屏划 600ms ⇒ 1.2–1.7/s，300ms ⇒ 1.9–2.0/s，都要判成"拖"。
        assertEquals(false, qsIsFling(progressDelta = 0.008f, deltaMs = 7f))   // 600ms 一屏
        assertEquals(false, qsIsFling(progressDelta = 0.014f, deltaMs = 7f))  // 300ms 一屏
        assertEquals(false, qsIsFling(progressDelta = 0.02f, deltaMs = 7f))
        // 手指停住不动（进度不变）当然不是甩。
        assertEquals(false, qsIsFling(progressDelta = 0f, deltaMs = 7f))
    }

    @Test
    fun `几乎没动的那一段永远不算甩`() {
        // 一屏的 1% 以下：时间戳噪声足以把速率算成任意值，一律按"拖"（安全的一侧）。
        assertEquals(false, qsIsFling(progressDelta = 0.009f, deltaMs = 0.1f))
        assertEquals(false, qsIsFling(progressDelta = 0.001f, deltaMs = 7f))
    }

    @Test
    fun `快速轻扫的间隔算甩`() {
        // 真机实测：一屏划 40ms ⇒ 5.5/s、80ms ⇒ 3.2–4.2/s、150ms ⇒ 3.5/s。
        assertEquals(true, qsIsFling(progressDelta = 0.038f, deltaMs = 7f))   // 40ms 一屏
        assertEquals(true, qsIsFling(progressDelta = 0.028f, deltaMs = 7f))   // 80ms 一屏
        assertEquals(true, qsIsFling(progressDelta = 0.025f, deltaMs = 7f))   // 150ms 一屏
        assertEquals(true, qsIsFling(progressDelta = 0.25f, deltaMs = 30f))
    }

    @Test
    fun `门槛是一屏 330ms`() {
        // 速率 3/s：一屏 333ms 正好在门槛上（大于才算甩）。
        assertEquals(false, qsIsFling(progressDelta = 0.03f, deltaMs = 10f))
        assertEquals(true, qsIsFling(progressDelta = 0.031f, deltaMs = 10f))
    }

    @Test
    fun `同一帧内跳完按甩处理`() {
        // `System.nanoTime()` 是单调的，真机上不会倒流；真出现 0 间隔说明这一帧的进度是一次跳变
        //（一步到位 / 系统直接把进度推到底），按"甩"处理。
        assertEquals(true, qsIsFling(progressDelta = 0.3f, deltaMs = 0f))
        assertEquals(true, qsIsFling(progressDelta = 0.3f, deltaMs = -5f))
    }
}
