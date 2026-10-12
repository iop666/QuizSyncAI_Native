package com.quizsync.android.dev.state

import kotlin.test.Test
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/**
 * 下拉刷新指示器的数据源。
 *
 * 用户 2026-09-29 反馈：「安卓端的下拉刷新的加载图标拉下来不会自己消失」。
 * 真机取证（`tools/verify_android_refresh.ps1`）：在「当前任务」页下拉一次之后，
 * 界面上多出一个 `android.widget.ProgressBar`，15 秒后仍在。
 *
 * 根因是指示器读错了标志 —— 读的是**启动标志**（默认 true、只在 `start()` 里落回 false 一次），
 * 它永远不会再变真；`PullToRefreshBox` 于是收不到「刷新中 → 刷新完」的那次转换，
 * 自己拉出来的指示器就没有人去收。这一组断言钉住正确语义：
 * 指示器 = 一次刷新正在进行（`history.loading`，`refreshAll()` 的进出标志）。
 */
class RefreshIndicatorStateTest {

    @Test
    fun `首次进入时历史还在加载 指示器跟着转`() {
        // HistoryState.loading 默认就是 true（还没取过本地库），历史页此时也是这个标志在显示加载态。
        assertTrue(QuizUiState().refreshing)
    }

    @Test
    fun `本地数据取完 指示器立刻收起`() {
        val loaded = QuizUiState().copy(history = HistoryState(loading = false))
        assertFalse(loaded.refreshing, "取完还转着 = 用户报的那个「不会自己消失」")
    }

    @Test
    fun `一次完整刷新的进出 指示器跟着真与假`() {
        // refreshAll() 的语义：进第一句 loading = true，finally 里 loading = false。
        val started = QuizUiState().copy(history = HistoryState(loading = true))
        val finished = started.copy(history = started.history.copy(loading = false))

        assertTrue(started.refreshing, "刷新进行中应当转着")
        assertFalse(finished.refreshing, "刷新结束必须落回 false —— 少了这一次转换，M3 的指示器就收不回来")
    }
}
