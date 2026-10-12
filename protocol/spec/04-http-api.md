# 04 HTTP 接口

> 状态：v1 现状（**按实现反推**：与旧契约文本冲突时，以实现为准）+ v2 目标（凡标「v2 变更」或「尚未实现」的句子**均未落地**，只是方向）。
> 强制级别：**MUST**（两端已依赖、不得偏离）/ **SHOULD** / **MAY**。
> 约定：正文只出现协议词汇与本地库的物理命名（`tasks`、`sync_ops`、`settings`、`deleted_at`、`local_path` 等），不出现任何一端的语言、框架、包名与仓库内路径。

## 1. 端点

### 1.1 通用规则（现状）

| 规则 | 内容 |
|---|---|
| 传输 | 单端口 HTTP/1.1；同端口 `/ws` 升级为 WebSocket（见 05 域）。端口默认 **8765**，被占用则依次试 **8766–8770**，实际端口由 `/api/v1/info` 与配对二维码给出 |
| 前缀 | 所有 HTTP 端点以 `/api/v1/` 开头 |
| 鉴权 | 除 `GET /api/v1/info` 与 `POST /api/v1/pair` 外，**MUST** 带 `Authorization: Bearer <token>`。缺失或认不出 → `401 unauthorized`；已吊销 → `401 revoked`。服务端只存 token 的 SHA-256 摘要（`devices.token_hash`），明文不落库 |
| 版本协商 | 请求 **SHOULD** 带 `X-QS-Client-Version`（点号前第一段为主版本）。主版本与服务端不一致 → `426 version_mismatch`（body 给出双方版本）。**空值/完全不带该头一律放行**；但**值不可解析**（例如 `abc`）等于主版本不一致，同样 `426`（现状，已被向量钉住）。所有响应带 `X-QS-Server-Version` |
| 编码 | JSON 请求与响应 `application/json; charset=utf-8`；图片上传 `multipart/form-data`；图片下载原样字节 |
| 错误体 | 恒为三键：`code`(str)、`message`(str)、`retry_after_seconds`(int｜null)。客户端 **MUST** 以 `code` 分支，**MUST NOT** 解析 `message` 文案 |
| 未知字段 | 请求里认不出的键一律忽略；服务端不因未知字段报错 |
| 未知路由 | 没匹配上任何端点 → `404`，但**不是**协议错误体（响应里没有 `code`）。客户端 **MUST NOT** 假设所有 404 都带 `code: not_found` |
| 数据隔离 | 现状**没有按设备隔离**：任何已配对设备都能读全库、给别人的图片下单、看所有人的记录。唯一的额外约束是 `POST /sync/ops` 的归属校验（op 的 `device_id` 必须等于调用者）；`DELETE /devices/<id>` 反而**没有**任何归属或角色约束（见 1.3.16） |

### 1.2 端点总表（17 条，与实现的路由表逐条对应）

| # | 方法 + 路径 | 鉴权 | 一句话 |
|---|---|---|---|
| 1 | `GET /api/v1/info` | 免 | 主机自述 + 能力 + 当前合集 |
| 2 | `POST /api/v1/pair` | 免 | 配对换 token |
| 3 | `POST /api/v1/images` | 需 | multipart 上传图片（字段 `file`） |
| 4 | `GET /api/v1/images/<hash>` | 需 | 按 sha256 取图片字节 |
| 5 | `POST /api/v1/tasks` | 需 | 创建分析任务（多页 + 合集） |
| 6 | `GET /api/v1/tasks/active` | 需 | 只读探测「主机现在在识别什么」 |
| 7 | `GET /api/v1/tasks/<taskId>` | 需 | 任务状态与结果 |
| 8 | `POST /api/v1/tasks/<taskId>/retry` | 需 | 重试失败任务 |
| 9 | `POST /api/v1/sessions/<sessionId>/reanalyze` | 需 | 按既有页序重跑（新会话） |
| 10 | `GET /api/v1/collections` | 需 | 合集列表 + 当前选中 |
| 11 | `POST /api/v1/collections/<id>/select` | 需 | 切换主机当前合集 |
| 12 | `POST /api/v1/sync/ops` | 需 | 推送本地操作日志 |
| 13 | `GET /api/v1/sync/ops` | 需 | 按设备 + 水位拉取操作日志 |
| 14 | `GET /api/v1/sync/snapshot` | 需 | 全量实体的分页快照 |
| 15 | `GET /api/v1/devices` | 需 | 已知设备列表 |
| 16 | `DELETE /api/v1/devices/<id>` | 需 | 吊销设备 |
| 17 | `GET /ws` | 需 | WebSocket 升级（事件见 05 域） |

> 静态路径 **MUST** 排在参数路径之前（`/tasks/active` 必须先于 `/tasks/<taskId>` 匹配），否则该端点会被当成一个 taskId 去查库并返回 `404`。

### 1.3 逐条端点

#### 1.3.1 `GET /api/v1/info`
- 鉴权：**免 token**。请求无参数。
- `200`（9 键恒在）：`device_id`(str) / `device_name`(str) / `platform`(str，**恒 `"windows"`**) / `protocol_version`(int) / `app_version`(str) / `ai_configured`(bool) / `active_collection_id`(str｜null) / `active_collection_name`(str｜null) / `capabilities`(str[]，见 11 域)。
- 错误：`426 version_mismatch`。
- 幂等：只读，**MAY** 任意频率轮询（客户端也用它探测主机是否在线）。
- `active_collection_id == null` 表示主机当前未选合集：客户端 **MUST NOT** 在此状态下发起识别；服务端另有 `409 no_active_collection` 兜底。

#### 1.3.2 `POST /api/v1/pair`
- 鉴权：免 token。请求 JSON：`code`(str，必填) / `device_id`(str，必填) / `device_name`(str，必填) / `platform`(str，缺省 `"android"`) / `app_version`(str，缺省 `""`)。四个必填项任一为空串 → `400`（值会被按字符串宽容转换，数字型 `code` 也接受——现状）。
- `200`：`token`(str，64 位小写十六进制) / `server_device_id`(str) / `server_name`(str) / `protocol_version`(int)。
- `409 already_paired`：同一 `device_id` 再次配对。body 与 `200` **完全相同**，额外带 `already_paired: true`。旧 token **立刻作废**，客户端 **MUST** 把 409 当成功解析；重新配对还会清掉该设备的吊销状态。
- 错误：`400 invalid_request`（body 不是合法 JSON，或必填字段缺失/为空）、`401 invalid_code`、`401 code_expired`（带 `retry_after_seconds`，区间 0–60）、`429 rate_limited`（带 `retry_after_seconds`）、`426`。
- 判定顺序（现状，可依赖）：JSON → 字段 → 锁定期 → 每分钟限速 → 比对配对码 → 比对有效期。因此「码已过期但填错」返回的是 `invalid_code`。
- 计数：每次尝试都占一分钟窗口的 1 个名额（无论成败）；连续失败 10 次 → 锁定 60 秒（窗口内一律 `429` 并带剩余秒数）。失败计数与锁定期落 `settings` 表，**重启不清零**；配对成功清零。
- 幂等：**非幂等**（每次成功都签发新 token 并使上一个失效）。配对码有效期 5 分钟，过期后由主机侧刷新（非 HTTP 动作）。

#### 1.3.3 `POST /api/v1/images`
- 鉴权：需。请求：`multipart/form-data`，唯一有效字段名 **`file`**（JPEG 字节）。存在多个 `file` part 时只处理**第一个**。
- `200`：`image_hash`(str，64 位小写十六进制) / `size`(int，收到的字节数) / `mime`(str，**恒 `"image/jpeg"`**，不嗅探内容) / `width`(int｜null) / `height`(int｜null) / `existed`(bool)。
- 错误：`400 invalid_request`（不是 multipart / 没有 `file` 字段 / 文件为空）、`413 payload_too_large`（单文件 > 2097152 字节）、`429 rate_limited`（带 `retry_after_seconds`）、`401`、`426`。
- 体积判定：先按 `Content-Length` 做一次预检（声明值 > 上限 + 64 KiB 时直接 `413`，不读正文），分块传输（无 `Content-Length`）时退化为**边收边计数**，一超限立刻 `413` 且不再继续读——**MUST NOT** 把整包读进内存再判大小。返回 4xx/413 前会尽量把已在路上的请求体吞掉（最多等 300 毫秒），避免客户端拿到「连接被重置」而不是错误码。
- 幂等键：**内容的 sha256**。同一字节序列重复上传返回同一 `image_hash`、`existed: true`，不产生第二份文件；`existed` 反映的是 `images` 表里**是否已有该 hash 的行**（不是文件是否已存在）。
- 现状：`width`/`height` 在本端点**从不计算**，只在「该 hash 已有带尺寸的行」时回填，首次上传恒为 `null`。`local_path` 被写成主机本地路径，且这一列**不参与同步**。
- 计数：额度在其它校验**之前**扣（限流按调用设备每分钟 30 次），因此被 `413`/`400` 拒绝的请求同样消耗额度。

#### 1.3.4 `GET /api/v1/images/<hash>`
- 鉴权：需。`hash` **MUST** 是 64 位**小写**十六进制（`^[0-9a-f]{64}$`）；形态不符（含大写、含路径分隔符或 `..`）直接 `404 not_found`，**且不会去碰任何文件**。哈希会直接用作文件名，这条形态校验是安全边界，**MUST NOT** 放宽。
- `200`：原始字节，`content-type: image/jpeg`（恒为此值）。文件不存在或读不出 → `404 not_found`。
- 幂等：只读。无范围请求支持，不消耗上传额度。现状不按上传者隔离：任何已配对设备可读任意 hash。

#### 1.3.5 `POST /api/v1/tasks`
- 鉴权：需。

| 字段 | 类型 | 必填 | 缺省 | 现状语义 |
|---|---|---|---|---|
| `task_id` | str | 是 | — | 幂等键，由发起端生成；空 → `400` |
| `image_hashes` | str[] | 二选一 | — | 多页页序，长度 1–6；非数组时退回单页 `image_hash`；空串元素被剔除 |
| `image_hash` | str | 二选一 | — | 单页图片；同时是会话首页与结果复用的键 |
| `source_device` | str | 否 | 主机自己的 `device_id` | 仅落库 |
| `collection_id` | str | 否 | 主机当前选中合集 | 空串等同缺省 |
| `force_reanalyze` | bool | 否 | `false` | 仅严格等于 `true` 时为真；为真则跳过结果复用 |
| `created_at` | int | 否 | — | **现状被完全忽略**（服务端用自己的时钟落库） |

- 校验顺序（可依赖）：JSON → `task_id` 与页序非空 → 页数 ≤ 6（否则 `400 too_many_pages`）→ 每一页的 hash 必须已存在于 `images` 表（否则 `400 invalid_request`）→ 合集存在性（缺省与显式值都为空、或指向不存在/已删合集 → `409 no_active_collection`）→ 队列深度（`status = queued` 的行数 ≥ 20 → `429 queue_full`，带 `retry_after_seconds: 10`）。
- `202`：`status`(str) / `session_id`(str｜null) / `question_count`(int｜null；新建任务是 `0`，命中复用时是既有题目数，会话不存在时为 `null`) / `cached`(bool)。`status` 取值 `queued`｜`analyzing`｜`done`｜`failed`｜`cancelled`。
- **状态码恒为 `202`**：命中复用（`cached: true`）、重复 `task_id` 也都是 202，真实状态只在 `status` 字段里。
- 幂等键：`task_id`。重复提交返回既有状态、不重复分析；若该任务仍是 `queued`/`analyzing`，会顺带再踢一次队列（关闭「入队后无人执行」的竞态）。
- 结果复用（现状）：同一 `image_hash` 且有 `status = done` 的会话、页序与本次**逐位相同**、且非强制重跑 → 直接以该会话登记任务并返回 `status: done`、`cached: true`。候选会话只在**最近 500 条**里找，更旧的会话不会被复用。

#### 1.3.6 `GET /api/v1/tasks/active`
见 1.4.1（语义专章）。

#### 1.3.7 `GET /api/v1/tasks/<taskId>`
- 鉴权：需。只读、幂等（**MAY** 在任务进行中按秒级间隔轮询）。
- `200`：`task_id`(str) / `status`(str) / `session_id`(str｜null) / `error_code`(str｜null) / `error_message`(str｜null) / `session`(对象；**为空时整个键被省略**)。
- `session` 只要会话行存在就带（不限 `done`），内容是会话全字段 + `image_hashes`(str[]) + `image_count`(int) + `questions`(对象数组)。
- `error_message` 取自**会话行**的 `error_message`（不是任务行），会话不存在时恒为 `null`。
- `404 not_found`：`tasks` 表里没有该 taskId。**主机本机截屏不建 `tasks` 行**，客户端拿不到它的 taskId，只能走 `/tasks/active`。

#### 1.3.8 `POST /api/v1/tasks/<taskId>/retry`
- 鉴权：需。无请求体（带了也忽略）。
- `200`：`{"status":"queued"}` —— **恒定值**，不是任务当前状态；客户端 **MUST NOT** 拿它当状态来源。
- 生效条件：只有 `status = failed` 的行会被改回 `queued` 并清空任务行的 `error_code`；其它状态返回 `200` 但**什么都不改**。会话行的 `error_code`/`error_message` 不会被清空，直到本次执行结束写回。
- `404 not_found`：任务不存在。无幂等键：重试不创建新任务。

#### 1.3.9 `POST /api/v1/sessions/<sessionId>/reanalyze`
- 鉴权：需。无请求体。
- `202`：`status`(str) / `session_id`(str，**新**会话) / `task_id`(str，**新**任务 id，UUID 形态；响应**包含 `task_id`**) / `question_count` **恒 `null`** / `cached` **恒 `false`**。`task_id` 由服务端生成并回传，客户端可直接用它轮询 `GET /tasks/<taskId>`（`tasks.ndjson` 16、17 钉住）。
- 页序取自该会话的 `session_images`；没有页行时退回 `sessions.image_hash`。`source_device` = 调用者。
- 强制重新调用分析（不复用缓存），因此每次调用都真实消耗一次分析额度并产生新会话。
- `404 not_found`：会话不存在。**幂等：无**。
- **已在 v1 拦住**：无页序行 / 首图 hash 为空 / 页图在 `images` 表无行 → `409 invalid_request`（`原图已不在电脑上，无法重新识别`），不再走未捕获异常的非协议 500（`sync.ndjson` 31 用「没有页序行」的会话钉住）。

#### 1.3.10 `GET /api/v1/collections`
- 鉴权：需。`200`：`collections`(对象数组) / `active_collection_id`(str｜null)。
- 条目字段：`collection_id` / `name` / `field_clocks_json`(对象) / `created_at` / `updated_at` / `updated_by` / `lamport` / `deleted_at`(int｜null，本列表里恒为 null)。
- 现状：只含未删除行（`deleted_at IS NULL`），按 `created_at` **倒序**，最多 500 条。只读、幂等。

#### 1.3.11 `POST /api/v1/collections/<id>/select`
- 鉴权：需。无请求体。`200`：`{"active_collection_id":"<id>"}`。
- `404 not_found`：合集不存在或已是墓碑。
- 副作用（**MUST**）：把选中项写入 `settings`（键 `active_collection_id`），并向所有已连接客户端广播 `collection_changed`（`collection_id` / `collection_name`）。
- 幂等：重复选中结果一致（但每次都广播）。

#### 1.3.12 `POST /api/v1/sync/ops`
- 鉴权：需。请求：`ops`(对象数组，**必填**；不是数组 → `400 invalid_request`)。元素字段：`op_id` / `device_id` / `lamport`(int) / `entity`(`session`｜`question`｜`image`｜`device`｜`collection`｜`session_image`｜`snapshot`) / `entity_id` / `op_type`(`upsert`｜`delete`) / `fields_json`(对象) / `created_at`(int)。
- `200`：`applied`(int) / `rejected`(int)。
- 逐条规则（**MUST**，现状）：
  1. 非对象元素：**静默跳过**（不计入任何计数）。
  2. `device_id` 等于**主机自己的** `device_id`：**静默跳过**（主机自己的 op 本来就在本地日志里；放行等于让任何已配对设备用高 `lamport` 冒充主机改写数据）。
  3. `device_id` 不等于**调用者认证身份**：计入 `rejected`、不落库（防止设备间互相冒名）。
  4. `op_id` 已存在：按幂等忽略，且**不计入 `applied`**。
  5. 通过上述检查的 op 才落 `sync_ops` 并做字段级 LWW 落地；`applied` 只统计真正新写入的条数。
- LWW 判定（**MUST**，细节见 06 域）：按**字段**比较写入时钟 `(lamport, device_id)`——`lamport` 大者胜，相等时 `device_id` **字典序大**者胜；落败的写入被跳过且不影响行级 `lamport`。墓碑与更新互相收敛：一行有墓碑时来一条**不含** `deleted_at` 的更晚写入，墓碑作废（删除输、行复活）。
- 幂等键：`op_id`。重试 **MUST NOT** 更换 `op_id`。

#### 1.3.13 `GET /api/v1/sync/ops`
- 鉴权：需。查询参数：`from_device`(str，**必填**，空 → `400 invalid_request`)、`since_lamport`(int，缺省 `0`)、`cursor`(int，缺省 = `since_lamport`)。
- 语义（**MUST**）：`from_device` 是「要拉谁的 op」，即 op 产生者的 `device_id`——拉主机自己的改动就填 `/info` 的 `device_id`。查询等价于在 `sync_ops` 上取 `device_id = from_device AND lamport > cursor ORDER BY lamport ASC`。
- `cursor` **覆盖** `since_lamport`；只给 `since_lamport` 时它就是初始游标。游标是**排他**的（`lamport > cursor`）；排序**只按 `lamport`**，续页 **MUST** 用上一页的 `next_cursor`。
- `200`：`ops`(对象数组，元素与 1.3.12 的请求元素同构) / `has_more`(bool) / `next_cursor`(int｜null；本页最后一条的 `lamport`，空页为 `null`)。
- 页大小：默认 **500** 条（服务端可配置）；实现多取 1 条判 `has_more`，因此恰好满页时 `has_more` 为 `true`，需要再拉一次空页才收敛。
- 幂等：只读。客户端 **MUST** 循环到 `has_more = false`，并防御 `next_cursor` 为 null 或空页（否则死循环）。
- 客户端游标是**每对端一份**的本地状态（存在 `settings` 表的 `pull_cursor:<device_id>` 键），**MUST NOT** 与「对端已确认收到我的 op」的水位混用。

#### 1.3.14 `GET /api/v1/sync/snapshot`
见 1.4.4（分页语义）。

#### 1.3.15 `GET /api/v1/devices`
- 鉴权：需。`200`：`devices`(对象数组)。条目：`device_id` / `name` / `platform` / `paired_at` / `last_seen_at`(int｜null) / `revoked_at`(int｜null) / `app_version`(str｜null)。`token_hash` **绝不出现在任何响应里**（**MUST**）。
- 含已吊销设备（不过滤）。只读、幂等。现状：鉴权与其它端点相同，任何已配对设备都可读——不是「仅主机本机可用」。

#### 1.3.16 `DELETE /api/v1/devices/<id>`
- 鉴权：需。无请求体。`200`：`{"revoked":"<id>"}` —— 对**不存在**的 id 也返回同一 body（无存在性校验，恒 200）。
- 副作用（**MUST**）：写 `devices.revoked_at`，广播 `device_revoked`，并**主动断开**该设备的 WS 连接；此后该 token 一律 `401 revoked`。
- 现状：**任何已配对设备都能吊销任何设备**（无角色与所有权校验）。幂等：重复吊销结果一致。

#### 1.3.17 `GET /ws`
- 同端口升级，鉴权取自与 HTTP 相同的 `Authorization: Bearer`；握手失败返回 `401`（`unauthorized` 或 `revoked`）。设备身份由 token 反查，**请求里的 `X-QS-Device-Id` 头现状被忽略**。
- 现状：`/ws` **跳过版本协商**（不做 `426` 判定），版本不一致的客户端仍能建连。
- 事件、心跳、重连与补偿规则属 05 域；本域只登记路径与鉴权。

### 1.4 语义专章

#### 1.4.1 `GET /api/v1/tasks/active`（只读探测 / 手机每秒轮询的兜底）
- 语义：**只读快照**，回放主机**最近一次广播出去**的任务状态；不创建任务、不改业务数据（仅鉴权时刷新「最近活跃」时间）。客户端在前台默认**每秒**调一次，作为推送不可靠时的兜底。
- 为什么必须单独存在：主机**本机截屏任务不经过任务队列**——`tasks` 表没有行，客户端拿不到它的 `task_id`，无法用 `GET /tasks/<id>` 取回结果。
- `200` 响应字段（除 `session` 外**恒在**）：`status`(`idle`｜`queued`｜`analyzing`｜`done`｜`failed`) / `task_id`(str｜null) / `session_id`(str｜null) / `image_count`(int，缺省 `0`；上报值 ≤ 0 时用会话页数补齐) / `updated_at`(int 毫秒；主机记下这次状态的时刻，从未有过任务时为 `0`) / `message`(str｜null；仅 `status = failed` 且有会话时才有值：取会话 `error_message`，其值为空时用「分析失败」；会话缺失或被删时为 `null`) / `active_collection_id`(str｜null) / `active_collection_name`(str｜null) / `collections`(对象数组) / `ops_lamport`(int) / `session`(对象；**仅** `status = done` 且会话存在且未删除时出现，否则键被省略)。
- 状态是**内存态**：主机重启后回到 `idle`，不补发历史结果（与「WS 只补发进行中任务」一致）。
- `session` 与 WS `task_result` 的 `session` **同构**，客户端可复用同一条落地逻辑。
- `collections` = 主机**当前未删除**的合集列表，条目与 `GET /api/v1/collections` 同构。客户端 **SHOULD** 把它镜像进本地库，规则是**只增改、不删**：主机已删的合集不再出现在列表里，客户端**保留**本地那份；客户端本机已删的行也 **MUST NOT** 被复活。空数组 = 老主机没上报（客户端不动）。
- `ops_lamport` = 主机 `sync_ops` 的最大 `lamport`（`MAX(lamport)`，空表为 `0`）。客户端 **SHOULD** 仅在它比上次大时做一次**只拉不推**的 `GET /sync/ops`，首次看到只建立基线。它与 `collections` 是两条独立通道：`collections` 是「想要的结果」，`ops_lamport` 是「有改动」的信号。
- 客户端约定（现状）：**第一次**探测就拿到 `done`/`failed` 时只当基线、不自动跳结果页；已经投递给界面的同一条结果不重复推。

#### 1.4.2 `GET /tasks/<id>` 的 done / failed 语义
- `done`：`session` 一定带 `questions` 与 `image_hashes`；`error_code`/`error_message` 为 `null`。
- `failed`：`error_code` 取自**任务行**，取值 `ai_timeout`｜`ai_auth`｜`ai_rate_limited`｜`ai_bad_response`｜`ai_quota_exceeded`｜`no_question_found`｜`internal`；`error_message` 取自**会话行**，会话不存在时为 `null`。客户端 **MUST** 按 `error_code` 出文案。
- `queued`/`analyzing`：会话行已存在（提交时就建），因此也会带 `session`，此时 `questions` 通常为空。客户端 **MUST** 以 `status` 判终态，而不是以 `session` 是否存在判。

#### 1.4.3 `GET /sync/ops` 的 `from_device` 与 `since_lamport` 游标
- `from_device` 是**来源设备**维度（不是「我」也不是「发给谁」）；每个来源设备各自一条独立游标序列。
- `since_lamport` 是**排他下界**：返回 `lamport` **严格大于**它的 op。它是断点续拉的简写形式，分页时用 `cursor` 覆盖它。
- 同一来源设备的 `lamport` 唯一且单调；跨设备不可比，也**MUST NOT** 混用一条游标。
- 只传 `since_lamport` 而不传 `cursor` 时，页内每拉一页都要把 `cursor` 更新为上页的 `next_cursor`；拉完后客户端把游标推进到**已应用 op 的最大 `lamport`**（推进时机在落地成功之后，避免丢 op）。
- 空页时 `next_cursor` 为 `null`：客户端此时 **MUST** 用「已应用 op 的最大 `lamport`」或上一页的 `next_cursor` 推进自己的 `since_lamport`，**MUST NOT** 把它重置为 `0`（那会从头重拉）。

#### 1.4.4 `GET /api/v1/sync/snapshot` 的分页
- 查询参数：`limit`(int，缺省 **200**，超出即 **clamp 到 1–1000**)、`offset`(int，缺省 **0**，clamp 到 0–2^30)。
- 分页对象：`sessions`（未删除行，按 `created_at ASC, session_id ASC` 稳定排序，**决定分页边界**）；`questions` 与 `session_images` 只含**本页涉及的会话**。
- 不分页、每次都全量的三张表：`images`（全部元数据，**不含 `local_path`** —— 本地专属列不进同步载荷）、`devices`（全部，无 `token_hash`）、`collections`（未删除，最多 500 条）。
- `200`：`sessions` / `questions` / `session_images` / `collections` / `images` / `devices` / `watermark`(int，主机 `MAX(lamport)`) / `has_more`(bool) / `next_offset`(int｜null，= `offset` + 本页会话数，`has_more = false` 时为 `null`)。
- 客户端 **MUST** 用 `next_offset` 续拉，**MUST NOT** 自行 `offset += limit`（末页不足一页时会跳页）。
- 幂等：只读。用途是新设备 bootstrap；响应里**不含 `sync_ops` 本身**，只有 `watermark` 水位。大库上全量字段（`images`/`devices`）会显著放大响应体积。

#### 1.4.5 v2 目标（**变更**，尚未实现）
1. `POST /tasks` 在主机未配置 AI 时**前置**拒绝（如 `503 ai_not_configured`），不再「先 202、再异步必失败」（见 11 域）。
2. `POST /sessions/<id>/reanalyze` 的响应收敛到与 `POST /tasks` 同一形状（真实 `status`、`question_count`，而不是恒 `queued`/`null`）。**v1 已补**：响应带 `task_id`，「无原图」由 500 路径改为 `409 invalid_request`。
3. `POST /tasks/<id>/retry` 返回任务**真实状态**，并对非 `failed` 的任务返回 `409 invalid_state`。
4. `GET /sync/snapshot` 的 `images`/`devices`/`collections` 改为可增量（按水位）并可分页；`has_more` 覆盖全部集合。（`local_path` 已在 v1 从同步载荷剔除，见 §1.4.4。）
5. `DELETE /devices/<id>` 对不存在的 id 返回 `404`，并引入角色校验（仅主机或该设备自己）。
6. `GET /sync/ops` 的 `since_lamport` 与 `cursor` 收敛为单一游标参数。

## 2. 数值表

| 项 | 值 | 备注 |
|---|---|---|
| 默认端口 / 探测范围 | 8765 / 8766–8770 | 全部占用则启动失败 |
| 图片单文件上限 | 2097152 字节（2 MiB） | 预检余量 +64 KiB（65536 字节） |
| 图片响应 `mime` | `image/jpeg`（恒定） | 不嗅探 |
| `hash` 形态 | `^[0-9a-f]{64}$` | 大写视为不合形态 → 404 |
| 上传限流 | 30 次/分钟/设备，窗口 60000 毫秒 | 失败请求同样计数 |
| 任务页数上限 | 6（超出 `400 too_many_pages`） | 客户端本地设置默认同为 6，但那是本地设置、不是协商结果 |
| 任务队列深度 | 20 行 `status = queued`（超出 `429 queue_full`，`retry_after_seconds: 10`） | |
| 结果复用候选扫描 | 最近 500 条会话 | 更旧的会话不复用 |
| `sync/ops` 页大小 | 500 条（可配置） | 多取 1 条判 `has_more` |
| `sync/snapshot` 分页 | `limit` 默认 200，clamp 1–1000；`offset` 默认 0，clamp 0–2^30 | |
| 合集列表上限 | 500 条（按 `created_at` 倒序） | `/collections` 与快照同值 |
| 配对码有效期 | 300000 毫秒（5 分钟） | 由主机侧刷新 |
| 配对尝试限速 | 5 次/分钟 | 第 6 次起 `429` |
| 配对失败锁定 | 连续 10 次失败 → 锁定 60000 毫秒 | 计数落库、重启不清零 |
| `code_expired` 的 `retry_after_seconds` | 0–60 | 实测范围由实现钳制 |
| token 形态 | 64 位小写十六进制（32 字节随机） | 服务端只存摘要 |
| 协议主版本 | 1（不一致 → `426 version_mismatch`；无版本头放行） | |

## 3. 一致性向量覆盖

| 向量 | 覆盖的本文章节 |
|---|---|
| `auth.ndjson` 1 | 1.3.1（免鉴权、`capabilities` 集合、`active_collection_id: null`） |
| `auth.ndjson` 2–3 | 1.1 鉴权（缺 token / 认不出 → `401 unauthorized`） |
| `auth.ndjson` 4–5 | 1.3.2（`invalid_code`、200 签发 64 位 hex） |
| `auth.ndjson` 6、12 | 1.3.10（空库列表 + `active_collection_id`） |
| `auth.ndjson` 7–8 | 1.3.2（`code_expired`：向量把时钟推进 300001 毫秒越过 5 分钟 TTL） |
| `auth.ndjson` 9 | 1.3.2（配对码刷新：主机侧动作，非 HTTP） |
| `auth.ndjson` 10 | 1.3.2（409 仍给新 token + `already_paired: true`） |
| `auth.ndjson` 11 | 1.3.2（重新配对后旧 token 立刻失效） |
| `auth.ndjson` 13–19 | 1.3.2（每分钟 5 次窗口、第 6 次 `429` + `retry_after_seconds`） |
| `auth.ndjson` 20 | 1.3.16（`DELETE /devices/<id>` → `{"revoked":"<id>"}`） |
| `auth.ndjson` 21 | 1.1 鉴权（吊销后 `401 revoked`，与 `unauthorized` 区分） |
| `auth.ndjson` 22–23 | 1.1 版本协商（`426`；无版本头放行） |
| `images.ndjson` 1 | 1.3.2 |
| `images.ndjson` 2–3 | 1.3.3（`image_hash`/`size`/`mime`/`existed` 与内容寻址去重） |
| `images.ndjson` 4 | 1.3.4（下载字节的 sha256 == `image_hash`） |
| `images.ndjson` 5 | 1.3.3（第三次上传同一张图 → 仍 `existed: true`；重复上传幂等） |
| `images.ndjson` 6–8 | 1.3.4（不存在 / 非 64 位小写 hex / 目录穿越 → `404 not_found`） |
| `images.ndjson` 9 | 1.3.3（空文件 → `400 invalid_request`） |
| `images.ndjson` 10 | 1.3.3（超限 → `413`；边收边判、不等整包） |
| `images.ndjson` 11–12 | 1.3.3（30 次/分钟；第 31 次 → `429`） |
| `images.ndjson` 13–14 | 1.1 鉴权（上传与下载都要 token） |
| `tasks.ndjson` 3–5 | 1.3.5（`400`：缺 `task_id`、超 6 页、页图未上传） |
| `tasks.ndjson` 7 | 1.3.5（`409 no_active_collection`：指定了不存在的合集） |
| `tasks.ndjson` 8 | 1.3.5（`202` + `status: queued` + `question_count: 0` + `cached: false`） |
| `tasks.ndjson` 9、12 | 1.3.7 / 1.4.2（`done` 的任务视图：题目数在 `session` 里；不存在的任务 `404`） |
| `tasks.ndjson` 10–11 | 1.3.5（`task_id` 幂等、同图同页序复用且 `cached: true`，状态码仍是 `202`） |
| `tasks.ndjson` 13 | 1.3.8（`retry` 对不存在的任务 `404`） |
| `tasks.ndjson` 14–15 | 1.3.5（队列满 → `429 queue_full` + `retry_after_seconds`） |
| `tasks.ndjson` 16–18 | 1.3.9（`reanalyze`：`202` + 新 `task_id` / 新 `session_id` / `question_count: null` / `cached: false`；重跑出的新任务可查到 `done`；不存在的会话 → `404`） |
| `sync.ndjson` 2、6、17 | 1.4.4（快照的 `watermark`；只给活行，墓碑不出现在 `questions` 里） |
| `sync.ndjson` 3–5 | 1.3.12（冒充主机的 op 静默跳过；冒名他人的 op 计入 `rejected`；本机 op `applied`） |
| `sync.ndjson` 10、12 | 1.3.12（LWW：低 `lamport` 被挡；同 `lamport` 时 `device_id` 字典序大者胜） |
| `sync.ndjson` 14 | 1.3.12（同 `op_id` 再投 → `applied: 0`） |
| `sync.ndjson` 18–19 | 1.3.13 / 1.4.3（只返回该设备的 op、按 `lamport` 升序；空页 `next_cursor` 为 `null`） |
| `sync.ndjson` 20 | 1.3.13（缺 `from_device` → `400 invalid_request`） |
| `sync.ndjson` 28–30 | 1.4.4（`limit=1` → `has_more: true`、`next_offset: 1`；末页 `has_more: false`、`next_offset: null`；按 `created_at` 升序拼回） |
| `sync.ndjson` 31 | 1.3.9（给没有页序行的会话重跑 → `409 invalid_request`；原「非协议 500」路径已在 v1 拦住） |
| `sync.ndjson` 32 | 1.4.4（快照 `images` 不含 `local_path`，由 `json_absent` 钉住） |
| `errors.ndjson` 2–4 | 1.1 版本协商（同主版本放行；不同主版本 `426`；**不可解析的值也 `426`**） |
| `errors.ndjson` 5–6 | 1.1 鉴权（空令牌、缺 `Bearer` 前缀 → `401 unauthorized`） |
| `errors.ndjson` 7–9 | 1.3.12（非 JSON、缺 `ops`、body 是数组 → `400 invalid_request`） |
| `errors.ndjson` 10–11 | 1.3.3 / 1.3.4（上传口收到 JSON → `400`；大写 hex 的下载 → `404`） |
| `errors.ndjson` 12 | 1.3.15（`token_hash` 绝不出现在响应里） |
| `errors.ndjson` 13 | 1.1 未知路由（普通 `404`，无协议错误体） |
| `errors.ndjson` 14 | 1.3.11（不存在的合集 → `404 not_found`） |

**仍未被任何向量覆盖（v1 缺口）**：`GET /api/v1/tasks/active`（全字段、`idle` 与 `done` 两态）完全没有向量；`POST /tasks/<id>/retry` 的**成功**路径（恒回 `{"status":"queued"}`、只对 `failed` 生效）只有 404 有；`POST /collections/<id>/select` 的成功路径（200 + `collection_changed` 广播）只有 404 有；`GET /collections` 的非空形态（条目字段）只有空库被覆盖；`GET /images/<hash>` 的 `width`/`height` 为 `null` 这一现状没有断言；`DELETE /devices/<id>` 对不存在 id 仍回 200 没有向量；缺 `file` 字段的上传（400）没有向量；`/ws` 握手（`401`、踢旧连接、`hello` 载荷）没有向量。`POST /sessions/<id>/reanalyze` **不在缺口里**：已由 `tasks.ndjson` 16、17、18 与 `sync.ndjson` 31 覆盖（见上表）。v2 **SHOULD** 优先补：`/tasks/active` 两态、`retry` 的成功路径、`collection_changed` 广播。

> 撰写时的向量状态：`auth.ndjson`、`images.ndjson`、`tasks.ndjson`、`sync.ndjson` 已入库；`errors.ndjson` 当时还是未提交的工作副本（内容已按上表逐条核对过）。

## 4. 文档-实现分叉

以下均为**现契约文本与实现不一致**之处（以实现为准；编号对应本文相应小节）：

1. **鉴权白名单**：现契约称「除配对外的所有 HTTP 请求都必须带 token」，实现里 `/api/v1/info` 同样免鉴权（向量第 1 步已按实现写）。→ 1.1
2. **端点总表缺项**：现契约的端点表没有 `GET /api/v1/tasks/active`（只在正文里单列），也把 `/ws` 完全放在另一节。→ 1.2
3. **`POST /tasks` 响应**：现契约的响应示例只有 `status`/`session_id`/`question_count`，漏了 `cached`；也没写「命中复用与重复提交时状态码仍是 `202`」。→ 1.3.5
4. **`created_at` 是死字段**：现契约把它列为请求字段，实现完全不读（既不做幂等也不做排序）。→ 1.3.5
5. **队列深度上限不存在于文档**：`429 queue_full`（20 行 / `retry_after_seconds: 10`）在现契约里没有。→ 1.3.5
6. **上传响应里的 `width`/`height`**：现契约示例给出 `1600 × 900`，实现从不计算尺寸，首次上传恒为 `null`，`mime` 也恒为 `image/jpeg`（不嗅探内容）。→ 1.3.3
7. **「客户端限流」措辞**：现契约把上传限流写成客户端行为，实现是服务端按调用设备统计，且失败的请求同样消耗额度。→ 1.3.3
8. **`GET /tasks/<id>` 的 `error_message` 来源**：现契约没说它取自会话行，也没说会话不存在时恒为 `null`。→ 1.3.7
9. **`retry` 的响应与生效范围**：现契约没有 `POST /tasks/<id>/retry` 的响应体，实现恒回 `{"status":"queued"}` 且只对 `failed` 生效。→ 1.3.8
10. **`reanalyze` 的恒定字段**：现契约说响应「与创建任务一致」，实现回恒定 `status: queued`、`question_count` 恒 `null`、`cached` 恒 `false`，且 **v1 已补上 `task_id`**（原来不回传，客户端拿到的任务号是空串）。→ 1.3.9
11. **`GET /sync/ops` 的游标**：现契约只写 `since_lamport` 与 `from_device`，实现还有 `cursor`（且覆盖 `since_lamport`）、页大小 500、`has_more`、`next_cursor`。→ 1.3.13
12. **`/sync/snapshot` 的分页**：现契约只说「bootstrap 快照」，实现有 `limit`(默认 200/上限 1000)、`offset`、`has_more`、`next_offset`，且 `images`/`devices`/`collections` 全量、会话内嵌题目只含本页。→ 1.4.4
13. **`POST /sync/ops` 的归属校验**：现契约只写「按 `op_id` 幂等」，实现还要求 `op.device_id` 等于调用者的认证身份（否则计入 `rejected`），并静默丢弃冒充主机的 op。这是安全收紧，文档缺失。→ 1.3.12
14. **WS 握手的 `X-QS-Device-Id`**：现契约把它列为握手头，实现的鉴权只看 `Authorization`，该头被忽略。→ 1.3.17
15. **`GET /devices` 的定位**：现契约标注「仅 Windows 端设置页用」，实现只是一个普通的已鉴权端点（任何已配对设备可读）。→ 1.3.15
16. **`DELETE /devices/<id>` 的权限与存在性**：现契约未写「任何已配对设备都能吊销任何设备」与「不存在的 id 也回 200」。→ 1.3.16
17. **版本头缺失与不可解析的差异**：现契约只写「主版本不一致返回 426」，没写「**空值/完全不带**该头一律放行」，也没写「**值不可解析**（如 `abc`）按不一致处理 → 426」。（实现已固化，`auth.ndjson` 第 23 步与 `errors.ndjson` 第 2–4 步钉住。）→ 1.1
18. **未知路由的 404 不是协议错误体**：现契约的「错误响应统一格式」容易被读成所有错误都有 `code`，实现里没匹配上路由的 404 是普通响应体。→ 1.1
19. **LWW 的同 `lamport` 定序未写**：现契约只写「字段级 LWW」，没写 `lamport` 相等时按 `device_id` 字典序定胜负，也没写「墓碑与更新互相收敛」这一半。→ 1.3.12
