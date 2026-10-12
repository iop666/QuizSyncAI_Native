package com.quizsync.android.dev.ui

import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.PickVisualMediaRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.History
import androidx.compose.material.icons.filled.Link
import androidx.compose.material.icons.filled.PlayCircle
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material.icons.outlined.History
import androidx.compose.material.icons.outlined.PhotoLibrary
import androidx.compose.material.icons.outlined.PlayCircle
import androidx.compose.material.icons.outlined.Settings
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ExtendedFloatingActionButton
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.NavigationRail
import androidx.compose.material3.NavigationRailItem
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.material3.adaptive.currentWindowAdaptiveInfo
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.window.core.layout.WindowWidthSizeClass
import com.quizsync.android.dev.QsTokens
import com.quizsync.android.dev.state.HostConnection
import com.quizsync.android.dev.state.HomeTab
import com.quizsync.android.dev.state.QuizUiState
import com.quizsync.android.dev.state.QuizViewModel
import com.quizsync.android.dev.ui.components.MessageEffect
import com.quizsync.android.dev.ui.components.QuizSnackbarHost
import com.quizsync.android.dev.ui.designsystem.QsTag
import com.quizsync.android.dev.ui.haptics.longPress
import com.quizsync.android.dev.ui.haptics.press
import com.quizsync.android.dev.ui.haptics.rememberEnhancedHapticFeedback
import com.quizsync.android.dev.ui.theme.quizPalette

/**
 * 主壳（规格 ui-spec-android §1.2 / §2.2）：**底部三标签** + 顶部 AppBar。
 *
 * 三个 tab 与顺序：`当前任务` / `历史记录` / `设置`（HomeTab 的声明顺序即界面顺序）。
 *
 * 关于「丝滑」的几条硬要求（用户明确要求像成熟原生应用那样流畅）：
 * * 状态从外面一次读进来（`ui`），这里**不碰 ViewModel 的内部状态**，只发起意图；
 * * `when (ui.tab)` 里每个分支各自是 `@Composable`，切 tab 只重组这一层；
 * * 列表页一律 `LazyColumn` + 稳定 key（在各自的屏幕里），这里不铺长列表。
 *
 * **P4 重构（2.0）**：
 * * 切标签改用 Material Motion 的 **fade-through**（旧内容先淡出、新内容再淡入），
 *   时长取动效令牌 —— 这是 M3 对「并列目的地之间切换」的规定动作，
 *   比横向滑动短、也比横向滑动不晕（底部导航是并列关系，不是层级关系）；
 * * 「选择主机当前合集」从 `AlertDialog` 换成 **M3 原生 `ModalBottomSheet`**：
 *   这是一个「从若干选项里挑一个」的动作，Android 上的规定动作就是底部弹层
 *   （弹层的列表可以长出屏幕，弹窗不行，合集多了就得滚动一个对话框——那是错的形状）；
 * * 顶栏与状态药丸的字号、尺寸全部换成令牌（原来写死 18.sp / 11.5.sp / 7.dp / 5.dp）。
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun HomeShell(
    vm: QuizViewModel,
    ui: QuizUiState,
    refreshing: Boolean,
    onOpenPairing: () -> Unit,
) {
    // 历史页的滚动状态**提在这里**（在 when 之上）：切 tab 时子树会重建，
    // 但状态活在这一层，位置就还在 —— 这是 Compose 里替代 Flutter `IndexedStack` 的低风险做法。
    val historyListState = rememberLazyListState()

    // 提示通道（ViewModel 的 notify → Snackbar）。
    val snackbarHostState = remember { SnackbarHostState() }
    MessageEffect(snackbarHostState, ui.message, ui.messageId) { vm.consumeMessage() }

    val haptics = rememberEnhancedHapticFeedback()

    // 窗口尺寸类（计划 §6 指定的新 API：`currentWindowAdaptiveInfo()`，**不用** OptIn）。
    // 宽度 ≥ Medium（平板 / 折叠屏展开）时把主导航从底栏换成侧栏。
    val windowSizeClass = currentWindowAdaptiveInfo().windowSizeClass
    val wideScreen = windowSizeClass.windowWidthSizeClass != WindowWidthSizeClass.COMPACT

    // 「相册选图搜题」（规格 §2.2 / §1.4 的可达性表）：
    // 仅当 **当前任务标签 + 已配对 + 识别模块开启 + 主机已选合集** 时才出现。
    // 这套条件是老应用 `home_page.dart:426-427` 的 `canUpload`，逐条一样。
    val canUpload = ui.tab == HomeTab.CurrentTask &&
        ui.paired &&
        ui.settings.recognitionEnabled &&
        ui.host.hasActiveCollection

    // 系统相册选择器（Android Photo Picker；老版本自动退回 ACTION_OPEN_DOCUMENT）。
    // 取消选择（uri == null）**静默**——那是用户的正常操作，不该弹提示。
    val context = LocalContext.current
    val pickImage = rememberLauncherForActivityResult(ActivityResultContracts.PickVisualMedia()) { uri ->
        if (uri != null) {
            vm.submitImageFromUri(uri)
        }
    }

    // 「选择主机当前合集」（规格 §1.4 的 SimpleDialog）。
    // **这一段原来是缺的**：`openCollectionPicker()` 会去主机拉列表、写进
    // `ui.collectionPicker`，但没有任何界面渲染它 —— 点「选择合集」表现为
    // 拉了数据然后什么都不发生。这是同一个模式的第六次（三层都写好了，连接线没接）。
    ui.collectionPicker?.let { options ->
        ModalBottomSheet(
            onDismissRequest = { vm.dismissCollectionPicker() },
            sheetState = rememberModalBottomSheetState(),
        ) {
            CollectionPickerSheet(
                options = options.map { it.name to it.collectionId },
                currentId = ui.host.collectionId,
                onPick = { index -> vm.selectCollection(options[index]) },
            )
        }
    }

    Scaffold(
        // 用规格里的浮动样式（§1.4），不用 M3 默认那条贴满宽度的。
        snackbarHost = { QuizSnackbarHost(snackbarHostState) },
        floatingActionButton = {
            if (canUpload) {
                ExtendedFloatingActionButton(
                    onClick = {
                        haptics.press()
                        pickImage.launch(
                            PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageOnly),
                        )
                    },
                    // **显式给无障碍标签**：Compose 的 extended FAB 不会把 text 暴露成
                    // content-desc（真机 dump 实测：节点在、文字不到树里），于是屏幕阅读器
                    // 读不出这个按钮、自动化也找不到它 —— 两者都是真问题。
                    modifier = Modifier.semantics { contentDescription = "相册选图搜题" },
                    icon = { Icon(Icons.Outlined.PhotoLibrary, contentDescription = null) },
                    text = { Text("相册选图搜题") },
                )
            }
        },
        topBar = {
            // AppBar 按规格 §2.2（home_page.dart:419-530）：标题是**两行 Column**，
            // 第 1 行应用名，第 2 行是**连接状态圆点 + 文案**。
            // 原来这里写的是当前 tab 名 —— 那是错的：规格里 tab 只由底部导航栏表示，
            // 标题位永远显示应用名与连接状态。
            val kind = ui.host.connection
            val paired = kind != HostConnection.Unpaired
            val dotColor = when {
                !paired -> MaterialTheme.colorScheme.error
                kind == HostConnection.Online -> quizPalette.highlightText
                else -> MaterialTheme.colorScheme.outline
            }
            // hostName 的取值链：主机上报的 device_name → 本机记的已配对主机（§2.2 :428-432）。
            val hostName = ui.host.deviceName ?: ui.pairedHost.orEmpty()
            val statusText = when {
                !paired -> "未配对"
                kind == HostConnection.Online -> "已连接 $hostName"
                else -> "电脑未连接"
            }

            // 外观按规格 §2.2（app_theme.dart:71-84）：底色 = surface、elevation 0、
            // **滚动时也不加 tint**（`scrolledUnderElevation: 0` / `surfaceTintColor: transparent`）、
            // 底边 1px 描边与内容分隔。M3 的 TopAppBar 默认会在滚动时叠加 surfaceTint 与阴影，
            // 与规格不符 —— 所以配色要显式给，底边要自己画。
            Column {
                TopAppBar(
                    colors = TopAppBarDefaults.topAppBarColors(
                        containerColor = MaterialTheme.colorScheme.surface,
                        scrolledContainerColor = MaterialTheme.colorScheme.surface,
                    ),
                    title = {
                        Column {
                            Text(
                                "AI 双端搜题",
                                style = MaterialTheme.typography.titleLarge,
                                fontWeight = FontWeight.Bold,
                            )
                            Spacer(Modifier.height(QsTokens.spaceXxs))
                            Row(verticalAlignment = Alignment.CenterVertically) {
                                Box(
                                    Modifier
                                        .size(QsTokens.statusDot)
                                        .background(dotColor, CircleShape),
                                )
                                Spacer(Modifier.width(QsTokens.spaceXs))
                                Text(
                                    statusText,
                                    style = MaterialTheme.typography.labelSmall,
                                    fontWeight = FontWeight.Medium,
                                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                                )
                            }
                        }
                    },
                    actions = {
                        IconButton(onClick = onOpenPairing) {
                            Icon(Icons.Default.Link, contentDescription = "重新配对")
                        }
                    },
                )
                HorizontalDivider(
                    thickness = QsTokens.hairline,
                    color = MaterialTheme.colorScheme.outlineVariant,
                )
            }
        },
        bottomBar = {
            // 窄屏才用底栏。宽屏（平板 / 折叠屏展开）走左侧 NavigationRail —— 见下面 content 里的分支。
            // 判据用 M3 adaptive 的 `currentWindowAdaptiveInfo()`（计划 §6 指定的新 API）：
            // 不要用旧的 `calculateWindowSizeClass(this)`（要 OptIn，且新版已改名）。
            if (!wideScreen) {
                NavigationBar {
                    HomeTab.entries.forEach { tab -> NavigationBarItemFor(tab, ui.tab, haptics, vm) }
                }
            }
        },
    ) { padding ->
        Row(modifier = Modifier.padding(padding).fillMaxSize()) {
            // 宽屏：底栏换成左侧导航栏（同一个「三标签」信息架构，只是换了承载形态）。
            // 这是 M3 窗口尺寸类的规定动作，不是我们发明的：宽度 ≥ Medium 时把主导航从底部移到侧面，
            // 省下的纵向空间给内容，同时拇指/鼠标都不用跨整块屏幕。
            if (wideScreen) {
                NavigationRail {
                    HomeTab.entries.forEach { tab -> NavigationRailItemFor(tab, ui.tab, haptics, vm) }
                }
            }

            // 同级标签**不做任何动画**（用户口径 2026-09-28：「删除除了进入页面和返回页面外的所有动画」）。
            // 这里曾经是 Material 的 shared-axis X（各滑 1/4 屏 + 淡入淡出），现已删掉：
            // 并列关系的切换**直接换内容**，动画只保留"进页面 / 返回页面"那两种（见 QsStackTransitions.kt）。
            Column(modifier = Modifier.fillMaxSize()) {
                when (ui.tab) {
                    HomeTab.CurrentTask -> CurrentTaskScreen(
                        ui = ui,
                        refreshing = refreshing,
                        onRefresh = { vm.refreshAll() },
                        onPickCollection = { vm.openCollectionPicker() },
                        onDismissTask = { vm.dismissTask() },
                        onOpenResult = { sessionId -> vm.openResult(sessionId, ui.host.collectionId) },
                    )

                    HomeTab.History -> HistoryTab(
                        history = ui.history,
                        dark = ui.settings.dark,
                        onKeyword = { vm.setHistoryKeyword(it) },
                        onOpenCollection = { vm.openCollectionDetail(it.collectionId, it.title) },
                        onOpenSession = { sessionId, collectionId -> vm.openResult(sessionId, collectionId) },
                        onDeleteSession = { vm.deleteSession(it) },
                        // 合集多选删除（规格 §3.4）：长按分组行进多选，确认后走 VM 落库 + 同步。
                        onDeleteCollections = { vm.deleteCollections(it) },
                        onNotify = { vm.note(it) },
                        onRefresh = { vm.refreshAll() },
                        groupListState = historyListState,
                    )

                    HomeTab.Settings -> SettingsTab(
                        vm = vm,
                        ui = ui,
                        onOpenPairing = onOpenPairing,
                    )
                }
            }
        }
    }
}

/** 底栏的一项（宽屏与窄屏共用同一份选中态与触感语义）。 */
@Composable
private fun androidx.compose.foundation.layout.RowScope.NavigationBarItemFor(
    tab: HomeTab,
    current: HomeTab,
    haptics: androidx.compose.ui.hapticfeedback.HapticFeedback,
    vm: QuizViewModel,
) {
    NavigationBarItem(
        selected = current == tab,
        onClick = {
            // 切 tab 震一下（学自 ImageToolbox 的 EnhancedNavigationBarItem）。
            haptics.longPress()
            vm.selectTab(tab)
        },
        icon = { Icon(iconOf(tab, selected = current == tab), contentDescription = tab.label) },
        label = { Text(tab.label) },
    )
}

/** 侧栏的一项。 */
@Composable
private fun NavigationRailItemFor(
    tab: HomeTab,
    current: HomeTab,
    haptics: androidx.compose.ui.hapticfeedback.HapticFeedback,
    vm: QuizViewModel,
) {
    NavigationRailItem(
        selected = current == tab,
        onClick = {
            haptics.longPress()
            vm.selectTab(tab)
        },
        icon = { Icon(iconOf(tab, selected = current == tab), contentDescription = tab.label) },
        label = { Text(tab.label) },
    )
}

/** 合集选择弹层的内容：当前合集前置一个勾，其余留同样宽的空位对齐。 */
@Composable
private fun CollectionPickerSheet(
    options: List<Pair<String, String>>,
    currentId: String?,
    onPick: (Int) -> Unit,
) {
    Column(modifier = Modifier.fillMaxWidth().padding(bottom = QsTokens.spaceXl)) {
        Text(
            "选择主机当前合集",
            style = MaterialTheme.typography.titleMedium,
            modifier = Modifier.padding(
                start = QsTokens.spaceLg,
                end = QsTokens.spaceLg,
                bottom = QsTokens.spaceSm,
            ),
        )
        if (options.isEmpty()) {
            Text(
                "主机上还没有合集。",
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.padding(horizontal = QsTokens.spaceLg),
            )
            return@Column
        }
        options.forEachIndexed { index, (name, id) ->
            val current = id == currentId
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .clickable { onPick(index) }
                    .padding(horizontal = QsTokens.spaceLg, vertical = QsTokens.spaceMd),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                // 当前合集行前置一个勾（规格：当前合集行前置 `Icons.check`）。
                if (current) {
                    Icon(
                        Icons.Default.Check,
                        contentDescription = null,
                        tint = MaterialTheme.colorScheme.primary,
                        modifier = Modifier.size(QsTokens.iconMd),
                    )
                } else {
                    Spacer(Modifier.width(QsTokens.iconMd))
                }
                Spacer(Modifier.width(QsTokens.spaceSm))
                Text(
                    name,
                    style = MaterialTheme.typography.bodyLarge,
                    fontWeight = if (current) FontWeight.SemiBold else FontWeight.Normal,
                )
                if (current) {
                    Spacer(Modifier.width(QsTokens.spaceSm))
                    QsTag(text = "当前", color = MaterialTheme.colorScheme.primary)
                }
            }
        }
    }
}

/**
 * 底部导航栏的图标（规格 §1.2，`home_page.dart:509-526`）。
 *
 * 规格给的是**成对**的：未选中用描边版、选中用实心版。
 * 原来三个 tab 各只有一个图标、选中态没有任何区别 —— 与规格不符；
 * 而且第一项用的是 `Home`，规格里是 `play_circle`（「当前任务」不是「首页」）。
 */
private fun iconOf(tab: HomeTab, selected: Boolean) = when (tab) {
    HomeTab.CurrentTask -> if (selected) Icons.Filled.PlayCircle else Icons.Outlined.PlayCircle
    HomeTab.History -> if (selected) Icons.Filled.History else Icons.Outlined.History
    HomeTab.Settings -> if (selected) Icons.Filled.Settings else Icons.Outlined.Settings
}
