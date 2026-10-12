# 05 WebSocket

> 状态：v1 现状（**按实现反推**——以当前 Windows 内置服务端的真实行为为准）+ v2 目标（凡标「v2 变更」的句子**均未实现**，只是方向）。
> 强制级别：**MUST**（两端已依赖、不得偏离）/ **SHOULD** / **MAY**。
> 约定：命名、时间与未知字段规则见 `01-conventions.md`；正文只出现协议词汇，不出现任何一端的语言、框架、包名与仓库内路径。

## 1. 连接与鉴权

### 1.1 握手

- **MUST** 与 HTTP **共用同一个端口**，路径固定 `GET /ws`；这是唯一的 WS 端点。
- **MUST** 令牌走请求头 `Authorization: Bearer <token>`（与 HTTP 完全一致）；**MUST NOT** 用查询参数、子协议或首帧消息携带令牌——现状服务端只读这一个头。
- **SHOULD** 同时带 `X-QS-Device-Id` 与 `X-QS-Client-Version`（现状客户端都带），但 **MUST NOT** 依赖它们：设备身份一律由 token 反查，`X-QS-Device-Id` 被忽略；`/ws` **不做**版本协商（主版本不一致也能建连，也没有 `X-QS-Server-Version` 响应头），见 `02-transport.md` 1.8、`10-versioning.md`。
- **MUST** 一条连接只对应一个 `device_id`（由 token 决定，全程不变）；握手不携带第二个身份。
- **MUST** 握手失败返回 **401** + 通用错误体 `{"code":…,"message":"WS 握手鉴权失败","retry_after_seconds":null}`：token 已吊销时 `code` 为 `"revoked"`，缺失或认不出时 `"unauthorized"`。
- **MUST** 客户端不把 401 当瞬时故障：按 3.2 停止重连并提示重新配对。

### 1.2 连上以后服务端先发什么（现状）

- **MUST** 连接建立后服务端发 `hello`，恰好 5 个键：`type`(`"hello"`) / `server_device_id`(str，**服务端自己**的 id，不是对端的 id) / `protocol_version`(int) / `active_collection_id`(str｜null) / `active_collection_name`(str｜null)。两个合集字段**键恒在**、可为 `null`。
- **MUST** 客户端用 `hello` 建立「主机是谁 + 当前合集是什么」；`active_collection_id` 为 `null` 时 **MUST NOT** 发起识别（服务端另有 `409 no_active_collection` 兜底，见 `04-http-api.md` 1.3.5）。
- 现状：`hello` 由异步任务发出，不是绝对的第一帧（与之并发的广播理论上可能先到）。客户端 **MUST NOT** 依赖「`hello` 一定是收到的第一条消息」。
- **MUST** 若服务端手上有**进行中**的本机任务（不经任务队列、`task_id` = `session_id`），建连后补发一次 `task_update`；**只补进行中**，已完成/失败的不补（否则每次重连都会跳回上一次结果）。该补发走广播通道，因此同在场的其它连接也会再收到一次。
- **MUST** 客户端在连上后发自己的 `hello`（见 2.2）；服务端只为该连接刷新 `last_seen_at` 与 `app_version`，**忽略**其中的 `device_id` 与 `platform`。

## 2. 事件清单

信封：一条 WS 文本帧 = 一个 JSON 对象，`type` 是唯一判别键（现状两端只发对象；收到非对象帧一律丢弃，见 2.2）。

### 2.1 服务端 → 客户端

| `type` | 触发时机 | 关键字段 |
|---|---|---|
| `hello` | 连接建立后（见 1.2） | `server_device_id` / `protocol_version` / `active_collection_id` / `active_collection_name` |
| `task_update` | 任一任务状态变化（手机提交的、主机本机截屏的都会发） | `task_id` / `status`(`queued`｜`analyzing`｜`done`｜`failed`｜`cancelled`) / `session_id`(str；**为 null 时整个键被省略**) / `image_count`(int) |
| `task_result` | **仅** `status = done` 且 `session_id` 非空、会话存在**且未删除** | `task_id` / `session`(对象) |
| `task_failed` | **仅** `status = failed` 且 `session_id` 非空 | `task_id` / `error_code`(会话不存在时 `"internal"`) / `message`(会话缺失或文案为空时 `"分析失败"`) |
| `collection_changed` | 主机切换当前合集（HTTP 选中端点或主机本机操作） | `collection_id`(str｜null) / `collection_name`(str｜null)，键恒在 |
| `device_revoked` | 任一设备被吊销 | `device_id`(被吊销的那台) |
| `ping` | 连接建立后每 30s | `ts`(int 毫秒) |

- **MUST** 广播语义：`task_update` / `task_result` / `task_failed` / `collection_changed` / `device_revoked` 一律发给**当时所有已连接客户端**；没有订阅、没有按设备过滤、也没有按任务定向。
- **MUST** `task_result.session` = 会话全字段 + `image_hashes`(str[]，页序权威) + `image_count`(int) + `questions`(对象数组)，与 `GET /api/v1/tasks/active` 的 `session` **同构**（客户端 **SHOULD** 复用同一套落地逻辑，见 `04-http-api.md` 1.4.1）；`source_device` 恒在，客户端靠它区分「自己发起的」与「主机本机发起的」（后者不自动跳结果页）。
- **MUST** `task_update.image_count` 是本次识别的页数；上报值 ≤ 0 时服务端用会话页序的条数补齐（会话不存在则保持 0）。客户端 **SHOULD** 优先用消息里的值，取不到再查本地库。
- **MUST**（**v2 变更**）客户端只在 `device_revoked.device_id` 等于自己时清本地配对。**v1 现状**：连接层比对了 id 才断开，但**消息落地层不比对 id**，收到别人的吊销也会清掉自己的配对（记入 §7）。
- **MUST NOT** 依赖服务端 → 客户端的 `ops` 事件：**实现未发出**（事件名只在旧契约里，代码里没有任何一处构造它；客户端解析层保留了该分支，是死分支）。

### 2.2 客户端 → 服务端

| `type` | 触发时机 | 关键字段 | 现状 |
|---|---|---|---|
| `hello` | **每次**连上（首次与每次重连） | `device_id` / `app_version` / `platform`(恒 `"android"`) | 生产路径发 |
| `pong` | 收到 `ping` 后立刻 | `ts`(回显 `ping.ts`) | 生产路径发 |
| `ack` | 「已收到的最大 lamport」（含来源设备） | `watermark_lamport`(int) / `watermark_device`(str) | **生产路径不发**（只有集成测试发）。服务端只读 `watermark_lamport`，`watermark_device` 被忽略；水位大于已记录值才更新，否则整条忽略 |
| `push_ops` | 「推送本地待发操作日志」 | `ops`(对象数组，元素与 `POST /api/v1/sync/ops` 同构) | **生产路径不发**：op 推送一律走 HTTP（见 `04-http-api.md` 1.3.12）；服务端有完整实现，但只被集成测试驱动过 |

- **MUST** `push_ops` 的服务端规则与 HTTP 同源：`op.device_id` 等于服务端自己的 id → 静默跳过；不等于**本连接**的 `device_id` → 静默跳过（HTTP 会计入 `rejected`，WS 不计账）；通过的 op 落库并做字段级 LWW；随后**只向该连接**回 `{"type":"ack","watermark_lamport":<本次处理过的 op 的最大 lamport；一条都没通过时为 0>}`。仅当 `ops` 是数组时才回 `ack`。
- **MUST** 无法解析的帧、未知 `type`、字段类型不对的消息一律**静默丢弃**：不断连、不报错（前向兼容）；`pong` 在服务端不触发任何动作。

### 2.3 客户端本地产出的伪事件

- 握手被 401 / 403 拒绝时客户端**不重连**，而是往自己的消息管道里塞一条本地合成的 `{"type":"auth_failed","status":<401｜403>}`（**服务端从不发这个事件**），随后按 `device_revoked` 同样处理：清配对、提示重新配对（见 3.2）。

## 3. 心跳与重连

### 3.1 心跳（现状）

- **MUST** 服务端每 **30s** 发一次 `{"type":"ping","ts":<毫秒>}`；定时器随连接建立、连接关闭或被踢时取消（每条连接一个）。
- **MUST** 客户端收到 `ping` 后立刻回 `{"type":"pong","ts":<回显 ping.ts>}`。
- **MUST NOT** 依赖服务端因 `pong` 缺失而断开：**实现里没有这个逻辑**（服务端不校验 `pong`、不做空闲超时断开）。**v2 变更**才是「服务端校验 `pong`，90s 断开死连接」。
- **MUST** 客户端自己判死连接：**超过 40s**（= 30s 心跳间隔 + 10s 容差）没收到服务端 `ping` → 关闭连接并按 3.2 重连；现状每 **5s** 检查一次。
- **MAY** 各端自行使用 WS 控制帧 ping（现状客户端每 **20s** 发一次，服务端不感知）；这是传输层细节，**MUST NOT** 当作应用层心跳（见 `02-transport.md` 1.8）。

### 3.2 重连与补偿（现状）

- **MUST** 退避序列 **1s / 2s / 5s / 10s / 30s**，此后保持 30s；**任何一次连上后重置**（下一次断线重新从 1s 开始）。
- **MUST** 握手 401 / 403 → 标记鉴权失败并**停止重连**（转轮询、提示重新配对，见 2.3）；主动关闭（用户取消配对 / 退出）同样不再重连。
- **MUST** 每次「连上」（含首次）都做一次补偿，且**不依赖**任何推送重发：
  1. 重新探测主机状态（`GET /api/v1/info`）；
  2. 跑一次完整同步：离线队列补跑 → 缺失图片补传 → **先拉后推**（拉对端 `GET /api/v1/sync/ops`，推本地 `POST /api/v1/sync/ops`）；
  3. 失效本地界面缓存（即使这轮什么都没拉到）。
- **MUST** 兜底轮询：客户端**前台**时每秒调一次 `GET /api/v1/tasks/active`，与 WS 并行、不互斥（现状恒开、无用户开关；进后台即停，回前台立即恢复并保留基线，见 `04-http-api.md` 1.4.1）。
- **MUST** 断线窗口里漏掉的 `task_update` / `task_result` **不会重发**：只能靠上面的轮询 + 完整同步找回。客户端 **MUST NOT** 把「没收到推送」当成「什么都没发生」。

## 4. 单设备踢旧

- **MUST** 同一 `device_id` 只保留一条连接：新连接建立时服务端**关闭旧连接**并取消它的心跳定时器，再登记新连接（连接表以 `device_id` 为键）。
- **MUST** 旧连接**延迟到达**的关闭通知不得误删新连接（只有「当前登记的就是这条连接」时才摘除并停心跳）。
- **MUST** 踢旧按 `device_id`、不按 token：重新配对换发 token 但 `device_id` 不变时同样适用。
- **MUST** 被踢方只表现为「连接被关闭」：按 3.2 退避重连。因此同一个 `device_id` **MUST NOT** 被两处同时使用，否则两端会互相踢、形成持续重连。
- **MUST** 被吊销的设备比被踢更进一步：服务端先广播 `device_revoked`、再关闭它的连接，此后它的 token 一律 401 `revoked`（见 `04-http-api.md` 1.3.16）。

## 5. 水位触发的只拉不推（不属于 WS）

- 分工一句话：**WS 报「刚刚发生了什么」（瞬时、可能丢），HTTP 报「现在是什么状态」（可轮询、可补偿）**；两者不互相替代。
- **SHOULD** 客户端盯 `GET /api/v1/tasks/active` 的 `ops_lamport`（= 主机 `sync_ops` 的最大 `lamport`）：**首次只建立基线**，此后一旦**变大**就做一次**只拉不推**的 `GET /api/v1/sync/ops`（`from_device` = 主机的 `server_device_id`，拉回后按 LWW 落地），不推本地 op；失败静默，等下一次水位变化或重连时再试。
- **MUST NOT** 把 `ops_lamport` 当事件通道：它只表示「主机侧有改动」，不携带内容、也不保证每次改动都被观察到（水位可跳号，拉一次即可收敛）。字段与游标细节见 `04-http-api.md` 1.4.1 / 1.4.3。

## 6. 一致性向量覆盖

### 6.1 现状：WS 尚无向量

- `conformance/vectors/` 目录里现有 **5 组、共 101 步**（`auth.ndjson` 23 步 / `images.ndjson` 14 步 / `tasks.ndjson` 18 步 / `sync.ndjson` 32 步 / `errors.ndjson` 14 步），**全部是 HTTP 步骤**：没有任何一步打 `/ws`，也没有 WS 操作类型。
- `conformance/README.md` 的组织表已给 `vectors/websocket.ndjson` 占位并标注「待写」（回放器的 `ws` 操作 `connect` / `expect` / `send` / `close` 均未实现）；该文件在目录里也**不存在**，所以 **MUST NOT** 据表认为 WS 已有覆盖。
- 因此本文全部条目目前**只靠实现与集成测试保证**，没有语言无关的裁判。

### 6.2 v2 应当补的 WS 向量（SHOULD）

用回放器已规划的 `ws.connect` / `ws.expect` / `ws.send` / `ws.close` 四个操作表达：

1. **握手拒绝**：不带 `Authorization` → 401 `unauthorized`；已吊销的 token → 401 `revoked`；两种情况都不得建连成功。
2. **`hello`**：连上后收到的 `hello` 五个键齐全，且 `server_device_id` 等于 `GET /api/v1/info` 的 `device_id`。
3. **心跳**：等一条 `ping`（`ts` 是毫秒整数）→ `ws.send` 一条 `pong` 回显同一 `ts`；回包后连接仍存活（后续广播照常到达）。
4. **任务推送**：创建任务后依次收到 `task_update`（`queued` → `analyzing`，`status`/`session_id`/`image_count` 正确）→ `done` 后再收一条 `task_result`；失败任务收 `task_failed` 且 `error_code`/`message` 非空。
5. **同构**：`task_result.session` 与同刻 `GET /api/v1/tasks/active` 的 `session` 逐字段相等。
6. **广播面**：两个不同 `device_id` 同时在线时，一次 `POST /api/v1/collections/<id>/select` 必须让**两个连接**都收到 `collection_changed`。
7. **踢旧**：同一 `device_id` 再连一次 → 旧连接被关闭，且新连接仍能收到后续广播。
8. **吊销**：`DELETE /api/v1/devices/<id>` → 该设备收到 `device_revoked` 且连接被关闭，此后它的 HTTP 请求一律 401 `revoked`。
9. **未实现事件的负向断言**：服务端不得发出 `ops`。回放器目前**没有**「某条消息必须不出现」的断言（`json_absent` 只作用于 JSON 路径）——补这条向量前需要先给回放器加一个带超时的 `ws.expect_absent`，否则只能列为人工检查项。

## 7. 文档-实现分叉

| 现有文档怎么写 | 实现是什么 | 处置 |
|---|---|---|
| 事件表列了服务端 → 客户端的 `ops` 事件 | 没有任何一处构造它 | 标为**未实现**、不得依赖（与 `02-transport.md` §4 同一结论） |
| 「客户端 10s 内未回 `pong` 则断开重连」 | 服务端不校验 `pong`、不主动断开；判死的是客户端「>40s 没收到服务端 `ping`」 | 10s 只是容差；现状判据写进 3.1，服务端校验 + 90s 断开是 **v2 变更** |
| 事件表未说建连时会补发状态 | 建连后补发一次**进行中**本机任务的 `task_update`（广播给所有连接） | 写进 1.2 |
| 「重连后先 `GET /api/v1/sync/ops` 补齐缺口」 | 重连补偿是一整套：刷状态 → 离线队列补跑 → 图片补传 → **先拉后推** → 失效界面缓存；ops 拉取只是其中一步，另有每秒的状态轮询与 WS 并行兜底 | 写进 3.2 |
| 握手头列出 `X-QS-Device-Id` | 身份只由 token 反查，该头被忽略；`/ws` 也不做版本协商，也没有 `X-QS-Server-Version` | 见 `02-transport.md` §4、`04-http-api.md` 1.3.17 |
| 客户端 → 服务端的 `ack` / `push_ops` 被写成常规路径 | 生产路径**从不发**（op 走 HTTP） | 2.2 已标「现状不发」；v2 要么接上、要么删除，不得写成已实现 |
| `device_revoked` 只说「收到后清 token 并断开」 | 客户端两层行为不一致：连接层比对 `device_id` 才断开，消息落地层**不比对**，收到别人的吊销也会清自己的配对 | 现状记进 2.1；v2 起统一为「**MUST** 比对 `device_id`」 |
