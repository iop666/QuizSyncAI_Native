// 页面栈转场 —— 按用户规格实现（2026-09-27 晚），参数与曲线取自 Image Toolbox。
//
// ## 规格（用户给的口径）
// * 进入：A **向左轻退**，B **从右侧进入**（右 → 左）。
// * 返回：B **向右退出**（左 → 右），A **从左侧自然回来**。
// * 两者方向**严格对应**；任何返回入口（顶部返回键 / 系统返回键 / 系统返回手势 / 代码 pop）
//   都走同一套逻辑，不允许"有的有动画、有的瞬间切换"。
// * 时长 250–350ms（本文件取 **300ms**），不要匀速生硬、不要超过 500ms。
//
// ## 预测式返回（2026-09-28 用户第二次报的两个 bug，这里是修复本体）
// 用户给的定义很明确：**预测式返回是一次连续、可被手指控制、可取消的转场**，
// 不是"手势预览动画 + 松手后再播一遍普通返回动画"。两个 bug 的根因都在这个文件 + MainActivity：
//
// ① **N+1 级返回时露出来的不是 N 级**。根因不在"取错上一页"，而在**上一页根本没进组合**：
//    手写路由只把 `routes.last()` 交给 `AnimatedContent`，屏幕最底下恒定是主壳，
//    所以四级页拖动时露出来的是**一级**（主壳），松手才换成三级。
//    修法：手势开始时把 `routes[size-2]`（**直接上一层**）组合在栈顶页下面，跟手从 −1/4 屏回到 0。
//    （见 [Modifier.qsStackBackPreview]，内容层在 MainActivity 的 `QuizRouteStack`。）
//
// ② **松手后又退回一次**。根因是"同一次返回被两套东西各演了一遍"：
//    手势已经把页面拖到终点，松手时 `popRoute()` 又发了一次 `Pop`，
//    于是 `AnimatedContent` 起了一段**全新的 300ms**；同时手势进度被归零 →
//    页面先从屏外跳回原位、再滑出去（用户看到的就是"回弹 / 跳一下 / 返回了两次"）。
//    修法：手势走完的出栈用 [NavDirection.PopGesture]（零时长），并且在提交的那一帧，
//    **已经出栈的那一页仍然被摆在屏幕外**（[qsStackPageOffsetFactor] 的第二条）——
//    这样即使 `AnimatedContent` 多留它一帧，也不可能盖住刚露出来的上一页。
//
// ## 出处（读过源码，不是猜的）
// * Image Toolbox 的栈动画：`core/ui/.../utils/animation/Animations.kt:73-98`
//   `stackAnimation(fade(300, AlphaEasing) + slide(400, FancyTransitionEasing) + scale(500, PointToPointEasing))`
// * 三条曲线：`core/ui/.../utils/animation/Easing.kt:22-26`（数值逐字照抄）
// * Decompose 的动画器语义：`extensions-compose/.../stack/animation/{Fade,Slide,Scale}.kt`
//   （进入 1→0、退出 0→±1；`Slide` 是"整屏宽 × factor"的位移，用 `layout{}` 摆位）
//
// ## 我们与它们的一处**有意不同**：不用缩放
// 早期版本让"被压住的那一页"缩放（0.88/0.94），结果是**同一件事由两个不同的组件在演**
// （主壳在 `AnimatedContent` 外面、合集页在里面），于是"三级返回二级"和"二级返回一级"看起来不一样。
// 现在统一成**纯横向视差**：离开/退后的那一页横向位移，进入的那一页整屏滑入 —— 两条路径逐帧同源。
package com.quizsync.android.dev.ui.designsystem

import androidx.compose.animation.ContentTransform
import androidx.compose.animation.EnterTransition
import androidx.compose.animation.ExitTransition
import androidx.compose.animation.core.CubicBezierEasing
import androidx.compose.animation.core.Easing
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.tween
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInHorizontally
import androidx.compose.animation.slideOutHorizontally
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.GraphicsLayerScope
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.unit.IntOffset
import com.quizsync.android.dev.state.NavDirection

/** Image Toolbox 的三条曲线（`core/ui/.../utils/animation/Easing.kt:22-26`，数值逐字照抄）。 */
object QsStackEasing {
    /** 位移。 */
    val Fancy: Easing = CubicBezierEasing(0.48f, 0.19f, 0.05f, 1.03f)

    /** 淡入（只用在"进来的那一页"上）。 */
    val Alpha: Easing = CubicBezierEasing(0.4f, 0.4f, 0.17f, 0.9f)
}

/** 普通页面 300ms（用户给的区间是 250–350ms）。 */
private const val SlideMs = 300

/** 进来那一页的淡入，比位移短一点。 */
private const val FadeMs = 200

/** 后面那一页"轻退"的距离：屏宽的 1/4（用户口径里的"向左轻微退出"）。 */
private const val Parallax = 0.25f

/**
 * 压栈 / 出栈 / 替换栈顶的转场。
 *
 * @param nav 由状态层给的导航方向（`QuizUiState.lastNav`）——**不要用 `targetState != null` 猜**：
 *            那只对两层栈成立，三级页返回二级页时 target 是二级页（非 null），会被误判成进场。
 */
fun qsStackTransform(nav: NavDirection): ContentTransform {
    val slide = tween<IntOffset>(SlideMs, easing = QsStackEasing.Fancy)
    val fade = tween<Float>(FadeMs, easing = QsStackEasing.Alpha)

    val enter: EnterTransition
    val exit: ExitTransition
    when (nav) {
        NavDirection.Push -> {
            // 进入（A → B）：B 从右侧整屏滑入（右 → 左）+ 淡入；A **向左轻退 1/4 屏**（视差，不整页退出）。
            enter = slideInHorizontally(slide) { it } + fadeIn(fade)
            exit = slideOutHorizontally(slide) { -(it * Parallax).toInt() }
        }

        NavDirection.Pop -> {
            // 返回（B → A）：B **向右整屏退出**（左 → 右）；A **从左侧自然回来**（−1/4 屏 → 0）。
            // 注意**不给退出的 B 加淡出** —— 淡出会读成"页面化掉"，而不是"退出去"。
            enter = slideInHorizontally(slide) { -(it * Parallax).toInt() }
            exit = slideOutHorizontally(slide) { it }
        }

        NavDirection.PopGesture -> {
            // **手势已经把这台戏演完了**：拖到哪、页面就在哪，松手那一刻视觉状态就是终点。
            // 所以这里一格动画都不播（零时长），只把导航状态换掉 —— 再播一遍就是用户报的
            // "松手后又重新退出一次 / 又进来一次"。
            // 页面位置由 [Modifier.qsStackFrontPage] 按"它在不在栈里"直接摆好，不依赖动画时序。
            enter = EnterTransition.None
            exit = ExitTransition.None
        }

        NavDirection.Replace -> {
            // 替换栈顶（结果页里的「上一次 / 下一次识别」）：页面整体不动，内容的横向切换由结果页自己播。
            enter = fadeIn(tween(120))
            exit = fadeOut(tween(120))
        }
    }

    // ⚠️ **必须显式关掉尺寸变换**（`sizeTransform = null`）：它会在转场结束那一刻重新量内容尺寸，
    // 量出来不同就把内容缩/放到目标尺寸 —— 表现就是"动画放完又跳一下"。
    // 两个内容都是 `fillMaxSize`，尺寸根本不变，这个变换只会帮倒忙。
    return ContentTransform(
        targetContentEnter = enter,
        initialContentExit = exit,
        targetContentZIndex = 0f,
        sizeTransform = null,
    )
}

/**
 * 一页在屏幕上的横向位置系数（× 屏宽）。**纯函数**，单测直接钉（`QsStackTransitionsTest`）。
 *
 * 关键是它的**输入不是动画进度，而是"这一页现在在栈里的身份"**：
 *
 * | 情况 | 系数 | 为什么 |
 * |---|---|---|
 * | 还在栈里（= 栈顶那一页） | `progress` | 手指拖到哪它就退到哪；取消时沿同一条路回到 0 |
 * | 已经出栈，且这次出栈是**手势**提交的 | `1`（屏幕外） | 视觉在手势里已经走完；提交那一帧它必须**仍然**在屏幕外 |
 * | 其它（普通进出场） | `0` | 位置交给 `AnimatedContent` 的时间动画，这一层不插手 |
 *
 * 第二条是问题 ② 的兜底：`AnimatedContent` 要等转场结束才把旧内容移出组合，
 * 中间至少还有一帧；如果那一帧它被摆回 0，就会盖住刚露出来的上一页 —— 表现就是"闪一下"。
 */
fun qsStackPageOffsetFactor(inStack: Boolean, gesture: Float, gestureCommitted: Boolean): Float = when {
    inStack -> gesture.coerceIn(0f, 1f)
    gestureCommitted -> 1f
    else -> 0f
}

/**
 * 「甩得太快」的判据（用户 2026-09-28：**「过快就没有动画，缓慢就有」**）：
 * 相邻两个手势进度事件之间的**速率**超过这个值就算"甩"。
 *
 * 真机量出来的速率（平板 e5b1641d，144Hz，`input swipe 5 1600 900 1600 <时长>`，
 * 每次都用 `adb logcat -s QuizSync` 把判定与速率打出来看）：
 *
 * | 一屏划过的时长 | 首段实测速率 | 判定 |
 * |---|---|---|
 * | 40ms | 5.5/s | 甩 |
 * | 80ms | 3.2–4.2/s | 甩 |
 * | 150ms | 3.5/s | 甩 |
 * | 300ms | 1.9–2.0/s | 慢拖 |
 * | 600ms | 1.2–1.7/s | 慢拖 |
 *
 * 所以门槛取 **3/s**（一屏 ≈ 330ms）刚好把"划一下就走"和"拉着看"分开。
 * 人手指是从静止加速的，第一段间隔比平均速度更慢 ⇒ 真慢拖会落在 1–2/s，留了一倍余量。
 *
 * 为什么要用**相邻两帧的速率**而不是"从手势开始算平均速度"：`onBackStarted` 是系统在手指
 * 已经移出边缘判定区之后才回调的，而且真机日志显示**开头连着两三个事件的进度都是 0.0**
 *（系统先发"起手"）—— 用平均值或不过滤零位移，慢拖与甩都会被算成 0/s，判据等于不存在
 *（第一版就是这么写的，日志一眼看穿）。
 *
 * 为什么判据只在**第一段有位移的间隔**上生效、而且**一次手势只判一次**：
 * 误判代价不对称 —— 把甩判成慢拖只是回到"多组合一页、掉帧"；把慢拖判成甩则是
 * **手指在拖、页面一动不动**，看起来像坏了。而真机日志显示**甩出去的手势本身就会减速**
 *（MIUI 的进度带阻尼：40ms 一屏的轻扫实测 3.9 → 3.2 → 2.4/s），所以"后面变慢就改判慢拖"
 * 会把甩全部翻回慢拖（试过，判据白做）。改成一次判死：真慢拖的第一段本来就只有 1–2/s，
 * 留了一倍多的余量，不会误判成甩。
 */
const val kFlingRatePerSecond = 3f

/**
 * 判定"甩"的最小位移：一屏的 1%。低于这个值说明手指几乎没动，
 * 那一小段的时间戳噪声足以把速率算成任意值 —— 直接按"慢拖"处理（安全的一侧）。
 */
const val kFlingMinDelta = 0.01f

/**
 * 这一段手势间隔算不算"甩"（纯函数，单测钉：`QsStackTransitionsTest`）。
 *
 * @param progressDelta 相邻两个进度事件的差值（0..1，负值按 0 处理）。
 * @param deltaMs 两次事件之间的毫秒数。同一帧内跳完（≤0）按"甩"处理。
 */
fun qsIsFling(progressDelta: Float, deltaMs: Float): Boolean {
    val delta = progressDelta.coerceAtLeast(0f)
    if (delta < kFlingMinDelta) return false
    if (deltaMs <= 0f) return true
    return delta / (deltaMs / 1000f) > kFlingRatePerSecond
}

/**
 * **当前页**（`AnimatedContent` 里的那一页）的横向位置。
 *
 * @param inStack 这一页**现在还在 `routes` 里**吗（等价于"它就是栈顶"）。出栈的那一页传 `false`。
 * @param gestureCommitted 这一次出栈是不是预测式返回手势提交的（`lastNav == PopGesture`）。
 * @param progress 手势进度的**读取器**。⚠️ 必须是 lambda：进度由 `Modifier.graphicsLayer`
 *   在**绘制阶段**读，不参与组合 —— 传 Float 会让整个页面（乃至整个 Activity）每帧重组一次。
 */
@Composable
fun Modifier.qsStackFrontPage(
    inStack: Boolean,
    gestureCommitted: Boolean,
    progress: () -> Float,
): Modifier = qsStableGraphicsLayer(
    block = { inStackNow, committedNow, progressNow ->
        translationX = size.width * qsStackPageOffsetFactor(inStackNow, progressNow, committedNow)
    },
    a = inStack,
    b = gestureCommitted,
    progress = progress,
)

/**
 * **手势预览层**（栈顶页下面那一层）的横向位置：跟手从 −1/4 屏回到 0。
 *
 * 这一层是问题 ① 的修复本体。它渲染的**永远是 `routes[size-2]`，也就是直接上一层**，
 * 不是主壳、也不是更下面那一层 —— 所以四级页拖动时看到的是三级页，绝不可能是二级或一级。
 * 位移参数与 [qsStackTransform] 的 `Pop` 分支严格同源（同一个 [Parallax]）。
 *
 * @param progress 进度读取器（理由同 [Modifier.qsStackFrontPage]）。
 */
@Composable
fun Modifier.qsStackBackPreview(progress: () -> Float): Modifier = qsStableGraphicsLayer(
    block = { _, _, progressNow ->
        translationX = size.width * Parallax * (progressNow.coerceIn(0f, 1f) - 1f)
    },
    a = false,
    b = false,
    progress = progress,
)

/**
 * **后面那一页**（被盖住 / 被露出的那一页，在本应用里可能是主壳，也可能是上一级子页）的动作。
 *
 * 与 [qsStackTransform] 严格对应，只是它必须一直留在组合里（主壳塞进 `AnimatedContent` 当 null 分支
 * 会把滚动位置与输入状态丢掉）：
 * * 压栈：**向左轻退 1/4 屏**（与上面 `exit` 的视差参数完全相同）；
 * * 返回：从 −1/4 屏**自然回到 0**。
 *
 * 预测式返回拖住不放时按手势进度插值（`progress` 0→1）：手指往回拉多少，它就回来多少。
 *
 * @param instant **不要在补间上再演一遍**。手势提交的那一帧，这一页的"静止值"必须**立即**生效：
 *   此刻 `pushed` 刚由真变假，若还走 `animateFloatAsState` 的 300ms 补间，
 *   它就会从 0 跳回 −1/4 屏再慢慢回来（二级返回一级时主壳就在眼前，这个跳非常明显）。
 * @param progress 手势进度读取器，理由同 [Modifier.qsStackFrontPage]。
 */
@Composable
fun Modifier.qsStackBackPage(
    pushed: Boolean,
    progress: () -> Float,
    instant: Boolean = false,
): Modifier {
    val target = if (pushed) -1f else 0f
    val shift = animateFloatAsState(
        targetValue = target,
        animationSpec = tween(SlideMs, easing = QsStackEasing.Fancy),
        label = "backShift",
    )

    return qsStableGraphicsLayer(
        block = { pushedNow, instantNow, progressNow ->
            val resting = (if (instantNow) (if (pushedNow) -1f else 0f) else shift.value) * Parallax
            translationX = size.width * (resting + (0f - resting) * progressNow.coerceIn(0f, 1f))
        },
        a = pushed,
        b = instant,
        progress = progress,
    )
}

/**
 * 一个**身份稳定**的 `graphicsLayer` 块。
 *
 * ## 为什么需要它（这是"返回时掉帧"的真凶，真机 2026-09-28 取证）
 *
 * `Modifier.graphicsLayer { … }` 的 block 一旦变化，Compose 就把这个图层标记为脏、**重录它的内容**。
 * 而上面三个修饰符的 block 都是**内联 lambda**：只要入参（`pushed` / `gestureCommitted` / `inStack`）
 * 变一下 —— 而每次压栈、每次返回都会变 —— lambda 的身份就变了，于是：
 *
 * * 主壳那一层（`qsStackBackPage`）在**每一次导航**都会被重录一遍。主壳里是历史列表 + 导航栏 + 顶栏，
 *   1800×2880 上这一下实测 **30–48ms**（`dumpsys gfxinfo framestats` 的 `DrawStart→SyncQueued`）；
 * * 手势预览层的 block 在"手指按下"那一帧也会变（`progress` 的来源换了），
 *   于是**预热好的那一页又白重录一次** —— 预热的收益全被这一步吃掉。
 *
 * 修法就是把 block 的**身份固定住**（`remember`），所有会变的值从 State 里读：
 * 绘制阶段读到变化只会**改图层矩阵**（几微秒），不会重录内容。实测同一段返回：
 * 录显示列表 34–48ms → **0.1–1.5ms**（见 docs/DECISIONS.md 本轮记录）。
 *
 * @param block 真正的绘制块，拿到的三个参数是"当前值"（不是创建时的值）。
 * @param a / b 两个会随导航变化的布尔入参。
 * @param progress 手势进度读取器。
 */
@Composable
private fun Modifier.qsStableGraphicsLayer(
    block: GraphicsLayerScope.(a: Boolean, b: Boolean, progress: Float) -> Unit,
    a: Boolean,
    b: Boolean,
    progress: () -> Float,
): Modifier {
    // 三个入参都走"可更新引用"：block 只创建一次，值每帧从 State 里现取。
    val latestA = rememberUpdatedState(a)
    val latestB = rememberUpdatedState(b)
    val latestProgress = rememberUpdatedState(progress)
    // block 本身也固定：它捕获的三个东西都是恒定身份的 State。
    val stableBlock: GraphicsLayerScope.() -> Unit = remember {
        {
            block(latestA.value, latestB.value, latestProgress.value())
        }
    }
    return this.graphicsLayer(stableBlock)
}
