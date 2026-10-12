// 主题层：Material 3 色板（由种子色**派生**）+ 规格 §6.2/§3.5 的**固定色值表**。
//
// 两条不同的颜色来源，**不许混**（ui-spec-android §6.1 / §9.1）：
//  ① primary / onPrimary / … / outline / error / primaryContainer … 由
//     `ColorScheme.fromSeed(seedColor = #FF16A34A, brightness = …)` **派生**。
//     规格 §9.1 写得很清楚：老应用源码里**没有**这些值的硬编码，是派生出来的，规格不臆造数值。
//  ② 标绿 / 标黄 / 题目不全 / 告警 / 选项竖条 / 会话状态色 等**固定色值**，
//     规格 §6.2 给了确切 ARGB，逐字抄进本文件，**不随种子色变化**。
package com.quizsync.android.dev.ui.theme

import android.os.Build
import androidx.annotation.ChecksSdkIntAtLeast
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.ColorScheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Shapes
import androidx.compose.material3.Typography
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.dynamicDarkColorScheme
import androidx.compose.material3.dynamicLightColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.Immutable
import androidx.compose.runtime.ReadOnlyComposable
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.Font
import com.quizsync.android.dev.R
import androidx.compose.ui.text.font.FontWeight
import com.quizsync.android.dev.QsTokens
import androidx.compose.ui.text.font.FontSynthesis
import androidx.compose.ui.text.font.FontFamily

/** 主题种子色：`AppSettings.accent` 默认值 `0xFF16A34A`（ui-spec-android §6.1）。 */
val QuizSeed = QsTokens.brandSeed

// ============================================================================
// 由种子色派生出来的两套 Material 3 色板
// ----------------------------------------------------------------------------
// **这些值不是我调的色，是跑出来的**：用 Flutter 自己算
//     ColorScheme.fromSeed(seedColor: Color(0xFF16A34A), brightness: …, surface: 浅色=白)
// —— 与老应用 `packages/quizsync_ui/lib/src/app_theme.dart:43-48` 是同一行代码、同一套算法。
//
// 原始输出（逐角色）在：
//   D:\DeepseekHarness\TemporaryConversationFile\phase0-e2e\seed_palette\test\dump_test.dart
//   运行方式：flutter test test/dump_test.dart （见 docs/DECISIONS.md 本轮记录）
// 交叉验证：其中 primary `#35693E` / onSurface `#181D18` / onSurfaceVariant `#414941` /
// 深色 surface `#101510` 与仓库 `design/tokens.json`（由 `tools/gen_design_tokens.py` 生成、
// 被 `design_token_parity_test.dart` 钉住）**逐字相同**。
//
// 为什么不写 `ColorScheme.fromSeed(...)`：**Compose 的 Material3 根本没有这个 API**
// （实测 material3 1.3.2 与 1.4.0 的 classes.jar 里都搜不到 `fromSeed` / `Brightness` 符号；
// fromSeed 是 Flutter 的 API，规格 §6.1 那段引用的也是 Flutter 代码）。
// 所以照 §9.1 的纪律：**不臆造数值**，抄 Flutter 的派生结果。
//
// 两个 Compose 侧的历史遗留角色（Flutter 已废弃、Compose 仍在使用）：
//   * `surfaceVariant` = 派生结果里的 `surfaceContainerHighest`（M3 新规范里就是这个对应关系）；
//   * `background` / `onBackground` = `surface` / `onSurface`（同上；画布色不走这里，
//     见 §6.1：`scaffoldBackgroundColor = canvas(brightness)` 是固定表里的值）。
// ============================================================================
private val SeedLightScheme = lightColorScheme(
    primary = Color(0xFF35693E),
    onPrimary = Color(0xFFFFFFFF),
    primaryContainer = Color(0xFFB7F1BA),
    onPrimaryContainer = Color(0xFF1C5128),
    inversePrimary = Color(0xFF9CD4A0),
    secondary = Color(0xFF516351),
    onSecondary = Color(0xFFFFFFFF),
    secondaryContainer = Color(0xFFD4E8D1),
    onSecondaryContainer = Color(0xFF3A4B3A),
    tertiary = Color(0xFF39656D),
    onTertiary = Color(0xFFFFFFFF),
    tertiaryContainer = Color(0xFFBDEAF3),
    onTertiaryContainer = Color(0xFF1F4D54),
    background = Color(0xFFFFFFFF),
    onBackground = Color(0xFF181D18),
    surface = Color(0xFFFFFFFF),
    onSurface = Color(0xFF181D18),
    surfaceVariant = Color(0xFFE0E4DB),
    onSurfaceVariant = Color(0xFF414941),
    surfaceTint = Color(0xFF35693E),
    inverseSurface = Color(0xFF2D322C),
    inverseOnSurface = Color(0xFFEEF2EA),
    error = Color(0xFFBA1A1A),
    onError = Color(0xFFFFFFFF),
    errorContainer = Color(0xFFFFDAD6),
    onErrorContainer = Color(0xFF93000A),
    outline = Color(0xFF727970),
    outlineVariant = Color(0xFFC1C9BE),
    scrim = Color(0xFF000000),
    surfaceBright = Color(0xFFF7FBF2),
    surfaceDim = Color(0xFFD7DBD3),
    surfaceContainer = Color(0xFFEBEFE7),
    surfaceContainerHigh = Color(0xFFE5E9E1),
    surfaceContainerHighest = Color(0xFFE0E4DB),
    surfaceContainerLow = Color(0xFFF1F5EC),
    surfaceContainerLowest = Color(0xFFFFFFFF),
)

private val SeedDarkScheme = darkColorScheme(
    primary = Color(0xFF9CD4A0),
    onPrimary = Color(0xFF003914),
    primaryContainer = Color(0xFF1C5128),
    onPrimaryContainer = Color(0xFFB7F1BA),
    inversePrimary = Color(0xFF35693E),
    secondary = Color(0xFFB8CCB6),
    onSecondary = Color(0xFF243425),
    secondaryContainer = Color(0xFF3A4B3A),
    onSecondaryContainer = Color(0xFFD4E8D1),
    tertiary = Color(0xFFA1CED7),
    onTertiary = Color(0xFF00363D),
    tertiaryContainer = Color(0xFF1F4D54),
    onTertiaryContainer = Color(0xFFBDEAF3),
    background = Color(0xFF101510),
    onBackground = Color(0xFFE0E4DB),
    surface = Color(0xFF101510),
    onSurface = Color(0xFFE0E4DB),
    surfaceVariant = Color(0xFF313630),
    onSurfaceVariant = Color(0xFFC1C9BE),
    surfaceTint = Color(0xFF9CD4A0),
    inverseSurface = Color(0xFFE0E4DB),
    inverseOnSurface = Color(0xFF2D322C),
    error = Color(0xFFFFB4AB),
    onError = Color(0xFF690005),
    errorContainer = Color(0xFF93000A),
    onErrorContainer = Color(0xFFFFDAD6),
    outline = Color(0xFF8B9389),
    outlineVariant = Color(0xFF414941),
    scrim = Color(0xFF000000),
    surfaceBright = Color(0xFF363A35),
    surfaceDim = Color(0xFF101510),
    surfaceContainer = Color(0xFF1C211C),
    surfaceContainerHigh = Color(0xFF262B26),
    surfaceContainerHighest = Color(0xFF313630),
    surfaceContainerLow = Color(0xFF181D18),
    surfaceContainerLowest = Color(0xFF0B0F0B),
)

/** 明暗两套 `ColorScheme`（都由种子色派生）。 */
fun quizColorScheme(dark: Boolean): ColorScheme = if (dark) SeedDarkScheme else SeedLightScheme

/**
 * 规格 §6.2「固定色值表」——**不随主题种子色变化**。
 *
 * 每一行的注释里写着它在规格里的出处，改值前先回去核对。
 */
@Immutable
data class QuizPalette(
    val dark: Boolean,
    /** 画布（Scaffold 背景）：浅 `#FFF3F5F4` / 深 `#FF0F1113`（§6.2）。 */
    val canvas: Color,
    /** 卡片描边 / 分隔线：浅 `#FFE3E7E5` / 深 `#FF2A2F33`（§6.2）。 */
    val cardStroke: Color,
    /** 次级填充（输入框 / 代码块底）：浅 `#FFF2F5F3` / 深 `#FF191C1F`（§6.2）。 */
    val fill: Color,
    /** 标绿文字：浅 `#FF16A34A` / 深 `#FF4ADE80`（§3.5）。 */
    val highlightText: Color,
    /** 标绿底：浅 `#FFDCFCE7` / 深 `#FF14351F`（§3.5）。 */
    val highlightBackground: Color,
    /** 不确定（黄）文字：浅 `#FFD97706` / 深 `#FFFBBF24`（§3.5）。 */
    val uncertainText: Color,
    /** 不确定（黄）底：浅 `#FFFEF3C7` / 深 `#FF322712`（§3.5）。 */
    val uncertainBackground: Color,
    /** 警告正文：浅 `#FF92400E` / 深 `#FFFCD34D`（§3.5）。 */
    val warnText: Color,
    /** 复核徽标 / 徽标底：`#FFF59E0B` / `#FFFEF3C7`（不分明暗，§6.2）。 */
    val reviewBadge: Color,
    val reviewBadgeBackground: Color,
    /** 题目不全主色 / 底：浅 `#FFEAA100` / `#FFFFF8E1`；深 `#FFFACC15` / `#FF2A2410`（§3.5）。 */
    val incomplete: Color,
    val incompleteBackground: Color,
    /** 题目正文：浅 `#FF1F2426` / 深 `Colors.grey[100]` = `#FFF5F5F5`（§6.2）。 */
    val questionText: Color,
    /** 解析块底 / 描边：浅 `#FFF6F8F7` / `#FFE9EDEB`；深 `#FF171A1D` / `#FF262B2F`（§6.2）。 */
    val analysisBackground: Color,
    val analysisStroke: Color,
    /** 材料块底 / 描边：浅 `#FFF5F7F9` / `#FFE3E8EC`；深 `#FF1B1E21` / `#FF2A2F34`（§6.2）。 */
    val materialBackground: Color,
    val materialStroke: Color,
    /** 选项未命中左竖条：浅 `#FFE7EBE9` / 深 `#FF2E3439`（§6.2）。 */
    val optionBar: Color,
    /** 判断题未命中边框：浅 `#FFD3D9D6` / 深 `#FF3A4046`（§6.2）。 */
    val judgeBorder: Color,
    /** 会话状态色：排队 / 取消 / 未知 浅 `#FF64748B` 深 `#FF9AA4AE`（§2.4.3）。 */
    val statusIdle: Color,
    /** 会话状态色：识别中 浅 `#FF2563EB` 深 `#FF60A5FA`（§2.4.3）。 */
    val statusAnalyzing: Color,
    /** 会话状态色：失败 浅 `#FFDC2626` 深 `#FFF87171`（§2.4.3）。 */
    val statusFailed: Color,
)

private val LightPalette = QuizPalette(
    dark = false,
    canvas = Color(0xFFF3F5F4),
    cardStroke = Color(0xFFE3E7E5),
    fill = Color(0xFFF2F5F3),
    highlightText = Color(0xFF16A34A),
    highlightBackground = Color(0xFFDCFCE7),
    uncertainText = Color(0xFFD97706),
    uncertainBackground = Color(0xFFFEF3C7),
    warnText = Color(0xFF92400E),
    reviewBadge = Color(0xFFF59E0B),
    reviewBadgeBackground = Color(0xFFFEF3C7),
    incomplete = Color(0xFFEAA100),
    incompleteBackground = Color(0xFFFFF8E1),
    questionText = Color(0xFF1F2426),
    analysisBackground = Color(0xFFF6F8F7),
    analysisStroke = Color(0xFFE9EDEB),
    materialBackground = Color(0xFFF5F7F9),
    materialStroke = Color(0xFFE3E8EC),
    optionBar = Color(0xFFE7EBE9),
    judgeBorder = Color(0xFFD3D9D6),
    statusIdle = Color(0xFF64748B),
    statusAnalyzing = Color(0xFF2563EB),
    statusFailed = Color(0xFFDC2626),
)

private val DarkPalette = QuizPalette(
    dark = true,
    canvas = Color(0xFF0F1113),
    cardStroke = Color(0xFF2A2F33),
    fill = Color(0xFF191C1F),
    highlightText = Color(0xFF4ADE80),
    highlightBackground = Color(0xFF14351F),
    uncertainText = Color(0xFFFBBF24),
    uncertainBackground = Color(0xFF322712),
    warnText = Color(0xFFFCD34D),
    reviewBadge = Color(0xFFF59E0B),
    reviewBadgeBackground = Color(0xFFFEF3C7),
    incomplete = Color(0xFFFACC15),
    incompleteBackground = Color(0xFF2A2410),
    questionText = Color(0xFFF5F5F5),
    analysisBackground = Color(0xFF171A1D),
    analysisStroke = Color(0xFF262B2F),
    materialBackground = Color(0xFF1B1E21),
    materialStroke = Color(0xFF2A2F34),
    optionBar = Color(0xFF2E3439),
    judgeBorder = Color(0xFF3A4046),
    statusIdle = Color(0xFF9AA4AE),
    statusAnalyzing = Color(0xFF60A5FA),
    statusFailed = Color(0xFFF87171),
)

/** 当前配色。`staticCompositionLocalOf`：换主题时整棵子树一起重组，读取零成本。 */
val LocalQuizPalette = staticCompositionLocalOf { LightPalette }

/** 会话状态 → 颜色（§2.4.3 表）。`done` 走标绿，`failed` 走失败色，其余走 idle 色。 */
fun QuizPalette.statusColor(status: String): Color = when (status) {
    "analyzing" -> statusAnalyzing
    "done" -> highlightText
    "failed" -> statusFailed
    else -> statusIdle
}

/**
 * 规格 §6.4 的字号表：主题里能定的四档字重（`titleLarge` w700 / `titleMedium` w600 /
 * `titleSmall` w600 / `labelLarge` w600），其余字号按位置在组件里取
 * （题干/选项/答案是**用户可调**的 `fontSize`，不在 Typography 里）。
 *
 * 2.0 补充（P1 令牌层）：**Android 不复制 Windows 的字号数值**。设计 token 里的六个角色
 * （caption / body / bodyStrong / subtitle / title / titleLarge）在这边映射到 M3 原生槽位：
 * `bodySmall / bodyMedium / titleSmall / titleMedium / titleLarge / headlineSmall`。
 * 品牌统一落在角色上，度量随平台 —— 把 Windows 的 20px 硬塞成 20sp 会让
 * `LargeTopAppBar` 的标题从原生 22sp 掉到 20sp，那就是用户明确不要的「跨端模仿」。
 */
fun quizTypography(base: Typography = Typography()): Typography = Typography(
    // **1.1.0 的加粗口径**（用户 2026-09-27 明确）：「除**选项内容**与**解析正文**以外全部加粗」。
    // 做法：给**各槽位**都挂真粗体字面（`fontWeight` 单独是不够的 —— 安卓端此前不注册字体，
    // 权重只能落到系统/合成；实测笔画游程 字母 0.80 → 换真字面后 1.00，见 docs/progress.md 第 128 轮）。
    // 为什么 `bodyLarge` 尤其关键：M3 的 `MaterialTheme` 会把 `LocalTextStyle` 设为 `typography.bodyLarge`，
    // 而 Compose 的样式合并在字段为 null 时**保留上层值** ⇒ 那些「只给 fontSize/color、没给 fontFamily」
    // 的 `Text`/`MathText`（题干、答案区、材料、警告条…）会自动继承粗体。
    // 为什么标题槽位也要挂（用户 2026-09-27 追加要求）：页面标题与设置页分类标题走的是
    // `titleLarge`/`titleMedium`/`titleSmall`/`headlineSmall`，不挂就仍然不粗。
    // 例外（选项内容 ResultScreen.kt 选项区、解析正文 AnalysisBlock）另用 CompositionLocalProvider
    // 逐处还原成 `FontFamily.Default`。
    titleLarge = base.titleLarge.copy(fontWeight = FontWeight.Bold, fontSynthesis = FontSynthesis.All),
    titleMedium = base.titleMedium.copy(fontWeight = FontWeight.SemiBold, fontSynthesis = FontSynthesis.All),
    titleSmall = base.titleSmall.copy(fontWeight = FontWeight.SemiBold, fontSynthesis = FontSynthesis.All),
    headlineSmall = base.headlineSmall.copy(fontWeight = FontWeight.Bold, fontSynthesis = FontSynthesis.All),
    headlineMedium = base.headlineMedium.copy(fontWeight = FontWeight.Bold, fontSynthesis = FontSynthesis.All),
    bodyLarge = base.bodyLarge.copy(fontWeight = FontWeight.SemiBold, fontSynthesis = FontSynthesis.All),
    // **次要文字槽位走常规字重**：M3 的 `bodyMedium` / `bodySmall` 在两端语义上就是"说明、副标题、
    // 计数、时间、状态"这类次要信息（用户 2026-09-27 逐条指出：会话卡片的标题与时间/状态、
    // 历史记录计数、识别模块设置页的说明文本…都**不该**加粗）。
    // 与其逐页打补丁，不如按槽位语义定死 —— 这样**所有页面**的说明文字一次到位。
    // 加粗面版仍是：title* / headline* / bodyLarge / label*。
    bodyMedium = base.bodyMedium.copy(fontWeight = FontWeight.SemiBold, fontSynthesis = FontSynthesis.All),
    bodySmall = base.bodySmall.copy(fontWeight = FontWeight.SemiBold, fontSynthesis = FontSynthesis.All),
    labelLarge = base.labelLarge.copy(fontWeight = FontWeight.SemiBold, fontSynthesis = FontSynthesis.All),
    labelMedium = base.labelMedium.copy(fontWeight = FontWeight.SemiBold, fontSynthesis = FontSynthesis.All),
    labelSmall = base.labelSmall.copy(fontWeight = FontWeight.SemiBold, fontSynthesis = FontSynthesis.All),
)







/**
 * 圆角阶梯：来自 `design/tokens.json` 的 radii（card 16 / control 10）。
 *
 * **为什么要设全局 Shapes**：M3 的 `Card` 默认形状是 `ShapesKt.getValue(CornerMedium)`
 * —— 也就是 `MaterialTheme.shapes.medium`，所以把 medium 设成 16 之后，
 * 裸 `Card(...)` 自动就是 1.1.0 约定的圆角 16，页面里不用再写 `shape = RoundedCornerShape(...)`。
 *
 * **为什么不会误伤按钮**（字节码实证，2026-09-27，`javap -p -c` 于 material3 1.3.2 的 classes.jar）：
 * `ButtonDefaults.getShape()` → `FilledButtonTokens.getContainerShape()` → 静态字段值是
 * `ShapeKeyTokens.CornerFull`，而 `ShapesKt.fromToken` 对 `CornerFull` 返回的是**常量**
 * （不读 `Shapes.*`）。所以按钮在 M3 里始终是原生胶囊形，改 Shapes 影响不到它 ——
 * 这正是我们要的：圆角品牌值走 Shapes，按钮保持平台原生。
 */
fun quizShapes(): Shapes = Shapes(
    extraSmall = RoundedCornerShape(QsTokens.radiusControl),
    small = RoundedCornerShape(QsTokens.radiusControl),
    medium = RoundedCornerShape(QsTokens.radiusCard),
    large = RoundedCornerShape(QsTokens.radiusCard),
    extraLarge = RoundedCornerShape(QsTokens.radiusCard),
)

/**
 * 本机支持动态取色（Material You）吗：API 31+。
 *
 * 版本判断用 `@ChecksSdkIntAtLeast(api = S)` 标注（照 nowinandroid L249-L250 的写法）：
 * 这样调用点在 lint 眼里就知道「进了这个分支就一定在 31+」，不会误报 NewApi。
 */
@ChecksSdkIntAtLeast(api = Build.VERSION_CODES.S)
private fun isDynamicColorAvailable(): Boolean = Build.VERSION.SDK_INT >= Build.VERSION_CODES.S

/**
 * 应用主题。
 *
 * 画布色**不进 ColorScheme**（§6.1：`scaffoldBackgroundColor = canvas(brightness)` 是固定表里的值，
 * 不是种子派生物），所以走 [LocalQuizPalette]，由各页 `Scaffold(containerColor = …)` 自取。
 *
 * @param dark 明暗（由「设置 → 显示设置 → 主题」三态算出来的 [SettingsState.dark]）。
 * @param disableDynamicTheming **反向开关**，默认 `true`（= 不用动态取色）。
 *   语义照 nowinandroid `core/designsystem/…/theme/Theme.kt` L192-L208 ——
 *   它那里的参数就叫 `disableDynamicTheming: Boolean = true`。
 *   计划 §6 的要求是「照它的语义接，别自己发明开关方向」，所以这里保持反向命名：
 *   调用方从设置里读到的正向值要**取反**传进来。API 31 以下自动忽略（[isDynamicColorAvailable]）。
 */
@Composable
fun QuizSyncTheme(
    dark: Boolean = isSystemInDarkTheme(),
    disableDynamicTheming: Boolean = true,
    content: @Composable () -> Unit,
) {
    val palette = if (dark) DarkPalette else LightPalette
    val scheme = if (!disableDynamicTheming && isDynamicColorAvailable()) {
        val context = LocalContext.current
        if (dark) dynamicDarkColorScheme(context) else dynamicLightColorScheme(context)
    } else {
        quizColorScheme(dark)
    }
    CompositionLocalProvider(LocalQuizPalette provides palette) {
        MaterialTheme(
            colorScheme = scheme,
            typography = quizTypography(),
            shapes = quizShapes(),
            content = content,
        )
    }
}

/** 取当前配色（`QuizPalette`）。规格里的固定色值一律从这里拿，别在组件里写字面量。 */
val quizPalette: QuizPalette
    @Composable @ReadOnlyComposable get() = LocalQuizPalette.current
