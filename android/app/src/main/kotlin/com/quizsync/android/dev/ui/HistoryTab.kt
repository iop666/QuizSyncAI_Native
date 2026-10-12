package com.quizsync.android.dev.ui

import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.background
import com.quizsync.android.dev.ui.haptics.hapticsClickable
import com.quizsync.android.dev.ui.haptics.hapticsCombinedClickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyListState
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.ChevronRight
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.Folder
import androidx.compose.material.icons.filled.FolderOff
import androidx.compose.material.icons.filled.ErrorOutline
import androidx.compose.material.icons.filled.PhotoCamera
import androidx.compose.material.icons.filled.RadioButtonUnchecked
import androidx.compose.material.icons.filled.SearchOff
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.pulltorefresh.PullToRefreshBox
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import com.quizsync.android.dev.QsTokens
import com.quizsync.android.dev.model.CollectionGroup
import com.quizsync.android.dev.model.qsSelectionLabel
import com.quizsync.android.dev.model.qsToggleCollectionSelection
import com.quizsync.android.dev.state.HistoryState
import com.quizsync.android.dev.ui.components.SessionTile
import com.quizsync.android.dev.ui.designsystem.QsCard
import com.quizsync.android.dev.ui.designsystem.QsConfirmDialog
import com.quizsync.android.dev.ui.designsystem.QsSecondaryButton
import com.quizsync.android.dev.ui.designsystem.QsStateView
import com.quizsync.android.dev.ui.designsystem.QsTextButton
import com.quizsync.android.dev.ui.designsystem.QsTextField
import com.quizsync.android.dev.ui.theme.quizPalette
import androidx.compose.ui.text.font.FontFamily

/**
 * 历史记录 tab（规格 ui-spec-android §2.4）。
 *
 * 二选一（§2.4 的 `_groupList` / `_searchResults`）：**搜索词为空看合集分组，非空看搜索结果**。
 *
 * 三个主体的实现在这里：§2.4.1 分组列表、§2.4.2 搜索结果、§2.4.4/§2.4.5 的详情页与删除确认
 * （详情页在 `CollectionDetailScreen`，删除确认走 `QsConfirmDialog`）；下拉刷新接的是 ViewModel 的 refresh。
 *
 * **合集多选删除（规格 §3.4）也在这一页**：长按分组行进入多选 → 点选 → 顶部操作条 → 二次确认。
 * 选择逻辑是纯函数 `qsToggleCollectionSelection`（有单测）。
 *
 * **P2 重构（2.0）**：删除确认换成 `QsConfirmDialog`、三种状态（加载/出错/空）统一到 `QsStateView`、
 * 分组卡片换成 `QsCard`，间距与尺寸全部取令牌（原来散着 12 / 88 / 38 / 19 / 6 dp 这些字面量）。
 */
@Composable
fun HistoryTab(
    history: HistoryState,
    dark: Boolean,
    onKeyword: (String) -> Unit,
    onOpenCollection: (CollectionGroup) -> Unit,
    onOpenSession: (String, String?) -> Unit,
    onDeleteSession: (String) -> Unit,
    /**
     * 多选删除合集（规格 §3.4）。参数是**选中的 collection_id 列表**。
     */
    onDeleteCollections: (List<String>) -> Unit,
    /**
     * 「未分类」不能删时用它出声（规格 §2.4.1 的未分类不是真实合集）。
     * **宁可明说，也不静默什么都不发生** —— 静默会被当成按钮坏了。
     */
    onNotify: (String) -> Unit,
    onRefresh: () -> Unit,
    /**
     * 分组列表的滚动状态。**由调用方（HomeShell）持有**，不是这里 `remember` 的 ——
     * 规格 §1.2 要求切 tab 不丢滚动位置，而 Compose 没有 `IndexedStack` 那种保活容器
     * （查证过：`foundation` / `foundation-layout` 1.9.1 里都没有这个类），
     * 所以把状态**提到 `when` 之上**：子树照旧重建，但位置还在。
     */
    groupListState: LazyListState,
    modifier: Modifier = Modifier,
) {
    // 待删的那条（规格 §2.4.5 要求**必须二次确认**）。
    var pendingDelete by remember { mutableStateOf<String?>(null) }

    // 多选模式：`null` = 不在多选；空集不会出现（取消最后一个选中项就退出，见纯函数）。
    var selection by remember { mutableStateOf<Set<String>?>(null) }

    // 待删的合集（规格 §3.4 同样要求二次确认）。
    var pendingCollectionDelete by remember { mutableStateOf<List<String>?>(null) }

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

    pendingCollectionDelete?.let { targets ->
        QsConfirmDialog(
            title = "删除这 ${targets.size} 个合集？",
            // 口径照 M18：**不级联删记录** —— 所以这里必须写清记录去哪儿了，否则用户不敢点。
            // 文案里**不许留 Markdown 记号**：`QsConfirmDialog` 的正文是纯 `Text(text)`，
            // 写成 `**不会**` 用户看到的就是字面的两个星号（真机 dump 实测）。
            text = "合集本身会被删除（软删除并同步到电脑）。\n"
                + "里面的识别记录不会跟着删，它们会移到「未分类」。",
            confirmLabel = "删除合集",
            onConfirm = {
                pendingCollectionDelete = null
                selection = null
                onDeleteCollections(targets)
            },
            onDismiss = { pendingCollectionDelete = null },
        )
    }

    Column(modifier = modifier.fillMaxSize()) {
        if (selection != null) {
            SelectionBar(
                count = selection?.size ?: 0,
                onDelete = { pendingCollectionDelete = selection?.toList()?.sorted() },
                onCancel = { selection = null },
            )
        } else {
            QsTextField(
                value = history.keyword,
                onValueChange = onKeyword,
                singleLine = true,
                placeholder = { Text("搜索题干与解析", fontFamily = FontFamily.Default) },
                trailingIcon = {
                    if (history.keyword.isNotEmpty()) {
                        Icon(
                            Icons.Default.Close,
                            contentDescription = "清空搜索",
                            modifier = Modifier.hapticsClickable { onKeyword("") }.padding(QsTokens.spaceXs),
                        )
                    }
                },
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(
                        start = QsTokens.spaceMd,
                        end = QsTokens.spaceMd,
                        top = QsTokens.spaceSm,
                        bottom = QsTokens.spaceXs,
                    ),
            )
        }

        when {
            history.loading -> QsStateView(
                title = "正在读取历史…",
                loading = true,
                modifier = Modifier.fillMaxSize(),
            )

            history.error != null -> QsStateView(
                title = "读取历史失败",
                message = history.error,
                icon = Icons.Default.ErrorOutline,
                error = true,
                modifier = Modifier.fillMaxSize(),
            )

            history.keyword.isNotEmpty() -> SearchResults(
                history,
                dark,
                onOpenSession,
                onClearSearch = { onKeyword("") },
                onDeleteRequest = { pendingDelete = it },
            )

            else -> GroupList(
                history = history,
                onOpenCollection = onOpenCollection,
                onRefresh = onRefresh,
                listState = groupListState,
                selection = selection,
                onSelect = { group ->
                    // 未分类不能删：如实说出来，而不是默默没反应。
                    if (group.isUnclassified) {
                        onNotify("「未分类」不是合集，不能删除")
                    } else {
                        selection = qsToggleCollectionSelection(selection, group)
                    }
                },
            )
        }
    }
}

/**
 * 多选模式的操作条：左边「已选 N 个合集」，右边删除 + 取消。
 *
 * 长按进入多选是 1.1.0 的口径（用户反馈 M18 第 2 条），操作条**替换搜索框的位置**，
 * 不额外占一行高度 —— 平板与手机都不会因此挤掉列表。
 */
@Composable
private fun SelectionBar(count: Int, onDelete: () -> Unit, onCancel: () -> Unit) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .padding(
                start = QsTokens.spaceMd,
                end = QsTokens.spaceMd,
                top = QsTokens.spaceSm,
                bottom = QsTokens.spaceXs,
            ),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(
            text = qsSelectionLabel(count),
            modifier = Modifier.weight(1f),
            fontWeight = FontWeight.SemiBold,
            style = MaterialTheme.typography.bodyLarge,
        )

        QsTextButton(onClick = onDelete) {
            Icon(Icons.Default.Delete, contentDescription = null, modifier = Modifier.size(QsTokens.iconSm))
            Spacer(Modifier.width(QsTokens.spaceXs))
            Text("删除", fontFamily = FontFamily.Default)
        }

        Spacer(Modifier.width(QsTokens.spaceSm))

        QsSecondaryButton(onClick = onCancel) {
            Text("取消", fontFamily = FontFamily.Default)
        }
    }
}

/** 列表统一的内边距：左右 12、上 4、**下留出底栏高度**（否则最后一条被底栏压住）。 */
private val ListInsets = PaddingValues(
    start = QsTokens.spaceMd,
    top = QsTokens.spaceXs,
    end = QsTokens.spaceMd,
    bottom = QsTokens.bottomBarClearance,
)

/** §2.4.2 搜索结果列表。 */
@Composable
private fun SearchResults(
    history: HistoryState,
    dark: Boolean,
    onOpenSession: (String, String?) -> Unit,
    onClearSearch: () -> Unit,
    onDeleteRequest: (String) -> Unit,
) {
    if (history.searchResults.isEmpty()) {
        // 空态文案逐字照 §2.4.2（含「清空搜索」按钮）。
        LazyColumn(modifier = Modifier.fillMaxSize(), contentPadding = ListInsets) {
            item {
                QsStateView(
                    icon = Icons.Default.SearchOff,
                    title = "没有匹配的记录",
                    message = "关键词「${history.keyword}」在题干与解析里都没找到。",
                    actions = {
                        QsSecondaryButton(onClick = onClearSearch) {
                            Icon(
                                Icons.Default.Close,
                                contentDescription = null,
                                modifier = Modifier.size(QsTokens.iconSm),
                            )
                            Spacer(Modifier.width(QsTokens.spaceSm))
                            Text("清空搜索")
                        }
                    },
                )
            }
        }
        return
    }

    LazyColumn(modifier = Modifier.fillMaxSize(), contentPadding = ListInsets) {
        items(history.searchResults, key = { it.sessionId }) { session ->
            SessionTile(
                session = session,
                dark = dark,
                onClick = { onOpenSession(session.sessionId, session.collectionId) },
                onLongPress = { onDeleteRequest(session.sessionId) },
            )
        }
    }
}

/** §2.4.1 合集分组列表。 */
@OptIn(androidx.compose.material3.ExperimentalMaterial3Api::class)
@Composable
private fun GroupList(
    history: HistoryState,
    onOpenCollection: (CollectionGroup) -> Unit,
    onRefresh: () -> Unit,
    listState: LazyListState,
    selection: Set<String>?,
    onSelect: (CollectionGroup) -> Unit,
) {
    // 下拉刷新（规格 §2.4.1 的 RefreshIndicator）。**`onRefresh` 原来声明了却没用** ——
    // 那不只是个死参数：`RecordsScreen` 删掉之后，「从电脑同步」这个入口在历史页就彻底没了，
    // 用户在这个页面无事可做。空态也要能下拉，否则「没有记录」时同样出不去。
    if (history.groups.isEmpty()) {
        PullToRefreshBox(
            isRefreshing = history.loading,
            onRefresh = onRefresh,
            modifier = Modifier.fillMaxSize(),
        ) {
            LazyColumn(state = listState, modifier = Modifier.fillMaxSize()) {
                item {
                    Spacer(Modifier.height(QsTokens.spaceXl * 4))
                    QsStateView(
                        icon = Icons.Default.PhotoCamera,
                        title = "还没有识别记录",
                        message = "点悬浮球截屏，或用「相册选图搜题」上传一张题目截图。",
                    )
                }
            }
        }
        return
    }

    PullToRefreshBox(
        isRefreshing = history.loading,
        onRefresh = onRefresh,
        modifier = Modifier.fillMaxSize(),
    ) {
        LazyColumn(
            state = listState,
            modifier = Modifier.fillMaxSize(),
            contentPadding = ListInsets,
        ) {
            items(history.groups, key = { it.key }) { group ->
                GroupTile(
                    group = group,
                    selecting = selection != null,
                    selected = group.collectionId != null && selection?.contains(group.collectionId) == true,
                    // 多选模式下**单击 = 选中/取消**，不再进详情页（否则没法多选）。
                    onClick = {
                        if (selection != null) onSelect(group) else onOpenCollection(group)
                    },
                    onLongPress = { if (selection == null) onSelect(group) },
                )
            }
        }
    }
}

/**
 * §2.4.1 的单个分组行。**长按进多选**（规格 §3.4 的合集删除入口），多选模式下单击即选中。
 * 选中逻辑本身在纯函数 `qsToggleCollectionSelection` 里（有单测）。
 */
@OptIn(androidx.compose.foundation.ExperimentalFoundationApi::class)
@Composable
private fun GroupTile(
    group: CollectionGroup,
    selecting: Boolean,
    selected: Boolean,
    onClick: () -> Unit,
    onLongPress: () -> Unit,
) {
    val primary = MaterialTheme.colorScheme.primary

    QsCard(
        modifier = Modifier
            .padding(bottom = QsTokens.spaceSm)
            .hapticsCombinedClickable(onClick = onClick, onLongClick = onLongPress),
        // 选中态用主色描边（1.1.0 的多选行就是这个观感）。
        border = if (selected) {
            BorderStroke(QsTokens.strokeEmphasis, primary)
        } else {
            BorderStroke(QsTokens.hairline, quizPalette.cardStroke)
        },
        contentPadding = PaddingValues(
            horizontal = QsTokens.spaceLg,
            vertical = QsTokens.spaceSm,
        ),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Box(
                modifier = Modifier
                    .size(QsTokens.iconBox)
                    .background(
                        if (selected) primary else primary.copy(alpha = 0.12f),
                        CircleShape,
                    ),
                contentAlignment = Alignment.Center,
            ) {
                if (selecting) {
                    // 多选模式下这一格变成勾选圈：**未分类不给勾**（它不是真实合集）。
                    Icon(
                        if (selected) Icons.Default.Check else Icons.Default.RadioButtonUnchecked,
                        contentDescription = if (selected) "已选中" else "未选中",
                        tint = if (selected) MaterialTheme.colorScheme.onPrimary else primary,
                        modifier = Modifier.size(QsTokens.iconMd),
                    )
                } else {
                    Icon(
                        // 未分类用「文件夹划掉」，与真实合集区分开（§2.4.1）。
                        if (group.isUnclassified) Icons.Default.FolderOff else Icons.Default.Folder,
                        contentDescription = null,
                        tint = primary,
                        modifier = Modifier.size(QsTokens.iconMd),
                    )
                }
            }

            Spacer(Modifier.width(QsTokens.spaceLg))

            Column(modifier = Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(QsTokens.spaceXxs)) {
                Text(
                    group.title,
                    fontWeight = FontWeight.SemiBold,
                    style = MaterialTheme.typography.bodyLarge,
                    // 共享元素**已删除**（用户口径 2026-09-28：只留"进页面 / 返回页面"两种动画）。
                    // 原来这里是 `Modifier.qsSharedElement(group.collectionId)`：标题会连续地飞到详情页顶栏。
                )
                Text(
                    "${group.count} 条识别记录", fontFamily = FontFamily.Default,
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }

            Icon(
                Icons.Default.ChevronRight,
                contentDescription = null,
                tint = MaterialTheme.colorScheme.outline,
                modifier = Modifier.size(QsTokens.iconMd),
            )
        }
    }
}
