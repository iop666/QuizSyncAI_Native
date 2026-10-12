# 11 能力协商

> 状态：v1 现状（**按实现反推**）+ v2 目标（凡标「v2 变更」的句子**均未实现**）。
> 强制级别：**MUST** / **SHOULD** / **MAY**。
> 本域只描述「主机声明了什么能力、双方如何使用它」。个别能力的**行为细节**（端点、字段、数值）在 04/06/07/08 域定义；`ai_configured` 与任务失败码的关系在 08/09 域复用。

## 1. 能力声明与现状行为

### 1.1 声明的位置与形态（现状）
- 能力只在 `GET /api/v1/info` 的 `capabilities` 字段里声明：一个字符串数组，**现状恒为 5 项**——`analyze`、`sync`、`image_fetch`、`collections`、`multipage`。
- 该数组是**常量**：不随主机配置变化，`ai_configured = false`（未配 AI）时也照旧报全 5 项；不随端口、平台、合集状态、版本变化。
- 元素**顺序不是契约**（一致性向量第 1 步的标题已如此标注，断言用的是「包含」而非「相等」）。客户端 **MUST** 按名字集合判断，**MUST NOT** 依赖下标或长度。
- 客户端**不声明**自己的能力：配对请求只有 `code`/`device_id`/`device_name`/`platform`/`app_version`，WS `hello` 只有 `device_id`/`app_version`/`platform`，都没有能力列表。
- 现状**没有任何能力变更通知**：能力只能靠再次拉 `/info` 获得（客户端启动、回到前台、轮询时取）。

### 1.2 各能力的现状含义
| 能力名 | 现状含义（服务端确实提供的行为） | 是否可变 | 客户端现状是否据此降级 |
|---|---|---|---|
| `analyze` | 提供 `POST /api/v1/tasks` 与 `POST /api/v1/sessions/<id>/reanalyze`，即主机能跑多模态分析。**不代表已配置 AI**（那由 `ai_configured` 单独表达） | 恒有 | 否 |
| `sync` | 提供 `POST`/`GET /api/v1/sync/ops` 与 `GET /api/v1/sync/snapshot`（Lamport 水位 + 字段级 LWW 的增量与全量通道） | 恒有 | 否 |
| `image_fetch` | 提供 `POST /api/v1/images` 与 `GET /api/v1/images/<hash>`（按 sha256 上传/下载字节，内容寻址去重） | 恒有 | 否 |
| `collections` | 提供 `GET /api/v1/collections`、`POST /api/v1/collections/<id>/select`，且任务**必须**归属某个合集（否则 `409 no_active_collection`） | 恒有 | 否（客户端按 `active_collection_id` 判空，不读这个能力名） |
| `multipage` | `POST /api/v1/tasks` 接受 `image_hashes` 页序，长度 1–6 | 恒有 | 否 |

### 1.3 `ai_configured` 的含义与现状判定
- 判定（现状）：主机侧存在 AI 配置**且** API Key 非空**且**模型名非空 → `true`；否则 `false`。**不做**联通性校验：不试调、不校验 Key 是否有效、不校验余额。
- `false` 的含义：`analyze` 能力虽然被声明，但**当次执行不了**。客户端 **SHOULD** 提示「主机尚未配置 AI」，而不是等任务失败。
- 客户端现状：仅在**配对成功那一刻**若 `ai_configured = false` 就提示一次；不阻止配对、不阻止后续任何请求。
- 服务端现状（**MUST**）：`POST /api/v1/tasks` **不**检查 `ai_configured`，仍返回 `202`、`status = queued`；任务被出队时**第一步**检查配置，缺 Key 就直接结束为失败：
  - `tasks` 行：`status = failed`、`error_code = ai_auth`；
  - 会话行：`status = failed`、`error_code = ai_auth`、`error_message`（「主机尚未配置 AI」）；
  - 广播 `task_update`（`status = failed`）与 `task_failed`（`error_code = ai_auth`、`message`）；
  - 该任务**不会**经过 `analyzing`，也**不会**进入「图片缺失 / 调用失败」等后续分支。
- 因此「缺 Key」在协议上有两个同时成立的投影：`/info` 的 `ai_configured = false`（事前），任务失败且 `error_code = ai_auth`（事后）。客户端 **MUST NOT** 只依赖其中一个：`ai_configured` 是拉取型的、可能过期；`ai_auth` 是权威结论。

### 1.4 能力协商的现状：不做降级
- 客户端把 `capabilities` 解析进内存模型，但**没有任何分支读它**：功能可用性由 `active_collection_id`（判空决定能否发起识别）与 `ai_configured`（提示文案）决定，与能力列表无关。
- 服务端**不读**客户端能力（客户端也不发）。
- 结论：现状的 `capabilities` 是**自述/展示**字段，**不是**协商结果。跨版本兼容实际由 `protocol_version` 承担（主版本不一致 → `426 version_mismatch`，见 04/10 域）。
- 现状唯一的隐式降级：主机未选合集时，客户端不发起识别，服务端也以 `409 no_active_collection` 兜底。
- 未知能力名：现状无规定，但客户端解析时按字符串保留、不做白名单校验，因此**已自然满足**「忽略不认识的能力名」（v2 把它写成 **MUST**）。
- 客户端确实会读的协商信号只有三个，且**都不是** `capabilities`：`protocol_version`（主版本不一致 → `426`，停止请求）、`ai_configured`（提示文案）、`active_collection_id`（能否发起识别的门禁）。
- 收益与代价（现状、可验收）：新增一个能力名对老客户端**没有任何影响**（它不读这个数组）；反过来，老主机不声明某个能力名，新客户端也**不会**降级（新客户端同样不读）。这正是 v2 要修的根因——字段是自述，不是协商。

### 1.5 现状的向前兼容方式：加法字段而非能力开关
现状不靠能力开关演进，靠三件事，**MUST** 全部保持：
1. **加法字段**：新信息只以新增键的形式出现，不改既有键的含义（例：`GET /api/v1/tasks/active` 里的 `ops_lamport`、`collections`、`session` 都是后续追加的，老客户端忽略；`collections` 为空数组表示「老主机没上报」，客户端不动）。
2. **忽略未知**：HTTP 请求里的未知字段一律忽略；客户端对不认识的 WS 事件类型直接忽略（协议向前兼容）；服务端对不认识的 WS 消息类型同样不做任何动作。
3. **主版本兜底**：破坏性变更不让能力列表承担，而由 `protocol_version` 主版本 + `426 version_mismatch` 拦截（见 04/10 域）。
- 推论（现状）：能力列表**既不能**保护老客户端免受新功能影响（老客户端不读它），**也不能**告诉新客户端该降级（新客户端也不读它）。它目前唯一的作用是人工排查与文档意义上的自述。

### 1.6 能力在 WS 路径上是空的（现状）
- WS `hello` **不带**能力：它只有 `server_device_id`、`protocol_version`、`active_collection_id`、`active_collection_name`。因此「刚连上、还没拉过 `/info`」的客户端手里**没有任何能力信息**，只能等一次 HTTP 拉取。
- 客户端在 hello 路径下另外拼了一份主机描述：`capabilities` 为**空数组**、`ai_configured` 被当作 `true`、`device_name` 与 `app_version` 为空串。这反证了 1.4 的结论——空的能力集合不会触发任何降级，也没有任何逻辑依赖 hello 路径上的 `ai_configured`。
- 只有 `GET /api/v1/info` 带 `capabilities`；`/tasks/active`、`/collections`、`/sync/ops`、`/sync/snapshot`、`/devices` 都不带任何能力字段。
- 没有任何 HTTP 端点能**修改**能力集合：能力在协议面上是只读常量，只能随主机实现或宿主配置变化。
- 客户端不持久化能力（每次 `/info` 覆盖内存里的那份），因此能力变化只能在**下一次拉取**时被发现——这正是 v2 的能力变更事件要解决的延迟问题。

### 1.7 v2 目标（**变更**，尚未实现）
1. **能力变更事件**：主机在能力集合或 `ai_configured` 变化时（AI Key 从无到有、宿主关闭某项能力、页数上限调整）向已连接客户端推送一条事件（形如 `{"type":"capabilities_changed","capabilities":[...],"ai_configured":true}`），客户端收到后立即刷新可用功能，不必等下一次 `/info`。
2. **让上限可被发现**：`multipage` 目前只表达「支持多页」，不表达「最多 6 页」；v2 **SHOULD** 增加一个上限对象（如 `limits: {"multipage_max_pages":6,"max_image_bytes":2097152,"uploads_per_minute":30}`），使这些数字不再靠两端各自硬编码。
3. **降级规则（v2 MUST）**：客户端在发起某项功能前 **MUST** 先确认对应能力名存在，缺失时降级到基础路径——无 `multipage` 时只发单页 `image_hash`；无 `collections` 时不展示合集界面、也不发 `collection_id`；无 `sync` 时不做 ops 同步，仅轮询任务状态；无 `image_fetch` 时不上传也不下载图片。反之，主机 **MUST NOT** 声明自己不具备的能力；两端 **MUST** 忽略不认识的能力名。
4. **未配 AI 前置拒绝**：`POST /api/v1/tasks` **SHOULD** 在 `ai_configured = false` 时直接拒绝（如 `503 ai_not_configured`，带 `retry_after_seconds`），不再「先 `202`、再必然失败」；`error_code = ai_auth` 保留为执行期意外缺 Key 的兜底。
5. **能力与版本绑定**：`capabilities` 只在 `protocol_version` 主版本一致时有意义；客户端收到 `426` 时 **MUST** 停止请求（提示升级），**MUST NOT** 猜测降级。

## 2. 数值表

| 项 | 值 | 备注 |
|---|---|---|
| `capabilities` 项数 | 5 | 顺序不是契约；含重与否未规定（现状无重复） |
| 能力名 | `analyze`、`sync`、`image_fetch`、`collections`、`multipage` | 常量数组 |
| 协议主版本 | 1 | 不一致 → `426 version_mismatch`；不带版本头放行 |
| 多页上限 | 6 页 | 超出 → `400 too_many_pages`；能力名本身不表达该上限 |
| 单文件图片上限 | 2097152 字节（2 MiB） | 超出 → `413 payload_too_large` |
| 上传限流 | 30 次/分钟/设备 | 超出 → `429 rate_limited` |
| 任务队列深度 | 20 行 `status = queued` | 超出 → `429 queue_full`（`retry_after_seconds: 10`） |
| 缺 Key 的任务错误码 | `ai_auth` | 会话与任务两侧同值 |
| 缺 Key 时的 HTTP 结果 | `202` + 随后 `status = failed` | 现状；v2 改为前置拒绝 |

## 3. 一致性向量覆盖

| 向量 | 覆盖的本文章节 |
|---|---|
| `auth.ndjson` 1 | 1.1 / 1.2（`/info` 免鉴权，`capabilities` 含全部 5 项，`ai_configured: true`，`active_collection_id: null`） |
| `auth.ndjson` 22–23 | 1.4（兼容性由版本头承担：主版本不同 `426`；不带版本头放行） |
| `images.ndjson` 2–12 | 1.2 的 `image_fetch` 现状语义（内容寻址去重、`413`、`429`、`404`） |
| `images.ndjson` 13–14 | 1.2（能力可用但**仍需 token**：鉴权与能力声明是两件事） |
| `tasks.ndjson` 4、15 | 2 的数值（`multipage` 的 6 页上限 → `400 too_many_pages`；队列深度 20 → `429 queue_full`）——即「能力名不表达上限」这一现状的实证 |
| `errors.ndjson` 2–4 | 2 的版本行（只比主版本；不可解析的版本头也 `426`） |

**覆盖缺口（v1）**：`ai_configured = false` 的 `/info` 与「缺 Key → 任务失败 + `error_code = ai_auth`」**没有任何向量**（1.3 的现状行为完全靠实现与人工验证）；能力数组「顺序无关」只有标题声明、没有负向向量；`multipage` 的 6 页上限只在任务维度被覆盖（见上表的 `tasks.ndjson`），没有能力维度的向量；`collections` 能力的门禁（`active_collection_id` 判空）没有 `/info` 维度的向量；`capabilities` 的「常量性」（改配置后不变）也没有向量。v2 **SHOULD** 至少新增两条：`ai_configured=false` 下的 `/info`；未配 AI 时创建任务 → 任务 `failed`/`ai_auth`（或 v2 的前置 `503`）。

向量侧的一条前提（现状）：`auth.ndjson` 第 1 步断言 `ai_configured: true`，因此向量执行器**必须**能把主机的 AI 配置注入为「已配置」；要跑 `ai_configured = false` 的向量需要另一套夹具（现状没有这套夹具，这也是缺口的一部分）。

## 4. 文档-实现分叉

1. **「不会静默失败」的措辞与实现不符**：现契约称 `ai_configured = false` 时客户端应提示「主机尚未配置 AI」，「而不是让任务静默失败」。实现的路径是「请求照常 `202`、任务随后失败并广播 `task_failed`」——它并不拦截请求，只是失败**不够静默**（有错误码与事件）。→ 1.3
2. **`ai_configured` 的判定口径未写**：现契约没有说明它是「配置非空」而不是「Key 有效」——不试调、不校验 Key 与余额。→ 1.3
3. **`capabilities` 的形态未写**：现契约给了示例数组，却没写它是常量、不随 `ai_configured` 变化、顺序不是契约、客户端不得据下标判断。→ 1.1
4. **能力与上限脱钩未写**：`multipage` 与 6 页硬上限的关系在文档中没有任何绑定，读者无法从能力名或 `/info` 得知上限。→ 1.2 / 2
5. **能力协商与降级完全缺章**：现契约没有定义能力变更如何通知、客户端缺能力时如何降级、未知能力名如何处理（现状是根本不降级）。→ 1.4 / 1.7
6. **`ai_configured` 与 `error_code = ai_auth` 的双投影未写**：现契约只在 `/info` 一节提 `ai_configured`、只在任务一节列 `ai_auth`，没有把它们写成同一件事的两个投影。→ 1.3
7. **WS `hello` 不带能力这件事没写**：现契约的 hello 事件里没有 `capabilities`，也没有说明客户端在 hello 路径上把能力视为空、把 `ai_configured` 视为 `true`。→ 1.6
