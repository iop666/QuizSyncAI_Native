# 2.0 原生化与 Design System 计划

> 目标：**摆脱 Flutter 跨平台 UI 的视觉与交互限制**，Android 做成「真正优秀的原生 Android 工具 App」，
> Windows 做成「真正优秀的 Windows 11 原生应用」。
>
> 这不是「把 Flutter UI 翻译成 Compose / WinUI 3」，而是按两个平台各自的**原生设计语言**
> 重新设计组件、动画与交互。**视觉品牌统一，平台交互原生** —— 统一的是色相、文案、信息架构；
> 不统一的是控件形态与手势。

## 0. 参考项目（只学思路，不抄界面）

| 平台 | 仓库 | 学什么 |
|---|---|---|
| Windows | `microsoft/WinUI-Gallery` | `NavigationView` / `SettingsCard` / `SettingsExpander` / `InfoBar` / `ContentDialog` / `Flyout` / `Mica` / 控件状态动画 |
| Windows | `CommunityToolkit/Windows` | `SettingsCard`/`SettingsExpander` 的实际用法、`CommunityToolkit.WinUI.Animations`、Windows 11 设置页组织方式 |
| Android | Image Toolbox | 工具类 App 的信息架构、Material 3、设置页、BottomSheet、预览、操作反馈、深浅色 |
| Android | `android/nowinandroid` | 完整产品的组织方式、`ui/designsystem` 模块、`NiaTheme`（含 Dynamic Color）、`WindowSizeClass`、状态页 |
| Android | `android/compose-samples`（Jetsnack 等） | Compose 动画、`AnimatedContent`/`AnimatedVisibility`、共享元素转场、折叠布局、自定义 Design System |

## 1. 现状审计（2026-09-27，行数为实测）

### Android
- 入口是**手写路由**：`MainActivity.kt`(408) 的 `Route` 密封类 + `AnimatedContent` + `showingPushed` 压暗层；
  没有 Navigation-Compose、没有 WindowSizeClass、没有 BottomSheet。
- 页面：`ui/HomeShell.kt`(292)、`ui/CurrentTaskScreen.kt`(445)、`ui/HistoryTab.kt`(265)、
  `ui/SettingsTab.kt`(209)、`ui/ResultScreen.kt`(912)、`ui/RecognitionSettingsScreen.kt`(467)、`PairScreen.kt`(167)。
- 已有共享组件：`ui/components/{Components.kt(297), SessionTile.kt(147), MathText.kt(334)}`，
  但 `ResultScreen` 与 `RecognitionSettingsScreen` **各自手写了 Card/Tile/Pill/SliderRow**。
- 主题：`ui/theme/QuizTheme.kt`(269) 用 `ColorScheme.fromSeed(#FF16A34A)` 派生 + `darkColorScheme`；
  **没有 Dynamic Color、没有 Shapes/Spacing 令牌**。
- 悬浮球与截屏是原生 Kotlin（`ball/`、`capture/`），不在 Compose 树里 —— 这部分不动。

### Windows
- 主界面几乎全在 code-behind：`Pages/HomePage.xaml.cs`(**1497**) + `HomePage.xaml`(291)。
- 设置页是**手搓的一套原语**：`Pages/SettingsPrimitives.cs`(310) + `SettingsTabs.cs`(937) + `SettingsPage.xaml.cs`(157)，
  **没有用 `NavigationView`，也没有 `SettingsCard`/`SettingsExpander`**。
- 卡片与令牌：`Controls/QuestionCardView.xaml.cs`(546)、`DesignTokens.xaml`(114)、
  `Theme/{ShellStyles.xaml(164), ShellLayout.xaml(301), ShellTheme.cs(91)}`。
- 其他：`FloatingBall.cs`(498)、`TrayController.cs`(97)、`App.xaml.cs`(492)。

### 两端共同的空白
| 端 | 动画相关行数（`animate|Animated|tween|spring` / `Storyboard|Composition|Animation|Transition`） |
|---|---|
| Android | **13** |
| Windows | **0** |

即：**两端基本没有动画层**（不是做得差，是还没做）。

## 2. Design System 目标结构

### Android —— `ui/designsystem/`
- `QsTheme`：`ColorScheme`（品牌 seed + **Dynamic Color 开关**，API 31+）、`Typography`、`Shapes`、
  `Spacing`/`Elevation` 令牌。
- 组件：`QsTopAppBar`、`QsNavigationBar`、`QsCard`、`QsListItem`、`QsSwitch`、`QsTextField`、
  `QsButton`、`QsDialog`、`QsBottomSheet`、`QsSnackbar`、`QsLoading`、`QsEmpty`、`QsError`、`QsSegmentedButton`。
- **硬规矩**：页面里不再出现裸 `Card(...)`/`Slider(...)`，只用 `Qs*`；间距/圆角/颜色不许写字面量。
- 导航：补 `WindowSizeClass` 适配（手机/平板/折叠屏）；转场用 Material Motion。

### Windows —— `Controls/Qs*.cs` + `Theme/QsTokens.xaml`
- **优先原生控件**：`NavigationView`、`SettingsCard`、`SettingsExpander`、`InfoBar`、`ContentDialog`、
  `Flyout`、`ToggleSwitch`、`ComboBox`、`CommandBar`、`Mica` 背景、`ThemeShadow`。
- 只在原生不够时做自定义（题目卡的命中态、公式排版、悬浮球/悬浮窗）。
- 目标：`SettingsTabs.cs` 从「手搓控件」降到「声明式描述 + `SettingsCard` 组合」。

## 3. Windows 设置页的目标形态（**保留 10 个分类**）

用户 2026-09-27 明确要求：**左侧 10 个分类不变**：

`使用说明` / `显示设置` / `API 配置` / `连接设备` / `识别设置` / `悬浮球设置` / `悬浮窗设置` / `热键设置` / `数据管理` / `关于`

实现方式改为：`NavigationView`（左侧导航） + `ScrollViewer` + `SettingsCard` / `SettingsExpander`（右侧内容），
信息组织照 Windows 11 设置页的习惯。**分类与每项的落库键位都不变**（只换外衣，不换行为）。

> **P3 落地时的取舍（`SettingsExpander` 没用）**：现有 10 个分类里，一个 `Group` 是「一个标题 + 若干扁平行」，
> 没有「主开关 + 从属子项」那种形状。把扁平行塞进 `SettingsExpander.Items` 就要**默认折叠**，
> 那等于把设置项藏起来 —— 是行为变更，不只是换外衣。
> 参照物 WinUI-Gallery 自己的设置页也是这么分的：**扁平行用 `SettingsCard` + 段头 `TextBlock`**，
> 只有「Sound」「About」这种真有从属项的才用 `SettingsExpander`。
> 所以 P3 用「段头 + `SettingsCard` 栈」，`SettingsExpander` 留给**确实需要折叠的分组**（等产品指定哪些组可折叠再接）。

## 4. 分阶段迁移（每阶段结束：编译 0 警告 0 错误 + 测试全绿 + 该阶段自己的验证）

| 阶段 | 内容 | 绿线 |
|---|---|---|
| **P0** | 本文件 + `DECISIONS.md` 记决策 | 纯文档 |
| **P1** | 两端**令牌层**（color/typography/shape/spacing/elevation）；页面暂不改 | ✅ **已完成**（2026-09-27，`835454f` + `701f3b8`）：字号阶 / 动效 / 层级 / 尺寸四节进 `design/tokens.json`，生成器新增四道自检（见 §8.1）；Windows 构建 0/0、测试 215、Android 编译 0/0、单测 23 |
| **P2** | Android `Qs*` 组件全套；把 `RecognitionSettingsScreen`/`SettingsTab`/`HistoryTab` 换成 `Qs*`（行为不变） | ✅ **已完成**（`658316f`）：`ui/designsystem/QsComponents.kt` 12 个组件；除计划里的三页外，`ResultScreen`/`CollectionDetailScreen`/`CurrentTaskScreen`/`HomeShell` 一并接入；`ui/components` 删掉 `SectionCard`/`StatusPill`/`EmptyState`（127 行） |
| **P3** | Windows 设置页：`NavigationView` + `SettingsCard`/`SettingsExpander`，10 分类归位、键位不变 | ✅ **已完成**（2026-09-27）：构建 0/0 + 测试 209 通过 + `verify_windows_ui.ps1` 38 PASS + 设置落库复核；`NavigationView`/`SettingsCard`/`InfoBar` 已上，`SettingsExpander` **未用**（见下注） |
| **P4** | Android 首页/结果页的 Material 3 信息架构（BottomSheet 选图、可折叠 TopAppBar、状态页） | ✅ **已完成**（本次）：`ModalBottomSheet` 选合集、`LargeTopAppBar` + `exitUntilCollapsedScrollBehavior` + `nestedScroll`、`QsStateView` 三态统一、`currentWindowAdaptiveInfo()` 宽屏换 `NavigationRail`、`enableEdgeToEdge` 跟随应用主题、动态取色开关（API 31+ 才显示） |
| **P5** | **动画层**：Android 用 Compose Animation/Material Motion（共享元素转场、卡片展开、BottomBar 切换）；Windows 用 Composition/ConnectedAnimation（页面转场、InfoBar 进出、设置项展开） | ✅ **已完成**：Android 切标签 fade-through + 共享元素单点（历史→合集详情，见 §8.3）+ **阅读材料面板的展开/收起**（`AnimatedVisibility` + `expandVertically`/`shrinkVertically`，时长与曲线取 `QsTokens.Motion`，见 §9.2）；Windows 原生转场 + 页面入场 + 内容替换（`701f3b8`）。**刻意不做**：`ConnectedAnimation`（理由见 §8.7）；`InfoBar`/`ContentDialog` 进出（WinUI 原生行为，自写会与系统动效打架）。⚠️ **安卓侧三项已作废**：2026-09-28 第 159 轮按用户口径「删除除了进入页面和返回页面外的所有动画」，共享元素、卡片展开、同级横向切换与列表项按下形变**全部删掉**，现状见 §9.7（本节保留当时的描述） |
| **P6** | Windows 悬浮窗（规格 §6.1–6.5）与打包/自动拉起 | ✅ **已完成**（`fd2218c` + `7b2c7f3` + 本轮）：真 XAML 窗口 + 13 项设置（`float_*` 键位、默认值、三种外观、6 套 seed 与 1.1.0 一致）；本轮按规格补齐**不抢焦点**、**下一条/上一条方向**、**识别结束回到最新**、头部 `第 N/M 次识别` + `YYYY-MM-DD HH:mm`、默认位置 `y=120`（见 §9.3）。**自动拉起主机与识别服务** ✅（`HostLauncher` + `ProviderLauncher`；第 169 轮起由设置页「开启连接设备」这个总开关一键启动，并新增「重启连接服务」）。**边界**：「打包」归 **Phase 8（发布）**，本计划不重复实现（`tools/package_windows.ps1` 仍是 Flutter 线） |

**禁止**（用户明确列出）：大量弹跳、过度缩放、页面乱飞、无意义的持续动画、为了好看而加动画。

## 5. 不动的部分（复用，不重写）

协议与同步（`QuizSync.Core`/`QuizSync.Data`、`RecordStore`）、业务状态机（`QuizViewModel` 的业务部分）、
Provider/`AnalysisEngine`、悬浮球与截屏（原生）、内容解析（`MathContent`，两端规则同源）。
信息架构与文案以 `ui-spec-android.md` / `ui-spec-desktop.md`（1.1.0 逐条提取）为准 ——
**照它的信息架构，不照它的像素**。

## 6. 参考仓库结论（2026-09-27，子代理实地读源码所得）

参考仓库已浅克隆到 **`D:\ZCode\refs\`**（`WinUI-Gallery` 19.7MB / `Windows` 6.2MB /
`nowinandroid` 32.8MB / `compose-samples` 98.2MB，均 `--depth 1`）。
完整 findings（3114 行，带文件路径与行号摘录）在
`D:\DeepseekHarness\TemporaryConversationFile\misc\design-refs-findings.md`；
下面是**动手前必须知道的硬事实**。

### Windows

| 事实 | 依据 | 对我们的意思 |
|---|---|---|
| `SettingsCard`/`SettingsExpander` 在 `CommunityToolkit.WinUI.Controls.SettingsControls`，**最新稳定 8.2.251219** | `WinUI-Gallery/standalone.props` L9；8.3.x 只有 preview | 已装 `8.2.251219` |
| 该包**只带 Triggers**，不带 Extensions | nuspec 依赖组实测 | XAML 用原生 `<FontIcon>`，别为 `ui:FontIcon` 多引包 |
| 库的 `.pri` 会由构建期合并进 App 的 `resources.pri`，**未打包同样生效** | nupkg 内含 `…SettingsControls.pri` | **不需要**手动 merge ResourceDictionary |
| **包内没有分组容器**（`SettingsGroup` 0 命中） | 全仓库 grep | 分组靠 `SettingsExpander.Items`；官方 workaround：设了 `MaxWidth` 的 `StackPanel` 必须包在 `Grid` 里，否则展开有视觉故障 |
| **`<TitleBar>` 控件在 1.7 可用**（⚠️ 首次判断被编译实证推翻）；`SystemBackdropElement` 才是 1.8+，我们没有 | 编译探针 A/B/C/D：`TitleBar` 及全部成员 exit=0，`SystemBackdropElement` → `CS0246` | Gallery `MainWindow.xaml` L24-L30 + `.xaml.cs` L148-L151 **可原样照抄**；只需自己加 `AppWindowTitleBar.IsCustomizationSupported()` 兜底 |
| Gallery 是**打包（MSIX）**应用，两项功能明说 unpackaged 不可用 | `BadgeNotificationManagerPage.xaml` L13、`JumpListPage.xaml` L12-L13 | 与「背景支持性」**无关**（勘误）：`IsSupported()` 的官方摘要是 「supported on the **current operating system**」—— 是**系统**判据。Mica 兜底的真正原因是 `TargetPlatformMinVersion=10.0.19041.0`（Win10 不支持 Mica） |
| `NavigationView` / `SettingsCard` / `ThemeTransition` / `ContentDialog` / `ConnectedAnimation` | 3.1 节逐项对照表 | **可原样照抄**，未打包不影响 |

### Android

| 事实 | 依据 | 对我们的意思 |
|---|---|---|
| 窗口尺寸类用**新 API** `currentWindowAdaptiveInfo()` | `nowinandroid/app/…/ui/NiaApp.kt` L40-L43 import、L93；`core/designsystem/…/Navigation.kt` L186-L193 | **不要**抄 Reply 里的 `calculateWindowSizeClass(this)`（旧 API，要 `@OptIn(ExperimentalMaterial3WindowSizeClassApi::class)`） |
| 动态取色开关是**反的**且默认关 | `core/designsystem/…/theme/Theme.kt` **L192-L208**：`disableDynamicTheming: Boolean = true`，`when { androidTheme -> …; !disableDynamicTheming && supportsDynamicTheming() -> dynamic…; else -> … }`；版本判断在 L249-L250（`@ChecksSdkIntAtLeast(api = S)`） | 照它的语义接，别自己发明开关方向 |
| `enableEdgeToEdge` 的正规写法**正好解决我们记了三轮的 Android 15 状态栏问题** | `nowinandroid/app/…/MainActivity.kt` **L115-L124**：`SystemBarStyle.auto(lightScrim = TRANSPARENT, darkScrim = TRANSPARENT) { darkTheme }` —— scrim 透明（App 自绘底色）+ **显式传 App 内主题**而非系统配置 | 用它替换我们手写的那套；`SystemBarStyle.auto { }` lambda 重载建议把 `activity-compose` 从 1.11.0 升到 **1.13.0** |
| 共享元素转场在**手写路由**下没有范例可抄（所有参考都用 `NavHost`） | `Jetsnack/…/JetsnackApp.kt` L65-L68、L169-L170；`Home.kt` L87-L126 | **先在一个页面对（历史→详情）做单点验证**再铺开；等价接线是 `AnimatedContent` 的 content lambda 里 `CompositionLocalProvider` 两个 scope |
| 折叠标题栏**三个 sample 全未覆盖** | Jetsnack/Jetcaster/Reply 里 `TopAppBarScrollBehavior`/`LargeTopAppBar`/`nestedScroll` 全 0 命中；Jetsnack 是手算 offset（`SnackDetail.kt` L403-L407） | 这条要**自己定方案**，没有可抄的 |

### 该引入的依赖

**NuGet**（已加进 `QuizSync.App.csproj`）：`CommunityToolkit.WinUI.Controls.SettingsControls` 与
`CommunityToolkit.WinUI.Animations`，均 `8.2.251219`。XAML 命名空间
`xmlns:controls="using:CommunityToolkit.WinUI.Controls"`、`xmlns:animations="using:CommunityToolkit.WinUI.Animations"`。

**Gradle**（`android/app/build.gradle.kts`）—— ⚠️ **不要写版本号，交给现有 `compose-bom:2025.09.00`**：

```kotlin
implementation("androidx.compose.material3.adaptive:adaptive")                    // currentWindowAdaptiveInfo()
implementation("androidx.compose.material3:material3-adaptive-navigation-suite")  // NavigationSuiteScaffold
// 不要加 androidx.window:window-core —— adaptive 已传递依赖它
```

写死高版本**反而危险**：BOM 2025.09.00 把 `adaptive` pin 在 **1.1.0**、
`material3-adaptive-navigation-suite` pin 在 **1.3.2**、`material3` 在 **1.3.2**
（`foundation`/`ui`/`runtime` = 1.9.1），这是一起测试过的组合；
`navigation-suite:1.4.0` 会要求 `material3:1.4.0`，等于**只把一件拉出 BOM 测试矩阵**。
动态取色、`enableEdgeToEdge`、折叠标题栏**都不需要新增依赖**（见 §6.3）。

### 6.1 编译实证与两处勘误（2026-09-27，探针可复现）

**勘误 1：`<TitleBar>` 控件在 WinAppSDK 1.7.250606001 上完全可用。** 之前判断「1.8+ 才有」是错的 ——
依据是 1.7 包里的 `Microsoft.WinUI.xml` **缺** `T:Microsoft.UI.Xaml.Controls.TitleBar` 文档条目，
属于「文档缺失导致误判」。编译探针：`TitleBar` 及 `Title`/`Subtitle`/`IsBackButtonVisible`/
`IsPaneToggleButtonVisible`/`BackRequested`/`PaneToggleRequested`/`IconSource`/`Content` 全部 exit=0。
**`SystemBackdropElement` 确实没有**（`CS0246`），这一条原判断正确。

**勘误 2：Mica 的兜底原因不是「未打包」。** `MicaController.IsSupported()` 的官方摘要原文是
"supported on the **current operating system**" —— 与打包方式无关；`AppWindowTitleBar.IsCustomizationSupported()`
同理是能力判据。我们真正需要兜底的原因是 `TargetPlatformMinVersion=10.0.19041.0`（Win10 上 Mica 不支持）。

**新发现（原样照抄项）**：我们正要上「自定义标题栏 + `NavigationView` 设置页」，
这会**必然踩到** 官方 issue microsoft-ui-xaml#9934 —— 最大化/还原时两者有 1–2px 错位。
Gallery 的 workaround 在 `WinUI-Gallery/WinUIGallery/MainWindow.xaml.cs` **L48-L57 + L98-L110**
的 `AdjustNavigationViewMargin`，**必须一起抄**。

**已实证的接入点**：`MainWindow.xaml` 目前只有 `<Frame x:Name="RootFrame"/>`（**没有标题栏元素，要先加**）；
`MainWindow.xaml.cs` L24-L31 的构造里、`WindowSizing.Apply` 附近接 backdrop 与标题栏调用。

**依赖生效的实证**：`bin\…\win-x64\` 下已有 `resources.pri` 与
`CommunityToolkit.WinUI.{Controls.SettingsControls,Animations,Extensions,Helpers,Triggers}.dll`
—— 这实证了「`.pri` 由构建期合并、**不需要手动 merge ResourceDictionary**」这条结论。

### 6.2 方法论（值得写进 AGENTS.md 的一条）

> **判断「某个 API 在某个 SDK 版本上有没有」不要靠 XML 文档或记忆，要用编译探针。**
> 本次就是「文档缺条目 → 误判 API 不存在」。可复用的最小形态：一个
> `ExcludeAssets="build;buildTransitive"` + `EnableDefaultCompileItems=false` 的库工程，
> 引用目标版本包，放一行 `typeof(...)` 或一个成员赋值，看编译器结果。
>
> 两个构建陷阱：① 不设 `ExcludeAssets="build;buildTransitive"` 会撞 `MrtCore.PriGen.targets` 的
> `MSB4062`（缺 VS 的 `Microsoft.Build.Packaging.Pri.Tasks.dll`，纯 dotnet SDK 上没有）；
> ② 不关 `EnableDefaultCompileItems` 会把探针文件一起编进去，**可能得出相反结论**。

### 6.3 Android 侧字节码实证与三处勘误（2026-09-27，`javap` 可复现）

| 原结论（已被推翻） | 实证 | 该怎么做 |
|---|---|---|
| 加 `adaptive:1.3.0`、`navigation-suite:1.4.0`、`window-core:1.5.1` | BOM 2025.09.00 已 pin **1.1.0 / 1.3.2**；`window-core` 由 `adaptive` 传递依赖（`adaptive-android-1.1.0.module`） | **两行、不写版本号**，`window-core` 整行不要 |
| `enableEdgeToEdge` 的 lambda 重载要升 `activity-compose` 到 1.13.0 | **1.11.0（现有版本）就有**：`javap androidx.activity.SystemBarStyle` → `auto(int, int, Function1<Resources, Boolean>)`；1.11.0 与 1.13.0 公开签名一致 | **一行依赖都不用动** |
| `currentWindowAdaptiveInfo()` 要 `@OptIn` | `javap -v`：它**无注解**（稳定）；只有 `currentWindowDpSize`/`currentWindowSize` 是 `@ExperimentalMaterial3AdaptiveApi` | 不用 OptIn |

> 踩坑：`enableEdgeToEdge` 的 **JVM 名是 `enable`**（`@JvmName` 改名），按名字 grep 方法表找不到它 ——
> 第一次就栽在这里，误判「1.11.0 没有」。**看字节码要看真实 JVM 名。**

**从「没法抄」变成「可以直接抄」**：M3 滚动折叠标题栏在 BOM pin 的 `material3:1.3.2` 里齐全
（`TopAppBarDefaults.pinnedScrollBehavior`/`enterAlwaysScrollBehavior`/`exitUntilCollapsedScrollBehavior`、
`LargeTopAppBar`/`MediumTopAppBar`）—— **不必照 Jetsnack 手算 `rememberScrollState` + `.offset{}`**：

```kotlin
val scrollBehavior = TopAppBarDefaults.exitUntilCollapsedScrollBehavior()
Scaffold(
    modifier = Modifier.nestedScroll(scrollBehavior.nestedScrollConnection),
    topBar = { LargeTopAppBar(title = { /* … */ }, scrollBehavior = scrollBehavior) },
) { /* … */ }
```

两点：① 必须配 `LargeTopAppBar`/`MediumTopAppBar`（配 `TopAppBar` 不折叠）；
② `Modifier.nestedScroll` 挂在**承载滚动容器的 Scaffold/父布局**上。

**动态取色确认无需动依赖**：`javap androidx.compose.material3.DynamicTonalPaletteKt`（1.3.2）有
`dynamicLightColorScheme(Context)` / `dynamicDarkColorScheme(Context)`；
`ColorScheme` 的五个 `surfaceContainer*` 角色都在（现有 `QuizTheme.kt` L85-L89 的用法是安全的）。
## 7. Mica 背板的实测证据（2026-09-27）

**先纠正一条错误判据**：复核曾用 `DwmGetWindowAttribute(DWMWA_SYSTEMBACKDROP_TYPE) = 0 (Auto)`
论证「Mica 没生效」—— 这条**不成立**。`Theme/QsBackdrop.cs` 用的是现代 API
（`Window.SystemBackdrop` + `MicaBackdrop` + `MicaController`），走 WinUI 的 **Composition** 背板路径；
而 `DWMWA_SYSTEMBACKDROP_TYPE` 是 **Win32 应用 opt-in「系统背板」**时才设置的属性，两者不是同一条路。
**唯一能定的判据是像素 A/B。**

**改动**（`7c76fc3`）：`ShellRoot`、`RootFrame`、原生 `TitleBar` 的背景由不透明画布改成
`Transparent`（页面内容区仍由各 Page 铺不透明底色）。改前它们被 `#FFF3F5F4` 完全盖住。

**探针**：`tools/probe_mica_ab.ps1` —— 把窗口平移 520×260 物理像素，在同一**逻辑**位置采样。
两个坑写在脚本注释里：① `$proc.MainWindowHandle` 会抓到**悬浮球**（96×96），必须自己
`EnumWindows` 取该进程可见顶层窗口里**面积最大**的那个；② 必须 `SetProcessDPIAware()`，
否则 `GetWindowRect`/`CopyFromScreen` 拿到的是虚拟化坐标。

**实测（本机 Win11 22631 / 200% DPI）**：

| 采样点 | 平移前 | 平移后 | 判读 |
|---|---|---|---|
| 标题栏条（顶 +28 物理 ≈ 逻辑 14） | `#D0DBEC` | `#D0DCE7` | **变化** → 背板在合成桌面；且该色**既不是**画布 `#F3F5F4` **也不是**卡片白，是桌面经 Mica 的色调 |
| 内容区（顶 +420 物理）作对照 | `#FFFFFF` | `#FFFFFF` | **不变** ✔ 符合预期（内容区不透明） |

另记一次**不干净的采样**：首次运行在启动后 9 秒采样，两点都是 `#151517`（渲染未完成的深色瞬间），
得出「两处都变了」的假象 —— 所以探针必须先自检状态稳定再采（等待约 22 秒）。

**未验**：`MicaController.IsSupported()` 为假时的亚克力/纯色兜底（本机支持 Mica，走不到那条分支）。

## 8. P1/P2/P4 实施记录与勘误（2026-09-27，本轮）

### 8.1 令牌层新增的四道自检（都可机检，不靠人记）

`tools/gen_design_tokens.py` 现在除了原有的 WCAG 对比度校验，还断言：

| 自检 | 规则 | 为什么 |
|---|---|---|
| `verify_type` | 字号阶逐档**不减**，且同字号的相邻两档**必须字重不同** | 重构前两端散落 14 种字号字面量（含 11.5 / 12.5 / 13.5 这些 1.1.0 的 Flutter 像素对齐值）。第一版规则写成「严格递增」，结果**自己的 `body`/`bodyStrong` 先被拦下来**（生成器当场退出 1）—— 同字号不同字重是合法的一档 |
| `verify_motion` | 时长 ≤ `maxMs`(400)、位移 ≤ `maxDistance`(24)、曲线控制点落在 [0,1] 且非恒等 | 用户禁令「无意义的持续动画 / 页面乱飞」的可执行版本。想加长动画就必须改这两个数，改的时候会被 git diff 记下来 |
| `verify_elevation` | `card == 0`，且 flat ≤ card ≤ raised ≤ overlay | 1.1.0 的 `cardTheme` / `appBarTheme` 都是 `elevation: 0`，整站平铺 + 描边。这条防的是「后来有人凭 Fluent 习惯给卡片加阴影」 |
| `verify_dimensions` | 全部为正数；`hairline` 必须为 1 | 图标尺寸 / 描边粗细这类值最容易变成散落的字面量（重构前 Android 侧实测有 12.5 / 13.8 / 38 / 19 这些） |

### 8.2 三处方向性修正（都是「第一版做错了，改回来」）

**① Android 不复制 Windows 的字号数值。** 第一版按同一套 px 值生成了 Kotlin 的 `QsTokens.Type`，
但 Android 的原生排版语言就是 M3 Typography：把 Windows 的 20px 硬塞成 20sp 会让
`LargeTopAppBar` 的标题从原生 22sp 掉到 20sp —— **那正是用户不要的「跨端模仿」**。
现在 Android 侧**只生成「角色 → M3 槽位」的映射**（caption→bodySmall、body→bodyMedium、
bodyStrong→titleSmall、subtitle→titleMedium、title→titleLarge、titleLarge→headlineSmall），
品牌统一落在**角色**上，度量各自随平台。生成器里那段 `Type` 对象因此被删掉（避免生成死代码）。

**② `QsTopBar` 必须包 M3 原生 `TopAppBar`，不能手搓 `Row`。** 第一版我写成了自己拼的 `Row` ——
那一版会丢掉状态栏 insets、滚动容器色、折叠行为，等于把原生行为重实现一遍且实现不全。
改成 `TopAppBar` 包装后还发现一条**签名约束**：`TopAppBarScrollBehavior` 是实验性类型，
一旦出现在 `QsTopBar` 的签名里，**每个调用点**都会被要求 opt-in（实测报错位置指在调用处，
不在定义处）。所以折叠版单独开了 `QsCollapsibleTopBar`，由用它的页面自己 opt-in 一次。

**③ 全局 `Shapes` 只影响 `Card`，不影响 `Button`（字节码实证）。**
`CardDefaults.getShape()` → `FilledCardTokens.getContainerShape()` → 静态字段值是
`ShapeKeyTokens.CornerMedium` → `ShapesKt.getValue` 读 `MaterialTheme.shapes.medium`；
而 `ButtonDefaults.getShape()` → `FilledButtonTokens.getContainerShape()` → **`CornerFull`**，
`ShapesKt.fromToken` 对 `CornerFull` 返回**常量**（不读 `Shapes.*`）。
所以 `quizShapes()` 把 `medium` 设成 16 之后，裸 `Card(...)` 自动拿到 1.1.0 约定的卡片圆角，
而按钮**仍然是 M3 原生的胶囊形**（`javap -p -c` 于 material3 1.3.2 的 `classes.jar`，可复现）。

### 8.3 本轮的实测发现（不是推断）

* **`MainActivity` 绕过了 `QuizSyncTheme`**（`MaterialTheme(colorScheme = …)` 直接调）——
  于是 P1 加的 `quizShapes()` / `quizTypography()` **从来没生效过**，`LocalQuizPalette`
  也没提供（各页读到的永远是浅色那一版固定色值）。这是「状态层齐了、连接线没接」的第 7 次。
  已改为统一走 `QuizSyncTheme`。
* **动态取色的语义照 nowinandroid 的反向开关**（§6 表）：`disableDynamicTheming: Boolean = true`。
  落库键位仍是正向的 `dynamic_color`（用户视角「要不要动态取色」），只在主题 API 这一层取反。
  API 31 以下**整项不显示**，而不是给一个永远点不动的灰开关。
* **`enableEdgeToEdge` 的 lambda 重载在现有 `activity-compose 1.11.0` 上就能用**
  （与本文件 §6.3 的字节码结论一致，本轮编译实证：无需升到 1.13.0）。
  接法：`LaunchedEffect(appDark) { enableEdgeToEdge(statusBarStyle = SystemBarStyle.auto(TRANSPARENT, TRANSPARENT) { appDark }) }`
  —— 系统栏图标明暗跟随**应用**明暗，而不是系统 uiMode（「系统浅色 + 应用深色」时这是唯一解）。
  导航栏**不动**（保留 `enableEdgeToEdge()` 默认），三键导航下更安全。
* **`androidx.window.core.layout.WindowWidthSizeClass` 不需要显式依赖**：
  加了计划 §6.3 指定的两行（`adaptive` + `material3-adaptive-navigation-suite`，**不写版本号**）之后，
  `currentWindowAdaptiveInfo()` 与其返回值类型都能直接编译（本轮实证，无需 `window-core`）。
* **共享元素在手写路由下的接线**（§6 表说「没有范例可抄」）：等价做法是把两个作用域放进
  `CompositionLocal` —— `SharedTransitionLayout` 提供 `SharedTransitionScope`（套在路由区外面），
  `AnimatedContent` 的 content lambda 提供 `AnimatedVisibilityScope`。见
  `ui/designsystem/QsSharedTransition.kt`；**单点对照**选的是「历史页合集行 → 合集详情页标题」，
  key 取合集 id。缺任一作用域时 `qsSharedElement` 原样返回 Modifier（不报错、不动画）。

### 8.4 刻意划出的边界：内容区指标 ≠ 站点 chrome

令牌治理的是**共享词汇**（图标阶梯、描边粗细、间距、圆角、动效、页面内边距）与**站点 chrome**。
题目卡**内部**的内容指标（徽标内边距 9/3、选项竖条缩进 26、分析块 12/10、材料块 …）来自
1.1.0 规格 §3.5 / §6.2，是**内容**的排版契约 —— 它们暂时保留字面量。
理由：把这些值硬塞进 `spacing` 会改变观感，而观感在无人值守下无法验收；
那属于「为了过规矩而改观感」，不做。`ResultScreen.kt` 文件头已注明这条边界。
本轮已收敛的是该文件里的**共享词汇**部分（47 处：图标 14/15/16/18 → 令牌阶梯、
1/2/4dp 描边 → `hairline`/`strokeEmphasis`/`strokeMarker`、页面级间距与内边距）。

### 8.5 Windows 侧的一处刻意偏差（要有记录）

计划 §6.1 要求「原样照抄」WinUI-Gallery 的 `AdjustNavigationViewMargin`（官方 issue
microsoft-ui-xaml#9934 的 workaround）。实际用的是等价的 `Theme/TitleBarTrim.cs`
（把补偿挂在标题栏正下方那个元素上），并且**实测有效**：
`NavigationView` 顶边 257 = `TitleBar` 底边 261 − 4 物理像素（200% DPI）。
不改回 Gallery 版本的原因：它只在**最大化**时调整 margin，而我们这条对
「普通尺寸 / 最大化 / 还原」三种情况用同一套补偿；且本轮 `verify_windows_ui.ps1` 的
13 项 P3 断言在此实现下全 PASS。**未验**：最大化/还原切换时 `-1` 那一支的观感。

### 8.6 存量字号字面量的收敛方式：**纯数值替换，不套 Style**

Windows 侧重构前散着 14 种字号字面量，其中 `11.5 / 12.5 / 13.5 / 14.5` 是从 1.1.0 的
Flutter 源码逐字搬来的**像素对齐值** —— 原生排版是整数阶梯，这几个值没有意义。

收敛方式选的是「把数值换成令牌引用」，而不是「换成 `Style="{StaticResource QsBodyTextStyle}""`：

| 方式 | 副作用 | 本轮取舍 |
|---|---|---|
| 换 Style | 顺带带上 `TextWrapping=Wrap` 与前景色，可能改变既有换行行为（长标签从「裁掉」变成「折行」） | ✗ 不用 |
| 换数值令牌 | 只改一个数，零副作用 | ✓ 用它 |

对应地，生成器同时产出两套东西：`QsXxxTextStyle`（新 XAML 用）与
`QsFontSizeXxx`（x:Double，存量收敛用）；C# 侧新增 `Theme/QsTypography.g.cs`
（`QsFontSizes.Caption/Body/…`）。映射：

```
11 / 11.5 / 12 / 12.5 → caption(12)      13 / 13.5 / 14 / 14.5 → body(14)
15 / 16               → subtitle(16)     17 / 18 / 20          → title(20)
26                    → titleLarge(28)
```

本轮替换 XAML 13 处、C# 40 处。**刻意没动**的是 `FontIcon FontSize`（18 处）：
那是**图形尺寸**不是文字字号，该走图标阶梯令牌 —— 而图标阶梯的观感取舍需要有人看着屏幕定，
无人值守下不做（清单在脚本输出里，`tools` 之外留档于 `misc/`）。

绿线：`QuizSync.App` 构建 0 警告 0 错误；`QuizSync.Tests` 215 全绿；
`tools/verify_windows_ui.ps1` **PASS 38 / FAIL 0**（与重构前基线一致）。

### 8.7 Windows 动画为什么用原生转场而不是 `ConnectedAnimation`

计划 §6 的参考结论里，`ConnectedAnimation` 与 `NavigationView`/`ThemeTransition` 一样属于
「可原样照抄」。本轮**没有**用它，理由是它的适用场景与本项目的导航结构不匹配：

* `ConnectedAnimation`（`ConnectedAnimationService`）是为「**同一元素在两个页面间连续移动**」
  设计的，需要 `PrepareToAnimate` / `TryStart` 成对调用，且**跨 Frame 导航**时依赖页面
  生命周期回调；典型用法是「列表缩略图 → 详情大图」。
* 本项目的压栈页只有两种（合集详情、识别模块设置），导航一律是**层级前进/后退**，
  WinUI 为此提供的就是 `DrillInNavigationTransitionInfo` / `SlideNavigationTransitionInfo` ——
  它们自带「旧页退出 + 新页进入」的完整时序，还考虑了输入方向与打断。
* 真正的「共享元素」需求出现在 Android 那一侧（历史页合集行 → 详情页标题），
  已按计划做了单点验证（§8.3）。Windows 侧的对应物是「会话列表行 → 结果页标题」，
  但那需要 `ConnectedAnimation` + 列表项虚拟化（`ListView` 回收容器）配合，
  是**另一个需要真机观感判断的独立任务**，本轮不做，记在这里避免以后误以为漏了。

---

## 9. 收口（2026-09-27，P5/P6 的最后两项）

### 9.1 P2 硬规矩的扫尾（页面里不许有裸 M3 原语）

`QsComponents.kt` 文件头那三条硬规矩，本轮**实测扫了一遍** `android/app/.../dev/**`，并补齐了所有页面：

| 对象 | 状态 |
|---|---|
| `SettingsTab` / `HistoryTab` / `RecognitionSettingsScreen` / `ResultScreen` / `CollectionDetailScreen` / `CurrentTaskScreen` / `HomeShell` / `PairScreen` | ✅ 只用 `Qs*` + 令牌 |
| `RecognitionSettingsScreen` 的**私有 `SliderRow`** | ❌→✅ 它内部裸用 `Slider`（3 个调用点：透明度 / 大小 / 最多页数）。已**原样**搬进设计系统（`QsInlineSliderRow`：版式与「松手才提交」语义逐字不动），页面删掉私有副本，并清掉因此失效的 5 个 import |
| 其余页面的裸 `OutlinedButton` / `TextButton` / `FilledTonalButton` / `OutlinedTextField` / `Card` | ❌→✅ 本轮逐个换成 `QsSecondaryButton` / `QsTextButton` / `QsTonalButton` / `QsTextField` / `QsCard`（新增 5 个薄壳组件，形状与配色按 §8.4 的边界**保持原样**） |

为什么行内版式单独开一个组件而不是给 `QsSliderRow` 加开关：两者**版式**不同（块状「标题行 + 下方整条滑杆」
vs 行内「标签 78 / 滑杆 / 数值 52」），合并要加一个布尔参数，比两个具名组件更难读。

**一处顺带的、必须写下来的观感变化**：`QsPrimaryButton` / `QsSecondaryButton` / `QsTonalButton` 把形状
**定死**成 `RoundedCornerShape(QsTokens.radiusControl)`（10dp）。改之前这些调用点没写 `shape`，
走的是 M3 默认的 `CornerFull`（胶囊形）—— 也就是说**按钮从胶囊变成了 10dp 圆角矩形**。
这是**有意**的：10dp 才是 1.1.0 的按钮圆角契约（`AppRadius.control`），而且改之前同一个应用里
有的按钮是 10dp（写了 `shape`）、有的是胶囊（没写），本身就不一致。
形状收进组件层之后，调用点不必再知道它 —— 漏写一处就掉回胶囊的那个坑也一并消失。

`PairScreen` 的真机回归（本轮实测）：`tools/verify_android_pair.ps1` → **✅ 配对成功**
（三模块渲染 → 三个输入框逐字填入 → 「开始配对」触发请求 → 主机设备表出现新设备 → 手机回到主壳）。
脚本本身修了一处**选错网卡**的缺陷：这台机器同时有 USB 网络共享（`192.168.231.x`）与 Wi-Fi（`192.168.101.x`），
原来「第一个非回环 IPv4」拿到的是 USB 那条链路，手机连不上它（报 `failed to connect … after 5000ms`），
看起来像配对页坏了。现在先从手机 `ip route` 问出 Wi-Fi 网段、再挑同网段的电脑地址。

### 9.2 P5「卡片展开」

* 位置：结果页阅读材料面板（`ResultScreen.kt` 的 `MaterialPanel`，规格 §3.4.10，默认折叠）。
* 改法：`if (open) { MathText(...) }` → `AnimatedVisibility(visible = open, …)`，
  进入 = `expandVertically(tween(Motion.normal, decelerateEasing)) + fadeIn(Motion.fast)`，
  退出 = `shrinkVertically(tween(Motion.fast, accelerateEasing)) + fadeOut(Motion.fast)`，`expandFrom/shrinkTowards = Alignment.Top`。
* 为什么不违反 §4 的禁令：这是**用户点出来**的状态变化；时长 120/200ms、位移 0（纯高度展开），
  都在 `design/tokens.json` 的 `verify_motion` 界内（≤400ms、≤24dp），曲线取令牌而不是手写贝塞尔。

### 9.3 P6 悬浮窗：一次逐条规格比对（详见 `DECISIONS.md` 第 152 轮）

把「规格（`ui-spec-desktop.md` §3.8 + §6.1–6.6）」与「实现（`Pages/FloatWindow.xaml(.cs)`、`App.xaml.cs`）」
逐条对了一遍（只读审计，逐条带 `文件:行号`），三档结论：

**① 已一致**：13 项设置全部落地（`float_*` 键名 / 默认值 / 三种外观尺寸 / 6 套 seed 与 1.1.0 逐字一致，写盘即生效）；
无边框 + 不可缩放、工具窗口、置顶、只有顶部第一栏可拖、锁定后不响应、松手才落库位置、
极简 / 详细两种模式、`white`/`lilac` 自动配深色、悬停提示只挂顶部按钮、托盘可开关。

**② 本轮改了 6 条**（规格明确要求、原来做错或缺失）：不抢焦点（`WS_EX_NOACTIVATE` + 不再 `Activate()`）、
下一条/上一条方向、识别结束回到最新、头部 `第 N/M 次识别` + `yyyy-MM-dd HH:mm`、默认位置 `y=120`、
设置页「不支持显示公式」这句与实现矛盾的文案；顺带删掉两处死代码。

**③ 刻意不追 4 类**（依据 §5「照它的**信息架构**，不照它的**像素**」）：§6.2 的布局常量表（`pad=8s`/`btn=27s`/`fs=12.5s`…，
前提是 1.1.0 自绘图元，而 2.0 是真 XAML 控件）、§6.4 的 14 个配色字段与两个 alpha 系数、
选区高亮色与「已选中 n 个字」1.4 秒提示、滚轮步长与 Dock 图标。
**透明度口径**（`Root.Opacity` vs 规格的「乘进像素」）单独写进了 `DECISIONS.md`，连同代价。

### 9.4 收尾状态

* ✅ 绿线：Windows 构建 **0 警告 0 错误**；`QuizSync.Tests` **232 通过 / 0 失败**；
  `tools/verify_windows_ui.ps1` **PASS 82 / FAIL 0**（含托盘「显示/隐藏悬浮窗」能真的开关一次 ——
  这条正好覆盖了本轮把 `Activate()` 换成 `Show(activateWindow: false)` 之后「窗口还显示得出来吗」）；
  Android `:app:compileDebugKotlin` 0/0、`:app:testDebugUnitTest` 全绿（26 条）；
  真机 `tools/verify_android_pair.ps1` **✅ 配对成功**（见 §9.1）；
  `tools/gen_design_tokens.py --check` → 6 个产物与 `design/tokens.json` 同步。
  （`dotnet test` 第一次跑出过 2 条 E2E 假红 —— 两条都要起**真主机进程**，与首次并发启动撞在一起，
  重跑即 232/0/0。**单次红不等于回归**这条纪律又生效了一次。）
* ✅ 本计划范围内的阶段项**全部完成**（P0–P6）；收尾第二轮的内容见 §9.5，9-28 之后的动效变更与硬规矩复核见 §9.7。
* ✅ **绿线复测（2026-09-28，第 162 轮）**：`QuizSync.Tests` **236 通过 / 0 失败**；安卓 `:app:testDebugUnitTest` **45 条 / 0 失败**（删掉结果目录强制重跑，XML 时间戳 `2026-09-28T12:59:58Z`）；`tools/gen_design_tokens.py --check` → 6 个产物与 `design/tokens.json` 同步；协议仓向量 6 文件 / 120 步全绿。**上面那条绿线（232 / 26 条 / PASS 82）是当时的记录，数字已过时**。
* ⬜ **Windows 打包 / 安装包 = Phase 8（发布）的范围**：`tools/package_windows.ps1` 现在还只服务 Flutter 线
  （读 `apps/desktop/pubspec.yaml`、打 `apps/desktop/build/.../Release`），native 侧目前只有 `perf_gate.ps1` 里的 `dotnet build`。

### 9.5 收尾（第二轮）：Provider 自动拉起 + 子页面转场改成容器变换

**① P6 的「自动拉起」补齐了后一半：识别服务（Provider）**

原来的缺口：主机能一键拉起（`HostLauncher`），但**识别是 Provider 的活**，少一个进程时手机的链路停在
「等待电脑识别」，而界面上没有任何提示。现在：

* `QuizSync.ServerBridge/ProviderLauncher.cs`：问主机「你在不在线」（`/api/v2/info` 的 `provider_online`，
  **不猜**）→ 不在就起 `dotnet QuizSync.Provider.Cli.dll provider` → 等主机认到它。
  三种结局分别可解释：`AlreadyOnline` / `Started` / `NotFound`（附找过的路径）/ `Timeout` / `Failed`（带退出码）。
* 设置 → 连接设备：识别服务**随应用自动启动**（第 169 轮起设置页不再有手动按钮，总开关一键启动整套服务）；**先查 AI 配置**再拉进程 ——
  没配 Key 的 Provider 会以退出码 2 立刻退出，那时候只能报「刚起来就退了」，不如当场说清去哪儿填 Key。
* 两个实测踩到的坑（都写进代码注释了）：
  1. **不能用 apphost（`.exe`）启动**：本机的 .NET 10 是私有安装（`D:\Windows\Apps\DSH_Tools\dotnet`），
     直接跑 `QuizSync.Provider.Cli.exe` 会以「You must install .NET to run this application」失败；
     `QS_DOTNET` 存在时一律走 `dotnet <dll>`。
  2. **`bin\Debug` 下的旧 TFM 残留会被选中**：`net10.0`（旧）与 `net10.0-windows`（当前）并存时，
     按文件系统顺序取第一个会拿到残留 —— 跑起来**退出码 1**、只报「刚起来就退出了」，极难查。
     现在按 TFM 精确匹配 + 产物新旧排序，并删掉了那棵残留目录。
* 测试：`ProviderLauncherTests` 4 条（环境变量覆盖 / 找不到时的路径提示 / 主机不可达时 `IsOnlineAsync` 返回
  `null` 而不是 `false` / **真起主机 + 真拉 Provider + 主机确认在线**）。

**② 子页面转场：改成「从被点的那一行长出来」的容器变换**（用户口径：展开/收起位置在模块所在位置）

> ⚠️ **本小节开头描述的实现（`Modifier.qsPushMorph` 那套自写状态机 + 指针观察者）已经在同一天被删掉了**：
> `053b623`「把自写的转场状态机整套删掉，按 Image Toolbox 的分工重写（净删 ~120 行）」——
> 反复出 bug 的根因见下面 ④，**④ 才是后续形态**；再往后又被用户给的转场规格重做
> （进入 右→左 / 返回 左→右 + 预测式返回，见 §9.6 与 §9.7）。原文保留，用来记住走过的弯路。

先量了上一版：20fps 采样录屏显示点下去**第一帧的整屏亮度差是 70**（正常滑入帧只有 10–28）——
背景（主壳缩小 + 压暗）是**瞬间跳变**的，然后页面才开始横向滑入，返回又只用 2 帧。

现在：路由区根节点上**只观察不消费**地记下最近一次按下的位置（`PointerEventPass.Initial`，
不抢任何手势），据此合成「行矩形」，让整块路由区从这个矩形**长大 / 缩回**
（`Modifier.qsPushMorph`：`graphicsLayer` 的 scale + translation，GPU 合成层、每帧零重排）；
背景的缩小与压暗改成与它同一条时长曲线补间，横向滑动撤掉（两种运动叠在一起就是"打架"）。

实测（60fps 录屏，取屏幕中线 x=50% 的竖向剖面，y 为屏高百分比）：

| 帧 | 3% | 10% | 24%（被点那一行） | 55% | 95% | 判读 |
|---|---|---|---|---|---|---|
| 点之前 | 白 | 白 | 白 | 白 | 白 | 设置页原样 |
| 刚点下 | 灰 | 灰 | 灰 | 灰 | 灰 | 整屏压暗，页面还没长出来 |
| +2 帧 | 暗 | **白** | **白** | 绿 | 暗 | 页面只占中间那一条（**就是被点的那一行**） |
| +6 帧 | **白** | 白 | 白 | 浅绿 | 灰 | 已经长到接近满屏 |
| 稳定 | 白 | 白 | 白 | 白 | 白 | 满屏，四角露出被压暗的主壳 |

即：**展开从被点那行的位置开始、收起回到同一行**，且不再有单帧跳变。

**③ 两个用户报的问题（2026-09-27，同一天的第二轮反馈）**

**「合集进入题目页没动画」**：`pushMorph` 原来用 `LaunchedEffect(showingPushed)`（布尔）做键 ——
「已经压着合集详情、再压一层题目页」时布尔不变，**容器变换一次都不播**；而且当时的
`transitionSpec` 对旧页用了 `ExitTransition.None`（含义是"没有退场动画"⇒ **立刻移除**），
于是旧页瞬间消失、新页只是淡入 —— 看起来就是"没动画"。
现在：用**栈深**（`ui.routes.size`）做键，每压一层都播一次；旧页那侧改成**位移 0 的滑出动画**
（`slideOutHorizontally(tween(340)) { 0 }`），旧页留在原地等新页盖上来。

**「动画卡卡的，吃满 120 帧率了吗」**：先量再改。这台平板的面板是 **144Hz**（`dumpsys display`：
`mActiveModeId=2`、`renderFrameRate=144.0`），预算 **6.94ms/帧**。用 `dumpsys gfxinfo` 采同样 6 轮进出：

| 版本 | 帧时间中位数 | p90 | p95 | 说明 |
|---|---|---|---|---|
| 改之前 | **10ms** | 13ms | 17ms | 上限 ≈100fps，**吃不满 120** |
| 关掉主壳缩放与压暗（对照实验） | **6ms** | 9ms | 13ms | 证明瓶颈就是它俩 |
| 现在 | **6ms** | 12ms | 17ms | 中位数已经进了 144Hz 的预算 |

**根因**：给**整屏主壳**做缩放，会让每一帧重新栅格化整页文字（Skia 的字形掩码随缩放比变）——
一帧 4ms 就这么来的。**取舍**：点进来的那一瞬**不再缩主壳、只压暗**（省掉这 4ms）；
**手势返回时仍然跟手缩放**（0.94 ↔ 1.0，那是手指在带、只画几帧，观感收益大于开销）。
若要把"点进来也缩"拿回来，代价就是每帧多 ~4ms（在这块 144Hz 屏上就是 100fps 对 144fps 的差别）。

另外顺手去掉两处多余图层：`qsPushMorph` 里的 `clip`（压栈页自己已经圆角裁剪）、
`AnimatedContent` 默认的尺寸变换（`.using(null)`，两个内容都是 `fillMaxSize`）。
剩下 p90 的 12ms 尾帧是**压栈页第一帧的组帧成本**（列表 + 公式排版），
现在用 `withFrameNanos { }` 把它挪到"页面还只有一行那么大"的时候发生 ——
成本不会消失，但不再在动画正中间顿一下。

**④ 按 Image Toolbox 重做转场（用户指路：「去看看他为什么那么流畅」）**

仓库浅克隆到 `D:\ZCode\refs\ImageToolbox`（`gh`/git 代理都试过，最后是 `git clone --depth 1` 成功的）。
读完的硬事实（都带路径）：

| 它怎么做 | 出处 | 对我们的意思 |
|---|---|---|
| 页面栈用 **Decompose**，全仓库 **0 处 `androidx.navigation`** | `core/ui/.../utils/navigation/Decompose.kt`、`feature/root/.../navigation/` | 手写路由/自管栈是主流做法，不必为"没上 Navigation-Compose"焦虑 |
| 栈动画 = `stackAnimation(fade(300) + slide(400) + scale(500))`，三段的**时长是错开的**（淡入最短、缩放最长） | `core/ui/.../utils/animation/Animations.kt:73-98` | 位移 + 淡入 + 缩放不是同一条曲线，而是**接力** |
| **`scale` 只作用于被盖住的那一页**（进场页只 fade + slide） | 同上 | ← 这就是"为什么流畅"：**缩放正在进场的那一页**要每帧重新栅格化整页文字，正是我们量出来的 4ms/帧 |
| 返回手势交给**系统**：`predictiveBackAnimation(..., selector = { androidPredictiveBackAnimatableV1(it) })` | 同上 `:97` | 系统级预测式返回由 SurfaceControl 合成，比自己写缩放跟手得多 |
| 横向切换是**纯平移**：`fancySlideTransition` = 位移 600ms + 淡入 300ms（**晚 100ms 起**） | 同上 `:47-71` | 我们照这个配比做（位移 400ms / 淡入 300ms 晚 100ms 起） |
| 全应用 ~50 处 `AnimatedContent` 的 transitionSpec 都是 `fadeIn() togetherWith fadeOut()`（多处还 `using SizeTransform(false)`） | 各 feature 模块 | 默认就该是交叉淡化，不是整页缩放；尺寸变换能关就关 |

我们的改动（**最终版 = 一比一照搬它的三个动画器**，见 `android/app/.../ui/designsystem/QsStackTransitions.kt`）：

我第一版自己造了一台动画状态机（记录按下点 → 合成行矩形 → 每页挂缩放 → 栈深判方向 → `animatingRoute` 记谁在动），
每轮都被用户抓到新 bug。**根因**：把值相等当身份（两条同参数的 `Route.Result` 相等）、把进出场判据散在三处，
任一处不同步就留下"卡在缩放态"的页面。

照搬之后（`QsStackTransitions.kt` 里写清每一处出处）：

| Decompose 的原始语义（`extensions-compose/.../stack/animation/`） | 我们的实现 |
|---|---|
| `Fade.kt`：`alpha = 1 - \|factor\|` | `fadeIn/fadeOut(tween(300, AlphaEasing))` |
| `Slide.kt`：`x = 子项宽度 × factor`（整屏位移，`layout{}` 摆位而非缩放） | `slideInHorizontally/slideOutHorizontally(tween(400, FancyTransitionEasing)) { it }` |
| `Scale.kt`：`factor≥0 ? factor*(1.15−1)+1 : factor*(1−0.95)+1` | `scaleIn(initialScale = 1.15f)` / `scaleOut(targetScale = 0.95f)` 等 |
| `StackAnimator.kt:36-40`：`ENTER_FRONT` 1→0、`EXIT_FRONT` 0→1、`ENTER_BACK` −1→0、`EXIT_BACK` 0→−1 | `AnimatedContent` 的 `targetState != null` 区分前页/下页 |
| 三段**同时开始、时长不同**：fade 300 / slide 400 / scale 500 | 三条 `tween` 各自带曲线，一次性交给 `transitionSpec` |

于是效果是：**两页同时整屏横移** —— 进场页从右滑入 + 淡入 + 从 1.15 缩到 1.0；
出场页向右滑出 + 淡出 + 放大到 1.15；**主壳（下页）向左滑出 + 淡出 + 缩到 0.95**，返回时反过来。
主壳那组用三个**无状态**的 `animateFloatAsState`（目标值只由"上面有没有页"决定），
并且主壳**始终留在组合里**（塞进 `AnimatedContent` 当 null 分支会把它的滚动位置与输入状态丢掉）。

**压测证据**（8 轮「设置→识别模块→返回 / 历史→合集→题目→返回→返回 / 切 tab」，约 40 次转场）：

* 进程活着、**崩溃缓冲 0 行**、结束界面是主壳；
* `dumpsys gfxinfo`：6276 帧、**Janky 2.50%**、中位数 **7ms**、p90 **10ms**、p95 **13ms**。

| 版本 | 中位数 | p90 | p95 | Janky | 崩溃 |
|---|---|---|---|---|---|
| 最初（主壳缩放 + 压暗 + 整页缩放进场） | 10ms | 13ms | 17ms | 4.94% | — |
| 自写容器变换（120 行状态机） | 6ms | 12ms | 17ms | 8.18% | 多轮报 bug |
| 简化版（只平移 + 淡入） | 8ms | 12ms | 12ms | 2.61% | 0 |
| **照搬 Image Toolbox（三个动画器全上）** | **7ms** | **10ms** | 13ms | **2.50%** | **0** |

**仍未做（要彻底吃满 144Hz 的话）**：① 把返回手势换成 system predictive back 的
`androidPredictiveBackAnimatableV1` 等价物（需要 Decompose 或自己接 `BackEvent` 的动画）；
② 压栈页第一帧的预组帧/轻量骨架。两条都是独立的一项。


### 9.6 预测式返回（用户 2026-09-28 给的口径）：一次连续转场，不是「预览 + 再播一遍」

用户把口径写得很死，这一节把它连同**两个已修的 bug**一起留档（根因与真机 A/B 见 `DECISIONS.md` 第 153 轮）。

**规格（用户原文要点）**

1. 预测式返回是**一个可交互、可暂停、可取消、可继续的单一 transition**，不是「拖动预览 + 松手后普通返回动画」。
2. 拖动中：`N+1` 在前、`N` 在后；`N+1` 按 progress 向右移动，`N` 按 progress 从左侧露出。
   **只能显示直接上一层** —— 四级页拖动时看到的是三级，绝不能是二级或一级。
3. 拖动期间**不动真实路由栈**：不提前 `popBackStack`、不销毁页面、不重新进入上一页，只改视觉。
4. 松手确认：**直接完成当前 transition 并提交导航状态**，禁止重新播放 exit / enter。
5. 松手取消：**从当前 progress 反向恢复**（`1 → 0`），不重建页面、不重播进场、不闪。
6. 一次返回严格只退一级（`N+1 → N`，没有跳级、没有"退了又回来"）。
7. 系统返回手势 / 返回键 / 顶部返回按钮 / 代码 pop 的**最终导航状态必须一致**；
   没有 progress 的那几种走普通 300ms 转场，但**不能让手势路径把动画播两遍**。

**实现落点**

| 规格 | 落点 |
|---|---|
| 只显示直接上一层 | `ui/QuizRouteStack.kt` 第 ② 层渲染 `qsStackPreviewRoute(routes) = routes[size-2]`；栈里只有一页时返回 `null`（那时上一页就是主壳，它常驻在最底下） |
| 拖动期间不动栈 | `PredictiveBackHandler` 的 `progress` 只喂 `Animatable`；`popRoute()` 只在 flow 正常结束（= 提交）时调一次 |
| 确认不重播 | `NavDirection.PopGesture`：`EnterTransition.None togetherWith ExitTransition.None`（零时长） |
| 确认那一帧不跳 | `qsStackPageOffsetFactor(inStack = false, gestureCommitted = true) = 1f` —— 已出栈的那一页**仍然摆在屏幕外**，`AnimatedContent` 多留它一帧也看不见 |
| 取消反向恢复 | `catch (CancellationException) { backGesture.animateTo(0f, 180ms) }`，预览层留到归零之后再撤（否则它会"啪"地消失） |
| 返回键仍有动画 | 只有**观察到中间 progress 值**才算"真的跟过手"；一步到位的返回（按键）走 `NavDirection.Pop`，播完整 300ms |
| 主壳不跟着重播 | `qsStackBackPage(..., instant = gestureCommitted)`：提交那一帧静止值**立即**生效，不走 `animateFloatAsState` 补间 |

**这一节同时关掉了 9.5 结尾那条遗留 ①**：「把返回手势换成 system predictive back 的等价物」——
现在的做法是 `PredictiveBackHandler` 的 progress **直接驱动**我们自己那套视差（位移量与 `Pop` 分支同一个常数），
所以系统给多少进度、画面就走多少，不存在"系统动画 + 自己动画"两套。

**性能口径**：进度只在 `Modifier.graphicsLayer { }` 的绘制阶段被读（`gestureProgress` 传的是读取器而不是 Float），
所以拖动期间**一次重组都不发生** —— 手势开始时组合一次「上一层」，之后每帧只更新两个图层的位移。
（改之前 `backGesture.value` 是在组合里读的，等于每帧重组整个 Activity。）

---

### 9.7 9-28 之后：动效被收成「只有页面进出」+ 硬规矩复核（2026-09-28，第 159–162 轮）

**这一节是「本计划写完之后又发生了什么」，读 §4 / §9.2 / §9.5 时必须连着它一起看。**

**① 用户口径：「删除除了进入页面和返回页面外的所有动画」**（第 158/159 轮，`87c8840`）

| 删掉的 | 原来在哪 | 现在 |
|---|---|---|
| 同级横向切换（主壳切标签 / 结果页切上一条·下一条） | 两个 `AnimatedContent` + `qsSiblingAxisTransform` | 直接换内容（`when (ui.tab)`） |
| 列表项按下形变 | `SessionTile` 的 `animateDpAsState`（16→12dp） | 圆角固定 16dp |
| 阅读材料面板展开/收起 | `AnimatedVisibility(expand/shrinkVertically)` | `if (open) { … }` |
| 共享元素 | `ui/designsystem/QsSharedTransition.kt`（**整文件删除**） | 普通 `Text` |

真机实测（`screenrecord` + 真实帧时间戳）：切标签 / 上一条 / 下一条 **各 1 帧（瞬间）**；
进合集详情 27 帧 / 181ms、进结果页 26 帧 / 174ms、返回 19 帧 / 126ms —— **只剩页面进出在动**。
全仓库只剩三处动画 API：`QuizRouteStack` 的 `AnimatedContent`、`QsStackTransitions` 的 `animateFloatAsState`、
`MainActivity` 的 `Animatable`。代价（写清楚）：并列关系上**失去方向感**。

**② 返回手势「速度判据」**（第 161 轮，`b6412e0`）：甩（>3/s，一屏约 330ms）不进预览层、不跟手、直接切；
慢拖照旧跟手。帧开销：甩 = **1 个**整页组合（52.8ms），慢拖 = 2 个（54.2 / 51.5ms）。判据是纯函数 `qsIsFling`（有单测）。

**③ 结果页五项**（第 160 轮，`3cd1248`）：题干/解析字重分级、选项圈与字母居中、窄屏（<600dp）隐藏字号加减并重排动作、
设置页字号预览、稳定 `graphicsLayer` 块（`qsStableGraphicsLayer`：块身份不变 ⇒ 不整层重录）。

**④ P2 三条硬规矩的复核（第 162 轮，今天扫的）**

| 规矩（`QsComponents.kt` 文件头） | 结果 |
|---|---|
| ① 页面里不出现裸 `Card(...)` / `Slider(...)` / `AlertDialog(...)` | ✅ 全仓库页面零命中（只剩注释） |
| ② 间距 / 圆角 / 颜色不写字面量，取 `QsTokens` / `quizPalette` | 🟡 **按字面口径未收敛**：`PairScreen` 33 处、`ResultScreen` 32 处、`SessionTile` 11 处 `.dp` 字面量（含图标尺寸这类本可走图标阶梯的）。§9.1 当时只扫了「裸原语」这一条，表格里那句「✅ 只用 `Qs*` + 令牌」偏乐观 |
| ③ 字号不写 `sp` 字面量，只用 `MaterialTheme.typography` | 🟡 `ResultScreen.kt` 还有 **6 处**（`12.5 / 11.5 / 11.sp` 与对应 `lineHeight`），来自 2026-09-27 建页那次（`9b2d90a7`）。**不收的原因**：这些是 1.1.0 的像素对齐值，套 M3 角色（11/12sp）会**改像素**；要收又不改观感，得照 §8.6 的做法先加「同数值」的字号令牌 —— 待定 |

**⑤ 本计划范围内仍未做的两条（其余全部完成）**

* ⬜ **Windows 打包 / 安装包** = Phase 8（发布）的范围：`tools/package_windows.ps1` 至今只服务 Flutter 线。
* ⬜ **Mica 不支持时的亚克力/纯色兜底**（§7 的「未验」）：本机支持 Mica，走不到那条分支，需要一台不支持的机器。
