# QuizSync AI — Agent 执行入口（原生线）

> **本仓是原生线**（GitHub: `iop666/QuizSyncAI_Native`，默认分支 `native`）。Flutter 正线在另一个仓：`iop666/QuizSyncAI`（默认分支 `flutter`）。
> 两条线**长期并存、互不影响**：各自独立历史、独立版本号、独立发布、独立构建链。本仓是并行实现线，逐步追平正线功能。

**一句话**：局域网内的跨端搜题工具。Windows 截屏 → 多模态 AI 解题 → 结果同步到 Android；Android 悬浮球截屏 → 上传 Windows 分析 → 结果回传两端显示。

本文件是 agent 的唯一入口。开工前按顺序读：本文件 → `docs/NATIVE-2.0-PLAN.md` → `docs/DECISIONS.md` → `docs/adr/` → 需要时再读 `docs/SPEC.md` / `docs/protocol.md` / `docs/data-model.md` / `docs/ai-contract.md`。

---

## 0. 无人值守工作守则（最重要）

你将在**没有人可以提问**的情况下持续工作。因此：

1. **不要提问、不要等待确认。** 遇到文档未定义的选择，按此优先级自行决定：

   ① 与 `docs/` 下的契约保持一致 → ② 选最小可行实现 → ③ 把选择与理由追加到 `docs/DECISIONS.md` 的「实施期决策」表格，然后继续往下做。

2. **每轮结束提交一次**：跑通该轮的验收命令 → 把实际输出摘要追加到 `docs/progress.md` → `git add -A && git commit -m "第 N 轮: <一句话>"`。不要把两轮混进一个提交。

3. **不得为了让测试通过而删改测试或放宽断言。** 测试确实写错时可以改，但必须在 `docs/DECISIONS.md` 里写清哪个测试、为什么原断言是错的。

4. **不得为了迁就实现而修改 `docs/` 下的四份契约**（`SPEC.md` / `protocol.md` / `data-model.md` / `ai-contract.md`）。契约本身有误时，改契约并在 `docs/DECISIONS.md` 记录原因。

5. **核心逻辑必须能在本机跑单测。** 纯逻辑（协议、解析、同步、状态机）一律写进可测的层，平台通道只做「取字节 / 显示窗口」这一层。

6. **依赖与协议**：
   - 协议结构**只在** `protocol/` 定义（已并入本仓）：不复制 schema 再改一份。
   - **唯一 Host** = `server/`（独立进程 `QuizSync.Server.Cli`，或经 `QuizSync.ServerBridge` 内嵌同一个 `QuizSync.Server.Core` 库）。
   - 安卓端与 Windows 端**不互相引用**：Windows 代码不进安卓，安卓代码不进 Windows。

7. **不要安装需要管理员权限或需要注册账号的软件。** 依赖一律走 NuGet / Gradle 镜像。打包工具缺失时跳过该步骤并记录。

8. **协议版本与应用版本是两回事**：`"protocol": "2.0.0"` 由 `protocol/` 定义，**不随应用版本（`x.y.z-native`）改动**。

---

## 1. 环境事实（2026-10-12 复核）

| 项 | 状态 |
|---|---|
| .NET SDK | ✅ **10.0.401**，位于 `C:\Program Files\dotnet`（已在 PATH） |
| Windows 目标框架 | `net10.0`；App 工程为 `net10.0-windows10.0.26100.0` |
| JDK（用于 Gradle） | ✅ 17.0.18，位于 `D:\Windows\Apps\Java\jdk-17.0.18`，**调用 `gradlew` 前需先 `export JAVA_HOME`** |
| 系统 PATH 里的 java | ⚠️ 版本 **26**。不要用它跑 Gradle，AGP 不支持 |
| Android SDK | ✅ 36.0.0（platform **android-36**、build-tools 36.0.0），位于 `D:\Windows\Apps\Android\Sdk` |
| adb | ✅ `D:\Windows\Apps\Android\Sdk\platform-tools\adb.exe` |
| Android 设备 | ⚠️ 当前 `adb devices` 为空。真机型号 `23046RP50C`（Android 15 / API 35 / arm64-v8a，adb `e5b1641d`）在接入时可用 |
| AGP / Gradle / Kotlin | AGP **8.13.2**、Kotlin **2.4.0**（Compose 编译器随 Kotlin 版本） |
| 安卓 SDK 目标 | `compileSdk 36` / `minSdk 26` / `targetSdk 36` |
| 安卓包名 | 正式 `com.quizsync.android.native`，开发 `.native.dev`（与 Flutter 线的 `com.quizsync.android` 三者并存） |
| Windows UI 框架 | WinUI 3 / Windows App SDK 1.7（`Microsoft.WindowsAppSDK`） |
| 本机 PowerShell | ⚠️ **5.1**（不是 pwsh 7）。`Get-Content`/`Set-Content` 默认按 GBK 读写，**用它改 UTF-8 源码会把中文改成乱码**。改文本一律用编辑器工具或 Python（显式 `encoding="utf-8"`） |
| `tools\*.ps1` 编码 | **必须存成 UTF-8 with BOM**：本机 PowerShell 5.1 无 BOM 时会把 UTF-8 中文按 GBK 解码，脚本里的中文字符串会把解析器搞崩。用 Python 写 `encoding='utf-8-sig'` |
| 读原生命令输出的陷阱 | 脚本里**不要用 `2>$null`** —— 脚本开头是 `$ErrorActionPreference = "Stop"`，第一行 stderr 就会终止整个脚本。正确写法：临时 `Continue` + `2>&1` + 只保留 `$_ -is [string]` 的行 |

---

## 2. 技术栈（已锁定）

| 层 | 选型 |
|---|---|
| Windows 界面 | WinUI 3 / Windows App SDK 1.7（.NET 10） |
| Windows 服务端 | `QuizSync.Server.Core`（库，可内嵌；独立进程为 `QuizSyncServer`） |
| Android 界面 | Jetpack Compose（Material 3 + adaptive） |
| Android 本地数据 | androidx.sqlite `BundledSQLiteDriver`（自带 FTS5 + trigram） |
| Android HTTP | OkHttp 4.12.0（Android 上没有 `java.net.http`） |
| 序列化 | kotlinx-serialization-json / System.Text.Json |
| 端到端同步 | Lamport 时钟 + 逐字段 LWW + 软删除墓碑 + 离线队列（与 Flutter 线同算法） |

**架构地基**（详见 `docs/adr/0001-host-provider-boundary.md`）：

- **Host 唯一化**：Server（独立进程或内嵌同一个 `QuizSync.Server.Core` 库）= 唯一 Host，负责监听、配对、设备与令牌、图片 Blob、任务队列、op 中继与水位。Server 对 ops **不透明**，不认识 `Question` / `Session`。
- **Provider 承担截屏 / AI / 热键 / 悬浮窗 / UI**：全在 Windows 客户端。热键（`F8` 截屏识别 / `F9` 多页）**归客户端**，Server 永不注册热键。

---

## 3. 仓库结构

```
D:\ZCode\QuizSync_Native\        （原生线仓库，GitHub: iop666/QuizSyncAI_Native）

├── AGENTS.md                  ← 本文件
├── CONTRIBUTING.md            ← 给人看的贡献指南
├── README.md                  ← 面向使用者
├── .gitignore
├── docs/
│   ├── NATIVE-2.0-PLAN.md     ← 原生 2.0 唯一执行依据
│   ├── DECISIONS.md           ← 已定决策 + 实施期决策追加表
│   ├── adr/                   ← 三份架构决策记录
│   ├── SPEC.md                ← 功能规格（与 Flutter 线共享契约）
│   ├── protocol.md            ← 局域网协议契约
│   ├── data-model.md          ← SQLite 表结构与同步算法
│   ├── ai-contract.md         ← prompt 全文 + AI 输出 JSON schema + 标绿规则
│   ├── progress.md            ← 每轮完成记录与验收输出
│   └── milestones.md          ← 历史里程碑
├── windows/                   ← .NET 解决方案（QuizSync.Windows.slnx）
│   ├── QuizSync.App/          ← WinUI 3 桌面客户端
│   ├── QuizSync.Core/         ← 核心（协议 / 模型 / prompt / 解析）
│   ├── QuizSync.Data/         ← 本地数据（SQLite + LWW 落地）
│   ├── QuizSync.Host/         ← 主机宿主
│   ├── QuizSync.Provider/     ← Provider（截屏 / AI / 热键 / 悬浮窗）
│   ├── QuizSync.Provider.Cli/ ← Provider 命令行入口
│   ├── QuizSync.ServerBridge/ ← Host 客户端（连内嵌/独立 Host）
│   └── QuizSync.Tests/        ← 单测（含 DartReference/ 基准文件）
├── android/                   ← Kotlin/Gradle 原生工程
│   ├── app/                   ← Compose 应用
│   ├── core/                  ← JVM 库（同步引擎 / HostApi）
│   └── baselineprofile/       ← Baseline Profile 采集
├── protocol/                  ← 通信规范（原独立仓 QuizSyncProtocol，已并入）
│   ├── spec/ schema/ versions/  ← 规范、JSON Schema、版本策略
│   └── conformance/vectors/   ← 一致性测试向量（NDJSON）
├── server/                    ← 唯一 Host（原独立仓 QuizSyncServer，已并入）
│   ├── src/QuizSync.Server.Core/  ← 宿主库（内嵌 Kestrel，可被客户端内嵌）
│   ├── src/QuizSync.Server.Cli/   ← 独立进程入口
│   └── tests/                     ← 服务端测试（含向量回放）
├── tools/                     ← 真机验证与打包脚本
├── icon/                      ← 应用图标与悬浮球图
├── design/                    ← 设计 token
└── keystore/                  ← 安卓签名（**绝不公开**）
```

> **本仓自包含**：`protocol/` 与 `server/` 是原独立仓的源码并入（见 `docs/DECISIONS.md`）。构建与测试**不再需要外部兄弟仓**；如需指向外部 checkout，各 csproj 保留 `-p:QuizSyncServerCoreProject=<路径>` 覆盖点，运行时可用 `QS_PROTOCOL_DIR` / `QS_SERVER_DLL` / `QS_VECTORS` 覆盖。

---

## 4. 构建与验收命令

```bash
# Windows 客户端（.NET）
cd windows
dotnet build QuizSync.Windows.slnx -warnaserror
QS_DOTNET="$PROGRAMFILES/dotnet/dotnet.exe" dotnet test QuizSync.Tests/QuizSync.Tests.csproj

# 服务端（在 server/ 下，已并入本仓）
cd ../server
dotnet publish src/QuizSync.Server.Cli -c Release -o src/QuizSync.Server.Cli/bin/Release/publish
dotnet test tests/QuizSync.Server.Tests/QuizSync.Server.Tests.csproj

# Android（需先设 JAVA_HOME 指向 JDK 17）
export JAVA_HOME="D:\Windows\Apps\Java\jdk-17.0.18"
cd ../android
./gradlew :app:assembleDebug
./gradlew :core:test                   # JVM 单测
./gradlew :app:connectedAndroidTest    # 真机仪器测试（需接入设备）
```

> ⚠️ **`QS_DOTNET` 必须设置**：部分测试（`ProviderCliTests` / `HostLauncherTests` 等）要起真实 dotnet 进程，
> 而测试进程的 PATH 里没有 dotnet。本机：`QS_DOTNET="/c/Program Files/dotnet/dotnet.exe"`（bash）或 `%ProgramFiles%\dotnet\dotnet.exe`（cmd）。
>
> ⚠️ **不要用系统 PATH 里的 java 26 跑 Gradle**，AGP 不支持。
>
> ⚠️ **跨实现 E2E 需要先 publish 服务端**：`server/src/QuizSync.Server.Cli/bin/Release/publish/` 存在后，
> `ServerBridgeEndToEndTests` 等用例才会绿（或用 `QS_SERVER_DLL` 指定）。

---

## 5. 里程碑索引

原生线的进度记录见 `docs/progress.md`（「Phase N」与「第 N 轮」两种计数）。关键节点：

- **Phase 0**：冻结 Flutter 基线（tag `v1.1.0-flutter`）
- **Phase 2–5**：协议 v2 与 v1 兼容层、Kotlin 同步引擎、Compose 安卓端、.NET 核心移植
- **第 167–170 轮**：主机内嵌进应用、连接服务一键化、悬浮窗与安卓 UI 收口

---

## 6. 全局非目标（写了就不要做）

- 不做公网访问、不做 HTTPS、不引入账号体系 / 云服务 / SaaS。
- 不把平台通道当业务层：核心逻辑必须在可跑单测的层。
- 不改四份共享契约来迁就实现。
- 不删改测试以让构建变绿。

---

## 7. 全局完成定义（DoD）

一轮工作完成 = ① 该轮验收命令全绿（`dotnet test` / `gradlew test` / 真机探针）→ ② 输出摘要追加进 `docs/progress.md` → ③ 一次提交（`第 N 轮: <一句话>`）→ ④ 涉及决策的补进 `docs/DECISIONS.md`。
