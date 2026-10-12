// QuizSync 设计系统（Android 侧）—— P2 组件层。
//
// 规矩（`docs/design-system-plan.md` §2）：
//   ① 页面里**不再出现**裸 `Card(...)` / `Slider(...)` / `AlertDialog(...)`，一律用 `Qs*`；
//   ② 间距 / 圆角 / 颜色**不许写字面量**，全部取 `QsTokens` 或 `quizPalette`；
//   ③ 字号不写 `sp` 字面量，取 `MaterialTheme.typography`（角色 → M3 槽位的映射见 `QuizTheme.kt`）。
//
// 为什么要有这一层：重构前页面直接拼 M3 原语，同一个「设置分组」在三个页面里被各写了一遍
// （`SettingsTab.Group` / `RecognitionSettingsScreen.SectionCard` / `HistoryTab` 里的裸 `Card`），
// 字号还各带一套小数（11.5 / 12.5 / 13.5sp）—— 那是 1.1.0 的 Flutter 像素对齐值，原生排版里没有意义。
package com.quizsync.android.dev.ui.designsystem

import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LargeTopAppBar
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.SegmentedButton
import androidx.compose.material3.SegmentedButtonDefaults
import androidx.compose.material3.SingleChoiceSegmentedButtonRow
import androidx.compose.material3.Slider
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.material3.TopAppBarScrollBehavior
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.input.VisualTransformation
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import com.quizsync.android.dev.QsTokens
import com.quizsync.android.dev.ui.haptics.rememberEnhancedHapticFeedback
import com.quizsync.android.dev.ui.haptics.toggle
import com.quizsync.android.dev.ui.theme.quizPalette
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp

// ============================================================================
// 分区卡片 / 段头
// ============================================================================

/**
 * 分区卡片：设置类页面的骨架。
 *
 * 圆角**不传** `shape` —— `MaterialTheme.shapes.medium` 已经由 `quizShapes()` 设成卡片圆角，
 * 而 M3 的 `Card` 默认形状正是 `CornerMedium`（字节码实证见 `QuizTheme.quizShapes()` 的注释）。
 * 卡片层级固定 0：1.1.0 的 `cardTheme.elevation` 就是 0，整站平铺 + 描边。
 */
@Composable
fun QsSectionCard(
    title: String,
    modifier: Modifier = Modifier,
    subtitle: String? = null,
    icon: ImageVector? = null,
    content: @Composable ColumnScope.() -> Unit = {},
) {
    val palette = quizPalette
    Card(
        modifier = modifier.fillMaxWidth(),
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface),
        border = BorderStroke(QsTokens.hairline, palette.cardStroke),
        elevation = CardDefaults.cardElevation(defaultElevation = QsTokens.Elevation.card),
    ) {
        Column(
            modifier = Modifier.padding(
                start = QsTokens.spaceLg,
                top = QsTokens.spaceLg,
                end = QsTokens.spaceLg,
                bottom = QsTokens.spaceLg,
            ),
            verticalArrangement = Arrangement.spacedBy(QsTokens.spaceMd),
        ) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                if (icon != null) {
                    Icon(
                        icon,
                        contentDescription = null,
                        tint = MaterialTheme.colorScheme.primary,
                        modifier = Modifier.size(QsTokens.iconSm),
                    )
                    Spacer(Modifier.width(QsTokens.spaceSm))
                }
                Column(modifier = Modifier.weight(1f)) {
                    Text(text = title, style = MaterialTheme.typography.titleSmall)
                    if (subtitle != null) {
                        Text(
                            text = subtitle,
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }
                }
            }
            content()
        }
    }
}

/**
 * 无标题卡片：列表项 / 内容块的外壳。
 *
 * 和 [QsSectionCard] 的区别只有「有没有标题行」—— 但两者都要在组件层把圆角、描边、
 * 层级、内边距定死，页面里才不会再出现裸 `Card(...)`。
 * `onClick` 给了就整卡可点（列表项），并走 M3 的 `Card` 交互态。
 *
 * `border` 默认取 `cardStroke`（装饰描边）；需要「警示卡」这类**功能性**描边时
 * 显式传一个用 `outline`/状态色的 `BorderStroke` —— 那边界必须能看清（对比度不低于 3:1）。
 */
@Composable
fun QsCard(
    modifier: Modifier = Modifier,
    onClick: (() -> Unit)? = null,
    contentPadding: PaddingValues = PaddingValues(QsTokens.spaceLg),
    border: BorderStroke? = BorderStroke(QsTokens.hairline, quizPalette.cardStroke),
    content: @Composable ColumnScope.() -> Unit,
) {
    val colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface)
    val elevation = CardDefaults.cardElevation(defaultElevation = QsTokens.Elevation.card)
    if (onClick != null) {
        Card(
            onClick = onClick,
            modifier = modifier.fillMaxWidth(),
            colors = colors,
            border = border,
            elevation = elevation,
        ) {
            Column(modifier = Modifier.padding(contentPadding), content = content)
        }
    } else {
        Card(
            modifier = modifier.fillMaxWidth(),
            colors = colors,
            border = border,
            elevation = elevation,
        ) {
            Column(modifier = Modifier.padding(contentPadding), content = content)
        }
    }
}

/**
 * 警示卡：`errorContainer` 半透明底、**无描边**的卡片外壳（规格 §2.1 的「相机权限未授予」与页底错误提示）。
 *
 * 它就是 M3 [Card] 加一层底色，和 [QsCard] 的唯一区别是**底色**：警示卡要用错误容器色，且规格要求半透明。
 * 底色取 `MaterialTheme.colorScheme.errorContainer`（跟随明暗主题，不写颜色字面量），
 * `0.5f` 是规格写死的半透明系数（用法与 `QsTag` / `QsStateView` 里的 alpha 一致）。
 * 圆角与层级沿用 M3 默认 —— `quizShapes()` 已把卡片圆角设成 16dp、层级 0（见 §8.2 ③ 的字节码实证）。
 *
 * **内边距与内容行留在调用点**：两张警示卡的内容不一样（一张只有「图标 + 文字」，
 * 另一张下面还要放「重新申请 / 去系统设置」两个按钮），而 14dp 内距 / 10dp 图标间距是规格 §2.1 的
 * **内容指标**，`QsTokens` 的间距阶梯里没有这两个值（只有 12 / 16）——
 * 按 `docs/design-system-plan.md` §8.4 划的边界，硬套令牌会改变观感，所以暂时保留字面量。
 */
@Composable
fun QsErrorCard(
    modifier: Modifier = Modifier,
    content: @Composable ColumnScope.() -> Unit,
) {
    Card(
        modifier = modifier.fillMaxWidth(),
        colors = CardDefaults.cardColors(
            containerColor = MaterialTheme.colorScheme.errorContainer.copy(alpha = 0.5f),
        ),
    ) {
        content()
    }
}

/** 段头：不在卡片里的分组标题（列表页用）。 */@Composable
fun QsSectionHeader(text: String, modifier: Modifier = Modifier) {
    Text(
        text = text,
        style = MaterialTheme.typography.labelLarge,
        color = MaterialTheme.colorScheme.onSurfaceVariant,
        modifier = modifier.padding(
            start = QsTokens.spaceXs,
            top = QsTokens.spaceSm,
            bottom = QsTokens.spaceXs,
        ),
    )
}

// ============================================================================
// 设置行
// ============================================================================

/**
 * 通用设置行：左标题（+说明），右尾部控件。
 *
 * `onClick` 给了就整行可点（用于「进入二级页」这种行），否则只有尾部控件可交互。
 */
@Composable
fun QsSettingRow(
    title: String,
    modifier: Modifier = Modifier,
    description: String? = null,
    icon: ImageVector? = null,
    onClick: (() -> Unit)? = null,
    trailing: @Composable (() -> Unit)? = null,
) {
    val base = if (onClick != null) modifier.fillMaxWidth().clickable(onClick = onClick) else modifier.fillMaxWidth()
    Row(
        modifier = base.padding(vertical = QsTokens.spaceSm),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        if (icon != null) {
            Icon(
                icon,
                contentDescription = null,
                tint = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.size(QsTokens.iconMd),
            )
            Spacer(Modifier.width(QsTokens.spaceMd))
        }
        Column(modifier = Modifier.weight(1f)) {
            Text(text = title, style = MaterialTheme.typography.bodyLarge)
            if (description != null) {
                Text(
                    text = description, fontFamily = FontFamily.Default,
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }
        if (trailing != null) {
            Spacer(Modifier.width(QsTokens.spaceMd))
            trailing()
        }
    }
}

/**
 * 开关设置行。
 *
 * 触感走 `EnhancedHaptics.toggle`（学自 ImageToolbox 的 EnhancedSwitch：开/关分别发
 * ToggleOn/ToggleOff），并保证**触感先于状态变化**，手感才跟手。
 */
@Composable
fun QsSwitchRow(
    title: String,
    checked: Boolean,
    onCheckedChange: (Boolean) -> Unit,
    modifier: Modifier = Modifier,
    description: String? = null,
    enabled: Boolean = true,
) {
    val haptics = rememberEnhancedHapticFeedback()
    QsSettingRow(
        title = title,
        description = description,
        modifier = modifier,
        trailing = {
            Switch(
                checked = checked,
                enabled = enabled,
                onCheckedChange = {
                    haptics.toggle(it)
                    onCheckedChange(it)
                },
            )
        },
    )
}

/** 滑杆设置行：左标题、右当前值，下面整条滑杆。 */
@Composable
fun QsSliderRow(
    title: String,
    value: Float,
    valueRange: ClosedFloatingPointRange<Float>,
    onValueChange: (Float) -> Unit,
    modifier: Modifier = Modifier,
    steps: Int = 0,
    valueLabel: String = value.toInt().toString(),
    minLabel: String? = null,
    maxLabel: String? = null,
) {
    Column(modifier = modifier.fillMaxWidth()) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text(text = title, style = MaterialTheme.typography.bodyLarge, modifier = Modifier.weight(1f))
            Text(
                text = valueLabel,
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
        Row(verticalAlignment = Alignment.CenterVertically) {
            if (minLabel != null) {
                Text(minLabel, style = MaterialTheme.typography.bodySmall)
            }
            Slider(
                value = value,
                onValueChange = onValueChange,
                valueRange = valueRange,
                steps = steps,
                modifier = Modifier.weight(1f).padding(horizontal = QsTokens.spaceSm),
            )
            if (maxLabel != null) {
                Text(maxLabel, style = MaterialTheme.typography.bodySmall)
            }
        }
    }
}

/**
 * 行内滑杆（标签在左、当前值在右，与滑杆同一行）—— 规格 §5.3 的 `_sliderRow` 形态。
 *
 * 与 [QsSliderRow] 只是**版式**不同（那个是「标题行 + 下方整条滑杆」的块状版式），
 * 语义上这条多一条硬要求：**松手才提交**。
 * 拖动期间只改本地草稿，`onValueChangeFinished` 才回调 —— 识别设置页的透明度/大小/最多页数
 * 都会落库并广播同步 op，拖动过程中每帧写一次会产生一串没有意义的写入。
 *
 * 原先这三行是 `RecognitionSettingsScreen` 里的私有 `SliderRow`（页面里裸用 `Slider`），
 * 按 P2 的规矩（页面只用 `Qs*`）收进这里，版式与语义**逐字不动**。
 */
@Composable
fun QsInlineSliderRow(
    label: String,
    value: Float,
    range: ClosedFloatingPointRange<Float>,
    display: String,
    onChangeEnd: (Float) -> Unit,
    modifier: Modifier = Modifier,
    steps: Int = 0,
) {
    var draft by remember(value) { mutableFloatStateOf(value) }
    Row(verticalAlignment = Alignment.CenterVertically, modifier = modifier.fillMaxWidth()) {
        Box(Modifier.width(QsTokens.sliderLabelWidth)) {
            Text(label, style = MaterialTheme.typography.bodyLarge)
        }
        Slider(
            value = draft,
            onValueChange = { draft = it },
            onValueChangeFinished = { onChangeEnd(draft) },
            valueRange = range,
            steps = steps,
            modifier = Modifier.weight(1f),
        )
        Box(Modifier.width(QsTokens.sliderValueWidth)) {
            Text(
                display,
                style = MaterialTheme.typography.bodySmall,
                modifier = Modifier.fillMaxWidth(),
                textAlign = TextAlign.End,
            )
        }
    }
}

/**
 * 单选项（分段按钮）：M3 原生的 [SingleChoiceSegmentedButtonRow]。
 *
 * 换掉原来的「一排 FilterChip」：分段按钮在 Material 3 里就是「互斥的少量选项」这个语义，
 * 可点区域更连续、选中态是原生那套（chip 的选中态是过滤语义，用在这里不对）。
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun <T> QsChoiceRow(
    options: List<T>,
    selected: T,
    onSelect: (T) -> Unit,
    label: (T) -> String,
    modifier: Modifier = Modifier,
) {
    SingleChoiceSegmentedButtonRow(modifier = modifier.fillMaxWidth()) {
        options.forEachIndexed { index, option ->
            SegmentedButton(
                selected = option == selected,
                onClick = { onSelect(option) },
                shape = SegmentedButtonDefaults.itemShape(index = index, count = options.size),
                label = { Text(label(option), maxLines = 1, overflow = TextOverflow.Ellipsis) },
            )
        }
    }
}

// ============================================================================
// 标签 / 状态
// ============================================================================

/**
 * 能力标签 / 状态药丸：圆角全胶囊（`AppRadius.chip`）、描边用 `outline`
 * （**功能边界**要用 outline，`cardStroke` 对比度天生很低，是装饰用的）。
 */
@Composable
fun QsTag(
    text: String,
    color: Color,
    modifier: Modifier = Modifier,
    icon: ImageVector? = null,
    filled: Boolean = true,
) {
    Row(
        modifier = modifier
            .background(
                color = if (filled) color.copy(alpha = 0.14f) else Color.Transparent,
                shape = RoundedCornerShape(QsTokens.radiusChip),
            )
            .border(
                width = QsTokens.hairline,
                color = color.copy(alpha = if (filled) 0.35f else 0.5f),
                shape = RoundedCornerShape(QsTokens.radiusChip),
            )
            .padding(horizontal = QsTokens.spaceSm, vertical = QsTokens.spaceXs),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        if (icon != null) {
            Icon(icon, contentDescription = null, tint = color, modifier = Modifier.size(QsTokens.iconXs))
            Spacer(Modifier.width(QsTokens.spaceXs))
        }
        Text(
            text = text,
            color = color,
            style = MaterialTheme.typography.labelMedium,
            maxLines = 1,
        )
    }
}

/**
 * 选项字母徽标（**圆底 + 圈内字母居中**）—— 结果页选项区与「设置 → 题目字号预览」共用。
 *
 * 几何按用户 2026-09-28 的口径定死：**圆圈居中、字母在圆圈里居中**。
 * 做法是给徽标一个「宽 = 圆直径（`fontSize × 1.25`）、高 = 正文行高（`fontSize × 1.5`）」的盒子，
 * 圆与字母都在盒子里居中 ⇒ 圆心 = **首行行心**，字母 = 圆心。
 *
 * 为什么高度取行高而不是取直径：选项行是 `Alignment.Top`（多行选项时徽标要贴着首行），
 * 盒子高度必须与正文首行同高，圆心才会落在首行中线；只按直径做盒子会让圆整体偏高 ——
 * 这正是用户报的「圆圈过高」。早期版本用两个 `offset`（圆下移 2.5dp、字母上移 1.5dp）
 * 硬凑位置，两个偏移互相耦合，改一个就得改另一个。
 */
@Composable
fun QsOptionBadge(
    label: String,
    fontSize: Int,
    textColor: Color,
    circleColor: Color,
    modifier: Modifier = Modifier,
) {
    Box(
        modifier = modifier.size(width = (fontSize * 1.25f).dp, height = (fontSize * 1.5f).dp),
        contentAlignment = Alignment.Center,
    ) {
        Box(
            modifier = Modifier
                .size((fontSize * 1.25f).dp)
                .background(circleColor, CircleShape),
        )
        Text(
            text = label,
            fontSize = (fontSize * 0.78f).sp,
            fontWeight = FontWeight.Bold,
            color = textColor,
        )
    }
}

/**
 * 统一状态页：空 / 错误 / 加载都用它。
 *
 * - `loading = true` → 圆形底上换成进度环（此时 [icon] 不用给）；
 * - 否则给 [icon] 显示图标；两者都没有就只显示标题与说明；
 * - `error = true` → 图标与标题走 error 色（出错和「暂时没有内容」不是同一种语气）；
 * - 想在剩余空间里垂直居中就自己传 `Modifier.fillMaxSize()`（组件不替调用方决定占地）。
 *
 * `fillMaxWidth()` 必须在组件里给：放进 `LazyColumn` 的 `item {}` 时 Column 宽度是包裹内容的，
 * 调用点补不出来（踩过：空态提示全部靠左）。
 */
@Composable
fun QsStateView(
    title: String,
    modifier: Modifier = Modifier,
    message: String? = null,
    icon: ImageVector? = null,
    loading: Boolean = false,
    error: Boolean = false,
    actions: (@Composable () -> Unit)? = null,
) {
    val accent = if (error) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.primary
    Column(
        modifier = modifier.fillMaxWidth().padding(QsTokens.spaceXl),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center,
    ) {
        if (loading || icon != null) {
            Box(
                modifier = Modifier
                    .size(QsTokens.stateBadge)
                    .background(accent.copy(alpha = 0.10f), CircleShape),
                contentAlignment = Alignment.Center,
            ) {
                if (loading) {
                    CircularProgressIndicator(
                        modifier = Modifier.size(QsTokens.iconXl),
                        strokeWidth = QsTokens.progressStroke,
                        color = accent,
                    )
                } else if (icon != null) {
                    Icon(
                        icon,
                        contentDescription = null,
                        tint = accent.copy(alpha = 0.9f),
                        modifier = Modifier.size(QsTokens.iconXl),
                    )
                }
            }
            Spacer(Modifier.height(QsTokens.spaceLg))
        }
        Text(
            text = title,
            style = MaterialTheme.typography.titleMedium,
            color = if (error) MaterialTheme.colorScheme.error else Color.Unspecified,
            textAlign = TextAlign.Center,
        )
        if (message != null) {
            Spacer(Modifier.height(QsTokens.spaceSm))
            Text(
                text = message,
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                textAlign = TextAlign.Center,
            )
        }
        if (actions != null) {
            Spacer(Modifier.height(QsTokens.spaceLg))
            actions()
        }
    }
}

// ============================================================================
// 页面顶栏 / 对话框
// ============================================================================

/**
 * 二级页顶栏。
 *
 * **实现是 M3 原生的 [TopAppBar]，不是手搓的 Row** —— 这一点是刻意的：
 * 顶栏要处理状态栏 insets、滚动容器颜色、折叠行为，自绘一份等于把原生行为重实现一遍
 * 而且实现不全（第一版我确实写成了 Row，已改回）。
 * 这一层只负责把「标题字号、返回图标、配色」定成一处。
 *
 * ⚠️ **签名里刻意没有 `scrollBehavior`**：它的类型 `TopAppBarScrollBehavior` 在 material3 1.3.2 上
 * 是实验性 API，一旦出现在这里的签名里，**每个调用点**都会被要求 opt-in（实测报
 * `This material API is experimental`，位置指在调用处而不是这里）。
 * 需要可折叠大标题的页面用 [QsCollapsibleTopBar]，并在那一页自己加一次 opt-in。
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun QsTopBar(
    title: String,
    modifier: Modifier = Modifier,
    onBack: (() -> Unit)? = null,
    actions: (@Composable RowScope.() -> Unit)? = null,
) {
    TopAppBar(
        modifier = modifier,
        title = { QsTopBarTitle(title) },
        navigationIcon = {
            if (onBack != null) {
                IconButton(onClick = onBack) {
                    Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = "返回")
                }
            }
        },
        actions = actions ?: {},
        colors = TopAppBarDefaults.topAppBarColors(
            containerColor = MaterialTheme.colorScheme.surface,
            scrolledContainerColor = MaterialTheme.colorScheme.surface,
        ),
    )
}

/**
 * 可折叠大标题顶栏（滚动时收成一行）—— 目前只有结果页在用。
 *
 * 用 [TopAppBarDefaults.exitUntilCollapsedScrollBehavior] 配合列表的 `nestedScroll`：
 * 目的是「把纵向空间还给内容」，不是为了好看。调用方需要自己加
 * `@OptIn(ExperimentalMaterial3Api::class)`（见 [QsTopBar] 的说明）。
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun QsCollapsibleTopBar(
    title: String,
    scrollBehavior: TopAppBarScrollBehavior,
    modifier: Modifier = Modifier,
    onBack: (() -> Unit)? = null,
    actions: (@Composable RowScope.() -> Unit)? = null,
) {
    LargeTopAppBar(
        modifier = modifier,
        title = { QsTopBarTitle(title) },
        navigationIcon = {
            if (onBack != null) {
                IconButton(onClick = onBack) {
                    Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = "返回")
                }
            }
        },
        actions = actions ?: {},
        colors = TopAppBarDefaults.topAppBarColors(
            containerColor = MaterialTheme.colorScheme.surface,
            scrolledContainerColor = MaterialTheme.colorScheme.surface,
        ),
        scrollBehavior = scrollBehavior,
    )
}

/** 两版顶栏共用的标题：单行 + 省略号，字号取 M3 的 `titleLarge`（不写 sp 字面量）。 */
@Composable
private fun QsTopBarTitle(title: String) {
    Text(
        text = title,
        style = MaterialTheme.typography.titleLarge,
        maxLines = 1,
        overflow = TextOverflow.Ellipsis,
    )
}

/**
 * 二次确认对话框（删除记录 / 合集这类不可逆操作）。
 *
 * 统一在这里，是为了三处调用点的按钮顺序与文案一致 —— 之前每处各写一遍 `AlertDialog`，
 * 「确认」在左还是在右都不一样。
 */
@Composable
fun QsConfirmDialog(
    title: String,
    text: String,
    confirmLabel: String,
    onConfirm: () -> Unit,
    onDismiss: () -> Unit,
    dismissLabel: String = "取消",
    destructive: Boolean = true,
) {
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(title) },
        text = { Text(text) },
        confirmButton = {
            TextButton(onClick = onConfirm) {
                Text(
                    text = confirmLabel,
                    color = if (destructive) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.primary,
                )
            }
        },
        dismissButton = {
            TextButton(onClick = onDismiss) { Text(dismissLabel) }
        },
    )
}

// ============================================================================
// 输入与按钮（M3 原语的薄壳）
// ============================================================================

/**
 * 文本输入框：M3 [OutlinedTextField] 的薄壳（配对页的三个框：主机地址 / 端口 / 6 位配对码）。
 *
 * 为什么要有它：页面里不出现裸 `OutlinedTextField(...)` 是 P2 的硬规矩①（见本文件头）。
 * 而**外观刻意一仍其旧** —— 形状仍是 M3 默认的 `MaterialTheme.shapes.extraSmall`、配色仍是 M3 默认：
 * 配对页此前没有显式指定过这两项，包一层不能顺手改掉观感（§8.4 的边界：不为过规矩而改观感）。
 * 要统一成 `QsTokens.radiusControl` 是**规格层面**的决定，得先落到 `design/tokens.json` 与规格里。
 *
 * 参数与 M3 一一对应（连默认值都照抄），只是把签名收窄到本 App 用得到的那几个：
 * `label` / `placeholder` / `trailingIcon` 保持 M3 的组合槽，调用点写的仍是
 * `label = { Text("端口") }` 或 `placeholder = { Text("搜索题干与解析") }`。
 *
 * 形状**不**在这里定死（与 [QsPrimaryButton] / [QsSecondaryButton] 不同）：历史页搜索框原先手写的
 * `shape = RoundedCornerShape(QsTokens.radiusControl)` 与 M3 默认值**相等** ——
 * `OutlinedTextField` 默认形状是 `MaterialTheme.shapes.extraSmall`，而 `quizShapes()` 正把它设成
 * `radiusControl`（见 `QuizTheme.quizShapes()`），所以调用点那一行是冗余的，删掉观感不变。
 */
@Composable
fun QsTextField(
    value: String,
    onValueChange: (String) -> Unit,
    modifier: Modifier = Modifier,
    label: (@Composable () -> Unit)? = null,
    placeholder: (@Composable () -> Unit)? = null,
    enabled: Boolean = true,
    singleLine: Boolean = false,
    isError: Boolean = false,
    keyboardOptions: KeyboardOptions = KeyboardOptions.Default,
    visualTransformation: VisualTransformation = VisualTransformation.None,
    trailingIcon: (@Composable () -> Unit)? = null,
) {
    OutlinedTextField(
        value = value,
        onValueChange = onValueChange,
        modifier = modifier,
        enabled = enabled,
        label = label,
        placeholder = placeholder,
        trailingIcon = trailingIcon,
        isError = isError,
        visualTransformation = visualTransformation,
        keyboardOptions = keyboardOptions,
        singleLine = singleLine,
    )
}

/**
 * 主按钮：M3 [Button] 的薄壳，**形状在组件里定死**为 `RoundedCornerShape(QsTokens.radiusControl)`。
 *
 * 为什么形状必须写死在这一层：`ButtonDefaults.shape` 走的是 M3 的 **`CornerFull`（胶囊形）**，
 * 与全局 `Shapes` 无关（字节码实证见 `docs/design-system-plan.md` §8.2 ③），
 * 而 1.1.0 的按钮圆角契约是 **10dp**（`QsTokens.radiusControl`）。页面此前每处都手写一次
 * `shape = …`，漏一处就掉回胶囊形 —— 圆角属于「共享词汇」，收进组件层后调用点不必再知道它。
 *
 * 其余一切照 [Button]：宽高由调用点的 `modifier` 决定（本页既有 `height(46.dp)` 的主按钮，
 * 也有默认高的行内按钮），配色走 M3 默认（`primary` 容器 + `onPrimary` 文字）。
 */
@Composable
fun QsPrimaryButton(
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
    content: @Composable RowScope.() -> Unit,
) {
    Button(
        onClick = onClick,
        modifier = modifier,
        enabled = enabled,
        shape = RoundedCornerShape(QsTokens.radiusControl),
        content = content,
    )
}

/**
 * 次按钮：M3 [OutlinedButton] 的薄壳 —— 与 [QsPrimaryButton] 只差「描边还是填充」，
 * 形状同样定死为 `RoundedCornerShape(QsTokens.radiusControl)`（理由见 [QsPrimaryButton]）；
 * 描边用 M3 默认的 `outlinedButtonBorder`（禁用态会自己变淡，不要在这里改成固定色）。
 */
@Composable
fun QsSecondaryButton(
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
    content: @Composable RowScope.() -> Unit,
) {
    OutlinedButton(
        onClick = onClick,
        modifier = modifier,
        enabled = enabled,
        shape = RoundedCornerShape(QsTokens.radiusControl),
        content = content,
    )
}

/**
 * 次级强调按钮：M3 [FilledTonalButton] 的薄壳（结果页空态的「重新生成」）。
 *
 * 与 [QsSecondaryButton] 只差「填充还是描边」。形状同样取 `RoundedCornerShape(QsTokens.radiusControl)`：
 * 这两个按钮在结果页里**并排**出现，一个 10dp 圆角、一个是 M3 默认胶囊，看起来像坏掉的排版；
 * 而 10dp 本来就是 1.1.0 的按钮圆角契约（见 [QsPrimaryButton] 的说明）。
 */
@Composable
fun QsTonalButton(
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
    content: @Composable RowScope.() -> Unit,
) {
    FilledTonalButton(
        onClick = onClick,
        modifier = modifier,
        enabled = enabled,
        shape = RoundedCornerShape(QsTokens.radiusControl),
        content = content,
    )
}

/**
 * 文本按钮：M3 [TextButton] 的薄壳（会话切换条的「上一次 / 下一次识别」、当前任务页的
 * 「选择合集 / 知道了 / 忽略」）。
 *
 * 为什么需要它：`TextButton` 是页面里第三个高频 M3 按钮原语，而 P2 的硬规矩①（见本文件头）是
 * **页面里不出现裸 M3 原语** —— 只包 `Button` / `OutlinedButton` 的话，这些调用点无处可去。
 *
 * 与 M3 原语的关系，以及**为什么这里不写 `shape`**：[QsPrimaryButton] / [QsSecondaryButton] 必须把
 * 形状定死成 `RoundedCornerShape(QsTokens.radiusControl)`，是因为 M3 的 `ButtonDefaults.shape` 走
 * `CornerFull`（胶囊形），而 1.1.0 的按钮圆角契约是 10dp。文本按钮**没有**这条契约：
 * `TextButton` 默认走 `ButtonDefaults.textShape`（= `MaterialTheme.shapes.small`），
 * 这些调用点此前一处 `shape` 都没写过，配色也是 M3 默认（容器透明、文字 `primary`、禁用态自动变淡）
 * —— 包一层不能顺手改掉观感（§8.4 的边界：不为过规矩而改观感），所以形状与配色**一律保持 M3 默认**。
 *
 * 其余一切照 [TextButton]：`enabled` 原样透传，`content` 仍是 `RowScope` 槽
 * （调用点可以照旧 `Icon` + `Spacer` + `Text`，也可以把 `Modifier.align(...)` 交给 `modifier`）。
 */
@Composable
fun QsTextButton(
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
    content: @Composable RowScope.() -> Unit,
) {
    TextButton(
        onClick = onClick,
        modifier = modifier,
        enabled = enabled,
        content = content,
    )
}
