// 本文件由 tools/gen_design_tokens.py 从 design/tokens.json 生成，请勿手改。
// 校验：跑一次 tools/gen_design_tokens.py，加参数 check
//
// 值来自老应用（1.1.0）的视觉契约 packages/quizsync_ui，
// 由 packages/quizsync_ui/test/design_token_parity_test.dart 逐条断言 —— 不要在这里手改数值。
// 字号阶 / 动效 / 层级三节是 P1 新增的（1.1.0 没有对应物），自检写在 tools/gen_design_tokens.py 里。
//
// 生成时逐条校验过的 WCAG 对比度：
//   主按钮文字 / 填充: 6.48:1（下限 4.5:1）
//   主按钮文字 / 填充（暗）: 7.73:1（下限 4.5:1）
//   正文 / 卡片: 17.10:1（下限 4.5:1）
//   正文 / 卡片（暗）: 14.33:1（下限 4.5:1）
//   次要文字 / 页面底色: 8.51:1（下限 4.5:1）
//   次要文字 / 页面底色（暗）: 11.14:1（下限 4.5:1）
//   标签描边 / 卡片: 4.48:1（下限 3.0:1）
//   标签描边 / 卡片（暗）: 5.83:1（下限 3.0:1）
package com.quizsync.android.dev

import androidx.compose.animation.core.CubicBezierEasing
import androidx.compose.animation.core.Easing
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.dp

/// 跨端设计 token（与 Windows 的 DesignTokens.xaml 同源，值都来自老应用的视觉契约）。
object QsTokens {
    /// 圆角：**按钮 10 / 标签 999 胶囊 / 卡片 16** —— 与 AppRadius.control / chip / card 一致。
    /// （第五轮教训：上一版这里写反了，把按钮做成胶囊、标签做成圆角矩形。）
    val radiusCard = 16.dp
    val radiusControl = 10.dp
    val radiusChip = 999.dp

    val spaceXxs = 2.dp
    val spaceXs = 4.dp
    val spaceSm = 8.dp
    val spaceMd = 12.dp
    val spaceLg = 16.dp
    val spaceXl = 24.dp

    /// 组件级尺寸（图标 / 发丝线 / 状态圆底 / 进度环粗细）。**组件里不许再写裸 dp。**
    val hairline = 1.dp
    val strokeEmphasis = 2.dp
    val strokeMarker = 4.dp
    val iconXs = 12.dp
    val iconBase = 16.dp
    val iconSm = 18.dp
    val iconMd = 20.dp
    val iconLg = 22.dp
    val iconXl = 32.dp
    val iconBox = 38.dp
    val statusDot = 7.dp
    val statusDotSm = 5.dp
    val touchTarget = 48.dp
    val sessionBarHeight = 46.dp
    val headerRowHeight = 46.dp
    val stateBadge = 68.dp
    val progressStroke = 2.5.dp
    val sliderLabelWidth = 78.dp
    val sliderValueWidth = 52.dp
    val bottomBarClearance = 88.dp

    // 单独取用的颜色（Compose 里不经过 ColorScheme 的那些）。
    val brandPrimary = Color(0xFF35693E)
    val brandPrimaryDark = Color(0xFF9CD4A0)
    val onBrandPrimary = Color(0xFFFFFFFF)
    val onBrandPrimaryDark = Color(0xFF003914)
    val brandSeed = Color(0xFF16A34A)
    val brandSeedDark = Color(0xFF16A34A)
    val canvas = Color(0xFFF3F5F4)
    val canvasDark = Color(0xFF0F1113)
    val surface = Color(0xFFFFFFFF)
    val surfaceDark = Color(0xFF101510)
    val onSurface = Color(0xFF181D18)
    val onSurfaceDark = Color(0xFFE0E4DB)
    val onSurfaceVariant = Color(0xFF414941)
    val onSurfaceVariantDark = Color(0xFFC1C9BE)
    val outline = Color(0xFF727970)
    val outlineDark = Color(0xFF8B9389)
    val cardStroke = Color(0xFFE3E7E5)
    val cardStrokeDark = Color(0xFF2A2F33)

    val lightScheme = lightColorScheme(
        primary = Color(0xFF35693E),
        onPrimary = Color(0xFFFFFFFF),
        background = Color(0xFFF3F5F4),
        onBackground = Color(0xFF181D18),
        surface = Color(0xFFFFFFFF),
        onSurface = Color(0xFF181D18),
        outline = Color(0xFF727970),
        outlineVariant = Color(0xFFE3E7E5),
        onSurfaceVariant = Color(0xFF414941),
    )

    val darkScheme = darkColorScheme(
        primary = Color(0xFF9CD4A0),
        onPrimary = Color(0xFF003914),
        background = Color(0xFF0F1113),
        onBackground = Color(0xFFE0E4DB),
        surface = Color(0xFF101510),
        onSurface = Color(0xFFE0E4DB),
        outline = Color(0xFF8B9389),
        outlineVariant = Color(0xFF2A2F33),
        onSurfaceVariant = Color(0xFFC1C9BE),
    )

    /// 字号阶：Android **不复制 Windows 的字号数值**，而是把六个角色映射到 M3 原生槽位。
    /// 品牌统一在角色上，度量各自随平台（见本文件顶部注释与 docs/design-system-plan.md）。
    /// micro（Windows 10px）→ MaterialTheme.typography.labelSmall
    /// captionSmall（Windows 11px）→ MaterialTheme.typography.labelSmall
    /// caption（Windows 12px）→ MaterialTheme.typography.bodySmall
    /// body（Windows 14px）→ MaterialTheme.typography.bodyMedium
    /// bodyStrong（Windows 14px）→ MaterialTheme.typography.titleSmall
    /// subtitle（Windows 16px）→ MaterialTheme.typography.titleMedium
    /// title（Windows 20px）→ MaterialTheme.typography.titleLarge
    /// titleLarge（Windows 28px）→ MaterialTheme.typography.headlineSmall

    /// 动效：时长与曲线。Compose 侧取 `QsTokens.Motion.normal` 这类。
    object Motion {
        /// 时长（毫秒）。上限 400ms 由 tools/gen_design_tokens.py 断言。
        val fast = 120
        val normal = 200
        val slow = 300

        /// 入场位移（dp）。上限 24 —— 位移越大越像「页面乱飞」。
        val enterOffset = 8

        /// 曲线：入场用 decelerate、出场用 accelerate、两者都在场用 standard。
        val standard = CubicBezierEasing(0.2f, 0.0f, 0.0f, 1.0f)
        val decelerate = CubicBezierEasing(0.1f, 0.9f, 0.2f, 1.0f)
        val accelerate = CubicBezierEasing(0.9f, 0.1f, 1.0f, 0.2f)

        /// 曲线对外只暴露 Easing 接口，免得调用方又去手写一条贝塞尔。
        val standardEasing: Easing get() = standard
        val decelerateEasing: Easing get() = decelerate
        val accelerateEasing: Easing get() = accelerate
    }

    /// 层级：卡片是 0（平铺 + 描边），只有浮层抬起来。
    object Elevation {
        val flat = 0.dp
        val card = 0.dp
        val raised = 1.dp
        val overlay = 3.dp
    }
}
