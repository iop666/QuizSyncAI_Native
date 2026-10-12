package com.quizsync.android.dev.ui.components

import androidx.compose.foundation.background
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import com.quizsync.android.dev.ui.haptics.hapticsCombinedClickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.HelpOutline
import androidx.compose.material.icons.filled.Autorenew
import androidx.compose.material.icons.filled.Cancel
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.filled.ChevronRight
import androidx.compose.material.icons.filled.ErrorOutline
import androidx.compose.material.icons.filled.Schedule
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.quizsync.android.dev.model.SessionItem
import com.quizsync.android.dev.model.SessionStatus
import com.quizsync.android.dev.QsTokens
import androidx.compose.ui.text.font.FontFamily

/**
 * 状态外观：颜色 + 图标 + 中文标签。
 *
 * 色值是规格 §2.4.3 的 `statusStyle` 表，**浅/深两套固定值**（不跟主题种子色走）——
 * 与 `ui-pkg/src/widgets.dart:173-211` 一致。
 */
data class StatusStyle(val color: Color, val icon: ImageVector, val label: String)

private val SlateLight = Color(0xFF64748B)
private val SlateDark = Color(0xFF9AA4AE)

/** 状态 → 外观。未知状态原样显示（不吞掉、也不猜）。 */
fun statusStyleOf(status: String?, dark: Boolean): StatusStyle = when (status) {
    SessionStatus.Queued.wire -> StatusStyle(if (dark) SlateDark else SlateLight, Icons.Default.Schedule, "排队中")
    SessionStatus.Analyzing.wire -> StatusStyle(if (dark) Color(0xFF60A5FA) else Color(0xFF2563EB), Icons.Default.Autorenew, "识别中")
    SessionStatus.Done.wire -> StatusStyle(if (dark) Color(0xFF4ADE80) else Color(0xFF16A34A), Icons.Default.CheckCircle, "已完成")
    SessionStatus.Failed.wire -> StatusStyle(if (dark) Color(0xFFF87171) else Color(0xFFDC2626), Icons.Default.ErrorOutline, "失败")
    SessionStatus.Cancelled.wire -> StatusStyle(if (dark) SlateDark else SlateLight, Icons.Default.Cancel, "已取消")
    // AutoMirrored 版本是官方替代（RTL 语言下图标要镜像）；旧的 Icons.Default.HelpOutline 已弃用。
    else -> StatusStyle(if (dark) SlateDark else SlateLight, Icons.AutoMirrored.Filled.HelpOutline, status.orEmpty())
}

/**
 * 历史条目（规格 §2.4.3 `SessionTile`，出处 `app/lib/ui/session_tile.dart`）。
 *
 * 结构：38×38 圆底（底色 = 状态色 @13%）里放状态图标（识别中时换成转圈）
 * → 标题（失败时用状态色）→ 副标题 `时间 · 状态[ · 来自电脑]` → 右侧 chevron。
 *
 * `subtitle` 由 `SessionItem` 算好（含「来自电脑」的判据 `sourceDevice == "android-local"`），
 * **这里不再拼一遍** —— 同一段文案两处实现迟早会走偏。
 *
 * 删除入口按规格是「长按 + 右滑」两个（`onLongPress` 与 `Dismissible.confirmDismiss`）。
 * 这里给出 `onLongPress`；右滑那层留到历史 tab 换成 `LazyColumn` 时一起做
 *（当前 `RecordsScreen` 用的是普通 `Column`，包不了 `Dismissible`）。
 */
@Composable
@OptIn(ExperimentalFoundationApi::class)
fun SessionTile(
    session: SessionItem,
    dark: Boolean,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    onLongPress: (() -> Unit)? = null,
) {
    val style = statusStyleOf(session.status, dark)

    // 按下**不再改圆角**（用户口径 2026-09-28：「删除除了进入页面和返回页面外的所有动画」）。
    // 这里原来有一句 `animateDpAsState`：按下时圆角 16dp → 12dp 再弹回（学自 ImageToolbox 的
    // `shapeByInteraction`）。现在圆角是固定的，按下的反馈只剩触感与 M3 的水波纹。
    val interaction = remember { MutableInteractionSource() }

    Card(
        shape = RoundedCornerShape(QsTokens.radiusCard),
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface),
        modifier = modifier.fillMaxWidth().padding(bottom = 8.dp),
    ) {
        Row(
            modifier = Modifier
                .fillMaxWidth()
                // 单击进详情 + 长按删除：`clickable` 一次只吃一个手势，
                // 两个都要就用 `combinedClickable` —— 分两层挂会互相把对方吃掉。
                // 用 `hapticsCombinedClickable`：**震动包在点击里**，不靠调用点记得调
                // （学自 ImageToolbox：它的列表项单击/长按都会震一下，手感主要来自这里）。
                .hapticsCombinedClickable(
                    interactionSource = interaction,
                    onClick = onClick,
                    onLongClick = onLongPress,
                )
                .padding(start = 14.dp, top = 12.dp, end = 10.dp, bottom = 12.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Box(
                modifier = Modifier
                    .size(38.dp)
                    .background(style.color.copy(alpha = 0.13f), CircleShape),
                contentAlignment = Alignment.Center,
            ) {
                if (session.status == SessionStatus.Analyzing.wire) {
                    CircularProgressIndicator(
                        modifier = Modifier.padding(10.dp),
                        strokeWidth = 2.dp,
                        color = style.color,
                    )
                } else {
                    Icon(style.icon, contentDescription = style.label, tint = style.color, modifier = Modifier.size(19.dp))
                }
            }

            Spacer(Modifier.width(12.dp))

            Column(modifier = Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                Text(
                    text = session.headline,
                    style = MaterialTheme.typography.bodyMedium, fontFamily = FontFamily.Default,
                    fontWeight = FontWeight.SemiBold,
                    // 失败时标题用状态色（session_tile.dart 的 headlineUsesStatusColor）。
                    color = if (session.headlineUsesStatusColor) style.color else MaterialTheme.colorScheme.onSurface,
                    maxLines = 2,
                    overflow = TextOverflow.Ellipsis,
                )
                Text(
                    text = session.subtitle,
                    fontSize = 11.5.sp, fontFamily = FontFamily.Default,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
            }

            Spacer(Modifier.width(6.dp))
            Icon(
                Icons.Default.ChevronRight,
                contentDescription = null,
                tint = MaterialTheme.colorScheme.outline,
                modifier = Modifier.size(20.dp),
            )
        }
    }
}
