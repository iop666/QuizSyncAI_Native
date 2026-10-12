// 界面状态：**一个不可变的 UiState**（用户要求：状态集中、StateFlow 驱动、组合期不做重活）。
//
// 状态机的口径逐条来自老应用：
//  * `ActiveTask` 与它的派生属性 ← app/lib/state/providers.dart:176-243（规格 §4.2 表）
//  * 主机连接四态          ← current_task_page.dart:143-171（规格 §2.3.1 表）
//  * 会话状态 / 结果页骨架  ← §2.4 / §3
package com.quizsync.android.dev.state

import com.quizsync.android.dev.model.CollectionGroup
import com.quizsync.android.dev.model.QuestionItem
import com.quizsync.android.dev.model.SessionItem

/** 主壳的三个标签（顺序固定，§1.2）。 */
enum class HomeTab(val label: String, val routeKey: String) {
    CurrentTask("当前任务", "tab-current"),
    History("历史记录", "tab-history"),
    Settings("设置", "tab-settings"),
}

/** 压在主壳之上的页面（§1.3 的 push 列表）。 */
sealed interface Route {
    /** 配对页（AppBar「重新配对」推入，也可以作为唯一路由存在）。 */
    data object Pairing : Route

    /** 结果页（可带合集，用来算「第 N / M 次识别」）。 */
    data class Result(val sessionId: String, val collectionId: String?) : Route

    /** 合集详情页。 */
    data class CollectionDetail(val collectionId: String?, val title: String) : Route

    /** 识别模块二级页。 */
    data object RecognitionSettings : Route
}

/**
 * 最近一次导航的方向 —— **由状态层告诉界面，界面不要自己猜**。
 *
 * 为什么必须显式带这个：界面曾用「`targetState` 是不是 null」来判断"进场还是返回"，
 * 那只对**两层**栈成立 —— 三级页返回二级页时 target 是二级页（非 null），于是被当成**进场**，
 * 播了「二级页从右边滑进来」的动画（用户原话：「三级页面返回到二级页面时，播的是一级进入二级的动画」）。
 * 状态层每次改路由都知道自己干的是 push / pop / 替换，直接记下来最准，也没有时序问题。
 */
enum class NavDirection {
    Push,

    /** 普通出栈（顶部返回键 / 系统返回键 / 代码 pop）：**由时间驱动**播完整的退出 + 上一页进场。 */
    Pop,

    /**
     * **预测式返回手势**完成后的出栈。
     *
     * 它与 [Pop] 的区别是"动画已经放过了"，不是"换个参数再放一遍"：
     * 手指拖到哪，栈顶页就往右走了多少、上一级页就从左侧露出来多少 —— 松手那一刻
     * **视觉状态已经是终点**。所以界面收到这个方向时**一格动画都不许再播**，
     * 直接把导航状态提交掉（`AnimatedContent` 用零时长转场），
     * 否则就是用户报的「拖到一半松手，页面又重新退出/重新进入一次」。
     */
    PopGesture,

    /**
     * 替换栈顶。
     *
     * ⚠️ **没有生产者**：唯一发过它的是结果页的「上一次 / 下一次识别」，那一处现在**不做动画**
     *（用户口径 2026-09-28：「删除除了进入页面和返回页面外的所有动画」），也不改路由。
     * 保留这个取值 + `qsStackTransform` 里的分支，是为了"真要替换栈顶"时有个现成的正确转场。
     */
    Replace,
}

/** 主机连接四态（§2.3.1）。 */
enum class HostConnection { Unpaired, Checking, Offline, Online }

/** 主机状态 + 当前合集（§2.2 副标题 / §2.3.1 状态行都用它）。 */
data class HostStatus(
    val connection: HostConnection = HostConnection.Unpaired,
    /** AppBar 副标题里的主机名：`info.device_name` → 配对时的 serverName → host。 */
    val deviceName: String? = null,
    val serverDeviceId: String? = null,
    val collectionId: String? = null,
    val collectionName: String? = null,
    /** `/tasks/active` 上报的主机合集列表（只增改不删的那个镜像，§1.4.1）。 */
    val collections: List<CollectionOption> = emptyList(),
    val aiConfigured: Boolean? = null,
    /** 主机 op 水位（涨了才做一次只拉不推的同步）。 */
    val opsLamport: Long = 0,
) {
    val hasActiveCollection: Boolean get() = !collectionId.isNullOrEmpty()
}

/** `/tasks/active` 里的一个合集条目。 */
data class CollectionOption(val collectionId: String, val name: String)

/** 一次识别的状态（§4.2 的 ActiveTask）。 */
enum class TaskStatus(val wire: String) {
    Queued("queued"),
    Analyzing("analyzing"),
    Done("done"),
    Failed("failed");

    companion object {
        fun parse(raw: String?): TaskStatus? = entries.firstOrNull { it.wire == raw }
    }
}

/**
 * 进行中的识别任务 —— 与 providers.dart:176-243 的 `ActiveTask` 逐字段对应。
 *
 * 派生属性（`running` / `showsPreviousResult` / …）就是规格 §4.2 表格左列，
 * 页面按它们选卡片，不自己判状态。
 */
data class ActiveTaskState(
    /** 是否已经发起过一次识别（本机发起或主机发起）。 */
    val started: Boolean = false,
    val taskId: String? = null,
    val sessionId: String? = null,
    val status: TaskStatus = TaskStatus.Queued,
    val imageCount: Int = 0,
    val message: String? = null,
    /** 上一轮已经完成、**仍留在页面上**的结果（§4.3 的「下一轮」浮层）。 */
    val shownSessionId: String? = null,
    /** 主机任务已完成、但「当前任务」页还没自动进入结果页。 */
    val autoOpenResult: Boolean = false,
    /** 这一轮是不是**主机自己发起**的识别（决定要不要自动跳结果页）。 */
    val hostInitiated: Boolean = false,
) {
    val idle: Boolean get() = !started
    val running: Boolean get() = started && (status == TaskStatus.Queued || status == TaskStatus.Analyzing)
    val failed: Boolean get() = started && status == TaskStatus.Failed
    val finished: Boolean get() = started && status == TaskStatus.Done

    /** 正在识别、页面上还留着上一轮结果 → 顶部浮层「N 张图片识别中…」。 */
    val showsPreviousResult: Boolean get() = running && !shownSessionId.isNullOrEmpty()

    /** 新一轮完成：浮层短暂显示「识别完成」，随后自动进入结果页。 */
    val completesIntoBanner: Boolean get() = finished && autoOpenResult && !shownSessionId.isNullOrEmpty()

    /** 卡片 A 的文案里用的张数（§2.3.2：`imageCount < 1 ? 1 : imageCount`）。 */
    val pages: Int get() = if (imageCount < 1) 1 else imageCount
}

/** 主机上报的一条任务状态（`/tasks/active` 与 WS 同构，这里只用轮询那条路）。 */
data class TaskUpdate(
    val taskId: String?,
    val status: TaskStatus,
    val sessionId: String?,
    val imageCount: Int,
    val message: String? = null,
    /** 由 `session.source_device` 判出来的「主机发起」（本机识别回声不算）。 */
    val hostInitiated: Boolean = false,
)

/**
 * `ActiveTaskNotifier.update()` 的逐行移植（providers.dart:276-304）。
 *
 * 关键在于 `sameRound`：同一轮任务的多次上报（queued → analyzing）**不是**新一轮，
 * 不能把旧结果再往 `shownSessionId` 挪一次；而「本机发起的任务被主机广播回来」时
 * 必须**保留** `hostInitiated=false`，否则手机自己的识别会被当成「电脑在识别」而跳页。
 */
fun ActiveTaskState.updated(update: TaskUpdate): ActiveTaskState {
    val incomingId = update.taskId?.takeIf { it.isNotEmpty() }
    val incomingSession = update.sessionId?.takeIf { it.isNotEmpty() }
    val running = update.status == TaskStatus.Queued || update.status == TaskStatus.Analyzing
    val sameRound = this.running &&
        (incomingId == null || this.taskId == null || incomingId == this.taskId)
    val carryOver = if (this.status == TaskStatus.Done) (this.sessionId ?: this.shownSessionId) else this.shownSessionId
    return ActiveTaskState(
        started = true,
        taskId = incomingId ?: this.taskId,
        sessionId = incomingSession ?: this.sessionId,
        status = update.status,
        imageCount = if (update.imageCount > 0) update.imageCount else this.imageCount,
        message = update.message ?: if (update.status == TaskStatus.Failed) this.message else null,
        shownSessionId = if (running && !sameRound) carryOver else this.shownSessionId,
        autoOpenResult = sameRound && this.autoOpenResult,
        hostInitiated = if (sameRound) this.hostInitiated else update.hostInitiated,
    )
}

/** `ActiveTaskNotifier.clear()`：失败卡「知道了」/ 完成卡「忽略」都走它。 */
fun ActiveTaskState.cleared(): ActiveTaskState = ActiveTaskState()

/** `consumeAutoOpen()`：自动跳页只跳一次。 */
fun ActiveTaskState.consumedAutoOpen(): ActiveTaskState =
    if (!autoOpenResult) this else copy(autoOpenResult = false)

/** 历史 tab 的状态（§2.4）。 */
data class HistoryState(
    val loading: Boolean = true,
    val error: String? = null,
    /** 空 = 显示合集分组；非空 = 显示搜索结果（§2.4 的二选一）。 */
    val keyword: String = "",
    val groups: List<CollectionGroup> = emptyList(),
    val groupsSessions: List<SessionItem> = emptyList(),
    val searchResults: List<SessionItem> = emptyList(),
    val detailTitle: String? = null,
    val detailSessions: List<SessionItem> = emptyList(),
)

/** 结果页的状态（§3）。 */
data class ResultState(
    val sessionId: String? = null,
    val collectionId: String? = null,
    /** 会话切换条用的列表（第 N / M 次识别）。 */
    val sessions: List<SessionItem> = emptyList(),
    val index: Int = 0,
    val loading: Boolean = true,
    /** 会话不存在（被清理或删除）→ §3.1 的 `missing` 空状态。 */
    val missing: Boolean = false,
    val session: SessionItem? = null,
    val questions: List<QuestionItem> = emptyList(),
    val reanalyzing: Boolean = false,
) {
    val hasPrev: Boolean get() = index < sessions.size - 1
    val hasNext: Boolean get() = index > 0

    /** AppBar 副标题：`第 {index+1} / {n} 次识别 · {time}`（§3.1）。 */
    val positionLabel: String?
        get() {
            if (sessions.isEmpty() || index !in sessions.indices) return null
            val current = sessions[index]
            return "第 ${index + 1} / ${sessions.size} 次识别 · ${current.timeLabel}"
        }
}

/**
 * 截屏采集链路的状态（规格 §3.1 悬浮球手势 / §3.2 截屏）。
 *
 * 这一块是**本机采集**的进度，与 `task`（主机在识别什么）分开：
 * 多页收集时页面还没提交给主机，`task` 那边什么都看不到。
 * 「当前任务」页与悬浮球的三态都读它（规格 §3.1：进度只反映在这两处）。
 */
data class CaptureState(
    /** 正在取帧 / 上传（球的「识别中」琥珀态）。 */
    val busy: Boolean = false,
    /** 多页收集中**已经截好并上传**的页数（0 = 不在多页模式）。 */
    val pages: Int = 0,
    /** 截屏会话可用（授权过且没失效）。设置页据此显示授权状态。 */
    val sessionReady: Boolean = false,
    /** 界面看到它涨一格就弹一次系统授权框（Composable 自己弹不了系统弹窗）。 */
    val authRequestId: Long = 0,
)

/** 主题模式（§2.5 的分段按钮）。 */
enum class ThemeMode(val label: String) {
    System("跟随系统"),
    Light("浅色"),
    Dark("深色"),
}

/** 截屏方式（§5 的 `_modeTile`）。 */
enum class CaptureMode(val wire: String) {
    Main("main"),
    Accessibility("accessibility");

    companion object {
        fun parse(raw: String?): CaptureMode = entries.firstOrNull { it.wire == raw } ?: Main
    }
}

/** 一次取帧**实际**走哪条路。 */
enum class CapturePath(val wire: String) {
    /** 主路径：MediaProjection 常驻会话。 */
    Projection("projection"),

    /** 备选路径：无障碍服务的 `takeScreenshot`。 */
    Accessibility("accessibility"),
}

/**
 * 采集方式 + 备选路径可用性 → 实际路径（规格 §3.2：无障碍是**备选**，不可用时回落主路径）。
 *
 * 纯判据，可 JVM 单测：这条规则错了的表现是「用户选了备选路径却走了主路径」，
 * 而两条路在界面上**看不出区别**（都能截到图），只能靠判据与日志分辨。
 */
fun qsCapturePath(mode: CaptureMode, accessibilityReady: Boolean): CapturePath =
    if (mode == CaptureMode.Accessibility && accessibilityReady) CapturePath.Accessibility else CapturePath.Projection

/**
 * 回落 / 不可用时的说明文案；`null` = 不需要说明（选的就是主路径，或备选路径确实可用）。
 *
 * **必须能解释三种不同的原因**，否则用户看到的现象完全一样（还是弹了授权框）：
 * ① 系统版本太低（`takeScreenshot` 是 Android 11 起才有的 API）；
 * ② 无障碍服务没开（用户没去系统设置里打开，或者打开了又关掉）；
 * ③ 服务开着但这次没取到帧（由调用方在取帧失败时另说）。
 */
fun qsCapturePathNote(mode: CaptureMode, accessibilityConnected: Boolean, supported: Boolean): String? = when {
    mode != CaptureMode.Accessibility -> null
    accessibilityConnected -> null
    !supported -> "这台设备是 Android 10 或更低，没有「无障碍截屏」能力（需要 Android 11+）：这次走主路径（屏幕录制）"
    else -> "无障碍服务还没开启，这次走的是主路径（屏幕录制）。在「设置 → 识别模块 → 无障碍（备选截屏路径）」里可以开启"
}

/** 权限快照：`null` = 未知（规格 §5.3 的 `granted == null` → 「未知」）。 */
data class PermissionSnapshot(
    val notifications: Boolean? = null,
    val overlay: Boolean? = null,
    val batteryWhitelist: Boolean? = null,
    val accessibility: Boolean? = null,
)

/** 设置 tab + 识别模块二级页的状态（§2.5 / §2.6）。 */
data class SettingsState(
    /** 识别模块开关默认 **关**（§2.5；ui-pkg/src/settings.dart:128）。 */
    val recognitionEnabled: Boolean = false,
    val themeMode: ThemeMode = ThemeMode.System,
    val systemDark: Boolean = false,
    /**
     * 动态取色（Material You）：用壁纸主色替换品牌绿。**默认关**。
     *
     * 为什么默认关：用户的品牌色就是设置页里那个绿（1.1.0 的 `AppSettings.accent` 默认值），
     * 开了会换成壁纸色 —— 那是用户自己该做的选择，不该由我们替他决定。
     * API 31 以下没有这个能力，界面上要隐藏这一项（不是显示成灰色开关）。
     */
    val dynamicColor: Boolean = false,
    /** 正文字号：默认 16，范围 12–32（§2.5 / result_page.dart:57-62）。 */
    val fontSize: Int = 16,
    /** 单次最多页数：默认 6，范围 1–6（§5 / core-pkg/util/keys.dart:17-18）。 */
    val multiPageLimit: Int = 6,
    val captureMode: CaptureMode = CaptureMode.Main,
    /** 悬浮球设置：开关 / 透明度 / 大小（球的截屏链路见 [CaptureState]）。 */
    val ballEnabled: Boolean = true,
    val ballOpacity: Float = 0.85f,
    val ballSizeDp: Int = 48,
    val permissions: PermissionSnapshot = PermissionSnapshot(),
) {
    val dark: Boolean get() = when (themeMode) {
        ThemeMode.System -> systemDark
        ThemeMode.Light -> false
        ThemeMode.Dark -> true
    }
}

/** 配对页的状态（§2.1）。 */
data class PairingUiState(
    val host: String = "",
    val code: String = "",
    val busy: Boolean = false,
    /** 过程文案：『正在连接 …』/『已找到电脑「…」，正在配对…』。 */
    val status: String? = null,
    /** 错误卡文案（失败时才有）。 */
    val error: String? = null,
    /** 深链已经消费过，避免重复自动提交。 */
    val linkConsumed: Boolean = false,
)

/** 应用整体状态（Composable 只读这一份）。 */
data class QuizUiState(
    val paired: Boolean = false,
    val pairedHost: String? = null,
    val tab: HomeTab = HomeTab.CurrentTask,
    /** 压在主壳之上的路由栈（最后一个是栈顶）。 */
    val routes: List<Route> = emptyList(),
    /** 最近一次导航方向（界面据此选进场/返回/替换的转场，见 [NavDirection]）。 */
    val lastNav: NavDirection = NavDirection.Push,
    val host: HostStatus = HostStatus(),
    /** 配对页的状态（配对页本身也是一个压栈路由，§1.4）。 */
    val pairing: PairingUiState = PairingUiState(),
    val task: ActiveTaskState = ActiveTaskState(),
    /** 「识别完成」卡片里逐题一行要用的题目（§2.3.2 卡片 D）。 */
    val taskQuestions: List<QuestionItem> = emptyList(),
    val taskQuestionsLoading: Boolean = false,
    val history: HistoryState = HistoryState(),
    val result: ResultState = ResultState(),
    val settings: SettingsState = SettingsState(),
    /** 截屏采集（悬浮球手势 → 取帧 → 多页暂存）的进度，见 [CaptureState]。 */
    val capture: CaptureState = CaptureState(),
    /** 「选择主机当前合集」弹窗的候选项；null = 不显示（§2.3.1）。 */
    val collectionPicker: List<CollectionOption>? = null,
    /** 单条提示（前台只保留最新一条，§1.4 的 SnackBar 策略）。 */
    val message: String? = null,
    /** 消息序号：同一条文案再发一次也要能弹出来。 */
    val messageId: Long = 0,
) {
    val topRoute: Route? get() = routes.lastOrNull()
}

/**
 * 下拉刷新指示器该不该转：**「正在刷新」这一个真相**。
 *
 * 数据源是 [HistoryState.loading]，因为 `refreshAll()`（下拉刷新 / 刷新图标 / 历史页下拉**同一个**入口）
 * 进第一句就是把它置 true、`finally` 里落回 false —— 它天然就是「一次刷新进行中」的标志。
 *
 * 踩过（用户报「下拉刷新的加载图标拉下来不会自己消失」）：这里原来读的是根状态上那个
 * 启动标志 `QuizUiState.loading`（默认 true，只在 `start()` 里落回一次 false）。
 * 它**永远不会再变真**，于是 `PullToRefreshBox` 收到的一直是 `isRefreshing = false`：
 * 下拉手势自己把指示器拉了出来，却没有任何一次「假 → 真 → 假」的转换去告诉它收起 ——
 * 指示器就原地转下去。真机取证：下拉后界面上多出一个 `android.widget.ProgressBar` 节点，
 * 15 秒后仍在（`tools/verify_android_refresh.ps1`）。
 */
val QuizUiState.refreshing: Boolean get() = history.loading
