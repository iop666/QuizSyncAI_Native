#!/usr/bin/env python3
"""从 design/tokens.json 生成两端的样式产物（唯一来源 → 两端一起更新）。

产物（都带「请勿手改」头，由本脚本负责改写）：
  android/app/src/main/kotlin/com/quizsync/android/dev/DesignTokens.kt
  android/app/src/main/res/values/design_tokens.xml
  android/app/src/main/res/values-night/design_tokens.xml
  windows/QuizSync.App/DesignTokens.xaml

用法：
  python tools/gen_design_tokens.py            # 生成/更新
  python tools/gen_design_tokens.py --check    # 只校验产物与 token 一致（CI 用，不一致就退出 1）

两道闸门：
  ① **值从哪来**：token 的值必须等于老应用（1.1.0）视觉契约里的值 —— 由
     packages/quizsync_ui/test/design_token_parity_test.dart 在 Dart 侧逐条断言
     （用真 Flutter 跑 ColorScheme.fromSeed，不是手抄）。第五轮用户当场质问
     「我应用 1.1.0 原来是浅绿的，你用紫色干什么」就是这道闸门缺失的后果：
     前几轮先凭空发明品牌紫 #63519F，再花好几轮把两端往那个虚构规格上拉，
     而且方向正好修反（老应用是按钮圆角 10 / 标签 999 胶囊）。
  ② **产物一致性**：手改任何一侧的产物、或改了 token 忘了重新生成，--check 就红。

生成时还会做：
  * WCAG 对比度逐条校验（不达标直接失败）；
  * 产物必须能被 XML 解析、且注释里不许出现两个连字符
    （踩过：注释里写脚本名加那次选项，XML 规范禁止，XamlCompiler 静默退出 1，一点提示都没有）。
"""

from __future__ import annotations

import argparse
import json
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
TOKENS = ROOT / "design" / "tokens.json"

# 控制台编码：Windows CI runner 上 Python 的 stdout 是 **cp1252**，打印中文会抛
# UnicodeEncodeError —— 于是「校验全部通过、却在打印成功信息那一行崩掉」，报错还看不出跟校验有关
# （实测：新仓第一次 CI 的 token 校验就是这么红的，栈顶停在 `print(f"设计 token 一致：…")`）。
# 这里显式把两个流改成 UTF-8；改不了（被重定向成非文本流）就跳过。
for _stream in (sys.stdout, sys.stderr):
    if hasattr(_stream, "reconfigure"):
        _stream.reconfigure(encoding="utf-8", errors="replace")

KOTLIN_OUT = ROOT / "android/app/src/main/kotlin/com/quizsync/android/dev/DesignTokens.kt"
ANDROID_VALUES_OUT = ROOT / "android/app/src/main/res/values/design_tokens.xml"
ANDROID_VALUES_NIGHT_OUT = ROOT / "android/app/src/main/res/values-night/design_tokens.xml"
WINDOWS_OUT = ROOT / "windows/QuizSync.App/DesignTokens.xaml"
WINDOWS_MOTION_OUT = ROOT / "windows/QuizSync.App/Theme/QsMotion.g.cs"
WINDOWS_TYPE_OUT = ROOT / "windows/QuizSync.App/Theme/QsTypography.g.cs"

GENERATED_BY = "tools/gen_design_tokens.py"
DO_NOT_EDIT = "本文件由 %s 从 design/tokens.json 生成，请勿手改。" % GENERATED_BY


# ---------------------------------------------------------------- token 读取


def load_tokens() -> dict:
    data = json.loads(TOKENS.read_text(encoding="utf-8"))
    for key in ("colors", "radii", "spacing", "contrast", "type", "motion", "elevation", "dimensions"):
        if key not in data:
            raise SystemExit(f"design/tokens.json 缺少 {key} 段")
    return data


# 字号阶的角色顺序（生成器按这个顺序排版，也是「两端角色必须一致」这条的清单）
TYPE_ROLES = ("micro", "captionSmall", "caption", "body", "bodyStrong", "subtitle", "title", "titleLarge")


def verify_type(tokens: dict) -> None:
    """字号阶自检：角色齐全、阶梯不减、行高不小于字号、同字号必须靠字重区分。

    「阶梯」这条不是洁癖：重构前两端攒出 14 种字号字面量，就是因为随手写一个数没人拦。
    这里要求字号逐档不减，且**同字号的相邻两档必须字重不同**（body / bodyStrong 就是这种关系）——
    否则就是两个没区别的档位，等于白加。
    第一版把这条写成「严格递增」，结果自己的 body/bodyStrong 先被拦下来了（生成器当场退出 1）。
    """
    table = tokens["type"]
    missing = [role for role in TYPE_ROLES if role not in table]
    if missing:
        raise SystemExit(f"type 段缺少角色：{missing}")
    previous_size, previous_weight, previous_role = 0, 0, "(起点)"
    for role in TYPE_ROLES:
        entry = table[role]
        for field in ("size", "weight", "lineHeight"):
            if field not in entry:
                raise SystemExit(f"type.{role} 缺少 {field}")
        size, weight, line_height = entry["size"], entry["weight"], entry["lineHeight"]
        if not 100 <= weight <= 900 or weight % 100:
            raise SystemExit(f"type.{role}.weight 必须是 100 的整数倍且在 100–900：{weight}")
        if line_height < size:
            raise SystemExit(f"type.{role} 的行高（{line_height}）小于字号（{size}）")
        if size < previous_size:
            raise SystemExit(f"type.{role} 的字号（{size}）比上一档 {previous_role}（{previous_size}）还小")
        if size == previous_size and weight <= previous_weight:
            raise SystemExit(
                f"type.{role} 与上一档 {previous_role} 同字号（{size}）却没有更重（{weight} <= {previous_weight}）——"
                "两个没区别的档位等于白加"
            )
        previous_size, previous_weight, previous_role = size, weight, role


def verify_motion(tokens: dict) -> None:
    """动效自检：时长封顶、位移封顶、曲线必须落在单位正方形内且端点合法。

    「时长封顶」与「位移封顶」是用户禁令的可执行版本
    （禁止无意义的持续动画 / 页面乱飞 / 大量弹跳）。
    把上限写进 token 后，想加长动画或加大位移就只能改这两个数 —— 改的时候会被 git diff 记下来。
    """
    motion = tokens["motion"]
    cap = motion["maxMs"]
    if cap > 400:
        raise SystemExit(f"motion.maxMs 不得超过 400：{cap}")
    durations = motion["duration"]
    for name, value in durations.items():
        if value <= 0 or value > cap:
            raise SystemExit(f"motion.duration.{name} = {value} 超出 (0, {cap}]")
    for name, value in motion["distance"].items():
        if value < 0 or value > motion["maxDistance"]:
            raise SystemExit(
                f"motion.distance.{name} = {value} 超出 [0, {motion['maxDistance']}]"
                "—— 位移越大越像『页面乱飞』，上限就是这条禁令的机器版"
            )
    for name, points in motion["easing"].items():
        if len(points) != 4:
            raise SystemExit(f"motion.easing.{name} 必须是 4 个控制点：{points}")
        if any(not 0.0 <= value <= 1.0 for value in points):
            raise SystemExit(f"motion.easing.{name} 的控制点必须落在 [0,1]：{points}")
        # 起点或终点必须是「全 0 / 全 1」——三次贝塞尔在 CSS/Fluent 里要求 x 分量合法即可，
        # 但本项目的曲线都是「标准缓入缓出」，端点飞出去一定是手滑写错了。
        if len(set(points[:2])) == 1 and len(set(points[2:])) == 1:
            raise SystemExit(f"motion.easing.{name} 是恒等曲线（0,0,1,1 之类的退化值）：{points}")


def verify_elevation(tokens: dict) -> None:
    """层级自检：卡片不许有阴影（1.1.0 的 cardTheme 是 elevation: 0，整站平铺 + 描边）。"""
    elevation = tokens["elevation"]
    if elevation["card"] != 0:
        raise SystemExit("elevation.card 必须为 0：1.1.0 的卡片是平铺 + 描边，没有阴影")
    if not elevation["flat"] <= elevation["card"] <= elevation["raised"] <= elevation["overlay"]:
        raise SystemExit("elevation 必须单调不减：flat <= card <= raised <= overlay")


def verify_dimensions(tokens: dict) -> None:
    """尺寸自检：必须为正数；发丝线必须是 1（粗细不是设计选择）。"""
    dimensions = tokens["dimensions"]
    for name, value in dimensions.items():
        if value <= 0:
            raise SystemExit(f"dimensions.{name} 必须为正数：{value}")
    if dimensions["hairline"] != 1:
        raise SystemExit("dimensions.hairline 必须是 1：分隔线/描边粗细不该随设计变")


def color_table(tokens: dict, theme: str) -> dict:
    return {c["name"]: c[theme] for c in tokens["colors"]}


def parse_hex(value: str) -> tuple[int, int, int]:
    text = value.lstrip("#")
    if len(text) != 6:
        raise SystemExit(f"颜色必须是 6 位十六进制：{value}")
    return int(text[0:2], 16), int(text[2:4], 16), int(text[4:6], 16)


def relative_luminance(rgb: tuple[int, int, int]) -> float:
    def channel(raw: int) -> float:
        c = raw / 255.0
        return c / 12.92 if c <= 0.03928 else ((c + 0.055) / 1.055) ** 2.4

    r, g, b = (channel(v) for v in rgb)
    return 0.2126 * r + 0.7152 * g + 0.0722 * b


def contrast_ratio(fg: str, bg: str) -> float:
    lf, lb = relative_luminance(parse_hex(fg)), relative_luminance(parse_hex(bg))
    hi, lo = max(lf, lb), min(lf, lb)
    return (hi + 0.05) / (lo + 0.05)


def verify_contrast(tokens: dict) -> list[str]:
    """逐条校验对比度下限；返回人类可读的实测报表（不达标直接 SystemExit）。"""
    report: list[str] = []
    failures: list[str] = []
    for rule in tokens["contrast"]:
        table = color_table(tokens, rule["theme"])
        try:
            fg, bg = table[rule["fg"]], table[rule["bg"]]
        except KeyError as missing:
            raise SystemExit(f"contrast 引用了不存在的颜色：{missing}")
        ratio = contrast_ratio(fg, bg)
        line = f"{rule['name']}: {ratio:.2f}:1（下限 {rule['min']}:1）"
        report.append(line)
        if ratio + 1e-9 < rule["min"]:
            failures.append("  " + line + "  ← 不达标")
    if failures:
        raise SystemExit("对比度校验失败：\n" + "\n".join(failures) + "\n\n" + "\n".join(report))
    return report


def verify_xml(text: str, label: str) -> None:
    """生成的 XML 必须能被解析，且注释体里不能出现两个连字符。

    这条自检是被一次真实的坑逼出来的：DesignTokens.xaml 的头注释里写了脚本名加那次
    选项（两个连字符），XML 规范**禁止注释体内含它**，而 XamlCompiler.exe 对这种语法
    错误**静默退出 1**（stdout 空、连 output.json 都不写），现场只能看到 MSB3073。
    当时我按「StaticResource 作用域」猜了一轮、又做了 8 次构造二分才定位到注释 ——
    其实先花 1 秒把产物丢给 XML 解析器就能立刻看出来。
    """
    try:
        ET.fromstring(text)
    except ET.ParseError as error:
        raise SystemExit(f"{label} 不是合法 XML：{error}") from error
    for comment in re.findall(r"<!--(.*?)-->", text, re.DOTALL):
        if "--" in comment:
            raise SystemExit(f"{label} 的 XML 注释里出现了两个连字符（XML 规范禁止，XamlCompiler 会静默失败）")


# ---------------------------------------------------------------- Android

# token 名 → Compose 里的用途
KOTLIN_SCHEME_SLOTS = [
    ("primary", "brandPrimary"),
    ("onPrimary", "onBrandPrimary"),
    ("background", "canvas"),
    ("onBackground", "onSurface"),
    ("surface", "surface"),
    ("onSurface", "onSurface"),
    ("outline", "outline"),
    ("outlineVariant", "cardStroke"),
    ("onSurfaceVariant", "onSurfaceVariant"),
]


def kotlin_color(hex_value: str) -> str:
    r, g, b = parse_hex(hex_value)
    return f"Color(0xFF{r:02X}{g:02X}{b:02X})"


def snake(name: str) -> str:
    return re.sub(r"(?<!^)(?=[A-Z])", "_", name).lower()


def scheme_block(tokens: dict, theme: str) -> str:
    table = color_table(tokens, theme)
    return "\n".join(
        f"        {slot} = {kotlin_color(table[token])}," for slot, token in KOTLIN_SCHEME_SLOTS
    )


def kotlin_style_comment(tokens: dict) -> str:
    """Android 侧**不生成字号数值**，只生成「角色 → Material 3 槽位」的映射表。

    为什么：Android 的原生排版语言就是 M3 Typography。如果把 Windows 的 20px 硬塞成 20sp，
    `LargeTopAppBar` 的标题会从原生 22sp 变成 20sp —— 那就是「跨端模仿」，正是用户不要的。
    品牌统一落在**角色**上（页标题 / 小节标题 / 正文 / 强正文 / 辅助文字六档，两端一一对应），
    度量各自随平台（Windows 走 Fluent 阶梯，Android 走 M3 槽位）。
    """
    mapping = {
        "micro": "labelSmall",
        "captionSmall": "labelSmall",
        "caption": "bodySmall",
        "body": "bodyMedium",
        "bodyStrong": "titleSmall",
        "subtitle": "titleMedium",
        "title": "titleLarge",
        "titleLarge": "headlineSmall",
    }
    lines = [
        f"    /// {role}（Windows {tokens['type'][role]['size']}px）→ MaterialTheme.typography.{slot}"
        for role, slot in mapping.items()
    ]
    return "\n".join(lines)


def kotlin_motion_block(tokens: dict) -> str:
    motion = tokens["motion"]
    durations = "\n".join(
        f"        val {name} = {value}" for name, value in motion["duration"].items()
    )
    easings = "\n".join(
        f"        val {name} = CubicBezierEasing({', '.join(f'{value}f' for value in points)})"
        for name, points in motion["easing"].items()
    )
    return (
        f"        /// 时长（毫秒）。上限 {motion['maxMs']}ms 由 tools/gen_design_tokens.py 断言。\n"
        f"{durations}\n\n"
        f"        /// 入场位移（dp）。上限 {motion['maxDistance']} —— 位移越大越像「页面乱飞」。\n"
        + "\n".join(f"        val {name}Offset = {value}" for name, value in motion["distance"].items())
        + f"\n\n        /// 曲线：入场用 decelerate、出场用 accelerate、两者都在场用 standard。\n"
        f"{easings}"
    )


def kotlin_elevation_block(tokens: dict) -> str:
    return "\n".join(
        f"        val {name} = {value}.dp" for name, value in tokens["elevation"].items()
    )


def kotlin_dimension_block(tokens: dict) -> str:
    return "\n".join(
        f"    val {name} = {value}.dp" for name, value in tokens["dimensions"].items()
    )


def render_kotlin(tokens: dict, report: list[str]) -> str:
    radii, spacing = tokens["radii"], tokens["spacing"]
    contrast_lines = "\n".join(f"//   {line}" for line in report)
    palette_lines = "\n".join(
        f"    val {name} = {kotlin_color(color_table(tokens, 'light')[name])}\n"
        f"    val {name}Dark = {kotlin_color(color_table(tokens, 'dark')[name])}"
        for name in (c["name"] for c in tokens["colors"])
    )
    return f"""// {DO_NOT_EDIT}
// 校验：跑一次 {GENERATED_BY}，加参数 check
//
// 值来自老应用（1.1.0）的视觉契约 packages/quizsync_ui，
// 由 packages/quizsync_ui/test/design_token_parity_test.dart 逐条断言 —— 不要在这里手改数值。
// 字号阶 / 动效 / 层级三节是 P1 新增的（1.1.0 没有对应物），自检写在 {GENERATED_BY} 里。
//
// 生成时逐条校验过的 WCAG 对比度：
{contrast_lines}
package com.quizsync.android.dev

import androidx.compose.animation.core.CubicBezierEasing
import androidx.compose.animation.core.Easing
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.dp

/// 跨端设计 token（与 Windows 的 DesignTokens.xaml 同源，值都来自老应用的视觉契约）。
object QsTokens {{
    /// 圆角：**按钮 10 / 标签 999 胶囊 / 卡片 16** —— 与 AppRadius.control / chip / card 一致。
    /// （第五轮教训：上一版这里写反了，把按钮做成胶囊、标签做成圆角矩形。）
    val radiusCard = {radii['card']}.dp
    val radiusControl = {radii['control']}.dp
    val radiusChip = {radii['chip']}.dp

    val spaceXxs = {spacing['xxs']}.dp
    val spaceXs = {spacing['xs']}.dp
    val spaceSm = {spacing['sm']}.dp
    val spaceMd = {spacing['md']}.dp
    val spaceLg = {spacing['lg']}.dp
    val spaceXl = {spacing['xl']}.dp

    /// 组件级尺寸（图标 / 发丝线 / 状态圆底 / 进度环粗细）。**组件里不许再写裸 dp。**
{kotlin_dimension_block(tokens)}

    // 单独取用的颜色（Compose 里不经过 ColorScheme 的那些）。
{palette_lines}

    val lightScheme = lightColorScheme(
{scheme_block(tokens, 'light')}
    )

    val darkScheme = darkColorScheme(
{scheme_block(tokens, 'dark')}
    )

    /// 字号阶：Android **不复制 Windows 的字号数值**，而是把六个角色映射到 M3 原生槽位。
    /// 品牌统一在角色上，度量各自随平台（见本文件顶部注释与 docs/design-system-plan.md）。
{kotlin_style_comment(tokens)}

    /// 动效：时长与曲线。Compose 侧取 `QsTokens.Motion.normal` 这类。
    object Motion {{
{kotlin_motion_block(tokens)}

        /// 曲线对外只暴露 Easing 接口，免得调用方又去手写一条贝塞尔。
        val standardEasing: Easing get() = standard
        val decelerateEasing: Easing get() = decelerate
        val accelerateEasing: Easing get() = accelerate
    }}

    /// 层级：卡片是 0（平铺 + 描边），只有浮层抬起来。
    object Elevation {{
{kotlin_elevation_block(tokens)}
    }}
}}
"""


def render_android_colors(tokens: dict, theme: str) -> str:
    table = color_table(tokens, theme)
    qualifier = "" if theme == "light" else "（夜间）"
    entries = "\n".join(f'    <color name="qs_{snake(name)}">{value}</color>' for name, value in table.items())
    return f"""<?xml version="1.0" encoding="utf-8"?>
<!-- {DO_NOT_EDIT}{qualifier} -->
<resources>
{entries}
</resources>
"""


# ---------------------------------------------------------------- Windows

# token 名 → Windows 画刷名
BRUSH_NAMES = {
    "brandPrimary": "QsBrandPrimaryBrush",
    "onBrandPrimary": "QsOnBrandPrimaryBrush",
    "brandSeed": "QsBrandSeedBrush",
    "canvas": "QsCanvasBrush",
    "surface": "QsSurfaceBrush",
    "onSurface": "QsOnSurfaceBrush",
    "onSurfaceVariant": "QsOnSurfaceVariantBrush",
    "outline": "QsOutlineBrush",
    "cardStroke": "QsCardStrokeBrush",
}


def xaml_color(hex_value: str) -> str:
    r, g, b = parse_hex(hex_value)
    return f"#FF{r:02X}{g:02X}{b:02X}"


def theme_dictionary(tokens: dict, theme: str, indent: str) -> str:
    table = color_table(tokens, theme)
    lines = [
        f'{indent}<SolidColorBrush x:Key="{brush}" Color="{xaml_color(table[name])}"/>'
        for name, brush in BRUSH_NAMES.items()
    ]
    # AccentButtonStyle 的悬停/按下/禁用态走这几个主题画刷；一起覆盖，
    # 免得 Normal 是品牌绿、一悬停又变回系统强调色。
    primary = xaml_color(table["brandPrimary"])
    lines.append(f"{indent}<!-- AccentButtonStyle 的交互态一起跟着 token 走 -->")
    lines.append(f'{indent}<SolidColorBrush x:Key="AccentFillColorDefaultBrush" Color="{primary}"/>')
    lines.append(f'{indent}<SolidColorBrush x:Key="AccentFillColorSecondaryBrush" Color="{primary}" Opacity="0.9"/>')
    lines.append(f'{indent}<SolidColorBrush x:Key="AccentFillColorTertiaryBrush" Color="{primary}" Opacity="0.8"/>')
    lines.append(f'{indent}<SolidColorBrush x:Key="AccentFillColorDisabledBrush" Color="{primary}" Opacity="0.4"/>')
    lines.append(f'{indent}<Color x:Key="SystemAccentColor">{primary}</Color>')
    return "\n".join(lines)


def xaml_type_block(tokens: dict) -> str:
    """字号阶 → WinUI TextBlock 样式 + **纯数值资源**。

    两套都给：
      * `QsXxxTextStyle`（Style）—— 新写的 XAML 用它，一次拿齐字号/字重/换行/前景色；
      * `QsFontSizeXxx`（x:Double）—— **给「只想换个数、不想改别的」的地方用**。
        为什么需要第二套：把 `<TextBlock FontSize="12.5">` 换成 `Style="{StaticResource …}"`
        会顺带带上 `TextWrapping=Wrap` 与前景色，可能改变既有换行行为；
    纯数值替换是零副作用的，能把存量字面量安全地收敛掉。

    字体族刻意不写：两端都用系统默认（用户第 2 条）。WinUI 的 TextBlock 默认就是
    Segoe UI Variable / Segoe UI，写死反而会在别的语言环境下退化。

    前景色只给「默认」那一支：caption 天生是次要文字（时间戳、提示、标签），用 onSurfaceVariant；
    其余五档用 onSurface。需要「正文但要次要色」时套 QsSecondaryTextStyle。
    """
    lines = []
    numeric = []
    for role in TYPE_ROLES:
        entry = tokens["type"][role]
        title = role[:1].upper() + role[1:]
        brush = "QsOnSurfaceVariantBrush" if role in ("micro", "caption", "captionSmall") else "QsOnSurfaceBrush"
        numeric.append(f'    <x:Double x:Key="QsFontSize{title}">{entry["size"]}</x:Double>')
        lines.append(
            f"    <!-- {entry['note']} -->\n"
            f'    <Style x:Key="Qs{title}TextStyle" TargetType="TextBlock">\n'
            f'        <Setter Property="FontSize" Value="{entry["size"]}" />\n'
            f'        <Setter Property="FontWeight" Value="{"SemiBold" if entry["weight"] >= 600 else "Normal"}" />\n'
            f'        <Setter Property="TextWrapping" Value="Wrap" />\n'
            f'        <Setter Property="Foreground" Value="{{ThemeResource {brush}}}" />\n'
            f"    </Style>"
        )
    secondary = tokens["type"]["body"]
    lines.append(
        f"    <!-- 正文尺寸（{secondary['size']}）+ 次要色：说明文字、辅助行。-->\n"
        '    <Style x:Key="QsSecondaryTextStyle" TargetType="TextBlock" BasedOn="{StaticResource QsBodyTextStyle}">\n'
        '        <Setter Property="Foreground" Value="{ThemeResource QsOnSurfaceVariantBrush}" />\n'
        "    </Style>"
    )
    return "    <!-- 字号数值（给「只换数不改别的」的地方用，见本节说明） -->\n" + "\n".join(numeric) + "\n\n" + "\n\n".join(lines)


def xaml_scale_block(tokens: dict) -> str:
    """间距与圆角：间距给 x:Double（StackPanel.Spacing / Margin 用），圆角给 CornerRadius。"""
    radii, spacing = tokens["radii"], tokens["spacing"]
    spacing_lines = "\n".join(
        f'    <x:Double x:Key="QsSpace{name[:1].upper() + name[1:]}">{value}</x:Double>'
        for name, value in spacing.items()
    )
    dimension_lines = "\n".join(
        f'    <x:Double x:Key="QsDim{name[:1].upper() + name[1:]}">{value}</x:Double>'
        for name, value in tokens["dimensions"].items()
    )
    radius_lines = "\n".join(
        f'    <CornerRadius x:Key="QsRadius{name[:1].upper() + name[1:]}">{value}</CornerRadius>'
        for name, value in radii.items()
    )
    padding_lines = f'    <Thickness x:Key="QsPadCard">{spacing["xl"]}</Thickness>'
    return (
        f"{spacing_lines}\n\n{dimension_lines}\n\n{radius_lines}\n\n"
        f"    <!-- 卡片内边距 -->\n{padding_lines}"
    )


def xaml_motion_block(tokens: dict) -> str:
    """动效在 XAML 里只放「必然合法」的数值资源。

    刻意**不**生成 `<Duration>` / `<KeySpline>` 资源：这两个类型能不能作为资源元素从文本内容
    初始化，我没有编译实证（第 7 条：API 可用性一律由编译探针决定，不靠记忆）。
    类型化的工厂放在同一份 token 生成的 Theme/QsMotion.g.cs 里 —— C# 侧写 Storyboard 本来就更顺手。
    """
    motion = tokens["motion"]
    duration_lines = "\n".join(
        f'    <x:Double x:Key="QsDurationMs{name[:1].upper() + name[1:]}">{value}</x:Double>'
        for name, value in motion["duration"].items()
    )
    spline_lines = "\n".join(
        f'    <x:String x:Key="QsEase{name[:1].upper() + name[1:]}Points">{",".join(str(value) for value in points)}</x:String>'
        for name, points in motion["easing"].items()
    )
    distance_lines = "\n".join(
        f'    <x:Double x:Key="QsDistance{name[:1].upper() + name[1:]}">{value}</x:Double>'
        for name, value in motion["distance"].items()
    )
    return (
        f"    <!-- 动效：时长上限 {motion['maxMs']}ms、位移上限 {motion['maxDistance']}"
        f"（{GENERATED_BY} 会断言 —— 这两条上限就是「禁止无意义持续动画 / 页面乱飞」的机器版）。\n"
        f"         这里的值给 XAML 用；C# 请直接用 Theme/QsMotion.g.cs 的常量与工厂。 -->\n"
        f"{duration_lines}\n\n{distance_lines}\n\n{spline_lines}"
    )


def render_windows(tokens: dict, report: list[str]) -> str:
    radii, spacing = tokens["radii"], tokens["spacing"]
    contrast_lines = "\n".join(f"     {line}" for line in report)
    light = theme_dictionary(tokens, "light", "            ")
    dark = theme_dictionary(tokens, "dark", "            ")
    return f"""<?xml version="1.0" encoding="utf-8"?>
<!-- {DO_NOT_EDIT}
     校验：跑一次 {GENERATED_BY}，加参数 check。
     值来自老应用（1.1.0）的视觉契约 packages/quizsync_ui，不要在这里手改数值。
     字号阶 / 间距 / 圆角 / 动效四节是 P1 新增的（1.1.0 没有对应物），自检写在 {GENERATED_BY} 里。

     生成时逐条校验过的 WCAG 对比度：
{contrast_lines}
-->
<ResourceDictionary
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

    <ResourceDictionary.ThemeDictionaries>
        <ResourceDictionary x:Key="Light">
{light}
        </ResourceDictionary>
        <ResourceDictionary x:Key="Dark">
{dark}
        </ResourceDictionary>
        <ResourceDictionary x:Key="HighContrast">
{light}
        </ResourceDictionary>
    </ResourceDictionary.ThemeDictionaries>

    <!-- ================= 间距 / 圆角 ================= -->
{xaml_scale_block(tokens)}

    <!-- ================= 字号阶 =================
         重构前这里散落 14 种字号字面量（11 / 11.5 / 12 / 12.5 / 13 / 13.5 / 14 / 14.5 / 15 / 16 / 18 / 20 / 26 / 32），
         其中带小数的几个是从 1.1.0 的 Flutter 源码搬过来的像素对齐值，原生排版里没有意义。
         收敛成 QsCaption / QsBody / QsBodyStrong / QsSubtitle / QsTitle / QsTitleLarge 六档，
         **页面里不再允许出现裸 FontSize**。 -->
{xaml_type_block(tokens)}

    <!-- ================= 动效 ================= -->
{xaml_motion_block(tokens)}

    <!-- 主按钮：圆角 {radii['control']}（AppRadius.control，M9 按 Fluent 观感收的值），品牌绿填充。
         第五轮教训：这里曾经被改成 999 胶囊，是往我凭空发明的规格上拉。 -->
    <Style x:Key="QsPrimaryButtonStyle" TargetType="Button" BasedOn="{{StaticResource AccentButtonStyle}}">
        <Setter Property="Background" Value="{{ThemeResource QsBrandPrimaryBrush}}" />
        <Setter Property="Foreground" Value="{{ThemeResource QsOnBrandPrimaryBrush}}" />
        <Setter Property="BorderBrush" Value="{{ThemeResource QsBrandPrimaryBrush}}" />
        <Setter Property="CornerRadius" Value="{radii['control']}" />
        <Setter Property="Padding" Value="20,8" />
        <Setter Property="FontSize" Value="14" />
        <Setter Property="UseSystemFocusVisuals" Value="True" />
    </Style>

    <!-- 能力标签：圆角 {radii['chip']}（AppRadius.chip = 全胶囊），描边用 outline
         （**功能边界**要用 outline，不能用 cardStroke —— 后者对比度天生很低，是装饰用的）。 -->
    <Style x:Key="QsTagBorderStyle" TargetType="Border">
        <Setter Property="Padding" Value="10,4" />
        <Setter Property="CornerRadius" Value="{radii['chip']}" />
        <Setter Property="BorderThickness" Value="1" />
        <Setter Property="BorderBrush" Value="{{ThemeResource QsOutlineBrush}}" />
        <Setter Property="Background" Value="{{ThemeResource QsSurfaceBrush}}" />
    </Style>

    <!-- Fluent 卡片：圆角 {radii['card']}（AppRadius.card）+ 装饰描边（cardStroke）。 -->
    <Style x:Key="QsCardStyle" TargetType="Border">
        <Setter Property="CornerRadius" Value="{radii['card']}" />
        <Setter Property="BorderThickness" Value="1" />
        <Setter Property="BorderBrush" Value="{{ThemeResource QsCardStrokeBrush}}" />
        <Setter Property="Background" Value="{{ThemeResource QsSurfaceBrush}}" />
        <Setter Property="Padding" Value="{spacing['xl']}" />
    </Style>

    <!-- 底部状态条：通栏条带，底色 = 页面底色 canvas、文字 = 次要文字。 -->
    <Style x:Key="QsStatusBarBorderStyle" TargetType="Border">
        <Setter Property="Padding" Value="16,8" />
        <Setter Property="BorderThickness" Value="0,1,0,0" />
        <Setter Property="BorderBrush" Value="{{ThemeResource QsCardStrokeBrush}}" />
        <Setter Property="Background" Value="{{ThemeResource QsCanvasBrush}}" />
    </Style>
</ResourceDictionary>
"""


def render_windows_typography(tokens: dict) -> str:
    """字号阶的 C# 常量（代码里新建 TextBlock 时用，避免再写 12.5 这种字面量）。"""
    roles = "\n\n".join(
        f"    /// <summary>{tokens['type'][role]['note']}（{tokens['type'][role]['size']}px / "
        f"w{tokens['type'][role]['weight']}）</summary>\n"
        f"    public const double {role[:1].upper() + role[1:]} = {tokens['type'][role]['size']};"
        for role in TYPE_ROLES
    )
    return f"""// {DO_NOT_EDIT}
// 校验：跑一次 {GENERATED_BY}，加参数 check。
//
// 字号阶的 C# 取值。XAML 里用 `QsFontSizeXxx` 资源或 `QsXxxTextStyle` 样式；
// 代码里新建 TextBlock/Button 时用这里的常量 —— **不要再写 11.5 / 12.5 / 13.5 这类字面量**，
// 那几个是 1.1.0 Flutter 源码里的像素对齐值，原生排版里没有意义（原生字号是整数阶梯）。

namespace QuizSync.App.Theme;

/// <summary>跨端字号阶的 Windows 侧取值。</summary>
internal static class QsFontSizes
{{
{roles}
}}
"""


def render_windows_motion(tokens: dict) -> str:
    """动效的 C# 常量与工厂（Windows 侧动画一律走这里，不再散落魔法数字）。"""
    motion = tokens["motion"]
    capitalized = {name: name[:1].upper() + name[1:] for name in motion["duration"]}
    durations = "\n".join(
        f"    /// <summary>{name}：{value}ms</summary>\n"
        f"    public const double {capitalized[name]}Ms = {value};"
        for name, value in motion["duration"].items()
    )
    distances = "\n".join(
        f"    /// <summary>入场位移：{value}px（上限 {motion['maxDistance']}）</summary>\n"
        f"    public const double {name[:1].upper() + name[1:]}Offset = {value};"
        for name, value in motion["distance"].items()
    )
    splines = "\n\n".join(
        f"    /// <summary>{name}：cubic-bezier({', '.join(str(value) for value in points)})</summary>\n"
        f"    public static KeySpline {name[:1].upper() + name[1:]}() => new()\n"
        f"    {{\n"
        f"        ControlPoint1 = new Point({points[0]}, {points[1]}),\n"
        f"        ControlPoint2 = new Point({points[2]}, {points[3]}),\n"
        f"    }};"
        for name, points in motion["easing"].items()
    )
    return f"""// {DO_NOT_EDIT}
// 校验：跑一次 {GENERATED_BY}，加参数 check。
//
// 动效的唯一来源是 design/tokens.json 的 motion 段：任何时长都不得超过 {motion['maxMs']}ms，
// 由生成器断言（用户明确禁止「无意义的持续动画 / 页面乱飞 / 大量弹跳」）。
// 页面与控件里**不允许**再出现动画时长/曲线的字面量。
#nullable enable

using System;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace QuizSync.App.Theme;

/// <summary>跨端动效 token 的 Windows 侧取值。</summary>
internal static class QsMotion
{{
{durations}

{distances}

    /// <summary>曲线：入场用 Decelerate，出场用 Accelerate，两者都在场用 Standard。</summary>
{splines}

    /// <summary>常用时长的 TimeSpan 形式；默认 normal，需要更短/更长时显式给参数。</summary>
    public static TimeSpan Duration(bool slow = false, bool fast = false) =>
        TimeSpan.FromMilliseconds(fast ? FastMs : slow ? SlowMs : NormalMs);

    /// <summary>
    /// 系统「辅助功能 → 视觉效果 → 动画效果」关掉了吗。
    /// **每个自绘动画都必须先问这一句**：尊重这个开关是无障碍要求，不是可选项。
    /// UISettings 实例缓存住（每次都 new 会走一次 COM 创建，动画路径上不划算）。
    /// </summary>
    public static bool AnimationsDisabled()
    {{
        try
        {{
            _settings ??= new UISettings();
            return !_settings.AnimationsEnabled;
        }}
        catch (Exception)
        {{
            // 拿不到系统设置时按「用户不要动画」处理：宁可不动，也不要动错。
            return true;
        }}
    }}

    private static UISettings? _settings;
}}
"""


# ---------------------------------------------------------------- 主流程

def outputs() -> dict[Path, str]:
    tokens = load_tokens()
    report = verify_contrast(tokens)
    verify_type(tokens)
    verify_motion(tokens)
    verify_elevation(tokens)
    verify_dimensions(tokens)
    files = {
        KOTLIN_OUT: render_kotlin(tokens, report),
        ANDROID_VALUES_OUT: render_android_colors(tokens, "light"),
        ANDROID_VALUES_NIGHT_OUT: render_android_colors(tokens, "dark"),
        WINDOWS_OUT: render_windows(tokens, report),
        WINDOWS_MOTION_OUT: render_windows_motion(tokens),
        WINDOWS_TYPE_OUT: render_windows_typography(tokens),
    }
    for path, content in files.items():
        if path.suffix in (".xml", ".xaml"):
            verify_xml(content, path.name)
    return files


def main() -> int:
    parser = argparse.ArgumentParser(description="生成跨端设计 token 产物")
    parser.add_argument("--check", action="store_true", help="只校验，不写盘；有差异退出 1")
    args = parser.parse_args()

    files = outputs()
    if args.check:
        stale = []
        for path, content in files.items():
            try:
                current = path.read_text(encoding="utf-8")
            except FileNotFoundError:
                stale.append(f"{path.relative_to(ROOT)}（缺失）")
                continue
            if current != content:
                stale.append(str(path.relative_to(ROOT)))
        if stale:
            print("设计 token 产物与 design/tokens.json 不一致：", file=sys.stderr)
            for item in stale:
                print(f"  - {item}", file=sys.stderr)
            print(f"\n跑 `python {GENERATED_BY}` 重新生成；**不要**手改产物。", file=sys.stderr)
            return 1
        print(f"设计 token 一致：{len(files)} 个产物与 design/tokens.json 同步")
        return 0

    for path, content in files.items():
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content, encoding="utf-8", newline="\n")
        print(f"写入 {path.relative_to(ROOT)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
