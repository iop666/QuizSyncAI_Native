# 贡献指南（QuizSync AI · 原生线仓库）

> 给 agent 的执行入口是 [`AGENTS.md`](AGENTS.md)（环境事实、平台实测坑、工作守则）；本文件是给人看的摘要：仓库定位、依赖方向、提交规范、版本号来源、发布红线。
>
> **本仓是原生线**（Windows .NET + Android Kotlin）。Flutter 正线在另一个仓：`iop666/QuizSyncAI`。

## 1. 两条线的关系

QuizSync AI 有**两条长期并存、互不影响**的实现线：

| 线 | 仓库 | 定位 | 版本号 |
|---|---|---|---|
| **Flutter（正线）** | `iop666/QuizSyncAI`（默认分支 `flutter`） | 主推产品线：Flutter/Dart 双端 + 共享核心包，持续演进 | `x.y.z`，如 `1.1.0` |
| **原生（本仓）** | `iop666/QuizSyncAI_Native`（默认分支 `native`） | 并行实现线：Windows .NET（WinUI 3）+ Android Compose，逐步追平 | `x.y.z-native`，如 `1.1.0-native` |

- 两仓**各自独立**：独立历史、独立版本号、独立发布、独立构建链。互不引用、互不阻塞。
- 版本号约定：原生线在主版本号后加 **`-native`** 后缀（例：Flutter `1.2.1` → 原生 `1.2.1-native`），两边功能对等时号也齐。
- 两线共享**四份契约**（`SPEC.md` / `protocol.md` / `data-model.md` / `ai-contract.md`）：本仓各有一份副本，改动须与 Flutter 线保持一致，否则以契约为准。
- **本仓额外自带** `protocol/`（通信规范：spec / schema / 一致性向量）与 `server/`（唯一 Host 实现）—— 这两部分原为独立仓，现已并入，使原生线构建与测试自包含。

## 2. 依赖方向

**本仓自包含**：协议规范与 Host 服务端源码都并入本仓，构建与测试不需要外部兄弟仓。

```
windows/ ──► protocol/                规范 + JSON Schema + 一致性向量（纸面与数据）
windows/QuizSync.ServerBridge ──► server/src/QuizSync.Server.Core   内嵌模式的 Host 能力（库）
android/ ──► protocol/                建库用的 schema 与一致性向量
server/tests ──► protocol/conformance/vectors   向量回放
```

- 协议结构**只在** `protocol/` 定义：本仓库不复制 schema 再改一份。
- 安卓端与 Windows 端**不互相引用**：Windows 代码不进安卓，安卓代码不进 Windows。
- Host 与 Provider 的边界见 `docs/adr/0001-host-provider-boundary.md`。
- 需要指向外部 checkout 时的覆盖点：`-p:QuizSyncServerCoreProject=<路径>`（构建期）、`QS_PROTOCOL_DIR` / `QS_SERVER_DLL` / `QS_VECTORS`（运行期）。

## 3. 分支

| 分支 | 用途 |
|---|---|
| `native` | **默认分支，主线**：原生线全部源码 |
| `feat/*`、`chore/*` | 特性与杂务 |

> 历史沿革：本仓原名 `QuizSyncAI`，其 `main` 分支（仅第 21 轮）已删除，工作成果以一次快照提交进入 `native`。

## 4. 提交规范

- 轮次提交（原生线沿用「第 N 轮」计数）：`第 N 轮: <一句话>`。
- 非轮次提交：`<域>: <做什么>`，域取 `docs` / `android` / `windows` / `tools` / `chore`。
- 中文提交信息。提交前必须跑通 `dotnet test`（Windows）与安卓对应测试。

## 5. 版本号唯一来源

| 位置 | 说明 |
|---|---|
| `android/app/build.gradle.kts` → `versionName` | 安卓应用版本，如 `1.1.0-native` |
| `android/build.gradle.kts` → `allprojects.version` | Gradle 工程版本 |
| `windows/` 各工程的 `app_version` / `X-QS-Client-Version` | 客户端版本，须与上面一致 |

> ⚠️ **协议版本与应用版本是两回事**：`ProviderProtocol` 里 `"protocol": "2.0.0"` 与 `ProviderCliTests` 中的协议断言属于**协议版本**，由 `QuizSyncProtocol` 仓定义，**不随应用版本改动**。

## 6. 发布红线

1. 交付物**不得包含 `userdata`**（运行期数据）：打包前强制删除并复核。
2. 交付物**不得出现内部轮次字样与出包序号**；界面不显示构建号。
3. Windows 便携包必须随包带上 VC 运行库（排除 `\onecore\` 目录），否则干净机器上双击即缺 DLL。
4. Android 交付物必须通过对应审计（包名 / 版本 / 权限白名单 / 备份策略 / 明文开关 / 签名）。
5. `tools/*.ps1` **必须存成 UTF-8 with BOM**（本机 PowerShell 5.1 无 BOM 会把中文按 GBK 解码导致解析崩）。
6. 公开仓库**只接受裁剪导出**：禁止直推内部全历史分支；`native` 禁强推/删除。
7. 局域网内**不做 HTTPS**、不引入账号体系 / 云服务 / SaaS / 公网访问。
