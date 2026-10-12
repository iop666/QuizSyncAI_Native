package com.quizsync.android.dev.ui

import com.quizsync.android.dev.state.Route
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * 「预测式返回只能显示**直接上一层**」（用户 2026-09-28 报的 bug ①）。
 *
 * 用户的原话是：四级页开始拖动时"错误地看到二级甚至一级，松手三级才突然出现"。
 * 根因不是动画参数，而是**上一页压根没进组合**（手写路由只画栈顶，屏幕最底下永远是主壳），
 * 所以修法是把 `routes[size-2]` 组合到栈顶页下面。这一组断言钉的就是"到底取的是哪一页"。
 *
 * 为什么放在 JVM 单测：`androidTest` 里那条图形断言（`QuizRouteStackInstrumentedTest`）
 * 才是端到端的，但它要真机且要有能启动测试 Activity 的环境 —— 这台小米平板不给
 * （MIUI 把测试 Activity 的后台启动拦掉了，跑起来会挂在 `waitForIdle`）。
 * 取值规则是纯逻辑，放这里每次改动都会跑，不会被设备问题连累。
 */
class QuizRouteStackTest {

    // 四级：一级 = 主壳；二级 = 合集详情；三级 = 结果页；四级 = 识别模块设置。
    private val lv2 = Route.CollectionDetail(collectionId = "c1", title = "二级")
    private val lv3 = Route.Result(sessionId = "s1", collectionId = "c1")
    private val lv4: Route = Route.RecognitionSettings

    @Test
    fun `四级返回，预览层必须是三级`() {
        val preview = qsStackPreviewRoute(listOf(lv2, lv3, lv4))

        assertEquals(lv3, preview, "四级返回必须露出**直接上一层**三级")
        assertEquals(
            false,
            preview == lv2,
            "露出二级就是用户报的 bug（跳级）",
        )
    }

    @Test
    fun `三级返回，预览层是二级`() {
        assertEquals(lv2, qsStackPreviewRoute(listOf(lv2, lv3)))
    }

    @Test
    fun `五级返回，预览层是四级（不是三级、更不是一级）`() {
        val lv5 = Route.CollectionDetail(collectionId = "c9", title = "五级")
        assertEquals(lv4, qsStackPreviewRoute(listOf(lv2, lv3, lv4, lv5)))
    }

    @Test
    fun `二级返回时没有额外预览层 —— 上一页就是主壳`() {
        // 主壳本来就常驻在最底下，再组合一层就等于把主壳画两遍。
        assertNull(qsStackPreviewRoute(listOf(lv2)))
        assertNull(qsStackPreviewRoute(emptyList()))
    }

    @Test
    fun `还在栈里的那一页才跟着手势走`() {
        val stack = listOf(lv2, lv3)
        assertTrue(qsStackIsInStack(stack, lv3), "栈顶在栈里 → 跟手位移由手势进度驱动")
        assertFalse(qsStackIsInStack(stack, lv4), "刚出栈的那一页不在栈里 → 位置交给退场动画")
        assertFalse(qsStackIsInStack(stack, null), "空内容永远不算在栈里")
    }
}
