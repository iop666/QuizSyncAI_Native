// 规格 §4.3 的浮层 + §3.1 的常驻免责声明 + §2.8 里**还没被设计系统取代**的共享件。
//
// **P2 重构（2.0）**：本文件原来还有三个组件 —— `SectionCard` / `StatusPill` / `EmptyState`。
// 它们各自带一套 1.1.0 的小数字号（11.5 / 12.5 / 13.8sp）与硬编码间距，而且和页面里的私有副本
// （`SettingsTab.Group`、`RecognitionSettingsScreen.SectionCard`）是两套写法。
// 现在统一收进 `ui/designsystem/QsComponents.kt`：
//   SectionCard → QsSectionCard     StatusPill → QsTag     EmptyState → QsStateView
// 本文件只留**领域件**（下一轮浮层、免责声明、Snackbar 宿主、消息副作用）与分隔线。
package com.quizsync.android.dev.ui.components

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import com.quizsync.android.dev.QsTokens
import com.quizsync.android.dev.ui.theme.quizPalette

/**
 * 「下一轮识别」浮层（§4.3）。
 *
 * `opacity`：当前任务页用默认 1，结果页用 0.85（`kResultBannerOpacity`）。
 * 只淡**底色**，文字始终完全不透明（M18 的返工结论）。
 */
@Composable
fun NextRoundBanner(
    text: String,
    spinning: Boolean,
    modifier: Modifier = Modifier,
    opacity: Float = 1f,
) {
    Card(
        modifier = modifier.fillMaxWidth(),
        shape = RoundedCornerShape(QsTokens.radiusCard),
        colors = CardDefaults.cardColors(
            containerColor = MaterialTheme.colorScheme.primaryContainer.copy(alpha = opacity),
        ),
        elevation = CardDefaults.cardElevation(defaultElevation = QsTokens.Elevation.overlay),
    ) {
        Row(
            modifier = Modifier.padding(horizontal = QsTokens.spaceLg, vertical = QsTokens.spaceSm),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            if (spinning) {
                CircularProgressIndicator(
                    modifier = Modifier.size(QsTokens.iconSm),
                    strokeWidth = QsTokens.progressStroke,
                    color = MaterialTheme.colorScheme.onPrimaryContainer,
                )
            } else {
                Icon(
                    imageVector = Icons.Filled.CheckCircle,
                    contentDescription = null,
                    tint = MaterialTheme.colorScheme.onPrimaryContainer,
                    modifier = Modifier.size(QsTokens.iconSm),
                )
            }
            Spacer(Modifier.width(QsTokens.spaceSm))
            Text(
                text = text,
                style = MaterialTheme.typography.titleSmall,
                color = MaterialTheme.colorScheme.onPrimaryContainer,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                modifier = Modifier.weight(1f),
            )
        }
    }
}

/** 常驻免责声明（§1.4 / §3.1）：最小一档字号、次要色。 */
@Composable
fun Disclaimer(modifier: Modifier = Modifier) {
    Text(
        text = "答案由 AI 生成，仅供参考",
        style = MaterialTheme.typography.labelSmall,
        color = MaterialTheme.colorScheme.onSurfaceVariant,
        textAlign = TextAlign.Center,
        modifier = modifier.fillMaxWidth().padding(QsTokens.spaceSm),
    )
}

/** 1px 分隔线（列表条目之间用；颜色 = §6.2 的卡片描边色）。 */
@Composable
fun HairLineDivider(modifier: Modifier = Modifier) {
    Box(modifier = modifier.fillMaxWidth().height(QsTokens.hairline).background(quizPalette.cardStroke))
}

/**
 * 把 `QuizUiState.message` 显示成 Snackbar，显示完调 `onShown` 清掉。
 *
 * **这一段原来是缺的**：ViewModel 里每个 `notify(...)` 都往 `message` 里写，
 * 但没有任何界面读它 —— 于是「已删除这条识别记录」这类提示全是静默的，
 * 用户看到的是「操作了但什么都没发生」。
 *
 * 用 `messageId` 当 key：同一条文案重复出现时也要再弹一次（连续删两条记录文案是一样的）。
 */
@Composable
fun MessageEffect(
    hostState: SnackbarHostState,
    message: String?,
    messageId: Long,
    onShown: () -> Unit,
) {
    LaunchedEffect(messageId) {
        if (message != null) {
            // 规格 §1.4：**前台只保留最新一条**（Flutter 侧是 `clearSnackBars()` 后显示）。
            // 不先关掉的话 `showSnackbar` 会排队，连着删三条记录就要等前两条各显完才轮到第三条，
            // 用户看到的是一串过期的提示。
            hostState.currentSnackbarData?.dismiss()
            hostState.showSnackbar(message)
            onShown()
        }
    }
}

/**
 * 浮动提示条（规格 §1.4：**浮动、圆角 10、反色底上的正文色**）。
 *
 * 为什么不用 M3 默认的 `Snackbar`：它贴满宽度、圆角很小、文字用主题色 —— 三条都不符规格。
 * 这里按规格自己拼一个：左右留 16dp 的浮动边距、圆角 10dp、白字 13.5sp。
 *
 * 颜色取 `inverseSurface` / `inverseOnSurface` 这一对（浅色主题下是深底白字，深色主题下自动反过来）。
 * 1.1.0 规格里写的是「白字」—— 那是**相对于那个深底**说的；
 * 直接写 `Color.White` 会在深色主题下变成浅底白字，一个字都看不见。
 */
@Composable
fun QuizSnackbarHost(hostState: SnackbarHostState, modifier: Modifier = Modifier) {
    SnackbarHost(hostState = hostState, modifier = modifier) { data ->
        Row(
            modifier = Modifier
                .fillMaxWidth()
                // 浮动：左右各留边距，别贴满屏幕（规格里的 floating）。
                .padding(horizontal = QsTokens.spaceLg, vertical = QsTokens.spaceSm)
                .background(
                    color = MaterialTheme.colorScheme.inverseSurface,
                    shape = RoundedCornerShape(QsTokens.radiusControl),
                )
                .padding(horizontal = QsTokens.spaceLg, vertical = QsTokens.spaceMd),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Text(
                text = data.visuals.message,
                color = MaterialTheme.colorScheme.inverseOnSurface,
                style = MaterialTheme.typography.bodyMedium,
            )
        }
    }
}
