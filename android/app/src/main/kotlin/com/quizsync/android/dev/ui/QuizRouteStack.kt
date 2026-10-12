// 路由区的**图层结构**：主壳（栈底）+ 手势预览层（直接上一层）+ 栈顶页。
//
// 为什么单独一个文件：这三层的**叠放顺序与各自的位置规则**就是预测式返回的两个 bug 所在，
// 而它原来内联在 `MainActivity` 的一坨 `Box` 里 —— 既没法测，也很难一眼看出"谁盖着谁"。
// 抽出来之后，`QuizRouteStackInstrumentedTest` 可以拿假内容直接断言
// "手势拖动时被组合的是直接上一层，而不是主壳 / 不是更下面那一层"。
//
// 三个图层各自由什么驱动，别搞混：
//  * **主壳**：`animateFloatAsState(pushed)` → 压栈向左轻退 1/4 屏。它**必须一直在组合里**
//    （塞进 `AnimatedContent` 当 null 分支会把滚动位置与输入状态丢掉）；手势期间由进度插值抵消。
//  * **手势预览层**：只渲染 `routes[size-2]`，只在手势进行中出现。它是问题①的修复本体
//    —— 修好之前这里根本没有这一层，屏幕最底下永远是主壳，所以四级页拖动时露出的是**一级**。
//  * **栈顶页**：`AnimatedContent` 负责进场/退场（时间驱动），位置则按"它在不在栈里"摆
//    （`qsStackPageOffsetFactor`）—— 手势提交那一帧不许被摆回原位。
package com.quizsync.android.dev.ui

import androidx.compose.animation.AnimatedContent
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import com.quizsync.android.dev.state.NavDirection
import com.quizsync.android.dev.state.Route
import com.quizsync.android.dev.ui.designsystem.qsStackBackPage
import com.quizsync.android.dev.ui.designsystem.qsStackBackPreview
import com.quizsync.android.dev.ui.designsystem.qsStackFrontPage
import com.quizsync.android.dev.ui.designsystem.qsStackTransform

/**
 * **手势预览层要渲染的那一页** = 路由栈里倒数第二页 = **直接上一层**；`null` 表示"上一页就是主壳"
 * （栈里只有一页或没有），那时最底下那一层已经是它，不需要再多组合一层。
 *
 * 抽成纯函数是为了**能被单测钉住** —— 用户报的 bug ①（四级返回看到二级甚至一级）
 * 根因就在这一行的取值上，而它以前根本没有这一行（只有栈顶进组合，屏幕最底下永远是主壳）。
 * ⚠️ 四级返回必须拿到三级：`routes[2]` 取 `routes[1]`，不是 `first()`、也不是 `null`。
 */
fun qsStackPreviewRoute(routes: List<Route>): Route? =
    if (routes.size >= 2) routes[routes.size - 2] else null

/** 这一页**现在还在栈里**吗（等价于"它就是栈顶"）。出栈的那一页传进来是 `false`。 */
fun qsStackIsInStack(routes: List<Route>, route: Route?): Boolean =
    route != null && routes.contains(route)

/**
 * 路由区。
 *
 * @param routes 压在主壳之上的路由栈（最后一个是栈顶）。
 * @param lastNav 状态层给的**最近一次导航方向**（见 `NavDirection` 的注释）；方向一律由它决定，
 *   界面不许用"target 是不是 null"之类的方式猜 —— 那只对两层栈成立。
 * @param gestureActive 预测式返回手势**正在进行**（已经在跟手了）。预览层只在此时存在：
 *   手势一开始就组合好，等第一个 progress 事件到达时它已经在屏幕上，不会慢一帧。
 * @param gestureProgress 手势进度（0→1）的**读取器**。传 lambda 而不是 Float 是刻意的：
 *   进度只在 `graphicsLayer` 的绘制阶段被读，不进组合 —— 否则每帧重组整棵页面树。
 * @param shell 主壳（栈底那一层）。
 * @param routeContent 一页路由的**内容**（不含位置与转场，那些由本文件负责）。
 */
@Composable
fun QuizRouteStack(
    routes: List<Route>,
    lastNav: NavDirection,
    gestureActive: Boolean,
    gestureProgress: () -> Float,
    shell: @Composable () -> Unit,
    routeContent: @Composable (Route) -> Unit,
) {
    val top = routes.lastOrNull()

    // **上一页 = 栈里倒数第二页**（见 [qsStackPreviewRoute]）：四级返回看到的是三级。
    // 栈里只有一页时它是 null —— 那时被露出来的本来就应该是最底下的主壳。
    val backRoute = qsStackPreviewRoute(routes)

    // 这一次出栈是不是手势提交的（视觉已经演完，一格动画都不许再播）。
    val gestureCommitted = lastNav == NavDirection.PopGesture

    // 「主壳上面压着东西」：主壳要向左轻退 1/4 屏。判据与 `routeContent` 真能画出来的三种页一一对应
    // （配对页是整页替换主壳的，不算压在它上面）。
    val showingPushed =
        top is Route.CollectionDetail || top is Route.Result || top is Route.RecognitionSettings

    Box(modifier = Modifier.fillMaxSize()) {
        // ① 主壳：一直在组合里（滚动位置 / 输入状态活在这一层）。
        Box(
            modifier = Modifier
                .fillMaxSize()
                .qsStackBackPage(
                    pushed = showingPushed,
                    progress = gestureProgress,
                    // 手势提交的那一帧，静止值必须**立即**生效（详见 qsStackBackPage 的注释）。
                    instant = gestureCommitted,
                ),
        ) {
            shell()
        }

        // ② 手势预览层：直接上一层。手势期间从 −1/4 屏跟手回到 0，取消时沿原路回去。
        //    它带与压栈页相同的 16dp 圆角，所以静止时与"栈顶页压在上一层上"看起来完全一致。
        //    **只在"慢拖"时存在**：甩的那条路不跟手也不需要露出上一层（见 `MainActivity` 的速度判据），
        //    这样甩一次只要组合一页（落点页），比慢拖少一次整页组合 —— 那正是掉帧的来源。
        if (backRoute != null && gestureActive) {
            Box(
                modifier = Modifier
                    .fillMaxSize()
                    .qsStackBackPreview(progress = gestureProgress),
            ) {
                routeContent(backRoute)
            }
        }

        // ③ 栈顶页。进场 / 退场由 `AnimatedContent` 按 `lastNav` 选转场；横向位置由"在不在栈里"决定。
        AnimatedContent(
            modifier = Modifier.fillMaxSize(),
            targetState = top,
            transitionSpec = { qsStackTransform(nav = lastNav) },
            label = "quiz-route-stack",
        ) { route ->
            Box(
                modifier = Modifier
                    .fillMaxSize()
                    .qsStackFrontPage(
                        // ⚠️ 用"在不在栈里"而不是"是不是最后一个"：`AnimatedContent` 会把
                        // **刚出栈的那一页**再留一帧用来播退场。那一帧它必须是屏幕外（手势提交时）
                        // 或交给时间动画（普通返回时），绝不能因为"它等于 top"而被摆回原位。
                        inStack = qsStackIsInStack(routes, route),
                        gestureCommitted = gestureCommitted,
                        progress = gestureProgress,
                    ),
            ) {
                if (route != null) routeContent(route)
            }
        }
    }
}
