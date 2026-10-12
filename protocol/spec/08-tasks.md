# 08 任务

> 状态：v1 现状（**按实现反推**）+ v2 目标（标注清楚哪句是现状、哪句是 v2 变更）。
> 强制级别：**MUST** / **SHOULD** / **MAY**。
> 「三端」= 主机（内嵌服务端的一端）、独立部署的服务端、客户端。未标「v2」的条目 = v1 现状且继续有效。
> 端点字段表见 `04-http-api.md` §1.3.5–1.3.8 与 §1.4.1–1.4.2；错误码语义见 `09-errors.md`；`cached` 的两种形态（整数 `0/1` 与布尔）见 `01-conventions.md` §2；图片必须先上传见 `07-images.md`。

## 1. 规则

### 1.1 任务与状态机
- **MUST** 任务的 `status` 取值固定五个：`queued` | `analyzing` | `done` | `failed` | `cancelled`；`done` / `failed` / `cancelled` 是终态。
- **MUST** 成功的状态路径只有 `queued → analyzing → done`（进入 `analyzing` 之后才调用 AI），**MUST NOT** 跳过 `analyzing`；唯一例外是 §1.4 的复用，它**登记**一行直接为 `done` 的任务，不走这条迁移。
- **MUST** 队列**串行**执行（并发恒 1，按 `created_at` 升序取一条 `queued`），且已登记的 `queued` 任务**最终一定会被执行**（现状靠「队列退出前再确认一次」关闭竞态）——客户端 **MUST NOT** 把长期停在 `queued` 当成正常状态。
- **MUST** 会话状态跟随任务（`analyzing` / `done` / `failed` / `cancelled` 写回会话行）；**结果（题目、`question_count`、`error_message`）挂在会话上**，任务行只有 `status` / `session_id` / `error_code`。
- **MUST** `cancelled` 现状只由**本机识别**路径的「取消」写入（见 §1.10）：服务端执行器**从不**产生它，也没有取消端点。

### 1.2 创建端点
- **MUST** 创建 = `POST /api/v1/tasks`，需鉴权。请求字段：`task_id`、`image_hash`、`image_hashes`、`source_device`、`collection_id`、`force_reanalyze`（缺省与语义见 `04-http-api.md` §1.3.5）；**`created_at` 被完全忽略**。
- **MUST** 响应状态码**恒 `202`**：新建、命中会话复用、重复 `task_id` 三者都是 `202`，真实状态只在 body 的 `status` 里（向量 8、10、11）。
- **MUST** 响应体四件套 `status` / `session_id` / `question_count` / `cached`；现状 `202` 路径上 `session_id` **恒非空**，新建时 `question_count` 为 `0`（向量 8），会话行不存在时为 `null`。
- **MUST** 校验顺序（可依赖）：JSON 合法 → `task_id` 与页序非空 → 页数 ≤ 6 → 每页 hash 已上传 → 合集存在 → 队列深度；四种拒绝分别见 §1.5–§1.7。
- **MUST** 客户端把 `202` + `status` 当成「已受理」，**MUST NOT** 假定响应时的状态是终态：要么轮询 `GET /api/v1/tasks/<taskId>`，要么等 WS 事件（现状客户端轮询间隔 2 秒、单次等待上限 90 秒）。
- **MUST** `force_reanalyze` 只在**严格等于** `true` 时为真；为真则跳过 §1.4 的复用（「重新生成」靠它）。

### 1.3 `task_id` 幂等
- **MUST** `task_id` 由**发起端**生成，唯一作用是幂等键；服务端**不校验**它的形态（不要求 UUIDv4）。
- **MUST** 同一个 `task_id` 再提交一次：回 `202` + **既有状态**，**MUST NOT** 重复识别、**MUST NOT** 新建会话（向量 10：同 `task_id` 第二次得到 `status: done` + 同一个 `session_id`）。
- **MUST** 幂等只看 `task_id`、**不看内容**：同 `task_id` 换图片 hash / 换页序 / 换合集，一律直接返回既有行（请求里其它字段全被忽略）。
- **MUST** 若既有任务仍是 `queued` / `analyzing`，重复提交会**顺带再踢一次**队列（关闭「入队后没人执行」的竞态）。
- **SHOULD** 客户端重试**必须复用**同一 `task_id`（`02-transport.md` §1.7），换新 `task_id` 等于承认是另一次识别。
- **v1 现状** 主机自己做本机识别时，广播里的 `task_id` **等于** `session_id`；v2 起两者独立生成（见 §1.12）。

### 1.4 同图同页序复用（`cached`）
- **MUST** 复用需**同时**满足：`force_reanalyze != true`；存在 `status = done` 的会话且其首页 `image_hash` 等于本次首页；该会话的页序与本次**长度相同且逐位相同**；该会话在**最近 500 条**会话之内（按 `created_at` 倒序取候选，且**不含已删除**的会话）。
- **MUST** 命中即：为**本次**的 `task_id` 登记一行 `status = done` 的任务、`session_id` 指向**既有会话**，直接回 `202`，**不调用 AI**（向量 11：换 `task_id`、同一张图 → 直接拿到既有结果）。
- **MUST** 多页必须**页序逐位一致**：首页相同、后续页不同 → 复用**不成立**（否则会把另一组题目的结果当成这次的答案）。页序来自会话已登记的页序，不是请求数组的拷贝。
- **MUST** `cached` 是布尔：`status == "done"` 时响应里**恒为 `true`**，新建且仍 `queued` 时为 `false`（向量 10、11 对 `true`，向量 8 对 `false`）；会话实体里的同名列是整数 `0/1`，解析端**两种都要收**。
- **MUST** 复用是**服务端**行为（客户端也可在本机先做同图同页序判定并直接跳既有结果，结论一致）；客户端 **SHOULD** 把它理解成「同一次识别」——展示既有会话即可，**MUST NOT** 期待新题目或新的 `question_count`。

### 1.5 页数上限与「已上传」校验
- **MUST** 单任务页数硬上限 **6**；7 页 → `400 too_many_pages`（向量 4）。
- **MUST** 页数判定排在「每页 hash 是否已上传」**之前**：向量 4 用 7 个**未上传**的 hash 拿到的是 `too_many_pages`，不是 `invalid_request`。
- **MUST** 每一页的 hash 必须已在图片表里有行（即已经 `POST /api/v1/images` 上传过）→ 否则 `400 invalid_request`（向量 5；向量 6 上传后，向量 8 才创建成功）。
- **MUST** 页序解析规则：`image_hashes` 是数组时优先；否则退回单页 `image_hash`；**空串元素在被剔除之后**才判空与计数。
- **MUST** 客户端**先逐页上传、再创建任务**（顺序不可交换）；上传过的 hash 可以跨任务复用，不必重传。

### 1.6 必须落在合集里（`409`）
- **MUST** 任务必须落在某个合集：请求的 `collection_id` 为空串/缺失时取主机**当前选中的合集**；两者都为空 → `409 no_active_collection`（message：请在电脑端先新建或选择一个任务合集）。
- **MUST** 显式给的 `collection_id` 在主机上不存在（或已删）→ **同样** `409 no_active_collection`（message：所选合集不存在，请重新选择）。**MUST NOT** 用 message 分支，两者 `code` 与状态码完全相同（向量 7）。
- **MUST NOT** 静默落进「未分类」（那会让历史记录分组错乱）：客户端 **MUST** 明确提示用户先去主机选合集，且 **SHOULD NOT** 把这种**整体性**阻塞计入离线队列的失败次数（否则用户还没选合集，队列里的任务就被烧成死信）。

### 1.7 队列深度上限（`429`）
- **MUST** 主机 `status = queued` 的任务行数 ≥ **20** 时，创建请求回 `429 queue_full` + `retry_after_seconds: 10`（向量 14 造 20 条排队 → 15 被拒）。
- **MUST** 只统计 `queued`：`analyzing` / `done` / `failed` / `cancelled` **不占**深度额度。
- **MUST** 判定在建行之前：被拒的请求**不产生**任务行、会话行或页序副作用。
- **MUST** `retry_after_seconds` 现状是**固定 10 秒**（不是按排队进度算的），客户端 **MUST** 至少等这么久再试；**MUST NOT** 把服务端这个「队列深度 20」与客户端「离线队列容量 20 条」（§1.12）混为一谈，两者独立、互不感知。

### 1.8 执行与失败码
- **MUST** 执行前检查 AI 配置：无 Key 或无模型 → 任务 `failed` + `error_code = "ai_auth"`、message「主机尚未配置 AI」。客户端 **SHOULD** 特判该码为「主机尚未配置 AI，请在电脑上填写 API Key」，而不是通用失败文案。
- **MUST** 失败码取值与触发（现状）：
  - `ai_auth`：主机未配置 AI（每次执行都必然失败，重试无效，除非先配置）；
  - `ai_timeout` / `ai_rate_limited` / `ai_bad_response` / `ai_quota_exceeded`：AI 层超时或连接失败 / 被限流 / 响应不是合法 JSON 或缺字段 / 当日额度用尽；`no_question_found`：AI 调用成功但**解析出 0 道题** —— 现状「空结果」算失败，**不是**成功；
  - `internal`：图片文件缺失（首页或第 N 页）、执行期间抛出未预期异常等兜底。
- **MUST** 失败同时写回会话行（`error_code` + `error_message`）；`GET /tasks/<id>` 的 `error_code` 取自**任务行**、`error_message` 取自**会话行**（会话行不存在时恒 `null`）。
- **MUST NOT** 把任务错误码当 HTTP `code` 用（两个取值空间，见 `09-errors.md` §1.4）。
- 现状：执行阶段**不做**第二次缓存回放（复用只在 §1.4 判定），因此「复用命中」不会在执行时被二次改写。

### 1.9 `retry`
- **MUST** 重试 = `POST /api/v1/tasks/<taskId>/retry`，需鉴权、无请求体（带了也忽略）。
- **MUST** 响应**恒** `200 {"status":"queued"}` —— 它是**固定字面量**，不是任务当前状态；客户端 **MUST NOT** 拿它当状态来源，重试后仍要轮询或等 WS。
- **MUST** 只有 `status = failed` 的行会被改回 `queued` 并**清空任务行的 `error_code`**；其它状态返回 `200` 但什么都不改；任务不存在 → `404 not_found`（向量 13）。
- **MUST NOT** 用重试创建新任务或新会话：`session_id` 与页序保持不变。现状（易踩）：会话行的 `error_code` / `error_message` **不会**被清空，直到本次执行结束才写回 → 重试后短时间内可能出现「`error_code: null` + `error_message` 仍是旧值」的组合。

### 1.10 本机截屏不经任务队列（现状特例）
- **v1 现状** 主机自己按热键/悬浮球触发的识别**不经任务队列**：在主机进程内直接分析并写会话，任务行从 `analyzing` 开始（**从不**经过 `queued`），服务端执行器因此永远取不到它；对外广播的 `task_id` = `session_id`，而本机任务行的 id 是**另一个**独立生成的值 → 客户端用广播里的 id 去 `GET /tasks/<id>` 会得到 `404`。
- **MUST** 客户端对本机识别的读取通道只有两条：WS 的 `task_update` / `task_result` / `task_failed`，以及 `GET /api/v1/tasks/active`（`done` 时直接带 `session`）。**MUST NOT** 依赖 `GET /tasks/<id>`。
- **MUST** 本机识别的状态变化**只广播**，**MUST NOT** 触发「把主机窗口带到前台」一类客户端面钩子（现状：该钩子只对手机提交的任务触发）——否则用户每按一次热键，主机界面就会自己跳出来。
- **MUST** 手机断线重连时**只补发进行中**的本机任务；已完成的不补，否则每次重连都会跳到上一次的结果页。
- **MUST** 主机未配置 AI 时，本机识别**不建会话、不入队**，只提示用户去配置（与 §1.8 的 `ai_auth` 形成对比：那条路径是「已建任务、异步失败」）。
- **MUST** 本机识别的「取消」把会话（以及那条本机任务行）写成 `cancelled`，并**丢弃本次结果**（不回写成 `done` / `failed`）。

### 1.11 `GET /api/v1/tasks/active`（只读探测）
- **MUST** 它是**只读探测**：不改变主机状态、不建任务、不消耗任何额度；供客户端在 WS 不可靠时**轮询兜底**（现状安卓端 1 秒一次）。
- **MUST** 状态是**内存态**：主机重启后回到 `idle`，**不补发**历史结果。
- **MUST** 它对本机识别是**结果来源**（§1.10），对 `POST /tasks` 创建的任务**只是探测**（真状态在 `GET /tasks/<id>` 或 WS 事件里）。
- **MUST** 客户端把**第一次**探测就看到的 `done` / `failed` 只当基线、不自动进结果页（否则每次打开 App 都会跳进上一轮结果）；`updated_at` 用于区分「同一个会话又出了新结果」；**MUST NOT** 把任务 id 取成 `active`（静态路径优先于 `<taskId>`，这样的任务永远查不到）。
- 其余字段（`image_count` / `message` / `active_collection_id` / `collections` / `ops_lamport` / `session`）见 `04-http-api.md` §1.4.1；`collections` 与 `ops_lamport` 是纯加法字段，老客户端忽略。

### 1.12 离线语义与 v2 目标
- **v1 现状（客户端侧离线）** 主机不可达时，客户端**本地**建 `queued` 任务进离线队列：容量 **20 条**，按入队顺序补跑，单条最多尝试 **5** 次，超限改判 `failed` + 任务错误码 `queue_give_up`（死信）并跳过；补跑时每一页**重新上传**，`task_id` 与 `collection_id` 原样复原。主机侧的队列**没有**优先级、也**没有**取消端点。
- **v2 变更（目标）**：
  1. **所有识别统一成一条任务**：本机截屏也入队（因此有真实 `task_id`、可 `GET /tasks/<id>`、可 `retry`），`/tasks/active` 退化为纯探测；未配置 AI 时 `POST /tasks` **前置**拒绝（不再「先 `202`、再异步必失败」，与 `04-http-api.md` §1.4.5 第 1 条一致）；
  2. `retry` 返回任务**真实状态**（对非 `failed` 的任务回 `409 invalid_state`），并增加取消端点，让 `cancelled` 成为三端都能产生的状态；
  3. **Provider 派发**：把「谁执行分析」从主机解耦成 Provider 角色。
     > ⚠️ **本句的后半句（「Provider 不在线时任务停在 `queued` 并退避重试」）已被实现取代** ——
     > v2 主机实际是**立刻**收口成 `failed` + 任务错误码 `provider_offline`（验收上限 2 秒，
     > 实测百毫秒量级），任务行与会话行都保留、可 `retry`。本域（Provider 派发、在线状态、
     > 错误码与收口时机）**以 `12-provider.md` 为准**；本行按仓规「v1 冻结文本不改」保留原文，
     > 只加这条指向。`versions/compat-matrix.md` §4 末行同步改过。

## 2. 数值表

| 项 | 值 | 依据 |
|---|---|---|
| 状态取值 | `queued` / `analyzing` / `done` / `failed` / `cancelled`（后三者为终态） | 状态机常量 |
| 执行并发 | 1（串行，按 `created_at` 升序） | 队列执行循环 |
| 创建响应状态码 | 恒 `202`（新建 / 复用 / 重复 `task_id` 一致） | 创建处理器 |
| 页数硬上限 | 6（超出 `400 too_many_pages`） | 契约常量 |
| 复用候选窗口 | 最近 500 条会话 | 复用判定 |
| 排队深度上限 | 20（只算 `status = queued`） | 主机选项默认值 |
| `queue_full` 等待 | 固定 10 秒 | 硬编码值 |
| `retry` 响应 | 恒 `200 {"status":"queued"}` | 重试处理器 |
| 客户端轮询节奏 | 任务状态：每 2 秒一次、单次上限 90 秒；主机状态：每 1 秒一次 | 客户端轮询与探测默认值 |
| 客户端离线队列 | 容量 20 条 / 单条最多 5 次尝试（超限 `queue_give_up`） | 客户端队列默认值 |
| 本机识别任务行起始状态 | `analyzing`（**从不** `queued`） | 本机识别流程 |

## 3. 一致性向量覆盖

| 规则 | 覆盖它的向量（`tasks.ndjson`） |
|---|---|
| 前置状态：主机已建并选中合集 | 2（seed：合集 + `active_collection`） |
| 缺 `task_id` → `400 invalid_request` | 3 |
| 页数硬上限 6 **早于**「已上传」判定 | 4（7 个未上传的 hash → `too_many_pages`） |
| 页图未上传 → `400 invalid_request` | 5（配合 6：先上传才成功） |
| 任务必须落在合集里（显式不存在的合集 → `409`） | 7 |
| 创建 → **`202`** + `status: queued` + `question_count: 0` + `cached: false` + `session_id` 形态 | 8 |
| 任务视图：状态 + `session`（题目数在会话里）+ `error_code: null` | 9 |
| `task_id` 幂等（同 id 再提交 → 既有状态、同 `session_id`、`cached: true`） | 10 |
| 同图复用（换 `task_id` → 不重复调用 AI，`cached: true`） | 11 |
| 不存在任务 / 给不存在的任务重试 → `404` | 12、13 |
| 队列深度 20 → `429 queue_full` + `retry_after_seconds` | 14、15 |

**未被向量覆盖（v1 缺口）**：`force_reanalyze` 跳过复用、多页**页序不一致**时不得复用、`retry` 对非 `failed` 任务「返回 200 但什么都不改」、失败码全集（`ai_auth` / `ai_timeout` / `no_question_found` / `internal` 等）、`cancelled`、`/tasks/active` 的 `idle` 与 `done` 两态、本机识别不经队列这条特例（需要 WS 或客户端参与，属 `websocket.ndjson` 的范围）。

## 4. 文档-实现分叉

| 现有契约怎么写 | 实现是什么 | 处置 |
|---|---|---|
| 创建请求字段表列了 `created_at`；响应示例只有 `status` / `session_id` / `question_count` | 服务端**完全不读** `created_at`；响应还有 `cached`，且三者状态码恒 `202` | §1.2、§1.4 按现状写明 |
| 未写队列深度上限 | `status = queued` ≥ 20 → `429 queue_full`，`retry_after_seconds` 固定 10 | §1.7 |
| 「已有 `done` 会话、页序完全一致」一句 | 还受**最近 500 条**候选窗口限制，且命中复用表现为「新 `task_id` 指向旧会话」 | §1.4 |
| `GET /tasks/<id>` 的 `error_message` 未说来源 | 取自**会话行**；会话行不存在时恒 `null` | §1.8 |
| `retry` 未写响应与生效范围 | 恒 `200 {"status":"queued"}`，只对 `failed` 生效，只清任务行的 `error_code` | §1.9 |
| 「本机截屏任务**不经任务队列**，数据库里没有对应的 `tasks` 行」 | 本机识别**确实会在本机任务表里建行**（从 `analyzing` 开始），只是 id 与广播出去的 `task_id`（= `session_id`）不同、客户端拿不到 → 对外效果等价于「没有可查的任务」 | §1.10 按「客户端不可查」写，并把「库里到底有没有行」列为待确认 |
| 未提「取消」的适用范围，也未提 `/tasks/active` 的 `done` 会把结果直接挂上 | `cancelled` 只由本机识别的「取消」写入；`status = done` 时 `/tasks/active` 带 `session`（与 WS `task_result` 同构），因为本机识别没有可查的任务行 | §1.1、§1.10、§1.11 |

## 5. 待确认

1. **本机识别是否真的「库里没有 `tasks` 行」**：按实现，本机流程会插入一行从 `analyzing` 开始的任务，而广播用的 `task_id` 是 `session_id` —— 两者是否**必然**不同，取决于本机流程有没有把 `task_id` 传进去（现状没传）。没有向量覆盖，这一条目前只有代码依据。
2. **被取消的会话遇上已排队的任务**：执行器不检查会话是否已被写成 `cancelled`，执行结束会把会话状态覆盖回 `done` / `failed`。这条路径能否在真实操作中走到，未见测试覆盖。
3. **重试后的瞬时读数**：`retry` 清任务行的 `error_code` 但不清会话行的 `error_message`，客户端在这段窗口内展示会看到「无错误码 + 有错误信息」。是否规范成「重试后两个字段一起清」，留给 v2 决定。
