<!--
本目录是 Phase 0 输入材料（按实现反推的现状规范），不是规范正文。
规范正文见 versions/v1-snapshot.md（Phase 1 交付）。
-->

# versions/v1-input —— 按实现反推的现状规范（输入材料）

## 1. 这是什么

四份**从现有实现反推出来的现状契约文档**，已做一轮机械剥离：去掉只属于「某一端某一种技术栈」的表述
（语言 / 框架机制 / 第三方库 / 包名 / 仓库内文件路径 / 应用内部标识符），**语义一字未改**。

它们**不是**规范正文，而是重写规范时的**事实底稿**：规范正文是 `versions/v1-snapshot.md`（Phase 1 交付，
语言中立、可被下一代安卓端（Compose）、Windows 端（WinUI）与 C# 服务端共同实现）。

本仓库只放纸面与数据（HTTP/WS 契约、消息结构、JSON Schema、协议版本策略、一致性测试向量），
不放任何一端的实现。

## 2. 来历

| 项 | 值 |
|---|---|
| 来源仓库 | QuizSyncAI（局域网跨端搜题工具：Windows 截屏 → 多模态 AI 解题 → 结果同步到 Android） |
| 来源版本 | `1.1.0`（tag 见每份文件前言里的来源行） |
| **原始提交** | `9dc4d7978b299a552fce109fa1fda55b0a427db2`（2026-09-26T02:13:05+08:00，提交信息「Phase 0: 三仓 CONTRIBUTING + 实施期决策与进度记录」）—— 复制时的源仓库 HEAD |
| 来源 tag | 前言来源行里那个 `v1.1.0-…` 前缀的 tag（仓库里确实存在该 tag；复制时的 HEAD 比该 tag 多 2 个提交，`git describe` = `<tag>-2-g9dc4d79`） |
| 逐文件最后改动提交 | `SPEC.md` = `eba9a44`（2026-09-25）；`protocol.md` = `70f1467`（2026-09-20）；`data-model.md` = `a6bd471`（2026-09-19）；`ai-contract.md` = `a6bd471`（2026-09-19） |
| 复制时源仓库工作区 | 干净（`git status` 无输出） |
| 来源位置 | 源仓库 `docs/` 目录 |
| 取得方式 | 整文件复制，**未改名、未重排章节、未合并段落**；剥离只做逐行最小替换（见第 4 节） |
| 本批文件 | `SPEC.md`（功能规格）、`protocol.md`（局域网协议契约）、`data-model.md`（表结构与同步算法）、`ai-contract.md`（prompt 全文 + AI 输出 JSON Schema + 标绿规则） |
| 源仓库状态 | 只读事实来源；语义有争议时以源仓库为准，直到 `v1-snapshot.md` 定稿 |

四份文件的**互引**（例如 `protocol.md` 引 `SPEC.md` §8、`SPEC.md` 引 `data-model.md`）保留原样，
它们在**本目录里同名存在**，引用不会失效。指向源仓库特有文档的引用（决策记录、服务端说明、
人工测试清单等）已改写为「原仓库的决策记录 / 原仓库的服务端说明」这类描述，因为它们在本仓库不存在。

## 3. 剥离口径

**铁律**：规范性内容一字不动，只把「某一端的技术栈表达」换成行为 / 能力描述。

### 3.1 必须保留（这些不是实现绑定）

- 全部端点、方法、路径（`/api/v1/...`、`/ws`）、HTTP 头名（`X-QS-*`、`Authorization`）、
  状态码、错误码、字段名、JSON 键名、表名与列名、默认值、单位、上限、超时、限流数字、
  算法语义（Lamport 时钟、逐字段 LWW、墓碑与 GC、水位线与增量拉取、快照折叠、离线队列）、
  不对称语义、验收口径、示例报文。
- **逐字保留的整块**：`ai-contract.md` 的 prompt 全文与输出 JSON Schema、`data-model.md` 的整段
  建表 SQL、`protocol.md` 的所有 JSON / jsonc 报文示例、`SPEC.md` 的错误码与文案表格。
- 端口 `8765` 与 `8766–8770`、协议版本号、应用名「AI 双端搜题」、项目名 `QuizSync AI` /
  `QuizSyncAI`、包名 `com.quizsync.android`、版本号 `1.1.0`、快捷键名（`F8` / `F9` / `Ctrl+V` 等）。
- **平台 API 与平台事实**：两条产品线跑在同一平台上（Windows / Android），这些名字不属于
  「某一端的技术栈」，而且承载验收口径（例：「恢复窗口必须不激活」「Android 15 起必须自己画状态栏底色」），
  因此保留。
- 字体与许可条款（MiSans、Material Icons 的 Apache-2.0 等）、第三方 AI 服务的模型名与端点、
  数据目录与产物路径约定、`M*` / `用户需求 N` / `用户反馈 N` 编号（溯源标记）。

### 3.2 必须剥离（→ 行为 / 能力描述）

| 类别 | 处理方式 |
|---|---|
| 语言与框架机制 | 语言名、框架名、界面组件名、渲染与离屏管线名、状态订阅机制名、平台通道名 → 中性描述；纯实现细节直接删除 |
| 第三方库 / 包名 | HTTP 服务端框架、数据库访问层与迁移钩子、状态管理库、HTTP / WS 客户端库、二维码生成与扫码库、窗口 / 托盘 / 全局热键 / 底层 FFI 库、密钥存储库、公式排版库 → 能力描述 |
| 仓库内路径与文件 | 共享核心包、两个应用目录、服务端源码、图标生成脚本、测试文件、资源文件、行号引用 → 角色词（客户端 / 服务端 / 共享核心）或行为描述 |
| 应用内部标识符 | 类名、方法名、常量名（`k*` 前缀）、驼峰字段名 → 「该上限 / 该默认值 / 该文案 / 该行为」，**数值一个都没丢** |
| 安装器与构建工具 DSL | 安装器脚本指令、构建开关 → 行为要求（安装目录固定为英文 `QuizSyncAI`、必须关闭图标字体裁剪…） |
| 失效文档引用 | 指向源仓库特有文档 → 「原仓库的决策记录 / 原仓库的服务端说明」 |

## 4. 逐文件改动清单

改动方式是**逐行最小编辑**（整文件复制后只替换命中的片段），因此除下表列出的行以外，
其余源行**逐字保留**。

| 文件 | 行数 | 字节数 | 编辑处数 | 涉及源行数 |
|---|---|---|---|---|
| `SPEC.md` | 606 | 71,383 | 65 | 90 |
| `protocol.md` | 339 | 16,498 | 10 | 11 |
| `data-model.md` | 317 | 15,123 | 7 | 7 |
| `ai-contract.md` | 320 | 18,825 | 15 | 31 |
| 合计 | 1,582 | 121,829 | 97 | 139 |

（每份文件另有统一的 6 行前言，是新增内容；「涉及源行数」由逐行比对源文件得出，
已核对**每一行改动都是有意为之**，没有误删。）

### 4.1 `SPEC.md`（功能规格）—— 65 处

| 类别 | 约计 | 典型条目 |
|---|---|---|
| 语言 / 框架 / 框架机制名 | 14 | 离屏渲染与浮层机制的框架叫法、界面组件与图标按钮、悬停提示组件、主题配色令牌、勾选型菜单项、Toast 排队、状态栏三层保障整段（引擎行为、声明式兜底、样式缓存）、平台通道与「应用层」、浮层层叠顺序、虚拟画布与撑开约束、自动换行约束、行内公式样式、缩放铺满性测试 |
| 应用内部标识符 | 31 | 页数硬上限常量、悬浮球描边常量、出厂默认值常量、缩放档位表、最小窗口基准、安卓图片保留上限常量、提示文案常量、`CaptureCoordinator` / `AnalysisWorkflow.retrySession` / `CaptureService` / `overlayHiders` / `FloatFrame` / `autoOpenResult` / `pullFromPeer` / `mirrorCollections` / `WindowsSecureStore` / `AndroidOptions` / `index = 0` / `rect.top` / `AppRadius` / `memcpy`、标绿判定与配色标识符、`Question.displayTitle` / `QuestionCard` / `MathText`（2 处）、决策记录引用（3 处） |
| 仓库内路径与文件 | 12 | 服务端采集流源码、窗口主题源码、悬浮窗视图 / 内容 / 合成三个文件、缩放铺满测试、图标生成脚本（2 处）、随包字体文件、应用图标资源（2 处）、托盘图标资源、服务端说明文档、导出路径归一化函数 |
| 第三方库名 | 3 | 窗口控制库的亮度接口、公式字形来源（KaTeX）、悬浮窗实现理由里的窗口 / 托盘库 |
| 安装器与构建工具 DSL | 2 | 安装器目录指令与应用标识、关闭图标字体裁剪的构建开关 |
| 包名 / 命名空间 | 1 | 旧示例命名空间字面量（2 次出现，1 处编辑） |
| 其他 | 2 | 「关于」页整行（版本号来源、图标资源、许可页）、平台调用名（ShellExecute）、框架组件名（SnackBar） |

被明确保留下来的密集区域：§2.1 剪贴板 DIB 取法（格式常量与头字段全部保留）、§2.4 窗口隐藏 / 恢复的
焦点语义（平台窗口 API 与标志保留）、§2.5 悬浮窗的窗口样式与合成路径、§3.2 截屏三条通路
（MediaProjection / 无障碍截屏 / 相册降级）、§4.3 标绿配色值、§8 失败矩阵全部错误码、§9 非功能指标全部数字。

### 4.2 `protocol.md`（局域网协议契约）—— 10 处

仅这些地方被改动，其余（端点表、配对报文、鉴权、错误码、WS 消息、幂等总则、环形验证 9 步）逐字保留：

1. 共享核心包引用 → 「两端共用同一份契约模型」；
2. 传输层「服务端」行：HTTP 服务端框架三件套 → 「以 HTTP 服务 + 路由 + WebSocket 能力实现」（`0.0.0.0` 保留）；
3. 明文行：安卓网络安全配置**文件名** → 「网络安全配置」，配置键 `cleartextTrafficPermitted` 保留；
4. 二维码渲染 / 摄像头扫码库名 → 「二维码由服务端渲染 / 通过摄像头扫码」；
5. 安卓 token 存储措辞 → 「系统加密存储（Keystore）」（平台 API 保留）；
6. 页数硬上限常量名 → 「服务端硬上限 = **6** 页」（数字、`too_many_pages` 保留）；
7. 客户端镜像合集列表的实现标识符 → 行为描述；
8. 合集删除的方向性：实现开关标识符（4 行）→ 「安卓端把『应用远端合集删除』的行为开关关闭 / 桌面端该开关默认开启」，
   因果链与「同步引擎契约没有被改」的结论一字不改；
9. 回环集成测试的文件路径 → 「回环（loopback）集成测试」。

### 4.3 `data-model.md`（表结构与同步算法）—— 7 处

**整段建表 SQL（含表名、列名、注释、索引、默认值）与全部算法章节逐字保留**，只改了：

1. schema 定义来源（数据库库名 + 包路径）→ 「两端使用**同一份**表结构（schema）定义」；
2. 密钥存储库名 → 「系统安全存储」（`devices.token_hash` 保留）；
3. 迁移实现类名 → 「数据库迁移钩子（迁移策略）」（`schemaVersion` 保留）；
4. 迁移测试文件路径 → 「数据库迁移测试」；
5–6. 两处 `onUpgrade` 标题 → 「v1 → v2 / v2 → v3 的升级迁移」（两张变更表逐字保留）；
7. 迁移测试断言描述里的文件名 → 「数据库迁移测试」。

### 4.4 `ai-contract.md`（prompt + JSON Schema + 标绿规则）—— 15 处

**prompt 全文（代码块）与输出 JSON Schema（代码块）逐字保留**，改动为：

1. 共享核心包引用 → 「共享核心」；
2. **§1 Provider 抽象**：原代码块（抽象类与配置类声明）→ 语言中立的能力描述 + 配置字段表；
   `id` / `analyze` / `jpegBytesList` / `prompt` / `config` / `providerId` / `baseUrl` / `apiKey` /
   `model` / `timeoutSeconds`（默认 90）/ `maxRetries`（默认 2）**全部逐字保留**；
3–4. 容错解析第 1、2 步的解析函数名 → 「按 JSON 解析」；
5. `Question.material` → 「对应 DB 列 `questions.material`」；
6. 超时说明里的决策记录引用 → 「依据见原仓库的决策记录 3.5」；
7. 多页上限：配置项与硬上限常量名 → 「可通过配置项 `multiPageLimit` 配置 / 服务端硬上限**就是 6 页**」；
8. 驼峰字段名 `answerGuessed` → 与 DB / JSON 一致的 `answer_guessed`；
9–10. `displayTitle`（2 处）→ 「排序序号 + 题号」的行为描述（示例 `3. 第 12 题` 保留）；
11. `QuestionCard` → 「两端共用的题目卡片组件」；
12. `Question.hasStructuredAnswer` → 「由端侧按『是否存在结构化多行答案』判定」；
13. 标绿配色标识符（4 行）→ 「判定只给出『是否需要复核』，颜色由端侧按『可信 / 不确定』两档选择」；
14. 测试 fixture 目录路径 → 「所有 fixture 由测试自带」。

## 5. 禁用词验证

在 `versions/v1-input/` 下按任务书给定的 15 个禁用词正则（不区分大小写）
搜索全部五个文件，结果：**四份规范文件的正文命中 0**；唯一的命中是四份文件第 2 行的统一前言来源行，
以及本 README 下面这一条豁免记录（元文档）。逐文件实测明细见交付答复。

| 命中词 | 位置 | 为什么必须保留 |
|---|---|---|
| `Flutter`（含 tag 形式） | `SPEC.md` / `protocol.md` / `data-model.md` / `ai-contract.md` 的**第 2 行**（统一前言的来源行），每行 2 次；另加本表格行 1 次 | 前言是本批材料的**统一模板**，由任务书指定逐字照抄（写明来源仓库、来源版本与技术栈 tag，属溯源信息）；删改前言即破坏四份材料的一致性。本 README 记录这条豁免是为了让「命中数不为 0」有据可查。**四份文件的正文里该词出现 0 次。** |

另外两点说明（避免误判）：

- `com.quizsync.android`（包名）**不匹配**下划线形式的禁用词（它中间是点号），它是数据与升级路径的一部分，按规则保留；
- 有两个很短的禁用词在英文单词里可能被误命中（词尾恰好相同），本目录里没有这类英文词，实际命中为 0。

## 6. 「未处理，待 Phase 1 重写时确认」清单

以下是**原样保留**（未剥离）但属于判断项的内容。保留理由见第 3.1 节；Phase 1 重写规范时请逐条确认。

1. **平台 API 与平台机制名**（数量多、分布广）
   - Windows：`SetWindowPos` 与 `SWP_NOACTIVATE` / `SWP_SHOWWINDOW`、`ShowWindow(SW_SHOW)`、
     `SetForegroundWindow`、`UpdateLayeredWindow`、`WS_EX_LAYERED` / `TOPMOST` / `TOOLWINDOW` /
     `NOACTIVATE` / `WS_POPUP`、`DWMWA_*` 与属性编号 20 / 19 / 35 / 36 / 34、`RegisterHotKey`、
     `GW_HWNDPREV`、`SetTimer` / `WM_TIMER`、`GlobalSize`、`CF_DIB` / `CF_DIBV5` / `BI_RGB` /
     `BI_BITFIELDS` / `biHeight`、`CryptProtectData` / DPAPI、`GetSaveFileNameW` /
     `FNERR_INVALIDFILENAME` / `CommDlgExtendedError`、`RegSetValue`、`Program Files` / `%LOCALAPPDATA%`；
   - Android：`MediaProjection` / `VirtualDisplay` / `ImageReader` / `MediaProjection.Callback.onStop`、
     `AccessibilityService.takeScreenshot()`、`SYSTEM_ALERT_WINDOW`、
     `foregroundServiceType="mediaProjection"`、`REQUEST_IGNORE_BATTERY_OPTIMIZATIONS`、
     `FLAG_SECURE`、`allowBackup` / `dataExtractionRules`、`EncryptedSharedPreferences` / Keystore、
     `API 30` / `API 35` / `Android 14` / `Android 15`、`onResume` / `resumed` / `Toast`；
   - 数据层：`FTS5` / `LIKE` / `fts5`。
   - 待确认：规范正文是否要降级为纯行为描述（如「不激活的显示路径」），或保留平台 API 名以便实现端对照。
2. **字体与许可条款**：`MiSans`、微软雅黑 / Segoe UI、`Material Icons` 的 Apache-2.0、官方 FAQ 链接。
   它们是产品与法律声明（含「不得单独再分发字体文件」这类义务），不是技术栈。
   待确认：下一代若更换字体 / 图标集，这类条款要整体重写。
3. **`M*` / `用户需求 N` / `用户反馈 N` 编号**：全部保留（数百处），作为溯源标记。
   待确认：`v1-snapshot.md` 是否保留这些编号（它们指向源仓库的里程碑与反馈记录，本仓库没有对应文档）。
4. **第三方 AI 服务与模型名**：`DeepSeek`、`deepseek-flash`、`https://api.deepseek.com`、
   `gpt-4o-mini`、`qwen-vl-max`、`glm-4v-plus`、`claude-sonnet`、`gemini-2.x`，以及三家端点的请求形态
   （`chat/completions`、`image_url`、`v1/messages`、`inline_data` 等）。它们是**外部服务的数据契约**，
   不是本项目的技术栈。待确认：是否要把默认模型名 / 默认 base_url 写进规范，还是写成「默认值由各处配置」。
5. **产物与目录约定**：`dist\quizsync-windows-portable-<版本>.zip`、
   `dist\quizsync-windows-setup-<版本>.exe`、安装目录 `QuizSyncAI`、数据目录
   `<exe 所在目录>\userdata`（含「**不能叫 `data`**」的理由与「不可写时退回系统数据目录」的行为）、
   `<应用目录>\userdata\exports`、`<数据目录>/images`、`server-images`、`app_icon.ico`。
   待确认：这些是**发布与升级路径**的一部分（保留），还是各端可自定的实现细节。
6. **少量平台调用名被改写成行为描述**（语义保留、名字去掉）：`ShellExecute` → 「用系统默认方式打开」、
   `memcpy` → 「一次内存拷贝」、`Toast` 的排队调用 → 「系统 Toast 接口本身会排队」。
   待确认：规范正文是否需要写回具体调用名。
7. **旧示例命名空间字面量**（`SPEC.md` §10，2 处）：只保留「旧的示例命名空间保持不变（它只决定
   资源类与相对类名的解析，不是安装身份）」这一事实，字面量不再出现（该字面量本身会命中禁用词）。
   待确认：是否需要以别的方式记录这个命名空间。
8. **`k*` 常量名与驼峰字段名**：全部改为行为描述（例如「页数硬上限 = 6 页」），数值与语义未变。
   待确认：`v1-snapshot.md` 若需要为这些上限 / 默认值定名，请**重新命名**（不要恢复源实现的旧名）。
9. **`ai-contract.md` §1 的接口形态**：原代码块已改成中性描述，字段名逐字保留；其中
   「输出为一次原始响应（原始文本 + 状态信息）」是对原声明的转写，待确认与原实现语义完全一致。
10. **原文既有的两处悬空 / 过时表述**（按「不许顺手改好」原则**未修复**）：
    `SPEC.md` §2.3 的「详见 §2.7」（本文件没有 §2.7）；`SPEC.md` §11 仍写着「公式渲染（M6 才做）」，
    而正文已有公式渲染章节。两者都是源文件的现状，留待 Phase 1 一并处置。
11. **平台配置文件名的处理不一致**：`protocol.md` 与 `SPEC.md` 都已把网络安全配置**文件名**改成
    「网络安全配置」（配置键 `cleartextTrafficPermitted` 保留）；`data-model.md` 保留
    `fts5` 虚拟表写法。待确认规范正文的统一粒度。
