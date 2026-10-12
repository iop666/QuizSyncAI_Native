// 「当前任务」tab —— 规格 §2.3 + 第 4 章逐条实现。
//
// 页面主体只有两样东西（current_task_page.dart:13-22 的注释原文）：
//   1. 与电脑的连接状态（一行：已连接 / 未连接 / 还没选合集）；
//   2. 电脑的任务结果（进行中 =「N 张图片识别中…」，完成 = 答案）。
// 本机识别的开关、权限引导、未配对提示**一概不在此页**（它们在「设置 → 识别模块」）。
package com.quizsync.android.dev.ui

import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.filled.ErrorOutline
import androidx.compose.material.icons.filled.PlayCircleOutline
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.WifiTethering
import androidx.compose.material.icons.outlined.FolderOff
import androidx.compose.material.icons.outlined.FilterNone
import androidx.compose.material.icons.outlined.LinkOff
import androidx.compose.material.icons.outlined.Visibility
import androidx.compose.material.icons.outlined.WifiOff
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.pulltorefresh.PullToRefreshBox
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.quizsync.android.dev.QsTokens
import com.quizsync.android.dev.ui.designsystem.QsCard
import com.quizsync.android.dev.ui.designsystem.QsTextButton
import com.quizsync.android.dev.ui.designsystem.QsTonalButton
import com.quizsync.android.dev.state.ActiveTaskState
import com.quizsync.android.dev.state.HostConnection
import com.quizsync.android.dev.state.HostStatus
import com.quizsync.android.dev.state.QuizUiState
import com.quizsync.android.dev.ui.components.NextRoundBanner
import com.quizsync.android.dev.ui.theme.quizPalette

/**
 * 当前任务页。
 *
 * 刷新用 `PullToRefreshBox`（Material 3 的下拉刷新），与规格里的 `RefreshIndicator` 同义；
 * 列表底部留 **96** 的内边距避开底部导航栏 + FAB（§2.3）。
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun CurrentTaskScreen(
    ui: QuizUiState,
    refreshing: Boolean,
    onRefresh: () -> Unit,
    onPickCollection: () -> Unit,
    onDismissTask: () -> Unit,
    onOpenResult: (String) -> Unit,
    modifier: Modifier = Modifier,
) {
    val listState = rememberLazyListState()
    PullToRefreshBox(
        isRefreshing = refreshing,
        onRefresh = onRefresh,
        modifier = modifier.fillMaxSize(),
    ) {
        LazyColumn(
            state = listState,
            modifier = Modifier.fillMaxSize(),
            contentPadding = PaddingValues(
            start = QsTokens.spaceLg,
            top = QsTokens.spaceMd,
            end = QsTokens.spaceLg,
            bottom = QsTokens.bottomBarClearance,
        ),
        ) {
            if (ui.task.showsPreviousResult || ui.task.completesIntoBanner) {
                item(key = "next-task-banner") {
                    NextRoundBanner(
                        text = if (ui.task.showsPreviousResult) "${ui.task.pages} 张图片识别中…" else "识别完成",
                        spinning = ui.task.showsPreviousResult,
                        modifier = Modifier.padding(bottom = QsTokens.spaceMd),
                    )
                }
            }

            item(key = "host-status") {
                HostStatusCard(
                    host = ui.host,
                    paired = ui.paired,
                    onPickCollection = onPickCollection,
                    onRefresh = onRefresh,
                )
            }

            // 多页收集中的进度（规格 §3.1：采集进度**只**反映在「当前任务」页与常驻通知里，
            // 不切 tab、不跳结果页）。这一行也是「长按手势真的走到了多页」的界面证据。
            if (ui.capture.pages > 0) {
                item(key = "capture-collecting") {
                    Gap()
                    CollectingCard(pages = ui.capture.pages, limit = ui.settings.multiPageLimit)
                }
            }

            when {
                ui.task.running && ui.task.showsPreviousResult ->
                    item(key = "task-done-previous") {
                        Gap()
                        DoneCard(
                            ui = ui,
                            sessionId = ui.task.shownSessionId,
                            dismissible = false,
                            onDismiss = onDismissTask,
                            onOpenResult = onOpenResult,
                        )
                    }

                ui.task.running ->
                    item(key = "task-running") {
                        Gap()
                        RunningCard(pages = ui.task.pages)
                    }

                ui.task.failed -> {
                    item(key = "task-failed") {
                        Gap()
                        FailedCard(message = ui.task.message, onDismiss = onDismissTask)
                    }
                    if (!ui.task.shownSessionId.isNullOrEmpty()) {
                        item(key = "task-done-after-failed") {
                            Gap()
                            DoneCard(
                                ui = ui,
                                sessionId = ui.task.shownSessionId,
                                dismissible = false,
                                onDismiss = onDismissTask,
                                onOpenResult = onOpenResult,
                            )
                        }
                    }
                }

                ui.task.finished ->
                    item(key = "task-done") {
                        Gap()
                        DoneCard(
                            ui = ui,
                            sessionId = ui.task.sessionId,
                            dismissible = true,
                            onDismiss = onDismissTask,
                            onOpenResult = onOpenResult,
                        )
                    }

                ui.host.connection == HostConnection.Online && ui.host.hasActiveCollection ->
                    item(key = "not-started") {
                        Gap()
                        IdleCard()
                    }
            }
        }
    }
}

/** 卡片之间的固定间距：12（`_gap`，current_task_page.dart:253）。 */
@Composable
private fun Gap() = Spacer(Modifier.size(QsTokens.spaceMd))

/** 连接状态卡（§2.3.1）：永远存在，四态文案逐字。 */
@Composable
private fun HostStatusCard(
    host: HostStatus,
    paired: Boolean,
    onPickCollection: () -> Unit,
    onRefresh: () -> Unit,
) {
    val palette = quizPalette
    val online = host.connection == HostConnection.Online
    val state = hostStatusLine(host = host, paired = paired)
    QsCard(contentPadding = androidx.compose.foundation.layout.PaddingValues(0.dp)) {
        Row(
            modifier = Modifier.padding(start = QsTokens.spaceLg, top = QsTokens.spaceSm, end = QsTokens.spaceXs, bottom = QsTokens.spaceSm),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Icon(state.icon, contentDescription = null, tint = state.color, modifier = Modifier.size(QsTokens.iconSm))
            Spacer(Modifier.width(QsTokens.spaceSm))
            Text(
                text = state.label,
                style = MaterialTheme.typography.bodyMedium,
                fontWeight = FontWeight.SemiBold,
                color = state.color,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                modifier = Modifier.weight(1f),
            )
            if (online && !host.hasActiveCollection) {
                QsTextButton(onClick = onPickCollection) { Text("选择合集") }
            }
            IconButton(onClick = onRefresh) {
                Icon(Icons.Filled.Refresh, contentDescription = "刷新状态", modifier = Modifier.size(QsTokens.iconSm))
            }
        }
    }
}

private data class StatusLine(val icon: ImageVector, val label: String, val color: Color)

/** §2.3.1 的五态（未配对 / 连接中 / 离线 / 在线已选合集 / 在线未选合集）。 */
@Composable
private fun hostStatusLine(host: HostStatus, paired: Boolean): StatusLine {
    val palette = quizPalette
    return when {
        !paired || host.connection == HostConnection.Unpaired ->
            StatusLine(Icons.Outlined.LinkOff, "未配对", MaterialTheme.colorScheme.error)

        host.connection == HostConnection.Checking ->
            StatusLine(Icons.Filled.WifiTethering, "正在连接电脑…", MaterialTheme.colorScheme.onSurfaceVariant)

        host.connection == HostConnection.Offline ->
            StatusLine(Icons.Outlined.WifiOff, "电脑未连接", MaterialTheme.colorScheme.error)

        host.hasActiveCollection -> StatusLine(
            Icons.Filled.CheckCircle,
            "已连接 · ${host.collectionName ?: host.collectionId}",
            palette.highlightText,
        )

        else -> StatusLine(Icons.Outlined.FolderOff, "已连接 · 电脑还没选合集", MaterialTheme.colorScheme.error)
    }
}

/** 卡片 A：「还没开始识别」（§2.3.2）。 */
@Composable
private fun IdleCard() {
    QsCard(contentPadding = androidx.compose.foundation.layout.PaddingValues(0.dp)) {
        Row(modifier = Modifier.padding(QsTokens.spaceLg)) {
            Icon(
                Icons.Filled.PlayCircleOutline,
                contentDescription = null,
                tint = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.size(QsTokens.iconMd),
            )
            Spacer(Modifier.width(QsTokens.spaceSm))
            Column(modifier = Modifier.weight(1f)) {
                Text("还没开始识别", style = MaterialTheme.typography.titleSmall)
                Spacer(Modifier.size(QsTokens.spaceXs))
                Text(
                    "在电脑上按下识别热键，或截屏识别后，结果会自动出现在这里。",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
            }
        }
    }
}

/**
 * 卡片 E：「多页收集中」（规格 §3.1 的一题多页手势）。
 *
 * 页数严格等于长按次数 —— 收尾的短按**只提交已经截好的页**，不再补截一张，
 * 所以这里显示的是「已截到第几页」，不是「第几次点」。
 */
@Composable
private fun CollectingCard(pages: Int, limit: Int) {
    // 与悬浮球的多页态同一个琥珀色（BallState.MultiPage = 0xFFF59E0B）。
    val amber = Color(0xFFF59E0B)
    QsCard(
        contentPadding = androidx.compose.foundation.layout.PaddingValues(0.dp),
        // 琥珀描边：这一张是「还在攒页」的进行中卡，边界要看得清（不是装饰描边）。
        border = androidx.compose.foundation.BorderStroke(QsTokens.hairline, amber.copy(alpha = 0.55f)),
    ) {
        Column(modifier = Modifier.padding(QsTokens.spaceLg)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Icon(
                    Icons.Outlined.FilterNone,
                    contentDescription = null,
                    tint = amber,
                    modifier = Modifier.size(QsTokens.iconMd),
                )
                Spacer(Modifier.width(QsTokens.spaceMd))
                Text(
                    "多页收集中 · 已截 $pages/$limit 页",
                    style = MaterialTheme.typography.titleSmall,
                    modifier = Modifier.weight(1f),
                )
            }
            Spacer(Modifier.size(QsTokens.spaceSm))
            Text(
                "继续「长按」悬浮球截下一页；「短按」即结束并提交已截的页（不会再多截一张）。",
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
        }
    }
}

/** 卡片 B：「识别中」（§2.3.2）。 */
@Composable
private fun RunningCard(pages: Int) {
    QsCard(contentPadding = androidx.compose.foundation.layout.PaddingValues(0.dp)) {
        Column(modifier = Modifier.padding(QsTokens.spaceLg)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                CircularProgressIndicator(modifier = Modifier.size(QsTokens.iconMd), strokeWidth = QsTokens.progressStroke)
                Spacer(Modifier.width(QsTokens.spaceMd))
                Text(
                    "$pages 张图片识别中…",
                    style = MaterialTheme.typography.titleSmall,
                    modifier = Modifier.weight(1f),
                )
            }
            Spacer(Modifier.size(QsTokens.spaceSm))
            Text(
                "题目已上传到电脑，AI 正在读题。完成后本页会自动刷新。",
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
        }
    }
}

/** 卡片 C：「识别失败」（§2.3.2）。 */
@Composable
private fun FailedCard(message: String?, onDismiss: () -> Unit) {
    QsCard(contentPadding = androidx.compose.foundation.layout.PaddingValues(0.dp)) {
        Column(modifier = Modifier.padding(QsTokens.spaceLg)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Icon(
                    Icons.Filled.ErrorOutline,
                    contentDescription = null,
                    tint = MaterialTheme.colorScheme.error,
                    modifier = Modifier.size(QsTokens.iconMd),
                )
                Spacer(Modifier.width(QsTokens.spaceMd))
                Text("识别失败", style = MaterialTheme.typography.titleSmall)
            }
            Spacer(Modifier.size(QsTokens.spaceSm))
            Text(
                message ?: "主机未返回原因，可在电脑端查看日志。",
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            Spacer(Modifier.size(QsTokens.spaceSm))
            QsTextButton(onClick = onDismiss, modifier = Modifier.align(Alignment.End)) { Text("知道了") }
        }
    }
}

/** 卡片 D：「识别完成」（§2.3.2）：逐题一行「{n}. 第 X 题」+ 右侧答案。 */
@Composable
private fun DoneCard(
    ui: QuizUiState,
    sessionId: String?,
    dismissible: Boolean,
    onDismiss: () -> Unit,
    onOpenResult: (String) -> Unit,
) {
    val palette = quizPalette
    QsCard(contentPadding = androidx.compose.foundation.layout.PaddingValues(0.dp)) {
        Column(modifier = Modifier.padding(QsTokens.spaceLg)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Icon(
                    Icons.Filled.CheckCircle,
                    contentDescription = null,
                    tint = palette.highlightText,
                    modifier = Modifier.size(QsTokens.iconMd),
                )
                Spacer(Modifier.width(QsTokens.spaceMd))
                Text("识别完成", style = MaterialTheme.typography.titleSmall, modifier = Modifier.weight(1f))
            }
            when {
                ui.taskQuestionsLoading -> {
                    Spacer(Modifier.size(QsTokens.spaceSm))
                    Text(
                        "正在读取结果…",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }

                ui.taskQuestions.isEmpty() -> {
                    Spacer(Modifier.size(QsTokens.spaceSm))
                    Text(
                        "结果已同步到本机，可以离线查看。",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }

                else -> {
                    val questions = ui.taskQuestions
                    Column {
                        questions.forEach { question ->
                            Row(modifier = Modifier.padding(top = QsTokens.spaceSm)) {
                                Text(
                                    question.displayTitle,
                                    style = MaterialTheme.typography.bodyMedium,
                                    modifier = Modifier.weight(1f),
                                )
                                Spacer(Modifier.width(QsTokens.spaceMd))
                                Text(
                                    question.answerBrief,
                                    style = MaterialTheme.typography.titleSmall,
                                    color = palette.highlightText,
                                    textAlign = TextAlign.End,
                                    modifier = Modifier.weight(1f),
                                )
                            }
                        }
                    }
                }
            }
            Spacer(Modifier.size(QsTokens.spaceSm))
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.End,
                verticalAlignment = Alignment.CenterVertically,
            ) {
                if (dismissible) {
                    QsTextButton(onClick = onDismiss) { Text("忽略") }
                    Spacer(Modifier.width(QsTokens.spaceSm))
                }
                if (sessionId != null) {
                    QsTonalButton(onClick = { onOpenResult(sessionId) }) {
                        Icon(Icons.Outlined.Visibility, contentDescription = null, modifier = Modifier.size(QsTokens.iconSm))
                        Spacer(Modifier.width(QsTokens.spaceSm))
                        Text("查看结果")
                    }
                }
            }
        }
    }
}
