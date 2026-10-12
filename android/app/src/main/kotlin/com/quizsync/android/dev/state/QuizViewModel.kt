// 唯一的状态持有者：所有取数、开网络、算派生量都在这里，Composable 只读 UiState。
//
// 三条与规格绑死的交互契约：
//  ① 前台每秒轮询 `/tasks/active`（`providers.dart` / 协议 §1.4.1「手机每秒一次」）；
//  ② **只有主机发起的**识别完成，才延迟 400ms 自动 push 结果页，且 App 在后台时不抢前台
//     （current_task_page.dart:31,66-70,107-119）—— 本机识别一律静默；
//  ③ 同步只在「主机 op 水位涨了」或用户下拉刷新时做一次**只拉不推**的拉取（§1.4.1）。
package com.quizsync.android.dev.state

import android.content.Context
import android.content.Intent
import android.content.SharedPreferences
import android.net.Uri
import android.os.Looper
import android.os.PowerManager
import android.provider.Settings
import android.util.Log
import android.widget.Toast
import androidx.core.app.NotificationManagerCompat
import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewModelScope
import com.quizsync.android.core.net.HostApi
import com.quizsync.android.core.net.HostResponse
import com.quizsync.android.dev.HostAddress
import com.quizsync.android.dev.ImageCompress
import com.quizsync.android.dev.PairRequest
import com.quizsync.android.dev.PairStore
import com.quizsync.android.dev.PairUri
import com.quizsync.android.dev.RecordStore
import com.quizsync.android.dev.capture.AccessibilityCaptureService
import com.quizsync.android.dev.capture.CaptureOverlay
import com.quizsync.android.dev.capture.CaptureService
import com.quizsync.android.dev.dbDispatcher
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import java.util.UUID

class QuizViewModel(
    context: Context,
    private val store: RecordStore,
    private val pairing: PairStore,
    private val prefs: SharedPreferences,
    private val deviceId: String,
    private val appVersion: String,
) : ViewModel() {

    private val appContext = context.applicationContext

    private val _state = MutableStateFlow(QuizUiState())
    val state: StateFlow<QuizUiState> = _state.asStateFlow()

    private var api: HostApi? = null
    private var pollJob: Job? = null
    private var searchJob: Job? = null
    private var autoOpenJob: Job? = null
    private var foreground = false
    private var messageCounter = 0L

    /** 悬浮球（`MainActivity` 建好后交进来）：截屏时要把它藏起来，见 [CaptureOverlay]。 */
    private var overlay: CaptureOverlay? = null

    /** 多页暂存：**已经上传好的页 hash（页序）**。空 = 不在多页模式（规格 §3.1）。 */
    private val pendingPages = mutableListOf<String>()

    /** 这一轮多页收集的任务 id（第一次长按定下来，收尾建任务时复用）。 */
    private var collectionTaskId: String? = null

    /** 授权框回来之后要接着做的那件事（用户点球时被系统框打断，回来得继续）。 */
    private var pendingCapture: PendingCapture? = null

    /** 一次手势还在处理中（取帧/上传没结束）：挡住连点，别产生两个任务。 */
    private var captureBusy = false

    /** 用户点球时想做的事：取一帧单图识别 / 取一页进多页。 */
    private enum class PendingCapture { Single, Page }

    /** 把悬浮球交给 ViewModel（截屏前藏、截完原地恢复）。 */
    fun attachCaptureOverlay(value: CaptureOverlay) {
        overlay = value
    }

    // ========================================================================
    // 启动与生命周期
    // ========================================================================

    fun start(linkedPair: PairRequest?) {
        val settings = SettingsState(
            recognitionEnabled = prefs.getBoolean(KEY_RECOGNITION_ENABLED, false),
            themeMode = ThemeMode.entries.firstOrNull { it.name == prefs.getString(KEY_THEME_MODE, null) }
                ?: ThemeMode.System,
            dynamicColor = prefs.getBoolean(KEY_DYNAMIC_COLOR, false),
            fontSize = prefs.getInt(KEY_FONT_SIZE, 16).coerceIn(12, 32),
            multiPageLimit = prefs.getInt(KEY_MULTIPAGE_LIMIT, 6).coerceIn(1, 6),
            captureMode = CaptureMode.parse(prefs.getString(KEY_CAPTURE_MODE, null)),
            ballEnabled = prefs.getBoolean(KEY_BALL_ENABLED, true),
            ballOpacity = prefs.getFloat(KEY_BALL_OPACITY, 0.85f),
            ballSizeDp = prefs.getInt(KEY_BALL_SIZE_DP, 48),
        )
        val host = pairing.host
        val token = pairing.token
        val paired = !host.isNullOrEmpty() && !token.isNullOrEmpty()
        rebuildApi()

        _state.update {
            it.copy(
                paired = paired,
                pairedHost = host,
                settings = settings,
                host = HostStatus(
                    connection = if (paired) HostConnection.Checking else HostConnection.Unpaired,
                    deviceName = host,
                ),
            )
        }
        refreshPermissions()
        viewModelScope.launch { loadHistory() }
        if (linkedPair != null) onPairLink(linkedPair)
        if (paired) {
            viewModelScope.launch {
                probeInfo()
                pullOpsIfNeeded(force = true)
            }
        }
    }

    fun setForeground(active: Boolean) {
        foreground = active
        if (active) startPolling() else stopPolling()
    }

    /**
     * 按 [PairStore] 里**当前**的地址与令牌重建 HTTP 客户端。
     *
     * **配对成功之后必须调它**（真机验收抓到的第三个接线缺口）：`api` 是 `start()` 那一刻
     * 按当时凭据建好的，而配对发生在**之后** —— 不重建的话，轮询 `/tasks/active` 一直用旧令牌
     * 打 401，界面永远停在「电脑未连接」，直到用户把 App 杀掉重开。
     * 更难发现的是：`refreshAll()` 里的同步走的是 `pairing.token`（**新**令牌）→ 服务端
     * `last_seen_at` 一直在更新，看起来「手机明明连着」，只有轮询那一路是坏的。
     */
    private fun rebuildApi() {
        val host = pairing.host
        val token = pairing.token
        val paired = !host.isNullOrEmpty() && !token.isNullOrEmpty()
        // `paired` 是 val、且判据里已经检查过非空，Kotlin 2.0 起能据此智能转换
        // （原来的 `host!!` / `token!!` 是多余的，编译器会报 Unnecessary non-null assertion）。
        api = if (paired) HostApi(host).apply { setToken(token) } else null
        _state.update { it.copy(paired = paired, pairedHost = host) }
    }

    override fun onCleared() {
        stopPolling()
        autoOpenJob?.cancel()
        store.close()
        super.onCleared()
    }

    // ========================================================================
    // 轮询：主机状态 + 活动任务
    // ========================================================================

    private fun startPolling() {
        if (pollJob?.isActive == true) return
        pollJob = viewModelScope.launch {
            var tick = 0
            while (isActive) {
                pollActiveTask()
                if (tick % 5 == 0) probeInfo()
                tick++
                delay(1000)
            }
        }
    }

    private fun stopPolling() {
        pollJob?.cancel()
        pollJob = null
    }

    /** `GET /tasks/active`：主机在识别什么（协议 §1.4.1，手机前台每秒一次）。 */
    private suspend fun pollActiveTask() {
        val client = api ?: return
        val response = withContext(Dispatchers.IO) { runCatching { client.activeTask() }.getOrNull() }
        if (response == null) {
            // 网络层就失败了（连不上）—— 与「主机回了非 2xx」分开如实反映。
            _state.update {
                if (it.paired) it.copy(host = it.host.copy(connection = HostConnection.Offline)) else it
            }
            return
        }
        if (response.status !in 200..299) {
            _state.update {
                it.copy(host = it.host.copy(connection = if (it.paired) HostConnection.Offline else HostConnection.Unpaired))
            }
            return
        }

        val body = response.jsonObject()
        val status = TaskStatus.parse(body["status"]?.jsonPrimitive?.content)
        val collections = body["collections"]?.jsonArray?.mapNotNull { element ->
            val obj = element.jsonObject
            val id = obj["collection_id"]?.jsonPrimitive?.content ?: return@mapNotNull null
            CollectionOption(id, obj["name"]?.jsonPrimitive?.content ?: id)
        } ?: emptyList()
        val sessionObj = body["session"]?.jsonObject
        val sourceDevice = sessionObj?.get("source_device")?.jsonPrimitive?.content
        val sessionId = sessionObj?.get("session_id")?.jsonPrimitive?.content
            ?: body["session_id"]?.jsonPrimitive?.content
        val hostInitiated = sourceDevice == null || sourceDevice != deviceId
        val imageCount = (sessionObj?.get("image_count")?.jsonPrimitive?.content?.toIntOrNull())
            ?: (body["image_count"]?.jsonPrimitive?.content?.toIntOrNull() ?: 0)

        var openedResult: String? = null
        _state.update { current ->
            val hostStatus = current.host.copy(
                connection = HostConnection.Online,
                collectionId = body["active_collection_id"]?.let { if (it.toString() == "null") null else it.jsonPrimitive.content },
                collectionName = body["active_collection_name"]?.let { if (it.toString() == "null") null else it.jsonPrimitive.content },
                collections = if (collections.isEmpty()) current.host.collections else collections,
                opsLamport = body["ops_lamport"]?.jsonPrimitive?.content?.toLongOrNull() ?: 0L,
            )
            val task = when (status) {
                null -> current.task
                TaskStatus.Done -> current.task.updated(
                    TaskUpdate(
                        taskId = body["task_id"]?.jsonPrimitive?.content,
                        status = TaskStatus.Done,
                        sessionId = sessionId,
                        imageCount = imageCount,
                        hostInitiated = hostInitiated,
                    ),
                ).let { done ->
                    // `done` 一旦上报，就带上「完成后自动进结果页」的意图；
                    // 本机（非主机发起）识别一律静默（§2.3.3）。
                    if (hostInitiated) done.copy(autoOpenResult = true) else done.copy(autoOpenResult = false)
                }

                TaskStatus.Failed -> current.task.updated(
                    TaskUpdate(
                        taskId = body["task_id"]?.jsonPrimitive?.content,
                        status = TaskStatus.Failed,
                        sessionId = sessionId,
                        imageCount = imageCount,
                        message = body["message"]?.jsonPrimitive?.content,
                        hostInitiated = hostInitiated,
                    ),
                )

                else -> current.task.updated(
                    TaskUpdate(
                        taskId = body["task_id"]?.jsonPrimitive?.content,
                        status = status,
                        sessionId = body["session_id"]?.jsonPrimitive?.content,
                        imageCount = imageCount,
                        hostInitiated = hostInitiated,
                    ),
                )
            }
            // 自动跳页只跳一次；结果页已经开着**这一轮**时不重复推。
            // ⚠️ 判据是「界面**正在显示**的那一条」（`result.sessionId`），不是路由里那条 ——
            // 「上一次 / 下一次识别」只在页面内部换内容、不再改路由，路由里的 id 是"进这一页时的那个"。
            val alreadyOpen = current.routes.lastOrNull() is Route.Result &&
                current.result.sessionId == task.sessionId
            if (task.finished && task.autoOpenResult && foreground && !alreadyOpen) {
                openedResult = task.sessionId
            }
            current.copy(host = hostStatus, task = task)
        }

        val lamport = _state.value.host.opsLamport
        if (lamport > lastAppliedLamport) pullOpsIfNeeded(force = false)

        // **识别结果落地**：主机不为识别结果生成 op（它只中转 op），所以结果得由客户端
        // 从任务载荷写进本地库 —— 协议 §4.2 就是这么规定的。不写的话，当前任务页会显示
        // 「识别完成，可以离线查看」，点进去却是「本次识别已不存在」。
        if (status == TaskStatus.Done && sessionId != null) {
            applyResultPayload(
                taskId = _state.value.task.taskId ?: body["task_id"]?.jsonPrimitive?.contentOrNull,
                fromActive = sessionObj,
                sessionId = sessionId,
            )
        }

        loadTaskPreview()

        if (openedResult != null) scheduleAutoOpen(openedResult)
    }

    /** 已经落地过的会话（轮询每秒一次，别反复写库）。 */
    private val appliedResults = mutableSetOf<String>()

    /**
     * 把一次识别结果写进本地库。
     *
     * * 主机自发的识别：`/tasks/active` 的 `session` 自带载荷；
     * * 手机自己提交的任务：载荷要用 `GET /api/v1/tasks/{id}` 取（`/tasks/active` 不带）。
     *
     * 作者写成**主机**（`host.serverDeviceId`）：这些字段本来就是主机写的，手机只是镜像。
     * 拿不到主机 device_id 时**先不落地**（下个 tick 再试），也别把作者记错 ——
     * 记错了以后真出冲突会判错方向。
     */
    private suspend fun applyResultPayload(taskId: String?, fromActive: JsonObject?, sessionId: String) {
        if (sessionId in appliedResults) return
        val hostDeviceId = _state.value.host.serverDeviceId
        if (hostDeviceId.isNullOrEmpty()) return

        val payload = fromActive ?: run {
            val id = taskId ?: return
            val client = api ?: return
            withContext(Dispatchers.IO) {
                runCatching { client.task(id).jsonObject()["session"]?.jsonObject }.getOrNull()
            }
        } ?: return

        val wrote = withContext(dbDispatcher) { store.applyTaskResult(payload, hostDeviceId) }
        appliedResults += sessionId
        if (wrote > 0) {
            loadHistory()
            refreshOpenResult()
        }
    }

    /** 「识别完成」卡片里那几行答案（§2.3.2 卡片 D）：只在会话变了或刚同步完时读一次库。 */
    private var taskPreviewSessionId: String? = null

    private suspend fun loadTaskPreview() {
        val task = _state.value.task
        if (!task.finished) {
            if (taskPreviewSessionId != null) {
                taskPreviewSessionId = null
                _state.update { it.copy(taskQuestions = emptyList(), taskQuestionsLoading = false) }
            }
            return
        }
        val sessionId = task.sessionId ?: return
        if (sessionId == taskPreviewSessionId && _state.value.taskQuestions.isNotEmpty()) return
        taskPreviewSessionId = sessionId
        _state.update { it.copy(taskQuestionsLoading = true) }
        val questions = withContext(dbDispatcher) { store.questions(sessionId) }
        _state.update { it.copy(taskQuestions = questions, taskQuestionsLoading = false) }
    }

    /** `GET /info`（免鉴权）：主机名、AI 是否配置、协议版本。 */
    private suspend fun probeInfo() {
        val host = _state.value.pairedHost ?: return
        val response = withContext(Dispatchers.IO) {
            runCatching { HostApi(host).info() }.getOrNull()
        }
        if (response == null || response.status !in 200..299) {
            _state.update {
                it.copy(host = it.host.copy(connection = if (it.paired) HostConnection.Offline else HostConnection.Unpaired))
            }
            return
        }
        val name = response.string("device_name")
        val aiConfigured = response.jsonObject()["ai_configured"]?.jsonPrimitive?.content?.toBoolean()
        _state.update {
            it.copy(
                host = it.host.copy(
                    connection = HostConnection.Online,
                    deviceName = name ?: it.host.deviceName,
                    serverDeviceId = response.string("device_id"),
                    aiConfigured = aiConfigured,
                    collectionId = response.jsonObject()["active_collection_id"]
                        ?.let { c -> if (c.toString() == "null") null else c.jsonPrimitive.content }
                        ?: it.host.collectionId,
                    collectionName = response.jsonObject()["active_collection_name"]
                        ?.let { c -> if (c.toString() == "null") null else c.jsonPrimitive.content }
                        ?: it.host.collectionName,
                ),
            )
        }
    }

    /**
     * 自动跳结果页：延迟 **400ms**（current_task_page.dart:31），且只在前台做
     * （§2.3.3「App 在后台时不抢前台」）。
     */
    private fun scheduleAutoOpen(sessionId: String) {
        autoOpenJob?.cancel()
        autoOpenJob = viewModelScope.launch {
            delay(AUTO_OPEN_DELAY_MS)
            val current = _state.value
            if (!foreground) return@launch
            if (current.task.sessionId != sessionId) return@launch
            val collectionId = current.host.collectionId
            openResult(sessionId, collectionId)
            _state.update { it.copy(task = it.task.consumedAutoOpen()) }
        }
    }

    // ========================================================================
    // 同步（只拉不推）
    // ========================================================================

    private var lastAppliedLamport = 0L

    /**
     * 只在「主机 op 水位涨了」时拉一次（§1.4.1；`force` 用于首次与手动刷新）。
     *
     * 失败**静默**（协议原文：失败等下一次水位变化或重连时再试），但刷新按钮那条路
     * 会把错误如实写进提示。
     */
    private suspend fun pullOpsIfNeeded(force: Boolean) {
        val host = _state.value.pairedHost ?: return
        val token = pairing.token ?: return
        if (!force && _state.value.host.opsLamport <= lastAppliedLamport) return
        val result = withContext(dbDispatcher) { runCatching { store.sync(host, token) } }
        lastAppliedLamport = _state.value.host.opsLamport
        result.onSuccess {
            // 新 op 落地了：历史、结果页、当前任务的答案预览都要重读一遍。
            taskPreviewSessionId = null
            loadHistory()
            refreshOpenResult()
            loadTaskPreview()
        }
    }

    /**
     * 把本机产生的 op 推给主机（**只推不拉**）。
     *
     * 删除入口（删记录 / 删合集）删完立刻调一次：不推的话，界面上删掉了、电脑上还在 ——
     * 而界面那句文案承诺的是「软删除并同步到电脑」。1.1.0 的 M50 就是为这件事补的
     * （`AndroidSync.pushOpsOnly`），2.0 重写时漏了，2.0 计划里「手机删合集 → 正常同步给主机」
     * 这一条因此不成立。
     *
     * 失败**静默**（只写日志）：本地墓碑已经落库了，下次同步/冷启动还会再推；
     * 反过来把「删除成功」改成「失败」才是错的 —— 用户看到的是本机事实。
     */
    private suspend fun pushLocalOps() {
        val host = _state.value.pairedHost ?: return
        val token = pairing.token ?: return
        val pushed = withContext(dbDispatcher) { runCatching { store.pushPending(host, token) } }
        pushed.onSuccess { count ->
            if (count > 0) {
                Log.i(TAG, "已推送本机 op：$count 条")
            }
        }.onFailure {
            Log.w(TAG, "推送本机 op 失败（本地已落库，下次同步再推）：${it.message}")
        }
    }

    /** 手动刷新（现在任务页的刷新图标 / 下拉刷新 / 历史下拉刷新）。 */    fun refreshAll(manual: Boolean = true) {
        viewModelScope.launch {
            // 刷新标志**必须在 finally 里落回 false**。
            // 踩过：原来这个函数从头到尾没碰过 `history.loading`，而未配对时会提前 `return`；
            // 一旦 `loadHistory()` 因为别的原因没跑完（当时是单线程派发器被 5 秒的网络轮询堵住），
            // 下拉指示器就**一直转着不消失**。用户报的正是这个。
            _state.update { it.copy(history = it.history.copy(loading = true)) }
            try {
                // 凭据可能刚变过（用户刚配对 / 刚解除配对）：先重建客户端，再决定要不要联网。
                rebuildApi()

                // **本地数据先刷，它不依赖配对。**
                // 踩过：原来这里第一句就是「没配对 → return」，于是未配对时 `loadHistory()`
                // 永远不会被调到 —— 表现是「本地明明有记录（或刚删了一条），历史页却一直显示空态」。
                // 造测试数据时暴露出来的：写完库弹了「已生成」，页面纹丝不动。
                loadHistory()

                if (!_state.value.paired) {
                    if (manual) notify("请先与电脑配对")
                    return@launch
                }
                _state.update { it.copy(host = it.host.copy(connection = HostConnection.Checking)) }

                // **联网这一段必须有上限**（第 168 轮探针抓到的第二层问题）：
                // 指示器读的 `history.loading` 要等整个 `refreshAll` 走完才落回 false，
                // 而 `probeInfo` / `pollActiveTask` / `store.sync` 都是网络调用 ——
                // 主机那边半死不活（TCP 建上了但不回包）时，它们会一直挂到操作系统的超时，
                // 表现就是用户看到的那句「拉下来一直转、不会自己消失」（实测真机上转满 60 秒仍在）。
                // 超时之后照样往下走：本地数据已经在上面刷过了，`finally` 把指示器收掉。
                val applied = withTimeoutOrNull(NetworkPhaseTimeoutMs) {
                    probeInfo()
                    pollActiveTask()
                    val host = _state.value.pairedHost
                    val token = pairing.token
                    if (host == null || token == null) {
                        0
                    }
                    else {
                        val result = withContext(dbDispatcher) { runCatching { store.sync(host, token) } }
                        result.onSuccess { count ->
                            lastAppliedLamport = _state.value.host.opsLamport
                            loadHistory()
                            refreshOpenResult()
                            count
                        }.getOrElse { error ->
                            if (manual) notify("同步失败：${error.message ?: error::class.simpleName}")
                            0
                        }
                    }
                }

                if (applied == null && manual) {
                    notify("刷新超时：电脑没回话，等会儿再试一次")
                }
                else if (manual && (applied ?: 0) > 0) {
                    notify("已从电脑同步 $applied 条变更")
                }
            } finally {
                _state.update { it.copy(history = it.history.copy(loading = false)) }
            }
        }
    }

    // ========================================================================
    // 历史（§2.4）
    // ========================================================================

    private suspend fun loadHistory() = withContext(dbDispatcher) {
        // **必须兜底**：`history.loading` 的默认值是 `true`，而这里原来是「成功了才置 false」——
        // 查询一抛异常，加载/下拉指示器就**永远转下去收不回来**（用户报的正是这个）。
        // 顺手把错误记进 `history.error`：静默失败和一直转圈一样难查。
        try {
            val groups = store.groups()
            val sessions = store.sessions()
            _state.update {
                it.copy(history = it.history.copy(loading = false, error = null, groups = groups, groupsSessions = sessions))
            }
        } catch (error: Exception) {
            _state.update {
                it.copy(
                    history = it.history.copy(
                        loading = false,
                        error = error.message ?: error::class.simpleName,
                    ),
                )
            }
        }
    }

    /** 搜索框输入（防抖 250ms，§2.4）。 */
    fun setHistoryKeyword(keyword: String) {
        _state.update { it.copy(history = it.history.copy(keyword = keyword)) }
        searchJob?.cancel()
        if (keyword.isBlank()) {
            _state.update { it.copy(history = it.history.copy(searchResults = emptyList())) }
            return
        }
        searchJob = viewModelScope.launch {
            delay(250)
            val results = withContext(dbDispatcher) { store.searchSessions(keyword) }
            _state.update { it.copy(history = it.history.copy(searchResults = results)) }
        }
    }

    /** 进入合集详情页（§2.4.4）：标题来自上一页，内容单独查一次。 */
    /** 打开「识别模块设置」二级页（规格 §5）。1.1.0 的结构：设置首页只放入口。 */
    fun openRecognitionSettings() {
        _state.update {
            if (it.routes.any { route -> route is Route.RecognitionSettings }) it
            else it.copy(routes = it.routes + Route.RecognitionSettings, lastNav = NavDirection.Push)
        }
        // 权限是在**系统界面**里改的，进页面时重读一次，别显示上一次的快照。
        refreshPermissions()
    }

    fun openCollectionDetail(collectionId: String?, title: String) {
        viewModelScope.launch {
            loadDetail(collectionId, title)
            _state.update { it.copy(routes = it.routes + Route.CollectionDetail(collectionId, title), lastNav = NavDirection.Push) }
        }
    }

    /**
     * 只加载详情数据，**不压路由**。
     *
     * 抽出来是因为「删完之后要刷新详情列表」这个需求：`openCollectionDetail` 会往
     * `routes` 里再压一层，删一条记录就多出一层同名路由，返回键要按好几次才出得来。
     * 压路由与取数据是两件事，别绑在一个函数里。
     */
    private suspend fun loadDetail(collectionId: String?, title: String) {
        val sessions = withContext(dbDispatcher) {
            if (collectionId == null) store.sessions().filter { it.collectionId == null }
            else store.sessions(collectionId)
        }
        _state.update {
            it.copy(history = it.history.copy(detailTitle = title, detailSessions = sessions))
        }
    }

    // ========================================================================
    // 结果页（§3）
    // ========================================================================

    fun openResult(sessionId: String, collectionId: String?) {
        viewModelScope.launch {
            val sessions = withContext(dbDispatcher) {
                if (collectionId == null) store.sessions() else store.sessions(collectionId)
            }
            val index = sessions.indexOfFirst { it.sessionId == sessionId }.let { if (it < 0) 0 else it }
            _state.update {
                it.copy(
                    routes = it.routes + Route.Result(sessionId, collectionId),
                    lastNav = NavDirection.Push,
                    result = it.result.copy(
                        sessionId = sessionId,
                        collectionId = collectionId,
                        sessions = sessions,
                        index = index,
                        loading = true,
                        missing = false,
                    ),
                )
            }
            loadResult(sessionId)
        }
    }

    /**
     * 结果页里的「上一次 / 下一次识别」（§3.1：上一次 = 更早的一条）。
     *
     * ⚠️ **先把这一条从库里读出来，再换 `sessionId`**（"原子切换"）。原来的写法是
     * 「先换 id、把 questions 清空、loading 置真」再异步读 —— 那样横向切换的第一帧里，
     * 新那一层画的是**转圈**，几十毫秒后才换成题目：动画中途换一次内容 = 又一次整屏重排，
     * 两次都压在动画上（`dumpsys gfxinfo` 实测每次切换一个 46–93ms 的 UI 线程尖刺，用户报"动画太卡"）。
     * 现在换 id 的那一帧内容就已经就绪，横移过程里不会再有任何重组。
     *
     * 用 `Mutex` 串行化：连点几下时每一下都按**最新**状态算，既不会丢步，也不会乱序写回。
     */
    fun stepResult(delta: Int) {
        viewModelScope.launch {
            stepMutex.withLock {
                val current = _state.value.result
                val next = current.index + delta
                if (next !in current.sessions.indices) return@withLock
                val target = current.sessions[next]
                val (session, questions) = withContext(dbDispatcher) {
                    store.session(target.sessionId) to store.questions(target.sessionId)
                }
                _state.update {
                    it.copy(
                        // ⚠️ **路由一行都不动**（用户报「并没有实现」的根因就在这里）：
                        // `Route.Result` 是 data class、`sessionId` 参与相等性 —— 改了它，外层
                        // `AnimatedContent`（`QuizRouteStack`）就认为"换页了"，会把整个 `ResultScreen`
                        // **重建一遍**，页面内那个横向切换第一次组合就是新 id ⇒ 一次过渡都不播。
                        result = it.result.copy(
                            sessionId = target.sessionId,
                            index = next,
                            loading = false,
                            missing = session == null,
                            session = session,
                            questions = questions,
                        ),
                    )
                }
            }
        }
    }

    /** 「上一次 / 下一次识别」串行化用（见 [stepResult]）。 */
    private val stepMutex = Mutex()

    private suspend fun loadResult(sessionId: String) {
        val (session, questions) = withContext(dbDispatcher) {
            store.session(sessionId) to store.questions(sessionId)
        }
        _state.update {
            // 页面可能已经被关掉或切到别的会话了：只有还停在这次会话上才写回。
            if (it.result.sessionId != sessionId) it
            else it.copy(
                result = it.result.copy(
                    loading = false,
                    missing = session == null,
                    session = session,
                    questions = questions,
                ),
            )
        }
    }

    /** 同步完成后，如果结果页正开着且还没有题目，就再读一次（完成 → 拉 op → 出题）。 */
    private suspend fun refreshOpenResult() {
        val sessionId = _state.value.result.sessionId ?: return
        // 「结果页正开着」只看**栈顶是不是结果页**；它显示的是哪一条由 `result.sessionId` 决定 ——
        // 「上一次 / 下一次识别」不再改路由（见 `stepResult`），拿路由里的 id 去比会永远比不中。
        if (_state.value.routes.lastOrNull() !is Route.Result) return
        loadResult(sessionId)
    }

    /** `重新生成`（§3.3）：只有三种文案照规格，其余如实回显主机给的信息。 */
    fun reanalyzeCurrent() {
        val client = api
        val sessionId = _state.value.result.sessionId
        if (client == null || !_state.value.paired) {
            notify("还没有配对，无法重新生成")
            return
        }
        if (sessionId == null) return
        viewModelScope.launch {
            _state.update { it.copy(result = it.result.copy(reanalyzing = true)) }
            val response = withContext(Dispatchers.IO) { runCatching { client.reanalyze(sessionId) }.getOrNull() }
            _state.update { it.copy(result = it.result.copy(reanalyzing = false)) }
            when {
                response == null -> notify("重新生成失败：连不上电脑")
                response.status in 200..299 -> notify("已请求电脑重新生成，完成后本页会自动刷新")
                response.string("code") == "no_active_collection" -> notify("请先在电脑上选择任务合集")
                else -> notify(
                    "重新生成失败：" +
                        (response.string("message") ?: response.string("code") ?: "http_${response.status}"),
                )
            }
        }
    }

    /**
     * 结果页空状态里的『重新加载』（§3.3 的 `reload-result`）。
     *
     * **只重读题目与会话，不压路由、不动别的状态** —— 光有 UI 的话这一行没地方放：
     * 复用 `openResult` 会再压一层 `Route.Result`（点一次多按一次返回键），
     * 复用 `refreshAll` 会顺带发网络请求并弹提示。这里就是 `loadResult` 的公开入口。
     */
    fun reloadResult() {
        val sessionId = _state.value.result.sessionId ?: return
        viewModelScope.launch {
            _state.update { it.copy(result = it.result.copy(loading = true, missing = false)) }
            loadResult(sessionId)
        }
    }

    // ========================================================================
    // 发起识别（规格 §2.2 的「相册选图搜题」FAB）
    // ========================================================================

    /**
     * 相册选了一张图 → 压缩 → **走与截屏同一条**提交路径（上传 → 建任务 → 轮询）。
     *
     * 全程**静默**（规格 §2.2 的 FAB 行 + §3.3）：不切 tab、不跳结果页、不抢前台，
     * 只更新「当前任务」页；结果由已有的 1 秒轮询 + 水位触发的同步找回（那两步早就在了）。
     *
     * 三条前置校验的文案是规格逐字（home_page.dart:537-552 与 `kNoActiveCollectionMessage`）：
     * 未配对 / 识别模块关 / 主机未选合集。**必须出声** —— 静默失败会被当成按钮坏了。
     */
    fun submitImageFromUri(uri: Uri) {
        submitBlockedReason()?.let {
            notify(it)
            return
        }

        val taskId = UUID.randomUUID().toString()
        viewModelScope.launch {
            // 先把状态推进「排队中」：点完立刻有反应，而不是等下一次轮询回来才动。
            // **不要先 cleared()** —— `updated()` 靠「当前是不是 Done」把上一轮的结果挪进
            // `shownSessionId`，那正是「上一轮结果还在页面上 + 顶部浮层」的来处。
            _state.update {
                it.copy(task = it.task.updated(TaskUpdate(taskId, TaskStatus.Queued, null, 1, hostInitiated = false)))
            }

            val jpeg = withContext(Dispatchers.IO) {
                runCatching { ImageCompress.jpegFromUri(appContext, uri) }.getOrNull()
            }
            if (jpeg == null) {
                failSubmit(taskId, "打开相册失败：读不出这张图（可改用悬浮球截屏）")
                return@launch
            }
            submitJpegs(taskId, listOf(jpeg))
        }
    }

    /**
     * 截屏那条路（规格 §3.2）：JPEG 字节**已经在手**（[com.quizsync.android.dev.capture.CaptureService]
     * 取的帧、已经按 1600/q80 压好），直接进与相册同一条上传 → 建任务 → 轮询。
     *
     * 与 [submitImageFromUri] 的差别只有「字节从哪来」—— 所以两条路共用一个 `submitJpegs`，
     * **不复制第二份上传/建任务逻辑**（复制出来的那份早晚会和主路径漂开）。
     */
    fun submitImageBytes(jpeg: ByteArray) {
        submitBlockedReason()?.let {
            speak(it)
            return
        }
        viewModelScope.launch { submitJpegs(UUID.randomUUID().toString(), listOf(jpeg)) }
    }

    /**
     * 三条前置校验（文案逐字照规格）：未配对 / 识别模块关 / 主机未选合集。
     * 返回 null = 可以提交。
     */
    private fun submitBlockedReason(): String? {
        val current = _state.value
        if (api == null || !current.paired) return "请先与电脑配对"
        if (!current.settings.recognitionEnabled) return "识别模块已关闭：请先在「设置 → 识别模块」打开"
        if (!current.host.hasActiveCollection) return "请先在电脑上选择任务合集"
        return null
    }

    /**
     * 共同部分：排队中 → 逐页上传 → 建任务 → 立刻踢一次轮询。
     *
     * 相册单图、截屏单图、多页收尾三条路都汇到这里 —— 上传失败的文案、
     * `no_active_collection` 的处理、同图复用后的那次踢轮询，全都是同一套。
     */
    private suspend fun submitJpegs(taskId: String, pages: List<ByteArray>) {
        val client = api ?: run {
            failSubmit(taskId, "请先与电脑配对", pages.size)
            return
        }
        if (pages.isEmpty()) {
            failSubmit(taskId, "没有可提交的图片")
            return
        }
        val limit = pageLimit()
        if (pages.size > limit) {
            failSubmit(taskId, "一次最多 $limit 页，这次是 ${pages.size} 页", pages.size)
            return
        }

        _state.update {
            it.copy(task = it.task.updated(TaskUpdate(taskId, TaskStatus.Queued, null, pages.size, hostInitiated = false)))
        }

        val hashes = mutableListOf<String>()
        for (page in pages) {
            if (page.size > ImageCompress.MAX_BYTES) {
                failSubmit(taskId, "图片压缩后仍超过 2MB，换一张再试", pages.size)
                return
            }
            hashes += uploadPage(client, page, taskId, pages.size) ?: return
        }
        submitHashes(client, taskId, hashes)
    }

    /** 上传一页，返回主机给的 `image_hash`；失败**出声**并返回 null。 */
    private suspend fun uploadPage(
        client: HostApi,
        jpeg: ByteArray,
        taskId: String,
        pageCount: Int,
    ): String? {
        val uploaded = withContext(Dispatchers.IO) { runCatching { client.uploadImage(jpeg) }.getOrNull() }
        if (uploaded == null) {
            failSubmit(taskId, "上传失败：连不上电脑", pageCount)
            return null
        }
        if (uploaded.status !in 200..299) {
            failSubmit(taskId, uploadFailureText(uploaded), pageCount)
            return null
        }
        val hash = uploaded.string("image_hash")
        if (hash.isNullOrEmpty()) {
            failSubmit(taskId, "上传失败：主机没有返回 image_hash", pageCount)
            return null
        }
        return hash
    }

    /** `POST /tasks`：多页走 `image_hashes` 页序（协议 §3.3），成功与否都如实反映。 */
    private suspend fun submitHashes(client: HostApi, taskId: String, hashes: List<String>) {
        val created = withContext(Dispatchers.IO) {
            runCatching { client.createTask(taskId, hashes, deviceId) }.getOrNull()
        }
        if (created == null) {
            failSubmit(taskId, "提交失败：连不上电脑", hashes.size)
            return
        }
        val code = created.string("code")
        if (code == "no_active_collection") {
            failSubmit(taskId, "请先在电脑上选择任务合集", hashes.size)
            return
        }
        if (code != null && code != "already_paired") {
            failSubmit(taskId, created.string("message") ?: "提交失败（$code）", hashes.size)
            return
        }
        if (created.status !in 200..299) {
            failSubmit(taskId, created.string("message") ?: "提交失败：http_${created.status}", hashes.size)
            return
        }

        // 立刻踢一次轮询：同图复用时任务提交那一刻就已经是 done，等 1 秒会显得迟钝。
        pollActiveTask()
    }

    /** 上传失败的文案（协议 §3.5 的那些码里，手机上真会遇到的几个）。 */
    private fun uploadFailureText(response: HostResponse): String = when (response.status) {
        413 -> "图片太大（主机上限 2MB），换一张或先裁剪"
        429 -> "上传太频繁，稍等一会再试"
        401 -> "配对已失效，请重新配对"
        else -> response.string("message") ?: "上传失败：http_${response.status}"
    }

    /** 失败要**同时**落到状态与提示：状态让「当前任务」页显示失败卡，提示让用户当场看见原因。 */
    private fun failSubmit(taskId: String, message: String, pages: Int = 1) {
        _state.update {
            it.copy(task = it.task.updated(TaskUpdate(taskId, TaskStatus.Failed, null, pages, message, hostInitiated = false)))
        }
        speak(message)
    }

    /**
     * 出声：前台走 SnackBar（[notify]），**后台额外补一条系统 Toast**。
     *
     * 规格 §3.1「过程提示不排队」：悬浮球手势期间人**在别的应用里**，SnackBar 要等他切回来
     * 才看得见（那时已经是过期信息）。那一侧的反馈由系统 Toast 当场承担 ——
     * 「失败才出声」里的「出声」在后台就是这一条。
     */
    private fun speak(text: String) {
        notify(text)
        if (foreground) {
            return
        }
        runCatching {
            // Toast 要落在有 Looper 的线程上。调用点都在 viewModelScope（Main），
            // 万一日后从 IO 调进来，也宁可只留 SnackBar 而不是崩。
            if (Looper.myLooper() == Looper.getMainLooper()) {
                Toast.makeText(appContext, text, Toast.LENGTH_SHORT).show()
            }
        }
    }

    // ========================================================================
    // 配对（§2.1）
    // ========================================================================

    fun onPairLink(request: PairRequest) {
        if (_state.value.pairing.linkConsumed && _state.value.pairing.host == request.host) return
        _state.update {
            it.copy(
                routes = if (it.routes.any { r -> r is Route.Pairing }) it.routes else it.routes + Route.Pairing,
                // 方向来源必须**唯一**：任何改 `routes` 的地方都要顺手写 `lastNav`，
                // 否则界面上会残留上一次的方向（`PopGesture` 残留会让下一次转场被当成"已经播过动画"）。
                lastNav = NavDirection.Push,
                pairing = it.pairing.copy(
                    host = request.host,
                    code = request.code,
                    error = null,
                    linkConsumed = true,
                ),
            )
        }
        submitPair(request.host, request.code, fromLink = true)
    }

    fun openPairing() {
        _state.update {
            it.copy(
                routes = if (it.routes.any { r -> r is Route.Pairing }) it.routes else it.routes + Route.Pairing,
                lastNav = NavDirection.Push,
                pairing = it.pairing.copy(
                    host = it.pairedHost ?: it.pairing.host,
                    code = "",
                    status = null,
                    error = null,
                    linkConsumed = false,
                ),
            )
        }
    }

    fun setPairHost(value: String) = _state.update { it.copy(pairing = it.pairing.copy(host = value, error = null)) }

    fun setPairCode(value: String) = _state.update {
        it.copy(pairing = it.pairing.copy(code = value.filter { ch -> ch.isDigit() }.take(6), error = null))
    }

    fun clearPairError() = _state.update { it.copy(pairing = it.pairing.copy(error = null)) }

    /**
     * `粘贴配对链接`（§2.1）：剪贴板为空 / 不是配对链接两条文案逐字照规格。
     */
    fun pastePairLink(text: String?) {
        if (text.isNullOrBlank()) {
            _state.update {
                it.copy(pairing = it.pairing.copy(error = "剪贴板里没有内容：请先在电脑端设置页点「复制配对链接」"))
            }
            return
        }
        val parsed = PairUri.parse(text)
        if (parsed == null) {
            _state.update {
                it.copy(pairing = it.pairing.copy(error = "剪贴板内容不是配对链接（应以 quizsync://pair? 开头）"))
            }
            return
        }
        _state.update { it.copy(pairing = it.pairing.copy(host = parsed.host, code = parsed.code, error = null)) }
        submitPair(parsed.host, parsed.code, fromLink = true)
    }

    /**
     * 扫码解出的文本（规格 §2.1 方式一）：**合法就直接配对，非法就静默忽略**。
     *
     * 为什么不复用 [pastePairLink]：那条是剪贴板路径，遇到非配对链接要报「剪贴板内容不是配对链接…」；
     * 而扫码扫到别家的码是**日常**（书上的码、别的 App 的码），规格对它的要求是「非法则忽略」——
     * 不该因为扫错一张码，弹一条关于剪贴板的错误。
     */
    fun submitScannedLink(text: String) {
        val parsed = PairUri.parse(text) ?: return
        _state.update { it.copy(pairing = it.pairing.copy(host = parsed.host, code = parsed.code, error = null)) }
        submitPair(parsed.host, parsed.code, fromLink = true)
    }

    /** 手动「开始配对」：两条校验文案照规格。 */
    fun submitManualPair() {
        val pairingState = _state.value.pairing
        val address = HostAddress.normalize(pairingState.host)
        if (address == null) {
            _state.update { it.copy(pairing = it.pairing.copy(error = "请填写电脑的局域网地址")) }
            return
        }
        if (pairingState.code.length != 6) {
            _state.update { it.copy(pairing = it.pairing.copy(error = "配对码是 6 位数字，请核对")) }
            return
        }
        submitPair(pairingState.host, pairingState.code, fromLink = false)
    }

    private fun submitPair(rawHost: String, code: String, fromLink: Boolean) {
        val address = HostAddress.normalize(rawHost) ?: return
        _state.update {
            it.copy(
                pairing = it.pairing.copy(
                    busy = true,
                    error = null,
                    status = "正在连接 $address …",
                ),
            )
        }
        viewModelScope.launch {
            val outcome = withContext(Dispatchers.IO) {
                runCatching {
                    val client = HostApi(address)
                    // 先探活：拿到主机名后按规格显示第二段过程文案（§2.1）。
                    val info = runCatching { client.info() }.getOrNull()
                    val name = info?.string("device_name")
                    if (info != null && info.status in 200..299 && !name.isNullOrBlank()) {
                        _state.update {
                            it.copy(pairing = it.pairing.copy(status = "已找到电脑「$name」，正在配对…"))
                        }
                    }
                    val response = client.pair(code = code, deviceId = deviceId, deviceName = "安卓设备")
                    val token = response.string("token")
                    if ((response.status == 200 || response.status == 409) && token != null) {
                        val aiConfigured = info?.jsonObject()?.get("ai_configured")
                            ?.jsonPrimitive?.content?.toBoolean()
                        PairOutcome.Success(address, token, aiConfigured)
                    } else {
                        PairOutcome.Rejected(
                            code = response.string("code"),
                            status = response.status,
                            message = response.string("message"),
                            retryAfter = response.jsonObject()["retry_after_seconds"]
                                ?.jsonPrimitive?.content?.toIntOrNull(),
                        )
                    }
                }.getOrElse { error -> PairOutcome.Threw(error.message ?: error::class.simpleName ?: "未知错误") }
            }

            when (outcome) {
                is PairOutcome.Success -> {
                    pairing.save(outcome.host, outcome.token)
                    api = HostApi(outcome.host).apply { setToken(outcome.token) }
                    _state.update {
                        it.copy(
                            paired = true,
                            pairedHost = outcome.host,
                            // 配对成功切标签到 0（当前任务）（§2.1 交互表）。
                            tab = HomeTab.CurrentTask,
                            routes = it.routes.filterNot { route -> route is Route.Pairing },
                            lastNav = NavDirection.Pop,
                            pairing = PairingUiState(linkConsumed = fromLink),
                            host = it.host.copy(connection = HostConnection.Checking, deviceName = outcome.host),
                        )
                    }
                    if (outcome.aiConfigured == false) {
                        notify("已配对，但主机尚未配置 AI：请先在 Windows 端设置 API Key")
                    }
                    probeInfo()
                    pullOpsIfNeeded(force = true)
                }

                is PairOutcome.Rejected -> _state.update {
                    it.copy(pairing = it.pairing.copy(busy = false, status = null, error = rejectionText(outcome)))
                }

                is PairOutcome.Threw -> _state.update {
                    it.copy(
                        pairing = it.pairing.copy(
                            busy = false,
                            status = null,
                            error = "无法连接主机：${outcome.message}",
                        ),
                    )
                }
            }
        }
    }

    /** 配对失败的文案表（§2.1 交互表，逐字）。 */
    private fun rejectionText(outcome: PairOutcome.Rejected): String = when (outcome.code) {
        "invalid_code" -> "配对码错误，请核对 Windows 端显示的 6 位数字"
        "code_expired" -> "配对码已过期，请在 Windows 端点「刷新」后重试"
        "rate_limited" -> "尝试过于频繁，${outcome.retryAfter ?: 0} 秒后再试"
        "network_error", "timeout" ->
            "连不上这台电脑：确认手机与电脑在同一 Wi-Fi、防火墙放行该端口，且地址填写正确"

        else -> "配对失败：" + (outcome.message ?: outcome.code ?: "http_${outcome.status}")
    }

    /** 解除配对（§2.5）：清凭据 → 回配对页。 */
    fun unpair() {
        pairing.clear()
        api = null
        lastAppliedLamport = 0L
        _state.update {
            it.copy(
                paired = false,
                pairedHost = null,
                routes = emptyList(),
                lastNav = NavDirection.Pop,
                host = HostStatus(connection = HostConnection.Unpaired),
                task = ActiveTaskState(),
                result = ResultState(),
                pairing = PairingUiState(),
            )
        }
    }

    // ========================================================================
    // 合集（§2.3.1 的「选择合集」）
    // ========================================================================

    fun openCollectionPicker() {
        viewModelScope.launch {
            val client = api
            if (client == null) {
                notify("请先与电脑配对")
                return@launch
            }
            val response = withContext(Dispatchers.IO) { runCatching { client.collections() }.getOrNull() }
            val options = response?.jsonObject()?.get("collections")?.jsonArray?.mapNotNull { element ->
                val obj = element.jsonObject
                val id = obj["collection_id"]?.jsonPrimitive?.content ?: return@mapNotNull null
                CollectionOption(id, obj["name"]?.jsonPrimitive?.content ?: id)
            } ?: emptyList()
            if (options.isEmpty()) {
                notify("电脑上还没有合集：请先在 Windows 端新建一个任务合集")
                return@launch
            }
            _state.update { it.copy(collectionPicker = options) }
        }
    }

    fun dismissCollectionPicker() = _state.update { it.copy(collectionPicker = null) }

    fun selectCollection(option: CollectionOption) {
        _state.update { it.copy(collectionPicker = null) }
        viewModelScope.launch {
            val client = api ?: return@launch
            val response = withContext(Dispatchers.IO) {
                runCatching { client.selectCollection(option.collectionId) }.getOrNull()
            }
            if (response != null && response.status in 200..299) {
                _state.update {
                    it.copy(host = it.host.copy(collectionId = option.collectionId, collectionName = option.name))
                }
                notify("已把主机的当前合集切换为「${option.name}」")
            } else {
                notify("切换合集失败：" + (response?.string("message") ?: "无响应"))
            }
        }
    }

    // ========================================================================
    // 设置（§2.5 / §2.6）
    // ========================================================================

    fun setRecognitionEnabled(enabled: Boolean) {
        prefs.edit().putBoolean(KEY_RECOGNITION_ENABLED, enabled).apply()
        _state.update { it.copy(settings = it.settings.copy(recognitionEnabled = enabled)) }
        // 规格 §3.1：识别模块总开关关掉时悬浮球不显示 —— 截屏会话同样要一并结束。
        if (!enabled) {
            stopCaptureSession("识别模块已关闭")
        }
    }

    fun setThemeMode(mode: ThemeMode) {
        prefs.edit().putString(KEY_THEME_MODE, mode.name).apply()
        _state.update { it.copy(settings = it.settings.copy(themeMode = mode)) }
    }

    fun setSystemDark(dark: Boolean) = _state.update {
        it.copy(settings = it.settings.copy(systemDark = dark))
    }

    /** 动态取色开关（API 31+ 才有意义；低版本界面上不出现这一项）。 */
    fun setDynamicColor(enabled: Boolean) {
        prefs.edit().putBoolean(KEY_DYNAMIC_COLOR, enabled).apply()
        _state.update { it.copy(settings = it.settings.copy(dynamicColor = enabled)) }
    }

    fun setFontSize(size: Int) {
        val clamped = size.coerceIn(12, 32)
        prefs.edit().putInt(KEY_FONT_SIZE, clamped).apply()
        _state.update { it.copy(settings = it.settings.copy(fontSize = clamped)) }
    }

    fun bumpFontSize(delta: Int) = setFontSize(_state.value.settings.fontSize + delta)

    fun setMultiPageLimit(limit: Int) {
        val clamped = limit.coerceIn(1, HARD_MAX_PAGES_PER_TASK)
        prefs.edit().putInt(KEY_MULTIPAGE_LIMIT, clamped).apply()
        _state.update { it.copy(settings = it.settings.copy(multiPageLimit = clamped)) }
    }

    fun setCaptureMode(mode: CaptureMode) {
        prefs.edit().putString(KEY_CAPTURE_MODE, mode.wire).apply()
        _state.update { it.copy(settings = it.settings.copy(captureMode = mode)) }
    }

    fun setBallEnabled(enabled: Boolean) {
        prefs.edit().putBoolean(KEY_BALL_ENABLED, enabled).apply()
        _state.update { it.copy(settings = it.settings.copy(ballEnabled = enabled)) }
        // 规格 §3.1：关掉悬浮球 = 截屏会话一并结束（球是截屏的唯一入口）。
        if (!enabled) {
            stopCaptureSession("悬浮球已关闭")
        }
    }

    fun setBallOpacity(value: Float) {
        prefs.edit().putFloat(KEY_BALL_OPACITY, value).apply()
        _state.update { it.copy(settings = it.settings.copy(ballOpacity = value)) }
    }

    fun setBallSize(dp: Int) {
        prefs.edit().putInt(KEY_BALL_SIZE_DP, dp).apply()
        _state.update { it.copy(settings = it.settings.copy(ballSizeDp = dp)) }
    }

    /** 回读真实权限（§5.3：回本页自动刷新）。 */
    fun refreshPermissions() {
        val snapshot = PermissionSnapshot(
            notifications = runCatching {
                NotificationManagerCompat.from(appContext).areNotificationsEnabled()
            }.getOrNull(),
            overlay = runCatching { Settings.canDrawOverlays(appContext) }.getOrNull(),
            batteryWhitelist = runCatching {
                val power = appContext.getSystemService(Context.POWER_SERVICE)
                (power as? PowerManager)?.isIgnoringBatteryOptimizations(appContext.packageName) ?: false
            }.getOrNull(),
            // 无障碍服务真的开着没有（备选截屏路径的前提）：读服务实例，不猜。
            accessibility = AccessibilityCaptureService.isConnected(),
        )
        _state.update {
            it.copy(
                settings = it.settings.copy(permissions = snapshot),
                // 截屏会话是**进程内**的状态（服务是否还活着），不是系统权限：
                // 每次回读权限时顺手同步一次，界面上的「已授权 / 未授权」才是真的。
                capture = it.capture.copy(sessionReady = CaptureService.isReady()),
            )
        }
    }

    // ========================================================================
    // 导航与提示
    // ========================================================================

    fun selectTab(tab: HomeTab) = _state.update { it.copy(tab = tab) }

    fun goHome() = _state.update { it.copy(routes = emptyList(), lastNav = NavDirection.Pop) }

    /**
     * 出栈一层。**一次返回只退一级**（不跳级、不重建上一页）。
     *
     * @param fromGesture 这次出栈是不是**预测式返回手势**走完触发的。
     *   是的话视觉已经由手势播完了（栈顶页跟着手指到了屏外、上一级页已经露到位），
     *   界面必须走 [NavDirection.PopGesture]（零时长转场）而不是 [NavDirection.Pop] ——
     *   后者会再播一遍 300ms 的退出 + 进场，用户看到的就是"返回了两次"。
     */
    fun popRoute(fromGesture: Boolean = false) = _state.update {
        it.copy(
            routes = it.routes.dropLast(1),
            lastNav = if (fromGesture) NavDirection.PopGesture else NavDirection.Pop,
        )
    }

    private fun notify(text: String) {
        messageCounter += 1
        _state.update { it.copy(message = text, messageId = messageCounter) }
    }

    fun consumeMessage() = _state.update { it.copy(message = null) }

    /**
     * 公开的提示入口：界面发现「这条路还没接」时用它出声。
     * **宁可明说「还没做」，也不要静默什么都不发生** —— 静默会被当成按钮坏了。
     */
    fun note(text: String) = notify(text)

    /**
     * 悬浮球**单击** = 单图识别（多页收集中 = 结束多页并提交**已截的页**）—— 规格 §3.1。
     *
     * 「收尾的短按只提交已经截好的页，**不再补截一张**」（页数严格等于长按次数）：
     * 这是规格里写死的一条，与 Windows 端 F8「结束多页并上传已抓的图」同一个语义。
     */
    fun onBallTap() {
        viewModelScope.launch {
            runCapture { if (pendingPages.isEmpty()) captureSingle() else submitCollected() }
        }
    }

    /**
     * 悬浮球**长按 ≥500ms** = 截第 1 页并**立即上传**该页、进入多页模式；再长按就累加
     * （规格 §3.1：页 hash 记进本地暂存，最后那次短按才建任务）。
     */
    fun onBallLongPress() {
        viewModelScope.launch { runCapture { capturePage() } }
    }

    /**
     * 一次手势的完整处理（藏球 → 取帧 → 上传 / 建任务）。
     *
     * 这期间 `capture.busy = true`（球是「识别中」琥珀），而且**同一时间只允许一个** ——
     * 连点两下不该产生两个任务、也不该有两个协程同时去藏球。
     */
    private suspend fun runCapture(block: suspend () -> Unit) {
        if (captureBusy) {
            speak("上一次截屏还在处理，稍等一下")
            return
        }
        captureBusy = true
        _state.update { it.copy(capture = it.capture.copy(busy = true)) }
        try {
            block()
        } finally {
            captureBusy = false
            _state.update { it.copy(capture = it.capture.copy(busy = false)) }
        }
    }

    /**
     * 「授予截屏权限」入口（设置 → 识别模块设置 → 截屏方式）：
     * 界面看到 [CaptureState.authRequestId] 涨了就调系统的 `createScreenCaptureIntent()`。
     *
     * 系统授权框**只能在 Activity 里弹**（前台服务没有界面），所以这里只发信号。
     */
    fun requestCapturePermission() {
        if (CaptureService.isReady()) {
            speak("截屏会话正在运行，不用重复授权")
            return
        }
        speak("请在弹出的系统窗口里选「立即开始」，让本应用能截屏（只弹这一次）")
        _state.update { it.copy(capture = it.capture.copy(authRequestId = it.capture.authRequestId + 1)) }
    }

    /** 授权**通过**：起前台服务（复用同一个会话），就位后接着做用户刚才想做的那件事。 */
    fun onProjectionGranted(resultCode: Int, data: Intent) {
        CaptureService.start(appContext, resultCode, data)
        viewModelScope.launch {
            val ready = awaitSessionReady()
            _state.update { it.copy(capture = it.capture.copy(sessionReady = ready)) }
            if (!ready) {
                pendingCapture = null
                speak("截屏服务没起来：授权可能已失效，请再点一次悬浮球重新授权")
                return@launch
            }
            val pending = pendingCapture
            pendingCapture = null

            // 授权框属于**本应用的任务**：系统框一关，宿主 Activity 就被带回前台（真机实测：
            // 关掉授权框后 `mCurrentFocus` 就是 MainActivity）。这时候如果接着补拍那一张，
            // 拍到的就是**应用自己的界面** —— 传给 AI 的是一屏设置页，识别结果必然是垃圾。
            // 所以这种情况**不自动补拍**，如实告诉用户回到原来的画面再点一次球。
            if (pending != null && foreground) {
                speak("截屏权限已授予：回到你要拍的画面，再点一次悬浮球就会截屏")
                return@launch
            }

            when (pending) {
                PendingCapture.Single -> runCapture {
                    if (pendingPages.isEmpty()) captureSingle() else submitCollected()
                }

                PendingCapture.Page -> runCapture { capturePage() }
                // 在设置页手动授权的：到此为止（会话常驻，之后点球直接截）。
                null -> Unit
            }
        }
    }

    /** 用户**拒绝**授权（规格 §3.2：会话失效/没授权要提示并引导，不静默失败）。 */
    fun onProjectionDenied() {
        pendingCapture = null
        _state.update { it.copy(capture = it.capture.copy(sessionReady = false)) }
        speak("已拒绝截屏授权：悬浮球暂时不能截屏；可在「设置 → 识别模块设置 → 截屏方式」里重新授予")
    }

    /**
     * 服务是 `startForegroundService` 起的、会话在 `onStartCommand` 里建 —— 就位是**异步**的。
     * 等它一下（最多 [SESSION_READY_TIMEOUT_MS]），别在没就位时就去取帧。
     */
    private suspend fun awaitSessionReady(): Boolean =
        withTimeoutOrNull(SESSION_READY_TIMEOUT_MS) {
            while (!CaptureService.isReady()) {
                delay(50)
            }
            true
        } ?: false

    // ========================================================================
    // 取帧（规格 §3.1 的手势 → §3.2 的会话）
    // ========================================================================

    /** 单图：取一帧 → 走 [submitImageBytes]。 */
    private suspend fun captureSingle() {
        val jpeg = grabFrame(PendingCapture.Single) ?: return
        Log.i(TAG, "单图截屏：${jpeg.size} 字节 → 提交识别")
        submitImageBytes(jpeg)
    }

    /** 多页：取一帧 → **立即上传**（页 hash 进暂存）→ 抓满上限自动收尾。 */
    private suspend fun capturePage() {
        val limit = pageLimit()
        if (pendingPages.size >= limit) {
            submitCollected()
            return
        }
        val jpeg = grabFrame(PendingCapture.Page) ?: return

        // 这一轮的任务 id 在第一次长按时定下来：中途的失败提示与最后的建任务用同一个，
        // 「当前任务」页上不会出现两个不同 id 的失败卡。
        val taskId = collectionTaskId ?: UUID.randomUUID().toString().also { collectionTaskId = it }
        val client = api ?: run {
            failSubmit(taskId, "请先与电脑配对", pendingPages.size + 1)
            return
        }
        val hash = uploadPage(client, jpeg, taskId, pendingPages.size + 1) ?: return
        pendingPages += hash
        _state.update { it.copy(capture = it.capture.copy(pages = pendingPages.size)) }
        Log.i(TAG, "多页累加：第 ${pendingPages.size}/$limit 页已上传（hash=${hash.take(12)}…，${jpeg.size} 字节）")

        if (pendingPages.size >= limit) {
            // 规格 §3.1：到达上限时提示「已达上限」并**自动**创建多页任务、退出多页模式。
            speak("已达上限 $limit 页，自动提交这一轮的识别")
            submitCollected()
        }
    }

    /** 收尾：只提交已经截好的页（**不再补截一张**），回到待识别。 */
    private suspend fun submitCollected() {
        if (pendingPages.isEmpty()) {
            return
        }
        val hashes = pendingPages.toList()
        val taskId = collectionTaskId ?: UUID.randomUUID().toString()
        pendingPages.clear()
        collectionTaskId = null
        _state.update { it.copy(capture = it.capture.copy(pages = 0)) }

        val client = api ?: run {
            failSubmit(taskId, "请先与电脑配对", hashes.size)
            return
        }
        Log.i(TAG, "多页收尾：提交 ${hashes.size} 页")
        submitHashes(client, taskId, hashes)
    }

    /**
     * 取一帧：**前置条件 → 授权 → 藏球 → 取帧 → 恢复球**（规格 §3.1）。
     *
     * 返回 null = 这次没取到，而且**已经出声**（没配对 / 没授权 / 会话失效 / 取帧失败）。
     *
     * 藏球的两种手段只能二选一（[CaptureOverlay] 的注释里写了为什么）：
     * 藏成功了帧里根本没有球；藏不成功才把球当时的矩形交给服务涂白。
     */
    private suspend fun grabFrame(kind: PendingCapture): ByteArray? {
        submitBlockedReason()?.let {
            speak(it)
            return null
        }

        // 采集方式决定这次走哪条路（规格 §3.2）。判据是纯函数，有单测。
        val mode = _state.value.settings.captureMode
        val accessibility = AccessibilityCaptureService.isConnected()
        val path = qsCapturePath(mode, accessibility)

        if (path == CapturePath.Projection && mode == CaptureMode.Accessibility) {
            // 选了备选路径却要走主路径 —— **必须说清为什么**（版本 / 没开服务），否则用户以为设置没生效。
            qsCapturePathNote(mode, accessibility, AccessibilityCaptureService.isSupported())?.let {
                Log.i(TAG, "采集路径回落：$it")
                speak(it)
            }
        }

        if (path == CapturePath.Projection && !CaptureService.isReady()) {
            // 记下「用户本来想干什么」：授权框回来之后接着做，不用让他再点一次球。
            pendingCapture = kind
            _state.update { it.copy(capture = it.capture.copy(sessionReady = false)) }
            requestCapturePermission()
            return null
        }

        // busy 由 runCapture 管（从手势开始到上传结束都是「识别中」），这里只管藏/取/恢复。
        val rect = withContext(Dispatchers.Main) { overlay?.visibleRect() }
        val hidden = withContext(Dispatchers.Main) { overlay?.hideForCapture() ?: false }
        try {
            if (hidden) {
                // 窗口移除之后系统还要重新合成一帧，立刻取会拿到「球还在里面」的旧帧。
                delay(BALL_HIDE_DELAY_MS)
            }

            if (path == CapturePath.Accessibility) {
                val jpeg = withContext(Dispatchers.IO) { AccessibilityCaptureService.capture() }
                if (jpeg != null) {
                    Log.i(TAG, "取帧成功（无障碍）：${jpeg.size} 字节（藏球=$hidden）")
                    return jpeg
                }

                // 备选路径没取到 → 落回主路径重试一次，并说明（不静默换路）。
                speak("无障碍截屏没取到画面，改用屏幕录制")
                if (!CaptureService.isReady()) {
                    pendingCapture = kind
                    _state.update { it.copy(capture = it.capture.copy(sessionReady = false)) }
                    requestCapturePermission()
                    return null
                }
            }

            val jpeg = withContext(Dispatchers.IO) {
                // 球矩形**总是**传：藏球失败、或系统没给新帧（只能退回旧帧）时靠它涂白；
                // 藏成功且拿到新帧时服务不会涂（涂了反而会擦掉题图那一块）。
                CaptureService.capture(ballRect = rect, ballHidden = hidden)
            }
            if (jpeg == null) {
                val lost = CaptureService.sessionLost
                _state.update { it.copy(capture = it.capture.copy(sessionReady = !lost)) }
                speak(if (lost) "截屏会话已失效：请重新授予截屏权限" else "截屏失败：没取到画面（该应用可能禁止截屏）")
                return null
            }
            Log.i(TAG, "取帧成功：${jpeg.size} 字节（藏球=$hidden，球矩形=${rect ?: "球当时不可见"}）")
            return jpeg
        } finally {
            // 无论成功失败都要把球放回来（原地），否则用户会觉得球被截没了。
            withContext(Dispatchers.Main) { overlay?.restoreAfterCapture() }
        }
    }

    /** 页数上限取设置（1..6），与主机侧硬上限取小。 */
    private fun pageLimit(): Int = _state.value.settings.multiPageLimit.coerceIn(1, HARD_MAX_PAGES_PER_TASK)

    /**
     * 规格 §3.1：**关掉悬浮球（或识别模块总开关）= 截屏会话一并结束**。
     *
     * 球是截屏的唯一入口，没有入口就不该继续共享屏幕（用户能看到系统那条「正在共享」提示）；
     * 下次要用时 `isReady()` 会报不可用 → 重新弹一次系统授权框（与其他会话失效路径一致）。
     */
    private fun stopCaptureSession(reason: String) {
        CaptureService.stop(appContext)
        pendingPages.clear()
        collectionTaskId = null
        pendingCapture = null
        _state.update {
            it.copy(capture = it.capture.copy(busy = false, pages = 0, sessionReady = false))
        }
        Log.i(TAG, "截屏会话已结束：$reason")
    }

    /** 失败卡「知道了」/ 完成卡「忽略」→ 掉状态（§2.3.2）。 */
    fun dismissTask() = _state.update { it.copy(task = it.task.cleared()) }

    fun appVersionLabel(): String = appVersion

    private sealed interface PairOutcome {
        data class Success(val host: String, val token: String, val aiConfigured: Boolean?) : PairOutcome

        data class Rejected(
            val code: String?,
            val status: Int,
            val message: String?,
            val retryAfter: Int?,
        ) : PairOutcome

        data class Threw(val message: String) : PairOutcome
    }

    class Factory(
        private val context: Context,
        private val store: RecordStore,
        private val pairing: PairStore,
        private val prefs: SharedPreferences,
        private val deviceId: String,
        private val appVersion: String,
    ) : ViewModelProvider.Factory {
        @Suppress("UNCHECKED_CAST")
        override fun <T : ViewModel> create(modelClass: Class<T>): T =
            QuizViewModel(context, store, pairing, prefs, deviceId, appVersion) as T
    }

    companion object {
        /** 自动跳结果页的延迟（current_task_page.dart:31）。 */
        const val AUTO_OPEN_DELAY_MS = 400L

        /** 主机侧页数硬上限（core-pkg/util/keys.dart:22）。 */
        const val HARD_MAX_PAGES_PER_TASK = 6

        /**
         * 藏球之后、取帧之前的等待（规格 §3.1：约 250ms）。
         * 窗口从 WindowManager 移除 ≠ 下一帧的合成里就没有它了，直接取会拍到球。
         */
        private const val BALL_HIDE_DELAY_MS = 250L

        /** 等截屏服务把会话建好（`startForegroundService` → `onStartCommand` 是异步的）。 */
        private const val SESSION_READY_TIMEOUT_MS = 2500L

        /**
         * 手动刷新里**联网那一段**的上限（见 `refreshAll` 的注释）。
         * 12 秒够主机正常回话（实测正常一轮 1–3 秒），又短到用户不会以为「卡死了」。
         */
        private const val NetworkPhaseTimeoutMs = 12_000L

        /** 日志标签：截屏链路的每一步都要能对上（验收时按它捞 logcat）。 */
        private const val TAG = "QuizSyncCapture"

        private const val KEY_RECOGNITION_ENABLED = "recognition_enabled"
        private const val KEY_THEME_MODE = "theme_mode"
    private const val KEY_DYNAMIC_COLOR = "dynamic_color"
        private const val KEY_FONT_SIZE = "font_size"
        private const val KEY_MULTIPAGE_LIMIT = "multipage_limit"
        private const val KEY_CAPTURE_MODE = "capture_mode"
        private const val KEY_BALL_ENABLED = "ball_enabled"
        private const val KEY_BALL_OPACITY = "ball_opacity"
        private const val KEY_BALL_SIZE_DP = "ball_size_dp"
    }
    /**
     * 删一条识别记录（规格 §2.4.5）：落库（含墓碑 op）后刷新历史。
     *
     * 刷新是必须的 —— 分组计数要跟着减，用户看到的才是真数。
     */
    fun deleteSession(sessionId: String) {
        viewModelScope.launch {
            val ok = withContext(dbDispatcher) {
                runCatching { store.deleteSession(sessionId) }.getOrElse { false }
            }
            if (ok) {
                notify("已删除这条识别记录")
                // 删完立刻推：文案承诺的是「本机与电脑上的这条记录都会被删除」。
                pushLocalOps()
                refreshAll(manual = false)
                // 停在合集详情页时还要重载那一页 —— `refreshAll` 刷的是主壳那套状态，
                // 不重载的话库已经是 4 条、界面上还挂着 6 条（**库对界面旧**）。
                (_state.value.routes.lastOrNull() as? Route.CollectionDetail)?.let { route ->
                    loadDetail(route.collectionId, route.title)
                }
            } else {
                notify("删除失败，记录还在")
            }
        }
    }
    /**
     * 批量删合集（规格 §3.4「长按合集分组进入多选模式」）。
     *
     * **不级联删记录**（M18 口径）：记录还在，删完在「未分类」里能看到 ——
     * 这正是 `RecordStore.groups()` 把墓碑合集的记录归到未分类的原因。
     * 刷新是必须的：分组列表与计数都要跟着变，否则界面上那个合集还在。
     */
    fun deleteCollections(collectionIds: List<String>) {
        if (collectionIds.isEmpty()) return

        viewModelScope.launch {
            val removed = withContext(dbDispatcher) {
                collectionIds.count { id ->
                    runCatching { store.deleteCollection(id) }.getOrElse { false }
                }
            }

            when {
                removed > 0 -> {
                    notify("已删除 $removed 个合集（里面的识别记录保留，移到「未分类」）")
                    // 删完立刻推（与删记录同一条路径）：确认框里写的是「软删除并同步到电脑」。
                    pushLocalOps()
                    refreshAll(manual = false)
                }
                // 一个都没删掉：说明这些合集早就被删了（或被主机删了）—— 如实说，不假装成功。
                else -> notify("没有合集被删除（可能已经在电脑上删掉了）")
            }
        }
    }
    /**
     * 造几条测试记录（设置页的「测试数据」用）。造完刷一遍界面，用户当场就能看到。
     */
    fun seedTestData(count: Int = 5) {
        viewModelScope.launch {
            val inserted = withContext(dbDispatcher) {
                runCatching { store.seedTestSessions(count) }.getOrElse { -1 }
            }
            if (inserted > 0) {
                notify("已生成 $inserted 条测试记录")
                refreshAll(manual = false)
            } else {
                notify("生成测试记录失败")
            }
        }
    }
}
