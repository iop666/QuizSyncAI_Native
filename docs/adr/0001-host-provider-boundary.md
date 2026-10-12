# ADR 0001 · Host 与 Provider 的职责边界

- **状态**：已接受（2026-09-26，用户拍板）
- **决策编号**：D1
- **相关**：`docs/NATIVE-2.0-PLAN.md` §2、§3、§8；本 ADR 取代 `docs/DECISIONS.md` 中 M47 ③ 的处置方式

## 背景

原架构里「服务端」有两份：

1. Windows 桌面端（`apps/desktop`）内嵌 `packages/quizsync_core/lib/server/`（shelf，15 个 HTTP 端点 + `/ws`），同时承担截屏、AI、UI、本地库；
2. 独立 `server/`（纯 Dart CLI，1.0.1），是 core 的 **20 文件逐字复制** + 服务端第二份实现，**已经漂移**（少 `syncPageSize` / `maxQueueDepth`，走独立版本线）。

规划中的三仓库（`QuizSyncAI` / `QuizSyncProtocol` / `QuizSyncServer`）必须先回答：**Host 角色（谁监听端口、谁配对、谁存图片、谁管设备）住在哪个进程**。备选方案有三：

| | A：Server 是唯一 Host | B：Server 只是中转 | C：Host + 截屏 + AI 全在 Server |
|---|---|---|---|
| GUI 关闭后手机 | 可配对/看历史/同步，不能识别 | 完全连不上 | 全部可用 |
| AI Key 归属 | 客户端（DPAPI + 设置页 UI） | 客户端 | Server（只能 CLI/配置文件） |
| 实现份数 | 1 份 Host | 1 份 | 1 份 Host，但截屏/热键/AI 各两份 |
| Server 能否冻结 | 能（不认识业务实体） | — | 不能（跟着 AI/截屏需求改） |
| 第三个仓库是否有意义 | 有 | **没有** | 有 |

## 决策

**采用 A。**

- `QuizSyncServer` = 唯一 **Host**：监听与传输、配对与鉴权、设备与令牌注册表、图片 Blob、任务队列与结果缓存、op 中继与水位、状态/配置/日志、`recognition_job` 派发。它对 ops **不透明**，不认识 `Question` / `Session` / 合集语义。
- `QuizSyncAI` Windows 客户端 = **Host 的客户端 + Provider**：截屏、热键、剪贴板、AI 调用（Key 在此）、结果解析与标色、悬浮窗/悬浮球/托盘、主窗口 UI、本地权威库 + **LWW applier**。
- `QuizSyncAI` Android = **Host 的客户端**（不内嵌任何 Host 逻辑）。
- 客户端启动先探活（`runtime.json` + `GET /api/v2/info`）：有独立 Server → 当客户端；没有 → **内嵌 `QuizSync.Server.Core` 起进程内宿主**（同进程直通，零 IPC）。两者**互斥**：命名互斥体 + 端口所有权，任意启动顺序下都只有一个 Host。
- **热键归 Provider**（与决策 D2 一致）：Server 永不注册热键。
- **无界面识别能力**由客户端的 `--headless` 模式承接（不开主窗口，只做热键 + 截屏 + AI + 任务处理）。**不把 AI 塞进 Server。**

## 理由

1. **三个仓库都有实质职责**；B 会让 `QuizSyncServer` 变成一个没有存在意义的仓库，与「最终形成三个长期维护仓库」的目标冲突。
2. **Server 真的能冻结**：它只在**协议版本**变化时才需要更新，符合「轻量、功能冻结、尽量不频繁更新」。
3. **AI Key 留在有 UI 的地方**：C 的致命点不是架构美感，而是「用户怎么改 Key」—— 今天的体验是设置页填写、遮挡显示尾 4 位、点保存有反馈；Key 若在 Server，就要另做一套无 UI 的配置体验。
4. **避免双份实现**：C 会让「截屏 + 热键 + AI」出现两份（Server 一份、客户端一份），正是本次审计抓到的 `server/lib/src/core/` 20 文件复制漂移问题的放大版。
5. **能力不丢**：C 唯一优于 A 的「GUI 关掉也能识别」由 `--headless` 承接（同一份 Provider 代码）。

## 后果

**正面**
- 一份 Host 实现；Server 可冻结；AI Key 只存在于有 UI、可管理的地方。
- **任务流水线合一**：所有识别（含桌面自己按 F8 的）都走一条 task 流水线，消灭今天的特例 —— `ActiveTask.hostInitiated`、「本机截屏任务不入队、库里没有 tasks 行」、`task_id == session_id`。
- 手机在电脑 UI 退出后仍可配对/看历史/收同步（用户体验提升）。

**代价（如实记录）**
- Windows 客户端从此是「Host 的客户端」，**必须实现 LWW applier** 以接收 Android 产生的 ops（语义已被 `core` 的 212 项测试钉死）。
- 安装包里出现两个进程（内嵌时可退化成一个）；首次安装可能触发一次防火墙询问（端口 8765）。
- 新增协议消息 `recognition_job` / `recognition_result` / `provider_online` / `provider_offline`；无 Provider 时必须有明确的 `provider_offline` 语义（≤2s 返回，不挂死）。
- **契约级兼容风险**：任务流水线合一改了本机任务语义，`protocol.md §3.3.1` 的 `/tasks/active` 旧语义（`done` 直接带 `session`）必须由 **v1 兼容层**继续满足，否则 Flutter 1.1.0 手机会异常。逐条清单见计划 §4.6。

## 被取代的旧决策

- `docs/DECISIONS.md` M47 ③：「`server/lib/src/core/` 20 文件逐字节复制（有意冻结，不换成 path 依赖）」—— 当时正确（避免把主项目耦合进独立产品）；本次三仓库拆分后由本节结论取代：新 Server **不复制**任何实现，Host 逻辑只存在于 `QuizSyncServer` 仓库，客户端以内嵌库或客户端接入。
- 原「Windows 端作为唯一服务端」（`DECISIONS.md` 3.3）的落地方式调整：服务端角色移出桌面进程，成为独立 Host（可内嵌）。

## 验收

1. 任意启动顺序（先 App、先 Server、同时）下，始终只有一个进程监听 8765，且客户端表现为客户端模式。
2. `--headless` 模式下，热键 → 截屏 → AI → 结果广播全链路可用（无主窗口）。
3. 停掉客户端进程后，手机仍能完成：重连、`/tasks/active` 轮询、历史拉取、`sync/ops` 增量同步。
4. 停掉 Provider 时，识别类请求 ≤2s 返回 `provider_offline`，任务保留不丢。
5. 内嵌模式与独立模式对同一套协议向量输出**逐字段一致**（时延差异记录在案）。
