<div align="center">

<img src="icon/QuizSync_AI.png" width="104" alt="AI 双端搜题">

# AI 双端搜题 · QuizSync AI（原生版）

**电脑截屏、手机截屏 → AI 读题作答 → 答案同时出现在电脑和手机上**

局域网内两台设备直连，不经过任何第三方中转服务器，也不需要注册账号。

**原生实现**：Windows 用 WinUI 3（.NET 10），Android 用 Jetpack Compose（Kotlin）

</div>

---

## 关于这个仓库

QuizSync AI 有**两条长期并存、互不影响**的实现线：

| 线 | 仓库 | 技术栈 | 版本号 |
|---|---|---|---|
| **Flutter 正线** | [`iop666/QuizSyncAI`](https://github.com/iop666/QuizSyncAI) | Flutter 3 / Dart 3，两端 + 纯 Dart 共享核心包 | `x.y.z`（如 `1.2.1`） |
| **原生线（本仓）** | [`iop666/QuizSyncAI_Native`](https://github.com/iop666/QuizSyncAI_Native) | Windows：WinUI 3（.NET 10）· Android：Jetpack Compose（Kotlin） | `x.y.z-native`（如 `1.2.1-native`） |

- 两条线**独立仓库、独立历史、独立版本号、独立发布**。
- 版本号约定：原生版在版本号后加 **`-native`** 后缀，与 Flutter 版一眼分得清（两边功能对等时号也齐）。
- 两线共享同一套**协议与数据契约**（见 `docs/` 下的 `protocol.md` / `data-model.md` / `ai-contract.md` / `SPEC.md`），所以两条线的电脑端与手机端可以互相通信。

> **想要稳定使用的版本，请到 Flutter 正线下载**。本仓是并行推进的原生实现线。

---

## 这是什么

一个**在局域网里跨设备搜题**的小工具。它把「截屏 → 多模态 AI 读题 → 显示答案」这条链路做成两端协同：

```
电脑截屏（热键 / 悬浮球 / 悬浮窗 / 剪贴板）
        │
        ▼
   多模态 AI 读题（题目 / 选项 / 材料 / 主观题结构化）
        │
        ├──────────────► 电脑端：当场显示答案与解析
        │
        └──── 局域网 ──► 手机端：自动刷新出同一条结果，可离线回看

手机截屏（悬浮球 / 相册选图）
        │
        ▼
   上传到电脑 → 电脑端 AI 分析 → 结果回传手机（电脑端同步留档）
```

**典型场景**：电脑上做网课作业 / 题库练习，想快速看答案与解析；或者用手机对着题目截屏上传给电脑上的 AI —— 两边看到的永远是同一份结果。

> 答案由 AI 生成，仅供参考；请遵守所在学校 / 机构关于作业与考试的规定。

---

## 架构

**Host / Provider 分离**（详见图纸 `docs/adr/0001-host-provider-boundary.md`）：

```
┌─────────────────────────────────────────────────────┐
│  Windows 客户端（Provider）                          │
│  截屏 · 热键 · 悬浮球/悬浮窗 · AI 调用 · 界面         │
└───────────────────────┬─────────────────────────────┘
                        │ 内嵌 或 独立进程
┌───────────────────────▼─────────────────────────────┐
│  Host（唯一）：监听 · 配对 · 设备令牌 · 图片 Blob     │
│  任务队列 · op 中继与水位（对 ops 不透明）            │
└───────────────────────┬─────────────────────────────┘
                        │ 局域网 HTTP + WebSocket
┌───────────────────────▼─────────────────────────────┐
│  Android 客户端（Compose）：三 tab · 悬浮球 · 历史     │
└─────────────────────────────────────────────────────┘
```

| 项目 | 说明 |
|---|---|
| `windows/QuizSync.App` | WinUI 3 桌面客户端（主界面与设置） |
| `windows/QuizSync.Provider` | 截屏 / 热键 / 悬浮窗 / AI 调用 |
| `windows/QuizSync.Host` | 主机宿主（可内嵌进客户端） |
| `windows/QuizSync.Core` | 核心：协议模型、prompt、解析 |
| `windows/QuizSync.Data` | 本地数据：SQLite + 逐字段 LWW |
| `windows/QuizSync.ServerBridge` | 连接 Host 的客户端封装 |
| `windows/QuizSync.Provider.Cli` | Provider 命令行入口 |
| `android/app` | Jetpack Compose 安卓应用 |
| `android/core` | Kotlin 同步引擎与 HostApi |

---

## 构建

需要 **.NET SDK 10**、**JDK 17**、**Android SDK 36**。

```bash
# Windows
cd windows
dotnet build QuizSync.Windows.slnx -warnaserror
dotnet test QuizSync.Tests/QuizSync.Tests.csproj

# Android（JAVA_HOME 必须指向 JDK 17）
export JAVA_HOME="D:\Windows\Apps\Java\jdk-17.0.18"
cd android
./gradlew :app:assembleDebug
./gradlew :core:test
```

---

## 文档

| 文件 | 内容 |
|---|---|
| `docs/NATIVE-2.0-PLAN.md` | 原生 2.0 架构审计与迁移计划（唯一执行依据） |
| `docs/DECISIONS.md` | 全部已定决策与实施期决策 |
| `docs/adr/` | 三份架构决策记录（Host/Provider 边界、协议 v2 兼容、DB 与打包） |
| `docs/SPEC.md` | 功能规格（与 Flutter 线共享） |
| `docs/protocol.md` | 局域网协议契约 |
| `docs/data-model.md` | 数据模型与同步算法 |
| `docs/ai-contract.md` | AI 契约与输出格式 |
| `docs/progress.md` | 逐轮进度与验收证据 |

---

## 许可

本项目代码以 [MIT 许可证](LICENSE) 开源。随包分发的第三方资源（字体、图标字体等）按各自条款使用。
