package com.quizsync.android.dev

import android.Manifest
import android.app.Activity
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.media.projection.MediaProjectionConfig
import android.media.projection.MediaProjectionManager
import android.os.Build
import android.os.Bundle
import android.util.Log
import androidx.activity.ComponentActivity
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.SystemBarStyle
import androidx.activity.result.contract.ActivityResultContracts
import androidx.core.content.ContextCompat
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.border
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.AssistChip
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.activity.compose.PredictiveBackHandler
import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.FastOutSlowInEasing
import androidx.compose.animation.core.tween
import androidx.compose.ui.draw.clip
import kotlinx.coroutines.CancellationException
import androidx.compose.foundation.background
import androidx.compose.ui.graphics.Color
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LifecycleEventEffect
import com.quizsync.android.core.db.QuizSyncDatabase
import com.quizsync.android.dev.ball.BallState
import com.quizsync.android.dev.ball.FloatingBallController
import com.quizsync.android.dev.state.QuizUiState
import com.quizsync.android.dev.state.QuizViewModel
import com.quizsync.android.dev.state.Route
import com.quizsync.android.dev.state.refreshing
import com.quizsync.android.dev.ui.CollectionDetailScreen
import com.quizsync.android.dev.ui.HomeShell
import com.quizsync.android.dev.ui.QuizRouteStack
import com.quizsync.android.dev.ui.RecognitionSettingsScreen
import com.quizsync.android.dev.ui.ResultScreen
import com.quizsync.android.dev.ui.designsystem.qsIsFling
import com.quizsync.android.dev.ui.theme.QuizSyncTheme
import java.io.File
import java.util.UUID

/**
 * 日志 TAG（与 `CaptureService` 同一套写法）。返回手势的**速度判定**只打这一行 ——
 * 阈值 `kFlingRatePerSecond` 是拿真机数据调的，下次要动它先看：
 * `adb logcat -s QuizSync` 里的「返回手势判定=甩/慢拖 … 速率/s=…」。
 */
private const val TAG = "QuizSync"

/**
 * 原生 2.0 的 Android 首屏。
 *
 * 第五轮纠正（用户当场质问「我应用 1.1.0 原来是浅绿的，你用紫色干什么」）：
 * 上一版所有样式取值都是我**凭空发明**的（品牌紫 #63519F、按钮胶囊、标签圆角矩形、
 * 描边 #78747D），而且方向正好修反 —— 老应用是**按钮圆角 10 / 标签 999 胶囊**。
 *
 * 现在一律走 `QsTokens`：值由 `tools/gen_design_tokens.py` 从 `design/tokens.json` 生成，
 * 而 `design/tokens.json` 的值又被 `packages/quizsync_ui/test/design_token_parity_test.dart`
 * 用真 Flutter 钉在老应用的视觉契约上（凭空写一个值就会红）。
 *
 * 结构也照老应用：页面底色用 canvas（#F3F5F4），内容放进白卡片 —— 不再整屏铺白。
 */
class MainActivity : ComponentActivity() {
    /// 深链进来的配对请求（扫码 / 点链接）。onNewIntent 时也要能更新，所以放在字段上。
    private var pendingPair by mutableStateOf<PairRequest?>(null)

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        pendingPair = PairUri.parse(intent?.dataString)
        val schema = readSchemaAsset()
        val deviceId = deviceId()
        val store = PairStore(this)
        // 记录存储要在整个 Activity 生命周期里用（拉取 → 落库 → 读页面），所以建在这里而不是组合里。
        val records = RecordStore(this, schema, deviceId)
        setContent {
            // 新架构：一个 ViewModel 持有全部状态，界面只读 `ui`、只发意图。
            // 用 remember 而不是 viewModel()：这个 app 只有一个 Activity、一个壳，
            // 少了 factory 那一层；真要跨配置变更保活再换。
            val viewModel = remember {
                QuizViewModel(
                    context = applicationContext,
                    store = records,
                    pairing = store,
                    prefs = getSharedPreferences("quizsync", MODE_PRIVATE),
                    deviceId = deviceId,
                    appVersion = packageManager.getPackageInfo(packageName, 0).versionName ?: "",
                )
            }
            val ui by viewModel.state.collectAsState()

            // 明暗由**设置里的选择**决定，不是硬编码跟随系统。
            // `ThemeMode.System` 时才看系统状态，而系统状态要喂给 ViewModel
            // （SettingsState.dark 是 themeMode + systemDark 算出来的）。
            // 之前这里直接读 isSystemInDarkTheme()，于是设置页选「深色 / 浅色」**完全没反应**。
            val systemDark = isSystemInDarkTheme()
            LaunchedEffect(systemDark) { viewModel.setSystemDark(systemDark) }

            // ================================================================
            // 系统栏跟随**应用**主题（用户报过三次的那一条）
            // ----------------------------------------------------------------
            // `enableEdgeToEdge()` 在 onCreate 里已经调过一次，但它判定明暗用的是
            // **系统**的 uiMode；而本应用的明暗是「设置 → 外观 → 主题」三态算出来的
            // （`ThemeMode.System` 时才跟随系统）。于是「系统浅色 + 应用选深色」时
            // 状态栏图标一直是深色 —— 深色导航栏上什么都看不见。
            //
            // 这里在明暗变化时**重贴一次**：`SystemBarStyle.auto(light, dark) { 判定 }` 的
            // 判定块直接给应用自己的 dark，图标明暗就跟着应用走了。
            // 状态栏底色一律透明（内容从它下面穿过去，由各页的 TopAppBar 铺底色），
            // 导航栏**不动** —— 保留 `enableEdgeToEdge()` 的默认行为，三键导航下更安全。
            // ================================================================
            val appDark = ui.settings.dark
            LaunchedEffect(appDark) {
                enableEdgeToEdge(
                    statusBarStyle = SystemBarStyle.auto(
                        android.graphics.Color.TRANSPARENT,
                        android.graphics.Color.TRANSPARENT,
                    ) { appDark },
                )
            }

            // 数据加载入口。**这一句原来漏了** —— 状态层全都建好了（HistoryState / ResultState / …），
            // 但没有任何地方调 start()，于是 history.loading 永远停在 true：
            // 历史 tab 只有一个转圈、当前任务页永远显示「未配对」。
            // 界面「什么都没显示」时，先怀疑「没人去取数据」，别急着怀疑渲染。
            //
            // ✅ **深链现在交给 ViewModel**（`start(pendingPair)` → `onPairLink` → `submitPair`）。
            // 这里原来是 `start(null)`，为的是躲开一个真机抓到的竞态：ViewModel 的 `onPairLink`
            // 与旧 `PairScreen` 自己的 `LaunchedEffect(prefill) → submit()` **各发一次**
            // `POST /api/v1/pair`，而协议里重复配对是「签发新令牌并作废旧令牌」→ 两个请求互相作废，
            // 屏幕上写「配对成功」、之后每个请求 401（表现成永远「电脑未连接」）。
            // 那个竞态的前提是「旧 PairScreen 自己会提交」—— 配对页按规格重做之后，
            // 页面已经是**纯视图**（不再自己发请求），所以按 `DECISIONS.md` 里
            // 「配对只能有一条路径」的口径，这条路径现在是 ViewModel 那一套。
            LaunchedEffect(Unit) { viewModel.start(pendingPair) }

            // 前台/后台。**这一条原来也漏了**（真机验收时才抓到）：`setForeground()` 定义了
            // 却没有任何调用点，于是 `foreground` 恒为 false →
            //   ① 1 秒轮询 `/tasks/active` 永远不启动（手机看不到任何进度）；
            //   ② `scheduleAutoOpen` 第一句就是 `if (!foreground) return`，自动进结果页永远不触发。
            // 这一条与上面那句是同一个模式：状态层齐了，没人去拨那个开关。
            // 用 `LifecycleEventEffect` 而不是自己加 observer：它就是为这件事准备的，
            // 且**跟随宿主的生命周期**自动注销，不会泄漏。
            LifecycleEventEffect(Lifecycle.Event.ON_START) { viewModel.setForeground(true) }
            LifecycleEventEffect(Lifecycle.Event.ON_STOP) { viewModel.setForeground(false) }

            // Android 13+ 的常驻通知要 POST_NOTIFICATIONS。**在启动时**要一次，
            // 不要在截屏那一刻要：那时用户多半停在别的应用里，系统框会打断他、关掉时还会把
            // 本应用带回前台（真机实测：MIUI 的权限框属于本应用的任务）—— 与「全程静默」冲突。
            // **被拒也照跑**（规格 §3.2）：通知画不出来而已，前台服务不受影响。
            val notificationLauncher = rememberLauncherForActivityResult(
                ActivityResultContracts.RequestPermission(),
            ) { granted ->
                if (!granted) {
                    viewModel.note("没有通知权限：截屏服务照常运行，只是常驻通知不显示")
                }
            }
            LaunchedEffect(Unit) {
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
                    ContextCompat.checkSelfPermission(
                        this@MainActivity,
                        Manifest.permission.POST_NOTIFICATIONS,
                    ) != PackageManager.PERMISSION_GRANTED
                ) {
                    notificationLauncher.launch(Manifest.permission.POST_NOTIFICATIONS)
                }
            }

            // ================================================================
            // 截屏授权（规格 §3.2）：系统那条 `createScreenCaptureIntent()` 的框
            // **只能在 Activity 里弹** —— 前台服务没有界面。ViewModel 只发信号
            // （`capture.authRequestId` 涨一格 = 弹一次），结果再交回 ViewModel。
            // ================================================================
            val projectionLauncher = rememberLauncherForActivityResult(
                ActivityResultContracts.StartActivityForResult(),
            ) { result ->
                val data = result.data
                if (result.resultCode == Activity.RESULT_OK && data != null) {
                    viewModel.onProjectionGranted(result.resultCode, data)
                } else {
                    // 用户在系统框上点了「取消」：**不静默**（§3.2「会话失效不静默失败」）。
                    viewModel.onProjectionDenied()
                }
            }
            LaunchedEffect(ui.capture.authRequestId) {
                if (ui.capture.authRequestId <= 0) return@LaunchedEffect
                val manager = getSystemService(Context.MEDIA_PROJECTION_SERVICE) as MediaProjectionManager
                // Android 14+ 的系统授权框默认是**「单个应用」**（验收机实测：不改就是单应用，
                // 抓到的只有被选中的那一个应用，而且系统还会再让你选一次应用）。
                // 这条链路（悬浮球截屏 → 整屏）永远要**整个屏幕**，所以显式给
                // `createConfigForDefaultDisplay()`，把默认值钉死在整屏上。
                // API 34 以下没有这个重载，退回无参版本。
                val consent = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
                    manager.createScreenCaptureIntent(MediaProjectionConfig.createConfigForDefaultDisplay())
                } else {
                    manager.createScreenCaptureIntent()
                }
                projectionLauncher.launch(consent)
            }

            // 两套配色都来自同一份 token（与 Windows 的 ThemeDictionaries 同源）。
            //
            // **P4 修正**：这里原来是直接 `MaterialTheme(colorScheme = …)`，把 `QuizSyncTheme`
            // 整个绕过去了 —— 于是主题层里的 `quizShapes()`（卡片圆角走 Shapes.medium）与
            // `quizTypography()` 从来没生效过，`LocalQuizPalette` 也没提供（各页读到的永远是浅色
            // 默认那一版固定色值）。现在统一走 `QuizSyncTheme`。
            QuizSyncTheme(
                dark = ui.settings.dark,
                // 注意是**反向**参数（照 nowinandroid 的语义，见 QuizSyncTheme 的注释）：
                // 设置里存的是「用户要不要动态取色」，这里取反传。
                disableDynamicTheming = !ui.settings.dynamicColor,
            ) {
                // 悬浮球（规格 §3.1）：开关跟着设置走；没有悬浮窗权限就显示不出来，
                // 那种情况**不静默** —— 设置页里给了一个「去授权」的入口。
                //
                // 球同时是**截屏要藏起来的那个浮层**（`CaptureOverlay`）：取帧前藏、截完原地恢复，
                // 否则球会被拍进题图里（AI 会把它当成题干的一部分）。
                val ball = remember {
                    FloatingBallController(
                        context = applicationContext,
                        onTap = { viewModel.onBallTap() },
                        onLongPress = { viewModel.onBallLongPress() },
                    )
                }
                LaunchedEffect(ball) { viewModel.attachCaptureOverlay(ball) }

                // 识别模块关闭时球不显示（规格 §3.1）—— 没有入口就不该留着一个球。
                val ballVisible = ui.settings.ballEnabled && ui.settings.recognitionEnabled
                // 三态：多页收集中（琥珀 + 页数角标）> 识别中（琥珀）> 待识别（绿）。
                val ballState = when {
                    ui.capture.pages > 0 -> BallState.MultiPage
                    ui.capture.busy || ui.task.running -> BallState.Busy
                    else -> BallState.Idle
                }
                LaunchedEffect(
                    ballVisible,
                    ui.settings.ballSizeDp,
                    ui.settings.ballOpacity,
                    ballState,
                    ui.capture.pages,
                ) {
                    if (ballVisible) {
                        ball.show(
                            sizeDp = ui.settings.ballSizeDp,
                            opacity = ui.settings.ballOpacity,
                            state = ballState,
                            pages = ui.capture.pages,
                        )
                    } else {
                        ball.hide()
                    }
                }
                DisposableEffect(Unit) { onDispose { ball.hide() } }

                Surface(
                    modifier = Modifier.fillMaxSize(),
                    color = MaterialTheme.colorScheme.background,
                ) {

                    // 深链进来就直接落到配对页并自动提交，用户不用手输地址与码。
                    var showPair by remember { mutableStateOf(false) }
                    LaunchedEffect(pendingPair) {
                        if (pendingPair != null) showPair = true
                    }

                    // 配对成功 = ViewModel 把 `busy` 从真放回假、且没有错误（它同时会摘掉 `Route.Pairing`）。
                    // **判据用「busy 由真变假」而不是「路由没了」**：深链进来的那一帧里路由可能还没加上，
                    // 看路由会把它误判成「已经配完了」。
                    var wasPairing by remember { mutableStateOf(false) }
                    LaunchedEffect(ui.pairing.busy) {
                        if (ui.pairing.busy) {
                            wasPairing = true
                        } else if (wasPairing) {
                            wasPairing = false
                            if (ui.pairing.error == null) {
                                pendingPair = null
                                showPair = false
                                viewModel.refreshAll()
                            }
                        }
                    }

                    if (showPair) {
                        // 配对页也要接返回。**它原来是没有的** —— 在配对页按返回键会直接退出
                        // 整个应用（`showPair` 只是一个状态，系统返回不知道有这一层）。
                        PredictiveBackHandler(enabled = true) { progress ->
                            progress.collect { /* 手势走完才关，中途松手系统自动回弹 */ }
                            pendingPair = null
                            showPair = false
                            // 状态里的路由也要摘掉，否则 ViewModel 那边会留着一条已经不在屏幕上的压栈页。
                            viewModel.popRoute()
                        }

                        // 页面是**纯视图**：状态读 `ui.pairing`，动作走 ViewModel。
                        // 它自己**不发任何请求** —— 全应用只有 `submitPair` 一条配对路径。
                        PairScreen(
                            pairing = ui.pairing,
                            onHostChange = viewModel::setPairHost,
                            onCodeChange = viewModel::setPairCode,
                            onPasteLink = viewModel::pastePairLink,
                            onScanLink = viewModel::submitScannedLink,
                            onSubmit = viewModel::submitManualPair,
                            onBack = {
                                pendingPair = null
                                showPair = false
                                viewModel.popRoute()
                            },
                        )
                    }
                    else {
                        // 压在主壳之上的路由（§1.3 的 push 列表）。
                        //
                        // 结构要点（用户实测反馈过三轮）：
                        // ① **主壳常驻、压栈页叠在上面**，不再二选一 —— 返回手势里被压栈那页
                        //    退开时，露出来的是真正的下一层，而不是一片窗口底色；
                        // ② **手势要露出来的必须是「直接上一层」**：四级页拖动时看到三级，不是二级、
                        //    更不是一级。那一层原来根本没进组合（手写路由只画栈顶），这就是 bug ①；
                        //    现在由 `QuizRouteStack` 的第 ② 层渲染 `routes[size-2]` 解决；
                        // ③ 手势走完**直接提交**导航状态、不再重播一遍退场/进场（bug ②），
                        //    中途松手则从当前进度反向回弹。两件事都在下面这一段里。
                        val top = ui.routes.lastOrNull()
                        // 压栈了「还没实现」的页面时**出声**，不要静默什么都不发生。
                        // `Route.Result` 原来就在这张表里（结果页还没做，点一条记录毫无反应）——
                        // 结果页接进 AnimatedContent 之后那一条**必须删掉**，否则每次进结果页
                        // 都会弹一句「结果页还没做」，比不弹还糟。
                        LaunchedEffect(top) {
                            when (top) {
                                null, is Route.CollectionDetail, is Route.Result -> Unit
                                is Route.Pairing -> Unit
                                // 识别模块设置页已经接进 AnimatedContent，这里不再出声。
                                is Route.RecognitionSettings -> Unit
                            }
                        }

                        // ================================================================
                        // 预测式返回（用户 2026-09-28 给的口径）
                        // ----------------------------------------------------------------
                        // 它是**一次连续、可被手指控制、可暂停、可取消的单一转场**，不是
                        // 「拖动预览 + 松手后再播一遍普通返回动画」。三条规矩：
                        //  ① 手势期间**只改视觉**：不动路由栈（不提前 pop、不销毁页面、不重新进入上一页）；
                        //  ② 松手确认 → 直接提交导航状态，**不再播 exit / enter**（`NavDirection.PopGesture`）；
                        //  ③ 松手取消 → 从**当前进度**反向回到 0，不重建页面、不重播进场。
                        // 只有一个 `Animatable` 被手势进度驱动，没有需要同步的状态机。
                        // ================================================================
                        val backGesture = remember { Animatable(0f) }
                        // **「慢拖」才跟手**：它同时决定「直接上一层」那一层要不要进组合。
                        // 刻意用布尔而不是 `progress > 0`：后者每帧都变，会让整棵页面树每帧重组一次。
                        var gestureActive by remember { mutableStateOf(false) }
                        PredictiveBackHandler(enabled = top != null) { progress ->
                            // ================================================================
                            // 速度判据（用户 2026-09-28 追加：「滑动过快还是会掉帧，建议加入一小段
                            // 速度检测缓冲，过快就没有动画，缓慢就有」）
                            // ----------------------------------------------------------------
                            // 真机量到的事实：**一次「慢拖」返回要组合两页** —— 手势预览层组合落点页
                            // （46.5ms，其中录显示列表 35.2ms），提交时 `AnimatedContent` 又组合一次
                            // （50.6ms）。两次都落在画面上，就是用户看到的掉帧。
                            // 而**甩**根本不需要这两下：手指已经离开，没有"跟手"可言 ——
                            // 甩的时候一格动画都不给（不跟手、不预览、不转场），只付提交那一次组合。
                            //
                            //   * 甩（间隔速率 > `kFlingRatePerSecond`）→ 页面**不动**，直接切过去；
                            //   * 慢拖 → 跟手 + 预览层，与以前完全一样；
                            //   * 返回键 / 一步到位（给不出中间进度）→ 照旧播完整 300ms 转场。
                            //
                            // **判据只认相邻两帧的速率**，且**单向**：第一段间隔快就按甩处理，
                            // 但只要后面有一段间隔变慢（手指停下来慢慢拖），立刻转成慢拖。
                            // 反过来的误判（把慢拖当成甩）才不会发生 —— 误判成甩的话手指在拖、
                            // 页面一动不动，那是"看起来坏了"，比掉帧严重得多。
                            // ================================================================
                            var interactive = false    // 慢拖：跟手 + 预览层
                            var decided = false        // 判据已经下过结论（一次手势只判一次）
                            var sawProgress = false    // 收到过中间进度（= 这是手势返回，不是返回键）
                            var prevProgress = 0f
                            var prevNs = 0L
                            // 判定结果打一行日志：阈值（`kFlingRatePerSecond`）是拿真机数据调的，
                            // 下次要动这个值，先看这行日志里的实际速率（`adb logcat -s QuizSync`）。
                            fun noteVerdict(fling: Boolean, deltaP: Float, deltaMs: Float, value: Float) {
                                Log.d(
                                    TAG,
                                    "返回手势判定=" + (if (!fling) "慢拖" else "甩") +
                                        " 进度=" + value +
                                        " 本段Δ=" + deltaP +
                                        " 本段耗时ms=" + deltaMs +
                                        " 速率/s=" + (if (deltaMs > 0f) deltaP / (deltaMs / 1000f) else -1f),
                                )
                            }
                            // ⚠️ **一次手势只判一次**（在"第一段真的有位移"的间隔上）。
                            // 试过"后面有一段变慢就改判慢拖"，真机日志显示**甩出去的手势本身就会减速**
                            //（MIUI 的进度是带阻尼的：40ms 一屏的轻扫实测 3.9 → 3.2 → 2.4/s），
                            // 于是甩会在第 3 帧被改判成慢拖，预览层照样组合 —— 判据白做。
                            fun judge(deltaP: Float, deltaMs: Float, value: Float) {
                                decided = true
                                val fling = qsIsFling(deltaP, deltaMs)
                                noteVerdict(fling, deltaP, deltaMs, value)
                                if (!fling) {
                                    interactive = true
                                    gestureActive = true
                                }
                            }
                            try {
                                progress.collect { event ->
                                    val value = event.progress
                                    val now = System.nanoTime()
                                    if (value >= 0.999f) {
                                        // 提交/一步到位的那一个 1.0：不参与"改位置"，
                                        // 但它是一次有效的速度采样（单事件轻扫就靠它判）。
                                        if (!decided && sawProgress) {
                                            judge(1f - prevProgress, (now - prevNs) / 1_000_000f, value)
                                        }
                                        return@collect
                                    }
                                    if (sawProgress && !decided) {
                                        val deltaP = value - prevProgress
                                        // ⚠️ **只在进度真的动了的那一段判定**：真机日志显示手势开头连着
                                        // 两三个事件都是 `进度=0.0`（系统先发"起手"，手指还没进判定区）。
                                        // 拿 Δ=0 的那一段去算速率必然得 0 ⇒ 所有手势都会被判成"慢拖"，
                                        // 判据等于不存在（第一版就是这么写的，真机日志一眼看穿）。
                                        if (deltaP > 0f) {
                                            judge(deltaP, (now - prevNs) / 1_000_000f, value)
                                        }
                                    } else if (!sawProgress) {
                                        Log.d(TAG, "返回手势开始 首帧进度=" + value)
                                    }
                                    sawProgress = true
                                    prevProgress = value
                                    prevNs = now
                                    // 没跟手就不要动这个值：甩的时候页面必须**纹丝不动**，
                                    // 否则会露出下面不该露的那一层（用户以前报过的"看到一级"）。
                                    if (interactive) backGesture.snapTo(value)
                                }
                                // 收尾：慢拖（视觉已经演完）与甩（本来就没动）都**直接提交**，
                                // 界面不再播第二次退出动画；只有没收到过中间进度的返回（返回键 /
                                // 一步到位）才交给 `AnimatedContent` 播完整 300ms。
                                viewModel.popRoute(fromGesture = sawProgress)
                            } catch (cancelled: CancellationException) {
                                // 没到阈值 → 从当前进度连续反向回到 0（只有跟过手的才需要回弹）。
                                if (interactive) {
                                    backGesture.animateTo(
                                        targetValue = 0f,
                                        animationSpec = tween(durationMillis = 180, easing = FastOutSlowInEasing),
                                    )
                                }
                                throw cancelled
                            } finally {
                                backGesture.snapTo(0f)
                                gestureActive = false
                            }
                        }

                        Box(modifier = Modifier.fillMaxSize()) {
                            // 共享元素（P5 的 `SharedTransitionLayout` + `Modifier.qsSharedElement`）**已删除**
                            //（用户口径 2026-09-28：「删除除了进入页面和返回页面外的所有动画」）——
                            // 它是"进出页面时额外多演的一段"（合集标题从列表行飞到详情页标题位），
                            // 不属于页面进/出本身。没有它，历史页与详情页各少一处作用域接线。
                            //
                            // 图层叠放与各自的位移规则全在 `ui/QuizRouteStack.kt`：
                            // ① 主壳（栈底，常驻）② 手势预览层 = **直接上一层** ③ 栈顶页。
                            // 「只能显示直接上一层」这条规格就落在第 ② 层的内容上。
                            QuizRouteStack(
                                routes = ui.routes,
                                lastNav = ui.lastNav,
                                gestureActive = gestureActive,
                                // 进度传**读取器**：它在 graphicsLayer 的绘制阶段被读，不进组合。
                                gestureProgress = { backGesture.value },
                                shell = {
                                    HomeShell(
                                        vm = viewModel,
                                        ui = ui,
                                        // 下拉指示器读的是**正在刷新**（`refreshAll()` 的标志，见
                                        // `QuizState.kt` 的 `QuizUiState.refreshing`），不是启动标志 ——
                                        // 读错的那一个会让指示器拉下来之后再也不会自己收起。
                                        refreshing = ui.refreshing,
                                        onOpenPairing = {
                                            // 两条都要：`openPairing()` 把路由压进状态并预填上次配对的地址，
                                            // `showPair` 才让配对页真的画出来（它在主壳之外，是整页替换）。
                                            viewModel.openPairing()
                                            showPair = true
                                        },
                                    )
                                },
                                routeContent = { route -> RouteContent(route, ui, viewModel) },
                            )
                        }
                    }
                }
            }
        }
    }

    /// 应用已经开着时再扫一次码 / 点一次链接，也要能接住。
    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        PairUri.parse(intent.dataString)?.let { pendingPair = it }
    }

    /// 设备标识：配对协议要求一个稳定的 `device_id`。它不是密钥，放普通 SharedPreferences 即可
    /// （配对 token 才是密钥，将来走 EncryptedSharedPreferences —— 见 NATIVE-2.0-PLAN Phase 3）。
    private fun deviceId(): String {
        val prefs = getSharedPreferences("quizsync", Context.MODE_PRIVATE)
        return prefs.getString("device_id", null) ?: UUID.randomUUID().toString().also {
            prefs.edit().putString("device_id", it).apply()
        }
    }

    /// 协议 schema 随 APK 走（assets）。真机第一次运行时抓到的教训：
    /// 内核原来靠「从工作目录往上找协议仓」拿 schema —— Android 上根本没有这个概念。
    private fun readSchemaAsset(): String =
        assets.open("quizsync/schema-v1.sql").bufferedReader().use { it.readText() }
}

/**
 * 一页路由的**内容**（本应用里真能被画出来的三种：合集详情 / 结果页 / 识别模块设置）。
 *
 * 位置与转场**不在这里** —— 那些由 [QuizRouteStack] 按"这一页在栈里的身份"负责
 * （在栈里 = 栈顶、刚出栈、还是正被手势露出来的上一层）。这里只管画什么，
 * 所以同一个函数既服务栈顶页，也服务手势预览层里那一页。
 *
 * 16dp 圆角保留在每一页自己身上：压栈页压在下一层上时，四角要能透出下面那一层
 * （这是这套"卡片栈"观感的一部分）。
 */
@Composable
private fun RouteContent(route: Route, ui: QuizUiState, viewModel: QuizViewModel) {
    if (route is Route.CollectionDetail) {
        Box(
            modifier = Modifier
                .fillMaxSize()
                .clip(RoundedCornerShape(16.dp)),
        ) {
            CollectionDetailScreen(
                title = route.title,
                history = ui.history,
                dark = ui.settings.dark,
                onBack = { viewModel.popRoute() },
                onOpenSession = { sessionId, collectionId ->
                    viewModel.openResult(sessionId, collectionId)
                },
                onDeleteSession = { viewModel.deleteSession(it) },
                message = ui.message,
                messageId = ui.messageId,
                onMessageShown = { viewModel.consumeMessage() },
                // 共享元素 key 必须与历史页那一行相同（都取合集 id）。
            )
        }
    }

    // 结果页（规格 §3）。
    if (route is Route.Result) {
        Box(
            modifier = Modifier
                .fillMaxSize()
                .clip(RoundedCornerShape(16.dp)),
        ) {
            ResultScreen(
                state = ui.result,
                dark = ui.settings.dark,
                fontSize = ui.settings.fontSize,
                task = ui.task,
                message = ui.message,
                messageId = ui.messageId,
                onMessageShown = { viewModel.consumeMessage() },
                onBack = { viewModel.popRoute() },
                // ⚠️ 列表是**新的在前**：更早的一条 = index+1 = 上一次识别；
                // 更新的一条 = index-1 = 下一次识别。
                onStep = { delta -> viewModel.stepResult(delta) },
                onReanalyze = { viewModel.reanalyzeCurrent() },
                onReload = { viewModel.reloadResult() },
                onBumpFontSize = { delta -> viewModel.bumpFontSize(delta) },
            )
        }
    }

    // 识别模块设置二级页（规格 §5）。1.1.0 的结构：
    // 设置首页只有「识别模块」开关 + 一个入口，细节在**这一页**
    // （截屏方式 / 悬浮球 / 一题多页 / 权限）。
    if (route is Route.RecognitionSettings) {
        Box(
            modifier = Modifier
                .fillMaxSize()
                .clip(RoundedCornerShape(16.dp)),
        ) {
            RecognitionSettingsScreen(
                ui = ui,
                vm = viewModel,
                onBack = { viewModel.popRoute() },
            )
        }
    }
}
