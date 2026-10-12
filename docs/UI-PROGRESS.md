# 2.0 UI 进度快照

> **这个文件是「当前状态」而不是日志**：上下文被压缩后，按这个路径回来读它就能接着干。
> 每一项都标了**怎么验的**或**没验** —— 没验的不要当成做完了。
> 最近更新：**2026-09-27（第 152 轮）**。下面 **§0 是最新状态**，§一~§六 是按时间累积的旧记录，
> 其中被 §0 明确纠正过的条目**以 §0 为准**（已在原处加勘误标记，未删除原文 —— 保留是为了看清走过的弯路）。

---

## 〇、最新状态（2026-09-27，本轮全部重新实测）

绿线（本轮推进到 `411b61e` 之后的实测）：

| 对象 | 命令 | 实测 |
|---|---|---|
| Windows 解决方案 | `dotnet build windows\QuizSync.Windows.slnx` | 0 警告 0 错误 |
| Windows 应用 | `dotnet build windows\QuizSync.App\QuizSync.App.csproj` | 0 警告 0 错误 |
| Windows 单测 | `dotnet test windows\QuizSync.Tests` | **215 通过 / 0 失败** |
| Windows 端到端界面 | `tools\verify_windows_ui.ps1` | **PASS 56 / FAIL 0** |
| 设计令牌一致性 | `python tools\gen_design_tokens.py --check` | 6 个产物与 `design/tokens.json` 同步 |
| 安卓编译 | `gradle :app:compileDebugKotlin`（**全量重编**） | 0 警告 0 错误 |
| 安卓单测 | `gradle :app:testDebugUnitTest` | **23 通过 / 0 失败** |
| 安卓内核 | `gradle :core:test`（要 `QS_PROTOCOL_DIR` + `QS_DOTNET`） | **11 通过 / 0 失败**，含**打真 C# 服务端的跨实现 E2E** 与 `sync.ndjson` 向量对拍 |
| 安卓真机 | `tools\verify_android_ui.ps1`（`e5b1641d`） | **PASS 12 / FAIL 0** |
| 性能门禁 | `tools\perf_gate.ps1` | **15 行逐项有数**：4 达标 / 5 不达标 / 5 N/A（明细见 `NATIVE-2.0-PLAN.md` §13.1） |
| 服务端仓 | `QuizSyncServer` build + test | 0/0 + **74 通过** |
| 协议仓 | `conformance\check_vectors.py` | 6 个文件 / **120 步**全过 |
| 协议仓规范 | `conformance\check_language_neutral.py` | 15 个 Markdown 全绿（含新增的 `spec/12-provider.md`） |

> ⚠️ **「0 警告」的口径**：`gradle :app:compileDebugKotlin` **增量**执行时只报被重编文件的警告 ——
> 本轮全量重编（`--rerun-tasks`）暴露出 4 个既有警告，已清掉。以后报「0 警告」请用全量口径。

**已完成的（相对 §一~§六 的增量）**：安卓端结果页（含 `MathText` 公式/化学式/表格）、
识别模块二级页、截屏采集链（悬浮球真能截屏）、`ui/designsystem` 组件层（P2）、
Material 3 信息架构（P4：BottomSheet 选合集 / 折叠顶栏 / 宽屏 `NavigationRail` /
系统栏跟随应用主题 / 动态取色）、动效层（P5：fade-through + 共享元素单点）；
Windows 端设置页原生控件（P3）、动效层（P5）、Mica 背板、本轮的用户反馈收口（顶栏重组）。
**主机服务端已在独立仓库 `D:\ZCode\QuizSyncServer` 落地**（§三 写的「仓库里没有服务端」只对本仓库成立）。

**还没做**（按阻塞面排序，这就是下一步的清单）：

1. ~~**Windows 悬浮窗**（规格 §6.1–6.5）~~ —— **已完成**：`fd2218c`（真 XAML 窗口）+ `7b2c7f3`（13 项设置）
   + **第 152 轮**按规格逐条比对后的六处修正（不抢焦点 / 翻页方向 / 识别后回到最新 / 头部序号与时间 / 默认 y=120 / 文案矛盾），
   见 `design-system-plan.md` §9.3 与 `DECISIONS.md` 第 152 轮。
2. ~~**协议仓 v2 草案**~~ —— **本轮已做**（协议仓 `9033e6e`：`spec/12-provider.md` 逐字段规范 +
   CHANGELOG 区分「已落地 / v2 目标」+ 兼容矩阵三处待确认改为已定）。**仍未做**：本域**零一致性向量**，
   两个前置卡在「向量需要分域适用范围」与「回放器缺三个能力」，理由写在 `spec/12-provider.md` §9。
3. ~~**安卓配对页按规格 §2.1 重做**~~ —— **已完成**（`411b61e` 三模块 + 扫码 + 权限卡；
   **第 152 轮**又把页面收口到 `Qs*` 组件层，真机 `tools/verify_android_pair.ps1` ✅ 配对成功）。
4. **Phase 7 全部**（本轮开工）：组合矩阵 / 旧备份兼容 / 性能门禁 / 内嵌口径，见 `NATIVE-2.0-PLAN.md §10`。
5. ~~**界面自动拉起主机与 Provider**~~ —— **都做完了**：主机 `HostLauncher`（第 169 轮起由「开启连接设备」这个总开关一键启动）、
   **识别服务 `ProviderLauncher`**（第 169 轮起随应用自动启动，设置页不再有手动按钮；
   判据是主机 `/api/v2/info` 的 `provider_online`，见 `design-system-plan.md` §9.5）。
6. **Windows「框选题目区域后重试」入口** —— 判据 `UiText.CanCropRetry` 已实现，没有入口。

**CI 的真实状态（本轮查清，之前一直是错的假设）**：`.github/workflows/ci.yml` **仍只跑 Flutter 时代的
5 个 Dart 包**（`packages/quizsync_core` / `quizsync_ui` / `apps/desktop` / `apps/android` / `server`）
+ 向量回放 + 仓库卫生。**C# 与 Kotlin 的 2.0 代码一行都没进 CI** —— 也就是说
「构建 0/0 + 单测全绿」这半年靠的是本机手跑。这是本轮要补的第一个缺口。

---

## 一、安卓端（Kotlin / Compose）

代码在 `android/app/src/main/kotlin/com/quizsync/android/dev/`（**源码包 = namespace，故意不带 native**）。
**包名（applicationId）**：正式版 `com.quizsync.android.native`、开发版 `com.quizsync.android.native.dev`
（用户 2026-09-28 拍板，与 Flutter 线的 `com.quizsync.android` 区分开），设备 `e5b1641d`。
构建：`:app:assembleDebug` → BUILD SUCCESSFUL。

### 1.1 文件地图

| 文件 | 职责 |
|---|---|
| `MainActivity.kt` | 路由分发 + 预测式返回手势（`PredictiveBackHandler` → 一个 `Animatable` 进度） |
| `ui/QuizRouteStack.kt` | 路由区的**图层结构**：① 主壳 ② 手势预览层（**直接上一层** = `routes[size-2]`）③ 栈顶页；取值规则是纯函数，有 JVM 单测 |
| `ui/designsystem/QsStackTransitions.kt` | 进出场转场（Push/Pop/PopGesture/Replace）+ 三页各自的横向位移规则 |
| `ui/HomeShell.kt` | Scaffold、双行 AppBar、底部导航、浮动提示条宿主 |
| `ui/CurrentTaskScreen.kt` / `ui/HistoryTab.kt` / `ui/SettingsTab.kt` | 三个 tab |
| `ui/CollectionDetailScreen.kt` | 合集详情（压栈页）+ 删除确认框 |
| `PairScreen.kt` | 配对页（手输码） |
| `ui/components/Components.kt` | `EmptyState`、`QuizSnackbarHost` 等共享件 |
| `ui/components/SessionTile.kt` | 历史条目（单击进详情 + 长按删除 + 按下形变） |
| `ui/haptics/EnhancedHaptics.kt` | 增强触感（学 ImageToolbox） |
| `ui/theme/QuizTheme.kt` | 配色 |
| `state/QuizViewModel.kt` / `state/QuizState.kt` | 状态与副作用 |
| `RecordStore.kt` | 所有 SQL 与 JSON 解析（Composable 里不碰库） |
| `ball/FloatingBallController.kt` | 悬浮球 |

### 1.2 已修的问题（多数是用户报的）与**验收方式**

| # | 问题 | 修法 | 怎么验的 |
|---|---|---|---|
| 1 | 空态提示**居左**不居中（用户报） | `EmptyState` 的 `Column` 补 `fillMaxWidth()` —— **在组件里修**，不靠调用点记得补 | 像素/控件树：两条文字中心 x=900（屏宽 1800，偏差 +0） |
| 2 | 返回时标题栏**方方正正**（用户报） | 真因是 `RoundedCornerShape(16f)` 走的是 **Float 重载不是 dp**（实测只有约 10px 圆角）。改成 `(16f + progress*12f).dp` | `tools/ascii_map.py` 打出 ASCII 色块图，看到约 48px（=16dp@3x）的扇形缺口 |
| 3 | 「返回和他不一样」（用户报） | 补上「上一页跟着手势由 0.94 放大到 1.0」+ 压暗随进度退掉 | 静止态像素：角上露出的颜色 = 主壳颜色，证明圆角与压暗叠对了 |
| 4 | **整个界面被缩小**（用户报，两次） | ① 判据从「拖动进度」改成「上面有没有页」；② 再从「有没有压栈路由」改成「**真正渲染出了**压栈页」（`showingPushed`） | 像素：主界面四边与应用底色一致、无深色带 |
| 5 | **native 崩溃 SIGSEGV**（用户报） | `androidx.sqlite` 的 `SQLiteConnection` **非线程安全**；我上一轮加的「先刷本地历史」多了一个并发库访问者。所有库访问收敛到单线程 `dbDispatcher` | 重放崩溃操作序列：进程活着 + 崩溃缓冲干净 + 删除真的执行了 |
| 6 | **点一下延迟 5 秒 + 下拉刷新不消失**（用户报） | 同一个根因：我把**网络调用也塞进了单线程**（网络有 5 秒超时）。分离：纯网络回 `Dispatchers.IO`（6 处）、库留 `dbDispatcher`（10 处）；`refreshAll` 的刷新标志用 `try/finally` 兜底 | 点击到页面出现**第一次轮询就命中**（之前要等 5 秒超时） |
| 7 | 下拉刷新**收不回去**（用户报） | `history.loading` 默认就是 `true`，而 `loadHistory()` 只在查询成功时才置 false —— 加 `try/catch` 兜底，并把错误记进 `history.error` | **未在设备上验成**：我发的下拉手势压根没触发刷新（连抓两次都没有提示） |
| 8 | 删除确认框断言**一直走 SKIP** | 之前设备上没有记录 | 现在真跑：长按 → 弹窗 → 文案逐字一致 → 取消关闭，4 条全绿 |
| 9 | 触感（学 ImageToolbox） | `EnhancedHaptics.kt`：`FLAG_IGNORE_GLOBAL_SETTING` + 尊重无障碍 + **震动包进点击本身** | logcat：`D QuizSyncHaptics: haptic 6 handled=true` |
| 10 | 按下形变（学 ImageToolbox 的 `shapeByInteraction`） | `SessionTile` 按下时圆角 16dp→12dp | **未验成**：按住期间截图与未按下**逐格相同**（可能抓帧时机或幅度太小） |
| 11 | 浮动提示条（**规格 §1.4** 要求浮动/圆角 10/白字 13.5） | M3 默认那条贴满宽度、圆角很小、字用主题色 —— 三条都不符。新增共享的 `QuizSnackbarHost` | 像素：左端内缩约 52px（=16dp）、深色底、白字；控件树文字 bounds 左端 80px |

### 1.3 现在**能用**的部分

双行 AppBar（应用名 + 连接状态点）、底部导航（outlined/filled 图标对）、当前任务页、
历史页（搜索 + 合集分组 + 空态居中）、合集详情 + 删除确认、设置页（主题/标签/测试数据）、配对页（手输码）。

**测试数据**：设置 → 测试数据 → 「生成 5 条测试记录」（`RecordStore.seedTestSessions`，
走应用自己的写入路径而不是直接改库）。设备上现有 5 条，其中 1 条是故意造的 `failed`。
细节页会显示「识别出 1 道题 / 09-26 21:05 · 已完成 · 来自电脑」这类条目。

### 1.4 安卓端还没做

> ⚠️ **勘误（2026-09-27，第 141 轮）**：本表写于第 138 轮之前，**四条里三条已经做完了** ——
> 结果页（§3）见 §6.1（`ui/ResultScreen.kt`，含 `MathText`）、识别模块二级页（§5）见
> `ui/RecognitionSettingsScreen.kt`、采集链路见 `ab094c3`（`capture/CaptureService.kt` +
> `FloatingBallController`，悬浮球能真截屏）。**仍未做的只剩「相册选图 FAB」（已做，见 §6.1）
> 之外的两条**：配对页按规格重做（§2.1）与 §5 里未接的成员。以 §0 的清单为准。

| 缺口 | 说明 |
|---|---|
| **结果页（规格 §3）** | ~~`Route.Result` 会被压栈但**什么都不渲染**~~ —— 已修，见 §6.1 |
| **识别模块二级页（规格 §5）** | ~~未做~~ —— 已做（`ui/RecognitionSettingsScreen.kt`） |
| 相册选图搜题 FAB（§2.2） | ~~故意不做~~ —— 已做（`ab094c3` 之后的主机上通了） |
| 配对页按规格重做（§2.1） | **仍未做**：`PairScreen.kt` 178 行，7 个规格成员未接 |
| 采集链路 | ~~全未接~~ —— 已接（`capture/CaptureService.kt`），真机取证见 §6.2 |

### 1.5 安卓端残留风险

- `PairScreen` 的**写库那一小段仍跑在多线程**上（我的「纯网络」判据把它归错类了），
  理论上还能与读并发。根治要把 `RecordStore` 内部统一加锁。
- 手势**进行中**的缩放/圆角无法用坐标量（系统接管了可访问性树），只有代码 + 静止态像素证据。

---

## 二、Windows 端（C# / .NET 10 + WinUI 3）

`windows/` 下 8 个项目（本轮新增 `QuizSync.Host`）。构建：整个解决方案 **0 警告 0 错误**。

- 单元测试 `QuizSync.Tests`：**209 通过 / 0 失败**（P3 完成后实测）
- `tools/verify_windows_ui.ps1`：**38 PASS / 0 FAIL**（P3 完成后实测，含设置页 13 条）
- 悬浮球：3 张真 256×256 带 alpha 资源、DPR 自适应、环+球合成 —— 像素验过
- 已修：圆角被渲染成椭圆（WinUI `CornerRadius` 不裁剪 → `PillShape.MakeCapsule`）、
  `ContentDialog` 重入导致整个进程 APPCRASH（加守卫）

**没做**：悬浮窗（规格 §6.1–6.5，最大一块）、公式/富文本渲染（现在显示原始 LaTeX）、
「框选题目区域后重试」入口、托盘「显示/隐藏悬浮窗」（依赖悬浮窗）。

### 2.1 P3 完成：设置页换成原生控件（2026-09-27）

`docs/design-system-plan.md` §4 的 **P3** 已做完。**只换外衣，不换行为** ——
十个分类、每个设置项的功能与落库键位一个都没动。

| 原来（手搓） | 现在（原生） |
|---|---|
| `Grid` + 10 个 `Button` 的左导航，`SizeChanged` 里自己算 880 断点 | **`NavigationView`**（`OpenPaneLength=232`、`CompactModeThresholdWidth=640`、`ExpandedModeThresholdWidth=880`） |
| 自绘返回按钮 + 「设置」标题文本 | NavigationView 的**返回按钮** + `PaneTitle` |
| 底部版本号 `TextBlock` 自己摆 | `NavigationView.PaneFooter` |
| 行 = `Grid`；宽控件行 = `StackPanel`；开关行 = `Grid`+`ToggleSwitch` | **`SettingsCard`**（`Header`/`Description`/`HeaderIcon`/`Content`；宽控件行用 `ContentAlignment=Vertical`） |
| 分组 = 一张大 `Border` + 主色标题带 + 行间分隔线 | **Win11 段头 + `SettingsCard` 栈**（与 WinUI-Gallery 自己的设置页同构） |
| 提示条 = 自绘圆角 `Border` | **`InfoBar`**（`Severity=Warning/Informational`，`IsClosable=false`） |
| 进度条 = 两层 `Border` 自绘 | **原生 `ProgressBar`** |
| **没有任何动画** | 见下（背板已接；页面转场待 P5） |

另外新增 `Theme/QsBackdrop.cs`：主窗口挂 **Mica**（`MicaKind.BaseAlt`），
`MicaController.IsSupported()` 为假时退回亚克力、再退回纯色。
⚠️ **它现在看不出效果**：每个页面都铺了不透明的 `QsCanvasBrush` 画布，背板被盖在下面。
要真看见 Mica 得把画布改成半透明 —— 那是另一个决定，没做。

**怎么验的（三条绿线全过）**

| 绿线 | 实测 |
|---|---|
| 构建 0 警告 0 错误 | `dotnet build` → 0/0 |
| 测试全绿 | `dotnet test` → **209 通过 / 0 失败** |
| UIA dump 十个分类 | `tools/verify_windows_ui.ps1` → **38 PASS / 0 FAIL**，设置页 13 条含 10 个分类全中 |
| 设置落库复核 | 走真实 UI 拨「剪贴板监听」→ `app.json` 的 `clipboard_watch` 真的翻面 → 再拨回原值。**`SettingsCard` 没有吃掉内部控件的事件** |

**踩到的两件事**（都记下来，别再踩）

1. **验收脚本有一次假红**：某次运行报 `PASS 14 / FAIL 12`，原因是 App 启动时**读到了 0 个合集**、
   落在首次使用门页（库里其实有 3 个合集 / 16 条记录）。随后冷启动 3 次全部 1.4 s 落在主界面、
   重跑验收 38/38 —— 判为「强杀 + WAL」下的**瞬时读空**，与本次改动无关。
   **它暴露的是真问题**：`StartPage()` 用 `List().Count == 0` 判定首次使用，读空一次就误进门页。
2. **`probe_pixels` 的屏幕拷贝会把窗口不可见的缩放边框算进来**（Win11 约 7–8 物理像素），
   所以「最左边那几个点是近黑」是**窗口背后的桌面**，不是界面缺陷 ——
   用 `ascii_map.py` 打改动前后的图才看清：那条黑列**改动前就在**（`PrintWindow` 的伪影）。
   **判形状要用图，别用单点采样**（这条是既有教训的第二次现身）。

**设置页真实配色（`probe_pixels`，200% DPI）**：画布 `#F3F5F4`、卡片 `#F9FAFA`、左栏 pane 略深一档。
原来左栏是纯白，现在跟 Win11 一样是 pane 底色、卡片浮在上面。

### 2.2 启动落点判据加固（2026-09-27）—— **不是 bug 修复，口径已更正**

> ⚠️ **这一节最初写成了「修 `StartPage()` 的 bug」，那个判断已被复核推翻并撤回。**
> 复核逐行读了 `a69ee95`：`try { 开库; Count==0 ? 门页 : 主界面 } catch { 主界面 }` ——
> **读异常本来就已经回主界面**，所以「读失败被判成首次使用」**没有发生过**。

**真正的现象与它的机制**：`verify_windows_ui.ps1` 某一轮报 `PASS 14 / FAIL 12`，App 停在「新建任务合集」门页。
机制是**探针把 `QS_APP_DATA` 指到了一个空目录** → `AppDataDirectory.Ensure()` 在那里**新建了一个空库** →
库能正常打开、查询也不抛，**就是真的 0 行** → 门页。当时真实用户库里其实有 3 行合集（2 行墓碑 + 1 行活的）。
**结论：这是验证假象，不是产品缺陷；而且那一路没有异常，下面的加固也防不住它。**

**那这次改了什么**（性质：**防御性加固**）：判据抽成 `QuizSync.Data.StartupProbe`，
落点从布尔改成**三态** `FirstUse` / `Main` / `MainAfterReadFailure`。买到的三件事：
① 把「异常」与「空」**显式分开**；② 读失败时调 `CrashLog.Write` **留一行日志**
（原来的 `catch` 是静默的 —— 出事时日志里什么都没有，只能靠猜，这才是当时查得那么费劲的原因）；
③ 两处调用点（`MainWindow.StartPage()` 与 `CollectionPickerPage.OnLoaded()`）**共用同一判据**
（原来各写一遍 = 改一处漏一处）。
对异常这一路，**行为与旧实现一致**（都是回主界面），变的是显式化 + 有日志 + 有测试。
判据放在 `QuizSync.Data` 而不是 `QuizSync.App`，因为 `QuizSync.Tests` **不引用 App**
（WinUI exe；`dotnet test` 不构建它）—— 放 Data 才有测试兜底。

**测试**（`QuizSync.Tests/StartupProbeTests.cs`，6 条）钉两件事：
* 纯判据：读失败 → `MainAfterReadFailure`（**与旧实现的隐式行为一致**，只是现在被钉住了）；
* **构造真的读失败**：把库路径指到一个**目录**上（`SqliteConnection.Open()` 必抛），
  断言 `Probe` 报的是「失败」而不是「空」—— 只测纯函数的话，「Probe 把异常吞掉」这种回归照样能溜过去。

**顺带记两条 `verify_windows_ui.ps1` 的假红**（都重跑即绿）：

| 现象 | 观察 | 判读 |
|---|---|---|
| `PASS 14 / FAIL 12`，停在门页 | 冷启动 3 次全在 1.4 s 内落主界面；重跑 38/38 | **探针的 `QS_APP_DATA` 指到空目录**，被 UIA 抓到了那个实例 |
| 3 条托盘断言红（球没来回、标题读空） | 重跑 38/38 | 托盘命令派发缺「等应用就绪 + 重试」 |

**读这个脚本的输出时要当心**：它有几条时序敏感的断言，**单次红不等于回归**。先重跑一次；
另外**两个 agent 不要同时跑 UIA 验收**（会互相杀实例，制造假 FAIL —— 已发生过一次，6 条假红）。

---

## 三、主机服务端（**识别的前提**）

⚠️ **主机不在这里 —— 它在 `D:\ZCode\QuizSyncServer`（独立仓库）。**
上一轮我在本仓库新建的 `windows/QuizSync.Host` 是**重复实现**（当时误以为「仓库里没有服务端」，
那句话只对本仓库成立），**已删**。两份主机抢同一个端口区间 8765–8770 与同一个数据目录
`%LOCALAPPDATA%\QuizSyncAI\server`，留着只会互相打架。完整证据与理由见
`docs/DECISIONS.md` 的「主机服务端（第二阶段）」。

### 3.1 主机侧（`D:\ZCode\QuizSyncServer`）

基线：**56 条测试 + 6 组 120 步一致性向量全绿**。已实现：配对与令牌（只存 SHA-256）/
限流与锁定（跨重启）/ 图片 Blob（内容寻址、边读边限长）/ 任务队列（`task_id` 幂等、
同图同页序复用）/ op 中继与逐字段 LWW / 快照 / `/ws` / v1 兼容层与 v2 骨架 /
CLI（`run` `pair` `devices` `config` `setup` `doctor`）/ 本机控制面（`control.token`）。

### 3.2 本轮补上的缺口 = **「识别」本身**

| 缺口 | 原来是什么样 | 现在 |
|---|---|---|
| **生产路径的识别器** | `QuizSyncHost.Analyzer = FixtureAnalyzer`：**固定回「第 1 题（fixture）」并且 status=done** —— 比报错更糟，用户只会觉得「识别不准」 | 默认**派发给 Provider**；假识别器只能显式开（`--analyzer fixture`，测试与向量用） |
| 题目字段 | 只落 8 列，`options_json`/`choice_json`/`material`/`need_review`…**根本没进库** → 手机上「标绿/材料折叠/标黄」看起来像没做 | 15 列全落（键名 = 协议 `questions` 表列名） |
| `/api/v1/info` 的 `active_collection_id` | **硬编码 null** → 按协议 §3.1 安卓端**永远不敢发起识别** | 取主机当前合集 |
| `ai_configured` | 取自静态选项，永远是 false | 取在线 Provider 自述的值，没 Provider 时退回静态值 |
| `/api/v1/tasks/active` | **恒返回 `idle`** → WS 断掉时手机看不到任何进度 | 真实快照（含 `session`） |
| 无 Provider 时 | （不存在这个概念） | `provider_offline`，**≤2 秒**快速失败，绝不假成功 |
| `/ws` 握手 | v2 客户端被 **426** 顶掉（版本闸门错误地管到了 `/ws`） | 按规格 `/ws` 不做版本协商 |

### 3.3 Windows 侧：Provider（无界面识别服务）

`windows/QuizSync.Provider.Cli`（`net10.0-windows`）：

```
dotnet QuizSync.Provider.Cli.dll provider
```

它会：探活（8765–8770）→ 读主机数据目录的 `control.token` 走控制面拿配对码 →
**自己配对**成一个普通已配对设备（`windows-provider`，令牌 DPAPI 落 `<共享根>\app\provider.token`）
→ 连 `/ws` 发 `hello{role:"provider"}` → 收到 `recognition_job` 就取图 → 调 AI → 回 `recognition_result`。

- **为什么 Key 在 Windows 端**：主机按架构不持有 API Key（Key 是 DPAPI 密文，只有本机用户解得开）。
- **复用的现成代码**：`AnalysisEngine`（缓存/配额/重试/容错解析）、`AnalysisCache`、`QuotaGuard`、
  `HttpAiProvider`、`Prompt`、`ResponseParser` —— 一行都没重写。
- 退出码：`0` 正常 / `2` 没配 AI / `3` 找不到主机 / `4` 主机不认令牌（要重新配对）。
- 与界面的关系：**两个进程读同一份 `ai.json`**（所以 `AiSettings`/`SecretStore` 从
  `QuizSync.App` 下沉到了 `QuizSync.Provider`），设置页里配好的 Key 直接就能用。

### 3.4 怎么验（本轮实测，24 PASS / 0 FAIL）

`TemporaryConversationFile\code\verify_host_recognition.ps1`：起真主机 + 真 Provider，只用 curl 打真 HTTP。

```
PASS  主机 /health、控制面读配对码、配对拿到 64 位令牌
PASS  未选合集时 /info 如实报 null；切合集后 active_collection_id=c-verify name=端到端验收
PASS  未起 Provider 时 ai_configured=false
PASS  上传图片（内容寻址）→ hash=027d5556…
PASS  无 Provider 时 error_code=provider_offline，耗时 246 ms（≤2 秒）
PASS  Provider 连上主机（openai-compatible / deepseek-flash）
PASS  Provider 在线后 ai_configured=true（它自述的值）
PASS  真识别 done 耗时 2321 ms；question_count=1；15 个协议字段齐全
PASS  type=single、choice=["B"]、标绿判据 choice ∩ options[].label 命中 B
PASS  /tasks/active 识别期间采样到 analyzing（不是恒 idle）
PASS  同 task_id 复提 → done + cached=true（幂等，不重复调 AI）
```

其它：主机仓库 `build` 0 警告 0 错误、`test` **74 通过**（56 → 74）、6 组 120 步向量全绿；
本仓库 Windows `build` 0 警告 0 错误、`test` **189 通过**（178 → 189）；
安卓 `:core:test` 全绿（含 Kotlin 客户端打真主机的 E2E）。

⚠️ **真识别那几条用的是用户的真 Key** —— 而本轮发现它曾被更早的探针覆盖成桩配置
（`127.0.0.1:9099` / `ui-key`），已从 1.1.0 的 `secure.bin` 取回并写回。
**探针一律用 `QS_APP_DATA` 指向临时目录**，别再写用户的真实数据目录。

### 3.5 还没做（别当成做完了）

- **界面不会自动拉起主机或 Provider**：现在要手动起两个进程（计划里的「一键拉起」未做）。
- **本机按 F8 那条链仍要等 `QuizSync.App`**：Provider 不注册热键、不截屏、不带界面。
- **主机本机截屏任务的「双端互显」**在 2.0 要改走 op 同步（Provider 与手机各自 apply），未动。
- **协议仓的 v2 草案仍未写**：`recognition_job` / `recognition_result` / `provider_online`
  这三个帧目前只落在两个实现与本仓库文档里 —— 写 `versions/v2-draft/` 时必须收进去。
- `SyncUploader` 与 `HostSession` 各有一份「读控制面 → 配对 → 存令牌」，**这是已知重复**
  （身份不同：一个是 `windows-desktop` 推 op，一个是 `windows-provider` 收派发）。

---

## 四、验证纪律（这一程反复救命的几条）

1. **验形状要用图，不能只采样点** —— 圆角 bug 就是采样点恰好落在白区、一直显示「正常」，
   换成 `tools/ascii_map.py` 打 ASCII 色块图才一眼看出「整片全白、只有角上两三像素被切」。
2. **判据要落在真实状态变化上，别落在渲染输出上** —— 「整屏被缩」两次都是判据看错了对象
   （把「状态里有没有」当成「屏幕上有没有」）。
3. **改到壳层/布局容器时，必须同时复核基线状态**（这里是「什么都不压栈」），不能只验新功能那条路。
4. **向原生命令传含引号的参数一律走文件** —— PowerShell 5.1 会把内联 JSON 的引号改坏，
   我因此把 5 条正确的服务端响应误判成「读 body 坏了」。
5. **子串匹配会骗人** —— 检查「有没有某个 import」时 `...SnackbarHost` 是
   `...SnackbarHostState` 的前缀，`-match` 返回真，实际没那行。按**整行**判断。
6. **`.NET` 静态文件 API 走进程 CWD**，不认 PowerShell 的 `Set-Location` —— 一律用绝对路径。

## 五、新增的验证工具（`tools/`）

| 工具 | 用途 |
|---|---|
| `check_centering.py` | 量文字是否水平居中（数值判据，不是观感） |
| `node_bounds.py` | 打印节点 bounds；用于「动画中坐标有没有变」这类判据 |
| `ascii_map.py` | 把截图一块区域打成 ASCII 色块图 —— **形状类问题的唯一可靠手段** |

---

## 六、安卓端：真机能识别了（本轮）

主机通了之后，安卓端第一次在真机上跑通 **相册选图 → 压缩上传 → 电脑调 AI → 结果回到手机**。

### 6.1 本轮补的接线

| 文件 | 做了什么 |
|---|---|
| `MainActivity.kt` | `LifecycleEventEffect(ON_START/ON_STOP)` → `setForeground`；深链**不再**同时喂给 VM（配对只留一条路）；`Route.Result` 进 `AnimatedContent` 并参与主壳缩放/压暗 |
| `state/QuizViewModel.kt` | `rebuildApi()`（配对后重建客户端）；`submitImageFromUri()`（相册选图 → 上传 → 建任务，全程静默）；`applyResultPayload()`（把任务载荷写进本地库）；`reloadResult()`（结果页「重新加载」的对接点） |
| `RecordStore.kt` | `applyTaskResult(session, hostDeviceId)`：走现成 LWW applier 把结果**镜像**进本地库（作者记成主机、op_id 带载荷指纹做幂等、只写非空字段） |
| `ImageCompress.kt`（新） | 长边 ≤1600 / 质量 80（1.1.0 与 Windows 同值）；`sampleSizeFor` 有 JVM 单测 |
| `ui/ResultScreen.kt` | 结果页规格 §3 全部：骨架 / 药丸行 / 两种空态 / 题目卡片（题号行 8 元素、材料折叠、选项标绿、答案块、解析块、告警条）/ **上一次·下一次识别（标题栏右上角，与主壳切标签同一套横向切换）** / 底部免责声明 |
| `ui/HomeShell.kt` | 「相册选图搜题」FAB（规格 §2.2 的 `canUpload` 条件）+ 系统相册选择器；FAB 补了无障碍标签（Compose 不会把 FAB 的 text 暴露成 content-desc） |

### 6.2 真机验收（脚本 `verify_android_recognition.ps1`，6 PASS）

```
PASS  主壳显示「已连接 · 手机验收」（主机名 + 当前合集）
PASS  识别模块已开启（FAB 的前置条件）
PASS  界面到达「识别完成」
PASS  识别结果已写进手机本地库（applyTaskResult 生效）
PASS  结果页打开（标题「识别结果」）
PASS  结果页有题目内容：药丸行「已完成/共 1 题/1 张图片/合集记录/deepseek-flash」、
      题号行「1 / 第 7 题 / 单选题 / 把握 99% / 09-26 23:53」、题干、A/B/C/D 四个选项、
      解析「将 $x=1$ 代入函数：…因此选 B。」
```

主机侧同时可见：`images` 有手机上传的图（18101 B，`uploaded_by` = 手机 device_id）、`tasks` 一条 `done`、`sessions` 一条 1 题。

### 6.3 这一轮抓到的三个 bug（都是「状态层写好了，没人拨那个开关」）

1. `setForeground()` 无人调用 → 1 秒轮询与自动跳结果页**永不生效**；
2. 深链配对走了**两条路**（VM 与旧 PairScreen 各发一次 `POST /pair`）→ 两次签发互相作废令牌，屏幕显示「配对成功」而所有请求 401；
3. 配对成功后 VM 的 `api` **从不重建** → 轮询一直用旧令牌（而同步那条路用的是新令牌，所以主机 `last_seen` 有更新，掩盖了故障）。

细节与取证方式见 `docs/DECISIONS.md` 的「安卓端」一节。

### 6.4 还没做

> ⚠️ **勘误（2026-09-27，第 141 轮）**：第 1 条**已经做完了** ——
> `ui/components/MathText.kt` + `ui/mathcontent/MathContent.kt`（LaTeX 解析、化学式上下标、表格），
> 由 `770bb79` / `3b4c4db` 两轮收紧判据。悬浮球截屏采集链也已接（`capture/CaptureService.kt`）。
> **仍然没做的**：配对页按规格重做、主机 `POST /images` 的 `width`/`height`、
> 以及下面最后一条「只有代码路径覆盖、真机没见过」的那批标记。

* ~~**`MathText`（规格 §3.6）安卓端没有实现**~~ —— 已实现（见上方勘误）。
* 悬浮球截屏采集链（§3.1/§3.2 主路径）~~未接~~ —— 已接；识别模块二级页（§5）已做；
  **配对页按规格重做（§2.1）仍未做**，接之前必须**一次替换**旧 `PairScreen`，不能并存；
* 主机 `POST /images` 返回的 `width`/`height` 仍是 null（协议 §3.2 要求有）。
* 结果页的**判断题按钮 / 黄框 / 猜测徽标 / 告警条 / 材料折叠 / 已人工修改药丸**只有代码路径覆盖，
  没在真机上看到（目标会话是普通单选且各标记为 0）—— 本地库里有对应 seed 数据，下一轮可补验。
