# ADR 0002 · 协议 v2 的路由、版本协商与 v1 兼容层

- **状态**：已接受（2026-09-26，随 D1 一并拍板）
- **决策编号**：D2（协议侧，与热键决策 D2 同名，本文指协议；热键决策见计划 §2.3）
- **相关**：`docs/NATIVE-2.0-PLAN.md` §4；`docs/protocol.md`（v1 契约，保留）

## 背景

`docs/protocol.md` 与实现已分叉，且版本协商本身是错的：

| 现状 | 问题 |
|---|---|
| 路由前缀统一 `/api/v1/`，无主版本区分 | 无法同时服务新旧客户端；无法在协议层面演进 |
| 426 版本协商比较 `X-QS-Client-Version` 的**主版本** vs 服务端 appVersion | **比错对象**：产品版本与协议版本是两件事，客户端升个补丁版本就可能被误判不兼容 |
| **不带版本头的请求一律放行** | 协商形同虚设 |
| 文档要求握手带 `X-QS-Device-Id` | 服务端**不读**它（device_id 由 token 反查） |
| 文档里的服务端 `ops` 事件 | **未实现** |
| 文档里的网络错误重试 1s/3s/10s | `ApiClient` **完全没有**实现 |
| `capabilities` 是硬编码数组 | 客户端不按它分支，协商没有实际作用 |

另外，D1 采纳后「任务流水线合一」会改变本机任务的语义，而 Flutter 1.1.0 客户端依赖旧语义 —— 手机端更新必然滞后。

## 决策

1. **URL 路径承载协议主版本**：`/api/v2/...`；响应头 `X-QS-Protocol: 2.<minor>` 携带完整协议版本。
2. **协商依据是 protocolVersion，不是 appVersion**：主版本不同 → `426 protocol_mismatch`（请求体给出双方支持的区间）。**对所有请求生效，包括没带版本头的**（默认按最低支持版本处理）。
3. **同主版本内只增不改不删**：未知字段**必须忽略**；字段废弃用 `deprecated_since` 标记，并至少保留一个大版本。
4. **Server v2 必须同时提供 `/api/v1/*` 兼容子集**：`info`、`pair`、`images`（POST/GET）、`tasks`（POST/GET/retry）、`tasks/active`、`collections`（含 select）、`sessions/*/reanalyze`、`sync/ops`（推/拉）、`sync/snapshot`、`devices`（GET/DELETE）、`/ws`。行为与 Flutter 1.1.0 客户端**逐字兼容**。
5. **v1 兼容层必须继续满足的旧语义**（v2 内部实现可以统一，但适配器不可改变对外行为）：
   - `GET /tasks/active` 的 `done` **直接带 `session`**；
   - 本机截屏任务「不经任务队列」、`task_id == session_id` 的等价表现；
   - 合集删除方向不对称（主机删 → 手机不跟随删，`applyRemoteCollectionDeletes=false` 的产品语义进规范）；
   - 缓存与同图去重**只对单页生效**、去重要求页序完全一致；
   - 多页硬上限 6。
6. **文档-实现分叉的处置**：以**实现为准**写进 `versions/v1-snapshot.md`（`X-QS-Device-Id` 标为「可选、服务端不读」；`ops` 事件在 v2 实现、v1 快照标为「未实现」；重试 1s/3s/10s 在 v2 落地并进入向量）。
7. **能力协商生效**：`GET /api/v2/info` 的 `capabilities` 由服务端**算出来**（例如是否配置了 Provider、是否支持分块上传），客户端按能力分支而非按版本号猜。
8. **安全修复一并进 v2**：`devices` / `snapshot` / `rekey` 等主机专属操作要求 `role=host`；图片下载强制 64 位 hex 白名单；所有 JSON 端点加体积上限（默认 1MB / ops 批量 8MB）；token 支持 `expires_at`。

## 理由

- **路径带主版本**是最可观测、最容易路由与排障的做法（curl 一眼看出；网关/日志天然可分）。
- **两代并存**是迁移期的硬需求：用户的手机不可能和电脑同时升级。
- **只增不改 + 忽略未知字段**让「Android v2.1 + Windows v2.0」这种组合天然可用（用户明确要求）。
- 把分叉**以实现为准**写进 v1 快照，比「改代码迎合文档」风险低：现有客户端正在跑的就是实现。

## 后果

**正面**：新旧混装可用；协议演进有明确规则；版本误判消失；越权与无上限等安全问题在协议层解决。

**代价**：Server 需要维护一个 v1 适配器（约一个 HTTP 层的映射模块）；节点需处理 4 种组合（新/旧客户端 × 新/旧宿主），Phase 7 必须做组合矩阵回归。

## 验收

1. Flutter 1.1.0 客户端对 v2 Server 完成全链路：配对 → 上传 → 建任务 → WS 收结果 → 轮询兜底 → ops 同步 → 吊销后 401 `revoked`。
2. `conformance/vectors` 里 v1 组与 v2 组分别全绿；v1 组由现有 `quizsync_core` 参考实现回放通过。
3. 带错误的主版本号请求得到 `426 protocol_mismatch`（含双方版本区间）；不带版本头的请求按最低支持版本处理而不是放行。
4. `role=android` 的 token 调用 `/api/v2/devices`、`/api/v2/sync/snapshot` 被拒（403 `forbidden_role`）。
5. 图片下载传非法 hash（非 64 位 hex、含路径分隔符）被拒，不发生路径拼接。
