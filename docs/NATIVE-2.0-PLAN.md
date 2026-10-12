# QuizSyncAI Native 2.0 · 架构审计与迁移计划

> **状态**：已定稿（2026-09-26）。三项地基决策已由用户拍板，见 §0.2。
> **性质**：本文件是「原生 2.0 重写」的**唯一执行依据**，面向后续执行 agent。
> **事实来源**：7 份只读审计（本仓库全量源码 / 测试 / 文档，逐条带 `文件:行号`），合计 263 个 Dart 文件 62,945 行、6 个 Kotlin 文件 1,076 行、`apps/desktop/windows` 全部 C++、11 份 `docs/`、670 项测试。
> **冲突裁决**：用户可见行为以 `docs/SPEC.md` 为准，协议以 `docs/protocol.md` 为准，数据与同步以 `docs/data-model.md` 为准，AI 以 `docs/ai-contract.md` 为准。本文件与四份契约冲突时**以契约为准**，并在本文件记录差异。
> **本轮边界**：本文件只是计划。不含任何实现、不改动现有代码、不建分支、不发版。

---

## 0. 导读

### 0.1 三条必须先纠正的事实（它们直接改变工作量估算）

| # | 流传的说法 | 代码里的真相 |
|---|---|---|
| 1 | 「Windows 端 = Flutter + 自写 C++ 原生层」 | **零自写 C++**。`apps/desktop/windows/runner/CMakeLists.txt:9-17` 只编译 Flutter 模板的 4 个源文件（549 行全是模板）。分层窗口、DIB、`UpdateLayeredWindow`、窗口过程、`RegisterHotKey`、GDI 截屏、DPAPI **全部在 Dart 侧 FFI**（`package:win32` + `DynamicLibrary.open`）。要原生化的是约 3,600 行 Dart FFI 浮层代码 + 881 行热键代码 |
| 2 | 「`server/` 是复用 `quizsync_core` 的独立服务端」 | 它是 `quizsync_core` 的**20 文件逐字复制**（SHA256 全同）+ 服务端第二份实现，且**已经漂移**（少 `syncPageSize` / `maxQueueDepth`，走独立版本线 `1.0.1`） |
| 3 | 「协议文档就是实现」 | `docs/protocol.md` 与实现已分叉 3 处：文档要求握手带 `X-QS-Device-Id`（服务端**不读**）、文档里的服务端 `ops` 事件（**未实现**）、文档里的网络重试 1s/3s/10s（`ApiClient` **完全没有**） |

### 0.2 已拍板的决策（不再讨论）

| 编号 | 决策 | 内容 |
|---|---|---|
| **D1** | **Server 是唯一 Host** | `QuizSyncServer`（独立进程，或客户端内嵌同一个 `QuizSync.Server.Core` 库）= 唯一 Host：监听、配对、设备与令牌、图片 Blob、任务队列、op 中继与水位、状态/配置/日志。**截屏 / AI / 热键 / 悬浮窗 / UI 全在 Windows 客户端（Provider）**。Server 对 ops **不透明**，不认识 `Question`/`Session` |
| **D2** | **热键归客户端** | 两个槽位：`F8` 截屏识别（多页模式下 = 结束多页并上传已抓的页）/ `F9` 多页模式。归 Windows 客户端，**含其 `--headless` 模式**。Server **永不注册热键** |
| **D3** | **包名区分 + 卸载重装**（第 167 轮按用户口径改过） | Android 正式版 `applicationId = com.quizsync.android.native`（开发版 = 它 + `.dev`），与 Flutter 线的 `com.quizsync.android` **明确区分**；升级路径 = 卸载旧版 → 装新版 → 重新配对 → 从电脑端拉回历史。**升级前先让手机在电脑在线时跑空离线队列**（卸载会清掉未上传成功的图） |
| — | **版本起点** | 协议 `v2.0.0`、客户端 `2.0.0`、Server `2.0.0`；Flutter 线冻结在 `1.1.0` + tag `v1.1.0-flutter`。**不要照抄 `dist\` 里的最大号**（目录里同时存在 Windows-only 的 1.1.3 与统一后的 1.1.0） |
| — | **两条「保守优先」约束** | ① Windows 视觉**沿用现有配色/间距/卡片样式/8px 节奏**，只换承载层，不拿 Fluent 默认外观重新设计；② 悬浮窗 spike 的验收标准是「**与 1.1.0 观感逐条一致**」（12 项设置 + 6 套配色的实际效果），不一致就退回「自绘底色 + 整窗 alpha」，宁可少一点现代感也不改变用户已习惯的样子 |

### 0.3 用户可见的连续性（回答「布局和用法会不会变」）

**不变**：Windows 主窗口结构（先选合集 → 左列表右卡片 → 上一次/下一次/回到本次）；设置页 **10 分类与顺序**、每项设置的名字/默认值/落库键名；热键与托盘菜单；悬浮球与悬浮窗的行为契约；Android 三 tab（当前任务/历史/设置）与全部手势语义；数据（11 表 + FTS，Windows 直接打开现有 `userdata\quizsync.db`）；覆盖安装/卸载重装的升级路径。

**会变**：① 进程数量（多一个 `quizsync-server.exe`，可加设置「客户端退出时同时停止服务」，默认关）；② **从托盘真正退出客户端后**，手机仍能配对/看历史/收同步（今天彻底连不上），只有识别会提示「电脑端未运行」；③ Android 视觉（框架换了，必然变，但页面与手势语义锁死）；④ 公式显示（现在是悬浮窗按 LaTeX 源码显示，2.0 统一走 WebView2 + KaTeX，属改善，需做字号/间距对齐验收）。

---

## 1. 当前项目架构审计

### 1.1 技术栈与规模

| 包 | 版本 | 第三方依赖 | lib 行数 | test 行数 / 用例 |
|---|---|---|---|---|
| `packages/quizsync_core` | 0.1.0 | **10，全纯 Dart**（drift / crypto / image / archive / dio / shelf ×4 / web_socket_channel） | 17,406 | 5,231 / **212** |
| `packages/quizsync_ui` | 0.1.0 | **1**（flutter_math_fork） | 2,944 | 847 / **56** |
| `apps/desktop` | 1.1.0+33 | 11（drift、flutter_riverpod、image、qr_flutter 纯 Dart；win32、ffi；window_manager、tray_manager、hotkey_manager、path_provider、desktop_drop） | 13,380 | 5,950 / **228** |
| `apps/android` | 1.1.0+22 | 8（drift、flutter_riverpod、image；mobile_scanner、image_picker、flutter_secure_storage、path_provider、sqlite3_flutter_libs） | 5,386 | 3,228 / **88** |
| `server/` | 1.0.1 | 12，全纯 Dart | 6,143 | 1,801 / **90** |

合计 **263 Dart 文件 / 62,945 行**（含生成物 `core/lib/db/database.g.dart` 10,071 行，占 16%）、Android Kotlin **6 文件 1,076 行**、**零自写 C++**、**无 CI、无 CHANGELOG**。测试合计 **670 项**（`docs/progress.md` 记录：core 212 / ui 57 / desktop 226 / android 85 / server 90）。

Android 的 6 个 Kotlin 文件：`CaptureBridge.kt`(360)、`FloatingBallManager.kt`(275)、`CaptureService.kt`(215)、`ScreenCaptureAccessibilityService.kt`(94)、`SystemUiBridge.kt`(72)、`MainActivity.kt`(60)。

### 1.2 各端架构现状

**Android**：三 tab（`NavigationBar` + `IndexedStack`）+ `Navigator.push` 二级页，**无路由表**；18 个 Riverpod provider + `SettingsController`(ChangeNotifier) 的**双轨失效机制**（`settingsRevisionProvider` 手动失效）。8 个页面：`PairingPage`、`AndroidHomePage`、`CurrentTaskPage`、`HistoryTab`、`CollectionSessionsPage`、`AndroidSettingsPage`、`RecognitionSettingsPage`、`ResultPage`；4 个弹窗。采集**只有悬浮球一个入口**（应用内无截屏按钮，相册是 FAB）。网络：WS 在 core（退避 1/2/5/10/30s、401/403 停重连）+ **1 秒 `GET /tasks/active` 兜底**（指纹 + 界面签名双重去重、`ops_lamport` 水位触发 pull-only）。版本适配：API 33 通知权限、API 34 `foregroundServiceType=mediaProjection` 且 `registerCallback` 必须先于 `createVirtualDisplay`、API 35 **引擎跳过 `setStatusBarColor`**（只能 App 自绘条带 + 自家桥重贴）。

**Windows**：单窗口 + `CollectionGate`（未选合集挡住主界面）；设置页 **10 分类**（使用说明/显示/API/连接设备/识别/悬浮球/悬浮窗/热键/数据/关于）；界面缩放走**虚拟画布 `OverflowBox` + `Transform.scale`**（根节点紧约束会夹回窗口，M14 实测「那片黑」）；托盘勾选项必须是 `MenuItem.checkbox`；热键是「**一次 await 都不做**的同步 `PeekMessageW` 循环 + 共享内存命令 + 每 2 秒同 id 再注册自检」；截屏 GDI BitBlt + 藏/恢复窗口**不抢焦点**；剪贴板 700ms 轮询 + `BI_BITFIELDS` 解析；AI Key 走**自写 DPAPI**（`secure.bin`）；悬浮窗/球 = Dart 离屏渲染 + 分层窗口（三块位图缓存 48/96/12MB、按片出图 1024/8192、预乘 BGRA、`UpdateLayeredWindow`）。

**网络与协议**：`shelf` 单端口 8765（占用试 8765–8770），HTTP + `/ws`；除 `/info`、`/pair` 外全部 `Bearer`，服务端只存 `sha256(token)`；**15 个 HTTP 端点 + 12 个 WS 事件**；6 位配对码 5 分钟 TTL、5 次/分、10 次失败锁 60 秒（**计数落库跨重启**）；上传**边读边限长**（2MB，两段 413）+ 每设备 60 秒 30 次限流；错误统一 `{code,message,retry_after_seconds}`（12 个 code）。

**数据库与同步**：drift **11 张表**（+ FTS5 `trigram` 虚表），`schemaVersion=3`，`beforeOpen` 按**硬编码 6 列清单**幂等补列；同步 = Lamport 时钟 + **逐字段 LWW**（字段时钟 `{l,d}` → 缺失回落行级基准 → 平局按 `device_id` 字典序，**从不用墙钟**）+ 软删除墓碑（30 天 + 全对端 acked 才 GC）+ 离线队列（存 `tasks` 表，上限 20，5 次后死信，`QueueRetryLater` 不计 attempts）。**本地专属列只有两列**：`images.local_path`、`devices.token_hash`；只改这两列会走 upsert 短路，必须显式 `setImageLocalPath`。**Windows 端不跑同步引擎**（只做服务端），Android 端跑全量。

**AI**：3 个 provider（openai-compatible 默认 / anthropic / gemini），prompt 全文 + `v1-<sha256前8>` 版本号，输出 JSON schema 17 字段；容错 5 步（直解 → 剥围栏 → 首 `{` 到末 `}` → 附严格提醒重试 1 次 → failed 保 `raw_response`）；标绿规则「只读结构化字段，禁止从 analysis 文本搜答案」；`confidence<0.6 || need_review` → 黄色；重试 `[1s,3s]` 最多 2 次、**无幂等键**；缓存 key = `image_hash+prompt_version+model`（30 天，**只对单页生效**）；图片统一「长边 ≤1600 + JPEG q80」。

### 1.3 值得保留 / 必须重写 / 技术债

**值得保留（语言无关的核心资产，应跨语言重实现而不是重新设计）**
- `core/lib/sync/`（Lamport、`lww_applier.dart:35-77` 三段式、`field_clocks`、`sync_engine` 的三条水位纪律）+ `core/lib/db/`（表结构、op writer、墓碑、离线队列）—— **212 项测试就是它的可执行规范**。
- `core/lib/ai/`（prompt 全文 `prompt.dart:7-104`、`response_parser` 前三步与 `toQuestions`、`highlight`、`retry_policy`、三份 `buildRequest`）+ `core/lib/util/image_proc.dart` + `core/lib/server/`（鉴权/限流/配对/幂等/流式限长）。
- 四份契约 + `DECISIONS.md` 的 **27 类「明确不改/不做」** + `AGENTS.md` 的平台实测坑（见附录 B）。
- 交付链门禁（见附录 C）：版本号唯一来源、打包红线、APK 60 项审计、`build_all.ps1` 的 `ALL GREEN`、`probe_memory.ps1`。

**必须重写（无移植价值）**
- 桌面浮窗/悬浮球（`float_window*.dart` 6 文件 ≈2,900 行 + `floating_ball.dart` + `ball_paint.dart`）：它是「不能开第二引擎 + widget 树收不到指针事件」两个约束下的产物。WinUI 3 可以用真实 XAML 窗口。
- 桌面热键服务（456 + 425 行，补偿 Dart isolate 线程漂移）：.NET 用一条专用 `GetMessage` 线程天然解决，但**「周期自检 + 显式注销 + 触发那刻判断是否吞掉」三条结论必须保留**。
- Android 采集与悬浮球的编排层（`CaptureBridge` 的通道协议、`AndroidAppState`、18 个 provider）：`CaptureService.kt` 的 MediaProjection 复用逻辑可**整文件复用**。

**技术债（按严重度；第 1 条是唯一的 P0 数据正确性缺陷）**
1. **`pollTaskUntilDone` 逐题 `upsertQuestion`**（`apps/android/lib/state/app_state.dart:214-216`）**绕过** `applyAnalysisResult` 的 `answer_edited/analysis_edited` 保护，与 M47 在 `live_updates.dart:151-155` 的口径**直接冲突** —— 轮询路径会覆盖用户手改。
2. 服务端越权与限制缺失：`GET/DELETE /api/v1/devices`、`/sync/snapshot`（含全量 devices/collections）对**任何已配对设备**开放；图片下载端点无 hash 白名单；除 images 外**所有 JSON 端点无体积上限**；token **无过期**只有吊销；无 CORS 处理。
3. `device_id` 是固定字面量 `android-local` / `windows-local` → **架构级「只能一台安卓」**，第二台手机覆盖同一行 token hash。
4. 版本协商 426 比较的是 **appVersion 主版本**而非 `protocolVersion`，且**不带版本头的请求一律放行**。
5. 多页失败语义两端不一致：桌面最后一页失败**保留已抓页**，安卓**全部丢弃**（`multipage_capture.dart:81-92`）。
6. `server/` 双份实现已漂移；`applyRemoteOp` 对 `entity='snapshot'` **只记账不落地**；`confirmPendingOverwrite` **无任何 UI 调用点**（`pending_overwrite` 只写不读）。
7. `_upgradedColumns` 只有 6 列的硬编码自愈清单；tombstone GC 在**无 `peer_state` 记录**时 `minAck=null` 会**跳过 ack 检查直接删**。
8. 工程面：`namespace com.example.quizsync_android` ≠ `applicationId com.quizsync.android`；`key.properties` 明文口令 + `keystore/口令-请妥善保存.txt` 同仓；无 CI；`database.g.dart` 10,071 行入库；**契约目标「Windows 空闲 ≤150MB」实测 277MB，从未达成**。

---

## 2. 三仓库最终架构

### 2.1 职责与「绝对不放」

| 仓库 | 是 | 绝对不放 |
|---|---|---|
| **QuizSyncProtocol** | 通信规范：HTTP/WS 契约、消息结构、JSON Schema、协议版本与兼容策略、一致性测试向量。**它是「纸面 + 数据」，不含可执行的产品逻辑** | UI、数据库、ViewModel、AI SDK、平台代码、任何一端的实现、任何业务规则（「合集删除不对称」这类**产品语义**可以写进规范，实现不在这个仓库） |
| **QuizSyncServer** | 唯一 **Host 角色**：监听、配对与鉴权、设备注册表、图片 Blob、任务队列与结果缓存、op 中继与水位、状态/配置/日志、`recognition_job` 派发。两个产物：`quizsync-server`(CLI) + `QuizSync.Server.Core`(库) | 任何 UI（含托盘/通知外壳）、AI 调用与 API Key、截屏与全局热键、悬浮窗、题库与同步冲突算法（ops 对它**不透明**，只按 lamport 记录与转发）、Flutter/Kotlin/Flutter 代码 |
| **QuizSyncAI** | 产品端：Windows 客户端（**Host 的客户端 + Provider**：截屏/热键/悬浮窗/托盘/AI 调用/历史与设置 UI/本地权威库）+ Android 原生 App（Host 的客户端） | Windows 代码进 Android、Android 代码进 Windows、把 Protocol 的 schema 复制一份改、把 Server 的实现代码抄进来 |

### 2.2 D1 的落地形态

```
有独立 Server 进程时：  App ──loopback(HTTP/WS + 本地控制令牌)──► Server ◄──LAN── 手机
没有独立 Server 时：    App【内嵌 QuizSync.Server.Core，**同一个进程**】  ◄──LAN── 手机
两者互斥：              **探活（HTTP）+ 端口所有权**，任一启动顺序下都只有一个 Host
```

> **第 167 轮落地情况**：默认走的就是下面那条「内嵌」——`QuizSync.ServerBridge/EmbeddedHost.cs`
> 在本进程里起 `QuizSyncHost`（Kestrel）并照旧监听 `0.0.0.0:8765`（见 `docs/DECISIONS.md` 第 167 轮）。
> 两条**偏差**要记住：
> ① **没做「同进程直通」**（第 110 行允许的那条优化）：内嵌宿主仍走回环 HTTP/WS，一个字节的协议都没换 ——
>    直通属于优化，要引入得先有「两种模式逐字段一致」的对拍证据。
> ② **命名互斥体运行时没用**：`SingleInstance` 只有自己的单测（`ServerBridgeTests`），
>    运行时的互斥靠「先探活、占用端口」这一条。所以「同数据目录 + 不同端口」的两个宿主理论上仍可并存
>    （这是**既有**风险，不是本轮引入的；要收口得让 CLI 与内嵌方共用同一个互斥体名字，属跨仓改动）。
> **代价**：宿主活在应用进程里 ⇒ 应用真的退出，手机就断（关窗收托盘不算退出）；
> 要「关掉客户端手机还能连」就显式走独立进程（`HostLaunchMode.Standalone` 或 `quizsync-server run`）。

- **内嵌模式允许同进程直通优化**（无 IPC、无序列化），但**必须与远端模式行为完全一致**：同一套协议向量跑两遍，时延差异记录在案。
- **识别任务统一**：所有识别都是一条 task（含桌面自己按 F8 的那条）。这消灭了今天的特例 —— `ActiveTask.hostInitiated` 标记、`task_id == session_id`、「本机截屏任务不入队、库里没有 tasks 行」。**这是契约级变更**，v1 兼容层必须仍按老语义回答旧客户端（见 §4.6）。
- **无界面识别能力保留**：客户端支持 `--headless`（不开主窗口，只做热键 + 截屏 + AI + 任务处理）。**不在 Server 里塞 AI**。

### 2.3 热键归属（D2 的理由）

| 判据 | Server | Windows 客户端 |
|---|---|---|
| 触发后要做什么 | 截屏 → AI → 落库 → 广播（Server 若做就要有 Key 与系统钩子） | 同左，且要**显示**实际生效键/失败原因 |
| 需要的上下文 | 无（连当前合集是什么都不知道） | 当前合集、AI 配置、暂存区、悬浮窗、结果页跳转、设置页热键录入器 |
| 「按键那刻要不要吞掉」 | 必须跨进程问客户端（要新增 IPC 与超时语义） | 同进程一个布尔判断（现 `_hotkeyBlocked()`） |
| 失败可观察性 | 无 UI，只能写日志 | 热键页直接显示 `HotkeyStatus` |

**结论**：热键 ≡ 「本机截屏并送 AI」，归 Provider。Server 永不注册热键。

---

## 3. Repository Dependency Graph

```
                        ┌──────────────────────────────┐
                        │      QuizSyncProtocol        │  规范 + JSON Schema + 一致性向量
                        │  （零依赖，纯文档 / 数据）      │  ← 三端唯一共享物
                        └───────┬──────────┬───────────┘
                    生成/校验模型 │          │ 生成/校验模型
                                 ▼          ▼
        ┌────────────────────────────┐   ┌───────────────────────────────┐
        │      QuizSyncServer        │   │         QuizSyncAI            │
        │  CLI + QuizSync.Server.Core│◄──┤  Windows 客户端（Host 客户端+Provider）
        │  Host：传输/配对/Blob/中继   │   │  Android 客户端（Kotlin）      │
        └────────────────────────────┘   └───────────────────────────────┘
                    ▲
                    └── NuGet 单向引用（唯一允许的代码依赖方向）
```

**依赖规则（写进三仓 README）**
- Protocol **零依赖**（只有 JSON Schema 与 markdown，允许一个纯校验脚本）。
- `QuizSyncServer → QuizSyncProtocol`；`QuizSyncAI → { QuizSyncProtocol, QuizSync.Server.Core(NuGet) }`；**Android 端只依赖 Protocol**，不依赖 Server 库。
- **禁止**：Protocol → 任何实现；Server → QuizSyncAI；Android ↔ Windows 互相引用；三仓库出现环。
- 版本关系：`Server.Core` 的 NuGet 主版本 = 它实现的协议主版本；客户端声明「我支持协议 v2 的 2.0–2.x」。
- 每个仓库**独立可构建、独立可发布**：Protocol 无构建；Server 一条 `dotnet build`；AI 两条（MSBuild + Gradle）。

### 3.1 通信图（运行期）

```
┌────────────┐   LAN：HTTP/1.1 + WS（Bearer，明文，8765）  ┌───────────────────────┐
│ Android App│◄───────────────────────────────────────►│   QuizSyncServer      │
│ Kotlin     │   v2 主用；v1 兼容层（Flutter 1.1.0）      │   Host 角色            │
└────────────┘                                          │   · 配对/设备/令牌      │
                                                        │   · Blob（图片字节）    │
┌────────────────────────────┐  loopback：HTTP/WS +     │   · 任务队列 + 结果缓存 │
│ QuizSyncAI Windows         │  本地控制令牌（或内嵌直通）│   · op 中继 / watermark │
│ · Provider：截屏/热键/AI    │◄───────────────────────►│   · 状态 / 配置 / 日志  │
│ · UI：主窗口/悬浮窗/托盘     │                          └───────────┬───────────┘
│ · 本地权威库（LWW applier）  │                    recognition_job ／ │ recognition_result
└────────────────────────────┘                                       ▼
        ▲ 用户按 F8/F9 ─► 截屏 ─► 上传 Blob ─► 建 task ─► Server 派发给 Provider
        └── Provider 调 AI（Key 只在 Windows，DPAPI）──► 结果回 Server ──► 广播两端
```

### 3.2 数据流图（一次「手机截屏搜题」）

```
Android 悬浮球单击
 → CaptureService（复用 MediaProjection 会话）取帧 → 长边 ≤1600 + JPEG q80
 → POST /api/v2/images（multipart，边读边限长）        → Server：sha256 命名落盘 + 记元数据
 → POST /api/v2/tasks{task_id(UUID), image_hashes[], collection_id}
 → Server：幂等入队（同 hash + 同页序的 done 直接复用）→ WS 发 recognition_job 给 Provider
 → Provider：取图（本地已有或 GET /images/<hash>）→ 调 AI → 解析 → 回 recognition_result
 → Server：落结果 + 记录 ops（Provider 与 Android 各自 apply）
 → WS 广播 task_result 给 Android 与客户端 → 两端 LWW 应用 → 结果页自动跳转（只跳一次）
失败分支：Provider 离线 → 任务保留并回 provider_offline（≤2s，不挂死）
          页序不一致 / 缺页 → 明确报出是第几页
```

---

## 4. Protocol 设计方案

### 4.1 传输与版本

| 项 | v2 规范 | 修的是什么 |
|---|---|---|
| 传输 | HTTP/1.1 + WS 同端口。**不做 HTTPS**（局域网明文是「已知且接受」的前提，用「吊销设备」兜底处置；`optimization-plan.md §4.C` 已明确否决 HTTPS 化） | 与既有决策一致 |
| 端口 | 默认 8765，范围 6（8765–8770），实际端口写进二维码与 `/info` | — |
| **版本** | **URL 路径承载主版本**：`/api/v2/...`；响应头 `X-QS-Protocol: 2.<minor>`；协商依据是 **protocolVersion**，不是 appVersion | 修「426 比错对象」 |
| 兼容策略 | 主版本不同 → `426 protocol_mismatch`（**对所有请求生效，包括没带头的**，默认按最低支持版本处理）；同主版本内**只增不改不删**；未知字段**必须忽略**；字段废弃走 `deprecated_since` + 至少一个大版本 | 修「不带版本头一律放行」 |
| **v1 兼容层** | Server v2 **必须同时提供 `/api/v1/*` 兼容子集**（info / pair / images / tasks / tasks-active / collections / devices / sync-ops / snapshot / ws），行为与 Flutter 1.1.0 客户端逐字兼容 | 「手机端更新必然滞后」是真实场景：`Android 1.1.0 + Server 2.0` 必须能通 |
| 能力协商 | `GET /api/v2/info` 的 `capabilities` **算出来**（不是硬编码数组），并给 `protocol{min,max}`；客户端按能力分支 | 修「capabilities 是静态数组、客户端不按它分支」 |
| 安全修复 | `devices` / `snapshot` / `rekey` 等**主机专属操作要求 `role=host`**（token 绑角色）；图片下载**强制 64 位 hex 白名单**；**所有** JSON 端点加体积上限（默认 1MB，ops 批量 8MB，超限 413）；token 支持 `expires_at`（默认 180 天，0 = 不过期） | 修越权、路径拼接、无上限、token 永不过期 |
| 设备标识 | `device_id` = 每安装一个 **UUIDv4**；**保留「同时一台安卓」的策略**（`role=android` 只允许一个活跃，第二个进 `pending` 并在主机端可见） | 修架构级单设备限制，且不改产品行为 |
| 心跳 | 服务端 ping 30s **并校验 pong、超时 90s 断开**；客户端静默 40s 强制重连 | 修「服务端不校验 pong、不断死连接」 |

### 4.2 图片传输：三种方案对比（结论：HTTP 上传 + WS 通知）

基准：单页压缩后典型 300–800KB，上限 2MB；一次多页任务最多 6 页（最坏 ≈12MB）。

| 维度 | ① Base64 进 JSON | ② **HTTP 上传 + WS 通知（采用）** | ③ WS 二进制帧 |
|---|---|---|---|
| 线缆体积 | ×1.33（12MB → 16MB） | ×1.00 | ×1.00 + 帧头 |
| 峰值内存 | 三端都要同时持有 JSON 文本 + 解码后字节（16MB 文本 → 解析期 ≥32MB） | 边读边写盘，8KB 缓冲即可 | 需自造分片重组缓冲 |
| 背压/流控 | 无 | HTTP 天然背压 + `Content-Length` 预检 | 要手写窗口/ACK，否则大帧阻塞控制消息（队头阻塞） |
| 断点续传 | 不可能 | 可分块（v2.1 用 `PATCH /uploads/{id}` + `Content-Range`） | 要自造 |
| 限长/413 | 只能解析后判断（太晚） | **读取中就能 413 并停读**（现状已实现） | 需要协议级限制 |
| 去重 | 要整包读完才算 hash | 落盘时算，`existed` 幂等 | 同左 |
| 调试 | 最易 | 易（curl 一条命令） | 难 |
| 三语言实现成本 | 最低 | 低 | **高**（OkHttp 与 `ClientWebSocket` 的大帧写读都要额外代码） |
| 与现有实现 | 全改 | **零改动（现状即此方案）** | 全改 |

**补充三条**：① 大 JSON（op 批量、snapshot）加 `Content-Encoding: gzip`，并改成 **NDJSON 流式分页**（去掉「整个 snapshot 一次性 JSON」这个无上限端点）；② Base64 **只保留在出站到 AI 服务商**的请求里（那是别人的契约）；③ 图片在 Provider 与 Android 本地也是文件 + hash，不做内存常驻。

### 4.3 目录结构（QuizSyncProtocol）

```
QuizSyncProtocol/
├─ README.md                    角色、部署形态、如何据规范实现一端
├─ docs/            00-overview.md（架构/角色/术语）、01-deployment.md（三种部署形态）
├─ spec/                        规范性契约（每个域一个文件，含 MUST/SHOULD 与验收清单）
│   01-conventions.md           命名 / 时间(UTC ms) / ID(UUIDv4) / 未知字段处理
│   02-transport.md             端口 / HTTP / WS / 明文 / 编码 / gzip / 体积上限 / 超时
│   03-auth-pairing.md          配对码 / 二维码 / 令牌 / 角色 / 吊销 / 限流 / 锁定 / 过期
│   04-http-api.md              端点逐条：方法、路径、角色、请求、响应、错误、幂等键
│   05-websocket.md             事件清单、信封、心跳、重连、补偿、单设备踢旧
│   06-sync.md                  Lamport、逐字段 LWW、墓碑、水位、折叠、快照 bootstrap
│   07-images.md                上传 / 去重 / 命名 / 校验 / 分块(预留) / Provider 取图
│   08-tasks.md                 任务生命周期、幂等、复用规则、Provider 派发与离线语义
│   09-errors.md                错误码全表（code ↔ HTTP 状态 ↔ 可重试 ↔ retry_after）
│   10-versioning.md            版本策略、兼容矩阵、废弃流程、v1 兼容层要求
│   11-capabilities.md          能力协商与降级
├─ schema/                      JSON Schema 2020-12：envelope.json、entities/、messages/、index.json
├─ examples/                    每个消息一组 request/response 示例（含错误示例）
├─ versions/                    v1-snapshot.md（**按现有实现反推的现状规范**）、v2-draft/、compat-matrix.md、CHANGELOG.md
└─ conformance/                 语言无关一致性向量
    ├─ vectors/*.ndjson         配对 / 鉴权 / 上传 / 任务 / sync 收敛 / 错误
    └─ README.md                如何用 Kotlin / C# / Dart 回放（**三个实现的唯一裁判**）
```

**Schema 组织**：`envelope.json` + 每种消息一个 schema；实体 schema 被消息 `$ref`。**v2.0 不引入 codegen 流水线**（手写模型 + 向量回放保证一致，避免提前引入构建复杂度）。

### 4.4 消息结构（v2）

```
HTTP 请求头：Authorization: Bearer <token> | X-QS-Protocol: 2.3 | X-QS-Client-Version: 2.0.1
             X-QS-Request-Id: <uuid>（响应原样回带，用于排障与幂等观测）
HTTP 错误体：{ code, message, retryable, retry_after_seconds?, request_id?, details? }
WS 信封：    { v:2, type:"task_result", id:"<uuid>", ts:<ms>, corr:"<request_id>", payload:{...} }
```

事件清单（现状 12 个 + 6 个新增）：

| 方向 | 事件 |
|---|---|
| Server → 客户端 | `hello`、`task_update`、`task_result`、`task_failed`、`collection_changed`、`device_revoked`、`ping`、**`ops`（本次真正实现）**、**`capability_changed`**、**`pairing_code_changed`**、**`provider_online`**、**`provider_offline`**、**`recognition_job`**（→ Provider） |
| 客户端 → Server | `hello`、`ack`、`push_ops`、`pong`、**`recognition_result`**（← Provider） |

### 4.5 幂等、超时、重试、重连

- 幂等键：`image_hash`（上传）、`task_id`（客户端生成 UUID）、`op_id`（op 推送）。**新增**：AI 调用加 `idempotency_key`（= `task_id + attempt`），修「超时重发可能重复计费」。
- 超时：HTTP 连接 5s / 读 30s；上传单独 120s；AI 90s；WS 心跳 30s / 40s。
- 重试：**只对 GET 与幂等 POST**，退避 1s / 3s / 10s（现状「文档有、实现无」→ v2 落到规范并三端实现）；4xx（除 408/429）不重试；429 用 `retry_after_seconds`。
- 重连：退避 1/2/5/10/30s；连上后**先拉 ops 再推**；401/403 停止重连并要求重新配对；`device_revoked` 自断。

### 4.6 v1 兼容层的硬要求（**D1 带来的最大兼容风险**）

「任务流水线合一」改了本机任务的语义，而旧客户端依赖这些语义：

| v1 旧语义（必须继续满足） | v2 内部实现 |
|---|---|
| `GET /tasks/active` 的 `done` **直接带 `session`** | v2 内部所有任务都有 task 行；v1 适配器负责拼出等价的响应体 |
| 本机截屏任务「不经任务队列」 | v2 走统一队列，但 v1 适配器不暴露 task_id |
| `task_id == session_id` | v2 各自独立，v1 适配器做映射 |
| 合集删除方向不对称（主机删 → 手机不跟随） | v2 保持（`applyRemoteCollectionDeletes=false` 的产品语义写进规范） |
| 缓存与同图去重**只对单页生效** | v2 保持 |
| 多页硬上限 6 | v2 保持 |

**验收方式**：Flutter 1.1.0 客户端（真实 APK 或 core 测试客户端）对 v2 Server 跑完整链路，逐条对齐。

---

## 5. Android Kotlin 重构方案

### 5.1 工程结构（单 module，按 feature 分包；v2.0 不上多 module + KMP）

```
android/
├─ app/
│   └─ src/main/java/com/quizsync/android/
│       ├─ QuizSyncApp.kt / MainActivity.kt
│       ├─ core/{db,net,model,sync,ai,util,di}
│       ├─ data/{repository,settings,keystore}
│       ├─ feature/{pairing,currenttask,result,history,collections,settings,recognition}
│       ├─ service/{CaptureService,CaptureSession,BallOverlayService,NotificationFactory}
│       └─ ui/{theme,components,navigation}
└─ build.gradle.kts / settings.gradle.kts（**阿里云镜像保留**：本机 maven.google.com 20s 超时）
```

> 多 module + KMP 的真实收益要到「Android 与桌面共享 Kotlin 代码」才出现，而桌面已定 C#，收益为 0 → **明确不做**。

### 5.2 依赖清单（每条都给理由与更轻的替代）

| 依赖 | 为什么需要 / 解决什么 | 更轻的替代 | 维护成本 |
|---|---|---|---|
| Kotlin 2.x + Compose BOM + **Material 3** | 目标就是现代原生体验；M3 给动态取色、组件与无障碍默认实现 | 无（需求本身） | 低 |
| **androidx.navigation-compose** | 现在是「无路由表 + 构造参数传参」；Compose 下手写栈会退化成样板 | 手写 `AppState` 栈（8 个二级页后难维护） | 低 |
| **ViewModel + StateFlow**（不用 RxJava） | 替换 18 个 Riverpod provider + `SettingsController` 双轨失效；`StateFlow` 内建去重 | 无 | 极低 |
| **Hilt（KSP）** | 真实依赖图：DB + Net + Settings + 3 个 Service + 8 个 ViewModel | 手写容器 / `kotlin-inject`（可接受，但 Hilt 省更多样板） | 中（注解处理） |
| **Room** | 必须读写**现有 drift v3 schema**（11 表 + FTS）；Room 给迁移、`Flow`、编译期 SQL 校验 | 裸 `SQLiteDatabase`：迁移与 FTS 要自己写，反而更贵 | 低 |
| **androidx.sqlite `BundledSQLiteDriver`** | **保证 FTS5 + trigram 可用**（系统 SQLite 随 ROM 变，中文检索不能赌） | 自建 n-gram 列 + FTS4/LIKE（见 §12 双方案） | 低 |
| **OkHttp + kotlinx.serialization** | OkHttp 是 Android 事实标准（连接池/拦截器/WS）；kotlinx 零反射、体积小 | Ktor client（更重） | 低 |
| **DataStore(Preferences)** | 只放**本地设备级**状态（首次运行标记、上次 tab）；**用户设置仍留 DB `settings` 表**（要参与同步与跨端一致） | 自建文件（可行但不必要） | 低 |
| **AndroidX Security** | 存 `pairing_info`（token）—— 现在靠插件，需自行实现一次；**换实现会导致旧值读不出，必须一次性迁移**（读旧 → 写新 → 删旧） | Android Keystore + AES-GCM 自持文件（约 120 行，更可控） | 中 |
| **CameraX + ML Kit barcode** | 替换 `mobile_scanner`；扫码是配对页三模块之一 | 保留 `mobile_scanner`（继续吃 R8 keep 规则）或 ZXing-embedded（无 Play 依赖） | 中 |
| **Macrobenchmark + Baseline Profiles** | 冷启动与滚动是可量化目标（§13），没有它就只剩主观感觉 | 无（测量手段） | 低 |
| ~~自研 DI~~ / ~~多 module~~ / ~~KMP~~ / ~~Compose 动态功能~~ | — | — | **v2.0 明确不做** |

### 5.3 Flutter → Kotlin 逐功能迁移表

| 现功能 | 现实现 | Kotlin 方案 | 判定 |
|---|---|---|---|
| 三 tab + 8 个二级页 | `NavigationBar` + `IndexedStack` + `Navigator.push` | Compose `NavHost` + 底部 `NavigationBar`；结果页/合集页走类型安全路由参数 | 重写（等价） |
| 状态层 18 provider | Riverpod + ChangeNotifier | 每页 1 `ViewModel` + `StateFlow<UiState>`；连接/配对状态合并为 `HostConnectionViewModel`（单源） | **重新设计** |
| 本地库 11 表 + FTS | drift（core 共用） | Room：**逐字照搬 DDL**（含 `field_clocks_json`、`deleted_at`、FTS 虚表） | 迁移（可双向验证） |
| 同步引擎 | core（Lamport + LWW + 墓碑 + 离线队列） | Kotlin 重实现，用 Protocol 的 `conformance/vectors` 对拍 | 重实现（规范固定） |
| 网络层 | `ApiClient`(dio) + `SyncSocket` | `HostApi`(OkHttp) + `HostSocket`(OkHttp WS) | 重写（语义照搬） |
| 轮询兜底 1s | `HostStatusPoller`（指纹 + 界面签名 + 闩锁） | `HostStatusPoller`（`StateFlow` + `distinctUntilChanged`，判据改成「界面已显示才跳过」） | 重实现（**保留三条语义**） |
| 配对三模块 | 扫码 / 粘贴链接 / 手输 | CameraX + ML Kit / 粘贴 / 手输；**保持「点开始使用后才申请相机」时序** | 重写 |
| 截屏会话 | `CaptureService.kt`（复用 MediaProjection） | **整文件复用**（改成长驻 `Service` + `Binder` 或 `LifecycleService`） | 迁移 |
| 悬浮球 | 原生 View + `WindowManager` | `WindowManager` + `ComposeView`（**必须手设 `ViewTreeLifecycleOwner` / `SavedStateRegistryOwner` / `ViewModelStoreOwner`，否则崩**）或保留自定义 View 只换编排 | 迁移（换宿主） |
| 系统栏三层 | `SystemUiBridge` + Flutter 自绘条带 | 原生 Activity 直接 `WindowInsetsControllerCompat` + Compose `WindowInsets`；**自绘条带可删**（原生不存在引擎覆盖问题） | **简化** |
| 提示三套并存 | SnackBar + 原生 Toast + 队列策略 | 一套：`SnackbarHost`（前台）+ 通知（后台），「新顶旧、后台不入队」 | 简化 |
| 权限引导 | 4 个 `Settings.ACTION_*` + `isIgnoringBatteryOptimizations` | 同（直接复用） | 迁移 |
| 采集方式切换 | 进程内静态字段（**不落库**） | 落 DataStore/Room（**修掉这个 bug**） | 重写 |
| 离线队列 | `tasks` 表 + `payload_json` | Room 同结构 + 连接回调驱动（**不用 WorkManager**：队列语义已由协议定义，WorkManager 会引入第二套调度语义） | 迁移 |
| 安卓内嵌 Host | — | **不做**（它本来就是 Host 的客户端，改动最小） | 不适用 |

---

## 6. Android UI/UX 重构方案（QuizSync Design System）

**现状问题**：Flutter 的 Material 3 默认外观 + 手写设置行，「能用但不原生」；状态表达散落在三套提示里；无统一空/错/加载态。

**目标**：像原生 Android 应用，但**不复制 ImageToolbox 的视觉**。可借鉴它的：Compose 分层、`StateFlow` 单一状态、组件粒度、M3 动态取色、Baseline Profile、`Scaffold` + `TopAppBar` 滚动行为。不借鉴：工具箱式网格首页、它的品牌配色。

| 令牌 | 取值策略 |
|---|---|
| Color | M3 `ColorScheme`（种子 = 现契约的绿系 `#16A34A` 作 primary）；**标绿/标黄仍是语义色，不随主题变**（浅 `#16A34A`/底 `#DCFCE7`，深 `#4ADE80`/`#14351F`；黄浅 `#D97706`/`#FEF3C7`，深 `#FBBF24`/`#322712`）；**颜色不得是唯一信息通道**（配 ✓ 图标与文案） |
| Typography | M3 Type Scale；题目正文独立 `questionTextStyle`（字号 12–32、字重 300–700 可配） |
| Shape | 12 / 16 / 28dp 三档（卡片刻意比 M3 默认收敛） |
| Spacing | 4 的倍数；页面 16、分组 8（沿用现有「统一 8px 间距」决定） |
| Elevation | 尽量不用阴影，用 tonal surface + 1dp 描边 |
| Motion | 只保留三处：页面切换（共享轴）、结果到达（顶部横幅下滑）、悬浮球状态（颜色/角标）；**不做悬停放大**（M34 用户否决过） |
| Icon | Material Symbols（R8 与图标资源都要测；Flutter 的 tree-shake 坑不再存在） |

**组件（`ui/components`）**：`QuizCard`（题干+选项+标色+徽标）、`ResultSection` / `MaterialPanel`（折叠）、`AnswerRow`（✓ / 黄框）、`DeviceCard`、`SettingItem` / `SettingSwitch` / `SettingSlider` / `SettingDropdown`、`ActionCard`、`SectionHeader`、`EmptyState` / `LoadingState` / `ErrorState` / `OfflineBanner`、`PrimaryButton` / `SecondaryButton`、`ConfirmDialog`、`PermissionGuideCard`、`ProgressStrip`（多页 N/6）、`ConnectingIndicator`。

**每个页面必须齐全的状态**：`Loading`（骨架屏，不用菊花）、`Empty`（历史为空给「去电脑端选合集」引导）、`Error`（可重试 + 错误码人话映射）、`Connecting / Connected / Disconnected`（状态行 + 一键重连）、`Recognizing`（进度 + 第几页）、`Done`、`Revoked / Unpaired`（强制回配对页）。

**导航**：底部三 tab 保留（用户习惯 + `SPEC.md §3.4` 契约）；二级页 `NavHost` push；未配对时配对页是唯一入口 —— **保留「不 pop 唯一路由」的教训**：用 `NavHost` 的 `startDestination` 切换，而不是手写栈。

**性能手法**：`LazyColumn` + 稳定 key（`sessionId`）+ `derivedStateOf`；题目卡片避免大对象进 `remember` 键；Baseline Profile 覆盖「冷启动 → 当前任务」与「打开结果页」两条路径。

---

## 7. Windows C#/.NET 重构方案

### 7.1 技术选型（逐条给理由与替代）

| 项 | 选择 | 理由 / 替代与代价 |
|---|---|---|
| .NET | **.NET 10 LTS**（若本机工具链未就绪则退回 .NET 8 LTS，二者不混用） | LTS 覆盖期长；.NET Framework 4.8 无 WinUI 3 |
| UI | **WinUI 3 + Windows App SDK**，**unpackaged + self-contained** 部署 | 保持「便携 zip + Inno Setup」两种交付；代价：包体积上升，仍需带 VC++ 运行库（现有 10 DLL 逻辑继续用） |
| 截屏 | **GDI BitBlt 为主 + Windows.Graphics.Capture 兜底** | GDI 同步、无 D3D 初始化、行为已被 49 轮验证；WGC 需要 D3D 设备 + `IGraphicsCaptureItemInterop`，只用于「GDI 取到黑图」的机器；**先有基线测量再考虑换主路径** |
| 热键 | `RegisterHotKey` + **一条专用消息线程**（`GetMessage` 循环） | 不再需要「不 await + 共享内存 + 2s 自检」那套 Dart 补偿；**保留周期自检、显式注销、触发时判定吞掉三条结论** |
| 托盘 | **H.NotifyIcon.WinUI**（或约 150 行 `Shell_NotifyIcon` P/Invoke） | WinUI 3 无内置托盘；勾选用 `ToggleMenuFlyoutItem` |
| 窗口/DPI | Win32（`SetWindowLong`、`DwmSetWindowAttribute`、`AppWindow`） | 保留现有 DWM 标题栏方案；配色与 XAML 主题对齐 |
| 数据库 | **Microsoft.Data.Sqlite + SQLitePCLRaw(e_sqlite3，含 FTS5)** + 手写迁移 SQL | 必须**字节级兼容现有 drift v3 库**；EF Core 的代价：模型/迁移要镜像同一套 schema，且 FTS5 虚表与 `field_clocks_json` 这类半结构化列收益为负 |
| 序列化 | `System.Text.Json`（source-generated） | 零依赖、AOT 友好 |
| HTTP/WS | `HttpClient` + `ClientWebSocket` | 内建；上传用 `StreamContent` 实现边读边限长 |
| 密钥 | **DPAPI（`ProtectedData`，CurrentUser）+ 可选 Credential Manager** | 与现有 `secure.bin` 语义一致，可平滑迁移 |
| 悬浮球位图 | 编译期烘焙 + 运行时 `System.Drawing.Common`（仅 Windows）或 SkiaSharp | 球体只有「状态 × 尺寸 × 描边」有限组合；**位图缓存与预乘 BGRA 的思路保留**，代码量从 3,600 行降到几百行 |
| 日志 | `Microsoft.Extensions.Logging` + 滚动文件（2MB 截断，沿用现状） | — |
| 明确不做 | MAUI、Avalonia、WPF、EF Core、MediatR/AutoMapper 一类模板层、多进程 UI | 见「不过度工程化」原则 |

### 7.2 项目结构

```
windows/
├─ QuizSync.App/            WinUI 3 外壳：主窗口、悬浮窗、托盘、设置 10 分类、结果与历史 UI
├─ QuizSync.Provider/       Provider 角色：截屏、热键、剪贴板、AI 客户端、解析与标色、--headless
├─ QuizSync.Data/           本地库（SQLite）、迁移、Repository、**LWW applier**、op 生成
├─ QuizSync.Core/           语言无关核心的 C# 移植：Question 模型、prompt、解析、highlight、图片处理
├─ QuizSync.ServerBridge/   内嵌 / 连接 Server 的适配层（本地控制令牌发现、命名互斥体、探活）
└─ QuizSync.Tests/          xUnit
```

### 7.3 Flutter → C# 对照迁移表

| 现实现（文件） | 行数量级 | C# 方案 | 判定 |
|---|---|---|---|
| `app.dart` + `main.dart` 外壳、`UiScale` 虚拟画布 | ~1,400 | WinUI `Window` + `NavigationView`；缩放改为真正的 XAML 缩放/字号档位（**不再需要 `OverflowBox` 虚拟画布** —— 那是 Flutter 紧约束的产物） | 重写（简化） |
| 设置页 10 分类 + 16 个设置组件 | ~2,500 | `NavigationView`(左) + `ScrollViewer`(右) + CommunityToolkit `SettingsCard` / `SettingsExpander`；**配色/间距/卡片样式沿用现状**（保守优先约束） | 重写（组件换官方） |
| 悬浮窗 6 文件 | ~2,900 | **独立 WinUI 无边框窗口**：`WS_EX_TOOLWINDOW|NOACTIVATE|TOPMOST` + `SystemBackdrop=Acrylic` + 圆角 + 真实 XAML 内容（滚动用 `ScrollViewer`，命中用 XAML 命中测试）；整窗透明度用 `SetLayeredWindowAttributes`（**逐像素透明不需要**） | **重写（删除切片/指纹/合成缓存全套）** |
| 悬浮球 | ~700 | 小型无边框窗口 + 圆形位图（**保留分层窗口**，因为需要逐像素 alpha 与 `WS_EX_NOACTIVATE`）+ 手势判定移植（松手结算、8px / 逻辑像素阈值、拖动取消长按、贴边吸附） | 重写（保留手势语义） |
| 热键 456 + 425 | ~880 | 专用消息线程 + `RegisterHotKey`；`gatedTrigger` 语义移植（页面挂载 ∧ 本窗口前台才吞掉）；`HotkeyStatus` 移植 | 重写（简化） |
| GDI 截屏 + 藏/恢复窗口 | ~380 | 直接 P/Invoke 移植；**「藏前记前台与 Z 序、恢复用 `SWP_NOACTIVATE` 插回原位、只有本来前台才还焦点」逐字保留** | 迁移 |
| 剪贴板 DIB | ~330 | 直接移植（`CF_DIB` → `CF_DIBV5`、掩码取通道、头自算字节、`biHeight` 符号扩展） | 迁移 |
| 托盘 | ~120 | `H.NotifyIcon` + `MenuFlyout`（勾选用 `ToggleMenuFlyoutItem`） | 重写 |
| DPAPI 安全存储 | ~120 | `ProtectedData` + `secure.bin` 同格式（**可读旧文件**） | 迁移 |
| 主窗口题目/结果 UI + `MathText` | ~3,000 | `ItemsRepeater` / `ListView` + 自绘文本；公式见下 | 重写 |
| 本地库与同步（core） | ~4,000 | `Microsoft.Data.Sqlite` + 手写 Repository + **LWW applier**（接收 Android 的 ops） | 重实现（规范固定） |
| AI 客户端 | ~900 | `HttpClient` + `System.Text.Json`；三份 provider 请求构造逐字照搬 | 迁移 |
| 服务端 | ~2,500 | **整块删除**，改用 `QuizSync.Server.Core` | 删除 |

**公式与富文本（最大单点风险）**：现在两端共用 `MathText`（`flutter_math_fork` + 四种定界符 + 化学式上下标 + Markdown / 空格对齐表）。三条路：① **WebView2 + KaTeX**（离线打包，推荐）；② Win2D 自绘公式（不建议，等于重写一个 TeX 子集）；③ 降级纯文本（**不可接受**，M40/M46 用户已明确要求公式与化学式正确）。**P1 spike 的验收 = 现有 `content_render_test` 的 19 条用例逐条在 WebView2 里得到等价视觉结果**（含 `⇌`、`e⁻`、空格对齐表、材料折叠）。

---

## 8. QuizSyncServer 方案

| 项 | 方案 |
|---|---|
| 技术栈 | C# / .NET 10；HTTP/WS 用 **Kestrel 自宿主 ASP.NET Core**（成熟的 WebSocket、限流、超时、日志；比自写 `HttpListener` 划算） |
| 形态 | 前台 CLI + `--hidden` 后台；**不做 Windows 服务**（理由见下） |
| 数据 | `<程序目录>\QuizSyncServer\`：`config.json`、`state.json`（设备/令牌/任务/水位）、`images\<sha256>.jpg`、`logs\`、`runtime.json`（pid/port/control_token）。**不引入 SQLite**（ops 对它不透明，只需按 lamport 顺序追加与游标读取；若体量成为问题再单独立项） |
| CLI 命令 | `setup`（首次向导：端口、随登录启动、防火墙提示）、`run`、`start` / `stop` / `status`、`pair`（配对码 + 二维码 + `quizsync://pair?...` 链接 + 有效期）、`devices list\|revoke <id>\|rename`、`provider status\|ping`、`config get\|set\|list`、`doctor`（端口/防火墙/版本/连通性/时钟偏移/**Provider 是否在线**）、`log tail [--follow]`、`version`、`help`。全局 `--json` |
| **明确去掉** | `api`（AI Key 归 Provider，Server 不该有 Key）、任何 UI、任何 AI/截屏/热键子命令 |
| 配对 | 与协议一致：6 位码 5 分钟、5 次/分、10 次失败锁 60 秒（计数落库）、`already_paired` 409 返新 token、`role=host` 专属操作 |
| **不做 Windows 服务** | ① 现有密钥是 DPAPI **CurrentUser**，服务账户解不开；② 服务无法把配对码/二维码展示给用户，配对会变成「改配置文件」；③ 防火墙与权限提示在安装期更友好。改为**随用户登录启动**（`setup` 安装/移除启动项）；客户端的默认形态是**自己内嵌宿主**（第 167 轮），独立进程只在显式选择时用 |
| 资源目标 | 空闲 RSS ≤40MB、稳态 CPU ≤0.5%、句柄平稳、启动 ≤200ms（测量方法见 §13） |
| 升级策略 | Server 只随**协议版本**升级；`doctor` 输出协议版本 / 构建 / 数据目录；升级前自动备份 `state.json`；**不自动更新** |
| 与客户端的关系 | 客户端启动先探活（`runtime.json` + `GET /api/v2/info`）：有 Server → 当客户端；没有 → **默认内嵌 `Server.Core` 起进程内宿主**（第 167 轮落地，用户拍板「一个应用」）；要「关掉客户端主机仍在」就走独立进程。互斥靠**探活 + 端口所有权**，命名互斥体运行时不参与（见 §2.2 的偏差说明） |
| 旧数据迁移 | 从 Dart 版 `server/` 的 `config.json` / `state.json` / `images` 一次性迁移（`doctor` 检测旧目录并提示） |

---

## 9. Git 分支策略

### 9.1 现状（已核实）

`QuizSyncAI` 远端 = `main`（默认，`c07636d`）+ `flutter`（= `fe13115a`，已锁定）；`native` 已删除；标签 `1.1.0` / `1.0.0` / `server-1.0.x` 完好；保护规则 `protect-main`（禁强推/删除）与 `freeze-flutter`（完全锁定）已生效；内部全历史库 `D:\ZCode\QuizSync_AI`（`master`，88 提交，含测试与内部文档）**未推送**。

### 9.2 分支模型

| 仓库 | 分支 | 说明 |
|---|---|---|
| **QuizSyncAI** | `main`（默认，**逐模块替换的主线**）、`flutter`（冻结的 1.x）、`feat/android-*`、`feat/windows-*`、`feat/protocol-bridge`、`chore/*` | **不移动任何 Flutter 目录**：Flutter 保持 `apps/`、`packages/`、`server/` 原地，新代码进新顶层目录 `windows/`、`android/`。这样每个阶段 Flutter 版仍可构建（回归基线）；2.0 发布后再整体删除 Flutter 目录（历史在 `flutter` 分支与 tag 里） |
| | tag：`v1.1.0-flutter`（打在当前主线，标记「最后一版 Flutter 全量源码」）、里程碑 `v2.0.0-m1`…、发布 `v2.0.0` | 旧版永远可恢复的三条腿：**tag + 冻结分支 + Release 资产**（1.1.0 安装包/APK 永久保留） |
| | 保护：`main` 禁强推/删除（已有）；`flutter` 完全锁定（已有）；新增 `legacy/*` 同 `flutter` | 出 1.1.x 补丁时才临时解冻 `flutter` |
| **QuizSyncProtocol** | `main`（= 已发布规范）、`spec/v2-draft`、`spec/v3-draft`；tag `v1.0.0`（现状反推快照）、`v2.0.0`（冻结） | 协议主版本冻结 = tag；冻结后只允许追加 |
| **QuizSyncServer** | `main`；tag `2.0.0` 起 | 与客户端 2.0 对齐；**不复用 1.x 版本号**（避免与 `server-1.0.x` 混淆） |
| **内部全历史库** | 保持本地真源（含测试与内部文档），**继续不推送**；建议加一个私有远端做异地备份 | 公开库继续做「裁剪发布镜像」（现有 `package_source.ps1` 口径）。**公开库的每次提交都走裁剪导出流程**，禁止把 `master` 直推（`AGENTS.md` 明确「禁止 `git push master`」；往公开库补目录用临时索引：`GIT_INDEX_FILE` + `read-tree origin/main` + `commit-tree`） |

### 9.3 Phase 0 的 Git 动作

1. `git tag -a v1.1.0-flutter -m "Flutter 1.1.0（Legacy 起点）"` 并推送。
2. 建两个新仓库（名字已确认未被占用）+ README 写清「职责 / 绝不放入什么 / 依赖方向 / 版本规则」。
3. 四份契约**复制**到 Protocol 仓库（原文件先不动），并**剥离实现绑定**（shelf / drift / `packages/quizsync_ui` / Flutter 包名）。
4. 三仓各加 `CONTRIBUTING`（依赖方向、提交规范、版本号唯一来源、发布红线）。

---

## 10. 分阶段迁移路线图

> 每阶段都必须**可编译、可运行、可回滚**，不允许 Big Bang。粗估为「单人 + AI 辅助」，仅供排序参考。

### Phase 0 · 基线冻结与决策（3–5 天）

> **进度（2026-09-26）**：✅ 完成 —— 基线冻结、3 篇 ADR、三仓建立；`versions/v1-snapshot.md` 记录 16 处分叉 + N1–N7。
- **目标**：把事实固定下来，避免反复考古。
- **内容**：① tag `v1.1.0-flutter` + 两个新仓库骨架；② 四份契约复制并剥离实现绑定；③ 三份 ADR 合入（见 `docs/adr/`）；④ 修复 §1.3 技术债**第 1 条（唯一 P0）**：轮询路径覆盖用户手改；⑤ **准备一台安卓真机**（当前无设备无模拟器 = 最大验证盲区）；⑥ **验证债**：用真 Key 跑一次端到端 AI、在干净机器/虚拟机上验证便携包（VC 运行库从未在干净机验证过）。
- **依赖**：无。**完成条件**：tag 存在、两仓库存在、3 份 ADR 合入、P0 修复有红→绿用例、`adb devices` 非空、AI 端到端跑通一次。
- **测试方式**：现有 670 项全绿（回归基线）；P0 修复做负向对照（退回旧实现必红）。
- **风险**：无（不改架构）。**回滚**：删 tag / 仓库即可。

### Phase 1 · Protocol v1 快照 + v2 规范冻结（2–3 周）

> **进度（2026-09-26）**：✅ **完成** —— `spec/` 11 篇 + `schema/`（11 表/113 列，与真实库逐列核对）+ `examples/` 三份 + **6 组 120 步一致性向量** + 兼容矩阵 + CHANGELOG；**Dart 参考回放器全绿**（`packages/quizsync_core/test/conformance/`，`--tags conformance` 6 条）；C# 回放器同样 120 步全绿。
> **更正（2026-09-27，第 141 轮）**：**Phase 1 并未完成** —— 标题里的「**v2 规范冻结**」没做。
> 协议仓 `versions/` 只有 `v1-input`，`spec/` 里 `recognition_job` / `recognition_result` /
> `provider_online` **一个字都没有**：这三个帧只活在 `QuizSyncServer`（C#）与
> `QuizSync.Provider`（C#）的实现里。本轮按「从 Phase 1 推进到 Phase 7 全部完成」的范围补它。
- **目标**：让三端能照着同一份纸实现。
- **内容**：① `versions/v1-snapshot.md` = **按实现反推**的现状规范（修掉 3 处文档-实现分叉，把 `/tasks/active`、`ops_lamport`、`already_paired 409`、合集删除不对称等只存在于代码与注释里的语义写进规范）；② `spec/` 11 个文件 + `schema/` + `examples/`；③ **`conformance/vectors`**（从 `core/test` 与 `loopback_test` 的 9 步导出）；④ 兼容矩阵（v1 客户端 × v2 服务端等 4 种组合的期望行为）。
- **依赖**：Phase 0 的 ADR。**完成条件**：向量可被一个 **Dart 参考回放器**（用现有 `quizsync_core` 实现跑）100% 通过 —— 证明规范描述的就是现有行为。
- **测试方式**：Dart 回放器（只做 I/O 适配，不写新逻辑）+ JSON Schema 校验全部 examples。
- **风险**：规范写宽了后面各端自由发挥 → 用向量兜住。**回滚**：规范仓库独立，随时改 draft。

### Phase 2 · Server v2（C#）可用（3–4 周）

> **进度（2026-09-26）**：🟡 **v1 兼容层完成** —— 向量 120 步全绿（C# 回放器打真 HTTP + 真 WS）；CLI 全命令集 + 控制面（回环 + 控制令牌）；`/api/v2/sync/ops` 单一游标；**路由级负向鉴权覆盖**（枚举路由逐条匿名打，防 v2 那类漏配）；gzip（大响应已验证）。**剩**：`setup` 向导；小响应是否压缩未查清（`docs/DECISIONS.md` 有记录）。测试 51 条。
> **补充（2026-09-27，第 141 轮）**：测试已到 **74 条**，识别真派发给 Provider。**仍未完成的两项**：
> ① `setup` 向导目前只做三件事（确保数据目录 / 写端口与绑定地址 / 初始化库，`Program.cs:184-250`），
> **「随登录启动」与防火墙提示都没做** —— 这正是「界面不会自动拉起主机」的根因；
> ② ~~「小响应是否压缩」未查清~~ **已结案（第 144 轮实测）**：小响应**也**被 gzip 压、且**没有阈值**；
> 源码注释里那句「超过阈值」不成立（取证表见 `DECISIONS.md` 第 144 轮）。
- **目标**：新 Server 能替代现有 Dart Server，并让 **Flutter 1.1.0 手机端照常工作**。
- **内容**：① Kestrel 宿主 + `/api/v2/*` + **`/api/v1/*` 兼容层** + `/ws`；② 配对/设备/令牌/角色/限流/锁定；③ Blob 存储（流式限长 2MB、hash 命名、hex 白名单、下载）；④ 任务队列（幂等、同 hash 同页序复用）+ `recognition_job` 派发（含 `provider_offline`）；⑤ op 中继与水位、快照分页（NDJSON + gzip）；⑥ CLI 命令集 + `setup` 向导 + 随登录启动；⑦ 日志与 `doctor`。
- **依赖**：Phase 1 向量。**完成条件**：向量 100% 通过；**Flutter 1.1.0 客户端（或对等的 core 测试客户端）完成「配对 → 上传 → 任务 → 收结果 → 同步」全链路**；`loopback_test` 的 9 步在新 Server 上重跑通过。
- **测试方式**：向量回放 + `dotnet test` 集成测试（起真 Server）+ 旧客户端手工跑一遍。
- **风险**：v1 兼容层语义偏差（丢字段、状态码不一致）→ 以现有 Flutter 客户端为裁判逐条对。**回滚**：Server 独立进程，退回 Dart 版即可（不能同端口共存）。

### Phase 3 · Android 内核（数据 + 网络 + 服务，无新 UI）（4–6 周）

> **进度（2026-09-26）**：🟡 **数据、网络、旧库兼容都跑通**（真机 `23046RP50C` / Android 15 已连；原生外壳已装上并跑通，协议 schema 打包进 APK assets 后真机内核自检通过） —— 协议 schema 逐字执行 + FTS5 trigram 实测可用；Kotlin 同步引擎与向量对拍；OkHttp `HostApi` + **跨实现 E2E**（打真实 C# 服务端：配对→上传去重→202→轮询到 done→幂等复提→冒充 op 被拒）；**旧库兼容 4 条**（Dart 生成真 drift v3 样本，Kotlin 打开读出内容并继续写入）。**剩**：Room 实体映射与迁移/自愈、真机 `androidTest`、Baseline Profile。测试 11 条。
> **补充（2026-09-27，第 141 轮）**：`android/core` 模块已扩到含 `SyncVectorParityTest`
> （向量对拍，要求 `QS_PROTOCOL_DIR`）与 `HostApiEndToEndTest`（**起真 C# 服务端**，要求
> `QS_SERVER_DLL`，找不到就**直接失败**）—— 两者本轮都进了 CI（新 job `android-core`）。
> **完成条件里仍未满足的两项**：**真机 `androidTest` 不存在**（`android/app/src` 下只有 `test/`）、
> **Baseline Profile 从未采集**。另有一处**计划与实现的偏差**要记：内核没有用 Room，
> 走的是 `androidx.sqlite` 的 `BundledSQLiteDriver` + 手写 DDL（`QuizSyncDatabase`），
> FTS5 那套 `§12 双方案`因此不适用 —— 立项理由（自带 SQLite、含 FTS5 trigram）已满足。
> **补充（2026-09-28，第 166 轮）：完成条件里那两项其实在 2026-09-27 就落地了**（上一轮的口头汇报
> 说"没做"，是错的）：`android/app/src/androidTest/` 下两个类共 **9 条**用例（`AppSmokeInstrumentedTest`
> 4 条 + `QuizRouteStackInstrumentedTest` 5 条，走 Compose 语义树而不是坐标），当天真机结果
> `TEST-23046RP50C - 15-_app-.xml` = `tests=1 failures=0`；`app/src/main/baselineProfiles/baseline-prof.txt`
> **1.6 MB**（2026-09-27 15:08 采集，真实 profile 行而不是占位）。
> 第 166 轮重跑的**第一次是红的**：`主壳三个标签都在` 挂 19 分 47 秒后
> 「Instrumentation run failed due to Process crashed」，根因是**设备内存压力**
> （UTP 抓的 logcat 里 `lowmemorykiller` 连续 `reason: cw`；设备 `free -m` = 7536 MB 里用掉 7254），
> 不是产品缺陷。处置与复跑结果见 `docs/progress.md` 第 166 轮。
> **两条设备侧限制（都已固化进脚本与文档）**：
> ① MIUI 对 **streamed install** 直接回 `INSTALL_FAILED_USER_RESTRICTED: Install canceled by user`，
>    而 `adb install --no-streaming`（push 安装路径）能过 —— 所以 `gradle connectedAndroidTest`
>    在这台机器上**必红**，且报错看着像"用户点了取消"。可复跑入口：
>    `tools/verify_android_instrumented.ps1`（只构建 → `--no-streaming` 装 → `am instrument` 直跑）。
> ② 这台平板 7536 MB 内存被占到 **96%**，被测进程起来就被 `lowmemorykiller` 收走 ——
>    表现为「`No compose hierarchies found in the app`（MainActivity 没起来）」或
>    「Process crashed」。**要复跑先腾内存或重启平板。**
- **目标**：先让「看不见的部分」跑通并与 Flutter 版数据互认。
- **内容**：① Room schema 逐字照搬 + 迁移/自愈；② Kotlin 同步引擎用向量对拍；③ OkHttp `HostApi` / `HostSocket` + 轮询兜底；④ `CaptureService` 复用 + 采集方式落库；⑤ AndroidX Security 自持实现 + **一次性 token 迁移**；⑥ 旧库兼容回归（Flutter 1.1.0 写的库 → 新 App 打开 → 数据完整 → 写回 → Flutter 版仍能打开）。
- **依赖**：Phase 1（向量）、Phase 2（有真 Server 可打）。**完成条件**：向量全绿；`androidTest`（真机）完成配对/上传/收结果；旧库双向回归通过；**Baseline Profile 首次采集**。
- **测试方式**：单元 + 仪器测试 + 真机人工清单。**风险**：Room + FTS5（见 §12 双方案）。**回滚**：新 App 与旧 APK 包名相同不能共存 → 灰度期用内部测试机，正式发布才替换。

### Phase 4 · Android UI/UX + 设计系统（4–6 周，可与 Phase 5 并行）

> **进度（2026-09-26）**：🟡 **已开工** —— 真机已连（`23046RP50C` / Android 15），`android/app`（Kotlin + Compose）已构建、安装、启动并**与在用的 1.2.1 并存**（applicationId 带 `.dev` 后缀，不覆盖用户能用的版本）；协议 schema 随 APK 走（assets），真机内核自检通过。**剩**：设计系统与各页面逐块替换 Flutter 界面，以及**观感逐条验收**（后者必须人看着屏幕比对，自动化只能证明「渲染出来了、文字对、不裁切」）。
> **补充（第五轮）**：设计 token 已从「各自顺手取值」改为**唯一来源**（`design/tokens.json` + `tools/gen_design_tokens.py` 生成两端产物，CI 跑 `--check`），且 token 的值被 `packages/quizsync_ui/test/design_token_parity_test.dart` 钉在**老应用 1.1.0 的真实视觉契约**上（品牌绿 `#16A34A` / 按钮圆角 `AppRadius.control=10` / 标签胶囊 `AppRadius.chip=999` / 页面底色 `canvas=#F3F5F4`）。**但用户看得见的页面仍是 0% 已迁移** —— 当前任务页、结果页（题目卡片）、历史、合集、设置 7 页、悬浮球全部未搬。下一轮起按**用户旅程**推进，不按平台：配对页 → 截屏搜题 → 题目卡片 → 历史。
> **补充（2026-09-27，第 141 轮）**：上面那句「**用户看得见的页面仍是 0% 已迁移**」**已经作废** ——
> 当前任务页、结果页（含 `MathText` 公式/化学式/表格）、历史页、合集详情、设置页、识别模块
> 二级页、悬浮球都已搬完，设计系统 P1–P5 主体落地（见 `docs/design-system-plan.md §4`）。
> **仍未做**：**配对页按规格 §2.1 重做**（`PairScreen.kt` 178 行，7 个 VM 成员未接 —— 本轮进行中）、
> **TalkBack 与深色主题检查表**（两份都不存在）、与 Flutter 版逐页对照的**功能对齐清单**。
- **完成条件**：功能对齐清单逐条打勾（与 Flutter 版逐页对照）+ 冷启动/滚动达到 §13 目标 + TalkBack 与深色主题检查表。
- **测试方式**：Compose UI 测试（对齐现有 88 条 android 测试的语义）+ 真机人工清单（沿用三段式结构，数量按 native 功能面重新生成）。
- **风险**：手势与悬浮球观感只能真机验证 → 必须真机 + 用户验收。**回滚**：Phase 3 的测试壳仍可跑。

### Phase 5 · Windows 内核（数据 + Provider 能力，无新 UI）（4–6 周，与 Phase 4 并行）

> **进度（2026-09-26）**：🟡 **除托盘外全部落地** —— `QuizSync.Data`（op 生成只带改动字段、逐字段 LWW、墓碑、Lamport 不变式、图片剪枝、分析缓存）；`QuizSync.Core`（prompt **哈希与 Dart 逐字对齐**、`Question.FromAiJson`、三步容错解析、标色、三家 provider、重试、配额）；`QuizSync.Provider`（识别编排引擎、GDI 截屏、藏窗口/恢复、剪贴板 DIB、全局热键）；`ServerBridge`（探活/单实例/客户端 + **打真实 C# 服务端 E2E**）；`Provider.Cli` 无界面 `doctor`。**剩**：托盘（`H.NotifyIcon`）（托盘后来已落地）。测试 **137 条**（PLAN 目标是 ≥120 条等价用例 —— 已超过）。
>
> **补（第 167 轮）**：`ServerBridge` 不再只有客户端 —— 新增 `EmbeddedHost`（本进程内起
> `QuizSyncHost` + Kestrel + `ProviderAnalyzer`），工程直接引兄弟仓的 `QuizSync.Server.Core`。
> 这就是 §7.2 说的「连接/**内嵌** Server 的适配层」那一半，迟到了 26 轮。
> **补充（2026-09-27，第 141 轮）**：测试已到 **215 条**；托盘也早已落地（`TrayController`，
> 13 项，`verify_windows_ui.ps1` 里有 4 条托盘断言）。
> **完成条件里仍差一条取证**：与 Android 的「手机截屏 → 电脑 AI → 两端显示」目前只有
> **相册选图**那一版真机取证（`docs/UI-PROGRESS.md §6.2`，6 PASS）；
> **悬浮球截屏**那条采集链是后来才接上的（`ab094c3`），没有独立的真机取证 —— 本轮补。
> **补充（2026-09-28，第 166 轮）**：内容③里**剪贴板的「自动识别」原来是空的** ——
> 设置项「剪贴板监听」只存值、没有任何监听线程（设置页自己写着「还没接上」）。
> 本轮接通：`AddClipboardFormatListener` + `WM_CLIPBOARDUPDATE`（不是 1.1.0 的 700 ms 轮询），
> 判据抽成可单测的纯函数 `ClipboardWatchPolicy`，静默、不抢焦点；验收见
> `tools/verify_clipboard_watch.ps1`（4 条判据 + 1 条负向对照，5 PASS）。
- **目标**：C# 侧能「截屏 → AI → 出结果 → 落库 → 与 Android 同步」，先不追求新界面。
- **内容**：① `QuizSync.Data`（SQLite + 迁移 + **LWW applier** + op 生成）；② AI 客户端 + 解析 + highlight 移植（对着 `core` 的 212 项测试逐条补 C# 测试）；③ 截屏（GDI + 藏/恢复）+ 剪贴板 + 热键 + 托盘；④ ServerBridge（探活/内嵌/本地令牌/命名互斥体）；⑤ 图片处理与缓存剪枝；⑥ `--headless` 模式。
- **完成条件**：C# 侧核心测试覆盖 `core` 的关键用例（目标 ≥120 条等价用例）；与 Android 完成一次真实「手机截屏 → 电脑 AI → 两端显示」。
- **风险**：C# 与 Kotlin 的 LWW 实现漂移 → 全部以向量为裁判。**回滚**：Flutter 版不受影响（数据目录先**读**不写，或用副本库）。

### Phase 6 · Windows UI + 悬浮窗/球（6–8 周）

> **进度（2026-09-26）**：🟡 **阻塞解除** —— 原以为要装「Windows App SDK workload」，实测 `Microsoft.WindowsAppSDK` 只是 NuGet 包（2.5.1，本机 nuget.org 可拉），配合已装的 VS 生成工具 + Windows SDK 10.0.26100 即可编。**剩**：起最小 WinUI 3 外壳 → 逐块替换 Flutter 界面；观感验收同 Phase 4，需要人看。
> **补充（第五轮）**：WinUI 3 外壳已起（`Microsoft.WindowsAppSDK` + XAML 编成 `.xbf`，窗口类名 `WinUIDesktopWin32WindowClass`），首屏改成产品真实结构（页面底色 `canvas #F3F5F4` + 白卡片圆角 16 + `cardStroke` 装饰描边 + 品牌绿按钮），不再是「整窗铺白 + 居中一堆文字」（用户当场质问「这真的是 WinUI 3 吗，怎么不像」——技术上是，但那样写读起来像 HTML 表单，批评成立）。**未做 Mica**：会让两端底色不一致，与「双端同源」目标冲突，列为后续可选项。**用户看得见的页面仍是 0% 已迁移** —— NavigationView 10 分类设置、题目卡片 + WebView2/KaTeX、悬浮窗、悬浮球、托盘全部未搬。（原记录：完成条件是「悬浮窗观感与 1.1.0 逐条一致」，需要 **Windows App SDK（WinUI 3）workload** 才能编译，且验收要人在屏幕前逐条比对。本机未安装该 workload，如实记录于此。
> **补充（2026-09-27，第 141 轮）**：上面那句「**用户看得见的页面仍是 0% 已迁移**」**已经作废** ——
> 10 分类 `NavigationView` + `SettingsCard` 设置页、题目卡片、悬浮球、托盘、多页暂存、搜索、
> 合集级导出都已搬完并过 `verify_windows_ui.ps1`（PASS 56 / FAIL 0）。**仍差的是第③项：悬浮窗**
> （规格 §6.1–6.5，12 项设置，全未开工 —— 这是 2.0 剩下最大的一块，本轮做）。
> 另一处**计划与实现的偏差要固化**：第②项的「WebView2 + KaTeX」**没有采用** ——
> 本机没有 WebView2 运行时，公式走自研 `Theme/MathTextBlock.cs` + `MathPieces.cs`
> （离线、无额外依赖）。理由与取舍已写在 `MathPieces.cs` 文件头，本轮补进 `DECISIONS.md`。
> **补充（2026-09-28，第 166 轮）**：上一条里那句「**仍差的是第③项：悬浮窗**」**已经作废** ——
> 悬浮窗做完了，并且过了**运行时探针**（`tools/probe_float_window.ps1` → **7 PASS / 0 FAIL**：
> 按 `float_aspect`/`float_scale` 算出 342×760 逻辑像素 @DPI192、置顶、不进 Alt+Tab/任务栏、
> 默认在屏幕右侧、重启后位置回读）。界面验收 `verify_windows_ui.ps1` 同期 **82 PASS / 0 FAIL**
> （含悬浮窗 12 项设置逐项在树里、10 分类内容区等宽、托盘两项 switch）。
> 同轮还修掉两处**假声明**：①「开启连接设备」总开关原来什么都不做（关着也照样能连、能配、
> 能吊销设备），本轮按 `SPEC.md` §2.2 接线（关 → 只留开关 + 「当前不可用」；开 → 真的用
> `HostLauncher` 拉主机）；②「界面缩放」是个**没有任何提示的哑开关**，现在界面上如实写明它没接
> （WinUI 没有 Flutter 那套虚拟画布）。
- **内容**：① `NavigationView` + CommunityToolkit `SettingsCard` 的 10 分类（**沿用现有配色/间距**）；② 主窗口题目卡片 + **WebView2 + KaTeX**；③ 悬浮窗 = 真实 XAML 窗口（12 项设置全部按契约）；④ 悬浮球（分层窗口，仅球）；⑤ 托盘菜单两项 checkbox。
- **完成条件**：逐条对照 `SPEC.md §2.2/§2.4/§2.5` 验收；**悬浮窗观感与 1.1.0 逐条一致**（保守优先约束）；旧 `float_window_*` 无残留引用。
- **风险**：**WinUI 3 无边框窗口的透明/置顶/不抢焦点**（spike 先行，见 §14 H3）。**回滚**：保留 Flutter 版安装包，用户可退回。

### Phase 7 · 集成、兼容与性能（3–4 周）

> **进度（2026-09-27，第 141 轮）**：🟡 **开工**。范围按用户口径定为「Phase 1 一路推进到 Phase 7 全部完成」。
>
> **进度（2026-09-28，第 167 轮）：✅ 完成条件全部拿到。**
> ① 矩阵 4 组通过；② 性能表逐项有数（达标 / 写明偏差原因 / N/A 写明为什么没有对等物）；
> ③ 第④项「内嵌 vs 独立」按用户第 167 轮的口径定型为**内嵌为默认、独立为显式选项**，
> 证据是 `tools\verify_embedded_host.ps1`（12 PASS / 0 FAIL，含「真的起 Provider 子进程连内嵌宿主」与负向对照）。
> **仍然红的只有一件**：真机 `androidTest`（第 166 轮记录，根因在设备侧：内存压力 + MIUI 限制），
> Phase 3 的完成条件因此仍挂着 —— 那不是 Phase 7 的内容。
> 四项的入口都已侦察清楚，并有**三处口径纠正**（都推翻了旧假设）：
> ① **组合矩阵**：两个宿主都在（Dart v1 在 `server/`、C# v2 在 `QuizSyncServer`），
> 但**客户端侧没有可复用的 E2E 驱动**，要新写；矩阵必须**先跑完再删 Flutter**（用户已确认）。
> ② **旧备份兼容**：1.1.0 的真实格式拿到了（`packages/quizsync_core/lib/util/backup.dart`：
> zip 内是 `quizsync.db` / `-wal` / `-shm` + `images/**`，**确实没有 manifest**，另有
> zip-slip 防护、无库时不覆盖现有数据、被占用时 `.restore-pending` 下次启动换入等语义）；
> 而 **Windows 2.0 端的备份/恢复根本没实现**（设置页「数据管理」里是 `NotYet`）——
> 这一项不是「兼容」而是**从零实现 + 兼容旧格式**。
> ③ **内嵌 vs 独立**：`windows/QuizSync.ServerBridge` **只有客户端**（`HostClient` / `HostDiscovery`），
> 没有内嵌宿主；D1 已把主机独立成仓、独立进程。所以第④项的口径改为
> 「**独立模式 + 客户端一键拉起主机进程**」，并把「启动到可用」的时延记进 §13。
> **⟶ 这条改口径在第 167 轮被用户否掉并作废**：`ServerBridge` 补上 `EmbeddedHost`，
> 客户端默认**内嵌**宿主（§2.2 的偏差说明与 `docs/DECISIONS.md` 第 167 轮）。
> 第④项现在的口径是：**内嵌为默认、独立进程为显式选项**；「同一套向量双跑」改由
> ① 的矩阵 + `tools\verify_embedded_host.ps1`（真的起 Provider 子进程连内嵌宿主）承担 ——
> 协议向量回放走的是库内直调，本来就分不出内嵌与独立。
> ④ **性能门禁**：§13 共 15 行指标，**一条脚本都没有**；本轮建 `tools/perf_gate.ps1` 逐项采集。
> **补充（2026-09-28，第 166 轮）：完成条件的两条都拿到了。**
> ① **矩阵 4 组全绿** —— 前两格 `tools/verify_matrix.ps1`（`-HostKind V2` = PASS 17/0、
> `-HostKind Dart` = PASS 18/0）；后两格新增 `tools/verify_matrix_android.ps1`
> （Kotlin 2.0 内核打 C# v2 主机 / 打 Dart v1 宿主，后者还要**查宿主自己的库**反证
> 「真的打了它，不是偷偷自己起了 C# 主机」）。Kotlin 客户端只用 `/api/v1/*`，所以两格走的都是兼容层。
> ② **性能表逐项有数**：`perf_gate.ps1` 复跑 **4 达标 / 3 不达标 / 6 N/A（15 行，131 s）**；
> 这一轮的表与上一轮差异、以及两处**口径陷阱**（与安卓仪器测试同时跑导致冷启动偏高；
> 安卓 PSS 读到 0 MB 是因为 UTP 跑完卸载了 APK）写进 §13.1。
> 另**更正**：②「旧备份兼容」与④「一键拉起主机」在前几轮就已经落地（`BackupArchiveTests`
> 有旧包用例、`HostLauncher.EnsureRunningAsync` 真的拉进程），本轮只是复核与补记。
- **内容**：① 组合矩阵回归：`{Android native, Android Flutter 1.1.0} × {Server v2, Flutter 1.1.0 宿主}` 四种；② 旧库/旧备份兼容（备份 zip 无 manifest → 新端要能读旧 zip）；③ 性能门禁脚本（§13）；④ **内嵌（默认）vs 独立进程双跑回归**：协议层的证据是「真的起 Provider 子进程连内嵌宿主、主机如实报在线」（`tools\verify_embedded_host.ps1` 第 8 条）与矩阵 4 格；启动时延差异记在 §13.3。
- **完成条件**：矩阵 4 组全通过；性能表逐项有数（达标或写明偏差与原因）。
- **风险**：Flutter 1.1.0 与 native 2.0 **同时运行**会互写同一库 → 见 §14 H5。

### Phase 8 · 发布 2.0 与 Flutter 下线（1–2 周）

> **进度（2026-09-26）**：⬜ 未开始。发布约定已定：公开仓更新说明**只写版本 + 变更**。
- **内容**：① 版本号与发布流程（沿用附录 C 的门禁）；② 三个仓库同时发版并互相引用；③ 迁移指引（数据目录、是否要重配、升级顺序：**先 Windows 后手机**、升级前跑空离线队列）；④ Flutter 目录下线（在 `main` 删除，历史留在 `flutter` 分支 + `v1.1.0-flutter` tag）。
- **回滚**：用户可装回 1.1.0（Releases 永久保留）；仓库侧 `git revert` 下线提交。

---

## 11. 并行开发方案

| 工作流 | 依赖 | 可并行 |
|---|---|---|
| Protocol 规范 + 向量 | 无（**一切的前置**） | ❌ 必须最先，冻结后才允许其它流进入实现 |
| Server v2（C#） | 向量 | ✅ 与 Android / Windows 内核并行 |
| Android 内核 | 向量 + 一个可打的 Server | ✅ |
| Android UI/设计系统 | Android 内核的 interface 冻结（先定 `ViewModel` 契约） | ✅ 与 Windows 内核并行；**与 Android 内核内部串行** |
| Windows 内核 | 向量 + `Server.Core` | ✅ |
| Windows UI + 悬浮窗 | Windows 内核 + WebView2 公式 spike | ✅ 与 Android UI 并行 |
| 集成 / 性能 | 上述全部 | ❌ 最后 |

**串行点**：① 向量冻结前不写任何端侧实现；② D1 未拍板前不写 Server（**已拍板 = A**）；③ `QuizSync.Data` 的 schema 映射未定前不写 Windows UI。
**并行度现实**（单人 + AI）：建议「一条端 + 一条辅助线」，例如（Android 内核 ‖ Protocol 补全）→（Android UI ‖ Server）→（Windows 内核 ‖ Android 打磨）。

---

## 12. 测试策略

| 层级 | 手段 | 目标（对齐现有资产） |
|---|---|---|
| 协议一致性 | **Protocol 的 `conformance/vectors`** 三端回放（Kotlin / C# / Dart 参考） | 三端对同一向量输出逐字段一致；**这是唯一裁判** |
| 单元 | Android: JUnit/Truth；Windows & Server: xUnit | Android 覆盖现有 88 条 + 新增；Windows 覆盖 core 212 条里的 AI/解析/同步/highlight 等价项（≥120）；Server ≥90 |
| 数据与迁移 | 旧库直开测试（真实 `quizsync.db` fixture）+ 备份 zip 读取 + 双向（native ↔ Flutter） | **「升级后数据不丢」必须有红→绿用例**（沿用 `migration_test` 口径） |
| 同步收敛 | 把 `lww_test` / `delete_edit_convergence_test` / `tombstone_gc_test` / `offline_queue_test` 的用例转成向量 | 删除 vs 修改、平局决胜、GC 条件、`QueueRetryLater` 全部可判定 |
| 集成（无设备） | loopback：真 Server + 真客户端完成 9 步 | Server 与 Android / Windows 客户端各跑一遍 |
| 端到端（真机） | 人工清单（沿用 `**操作** / **预期** / **失败看**` 三段式） | 真机 + 录屏取证；条目按 native 功能面重新生成 |
| LAN / 配对 / 离线 / 重连 | 关 Wi-Fi、切飞行、拔网线、端口占用、token 吊销、配对码过期、第二台安卓 | 每组都要有「期望文案 + 期望状态」 |
| 性能 | Windows：`probe_memory.ps1`；Android：Macrobenchmark + Baseline Profile + JankStats；Server：`dotnet-counters` | §13 每一项都有脚本与阈值 |
| 回归 | 每次提交跑三仓 analyze + 单测；`ALL GREEN` 口径移到新构建脚本 | **任何一包测试数下降即停下** |

**FTS5 双方案（Phase 3 的 spike 结论决定）**：主方案 = Room + `BundledSQLiteDriver`（自带 SQLite，保证 FTS5 + trigram）；回退方案 = 预分词 n-gram 列 + FTS4 / LIKE。两方案用**同一组检索测试**判定（含「中文 ≥3 字」「<3 字退化 LIKE」「墓碑不出现在结果」）。

---

## 13. 性能目标（Baseline → Target，全部给测量方法）

> 现状契约值与实测值都列出来；**不达标就如实标不达标**（这是本次审计发现的技术债第 8 条的一部分）。所有指标先有基线，再谈 target；没有基线就写「未测」，不许编数字。

| 指标 | 契约（SPEC §9） | 实测基线 | Target | 测量方法 |
|---|---|---|---|---|
| Windows 冷启动 | ≤1.5s | **未测** | ≤1.0s | `Stopwatch`：进程启动 → 主窗口首帧可见（release，冷缓存，5 次取中位） |
| Windows 空闲内存(RSS) | ≤150MB | **277MB**（M25 探针，release 稳态） | ≤150MB（去掉 Flutter 引擎后最可能改善的一项） | `probe_memory.ps1`（临时副本、60s 稳态与漂移） |
| Windows 点击 → 开始识别 | ≤800ms | 未测 | ≤800ms | 日志时间戳：`WM_HOTKEY` → 「开始分析」 |
| 取帧(GDI) | ≤500ms | 未测 | ≤300ms | 日志 `capture` 段耗时 |
| 悬浮窗内容帧 | — | **~40ms/帧（约 25fps 观感）**，M35 记录为未解决；富文本路径 260–660ms/片 | 滚动期 ≤16ms 且**不重排版** | 内置帧计时日志（≥40ms 写 warn，沿用现值）+ 人工体感 |
| Windows 便携包 | ≤60MB | 31.9MB（1.1.0 zip） | ≤80MB（WinUI 3 self-contained 会变大；**先测再定**，超 80MB 再裁剪） | `package_*.ps1` 产出体积 |
| Server 空闲 RSS | — | 未测 | ≤40MB | `dotnet-counters` / 任务管理器，60s 采样 |
| Server 空闲 CPU | — | 未测 | ≤0.5%（不含传输） | 同上 |
| Server 启动到 `/info` 可用 | — | 未测 | ≤200ms | 日志差值 |
| 无 Provider 时的任务响应 | — | 无此场景 | ≤2s 返回 `provider_offline`（不挂死） | 集成测试计时 |
| Android 冷启动 | — | **无设备，无法测** | 先采基线，目标「不超过 Flutter 版基线」，再向 ≤1.0s 收敛 | Macrobenchmark `StartupTimingMetric`（真机，冷启动 5 次中位） |
| Android 列表滚动 | — | 无 | 掉帧率 <1% | Macrobenchmark `FrameTimingMetric` / JankStats |
| Android 截屏取帧 | — | `Thread.sleep(60)` + `acquireLatestImage`，**无超时** | ≤500ms 且**有超时上限** | 日志时间戳 + 超时用例 |
| Android 内存 | ≤200MB | 不可测 | 先测基线再定 | `dumpsys meminfo` / Profiler |
| APK 体积 | 单 ABI ≤40MB | arm64 30.4MB（1.1.0） | ≤40MB（去掉 Flutter 引擎后大概率显著下降） | 构建产出体积 |

### 13.1 实测（2026-09-27 第 141 轮 · `tools/perf_gate.ps1`）

> 15 行**逐项有数**，含 5 行如实标 N/A（附原因）。表由脚本生成，可复跑：
> `$env:QS_DOTNET='D:\Windows\Apps\DSH_Tools\dotnet\dotnet.exe'; powershell -File tools\perf_gate.ps1`
> 原始表另存于 `TemporaryConversationFile\perf\perf-table.md`。**判定 4 达标 / 5 不达标 / 5 N/A**（整轮 128s）。

| 指标 | 实测 | 判定 | 与契约/基线的差 |
|---|---|---|---|
| Windows 冷启动 | **743 ms**（5 次中位） | ✅ | 契约 1.5s；比 Flutter 版「未测」→ 首次有数。口径是「主窗口可见」，比「首帧画完」**偏乐观** |
| Windows 空闲内存（工作集） | **159 MB**（私有 95 MB） | ❌ | 差 9 MB；但相对 Flutter 基线 **277 MB 降了 118 MB**（去掉 Flutter 引擎的预期兑现） |
| Windows 分发包体积 | **181 MB**（构建输出目录） | ❌ | 这是**产物目录**不是打包 zip（2.0 还没有打包脚本）；真实包体积要等 Phase 8 的打包 |
| Windows 点击 → 开始识别 | 未测 | N/A | **热键/按钮路径没有耗时日志埋点** —— 要补一行日志才采得到 |
| 取帧(GDI) | 未测 | N/A | 同上，截屏路径也没有埋点 |
| 悬浮窗内容帧 | 未测 | N/A | 悬浮窗本身还没做（Phase 6 剩余项） |
| Server 启动 → `/info` 可用 | **684 ms** | ❌ | 契约 200ms；含进程启动 + 端口绑定 + 建库。**首启要建库**，这一项要拆开看 |
| Server 空闲 RSS | **50 MB** | ❌ | 契约 40MB，差 10 MB |
| Server 空闲 CPU | **0.00 %**（16 核归一） | ✅ | 10s 采样窗口 |
| 无 Provider 时的任务响应 | **80 ms** 返回 `provider_offline` | ✅ | 契约 2s；从提交任务算起（含建任务往返） |
| Android 冷启动 | **1,305 ms**（`am start -W` 中位） | 基线 | **代理口径**：不是 Macrobenchmark，且不含 Application 之前的部分 |
| Android 内存（PSS） | **210 MB** | ❌ | 契约 200MB，差 10 MB |
| APK 体积（debug） | **24 MB** | ✅ | 契约 40MB；2.0 还没做 release 打包，debug 偏大 |
| Android 列表滚动掉帧 | 样本不足（**13 帧**、卡顿 1） | N/A | **帧数不到 100 的百分比不可作判据**（第一版按 7.69% 判「不达标」，是噪声） |
| Android 截屏取帧 | 未测 | N/A | 需真机手势 + 悬浮球权限 + logcat 埋点 |

**这一轮从「一条脚本都没有」到「15 行有数」，同时暴露了三个真问题**（都不是产品缺陷，是**测量**缺陷，
但它们会让性能表说谎，所以按同一条纪律对待）：

1. **图片上传要 `multipart/form-data`**（协议 `07-images` 字段名 `file`）。我第一版用 `--data-binary` 发裸 body，
   服务端如实回 `invalid_request: 需要 multipart/form-data`。
2. **上传响应的字段是 `image_hash`，不是 `hash`**（图片实体才是 `hash`）—— 这处不对称正是
   `versions/compat-matrix.md` 待决策 **C29** 记的那条。读了 `.hash` → 上传成功也拿不到值。
3. **建任务必须先有活跃合集**（没切合集 → `409 no_active_collection`）：配方是
   「推 collection op → `POST /collections/<id>/select` → 建任务」。这三条合起来，
   第一版的 `provider_offline` 那一行是 N/A —— 看起来像「这项测不了」，其实是**脚本错**。
   **教训写进脚本注释：N/A 之前先确认不是自己写错了。**

**同一轮补的两道测量纪律**（都在脚本里）：
* 掉帧率必须同时读**总帧数**，样本不足（<100 帧）只能记 N/A；
* 进程类指标必须**先杀掉同名进程再测**，且 `QS_APP_DATA` 指到临时目录（绝不碰用户数据）。

---

### 13.2 复跑（2026-09-28 第 166 轮 · 同一脚本）

> `$env:QS_DOTNET='D:\Windows\Apps\DSH_Tools\dotnet\dotnet.exe'; powershell -File tools\perf_gate.ps1`
> 判定 **4 达标 / 3 不达标 / 6 N/A**（15 行，整轮 131 s）。原始表：`TemporaryConversationFile\perf\perf-table.md`。

| 指标 | 实测 | 判定 | 与上一轮 / 契约的差 |
|---|---|---|---|
| Windows 冷启动 | **1,266 ms**（5 次中位） | 仅达契约 | ⚠️ **口径陷阱①**：这一轮与安卓仪器测试**同时跑**，机器满载；上一轮同一脚本是 743 ms。要干净数就单独跑 |
| Windows 空闲内存（工作集） | **157 MB**（私有 94 MB） | ❌ | 与上一轮 159 MB 一致；契约 150 MB，差 7 MB（Flutter 基线 277 MB） |
| Windows 分发包体积 | **181 MB**（构建输出目录） | ❌ | 仍是产物目录近似；2.0 还没有打包脚本（Phase 8） |
| Windows 点击→开始识别 | 未测 | N/A | 埋点仍未加（要补一行日志才采得到） |
| Windows 取帧（GDI） | 未测 | N/A | 同上 |
| 悬浮窗内容帧 | 未测 | N/A | **理由已更正**：悬浮窗**已经做完**（`probe_float_window.ps1` 7 PASS），但 2.0 的悬浮窗是**原生 XAML 窗口**，不是 1.1.0 的「离屏合成 + 分层窗口」，这条指标没有对等物 → 如实 N/A，不编数字 |
| Server 启动 → `/info` 可用 | **732 ms** | ❌ | 契约 200 ms；含进程启动 + 绑定 + 建库（首启建库占大头） |
| Server 空闲 RSS | **7 MB** | ✅ | 上一轮 50 MB → 7 MB（本轮测量时进程刚起、无连接） |
| Server 空闲 CPU | **0.00 %**（16 核归一） | ✅ | 10 s 采样 |
| 无 Provider 时任务响应 | **81 ms** 返回 `provider_offline` | ✅ | 契约 2 s |
| Android 冷启动 | **1,437 ms**（`am start -W` 中位） | 基线 | 代理口径，仍不是 Macrobenchmark |
| Android 内存（PSS） | **0 MB** | N/A | ⚠️ **口径陷阱②**：跑仪器测试的 Gradle 任务**结束时会卸载被测包**，这一刻机器上根本没有这个 APK —— 不是"0 内存"。要测就先重装 |
| APK 体积（debug） | **32 MB** | ✅ | 契约 40 MB；debug 偏大 |
| Android 列表滚动掉帧 | `gfxinfo` 没有帧统计 | N/A | 需要真的滚动过列表 |
| Android 截屏取帧 | 未测 | N/A | 需真机手势 + 悬浮球权限 + logcat 埋点 |

**两条口径陷阱是这一轮的主要收获**：表会因为「同时跑了别的东西」和「被测对象在测量那一刻不在机器上」
而说谎，而这两件事在数字上完全看不出来（1266 ms 与 0 MB 都长得像正常值）。

---

### 13.3 复跑（2026-09-28 第 167 轮 · 主机改为内嵌后 · 干净机器单独跑）

> 同一脚本、机器上没有别的东西在跑。判定 **4 达标 / 5 不达标 / 5 N/A**（未测行 3 → 1）。
> 原始表：`TemporaryConversationFile\perf\perf-table.md`。

| 指标 | 实测 | 与上一轮 / 契约的差 |
|---|---|---|
| Windows 冷启动 | **666 ms**（5 次中位） | ✅ 上一轮 1266 ms 是**满载污染值**；743 → 666 ms，**内嵌宿主没有拖慢冷启动**（宿主是在首帧之后异步起的） |
| Windows 空闲内存（工作集） | **158 MB**（私有 93 MB） | 与第 166 轮 157 MB、第 141 轮 159 MB **一致** ⇒ 把 Kestrel/ASP.NET 宿主搬进应用进程**没有让应用变胖**；而独立主机进程那份 50 MB（本表 `Server 空闲 RSS` 行）**省掉了**。契约 150 MB 仍差 8 MB（Flutter 基线 277 MB） |
| 内嵌宿主就绪耗时 | **142 / 214 ms** | 应用 `logs\crash.log`：「启动时主机：内嵌在本应用进程内 …（142 ms）」。对比独立进程 `Server 启动 → /info 可用` 本表 **708 ms** ⇒ 内嵌在这一项上快 3 倍多 |
| Windows 分发包体积 | **210 MB**（构建输出目录） | ❌ 仍是产物目录近似（Phase 8 才有打包脚本）；注意内嵌后**应用自己**要带 ASP.NET 运行时包 |
| Server 空闲 RSS | **50 MB** | 上一轮 7 MB 是「刚起、无连接」的取样；本轮 45 s 稳态更可信 |
| 其余行 | 与 13.2 同量级 | Android 冷启动 1,639 ms / PSS 233 MB（本轮重装过 APK，所以 PSS 不再是 0）/ APK 32 MB |

**这一轮的口径提醒**：内嵌之后「Server」不再必然是独立进程 ⇒ 读性能表时先确认
`Server 启动 → /info` 那一行测的是**独立进程**（脚本里就是那么测的），
应用内那一份的启动代价体现在「内嵌宿主就绪耗时」那一行（142–214 ms）。

## 14. 风险清单

### 高风险

| # | 风险 | 原因 | 解决 | 回滚 |
|---|---|---|---|---|
| H1 | **Android 真机行为无法自动验证** | 本机无设备无模拟器（现状已导致 38 项人工清单长期「未验证」） | Phase 0 就位真机；每阶段末尾人工清单 + 录屏取证；把可验证部分尽量上移到 JVM 单测 | 保留 Flutter 版 APK 供用户回退 |
| H2 | **同步引擎多处实现（Kotlin / C#）漂移** | LWW / 墓碑 / 水位是「细节决定正确性」的算法 | 全部以 Protocol 向量为裁判；迁移期**禁止顺手优化算法**（附录 A 清单） | 任一端发现偏差 → 以向量为准修实现，不改规范 |
| H3 | **WinUI 3 悬浮窗的透明 / 置顶 / 不抢焦点不达预期** | WinUI 3 对无边框 + 逐像素透明支持不如 WPF / Win32 分层窗口 | P1 spike（最小样例 + 契约 §2.5 的 12 项行为验收）。**备选**：悬浮窗也退回分层窗口（牺牲交互） | 悬浮窗契约本来就是「默认关闭」，2.0 可先默认关 |
| H4 | **旧库兼容失败（FTS5 / 迁移）** | Android 系统 SQLite 版本不定；FTS5 trigram 是中文检索关键 | 两端**自带 SQLite**；**双方案**（见 §12）；先只读不写 | 用户可退回 Flutter 版（库未被破坏） |
| H5 | **并存期两个版本写同一个 `userdata`** | Flutter 1.1.0 与 native 2.0 都指向 `<exe>\userdata\quizsync.db` | native 版启动时**等待 Flutter 版已知的命名信号量** `QuizSyncAI_SingleInstance_7C6E1F42`，有实例就提示并不写库；首次运行走复制式迁移 | 关掉 native 版即可；数据始终有备份 |

### 中风险

| # | 风险 | 解决 |
|---|---|---|
| M1 | 协议 v2 冻结后仍需变更 | 主版本内只增不改；变更必须同时更新向量与兼容矩阵；`deprecated_since` |
| M2 | v1 兼容层语义偏差导致旧手机端异常 | 以现有 Flutter 客户端为裁判逐条对；四种组合矩阵回归（§4.6） |
| M3 | 公式渲染（WebView2 + KaTeX）体积与性能 | P1 spike 定量：常驻内存、首屏排版耗时、离线资源大小；不达标则只对含公式的题按需创建实例 |
| M4 | Server 从 Dart 换 C# 后既有 Server 数据迁移 | 一次性迁移器 + `doctor` 检测旧目录 |
| M5 | AI Key 只存 Windows → 电脑不开机手机不能用 | 这是**产品选择**（`SPEC.md §10`）；将来若要手机直连 AI，作为新能力 `direct_ai` 单独立项，v2 不做 |
| M6 | 单人维护三仓库的管理成本 | Protocol 只随协议动；Server 冻结；App 是唯一活跃仓库；CI 只做三件事（构建、测试、向量回放） |
| **M7** | **两个进程争 Host 角色** | 命名互斥体 + 端口所有权；验收 = 任意启动顺序下只有一个 Host，且客户端自动退化为客户端模式 |
| **M8** | **把「从未验证的行为」当契约照抄** | 开工前把附录 A 的不对称语义逐条标注为「契约」或「待验证假设」；标注为假设的必须先真机确认 |

### 低风险

| # | 风险 | 解决 |
|---|---|---|
| L1 | WinUI 3 self-contained 包体积上升 | 先测再定；必要时裁剪资源 |
| L2 | Hilt / KSP 编译时间 | 单 module + KSP（不是 KAPT）；必要时按 feature 拆 module |
| L3 | 托盘/通知在 Win11 上的行为差异 | `H.NotifyIcon` 与系统 API 双路径，抽查 Win10 / Win11 |
| L4 | Android targetSdk 34 → 35/36 的强制 edge-to-edge | 第一阶段保持 34；升 35 作为独立任务（现有三层状态栏经验直接搬） |

---

## 15. 第一阶段任务清单（P0–P3）

### P0（必须先做，不写产品代码）
1. **冻结基线**：`git tag -a v1.1.0-flutter` 并推送；核对 `flutter` 分支锁定与 Releases 资产仍在。验收：`gh api .../tags` 有该 tag；向 `flutter` 推送被拒。
2. **准备一台安卓真机**（USB 调试可用），`adb devices` 非空。**这是一切 Android 工作的前置**。
3. **修掉唯一 P0 数据缺陷**：`apps/android/lib/state/app_state.dart:214-216` 的轮询落库改走 `applyAnalysisResult`。验收：新增红→绿用例（轮询结果不得覆盖 `answer_edited` / `analysis_edited`）+ 负向对照。
4. **建两个新仓库骨架**（`QuizSyncProtocol` / `QuizSyncServer`）+ 三仓 `CONTRIBUTING`。
5. **三份 ADR 合入**（`docs/adr/0001`–`0003`）。
6. **复制四份契约**到 Protocol 仓库的 `versions/v1-snapshot.md` 输入区，**原文件不动**。
7. **验证债**：真 Key 跑一次端到端 AI；干净机器验证便携包（VC 运行库）。

### P1（Phase 1–2 核心）
8. `spec/` 11 个文件 + `schema/` + `examples/` 写完；3 处文档-实现分叉在 v1 快照里标注为「实现为准，文档修正」。
9. `conformance/vectors` 首批（配对 / 鉴权 / 上传 / 任务 / sync / 错误 6 组），并用 **Dart 参考回放器**跑通。
10. **三个 spike**（各 1–2 天，只出结论与最小样例，不进生产）：① WinUI 3 无边框悬浮窗（透明/置顶/不抢焦点/整窗 alpha + 契约 12 项行为）；② WebView2 + KaTeX 渲染现有 19 条渲染用例；③ Room + `BundledSQLiteDriver` 的 FTS5 trigram 可用性（含回退方案基准）。
11. **Server v2 骨架**：Kestrel + `/api/v2/info` + `/health` + 配对 + 令牌 + 日志 + `doctor`；向量跑到 30%。
12. **建立 CI**（三仓库：构建 + 测试 + 向量回放；Windows 用 `windows-latest`，Android 只跑 JVM 单测 + 编译）。现状**完全没有 CI**。

### P2（Phase 3–5）
13. Server：`/api/v1` 兼容层、Blob 流式限长、任务队列 + `recognition_job`、op 中继与水位、快照分页、CLI 全命令集、随登录启动。
14. Android 内核：Room schema + 同步引擎 + OkHttp 网络 + 旧库双向兼容回归 + `CaptureService` 复用 + token 自持存储与一次性迁移。
15. Windows 内核：`QuizSync.Data` + AI/解析/highlight 移植 + 截屏/热键/剪贴板/托盘 + ServerBridge + `--headless`。
16. **数据单写者保护**（H5）：native 版等待 Flutter 版信号量 + 首次运行复制式迁移。

### P3（Phase 6–8）
17. Android UI/设计系统（§6 组件表 + 状态矩阵）；Windows UI（10 分类 `NavigationView` + `SettingsCard`）+ 悬浮窗/球重写。
18. 性能门禁脚本与基线采集（§13 每一项）+ Baseline Profile。
19. 组合矩阵回归（4 组）+ 内嵌/独立双跑 + 性能达标 + 发布 2.0 + Flutter 目录下线。

---

## 附录 A · 不得顺手改动的「不对称语义」清单

这些是用户反复拍板、看起来「不够干净」但**必须原样保留**的行为。重写时逐条标注为「契约」或「待验证假设」：

1. **合集删除方向不对称**：主机删合集 → 手机**不跟随删**（`applyRemoteCollectionDeletes=false`）；手机删合集 → 正常同步给主机；桌面端默认 `true`。
2. **缓存与同图去重只对单页生效**；多页必须真实调用 AI；去重要求**页序完全一致**。
3. **多页硬上限 6**（`kHardMaxPagesPerTask`）。
4. **题号与序号分离**：题号徽标不带尾随点号。
5. **极简模式只留命中选项**，不渲染解析与材料；无答案时保留全部选项。
6. **悬浮球默认关闭** + 一次性迁移标记（`ball_enabled_migrated` / `float_window_mode_migrated`）。
7. **界面不显示构建号**；出包序号只留在 pubspec 的 `+N`。
8. **安卓端不提供任何导出/分享入口**（能力代码保留但无入口）。
9. **关掉悬浮球 = 截屏会话一并结束**（停前台服务并释放 MediaProjection）。
10. **手机发起识别要显示、本机识别必须静默**（连「藏/恢复窗口不得抢焦点」都算）。
11. **本机截屏任务不经任务队列**（v2 内部统一，但 v1 兼容层必须保持旧语义）。
12. **开机自启不做**（M47 已删功能与条款）。
13. **悬停放大不做**；自由拉伸不做（只留三种外观）；窗内 `A−`/`A＋` 不做。
14. **局域网不做 HTTPS 化**（`optimization-plan.md §4.C`）。
15. **不引入账号体系 / 云服务 / SaaS / 公网访问**（`AGENTS.md §6`）。
16. **陌生设备提示不做**（M48 反转 M47：拒绝，但不显示提示）。
17. **配对药丸不做**（配对状态只在「设置 → 连接设备」）。
18. **安卓「图片缓存上限」设置项不做**（固定最近 20 张）。

## 附录 B · 平台实测坑（新实现必须重新满足）

> 完整清单见 `AGENTS.md §1` 与 `docs/DECISIONS.md`。以下是重写时**最容易丢**的一批，每条都要在新实现里有对应做法与取证：

**Win32 / 分层窗口**：`UpdateLayeredWindow` 前必须真建好 DIB（`_bits != nullptr`，不许静默吞）；移动窗口要补 `SetWindowPos`；像素必须是预乘 BGRA + `ULW_ALPHA`；位图高度有 `clamp(1, 8192)` 物理像素上限（长内容必须切片）；分片拷贝两侧有留白时**必须逐行**；切片原点要带 `bodyRect.topLeft`；未置顶不能把 Z 序写成 `HWND_TOP`；点击阈值必须是**逻辑**像素（按 DPR 折算）；非激活窗口（`WS_EX_NOACTIVATE`）收不到键盘 → 复制入口挂右键；验证分层窗口渲染用 `PrintWindow(hwnd,hdc,2)`，不要用 GDI 截屏。

**全局热键**：`RegisterHotKey` 绑**线程**；要线程稳就别 await；每槽位固定 id；每次注册前 `UnregisterHotKey`、退出前显式注销；**每 2 秒同 id 再注册自检**（成功即说明丢了）；注销后立刻重注册偶发 1409 要重试；`F2/F8/F9` 可能被别的程序吃掉（注册成功但永不触发）、`F12` 已被占；**不要用「注销热键」表达 UI 状态**，改成触发那刻判断「页面挂载 ∧ 本窗口前台」，被吞的触发要写日志。

**剪贴板 / 对话框 / 窗口**：`BI_BITFIELDS` 是常态，`GlobalSize` 不可靠，字节数由 DIB 头自算，`biHeight` 为负要真符号扩展；`GetSaveFileNameW` 的路径不能混正反斜杠（否则 `FNERR_INVALIDFILENAME` 0x3002），「返回 0」要区分取消与真错误；窗口标题只能有 `kAppName` 一个来源；藏/恢复窗口不得抢焦点；单实例用 `CreateSemaphoreW` + `WaitForSingleObject` **只看返回值**（Dart FFI 不保证 last-error，.NET 无此问题但语义照旧）；托盘图标要用**绝对路径**（`LoadImage` 按进程 CWD 解析，WinUI 侧同理）。

**Android**：API 35 起引擎不设状态栏底色 → 原生实现里**自绘条带可以删除**（原生不存在引擎覆盖问题），但图标明暗仍要在 `onResume`/获焦重贴；`createVirtualDisplay` 每会话只能调一次，且 `registerCallback` 必须早于它；前台服务必须声明 `foregroundServiceType="mediaProjection"` 且有常驻通知；overlay 必须挂 `applicationContext`（挂 Activity 会随 window token 销毁被移除）；**ComposeView 放进 overlay 必须手设三个 ViewTree owner**；R8 full mode 会裁掉 ML Kit 的反射构造器（要 keep 规则）；关球即停投屏。

**交付与编码**：版本号唯一来源；`tools/*.ps1` 与 `installer.iss` 必须 UTF-8 **with BOM**（`edit` 工具会剥 BOM）；脚本里**不要用 `2>$null`** 读原生命令输出；`Runner.rc` 保持 UTF-8 无 BOM；交付物不得含 `userdata`、不得出现内部里程碑字样与出包序号。

## 附录 C · 交付与发布门禁（沿用并迁移）

1. **版本号唯一来源**：Flutter 时代 = 两个 `pubspec.yaml`；native 时代 = 三仓库各自的版本清单（协议版本 / 客户端版本 / Server 版本），并保留一致性断言。
2. **产物命名**：`quizsync-windows-portable-<版本>.zip`、`quizsync-windows-setup-<版本>.exe`、`quizsync-android-<abi>-<版本>.apk`（三 ABI：`arm64-v8a` / `armeabi-v7a` / `x86_64`）、`quizsync-source-<版本>.zip`；便携版门禁口径 `entries ≈ 61–64 / userdata=0 / VC DLL=10`。
3. **APK**：`versionCode = 1000×ABI段 + 构建号`（arm64=1、armeabi=2、x86_64=4；末次 1022/2022/4022）；`minSdk 26 / targetSdk 34 / compileSdk 36`；`applicationId com.quizsync.android`；`allowBackup=false` + `dataExtractionRules` 排除云备份与换机直传；`isMinifyEnabled=true` + proguard keep ML Kit；**60 项只读审计必须 PASS**（失败即非 0 退出、不打印 `ALL GREEN`）。
4. **签名**：`keystore\quizsync-release.jks`（PKCS12，alias `quizsync`，30 年）；口令在 `keystore\口令-请妥善保存.txt`（需转存密码管理器）；`keystore/`、`*.jks`、`key.properties` 全部 gitignore。**keystore + 口令 = 升级的唯一钥匙，丢了只能卸载重装**。
5. **发布**：主项目 tag = 版本号、**保持 Latest**；独立 Server tag = `server-<版本>`、`make_latest=false`；创建走 `gh api --input <utf-8 json>`（不要用命令行传中文标题）；发布前红线自检（无 `config.json` / `state.json` / `runtime.json` / `images` / `userdata`、exe 内无 `sk-` 真 Key、无用户名路径、无 `192.168.x.x`）；**发布后把资产下载回来比 sha256**。
6. **打包红线**：打包前强制删除并复核 `userdata`（zip 内再查、`installer.iss` 再排除）；不在 `build\...\Release\` 里跑过 exe 再打包；随包 VC 运行库 10 个 DLL（**必须排除 `\onecore\`**）；冒烟只走 `probe_memory.ps1`（临时副本、发现已有实例直接 ABORT、绝不杀进程）。
7. **源码包**：带 MiSans 字体（20,000,736 B，不带没人能构建）；剔除内部构建记录（`AGENTS.md` 与 `docs/{DECISIONS,DEV,milestones,progress,optimization-plan,manual-test-android,agent-kickoff-prompt}.md`，只留 `SPEC/protocol/data-model/ai-contract`）；zip 内无外层目录。

## 附录 D · 本轮决策与后续变更入口

- 决策见 `docs/adr/0001-host-provider-boundary.md`、`0002-protocol-v2-and-v1-compat.md`、`0003-db-compat-and-packaging.md`；`docs/DECISIONS.md §六` 记录了它们**取代了哪条旧决策**（首条即 M47 ③）。
- 后续任何偏离本文件的决定，一律写进 `docs/DECISIONS.md`（新增「重写决策」条目），并同步更新本文件对应章节；**不允许只在代码里体现**。
- 文档归属（Phase 1 之后）：`milestones.md` / `progress.md` / `optimization-plan.md` / `agent-kickoff-prompt.md` 作废（只抽验收口径与经验条目）；`DEV.md` 整篇重写；`AGENTS.md` 拆两半（§1 环境事实 + §6 非目标 保留扩写；§2–§5 重写）；四份契约保留并剥离实现绑定。
