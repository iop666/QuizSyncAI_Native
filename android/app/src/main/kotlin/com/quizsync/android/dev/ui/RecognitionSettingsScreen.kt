package com.quizsync.android.dev.ui

import android.content.Intent
import android.net.Uri
import android.provider.Settings
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
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
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.HelpOutline
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.filled.ChevronRight
import androidx.compose.material.icons.filled.ErrorOutline
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.outlined.Accessibility
import androidx.compose.material.icons.outlined.BatterySaver
import androidx.compose.material.icons.outlined.BubbleChart
import androidx.compose.material.icons.outlined.FilterNone
import androidx.compose.material.icons.outlined.Info
import androidx.compose.material.icons.outlined.NotificationsActive
import androidx.compose.material.icons.outlined.PictureInPictureAlt
import androidx.compose.material.icons.outlined.RadioButtonChecked
import androidx.compose.material.icons.outlined.RadioButtonUnchecked
import androidx.compose.material.icons.outlined.ScreenshotMonitor
import androidx.compose.material.icons.outlined.TouchApp
import androidx.compose.material.icons.outlined.VerifiedUser
import androidx.compose.material.icons.outlined.Videocam
import androidx.compose.material.icons.outlined.WarningAmber
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import com.quizsync.android.dev.QsTokens
import com.quizsync.android.dev.state.CaptureMode
import com.quizsync.android.dev.state.QuizUiState
import com.quizsync.android.dev.state.QuizViewModel
import com.quizsync.android.dev.state.qsCapturePathNote
import com.quizsync.android.dev.ui.designsystem.QsInlineSliderRow
import com.quizsync.android.dev.ui.designsystem.QsSecondaryButton
import com.quizsync.android.dev.ui.designsystem.QsSectionCard
import com.quizsync.android.dev.ui.designsystem.QsSwitchRow
import com.quizsync.android.dev.ui.designsystem.QsTag
import com.quizsync.android.dev.ui.designsystem.QsTopBar
import com.quizsync.android.dev.ui.haptics.press
import com.quizsync.android.dev.ui.haptics.rememberEnhancedHapticFeedback
import com.quizsync.android.dev.ui.theme.quizPalette
import androidx.compose.ui.text.font.FontFamily

/** 「识别模块设置」二级页（规格 ui-spec-android §5）。 */
const val RECOGNITION_SETTINGS_TITLE = "识别模块设置"

/** 顶部说明条文案（**与设置首页共用同一份常量**，规格 §5.1 明确要求防漂移）。 */
const val RECOGNITION_SLOW_NOTE = "受限于网络延迟与 API 端响应，可能识别时间过长，不建议开启。"

/** 主机侧硬上限（协议 §3.3：一次任务最多 6 页）。 */
private const val HARD_MAX_PAGES = 6

/**
 * 识别模块二级页（规格 §5）。**这里是 1.1.0 的结构**：
 * 设置首页只放「识别模块」开关与一个入口，截屏方式 / 悬浮球 / 一题多页 / 权限都在这一页。
 *
 * 为什么必须分开（用户当场指出过）：这几项是**同一件事的参数**（截屏→上传→识别），
 * 散在设置首页里既看不出它们的关系，也放不下权限的状态与引导；
 * 1.1.0 的做法是「首页一个开关 + 一个入口，细节进二级页」，照做。
 *
 * 进入这一页、以及从系统设置返回时都会 `refreshPermissions()` —— 权限是在**系统界面**里改的，
 * 不重读就会显示上一次的快照（规格 §5.3 的「返回本页自动刷新」）。
 *
 * **P2 重构（2.0）**：顶栏、分区卡片、状态药丸、开关行换成设计系统组件；
 * 原来的 `TopAppBar` + 手写 `HorizontalDivider` 由 `QsTopBar` 统一（底层仍是原生 `TopAppBar`），
 * 极小的 `12.5.sp` / `38.dp` / `19.dp` 这类 1.1.0 像素对齐值收敛到令牌。
 * **权限项、开关、滑杆的落库调用与文案一字未改。**
 */
@Composable
fun RecognitionSettingsScreen(
    ui: QuizUiState,
    vm: QuizViewModel,
    onBack: () -> Unit,
) {
    val settings = ui.settings
    val context = LocalContext.current
    val haptics = rememberEnhancedHapticFeedback()

    Scaffold(
        topBar = {
            Column {
                QsTopBar(
                    title = RECOGNITION_SETTINGS_TITLE,
                    onBack = onBack,
                    // 「重新检查权限」：权限是在系统界面里改的，回来要能手动再读一次。
                    actions = {
                        IconButton(onClick = { vm.refreshPermissions() }) {
                            Icon(Icons.Default.Refresh, contentDescription = "重新检查权限")
                        }
                    },
                )
                HorizontalDivider(thickness = QsTokens.hairline, color = MaterialTheme.colorScheme.outlineVariant)
            }
        },
    ) { padding ->
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(padding)
                .verticalScroll(rememberScrollState())
                .padding(
                    start = QsTokens.spaceLg,
                    top = QsTokens.spaceLg,
                    end = QsTokens.spaceLg,
                    bottom = QsTokens.spaceXl,
                ),
            verticalArrangement = Arrangement.spacedBy(QsTokens.spaceMd),
        ) {
            SlowNote()

            QsSectionCard(
                title = "截屏方式",
                subtitle = "主路径是屏幕录制（系统授权一次后长期复用）。",
                icon = Icons.Outlined.ScreenshotMonitor,
            ) {
                ModeTile(
                    selected = settings.captureMode == CaptureMode.Main,
                    icon = Icons.Outlined.Videocam,
                    title = "主路径：屏幕录制（推荐）",
                    subtitle = "首次授权一次，之后点悬浮球直接截屏",
                    onSelect = {
                        haptics.press()
                        vm.setCaptureMode(CaptureMode.Main)
                        vm.note("已切换到主路径：屏幕录制")
                    },
                )
                ModeTile(
                    selected = settings.captureMode == CaptureMode.Accessibility,
                    icon = Icons.Outlined.Accessibility,
                    title = "备选路径：无障碍截屏",
                    subtitle = "Android 11+；不可用时自动回落主路径",
                    onSelect = {
                        haptics.press()
                        vm.setCaptureMode(CaptureMode.Accessibility)
                        vm.note("已切换到备选路径：无障碍截屏（不可用时自动回落）")
                    },
                )

                // 选了备选路径、但无障碍服务没开（或系统版本不够）时**当场说清后果**：
                // 不说的话，用户以为在用无障碍，实际每次都弹屏幕录制授权框 —— 现象完全一样。
                if (settings.captureMode == CaptureMode.Accessibility &&
                    settings.permissions.accessibility != true
                ) {
                    PermissionHint(
                        text = qsCapturePathNote(
                            mode = CaptureMode.Accessibility,
                            accessibilityConnected = false,
                            supported = android.os.Build.VERSION.SDK_INT >= android.os.Build.VERSION_CODES.R,
                        ) ?: "无障碍服务没开启：现在会回落到「屏幕录制」路径",
                        actionLabel = "去开启无障碍服务",
                        onAction = { openSystemSettings(context, Settings.ACTION_ACCESSIBILITY_SETTINGS) },
                    )
                }

                // 截屏授权（规格 §3.2）：主路径是「一次授权 = 一个常驻会话」，所以这里显示的
                // 不是普通权限而是**会话是否可用**；会话失效（锁屏 / 用户停止共享 / 权限撤销）
                // 或从没授权过时，点这一行重新弹系统授权框 —— 与上面两个模式行同一个形状。
                PermissionTile(
                    icon = Icons.Outlined.VerifiedUser,
                    title = "截屏授权（屏幕录制）",
                    subtitle = if (ui.capture.sessionReady) {
                        "已授权：会话常驻，点悬浮球直接截屏（不会再弹系统框）"
                    } else {
                        "未授权 / 会话已失效：点一下，在系统弹窗里选「立即开始」"
                    },
                    granted = ui.capture.sessionReady,
                    onOpen = { vm.requestCapturePermission() },
                )
            }

            QsSectionCard(
                title = "悬浮球",
                subtitle = "单击悬浮球＝截一张图直接识别；长按＝进入「一题多页」。",
                icon = Icons.Outlined.BubbleChart,
            ) {
                QsSwitchRow(
                    title = "显示悬浮球",
                    description = "在其他应用上也能一键截屏",
                    checked = settings.ballEnabled,
                    onCheckedChange = { vm.setBallEnabled(it) },
                )

                // 缺权限时**不静默**：给一条能走通的入口（读数来自 PermissionSnapshot）。
                if (settings.ballEnabled && settings.permissions.overlay == false) {
                    PermissionHint(
                        text = "还差一步：系统还没允许本应用「显示在其他应用上层」，悬浮球现在不会出现。",
                        actionLabel = "去授予悬浮窗权限",
                        onAction = { openSystemSettings(context, Settings.ACTION_MANAGE_OVERLAY_PERMISSION) },
                    )
                }

                QsInlineSliderRow(
                    label = "透明度",
                    value = settings.ballOpacity,
                    range = 0.3f..1f,
                    display = "${(settings.ballOpacity * 100).toInt()}%",
                    onChangeEnd = { vm.setBallOpacity(it) },
                )
                QsInlineSliderRow(
                    label = "大小",
                    value = settings.ballSizeDp.toFloat(),
                    range = 36f..72f,
                    display = "${settings.ballSizeDp} dp",
                    onChangeEnd = { vm.setBallSize(it.toInt()) },
                )
            }

            QsSectionCard(
                title = "一题多页",
                subtitle = "一张图放不下一道题时用：每页立即上传，收尾后一次性识别。",
                icon = Icons.Outlined.FilterNone,
            ) {
                Text(
                    "一题多页：「长按」悬浮球截取第 1 张（松手即完成一页）→ 再「长按」截取第 2 张 → " +
                        "以此类推（最多 ${settings.multiPageLimit} 张）→ 最后「短按」悬浮球收尾，" +
                        "本次所有截图会作为一道题一起识别。收尾的短按只提交已经截好的页，不会再多截一张；" +
                        "抓满上限会自动提交。",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                QsInlineSliderRow(
                    label = "最多页数",
                    value = settings.multiPageLimit.toFloat(),
                    range = 1f..HARD_MAX_PAGES.toFloat(),
                    steps = HARD_MAX_PAGES - 2,
                    display = "${settings.multiPageLimit} 页",
                    onChangeEnd = { vm.setMultiPageLimit(it.toInt()) },
                )
                Text(
                    "主机侧硬上限为 $HARD_MAX_PAGES 页，超过会被服务端拒绝。",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }

            QsSectionCard(
                title = "权限设置",
                subtitle = "缺哪一项，点进去开好再回来（返回本页会自动刷新）。",
                icon = Icons.Outlined.VerifiedUser,
            ) {
                PermissionTile(
                    icon = Icons.Outlined.NotificationsActive,
                    title = "通知权限",
                    subtitle = "缺失：截屏服务无法常驻（Android 14+ 硬性要求），后台完成也没有提醒",
                    granted = settings.permissions.notifications,
                    onOpen = { openSystemSettings(context, Settings.ACTION_APP_NOTIFICATION_SETTINGS) },
                )
                PermissionTile(
                    icon = Icons.Outlined.PictureInPictureAlt,
                    title = "悬浮窗权限",
                    subtitle = "缺失：悬浮球不显示，无法一键截屏",
                    granted = settings.permissions.overlay,
                    onOpen = { openSystemSettings(context, Settings.ACTION_MANAGE_OVERLAY_PERMISSION) },
                )
                PermissionTile(
                    icon = Icons.Outlined.BatterySaver,
                    title = "电池优化白名单",
                    subtitle = "缺失：后台可能被系统杀掉，截屏服务中断",
                    granted = settings.permissions.batteryWhitelist,
                    onOpen = { openSystemSettings(context, Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS) },
                )
                PermissionTile(
                    icon = Icons.Outlined.TouchApp,
                    title = "无障碍（备选截屏路径）",
                    subtitle = "仅借用系统的 takeScreenshot 能力，不读取屏幕内容；主路径失败时自动回落",
                    granted = settings.permissions.accessibility,
                    onOpen = { openSystemSettings(context, Settings.ACTION_ACCESSIBILITY_SETTINGS) },
                )

                Row(
                    modifier = Modifier.fillMaxWidth(),
                    verticalAlignment = Alignment.Top,
                ) {
                    Icon(
                        Icons.Outlined.Info,
                        contentDescription = null,
                        modifier = Modifier.size(QsTokens.iconSm),
                        tint = MaterialTheme.colorScheme.primary,
                    )
                    Spacer(Modifier.width(QsTokens.spaceSm))
                    Text(
                        "部分系统需要额外允许「后台运行、自启动」，否则悬浮球或截屏服务可能在息屏后被系统回收。",
                        style = MaterialTheme.typography.bodySmall,
                    )
                }
            }
        }
    }
}

/** 顶部说明条（规格 §5.1）：底色与边框都用「题目不全」那个琥珀色系（取 palette，不写字面量）。 */
@Composable
private fun SlowNote() {
    val amber = quizPalette.incomplete
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .background(amber.copy(alpha = 0.12f), RoundedCornerShape(QsTokens.radiusControl))
            .border(QsTokens.hairline, amber.copy(alpha = 0.35f), RoundedCornerShape(QsTokens.radiusControl))
            .padding(horizontal = QsTokens.spaceMd, vertical = QsTokens.spaceSm),
        verticalAlignment = Alignment.Top,
    ) {
        Icon(
            Icons.Outlined.WarningAmber,
            contentDescription = null,
            modifier = Modifier.size(QsTokens.iconSm),
            tint = amber,
        )
        Spacer(Modifier.width(QsTokens.spaceSm))
        Text(
            RECOGNITION_SLOW_NOTE,
            style = MaterialTheme.typography.bodySmall,
            color = amber,
        )
    }
}

/** 截屏方式的一行（规格 §5.3 的 `_modeTile`）。 */
@Composable
private fun ModeTile(
    selected: Boolean,
    icon: ImageVector,
    title: String,
    subtitle: String,
    onSelect: () -> Unit,
) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .clickable(onClick = onSelect)
            .padding(vertical = QsTokens.spaceSm, horizontal = QsTokens.spaceXs),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Icon(
            if (selected) Icons.Outlined.RadioButtonChecked else Icons.Outlined.RadioButtonUnchecked,
            contentDescription = null,
            modifier = Modifier.size(QsTokens.iconMd),
            tint = if (selected) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.outline,
        )
        Spacer(Modifier.width(QsTokens.spaceMd))
        Icon(
            icon,
            contentDescription = null,
            modifier = Modifier.size(QsTokens.iconSm),
            tint = MaterialTheme.colorScheme.onSurfaceVariant,
        )
        Spacer(Modifier.width(QsTokens.spaceMd))
        Column(modifier = Modifier.weight(1f)) {
            Text(
                title,
                style = MaterialTheme.typography.bodyLarge,
                fontWeight = if (selected) FontWeight.Bold else FontWeight.Medium,
            )
            Text(
                subtitle,
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
    }
}

/** 权限行（规格 §5.3 的 `_PermissionTile`）：状态药丸三态「未知 / 已授予 / 未授予」。 */
@Composable
private fun PermissionTile(
    icon: ImageVector,
    title: String,
    subtitle: String,
    granted: Boolean?,
    onOpen: () -> Unit,
) {
    val color = when (granted) {
        null -> MaterialTheme.colorScheme.onSurfaceVariant
        true -> quizPalette.highlightText
        false -> MaterialTheme.colorScheme.error
    }
    val label = when (granted) {
        null -> "未知"
        true -> "已授予"
        false -> "未授予"
    }
    val badge = when (granted) {
        null -> Icons.AutoMirrored.Filled.HelpOutline
        true -> Icons.Default.CheckCircle
        false -> Icons.Default.ErrorOutline
    }

    Row(
        modifier = Modifier
            .fillMaxWidth()
            .clickable(onClick = onOpen)
            .padding(vertical = QsTokens.spaceSm, horizontal = QsTokens.spaceXs),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Box(
            modifier = Modifier
                .size(QsTokens.iconBox)
                .background(color.copy(alpha = 0.13f), RoundedCornerShape(QsTokens.radiusControl)),
            contentAlignment = Alignment.Center,
        ) {
            Icon(icon, contentDescription = null, modifier = Modifier.size(QsTokens.iconMd), tint = color)
        }
        Spacer(Modifier.width(QsTokens.spaceMd))
        Column(modifier = Modifier.weight(1f)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(
                    title,
                    style = MaterialTheme.typography.bodyLarge,
                    fontWeight = FontWeight.SemiBold,
                    modifier = Modifier.weight(1f),
                )
                QsTag(text = label, color = color, icon = badge)
            }
            Spacer(Modifier.height(QsTokens.spaceXs))
            Text(
                subtitle,
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
        Spacer(Modifier.width(QsTokens.spaceSm))
        Icon(
            Icons.Default.ChevronRight,
            contentDescription = null,
            modifier = Modifier.size(QsTokens.iconSm),
            tint = MaterialTheme.colorScheme.outline,
        )
    }
}

@Composable
private fun PermissionHint(text: String, actionLabel: String, onAction: () -> Unit) {
    Column(modifier = Modifier.fillMaxWidth()) {
        Text(text, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.error)
        Spacer(Modifier.height(QsTokens.spaceSm))
        QsSecondaryButton(
            onClick = onAction,
            modifier = Modifier.fillMaxWidth(),
        ) { Text(actionLabel) }
    }
}

/** 打开系统设置页（权限只能在那里改）。 */
private fun openSystemSettings(context: android.content.Context, action: String) {
    val intent = Intent(action, Uri.parse("package:" + context.packageName))
    runCatching { context.startActivity(intent) }
}
