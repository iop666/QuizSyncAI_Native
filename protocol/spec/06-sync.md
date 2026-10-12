# 06 同步

> 状态：v1 现状（**按实现反推**）+ v2 目标。与旧契约文本冲突时，以实现为准。
> 强制级别：**MUST** / **SHOULD** / **MAY**。
> 约定：正文只出现协议词汇与本地库的物理命名（`sync_ops`、`field_clocks_json`、`deleted_at` 等），字段级清单与状态码见 `04-http-api.md`，本文只写同步语义。

## 1. op 的结构与身份

- **MUST** 同步的唯一载体是操作日志（表 `sync_ops`）。线上载荷是 op 数组：`POST /api/v1/sync/ops` 的 `{"ops":[…]}`，元素固定 8 个字段：`op_id` / `device_id` / `lamport` / `entity` / `entity_id` / `op_type` / `fields_json` / `created_at`。
- **MUST** `op_id` 为 UUIDv4（01 域），是**唯一幂等键**：同一 `op_id` 再次到达按「已存在」忽略——不重放、不改值、不推进本地时钟（向量 14、15）。
- **MUST** `device_id` 是**产生**该 op 的设备，不是转发者。从对端拉来并转存的 op 保留原 `device_id`；推送只取 `device_id = 本机` 的行（第 6 节）。
- **MUST** `entity` 取值固定七种：`session` / `question` / `image` / `device` / `collection` / `session_image` / `snapshot`；认不出的取值按 `snapshot` 兜底。`entity_id` 形态随实体：会话 / 题目 / 合集 / 页图片 / 设备是 UUIDv4，`image` 是 `image_hash`，`snapshot` 是固定串 `"global"`（01 域）。
- **MUST** `op_type` 只有 `upsert` / `delete`，认不出按 `upsert` 兜底（01 域）。
- **MUST** `fields_json` **只含本次真正改动过的字段**（键与实体列名同形）：新建行给该实体的整行字段，改一行只给差异字段；差异为空则**不产生 op**。
  - 现状例外一：用户手改会强制携带 `answer_edited` / `analysis_edited`（即使值没变），它是「这是用户手改」的信号（第 4 节）。
  - 现状例外二：`delete` op 固定带 `deleted_at`；收到不带 `deleted_at` 的 `delete` op 时，用该 op 的 `created_at` 当删除时间戳。新建行的整行 op 会带 `deleted_at: null`，按「无墓碑」处理。
- **MUST** 每次本机写入恰好产生 1 条 op，且业务行与 op 在**同一事务**内落库（不许崩在中途只落一半）。
- **MUST NOT** 把 op 当全量快照用：接收端只按 `fields_json` 出现的键做逐字段合并，缺的键一律不动。
- **SHOULD** 客户端重试推送时保持 `op_id` 不变（01 域）。

## 2. Lamport 时钟

- **MUST** 每个节点维护一个本地整数 `lamport`（表 `sync_ops` 按 `(device_id, lamport)` 建索引）。排序与胜负一律看它，**MUST NOT** 用墙钟（`created_at` 等 `*_at`）做因果判定（01 域）。
- **MUST** 本机产生 op 时取 `lamport = 本地计数 + 1`（计数随之 +1）；一次本地写入只 +1，一条 op 一个值。
- **MUST** 收到对端 op 时先 observe，再落地：`本地计数 = max(本地计数, 收到的 lamport) + 1`（**无条件再 +1**，即使收到的值更小）。
- **MUST** 幂等判重**先于** observe：重复 `op_id` 直接返回，不推进本地时钟。
- **SHOULD** 启动时用 `sync_ops` 里已见的**最大 `lamport`**（跨设备）恢复计数，避免重启后从头开始。
- **v1 现状** 同一来源设备的 `lamport` 严格递增且唯一（本地写入自增，observe 只增不减），因此「同一 `from_device` 的 lamport 单调且可作游标」可直接依赖（第 6 节）。

## 3. 逐字段 LWW

- **MUST** 比较键是 **(lamport, device_id)** 二元组：先比 `lamport`，相等再比 `device_id` 的字典序，越大越新。
- **MUST** **相等即判负**：`(op.lamport, op.device_id) <= (字段时钟.l, 字段时钟.d)` 时跳过该字段、不覆盖（向量 9、10 是低 lamport 判负；向量 12、13 是同 lamport 按 `device_id` 决胜）。
- **MUST** 参与 LWW 的行维护 `field_clocks_json`，形态 `{"<列名>": {"l": <lamport>, "d": "<device_id>"}}`，**只记录被显式写过的字段**；字段被接受时写入 `{l: op.lamport, d: op.device_id}`。
- **MUST** 字段没有单独时钟时，以该行的 `lamport` / `updated_by` 作为比较基准（使「整行写入」与「单字段写入」可正确比先后）。
- **MUST** 行级 `lamport` / `updated_by` = 该行**所有字段时钟里的最大值**（用于排序与墓碑比较）。
- **MUST** 只有 `session` / `question` / `collection` / `session_image` 四种实体有 `lamport` 与字段时钟列。`image` / `device` **不做**字段级 LWW：按主键合并、后到写入。
- **MUST（本地专属列）** 下列列**永不进 op**，远端 op 里带上也要丢弃：图片的本地文件路径 `local_path`、设备的令牌哈希 `token_hash`。设备行的 `revoked_at` 一旦非空，**MUST NOT** 被不含该键的远端 op 清掉。
- **v1 现状** `settings` / `peer_state` / `tasks` / `ai_usage` 四张表**完全不同步**（没有对应 `entity` 取值）；`sessions.task_id` 会随 op 走，但任务表不跟着走，对端拿到的 `task_id` 在本地可能没有对应任务行。
- **v1 现状** `session_image` 有一处特殊落地：非删除 op 先按 `entity_id` 找行，找不到再按「同一 `session_id` + 同一 `ordinal`」合并到本机既有行（保留本机行 id）；**删除 op 必须按自己的 `entity_id` 落地**（否则会误删本机合法的页）。读取时同一 `ordinal` 只认最早建的那行，避免两端各建一行导致页数翻倍。

## 4. 用户手改优先

- **MUST** 手改答案置 `answer_edited = 1`、手改解析置 `analysis_edited = 1`，并让 op **显式携带**该标记。两组字段互相独立：答案组是 `choice_json` + `answer_text`，解析组是 `analysis`。
- **MUST** 收到 `question` 的 upsert 时按 op 是否携带标记区分来源：
  - op **未**携带 `answer_edited` 而本行 `answer_edited = 1` → 答案组字段**跳过**（保护），其余字段照常按 LWW 合并；`analysis_edited` 与 `analysis` 同理。
  - op **携带**了标记（= 对端的用户手改）→ 按正常字段 LWW 走。**MUST NOT** 一律保护，否则两端各留一份编辑、永久分歧。
- **MUST** 被跳过的字段记成一次待决项（本地 `settings` 的 `pending_overwrite:<question_id>`，值含 `op_id` / `lamport` / `device_id` / 被保护的字段值），**不同步**；用户确认覆盖后才用暂存值落库，且只清**本次覆盖到的**标记。
- **v1 现状（事实）** 两端 UI **都没有**改答案、改解析的入口，也没有「AI 已给出新结果，是否覆盖？」的入口；上面这条链路只有仓库层 API 与单测覆盖，真实运行中**永远不会**产生 `*_edited = 1`。

## 5. 墓碑与回收

- **MUST** 删除是软删除：写 `deleted_at`（毫秒）并产生一条 `op_type = delete` 的 op；一切读取（快照、列表、检索）都带 `deleted_at IS NULL`（向量 17）。
- **MUST** 删除幂等：已删除的行再次删除**不产生**第二条 delete op（否则墓碑时间被不断推后，永远过不了保留期）。
- **MUST** 墓碑与「迟到的高 lamport 修改」必须收敛，不得一端删一端在：
  - 行已有墓碑、来的 upsert **不含** `deleted_at`、且这次写入比墓碑新 → 墓碑作废（把 `deleted_at` 清成空并记字段时钟），删除方跟着复活；
  - 反之删除更「新」时，收到删除的一端照旧落墓碑。
- **MUST** 物理回收（GC）**同时**满足两个条件：`deleted_at` 距今超过 **30 天**，且该实体的**全部** op 的最大 `lamport` ≤ 「所有对端 `acked_lamport` 的最小值」。
- **v1 现状** 回收动作是直接删行（题目的全文索引由触发器同步清理），返回清理行数；**只有** `sessions` / `questions` 会被物理回收，`collections` / `session_images` 的墓碑永不清理。
- **v1 现状** `peer_state` **一行都没有**时，第二个条件被整体跳过（只看 30 天）。而 `acked_lamport` 只能由 WS 的 `ack`（或 bootstrap 导入快照时直接设定，见第 6 节）推进：`POST /api/v1/sync/ops` 的响应只有 `applied` / `rejected`，**不含**水位线（与旧契约文本不同，见第 10 节）。
- **v1 现状** GC 与折叠在两端应用里都**没有生产调用点**，仅由单测覆盖——`data-model.md` 2.6 / 2.7 描述的是目标，不是运行事实。

## 6. 水位与增量拉取

- **MUST** 每个对端一条 `peer_state`：`sent_lamport`（我推给它的最大 lamport）、`acked_lamport`（它确认收到我的最大 lamport）、`last_sync_at`。
- **MUST** 推送只取**本机产生**的 op：`device_id = 本机 AND lamport > sent_lamport`，按 `lamport` 升序、每批最多 **500** 条；推送成功后把 `sent_lamport` 推进到本批最后一条的 `lamport`。**MUST NOT** 把从对端拉来转存的 op 再推回去（否则水位被外来 lamport 顶高、每次同步都在重传历史）。
- **MUST** 拉取游标是**每对端一份的本地协调状态**（存在 `settings` 的 `pull_cursor:<device_id>`），与 `acked_lamport` **不是**同一个东西：混用会让「从未推送出去的本地 op」被当成已确认而折叠删除（第 6、5 节）。
- **MUST** 增量拉取用 `GET /api/v1/sync/ops?from_device=<来源设备>&since_lamport=<已见最大>`：`from_device` 是**来源设备**维度（拉主机自己的改动就填主机的 `device_id`，见 `/api/v1/info`），游标**排他**、只按 `lamport` 升序；续页用响应里的 `next_cursor`（也接受 `cursor` 参数覆盖 `since_lamport`，见 `04-http-api.md` 1.3.13），直到 `has_more = false`；缺 `from_device` → `400 invalid_request`（向量 18–20）。
- **MUST** 游标在 op **落地之后**才推进到「已应用 op 的最大 `lamport`」。空页时 `next_cursor` 为 `null`，此时 **MUST NOT** 把游标重置为 `0`（那会从头重拉）；向量 19 把游标推进到 20 后再拉，`ops` 为空、`next_cursor` 为 `null`。
- **MUST** 断线重连的顺序是**先拉后推**，避免缺口。
- **MUST** 容忍「空页 + `has_more: false` + `next_cursor: null`」的稳态：不产生任何 op 的那套主机实现就是这样应答的（两套主机实现见 `09-errors.md` 7.1）。
- **SHOULD** 只在**有改动信号**时做一次**只拉不推**的拉取：主机在 `GET /api/v1/tasks/active` 上报 `ops_lamport`（= 主机 `sync_ops` 的最大 `lamport`，空表 `0`），客户端发现它比上次大才拉一次 `GET /sync/ops`（`from_device` = 主机的 `device_id`），首次只建立基线。它与 `collections` 是两条独立通道：`collections` 是「想要的结果」，`ops_lamport` 是「有改动」的信号（`04-http-api.md` 1.4.1）。
- **MUST** 新设备 bootstrap 用 `GET /api/v1/sync/snapshot`：导入后把该对端的 `sent_lamport` / `acked_lamport` 都设为快照 `watermark`，之后只走增量；**MUST NOT** 靠重放全部历史 op 来 bootstrap。
- **MUST** 快照分页：`limit` 缺省 **200**、clamp 到 **1–1000**；`offset` 缺省 **0**、clamp 到 0–2^30。分页对象是**未删除的会话**（按 `created_at ASC, session_id ASC`），`questions` / `session_images` 只含本页会话；`watermark` = 主机 `MAX(lamport)`；`has_more` 为真时给 `next_offset`，为假时为 `null`。客户端 **MUST** 用 `next_offset` 续拉，**MUST NOT** 自行 `offset += limit`（向量 2、28–30）。
- **v1 现状** `from_device` 不限定主机：任何已配对设备都能按任意 `device_id` 拉取（无隔离，见 `04-http-api.md` 1.1）；而客户端只拉它配对的那台主机的 `device_id`。

## 7. 集合语义的两处「不对称」（用户拍板，原样保留）

**① 合集删除方向不对称**

- **MUST** 客户端侧：收到远端 `collection` 的删除**不写** `deleted_at`（只记账：op 入库、时钟 observe、拉取游标照常推进），本地合集保留——主机删合集，手机不跟着删。
- **MUST** 客户端主动删合集照常软删除并产生 delete op，正常同步给主机，主机侧按正常删除落地。
- **MUST** 合集镜像（把主机上报的活跃合集列表写进本地库，见 `04-http-api.md` 1.4.1）：**只增改、不删**。主机列表里没有的合集本地原样保留；本机已删的合集（本地墓碑）**绝不复活**；镜像**不产生 op**、不改本地行的 `lamport` / `field_clocks_json`；名字与 `updated_at` 都没变时一行不写。
- 向量缺口：`sync.ndjson` 未覆盖删除方向的任一侧（第 9 节）。

**② 同图缓存与去重只对单页生效**

- **MUST** 任务结果复用（`POST /api/v1/tasks` 的去重）：候选是**最近 500 条**会话里 `image_hash` 相同且 `status = done` 的行；本次页序必须与候选会话的页序**长度相同且逐位相同**才算命中，命中即登记任务并回 `done` / `cached: true`，不调用分析。多页**MUST** 页序完全一致；单页（长度 1）退化为「同 `image_hash` 即复用」。
- **MUST** 分析缓存只对**单图**调用生效：键是 `image_hash` + `prompt_version` + `model`，命中且距今 < **30 天**、且该会话有题目时回放（不占额度）；多页**直接走真实调用**，**MUST NOT** 拿「首页相同」当命中。
- **v1 现状** 没有 `session_images` 行的旧会话，页序退回 `sessions.image_hash`（视作单页）。

## 8. op 归属校验

- **MUST** `POST /api/v1/sync/ops` 逐条校验、逐条计数：
  - 非对象元素 → **静默跳过**（不计入任何计数）；
  - `device_id` = **主机自己的** `device_id` → **静默跳过**（不计入 `applied`，也不计入 `rejected`；向量 3）；
  - `device_id` ≠ **调用者认证身份** → 计入 `rejected`、不落库（向量 4）；
  - `op_id` 已存在 → 按幂等忽略，**不计入** `applied`（向量 14）；
  - 通过以上检查才落 `sync_ops` 并做字段级 LWW；`applied` 只统计**新入库**的 op 数。
- **MUST** `applied` 的含义是「op 新入库」，**不是**「字段真的被改写」：被 LWW 判负的 op 照旧计入 `applied`（向量 9 推低 lamport 的 op → `applied: 1`，向量 10 的值并未改变）。
- **MUST** WS 的 `push_ops` 用同一套归属规则（冒充主机、冒充别的设备都丢弃），并回一条 `ack`（`watermark_lamport` = 本次接受 op 的最大 `lamport`）。只有这条 `ack` 能推进主机的 `acked_lamport`（第 5 节）。
- **MUST NOT** 把本节当成完整的数据隔离：v1 没有按设备隔离数据，任何已配对设备都能读全库；同步侧的归属校验只有本节这几条（`04-http-api.md` 1.1）。

## 9. 一致性向量覆盖

| 向量步（`sync.ndjson`） | 覆盖的规则 |
|---|---|
| 1、11 | 配对换 token（第 8 节的前置；见 `03-auth-pairing.md`） |
| 2 | 空库快照形状、`has_more: false`、`next_offset: null`、`watermark` 为整数（第 6 节） |
| 3 | 归属校验：冒充主机的 op **静默跳过**（第 8 节） |
| 4 | 归属校验：`device_id` ≠ 调用者 → `rejected`（第 8 节） |
| 5 | 本机 op 推送的 `applied` 计数；快照 `questions` 只带本页会话的题目（第 1、6 节） |
| 6、8、10、13、15 | 快照只出活行、字段值经 LWW 落地（第 3、5 节） |
| 7 | 高 `lamport` 覆盖同字段（第 3 节） |
| 9 | 低 `lamport` 的 op 仍入库（`applied: 1`），字段判负（第 3、8 节） |
| 12 | 同 `lamport` 按 `device_id` 字典序决胜（第 3 节） |
| 14 | `op_id` 幂等：重复投递 `applied: 0`（第 1、8 节） |
| 16 | 墓碑：高 `lamport` 的 `delete` op（第 5 节） |
| 17 | 墓碑后不出现在快照（第 5 节） |
| 18 | `from_device` + `since_lamport` 拉取、`next_cursor` 为整数（第 6 节） |
| 19 | 游标推进后空页：`ops: []`、`next_cursor: null`（第 6 节） |
| 20 | 缺 `from_device` → `400 invalid_request`（第 6 节） |
| 21–27 | 图片与任务的准备步骤（`07-images.md` / `08-tasks.md`） |
| 28–30 | 快照分页 `limit` / `offset` / `has_more` / `next_offset`、按 `created_at` 升序（第 6 节） |

**`sync.ndjson` 未覆盖（v1 缺口）**：`field_clocks_json` 的形态与基准回落、墓碑作废（复活）规则、GC 的两个条件与不可回收的表、折叠（阈值 5000）、`/tasks/active` 的 `ops_lamport` 与只拉不推、合集删除方向与镜像（第 7 节）、WS `push_ops` / `ack`、`cursor` 参数与 500 条页大小、`snapshot` op 到达后的语义。

## 10. 文档-实现分叉

| 现有文档怎么写 | 实现是什么 | 处置 |
|---|---|---|
| 折叠触发条件：单个 `device_id` 的行数 > 5000，**且**该设备所有对端 `acked_lamport` 越过最早一批 | 触发看 `sync_ops` 的**全表**行数 > 5000（阈值可配、默认 5000）；要求 `sent` 与 `acked` **双双**越过最旧 op，水位取两者较小值；删除条件是 `lamport <= 水位`，**不按 `device_id` 过滤**；并写入一条 `entity = snapshot`、`entity_id = "global"`、`fields_json = {"watermark": <水位>}` 的 op | 规范按实现写；阈值口径与「跨设备删 op」列入 v2 复核 |
| 拉取游标写在 `peer_state`（与 `acked_lamport` 混用） | 拉取游标存在 `settings` 的 `pull_cursor:<device_id>`；`acked_lamport` 只由真实 `ack` 推进 | 规范区分两者（第 6 节） |
| 「确认：对端在 WS 上发 `ack` **或在 HTTP 推送响应里回水位线**」 | `POST /sync/ops` 的响应只有 `applied` / `rejected`，**没有**水位线字段 | 规范写明只有 WS `ack` 能推进 `acked_lamport` |
| GC 的两个条件必须同时满足 | `peer_state` 无行时跳过确认条件；且只对 `sessions` / `questions` 物理删行 | 规范写明现状（第 5 节） |
| `user_edited` 保护只按「`answer_edited == 1` 就不覆盖」 | 还要看 op **是否携带** `*_edited` 标记（用户手改才覆盖）；被保护的字段转成 `pending_overwrite:<question_id>` 待决项 | 规范写明来源判据与待决项（第 4 节） |
| 文档说用户「在 UI 上修改答案或解析」，并给出「是否覆盖」入口 | 两端 UI 都没有改答案 / 改解析 / 覆盖确认入口，只有仓库层 API 与单测调用 | 规范把「无 UI 入口」写成事实；v2 需要决策是补 UI 还是废弃该机制 |
| 冲突表：「删除胜出（墓碑**时间戳**更大）或修改胜出，取决于时间戳」；两端最终一致 | 判定键是 `(lamport, device_id)` 而非时间戳；且补了「修改赢时墓碑作废」的另一半（单看 LWW 会一端删一端在） | 规范按 `(lamport, device_id)` 写，并写明墓碑作废规则 |
| 快照「返回全量实体（分页）」 | 只有会话分页（`limit` 默认 200 / 上限 1000 + `offset`），`questions` / `session_images` 只含本页会话；`images` / `devices` / `collections` 每次全量 | 规范写明分页口径（第 6 节）；字段细节见 `04-http-api.md` 1.4.4 |
| 文档未提 | 现状客户端只调 `GET /sync/ops` 增量通道；`GET /sync/snapshot`、GC、折叠在两端都**没有生产调用点**（仅单测覆盖） | 规范把 bootstrap / GC / 折叠标为「服务端侧已实现、客户端未接入」 |
