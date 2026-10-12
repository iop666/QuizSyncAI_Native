# examples —— 线协议报文样例

本目录是**照着写解析代码**用的样例集：一条样例 = 一次真实的 HTTP 往返、或一条真实的 WebSocket 文本帧。
字段名、嵌套关系、状态码与错误码都与已通过的一致性向量逐条对齐，**没有一处是凭印象写的**。

- `conformance/vectors/*.ndjson` 是**裁判**：机器回放、必须全绿、冲突时以它为准。
- 本目录是**说明书**：给人看、抄字段、写解析分支用；它不参与回放，也不是第二套契约。
- 每条样例都用 `vectors` 标出对应的向量步骤（形如 `auth.ndjson#5`）；WS 与少数端点目前没有任何向量，则标 `[]` 并在下表里写明依据的规范小节。

## 1 文件与条数

| 文件 | 条数 | 一行是什么 |
|---|---|---|
| `http.ndjson` | **24** | 一次 HTTP 请求 + 它的响应（同一行内） |
| `ws.ndjson` | **14** | 一条 WebSocket 文本帧（含方向是服务端→客户端还是客户端→服务端） |
| `errors.ndjson` | **18** | 一个错误响应，按 `code` 覆盖全表 |
| `README.md` | — | 本说明 |

三份数据文件都是 NDJSON：一行一个 JSON 对象，行尾一个换行符，文件末尾不留半行、不留空行。
行号只是文件内的阅读顺序，**不要按行号硬编码**（本目录会随规范增补而变化，`vectors` 字段才是锚点）。

## 2 行格式

```jsonc
// http.ndjson
{"title": "…", 
 "request":  {"method": "GET|POST|DELETE", "path": "/api/v1/…", "headers": {"<头名>": "…"}, "body": {…}|"…"|null},
 "response": {"status": 200, "headers": {"Content-Type": "…", "X-QS-Server-Version": "1.0.0"}, "body": {…}|"…"|null},
 "vectors":  ["auth.ndjson#5"]}

// ws.ndjson
{"direction": "server→client" | "client→server", "message": {"type": "…"}, "when": "…", "vectors": []}

// errors.ndjson
{"status": 401, "body": {"code": "…", "message": "…", "retry_after_seconds": null},
 "when": "…", "client_hint": "…", "vectors": ["auth.ndjson#4"]}
```

约定：`body` 为 `null` = 这一侧没有实体（GET 请求、无体 POST 请求、以及 `errors.ndjson` 里**不是协议错误体**的两行）。
`when` 写清触发条件与必须注意的规则，`client_hint` 写客户端该做什么（分支、文案、能不能重试）。
`errors.ndjson` 的 `body` 为 `null` 表示**没有协议错误体**（框架级 404），`status` 为 `null` 表示**不由任何 HTTP 端点产生**（`internal`）。

## 3 占位符约定

样例里的占位符要替换成真实值才是可发送的报文。核心四个：

| 占位符 | 含义 | 形态 / 约束 | 出现在哪 |
|---|---|---|---|
| `<host>` | 主机地址与端口 | 例如 `192.168.1.20:8765`；默认端口 8765，被占用时依次试 8766–8770，实际端口由 `GET /api/v1/info` 与配对二维码给出 | 请求头 `Host` |
| `<token>` | 配对签发的令牌 | 64 位小写十六进制（`^[0-9a-f]{64}$`）；只走请求头 `Authorization: Bearer <token>`；主机只存它的 sha256 摘要，明文不落库 | `Authorization`、`POST /pair` 响应 |
| `<sha256>` | 图片内容 hash | 64 位小写十六进制（`^[0-9a-f]{64}$`）；即 `image_hash` / 快照里的 `hash`，同时是下载路径里的文件名。大写视为形态非法 | `image_hash`、`hash`、下载路径 |
| `<uuid>` | UUID 形态的实体 id | 36 字符（`^[0-9a-f-]{36}$`） | `session_id`、`question_id`、主机会生成的 `task_id` |

区分写法：`<uuid:session>` / `<uuid:question>` / `<uuid:task>` / `<uuid:session-image>` 表示同一行里**不同**的实体；
同一行里**同名**占位符表示**同一个值**（例如会话的 `session_id` 与题目里的 `session_id`）。

扩展两个（报文里没有别的写法）：

| 占位符 | 含义 |
|---|---|
| `<boundary>` | multipart/form-data 的边界串 |
| `<jpeg-bytes:N>` | N 字节 JPEG 原始字节（上传 part 的体、下载响应体、超限用例的超长体） |

与向量里变量的对应：

| 本目录 | 向量里的写法 |
|---|---|
| `<token>` | `$token`（由 `capture: {"token": "$.token"}` 产生） |
| `<sha256>` | `$hash`（由 `capture: {"hash": "$.image_hash"}` 产生） |
| `<uuid:session>` | `$session_id` / `$reanalyze_session` |
| `<uuid:task>` | `$reanalyze_task` |
| `<host>` | 回放器监听的端口与地址（`$port`） |
| 6 位配对码（样例写成 `123456`） | `$pairingCode`（回放器每次使用时实时读取主机当前配对码） |

## 4 哪些是契约、哪些只是示意值

**契约（不得改，改了就是实现错）**：字段名与嵌套关系；键的出现/省略规则（`task_update` 的 `session_id` 为 null 时**整个键被省略**而不是 `null`；`GET /tasks/<id>` 的 `session` 只在会话行存在时出现；`GET /tasks/active` 的 `session` **只**在 `status=done` 时出现；`errors.ndjson` 里错误体恒为三键）；HTTP 状态码与 `code` 的配对；`type` / `entity` / `op_type` / `status` 的取值；`session.cached` 是 **0/1 整数**而 `POST /tasks` 顶层的 `cached` 是**布尔**；除 `/ws` 外所有响应（含错误响应）都带 `X-QS-Server-Version`。

**只是示意值（不得据此分支）**：`message` 文案（同码多种文案，措辞不是契约）；所有时间戳、`lamport`、`latency_ms`、`retry_after_seconds` 的具体数字；`device_id` / `device_name` 的具体值（样例沿用向量里的 `server-device-1`、`android-device-1`、`TEST-HOST` 便于对照）；`ai_provider` / `ai_model` / `prompt_version` / `raw_response`（来自主机自己的 AI 配置与 AI 返回**原文**，样例里 `raw_response` 截断成 `{"questions":[...]}` 以免占满一行）；`field_clocks_json`（样例只列了本次变更的两个字段，真实报文里变更的每个字段都有一条 `{"l": 序号, "d": 设备 id}`）；`capabilities` 的**顺序**（集合才是契约）；`collections` 的顺序（实际按 `created_at` 倒序）；`GET /tasks/active` 里 `collections` 的条数。

**样例之间不构成一条时间线**：`session_id` 用同一个占位符只是为了形状一致，不要把第 11 行与第 19 行当成同一个会话。

## 5 对应关系（每条样例 → 向量 / 规范）

### 5.1 `http.ndjson`

| 行 | 内容 | 向量 | 规范 |
|---|---|---|---|
| 1 | `GET /api/v1/info` → 200，9 键、`capabilities` | `auth.ndjson#1` | 04 §1.3.1 |
| 2 | `POST /api/v1/pair` → 200 签发 token | `auth.ndjson#5` | 04 §1.3.2 |
| 3 | 配对码错误 → 401 `invalid_code` | `auth.ndjson#4` | 04 §1.3.2 |
| 4 | 窗口内第 6 次配对 → 429 `rate_limited` | `auth.ndjson#19` | 04 §1.3.2 |
| 5 | `POST /api/v1/images`（multipart，字段 `file`）→ 200 六键 | `images.ndjson#2`、`#3` | 04 §1.3.3 |
| 6 | 空文件 → 400 `invalid_request` | `images.ndjson#9` | 04 §1.3.3 |
| 7 | 超 2 MiB → 413 `payload_too_large` | `images.ndjson#10` | 04 §1.3.3 |
| 8 | `GET /api/v1/images/<hash>` → 200 原样字节 | `images.ndjson#4` | 04 §1.3.4 |
| 9 | 下载不存在的 hash → 404 `not_found`（形态非法同码） | `images.ndjson#6`（形态非法见 `#7`、`#8`；`errors.ndjson#11`） | 04 §1.3.4 |
| 10 | `POST /api/v1/tasks` → 202 `queued` | `tasks.ndjson#8` | 04 §1.3.5 |
| 11 | `GET /api/v1/tasks/t-1` → 200 `done`（含 `session` 嵌套） | `tasks.ndjson#9` | 04 §1.3.7、§1.4.2 |
| 12 | `POST /api/v1/sessions/<id>/reanalyze` → 202 + 新 `task_id` | `tasks.ndjson#16` | 04 §1.3.9（`§4` 分叉表第 10 条仍是旧话，见 §6） |
| 13 | `GET /api/v1/tasks/active` → 200 `analyzing` | **无向量** | 04 §1.4.1 |
| 14 | `GET /api/v1/collections` → 200（非空条目） | `auth.ndjson#6`（空库形态） | 04 §1.3.10 |
| 15 | `POST /api/v1/collections/c1/select` → 200 | **无向量**（只有 404 有） | 04 §1.3.11 |
| 16 | 选择不存在的合集 → 404 `not_found` | `errors.ndjson#14` | 04 §1.3.11 |
| 17 | `POST /api/v1/sync/ops` → 200（`applied` + `rejected` 各 1） | `sync.ndjson#5`（applied 侧）+ `#4`（rejected 侧）——**组合行**，见 §6 | 04 §1.3.12 |
| 18 | `GET /api/v1/sync/ops?from_device=…` → 200（含 `next_cursor`） | `sync.ndjson#18`（空页 `#19`、缺参数 `#20`） | 04 §1.3.13、§1.4.3 |
| 19 | `GET /api/v1/sync/snapshot?limit=1` → 200（分页字段） | `sync.ndjson#28`、`#32` | 04 §1.4.4 |
| 20 | `GET /api/v1/devices` → 200（**不得**含令牌哈希） | `errors.ndjson#12` | 04 §1.3.15 |
| 21 | `DELETE /api/v1/devices/<id>` → 200 `revoked` | `auth.ndjson#20`（被吊销 token 的后续请求见 `#21`） | 04 §1.3.16 |
| 22 | `X-QS-Client-Version: 2.0.0` → 426 `version_mismatch` | `auth.ndjson#22`（放行见 `#23`；`errors.ndjson#3`、`#4`） | 04 §1.1 |
| 23 | 缺 token → 401 `unauthorized` | `auth.ndjson#2`（空令牌/畸形头见 `errors.ndjson#5`、`#6`） | 04 §1.1 |
| 24 | 已吊销的 token → 401 `revoked` | `auth.ndjson#21` | 04 §1.1 |

### 5.2 `ws.ndjson`（**WS 目前没有任何向量**：`vectors` 全为空数组，依据的是规范正文）

| 行 | 方向 | 内容 | 规范 |
|---|---|---|---|
| 1 | 服务端→客户端 | `hello`（5 键，`server_device_id` 是主机自己） | 05 §1.2、§2.1 |
| 2 | 服务端→客户端 | `task_update`（`analyzing`，`image_count`） | 05 §2.1 |
| 3 | 服务端→客户端 | `task_update` 建连补发（本机进行中任务，`task_id` = `session_id`） | 05 §1.2 |
| 4 | 服务端→客户端 | `task_result`（`session` 与任务查询/`/tasks/active` 同构） | 05 §2.1 |
| 5 | 服务端→客户端 | `task_failed`（任务错误码，不是 HTTP `code`） | 05 §2.1、04 §1.4.2 |
| 6 | 服务端→客户端 | `collection_changed` | 05 §2.1、04 §1.3.11 |
| 7 | 服务端→客户端 | `device_revoked` | 05 §2.1、§4、04 §1.3.16 |
| 8 | 服务端→客户端 | `ping`（每 30s，客户端 40s 判死） | 05 §2.1、§3.1 |
| 9 | 客户端→服务端 | `hello`（服务端忽略其中的 `device_id`、`platform`） | 05 §2.2、§1.2 |
| 10 | 客户端→服务端 | `pong`（回显 `ts`） | 05 §2.2、§3.1 |
| 11 | 客户端→服务端 | `ack` —— **生产路径不发** | 05 §2.2 |
| 12 | 客户端→服务端 | `push_ops` —— **生产路径不发**（op 一律走 HTTP） | 05 §2.2 |
| 13 | 服务端→客户端 | `ops` —— **实现从未发出，不得依赖** | 05 §2.1、§7 |
| 14 | 客户端→服务端 | `auth_failed` —— **客户端本地合成，服务端从不发送** | 05 §2.3 |

握手本身不是 WS 帧：`GET /ws` 失败时是普通 401 响应，`code` 为 `revoked`（token 已吊销）或 `unauthorized`（缺失/认不出），`message` 是「WS 握手鉴权失败」，且该响应**不带** `X-QS-Server-Version`（`/ws` 绕过中间件、也不做版本协商）。这一条在 `errors.ndjson` 第 1、2 行的 `when` 里也写明了。

### 5.3 `errors.ndjson`

| 行 | `code` / 状态 | 向量 | 规范 |
|---|---|---|---|
| 1 | `unauthorized` / 401 | `auth.ndjson#2`、`#3`、`errors.ndjson#5`、`#6` | 09 §3 |
| 2 | `revoked` / 401 | `auth.ndjson#21` | 09 §3 |
| 3 | `version_mismatch` / 426 | `auth.ndjson#22`、`errors.ndjson#3`、`#4` | 09 §3、04 §1.1 |
| 4 | `invalid_code` / 401 | `auth.ndjson#4`、`#14` | 09 §3、04 §1.3.2 |
| 5 | `code_expired` / 401 | `auth.ndjson#8` | 09 §3、§5（值是「已过期秒数」） |
| 6 | `rate_limited` / 429（配对滑窗） | `auth.ndjson#19`、`images.ndjson#12` | 09 §3、§5（三种触发口径不同） |
| 7 | `queue_full` / 429 | `tasks.ndjson#15` | 09 §3（`retry_after_seconds` 固定 10） |
| 8 | `invalid_request` / 400 | `errors.ndjson#7`–`#10` | 09 §3、§4（各分支 message） |
| 9 | `too_many_pages` / 400 | `tasks.ndjson#4` | 09 §3 |
| 10 | `no_active_collection` / 409 | `tasks.ndjson#7` | 09 §3（客户端用固定文案） |
| 11 | `invalid_request` / 409（重新识别原图缺失） | `sync.ndjson#31` | 09 §7.1（两套 Host 已收敛为同码同状态）、04 §1.3.9 |
| 12 | `payload_too_large` / 413 | `images.ndjson#10` | 09 §3、§5（两条产生路径） |
| 13 | `not_found` / 404 | `images.ndjson#6`、`#7`、`errors.ndjson#11`、`#14` | 09 §3、§5 |
| 14 | **框架级 404，无协议错误体** | `errors.ndjson#13` | 04 §1.1（未知路由） |
| 15 | `internal`（无状态码） | **无向量**（09 §6 标为缺口） | 09 §3、§1.4 |
| 16 | `forbidden` / 403（本机控制面） | **无向量** | 09 §7.1 |
| 17 | `unavailable` / 501（本机控制面） | **无向量** | 09 §7.1 |
| 18 | `bad_request` / 400（本机控制面） | **无向量** | 09 §7.1 |

## 6 与规范/向量的冲突，以及处置

一句话：**三份样例的报文内容全部以向量为准**（向量跑在真实实现上、且是全绿裁判）。核对时发现四处依据互相打架，其中三处**现行规范已经改齐**，只剩一处仍是规范内部的矛盾。

**① 仍然存在的矛盾：`spec/04 §4` 分叉表第 10 条。** 该条写「`reanalyze` …… 实现没有 `task_id`」，与同一文件的 `§1.3.9`（「响应**包含 `task_id`**（UUID 形态），由服务端生成并回传，`tasks.ndjson` 16、17 钉住」）以及向量 `tasks.ndjson#16` 都矛盾。**处置**：样例按 `§1.3.9` 与向量带 `task_id`（`http.ndjson` 第 12 行）。建议把 §4 第 10 条改写成「响应带 `task_id`；v2 再把 `status`/`question_count` 收敛到与 `POST /tasks` 同形」。

**② 曾经冲突、现行规范已改齐（样例与之一致）**：

| 曾经的冲突 | 现行规范 | 样例处置 |
|---|---|---|
| `reanalyze` 是否带 `task_id`（旧文写「不回传」） | `04 §1.3.9` 写明带；`04 §1.4.5` 第 2 条「v1 已补」 | `http.ndjson` 第 12 行带 `task_id`，与 `tasks.ndjson#16` 一致 |
| 快照 `images` 是否含 `local_path`（旧文写「含」） | `04 §1.4.4` 写明**不含**；`04 §1.4.5` 第 4 条注明已剔除 | `http.ndjson` 第 19 行不含，与 `sync.ndjson#32` 的 `json_absent` 一致 |
| 会话无原图时 `reanalyze` 回什么（旧文写「非协议 500」；`09 §7.1` 曾列「内嵌 Host 400 / 独立服务端 409」） | `04 §1.3.9` 写明 `409 invalid_request`；`09 §7.1` 把这条标为「已在 v1 内收敛的历史差异」（两套 Host 现在同码同状态） | `errors.ndjson` 第 11 行记 `409 invalid_request`，与 `sync.ndjson#31` 一致 |

**③ 刻意的组合行（不是冲突）**：`http.ndjson` 第 17 行把向量 `sync.ndjson#5` 的一条本机 op 与 `#4` 的一条冒名 op 放进同一次请求，用来一次展示 `applied` 与 `rejected` 两个计数。两个 op 的字段逐字取自各自向量，响应形状与两个计数器都由向量钉住；实现的逐条计数规则是独立的（`04 §1.3.12`），所以组合后的结果 `{"applied":1,"rejected":1}` 成立。

除上述以外，错误码全表（`09 §3`）与各码的 message 取值（`09 §4`）与样例逐条一致，没有发现别的冲突；`conformance/README.md` 与 `05 §6.1` 的向量步数（auth 23 / images 14 / tasks 18 / sync 32 / errors 14，共 101 步）也已与实际文件一致。

## 7 永远不会出现在线路上的取值

这些取值**不是**服务端能发的东西，样例里只在 `ws.ndjson` 第 14 行出现一条（并已在 `when` 里标明「服务端从不发送」），其余写在这里以免被误当成协议码：

| 取值 | 谁产生 | 何时 |
|---|---|---|
| `timeout` | 客户端本地 | 连接 / 读超时 |
| `network_error` | 客户端本地 | 连不上（主机没运行、不同网段、证书问题） |
| `cancelled` | 客户端本地 | 调用方主动取消 |
| `auth_failed` | 客户端本地（WS 侧） | WS 握手被 401/403 拒绝 |
| `internal` | 客户端本地兜底 | 响应里**没有** `code` 时（此时**不得**当成成功） |

## 8 本机控制面 ≠ LAN 协议

`errors.ndjson` 第 16–18 行的 `403 forbidden` / `501 unavailable` / `400 bad_request` 只可能来自**独立命令行服务端**的**本机**端点（停止服务、本机命令行、本机控制令牌），内嵌 Host 根本没有这些端点。它们已被规范划到「本机控制面」，**不属于 LAN 协议**：LAN 客户端不应调用、也不应为它们写协议分支。注意 `bad_request` 与协议码 `invalid_request` 是**两个码**（状态码同为 400）。

## 9 目前没有向量覆盖的部分（照样例写代码时请留意）

- **WebSocket 全部事件**：`conformance/vectors/` 里没有任何一步打 `/ws`（`05 §6.1` 明确写了这一点）。`ws.ndjson` 的每条都对应该规范的正文小节，`vectors` 一律为空数组。
- `GET /api/v1/tasks/active`（全字段、`idle` 与 `done` 两态）完全没有向量（`04 §3` 的缺口清单把它列在第一位）、`POST /tasks/<id>/retry` 的成功路径（响应恒为 `{"status":"queued"}`，只对 `failed` 生效；只有 404 有向量）、`POST /collections/<id>/select` 的成功路径（200 + `collection_changed` 广播；只有 404 有向量）、非空的 `GET /collections`（条目字段只有空库被覆盖）、`GET /images/<hash>` 的 `width`/`height` 为 `null`、`DELETE /devices/<id>` 对不存在 id 仍回 200、缺 `file` 字段的 400、`/ws` 握手（`401`、踢旧连接、`hello` 载荷）。
  （注意：`POST /sessions/<id>/reanalyze` **不在缺口里** —— 它已由 `tasks.ndjson` 16、17、18 与 `sync.ndjson` 31 覆盖。）
- 配对的 `409 already_paired`（**body 里没有 `code`**，按成功处理；见 `03` 域）本目录未单列样例；`429` 的「失败次数过多已锁定」分支与 `internal` 兜底路径是 `09 §6` 自认的两个缺口（`09 §6` 也已把「无原图重跑 → 409 `invalid_request`」记为 `sync.ndjson#31` 覆盖）。
- 响应头没有向量断言（`conformance/README.md` 已注明），所以 `X-QS-Server-Version` 这类头只以规范为准。

## 10 照着写解析代码的要点

1. **按 `code` 分支，不按 HTTP 状态码、更不按 `message` 文案**；收到不认识的 `code` 按「通用失败」处理（显示 `message`、保留原始 `code`），不得崩溃、不得静默吞掉、不得当成成功。
2. **错误体恒三键**：`code` / `message` / `retry_after_seconds`（不用时该键仍然出现、值为 `null`）。等待时间只读响应体，**不用** `Retry-After` 头。
3. 只自动重试 `429`（必须等 `retry_after_seconds`）与「主机状态确实变了」的 `409 no_active_collection`；401 / 403 / 404 / 413 / 426 一律不自动重试。
4. **并不是所有 404 都带 `code`**（第 7 节第 14 行）；解析时先判「有没有协议错误体」。
5. WS 侧：`type` 是唯一判别键；未知 `type`、非对象帧、字段类型不对的消息一律**静默丢弃**（不断连、不报错）；`hello` 不保证是第一条消息，`session_id` 为 null 时键被省略。
6. WS 广播**没有定向**：收到的 `task_update` / `task_result` 可能是别的设备的任务，靠 `session.source_device` 区分「自己发起的」与「主机本机发起的」；断线期间漏掉的推送**不会补发**，只能靠 `GET /api/v1/tasks/active` 轮询与完整同步找回（仅「进行中的本机任务」在建连后补发一次 `task_update`）。
7. 拉 op 的游标是**排他**的（`lamport > cursor`）；空页 `next_cursor` 为 `null` 时用「已应用 op 的最大 `lamport`」推进，**不得**归零；快照续页必须用 `next_offset`，不要自己 `offset += limit`。
8. 上传：字段名恒为 `file`；被拒（400/413）的上传照样消耗每分钟 30 次的额度；限流先于体积判定。

## 11 本目录的校验

三份数据文件在入库前逐条机器校验过：

- 每行都是合法 JSON 对象、键名与 `§2` 的行格式一致、无空行、末尾无半行；
- 每条引用向量步骤的样例，其**状态码与错误码与该向量步骤逐一比对**一致（`vectors` 里列出的每一步都比对）；
- 样例里出现的每个 JSON 键与每个枚举取值，都能在 `spec/` 正文、`conformance/vectors/` 或实现行为里找到出处（无自造字段、无自造码）；
- 错误码覆盖 `09 §3` 全表（13 个码，含状态码为「无」的 `internal`）与 `09 §7.1` 的三个本机控制面码。

唯一的例外已在 `§4` 与 `§6` 写明：`raw_response` 与 `field_clocks_json` 是**截断示意**，`http.ndjson` 第 17 行是**组合行**，`tasks/active`、`collections/select` 的成功路径等少数端点**没有向量**、按规范正文写。
