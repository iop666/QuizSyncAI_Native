# v1 协议快照（按实现反推）

> 目的：三端重写时的**对照基线**。本文描述「2026-09 的 v1 实现实际行为」，不是目标形态。
> 规范性规则见 `spec/`；本文只做叙述、汇总与**迁移注意**。
> 覆盖范围：Windows 内置 Host、独立命令行 Host、Android 客户端（三者共用一套线协议）。

**怎么读**：本文的每一条结论都能回到一处证据 —— 一致性向量的「文件 + 步骤」或某个测试名（见第 12 节）。凡两条 Host 实现行为不同的地方，均在第 2 / 10 节显式标注「内置 / 独立」。

---

## 1. 一句话概览

一台 Windows 主机在一个明文端口上同时提供 HTTP 与 WebSocket；手机扫二维码拿到一枚 64 位十六进制 token，此后所有请求带 `Authorization: Bearer <token>`。业务面只有三条链路：

| 链路 | 载体 | 作用 |
|---|---|---|
| 控制/数据面 | `/api/v1/*` 的整包 JSON | 配对、图片内容寻址存取、任务创建与查询、合集、设备、op 推送与拉取、全量快照 |
| 推送面 | 同端口 `/ws` | 只报「刚刚发生了什么」，可能丢、不补发 |
| 状态面 | 秒级轮询 `/api/v1/tasks/active` + `/api/v1/info` | 报「现在是什么状态」，是推送的兜底 |

数据一致性只有一套机制：**操作日志（op）+ Lamport 时钟 + 逐字段 LWW + 软删除墓碑 + 水位游标**；图片以 sha256 内容寻址；识别任务串行执行，**结果（题目、题目数、错误文案）挂在会话上，任务行只有状态**。

---

## 2. 分层图与端口/传输（含两张 Host 实现的关系）

```
Android 客户端 ──HTTP/1.1 明文──┐
      └────────WS /ws──────────┴──► 单端口 8765（占用则依次 8766…8770，共 6 个）
                                     ├─ Windows 内置 Host（随桌面端进程，无独立部署）
                                     └─ 独立命令行 Host（同线协议的另一份实现 +
                                        本机控制面端点，不进 LAN 协议）
```

- **端口**：默认 **8765**，被占用依次试 **8766–8770**；6 个全占用 = **启动失败**，不静默换段。绑定 IPv4 通配地址、**非共享**绑定；实际端口只从配对二维码 / 手输获得（`/info` 里**没有**端口字段）。
- **传输**：全程明文，**不做 TLS**；HTTP/1.1 与 WS 共用同一端口，WS 路径固定 `/ws`。响应无压缩、无流式，大结果靠**参数分页**。
- **两张 Host 的关系**：同一份线协议的两次实现。内置 Host 是「全功能 + 落库 + 队列表 + 字段级 LWW 落地」；独立 Host 是「冻结功能集 + JSON 文件存储 + 串行单任务 + 只按 `op_id` 记账」，并多出两个**只允许回环地址 + 本机控制令牌**的端点（`/api/v1/shutdown`、`/api/v1/console`）。协议面上必须一致的部分是 §5 的 17 条路由；两边的行为差异逐条列在第 10 节第 16 条。
- **超时与心跳**：客户端建连 **5s** / 读 **30s**（发送体不设超时）；服务端不设请求超时。WS 服务端每 **30s** 发 `ping`，客户端「**>40s** 没收到服务端 `ping`」即判死重连（检查周期 5s），另有 20s 间隔的传输层控制帧 ping（服务端不感知）。重连退避 **1/2/5/10/30s**，连上即重置。
- **限流常量**：配对 5 次/分（成功也计数）、连续 10 次失败锁 60 秒；图片上传 30 次/分/设备（滑窗 60000ms）；单图 ≤ **2097152** 字节，`Content-Length` 预检阈值 **上限 + 64 KiB = 2162688**。
- **迁移注意**：端口探测表、`0.0.0.0` 绑定、非共享绑定、WS 与 HTTP 同端口、`/tasks/active` 必须排在 `/tasks/<id>` 之前 —— 这五条在重写时逐字保留。

---

## 3. 身份与配对

- **身份**：v1 只有一种身份 —— **持有未吊销 token 的已配对设备**。协议里没有 host/client 角色字段，服务端**不区分**「主机自己」与「客户端」。
- **固定字面量设备 id**：主机恒自报 `windows-local`，客户端恒自报 `android-local`；不是每安装生成的 UUID。设备注册表以 `device_id` 为主键，于是「同一时间只支持一台安卓」：第二台安卓配对会走「重复配对」分支、覆盖同一行的 token 摘要，第一台的 token 立刻失效。
- **配对码**：6 位十进制（100000–999999）、TTL **5 分钟**（严格 `>` 到期时刻才算过期）、**刷新即换码**、**不落库**（主机重启换新码）、配对成功**不会**让码失效（剩余时间内可继续用）。
- **配对流**（`POST /api/v1/pair`，免鉴权）：判定顺序**可依赖** —— JSON → 必填字段 → **锁定期** → 每分钟窗口 → 码比对 → 有效期 → 签发。因此「锁定期内配对码正确也被拒」「码已过期但填错码回 `invalid_code`」。
- **重复配对** = `409`，body 与 `200` 同构（`token` / `server_device_id` / `server_name` / `protocol_version`）**外加 `already_paired: true`，且没有 `code` 字段**；旧 token 立刻作废；`paired_at` 保留首次值；重新配对会**清空吊销状态**（吊销可被自助解除）。客户端把 200 与 409 **都当成功**解析。
- **限流与锁定**：窗口内第 6 次 → `429 rate_limited`；连续 10 次失败（只算 `invalid_code` / `code_expired`）→ 锁 60 秒；失败计数与锁定到期时刻**落库、跨重启不清零**（内置 Host 的硬要求）。**独立 Host 只存在内存**，重启即清零 —— 见第 10 节第 16 条。
- **配对链接**：`quizsync://pair?host=<地址>&port=<端口>&code=<码>&sid=<主机 id>&v=1`；客户端只认这个 scheme，`v` 被忽略，`sid` 缺失按空串。
- **令牌**：32 随机字节 → 64 位小写十六进制；服务端只存摘要，明文只在配对响应出现一次；**没有有效期**，只有「重新配对换新」或「吊销」两种终结。没有 token / 认不出 → `401 unauthorized`；命中但已吊销 → `401 revoked`（两者必须区分）。
- **零隔离现状（不是推荐做法）**：任何已配对设备都能读全库、给任意 hash 下单识别、看所有设备、并且**吊销任意设备**。唯一的额外约束是 op 推送的归属校验（第 4 节）。
- **迁移注意**：主机界面必须给出正确换机顺序「**先吊销旧设备，再用新手机扫码**」；直接配对新手机，旧手机会立刻掉线且只在收到 `revoked` / `device_revoked` 时才提示重新配对，否则表现为「电脑未连接」。

---

## 4. 数据模型与同步（op/Lamport/逐字段 LWW/墓碑/水位）

**op 载荷**（`POST /api/v1/sync/ops` 的元素，也是拉取响应的元素）固定 8 键：`op_id` / `device_id` / `lamport` / `entity` / `entity_id` / `op_type` / `fields_json` / `created_at`。

- `entity` 七种：`session` / `question` / `image` / `device` / `collection` / `session_image` / `snapshot`（认不出回落 `snapshot`）；`entity_id`：多数实体是 UUIDv4，`image` 用 `image_hash`，`snapshot` 是固定串 `"global"`。`op_type`：`upsert` / `delete`（认不出回落 `upsert`）。
- `fields_json` **只含本次真正改动的字段**（键与实体列名同形），差异为空则**不产生 op**；业务行与 op 在同一事务内落库。`delete` op 固定带 `deleted_at`。
- **Lamport**：本机产生 op 时 `本地计数 + 1`；收到对端 op 时先幂等判重（重复 `op_id` 直接返回、**不推进时钟**），再 observe：`本地计数 = max(本地, 收到) + 1`。启动时用已见最大 `lamport` 恢复计数。**任何因果判定都只看 `lamport`，不看时间戳**（时间戳只用于展示与排序）。
- **逐字段 LWW**：比较键是 `(lamport, device_id)`，`lamport` 大者胜、相等时 `device_id` **字典序大**者胜；**相等即判负**、跳过该字段。每行维护 `field_clocks_json = {"<列名>": {"l": <lamport>, "d": "<device_id>"}}`，只在字段被接受时写入；字段没有独立时钟时**回落**到行级 `lamport` / `updated_by` 作基准；行级 `lamport` / `updated_by` = 该行所有字段时钟的最大值。
- **只有四种实体做字段级 LWW**：`session` / `question` / `collection` / `session_image`。`image` / `device` 按主键合并、后到写入。
- **本地专属列**：图片的本地路径 `local_path`、设备的令牌摘要 `token_hash` **永不进 op**，远端 op 里带上也要丢弃；设备行的 `revoked_at` 一旦非空，**不得**被不含该键的远端 op 清掉。
- **墓碑与收敛**：删除是软删除（写 `deleted_at` + 一条 `delete` op），已删行再删**不产生**第二条 op；一切读取都带「未删除」条件。行已有墓碑、来的 `upsert` **不含** `deleted_at` 且写入比墓碑新 → **墓碑作废（删除输、行复活）**，两个方向都收敛到同一状态。
- **用户手改优先（链路存在、但永不触发）**：手改答案/解析会置 `answer_edited` / `analysis_edited` 并**显式带进 op**；收到未携带标记的 upsert 而本行标记为 1 → 该组字段跳过（保护），被跳过的内容记成本地待决项 `pending_overwrite:<题目 id>`（不同步）。**两端 UI 都没有修改答案/改解析的入口**，这条链路在真实运行中永不触发（第 10 节第 14 条）。
- **水位有三条，语义不同、不可混用**：`sent_lamport`（我推给它的最大 lamport）、`acked_lamport`（它确认收到我的最大 lamport）、拉取游标（我从它那里拉到的最大 lamport，存在本地设置的 `pull_cursor:<设备 id>`）。**只有 WS 的 `ack` 能推进 `acked_lamport`**，而生产路径从不发 `ack`（见第 6 节）；`POST /sync/ops` 的响应**没有**水位字段。
- **推送只取本机产生的 op**（`device_id = 本机 AND lamport > sent`，按 `lamport` 升序、每批最多 500）；从对端拉来转存的 op **不得**再推回去。
- **归属校验**（`POST /sync/ops` 逐条计数）：非对象元素 → 静默跳过；`device_id` = **主机自己的 id** → **静默跳过**（`applied` / `rejected` 都不计）；`device_id` ≠ 调用者 → `rejected++`、不落库；`op_id` 已存在 → 按幂等忽略、**不计 `applied`**。`applied` 的含义是「op 新入库」，**被 LWW 判负的 op 也算 applied**。
- **快照**（`GET /sync/snapshot`）：分页对象**只有会话**（按 `created_at ASC, session_id ASC`），`questions` / `session_images` 只含**本页会话**；`images` / `devices` / `collections` **每次全量**；`limit` 默认 200 / clamp 1–1000，`offset` clamp 0–2^30；`watermark` = 主机 `MAX(lamport)`；`images` 已剔除 `local_path`。客户端**必须**用 `next_offset` 续拉，不能自己 `offset += limit`。
- **两处用户拍板的不对称（原样保留）**：
  1. **合集删除方向不对称**：主机删合集 → 客户端**不跟随删**（远端 collection 的 delete 只记账、不落 `deleted_at`）；客户端删合集 → 正常同步给主机、主机按正常删除落地；**合集镜像**（把主机上报的活跃合集列表写进本地库）**只增改、不删**，不产生 op、不改行级时钟，本机已删的合集**绝不复活**。
  2. **同图缓存/去重只对单页生效**：任务复用要求页序**长度相同且逐位相同**（多页必须完全一致，单页退化为「同 hash 即复用」，候选窗口**最近 500 条**会话）；分析缓存也**只对单图调用**生效（键 = `image_hash` + prompt 版本 + 模型，命中且距今 < **30 天**）。

---

## 5. 端点总表（17 条，含鉴权与状态码）

| # | 方法 + 路径 | 鉴权 | 成功 | 主要失败码 |
|---|---|---|---|---|
| 1 | `GET /api/v1/info` | **免** | 200（9 键，含 `capabilities`） | 426 |
| 2 | `POST /api/v1/pair` | **免** | 200 / **409**（非错误体） | 400 / 401 `invalid_code` / 401 `code_expired` / 429 / 426 |
| 3 | `POST /api/v1/images` | 需 | 200（`image_hash`/`size`/`mime`/`width`/`height`/`existed`） | 400 / 413 / 429 / 401 / 426 |
| 4 | `GET /api/v1/images/<hash>` | 需 | 200（原始字节，`image/jpeg`） | 404（形态不符也是 404）/ 401 |
| 5 | `POST /api/v1/tasks` | 需 | **恒 202** | 400 / 400 `too_many_pages` / 409 `no_active_collection` / 429 `queue_full` |
| 6 | `GET /api/v1/tasks/active` | 需 | 200（只读探测；`done` 时直接带 `session`） | 401 |
| 7 | `GET /api/v1/tasks/<taskId>` | 需 | 200 | 404 |
| 8 | `POST /api/v1/tasks/<taskId>/retry` | 需 | **恒 200 `{"status":"queued"}`** | 404 |
| 9 | `POST /api/v1/sessions/<sessionId>/reanalyze` | 需 | 202 | 404 / 409 `invalid_request`（原图不在主机上） |
| 10 | `GET /api/v1/collections` | 需 | 200 | 401 |
| 11 | `POST /api/v1/collections/<id>/select` | 需 | 200（并广播 `collection_changed`） | 404 |
| 12 | `POST /api/v1/sync/ops` | 需 | 200 `{applied, rejected}` | 400 / 401 |
| 13 | `GET /api/v1/sync/ops` | 需 | 200 `{ops, has_more, next_cursor}` | 400（缺 `from_device`） |
| 14 | `GET /api/v1/sync/snapshot` | 需 | 200（分页；见第 4 节） | 401 |
| 15 | `GET /api/v1/devices` | 需 | 200（**不含**令牌摘要） | 401 |
| 16 | `DELETE /api/v1/devices/<id>` | 需 | **恒 200 `{"revoked":"<id>"}`**（不存在的 id 也 200） | 401 |
| 17 | `GET /ws` | 需 | 101 升级 | 401（`unauthorized` / `revoked`） |

- 免鉴权只有第 1、2 两条；其余全部先过 `Authorization: Bearer`。**鉴权先于版本协商**。
- 未匹配任何路由 → **404，但不是协议错误体**（响应里没有 `code`），客户端不得假设所有 404 都带 `not_found`。
- 独立 Host 另有两条**只允许回环地址 + 本机控制令牌**的端点（`POST /api/v1/shutdown`、`POST /api/v1/console`），它们**不属于**这 17 条 LAN 协议面。
- `GET /tasks/<id>` 的视图只有 `task_id` / `status` / `session_id` / `error_code` / `error_message` / `session`：**没有 `question_count`**（题目数在 `session` 里）；`error_message` 取自**会话行**，会话不存在时恒 `null`。`session` 键在会话行为空时**整个省略**。

---

## 6. WebSocket 事件总表（两方向，标注哪些是死分支）

**服务端 → 客户端**（一条文本帧 = 一个 JSON 对象，`type` 是唯一判别键）：

| `type` | 触发 | 关键字段 | 现状 |
|---|---|---|---|
| `hello` | 建连后（异步发出，**不保证是第一帧**） | `server_device_id`（主机自己）/ `protocol_version` / `active_collection_id` / `active_collection_name` | 生产路径发 |
| `task_update` | 任一任务状态变化（手机提交的与本机截屏的都会发） | `task_id` / `status` / `session_id`（为 null 时**键被省略**）/ `image_count` | 生产路径发 |
| `task_result` | **仅** `status = done` 且会话存在**且未删除** | `task_id` / `session`（会话全字段 + `image_hashes` + `image_count` + `questions`） | 生产路径发 |
| `task_failed` | **仅** `status = failed` | `task_id` / `error_code`（会话缺失时 `internal`）/ `message`（缺失或空时「分析失败」） | 生产路径发 |
| `collection_changed` | 切换当前合集 | `collection_id` / `collection_name`（键恒在、可为 null） | 生产路径发 |
| `device_revoked` | 任一设备被吊销 | `device_id` | 生产路径发 |
| `ping` | 每 30s | `ts`（毫秒） | 生产路径发 |
| **`ops`** | —— | —— | **死分支：服务端从不构造它**（旧契约有、实现没有；客户端解析层保留了 `case 'ops'`，永不命中） |

广播语义：除 `hello` / `ping` 外一律发给**当时所有已连接客户端**，没有订阅、没有按设备过滤。建连后若主机手上有**进行中**的本机任务，额外补发一次 `task_update`（**只补进行中**，已完成/失败的不补）。

**客户端 → 服务端**：

| `type` | 关键字段 | 现状 |
|---|---|---|
| `hello` | `device_id` / `app_version` / `platform` | 生产路径发（服务端只用它刷新「最近活跃」与 `app_version`，**忽略** `device_id` 与 `platform`） |
| `pong` | `ts`（回显） | 生产路径发；**服务端不校验 `pong`、不因缺 `pong` 断开** |
| `ack` | `watermark_lamport` / `watermark_device` | **死分支：生产路径从不发**（方法存在、只被集成测试调用）。服务端只读 `watermark_lamport`，且内置 Host 会据此写对端水位；**独立 Host 的 `ack` 是空操作** |
| `push_ops` | `ops`（元素与 HTTP 同构） | **死分支：生产路径从不发**（op 一律走 HTTP）。服务端实现完整（同归属规则 + 回一条 `ack`），只被集成测试驱动过 |

**其它现状**：`/ws` **绕过版本协商**（主版本不符也能建连，握手失败响应**没有** `X-QS-Server-Version`）；握手里的 `X-QS-Device-Id` 被**忽略**（身份只由 token 反查）；同一 `device_id` 只保留一条连接，**新连接踢旧连接**（只表现为连接被关闭，被踢方按退避重连）；吊销时**先广播 `device_revoked` 再关闭**该设备的连接。客户端本地合成一个服务端从不发送的事件 `auth_failed`（握手 401/403 时），随后清配对、停止重连、转轮询。

---

## 7. 图片与任务流水线

**图片（内容寻址）**：客户端截屏/选图 → 统一转 JPEG（长边 ≤ **1600**、质量 **80**）→ 逐页 `POST /api/v1/images`（`multipart`，字段名固定 `file`，**只处理第一个 part**）→ 拿到 `image_hash` → 建任务。服务端对**收到的字节**算 sha256，收到即原样落盘，**不嗅探、不转码、不算尺寸**（`mime` 恒 `image/jpeg`，`width`/`height` **恒 null**，只有既有行带过尺寸时才回填旧值）。

- **两道体积闸门**：`Content-Length` > 2162688 → 立刻 413（不读 body）；边收边计数，一超 **2097152** 立刻 413 且**停止读取**（分块传输没有 `Content-Length`，只有这道拦得住）。
- **顺序**：鉴权（未认证 401，**不消耗额度**）→ 限流（每分钟 30 次/设备）→ 体积/格式判定。因此被 400/413 拒绝的上传**同样消耗额度**。
- **提前返回前排空**：回 400/413/429 之前把「已经在路上」的请求体读掉，**最多等 300ms**、丢弃上限 = 上限 + 8 MiB。否则带未读数据的关闭会被重置，客户端拿到的是「网络错误」而不是真正的错误码。
- **下载**：`<hash>` 必须匹配 `^[0-9a-f]{64}$`（**小写**），形态不符一律 404 **且不接触任何文件**（hash 直接做文件名，这条是安全边界）；命中回原始字节。
- **本地缓存**：文本结果与元数据永久保留，剪枝只删本地文件并清空 `local_path`；客户端侧固定保留最近 **20** 张（队列引用的原图豁免且不占额度），补传一次最多 **5** 张、候选只看最近 **200** 张。

**任务（串行队列）**：`POST /api/v1/tasks` 的校验顺序**可依赖** —— JSON → `task_id` 与页序非空 → 页数 ≤ **6**（超 → `400 too_many_pages`，**早于**「页图是否已上传」判定）→ 每页 hash 已在图片表 → 合集存在 → 队列深度（`status = queued` 行数 ≥ **20** → `429 queue_full`，`retry_after_seconds` 固定 **10**）。

- **状态码恒 202**：新建、命中复用、重复 `task_id` 三者都是 202；真实状态只在 `status` 里（`queued` / `analyzing` / `done` / `failed` / `cancelled`）。`done` / `failed` / `cancelled` 是终态。
- **响应四件套** `status` / `session_id` / `question_count` / `cached`：新建任务 `question_count = 0`（会话行在建任务时就已创建）、`cached = false`；`status == "done"` 时 **`cached` 被强制为 `true`**；会话实体里的同名列是整数 `0/1`，而任务响应里是布尔 —— 解析端两种都要收。
- **执行**：并发恒 1，按 `created_at` 升序；执行前先查 AI 配置，缺 Key → 任务 `failed` + `error_code = ai_auth`，**不经过 `analyzing`**。失败码：`ai_timeout` / `ai_auth` / `ai_rate_limited` / `ai_bad_response` / `ai_quota_exceeded` / `no_question_found`（解析出 0 道题**算失败**）/ `internal`。
- **重试**：`POST /tasks/<id>/retry` **恒回 `{"status":"queued"}`**（固定字面量，不是真实状态）；只有 `failed` 的行会被改回 `queued` 并清空**任务行**的 `error_code`；会话行的错误字段保持旧值直到本次执行结束。
- **重新生成**：`POST /sessions/<id>/reanalyze` 按既有页序强制重跑（新建会话），`202` + `status` + **新的 `session_id`** + `task_id` + `question_count: null` + `cached: false`；会话没有页序行、或页图在图片表里没有行 → `409 invalid_request`（不再抛非协议 500）。
- **本机截屏不走任务队列**：主机自己按热键的识别没有客户端可查的 `task_id`，只能从 `/tasks/active` 或 WS 事件拿到结果；这条路径的 `task_id` 就等于 `session_id`。`cancelled` 只由这条路径的「取消」写入。
- **`/tasks/active` 是内存态**：只回放主机**最近一次广播出去**的状态，重启回到 `idle`、不补发历史；`done` 时直接带与 `task_result` **同构**的 `session`；还带 `collections`（主机当前未删合集列表，客户端**只增改不删**地镜像）与 `ops_lamport`（主机 op 日志最大 `lamport`，涨了才做一次**只拉不推**的拉取）。客户端前台每秒调一次。

---

## 8. 错误码与可重试性

错误体形状固定三键：`code` / `message` / `retry_after_seconds`（不需要时该键**仍存在**、值为 `null`）；**不使用** `Retry-After` 头；客户端按 `code` 分支，**不得**按 `message` 文案分支。唯一例外是重复配对的 **409：body 里没有 `code`**。

| code | HTTP | 含义 | 可重试 | `retry_after_seconds` |
|---|---|---|---|---|
| `unauthorized` | 401 | 没有 token / token 认不出（WS 握手同码） | 否（需重新配对） | null |
| `revoked` | 401 | token 命中但设备已吊销 | 否（需重新配对） | null |
| `invalid_code` | 401 | 配对码不匹配（**计入失败数**） | 核对后 | null |
| `code_expired` | 401 | 配对码已过期（**计入失败数**） | 主机刷新后 | **有：已过期秒数 clamp 0–60**（不是等待时间） |
| `version_mismatch` | 426 | `X-QS-Client-Version` 主版本与主机不同（含值不可解析） | 否（需升级） | null |
| `invalid_request` | 400 | body/字段/参数不合法（含 body 不是 JSON 对象、缺 `file`、空文件） | 否 | null |
| `too_many_pages` | 400 | 页数 > 6 | 否 | null |
| `not_found` | 404 | 图片 / 任务 / 会话 / 合集不存在（图片 hash 形态非法也是它） | 否 | null |
| `no_active_collection` | 409 | 任务没落到合集（主机未选，或指定的合集不存在） | 主机状态变了才行 | null |
| `payload_too_large` | 413 | 单文件 > 2097152 字节 | 否（缩小后重传） | null |
| `rate_limited` | 429 | 三种触发：配对锁定 / 配对频繁 / 上传频繁 | 是（等指示值） | **有**（三种口径不同，见下） |
| `queue_full` | 429 | 排队任务 ≥ 20（内置 Host） | 是 | **有：固定 10** |
| `internal` | 无 | 兜底码，**不由任何 HTTP 端点产生** | 视情况 | —— |

- 三条 `429` 的口径不同：配对**锁定** = 剩余锁定整秒；配对**滑窗** = 最早一次尝试离开窗口所需整秒；上传滑窗 = 最早一次上传离开窗口所需整秒（都向上取整）。
- **客户端本地合成、服务端从不发送**的取值：`timeout` / `network_error` / `cancelled`（HTTP 状态记 0），以及 WS 侧的本地事件 `auth_failed`。响应里缺 `code` 时按 `internal` 兜底。
- **两个码空间不可混用**：HTTP `code`（上表）与任务 `error_code`（`ai_*` / `no_question_found` / `internal` / `queue_give_up`）字段名与出现位置都不同。
- **自动重试现状**：HTTP 层不做自动重试。客户端唯一的自动补跑通路是离线队列（容量 20 条、单条最多 5 次尝试，超限成死信 `queue_give_up`）；「截屏」入口对**除 `revoked`、`no_active_collection` 外**的错误码也入队（**含 400/413/429**，补跑时会再次失败），「相册选图」入口只在传输层异常时入队。

---

## 9. 版本与兼容

- **两个独立概念**：`protocol_version`（线缆契约版本，现状整数 **1**，由 `/info`、配对响应、WS `hello` 三处同源自述）与**应用版本**（现状 `1.0.0`，出现在请求头 `X-QS-Client-Version`、响应头 `X-QS-Server-Version`、body 的 `app_version`）。
- **现状的 426 比的是应用版本的主版本**（版本串按 `.` 切分取第一段，**字符串相等**才放行）—— 与 `protocol_version` 无关；`01.0.0`、`v1.0.0` 会被判成不同主版本。**没有任何一端依据 `protocol_version` 分支**。
- **协商规则**：完全不带该头（或只有空白）→ **放行**；带了且主版本不等 → **426 `version_mismatch`**，双方版本**只出现在 `message` 文本里**（没有独立字段）；**值不可解析（如 `abc`）等于不一致 → 426**。
- **检查顺序**：**先鉴权、后版本**（既缺 token 又版本不符 → 401）；版本检查在**路由之前**（未知路径 + 版本不符 → 426 而不是 404）。
- **响应头**：除 `/ws` 外每个 `/api/*` 响应（**含错误响应**）都带 `X-QS-Server-Version`；`/ws` 完全绕过中间件，既不做版本判定也不带该头。
- **`capabilities`**：`/info` 里恒为 5 项常量 `analyze` / `sync` / `image_fetch` / `collections` / `multipage`，**顺序不是契约**，不随配置变化（未配 AI 时也照报全 5 项）。客户端**不按它降级**（实测没有任何分支读它），WS `hello` 也**不带**能力。真正的兼容兜底是：**加法字段 + 忽略未知字段/未知事件 + 主版本 426**。
- **已知的两个「比错对象」风险**：① 426 依据的是应用版本，把产品版本号当成线缆兼容判据；② `/ws` 不做版本判定 —— 主版本不匹配的客户端仍能建连并收到 `task_result` 的完整会话。

---

## 10. 已知缺陷与实现-文档分叉（**最重要的一节**，见下）

> 以下 16 条为逐条复核结果，标注「**真**」= 与实现一致、「**修正**」= 结论成立但需要限定条件或补充。每条给出可验证它的向量步骤或测试。

1. **`/info` 免鉴权** —— **真**（`auth.ndjson` 1、23 用 `auth:"none"` 打 `/info` 得 200；`errors.ndjson` 3、4 同）。分叉点：旧契约文本写「只有 `/pair` 免鉴权」，`spec/04` §1.1 已按实现修正，但旧文本仍在引用。
2. **`device_id` / `server_device_id` 是固定字面量**（`windows-local` / `android-local`）→ 同一时间只支持一台安卓 —— **真**（`auth.ndjson` 10–12 用同一个合成 `device_id` 重现「重复配对 → 旧 token 立刻失效」）。
3. **409 重复配对的响应体没有 `code`**，只有 `already_paired: true` + 新 token（旧 token 立即作废）—— **真**（`auth.ndjson` 10 断言 409 + `already_paired` + 捕获新 token；11 断言旧 token 失效；12 断言新 token 可用）。补注：向量只做了局部匹配，**「没有 `code`」这一点由实现钉住**，向量无法证明「不存在」。
4. **`code_expired` 的 `retry_after_seconds` 是「已过期秒数」**（clamp 0–60），不是「多久后可重试」—— **真**（`auth.ndjson` 7–8 走通该分支；数值语义由实现钉住，向量未断言具体值）。
5. **配对限流：5 次/分（成功也计数）、10 次失败锁 60 秒、计数与锁定落库跨重启、锁定检查先于码比对** —— **真，但需限定「内置 Host」**（`auth.ndjson` 13–19 钉住窗口与第 6 次 429，**未覆盖锁定分支与跨重启**；后两者由实现与单测钉住）。**修正**：独立 Host 的失败计数与锁定**只在内存**（无落库、启动不装载），重启即清零，与该条及 `spec/03` §8 的 MUST 冲突。
6. **`POST /tasks` 恒返 202；`cached` 在 `status == 'done'` 时被强制为 true；`question_count` 在新任务上是 0 而不是 null** —— **真，但需限定「内置 Host」**（`tasks.ndjson` 8 断言 202 + `question_count: 0` + `cached: false`；10、11 断言复用与重复提交仍是 202 且 `cached: true`）。**修正**：独立 Host 新建任务是 `question_count: null`，且没有队列——「已有识别在跑」直接 `429 queue_full`（`retry_after_seconds` 固定 5）。
7. **`GET /tasks/<id>` 没有 `question_count` 字段**（题目数在 `session` 里）；`error_message` 取自会话行 —— **真**（`tasks.ndjson` 9 断言任务视图含 `session.question_count` 与顶层 `error_code: null`）。补注：向量是局部匹配，「顶层没有该键」由实现钉住。
8. **`/sync/ops` 的归属校验** —— **真**（`sync.ndjson` 3 声称由主机产生的 op 被**静默跳过**：`applied: 0` / `rejected: 0`；4 冒充别的设备 → `rejected: 1`；14 同 `op_id` 重投 → `applied: 0`）。补注：判序是「先比主机 id、再比调用者」，所以冒充主机永远静默跳过（哪怕调用者是别人）；WS 的 `push_ops` 用同一规则但冒充他人是**静默跳过、不计 rejected**。
9. **WS 死分支清单** —— **真**（全部由实现与集成测试钉住，**没有任何向量覆盖 WS**）：服务端**从不**发送 `ops` 事件（客户端解析层保留了该分支，永不命中）；客户端的 `ack` / `push_ops` **生产路径不发**（op 走 HTTP；两个方法只被集成测试调用）；服务端**不校验 `pong`**、不因缺 `pong` 断开；客户端判死是「**>40s** 没收到服务端 `ping`」（30s 间隔 + 10s 容差，5s 检查一次）；`/ws` 绕过版本协商、握手响应**无** `X-QS-Server-Version`；握手头 `X-QS-Device-Id` 被忽略。补注：独立 Host 的 `ack` 是**空操作**（不写任何水位），而内置 Host 会写对端水位。
10. **版本协商比的是应用版本主版本；不带版本头放行；不可解析的值 → 426；先鉴权后版本；版本检查在路由之前** —— **真**（`auth.ndjson` 22、23；`errors.ndjson` 2、3、4 —— 其中 2 用 `1.9.9` 放行证明「只比主版本」，4 用 `abc` 得 426；未知路径 + 版本不符 → 426 由实现钉住）。
11. **上传细节**（预检阈值 = 上限 + 64 KiB；边收边计数；超限回 413 **不等整包收完**；回 400/413/429 前最多吞 300ms；限流 30 次/分/设备且体积被拒的也算；`<hash>` 白名单；`width`/`height` 从不计算恒 null；`mime` 恒 `image/jpeg`）—— **真**（`images.ndjson` 2 断言 `size`/`mime`/`existed:false`，9 断言空文件 400，10 用 2097153 字节断言 413「不等整包」，11–12 断言额度用满后第 31 次 429，6–8 断言 404 与目录穿越拦截；`upload_limits_test` 另钉住「内存有界、不等整包」）。补注：`429` 自身不计额度、未认证请求在限流之前就 401（`images.ndjson` 13、14）。
12. **`/sync/snapshot` 的 `images` 已剔除 `local_path`；`questions` 只带本页会话下的题目；分页只沿会话分**（`limit` 默认 200 / 上限 1000 / `offset`）—— **真**（`sync.ndjson` 32 断言 `images[0]` 无 `local_path`；28–30 断言 `limit=1` → `has_more: true` / `next_offset: 1`、末页 `false` / `null`、按 `created_at` 升序拼回；5、6 与 2 一起证明题目只随本页会话出现）。补注：独立 Host 的快照 `watermark` 恒 0、`session_images` 恒空。
13. **`/sync/snapshot`、墓碑 GC、op 折叠在生产代码里没有调用点** —— **修正**：准确说法是三层。(a) **客户端从不调用** `/sync/snapshot`（客户端没有对应方法），**服务端已实现该路由**，调用者只有一致性回放器与单测（`sync.ndjson` 2 起就靠它）。(b) **墓碑 GC 在两端应用里都没有调用点**，只被 `tombstone_gc_test` 覆盖。(c) **op 折叠同样只在单测里**（`sync_engine_test`），快照导入（bootstrap）也没有生产调用点。因此「新设备靠快照 bootstrap」目前是**未接线的能力**。
14. **两端 UI 都没有修改答案 / 改解析的入口**，手改答案/手改解析的仓库层入口与 `*_edited` 链路真实运行永不触发，但保护逻辑保留 —— **真**（这两个入口与整条保护链路只被 `user_edited_test`、`remote_op_test`、`collection_features_test` 与客户端轮询测试调用）。保护逻辑本身（op 未带标记则跳过该组字段 + 写本地待决项 `pending_overwrite:<题目 id>`）仍必须保留。
15. **两处用户拍板的不对称必须原样保留** —— **真**（合集删除方向：`collection_features_test`、`active_task_poll_test` 钉住「主机删除不跟随、镜像只增改不删」；同图缓存/去重只对单页生效：`tasks.ndjson` 11 钉住单页复用，多页「页序逐位相同才算命中」由 `multipage_workflow_test` 与 `server_loopback_test` 钉住，分析缓存「只对单图调用」由 `cache_quota_test` 钉住）。补注：「远端合集删除是否落地」在共享仓库层**默认是「落地」**，「不跟随删」是**客户端显式关掉**的结果，不是协议层强制 —— 重写时不要误以为服务端会阻止删除落地。
16. **两张 Host 实现的差异** —— **真**，且不止列出的那些。除「独立 Host 另有 `403 forbidden`（本机控制面两道门槛）、`501 unavailable`、`400 bad_request`；『原图不在电脑上』用 `409 invalid_request`；`/sync/ops` 拉取恒空页；`/tasks/active` 的 `ops_lamport` 恒 0；op 推送只按 `op_id` 记账、不做字段级 LWW 落地」之外，复核又确认了七处：① **配对失败计数与锁定不落库**（内置落库、独立只存内存）；② 新建任务 `question_count` **null vs 0**；③ `queue_full` 的语义与 `retry_after_seconds` **5 vs 10**（独立的「队列」其实只有串行的一条）；④ `reanalyze` 响应**没有 `task_id`**（内置有）；⑤ 合集恒为**一条**（列表、选中、任务的 `collection_id` 都只认那一条）；⑥ 快照 `watermark` 恒 0、`session_images` 恒空；⑦ `ack` 是**空操作**（不推进任何水位）。补注：「内置 Host 现已统一为 409 invalid_request」是对的，且已被 `sync.ndjson` 31 钉住 —— 但 `spec/09` §7.1 仍写内置是 `400 invalid_request`，**该行已过期**。

**复核过程中另外发现的缺陷（不在上表 16 条内）**：

- **N1** `spec/09` §7.1 过期：内置 Host 的「原图不在电脑上」已是 `409 invalid_request`（与独立 Host 统一，`sync.ndjson` 31 钉住），规范仍写 `400`。
- **N2** `spec/04` §1.3.9 过期：`reanalyze` 的响应**已经**带 `task_id`（`tasks.ndjson` 16 用正则断言它是 UUID 形态），规范说「没有 `task_id`」并把它列进 v2 目标。
- **N3** 覆盖表过期：`spec/04` §3 把 `reanalyze` 列为「完全没有向量」，实际 `tasks.ndjson` 16–18 与 `sync.ndjson` 31 已覆盖；`conformance/README` 与 `spec/05` §6.1 的步数仍是旧值（`tasks` 15 → **18**、`sync` 30 → **32**），五组向量实际共 **101 步**。
- **N4** 独立 Host 的配对锁定不落库 → 两套 Host 的安全强度不对等，且违反 `spec/03` §8 的 MUST（「落库、跨重启不清零」）。
- **N5** 客户端收到 `device_revoked` 时**连接层**比对了 `device_id`、**消息落地层不比对** → 别人的设备被吊销也会清掉本机配对（`live_updates_test` 里恰好是用自己的 id 测的，掩盖了这条）。
- **N6** 独立 Host 的 `/tasks/active` 的 `collections` 恒为一条 → 客户端「只增改不删」的合集镜像在它上面退化成「只有一条」，与内置 Host 的语义不等价（不破坏数据，但两端观感不同）。
- **N7** 次要：`spec/04` §3 称 `images.ndjson` 4–5「两步的 `do` 逐字相同，疑似向量笔误」——实际第 4 步是下载、第 5 步是上传，描述不成立（真正逐字相同的上传是 2、3、5 步）。

---

## 11. 重写时的迁移注意（哪些行为必须逐字保留、哪些是历史包袱可以丢）

**必须逐字保留（有向量或客户端依赖钉住）**：

1. 路由与形状：17 条路径、`/api/v1/` 前缀、静态路径优先、成功响应体的键名（`image_hash` vs 实体的 `hash` 这对分叉也要一起认）。
2. 错误体三键恒在（`retry_after_seconds` 不许省）、`code` 与状态码成对、**409 重复配对不是错误体**、未知路由的 404 **不带** `code`。
3. 配对全流程的判定顺序与口径：码 TTL 5 分钟「严格大于」、成功后码不失效、锁定先于码比对、限流成功也计数、锁定与计数**跨重启**、`code_expired` 的 `retry_after_seconds = 已过期秒数`（这是 Bug 兼容点，别「修好」它，除非同时改客户端）。
4. `POST /tasks` 恒 202 + 四件套语义（`done` 时 `cached` 强制 true）、`retry` 恒回 `queued`、`reanalyze` 的 `question_count: null` / `cached: false`。
5. 上传的两道体积闸门与「不等整包」、限流先于体积、被拒也计数、提前返回前排空 300ms、`<hash>` 白名单、`mime` 恒 `image/jpeg`。
6. 同步内核：op 八字段、`lamport` 自增与 observe、逐字段 LWW 的 `(lamport, device_id)` 且**相等判负**、字段时钟形态与基准回落、`image`/`device` 不做字段级 LWW、本地专属列不进 op、墓碑与「删除输则复活」的收敛、三种水位分离、拉取游标排他且空页不归零。
7. 推送与轮询的分工：WS 只报发生、HTTP 报状态；断线期间的推送**不补发**；建连只补发**进行中**的本机任务。
8. 两处不对称（合集删除方向、同图缓存只对单页）与「本机截屏不走任务队列、`task_id = session_id`」。

**历史包袱，可以（也应该）在重写时丢掉或收紧**：

1. 免鉴权的 `/info`（信息自述无需 token 可以保留，但别把「免鉴权路径靠字符串比较硬编码」带过去）。
2. 固定字面量 `device_id` → 改成每安装一次的 UUID；同时把「同一时间只支持一台安卓」的界面说明改掉。
3. 令牌无有效期、吊销可被重新配对自助解除、任何已配对设备都能吊销任何设备（没有角色与归属校验）→ 引入角色与到期时间。
4. `width` / `height` 恒 null、`mime` 不嗅探、`local_path` 出现在实现里（同步载荷已剔除，但列本身在）。
5. 死分支：服务端 → 客户端的 `ops` 事件、客户端的 `ack` / `push_ops` 发送路径 —— 要么接上要么删掉，**不要**原样搬成一个「看起来支持、实际不发」的实现。
6. `internal` 作为两个码空间共用的兜底；`version_mismatch` 比应用版本主版本而不是协议版本。
7. 「截屏入口对除 `revoked` / `no_active_collection` 外的错误码也入离线队列」的重试口径（4xx 不该入队）。
8. 客户端消息落地层不比对 `device_revoked.device_id`；服务端不校验 `pong`。
9. 两套 Host 的行为分叉本身（`question_count`、`queue_full` 数值、`reanalyze` 的 `task_id`、快照 `watermark`、`ack` 语义）—— 重写目标应是**一套线协议一份语义**，分叉只允许存在于「本机控制面」。

---

## 12. 验证手段索引（哪条结论由哪个向量文件/步骤或哪个测试覆盖）

| 结论 | 证据 |
|---|---|
| `/info` 免鉴权 + 能力自述 + `protocol_version` 自述 | `auth.ndjson` 1、23；`errors.ndjson` 3、4 |
| 缺 token / 假 token / 空 token / 无 `Bearer` 前缀 → 401 `unauthorized` | `auth.ndjson` 2、3；`errors.ndjson` 5、6；`images.ndjson` 13、14 |
| 配对成功签发 64 位 hex；重复配对 409 + `already_paired` + 新 token + 旧 token 立刻失效 | `auth.ndjson` 5、10、11、12 |
| 配对码错误 / 过期 / 刷新 | `auth.ndjson` 4、7、8、9 |
| 配对窗口 5 次/分、第 6 次 429 | `auth.ndjson` 13–19 |
| 吊销设备 → 200；被吊销 token → 401 `revoked` | `auth.ndjson` 20、21 |
| 版本协商：主版本不同 426、同主版本放行、不带该头放行、值不可解析也 426 | `auth.ndjson` 22、23；`errors.ndjson` 2、3、4 |
| 未知路由的 404 不是协议错误体；令牌摘要绝不出现在响应里 | `errors.ndjson` 13、12 |
| 上传成功体 / 内容寻址去重 / 下载字节与原图一致 | `images.ndjson` 2、3、4、5 |
| 图片 404 的三种来源（不存在 / 形态不符 / 目录穿越） | `images.ndjson` 6、7、8 |
| 空文件 400、超限 413（边收边判、不等整包） | `images.ndjson` 9、10；`upload_limits_test` |
| 上传限流 30 次/分（被拒也计数）、第 31 次 429 | `images.ndjson` 11、12 |
| 任务校验顺序（缺 `task_id` / 超 6 页 / 页图未上传 / 合集不存在） | `tasks.ndjson` 3、4、5、7 |
| 创建恒 202 + `question_count: 0` + `cached: false`；任务视图无顶层题目数 | `tasks.ndjson` 8、9 |
| `task_id` 幂等 + 同图同页序复用（`cached: true`，状态码仍 202） | `tasks.ndjson` 10、11 |
| 不存在任务 / 重试 404；队列深度 20 → 429 `queue_full` | `tasks.ndjson` 12、13、14、15 |
| `reanalyze`：202 + 新 `task_id` / `session_id` / `question_count: null` / `cached: false`；不存在会话 404；无页序 409 | `tasks.ndjson` 16、17、18；`sync.ndjson` 31 |
| 空库快照形状 + `watermark` | `sync.ndjson` 2 |
| op 归属校验（冒充主机静默跳过、冒名他人 rejected） | `sync.ndjson` 3、4 |
| 逐字段 LWW（高 lamport 覆盖、低 lamport 判负、相等判负、同 lamport 按 `device_id` 决胜、字段时钟与基准回落） | `sync.ndjson` 7–13；`lww_test`、`lamport_test` |
| `op_id` 幂等（`applied: 0`） | `sync.ndjson` 14、15 |
| 墓碑（软删除后不出现在快照） | `sync.ndjson` 16、17 |
| 拉取游标（`from_device` + `since_lamport`、空页 `next_cursor: null`、缺参 400） | `sync.ndjson` 18、19、20 |
| Lamport 自增与 observe（幂等判重先于 observe） | `lamport_test`、`sync_ops_test` |
| 墓碑软删除与「删除输则复活」的收敛 | `tombstone_test`、`delete_edit_convergence_test` |
| 同图去重/缓存只对单页生效（多页要求页序逐位相同） | `tasks.ndjson` 11；`multipage_workflow_test`、`server_loopback_test` |
| 快照分页（`limit`/`offset`/`has_more`/`next_offset`、按 `created_at` 升序） | `sync.ndjson` 28、29、30 |
| 快照 `images` 剔除主机本地路径 | `sync.ndjson` 32 |
| WS 事件、心跳、重连、踢旧、死分支 | **无向量**（`websocket.ndjson` 未建、回放器的 `ws` 操作未实现）→ 只靠 `push_ops_ownership_test`、`live_updates_test`、`poll_task_test` 等集成/单测 |
| 折叠与快照导入、墓碑 GC 的两条件 | `sync_engine_test`、`tombstone_gc_test`（均无生产调用点） |
| 手改保护与待决项 | `user_edited_test`、`remote_op_test` |
| 合集删除方向不对称 + 镜像只增改不删 | `collection_features_test`、`active_task_poll_test` |
| 分析缓存只对单图生效（键 = hash + prompt 版本 + 模型，30 天）与每日额度 | `cache_quota_test` |
| 本机截屏不走任务队列、`task_id = session_id` | `local_session_test` |
| 上传后把本地路径写回图片行（否则主机永远没有缩略图） | `image_upload_path_test` |
| 离线队列容量 20 / 单条最多 5 次尝试 / 死信 | `offline_queue_test` |
| `/tasks/active` 的 `ops_lamport` 只拉不推、`done` 带 `session` | `active_task_poll_test`、`poll_task_test` |
| 端到端的配对 → 上传 → 建任务 → 结果 → 同步 op 生成 | `loopback_test` |
| 快照分页与硬化（大库、越界 `limit`/`offset`） | `server_hardening_test` |
| 独立 Host 的同一批语义（含它的分叉） | `server_loopback_test` |
| 本地库结构迁移（新增列/表的历史兼容） | `migration_test` |
| 全部 5 组向量（101 步）在参考实现上 100% 通过 | `vector_replay_test`（向量目录由环境变量指定，目录不存在 = 失败） |
