package com.quizsync.android.dev.ui

import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.ErrorOutline
import androidx.compose.material.icons.filled.Inbox
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.TextButton
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import com.quizsync.android.dev.ui.components.MessageEffect
import androidx.compose.material3.Scaffold
import com.quizsync.android.dev.ui.components.QuizSnackbarHost
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import com.quizsync.android.dev.QsTokens
import com.quizsync.android.dev.state.HistoryState
import com.quizsync.android.dev.ui.components.SessionTile
import com.quizsync.android.dev.ui.designsystem.QsConfirmDialog
import com.quizsync.android.dev.ui.designsystem.QsStateView

/**
 * 合集详情页（规格 ui-spec-android §2.4.4，出处 `history_page.dart` 的 `CollectionSessionsPage`）。
 *
 * * `Scaffold` + `AppBar(title = 合集名)`，返回走**系统自动返回键**（规格 :379-382）；
 * * loading → 居中转圈；error → `QsStateView(出错了, {e})`；
 *   空 → `QsStateView(收件箱, '这个分组还没有识别记录', '回到「当前任务」发起一次识别后，记录会出现在这里。')`；
 * * 有数据 → `LazyColumn(padding fromLTRB(12,12,12,24))` + `SessionTile`（点击 → 结果页）。
 *
 * 这一页是**压在主壳之上的路由**（`Route.CollectionDetail`），不是标签页 ——
 * 所以它有 AppBar 与返回箭头，主壳没有。
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun CollectionDetailScreen(
    title: String,
    history: HistoryState,
    dark: Boolean,
    onBack: () -> Unit,
    onOpenSession: (String, String?) -> Unit,
    onDeleteSession: (String) -> Unit,
    message: String?,
    messageId: Long,
    onMessageShown: () -> Unit,
) {
    // 二次确认（规格 §2.4.5）。与 HistoryTab 里那份同源，都照规格文案。
    var pendingDelete by remember { mutableStateOf<String?>(null) }

    pendingDelete?.let { target ->
        QsConfirmDialog(
            title = "删除这条识别记录？",
            text = "本机与电脑上的这条记录都会被删除（软删除，会同步到主机）。此操作不可撤销。",
            confirmLabel = "删除",
            onConfirm = {
                pendingDelete = null
                onDeleteSession(target)
            },
            onDismiss = { pendingDelete = null },
        )
    }

    // 删除就发生在这一页，提示必须在这一页看得见（主壳的 Snackbar 盖不到压栈页面）。
    val snackbarHostState = remember { SnackbarHostState() }
    MessageEffect(snackbarHostState, message, messageId) { onMessageShown() }

    Scaffold(
        // 用规格里的浮动样式（§1.4），不用 M3 默认那条贴满宽度的。
        snackbarHost = { QuizSnackbarHost(snackbarHostState) },
        topBar = {
            TopAppBar(
                // 共享元素**已删除**（用户口径 2026-09-28：只留"进页面 / 返回页面"两种动画）。
                // 原来这里挂着 `Modifier.qsSharedElement(sharedKey)`，让合集名从历史页那一行连续地移过来。
                title = { Text(title) },
                navigationIcon = {
                    IconButton(onClick = onBack) {
                        Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = "返回")
                    }
                },
            )
        },
    ) { padding ->
        Box(modifier = Modifier.padding(padding).fillMaxSize()) {
            when {
                history.loading -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                    CircularProgressIndicator()
                }

                history.error != null -> QsStateView(
                    icon = Icons.Default.ErrorOutline,
                    title = "出错了",
                    message = history.error,
                    modifier = Modifier.align(Alignment.TopCenter),
                )

                history.detailSessions.isEmpty() -> QsStateView(
                    icon = Icons.Default.Inbox,
                    title = "这个分组还没有识别记录",
                    message = "回到「当前任务」发起一次识别后，记录会出现在这里。",
                    modifier = Modifier.align(Alignment.TopCenter),
                )

                else -> LazyColumn(
                    modifier = Modifier.fillMaxSize(),
                    contentPadding = PaddingValues(
                    start = QsTokens.spaceMd,
                    top = QsTokens.spaceMd,
                    end = QsTokens.spaceMd,
                    bottom = QsTokens.spaceXl,
                ),
                ) {
                    items(history.detailSessions, key = { it.sessionId }) { session ->
                        SessionTile(
                            session = session,
                            dark = dark,
                            onClick = { onOpenSession(session.sessionId, session.collectionId) },
                            onLongPress = { pendingDelete = session.sessionId },
                        )
                    }
                }
            }
        }
    }
}
