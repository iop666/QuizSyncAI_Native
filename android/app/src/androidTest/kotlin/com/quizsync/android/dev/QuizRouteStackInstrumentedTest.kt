package com.quizsync.android.dev

import androidx.compose.material3.Text
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.test.getUnclippedBoundsInRoot
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.test.onAllNodesWithText
import androidx.compose.ui.test.onNodeWithText
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.quizsync.android.dev.state.NavDirection
import com.quizsync.android.dev.state.Route
import com.quizsync.android.dev.ui.QuizRouteStack
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

/**
 * 路由区的**图层接线**（用户 2026-09-28 报的 bug ①）。
 *
 * 这条测试存在的唯一理由：那个 bug 的根因不是"动画参数不对"，而是**上一页根本没进组合** ——
 * 手写路由只把 `routes.last()` 交给 `AnimatedContent`，屏幕最底下永远是主壳，
 * 所以四级页拖动时露出来的是**一级**，松手才突然换成三级。
 * 光看动画代码是看不出来的，必须断言"此刻被组合出来的到底是谁"。
 *
 * ⚠️ **这台小米平板（23046RP50C / Android 15 / MIUI）上跑不起来**：MIUI 会拦掉测试 Activity 的
 * 启动 —— 实测 `am instrument` 期间 `dumpsys activity` 里**一个本应用 Activity 都没有**、
 * 焦点始终在 `com.miui.home`，于是测试卡在 `waitForIdle`（`top` 显示进程 0% CPU，不是死循环）。
 * 同期应用自己 `am start` 是正常的（冷启动 1.4s、界面画得出来），所以这是设备策略问题、不是被测代码。
 * 取值规则另有 JVM 版（`test/.../ui/QuizRouteStackTest.kt`）每次都会跑；这一条留给
 * 非 MIUI 设备 / 模拟器（`gradle :app:connectedDebugAndroidTest`）。
 *
 * 用假内容（一行文字代表一页）而不是真页面：这里要断言的是**图层关系**，
 * 把真页面拉进来只会让失败原因变得含糊。
 */
@RunWith(AndroidJUnit4::class)
class QuizRouteStackInstrumentedTest {

    @get:Rule
    val rule = createComposeRule()

    // 四层：一级 = 主壳，二级 = 合集详情，三级 = 结果页，四级 = 识别模块设置。
    private val level2 = Route.CollectionDetail(collectionId = "c1", title = "二级")
    private val level3 = Route.Result(sessionId = "s1", collectionId = "c1")
    private val level4: Route = Route.RecognitionSettings

    private fun label(route: Route): String = when (route) {
        is Route.CollectionDetail -> "二级"
        is Route.Result -> "三级"
        is Route.RecognitionSettings -> "四级"
        is Route.Pairing -> "配对"
    }

    /** 挂上路由区；`progress` 是可变状态，测试可以中途改它（模拟手指拖动）。 */
    private fun show(
        routes: List<Route>,
        lastNav: NavDirection,
        gestureActive: Boolean,
        progress: () -> Float,
    ) {
        rule.setContent {
            QuizRouteStack(
                routes = routes,
                lastNav = lastNav,
                gestureActive = gestureActive,
                gestureProgress = progress,
                shell = { Text("一级主壳") },
                routeContent = { route -> Text(label(route)) },
            )
        }
        rule.waitForIdle()
    }

    private fun count(text: String): Int =
        rule.onAllNodesWithText(text).fetchSemanticsNodes().size

    @Test
    fun `四级页拖动时露出来的是三级，不是二级也不是一级`() {
        // 手指拖到一半：四级在前、三级在后。
        show(
            routes = listOf(level2, level3, level4),
            lastNav = NavDirection.Push,
            gestureActive = true,
            progress = { 0.5f },
        )

        assertEquals("四级（栈顶）必须在", 1, count("四级"))
        assertEquals("**直接上一层**三级必须在（这就是 bug ①）", 1, count("三级"))
        assertEquals("更下面那一层（二级）不该被组合出来", 0, count("二级"))
        assertEquals("主壳是栈底，永远在组合里", 1, count("一级主壳"))
    }

    @Test
    fun `没有手势时预览层不进组合`() {
        show(
            routes = listOf(level2, level3, level4),
            lastNav = NavDirection.Push,
            gestureActive = false,
            progress = { 0f },
        )

        assertEquals("四级（栈顶）必须在", 1, count("四级"))
        assertEquals("不在手势里就不该多组合一页出来", 0, count("三级"))
    }

    @Test
    fun `手指拖动时栈顶页跟着往右走`() {
        val progress = mutableFloatStateOf(0f)
        show(
            routes = listOf(level2, level3, level4),
            lastNav = NavDirection.Push,
            gestureActive = true,
            progress = { progress.floatValue },
        )

        assertEquals(
            "没拖动时栈顶页在原位",
            0f,
            rule.onNodeWithText("四级").getUnclippedBoundsInRoot().left.value,
            1f,
        )

        rule.runOnIdle { progress.floatValue = 0.5f }
        rule.waitForIdle()

        val moved = rule.onNodeWithText("四级").getUnclippedBoundsInRoot().left.value
        assertTrue("拖到一半栈顶页应该已经右移（left=$moved dp）", moved > 0f)
    }

    @Test
    fun `手势提交后栈顶换成上一页，出栈页不再出现在屏幕上`() {
        show(
            routes = listOf(level2, level3),
            // 手势提交：视觉已经演完，界面不许再播一遍。
            lastNav = NavDirection.PopGesture,
            gestureActive = false,
            progress = { 0f },
        )

        assertEquals("上一页成为栈顶", 1, count("三级"))
        assertEquals("已经出栈的那一页不该再被组合到屏幕上", 0, count("四级"))
    }

    @Test
    fun `普通返回后栈顶也是上一页（与手势提交的最终状态一致）`() {
        show(
            routes = listOf(level2),
            lastNav = NavDirection.Pop,
            gestureActive = false,
            progress = { 0f },
        )

        assertEquals("上一页成为栈顶", 1, count("二级"))
        assertEquals("已经出栈的那一页不该再被组合到屏幕上", 0, count("三级"))
    }
}
