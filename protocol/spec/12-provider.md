# 12 Provider 派发

> 状态：**v2 新增域（按实现反推）**。v1 快照里没有本域的任何一条 —— v1 没有「谁执行分析」这个概念，分析恒由主机自己执行（见 `versions/v1-snapshot.md`）。
> 记号：「**实现现状**」= 两台主机实现（内嵌的、独立部署的）与 Provider 一侧已经真实存在、可观测的行为；「**v2 目标**」= 已定方向但**实现里还没有**的句子，逐句标注。未标注的条目 = 实现现状且必须照做。
> 强制级别：**MUST** / **SHOULD** / **MAY**。
> 相关域：WS 信封、连接生命周期、单设备踢旧见 `05-websocket.md`；任务状态机、失败码落库见 `08-tasks.md`；错误码两个空间与全表见 `09-errors.md`；版本与 `/api/v2/*` 见 `10-versioning.md`；能力声明见 `11-capabilities.md`。
> JSON 对象的**键序不进契约**（`01-conventions.md` §1.5）；本文件的「顺序」一律指**同一连接上帧的发送顺序**。

## 1. Provider 角色

### 1.1 角色从哪来（实现现状）

- **MUST** Provider **不是第二种身份**：它就是一个**普通的已配对设备** —— 同一套配对码、同一套令牌、同一套 `device_id` 规则（`03-auth-pairing.md`），没有特权通道、没有单独的令牌类型、也没有「只有主机本机进程才能当 Provider」的校验。任何已配对设备都能自述成 Provider。
- **MUST** 「是不是 Provider」**只在 `/ws` 的 `hello` 帧里自述**：`role` 字段存在且**严格等于**字符串 `"provider"` → 这条连接登记为 Provider；`role` 缺失、为其它取值、或不是字符串 → 按普通客户端处理，行为与 v1 **一字不变**。
- **MUST** 角色是**连接级**的：不写库、不随设备持久化。同一条连接上先发一条不带 `role` 的 `hello`、再发一条带 `role` 的 `hello`，第二次即登记；连接断开即失去角色。
- **MUST** 自述帧复用 v1 的 `hello` 形状（`05-websocket.md` §2.2），**只做加法**：新增 `role` 与三个 AI 自述字段（见 1.2）。老客户端不认识这些键，按 `01-conventions.md` §1.4 忽略即可。
- **MUST** 主机**不持有 AI 凭据**：凭据属于 Provider 一侧。主机的职责是派发作业、接回结果、把结果落库；**MUST NOT** 在派发失败时回退成任何「本地假识别」（那会让用户拿到看似成功、实为编造的结果）。
- **MUST** Provider 连上后仍走 v1 的心跳与重连规则：收到 `ping` 要回 `pong`；握手 401 是**终态鉴权失败**，**MUST NOT** 无限重连（`05-websocket.md` §3.1 / §3.2）。
- **MUST** 一台主机上的 Provider 与「主机本机的界面客户端」**必须用不同的 `device_id`**：`05-websocket.md` §4 按 `device_id` 踢旧，同名会让两条连接互相踢、表现为随机断线重连。这条是实现里踩过并写进决策记录的硬约束。

### 1.2 `hello{role:"provider"}` 的加法字段

| 字段 | 类型 | 可空 | 取值域 | 语义 |
|---|---|---|---|---|
| `role` | str | 否 | 恒 `"provider"` | 登记开关；只有这一个取值会被识别 |
| `ai_configured` | bool | 否 | `true` / `false` | Provider **自己那台机器**上「AI 凭据是否可用」；判定口径见 `11-capabilities.md` §1.3（非空即可用，**不**试调、**不**校验有效性） |
| `ai_provider` | str | **是** | 供应商标识串（自由文本，现状形如 `openai-compatible`） | 诊断用；主机只原样透出，不据此分支 |
| `ai_model` | str | **是** | 模型名（自由文本，现状形如 `deepseek-chat`） | 作为 `/api/v2/info` 的 `provider_model` 原样透出 |

- **MUST** `ai_configured` 缺失、非布尔或为 `false` 时登记仍然成功，只是自述值按 `false` 记（**MUST NOT** 因为缺字段而拒绝登记）。
- **MUST** `ai_provider` / `ai_model` 缺失或非字符串时按「无自述」记（`null`）。
- **MUST** `hello` 里的 `device_id` / `platform` 对 Provider 同样**被忽略**：身份一律由 token 反查（`05-websocket.md` §1.2）。
- **SHOULD** Provider 每次连上（含每次重连）都重发自述帧，与普通客户端一致。

### 1.3 单 Provider 与顶替（实现现状）

- **MUST** 同一时刻只登记**一个** Provider；第二个连接自述 `role: "provider"` 时**顶替**前一个。
- **MUST** 顶替是**静默**的：被顶掉的那条连接**不断开**、也收不到任何「你已被顶替」的帧；它只是**不再收到** `recognition_job`。
- **MUST** 顶替后仍广播一次 `provider_online`，内容是**新** Provider 的自述值（`online: true`）。
- **MUST** 注销只在「当前登记的就是这条连接」时才生效：同一 `device_id` 重连时，**旧连接迟到的关闭不得把新 Provider 判成掉线**（与 `05-websocket.md` §4 的踢旧是同一条纪律）。
- **MUST** 顶替与重连的窗口期**允许**出现一条 `online: false`（旧连接被踢到新连接登记之间确实没有 Provider 在线）：客户端 **MUST NOT** 把中间态当终态，**SHOULD** 以**最后一条** `provider_online` 为准。
- **MUST** 登记不走 HTTP：没有任何端点能查询或修改「谁是 Provider」，唯一入口是自述帧。

### 1.4 在线状态如何被消费（实现现状）

- **MUST** 在线状态通过两处暴露，且**都只出现在 v2 面**：`provider_online` 事件（§4）与 `/api/v2/info` 的 `provider_online` / `provider_model` 两个加法字段（§6.3）。**v1 的 `/info` 一个字段都不加** —— 它是冻结契约，老客户端照着解析。
- **MUST** `ai_configured` 在 v2 的口径是「**有 Provider 在线时以它的自述为准，没有 Provider 时退回主机自己的启动配置**」：凭据在 Provider 那台机器上，主机自己分不清「在线且说没配凭据」与「根本不在线」。这是与 v1 的一处**语义差异**，`/api/v2/info` 与 v1 的 `/info` **同源取值**。
- **MUST** 客户端**MUST NOT** 只依赖 `ai_configured` 判断「现在能不能识别」：它是拉取型的、可能过期；权威结论是提交任务后落库的 `error_code`（§5，与 `11-capabilities.md` §1.3 对 `ai_configured` / `ai_auth` 的双投影要求同一条道理）。
- **MUST** 客户端在 `provider_online = false` 时 **SHOULD** 直接提示「电脑端没有在线的识别服务」，而不是等任务失败。

### 1.5 v2 目标（**未实现**）

1. **v2 目标** 角色白名单：配对期或握手期限制哪些设备能自述为 Provider（实现现状是任意已配对设备都能）。
2. **v2 目标** Provider 能力自述与 `11-capabilities.md` §1.7 的能力变更事件合流：能力集合变化时推 `capabilities_changed`，而不是只推在线状态。
3. **v2 目标** 多 Provider / 按能力路由：现状是**单 Provider、登记即顶替**，没有负载分担、没有按模型选择。
4. **v2 目标** 主机自述「Provider 会话已建立多久」一类可观测字段（现状只能靠事件与 `provider_online` 的布尔值）。

## 2. `recognition_job`（主机 → Provider，定向单播）

### 2.1 触发与顺序（实现现状）

- **MUST** 触发条件：任务出队、进入 `analyzing` 之前，主机把本次识别派发给**当前登记的那一个** Provider。
- **MUST** **只有当前登记的 Provider 那条连接**能收到本帧：它是**定向单播**，不参与广播面（`05-websocket.md` §2.1 的广播清单里没有它）。普通客户端连接**不得**收到（实现里有负向用例钉住）。
- **MUST** 一个任务**恰好派发一帧**：`job_id` 每次派发**新生成**（UUIDv4），不是 `task_id` 的别名。
- **MUST** 派发处于任务状态机的 `queued → analyzing` 之间：帧发出后任务进入 `analyzing` 并广播对应的 `task_update`。客户端看到 `analyzing` 就说明「已经有人在做」；`provider_online = false` 时**不会**出现 `analyzing`（§5）。
- **MUST** 同一时刻主机只执行一个任务（`08-tasks.md` §1.1 的串行队列），因此**同一个 Provider 连接上同一时刻至多只有一个未收口的 `job_id`**；Provider **SHOULD** 自行串行化处理（实现里用一个信号量收着，免得某次任务卡住时后续结果乱序回传）。
- **MUST** 主机 **MUST NOT** 因为「Provider 在线」就假定派发一定成功：帧**真的写出去没有**决定这一单是立刻收口还是继续等（§5.1）。

### 2.2 字段表

| 字段 | 类型 | 可空 | 取值域 | 语义 | 依据 |
|---|---|---|---|---|---|
| `type` | str | 否 | 恒 `"recognition_job"` | 判别键 | 派发构造 |
| `job_id` | str | 否 | UUIDv4（小写十六进制带连字符） | **关联键**：Provider 回帧必须原样带回；`recognition_result` 按它认领 | 派发构造 |
| `task_id` | str | 否 | 任务 id（发起端给的幂等键） | 让 Provider 的日志/文案能对上主机侧的任务 | 派发构造 |
| `session_id` | str | 否 | 会话 id | Provider 结果要关联回的那条会话 | 派发构造 |
| `collection_id` | str | 见注 | 合集 id | 本次识别所属合集；Provider **MUST NOT** 据此改写结果（只用于自述与排障） | 派发构造 |
| `image_hashes` | str[] | 否 | 长度 1–6，元素为 64 位小写十六进制 sha256 | **页序权威**：按下标顺序就是页顺序；Provider 按它逐页取图 | 派发构造 |
| `deadline_ms` | int | 否 | 正整数毫秒；协议冻结的默认值 **90000** | 主机等回包的**上限**；到点未回 → `ai_timeout`（§5.2） | 派发构造 |
| `created_at` | int | 否 | UTC 毫秒 epoch | 派发时刻；只用于展示与排障，**MUST NOT** 作为因果判据（`01-conventions.md` §1.2） | 派发构造 |

- **MUST** 上表 8 个键在**每一帧**里都出现（没有「键可省略」的项）。
- **注（`collection_id` 的可空性，待实现确认）**：任务**必须**落在某个合集里（未选/不存在 → `409 no_active_collection`，`08-tasks.md` §1.6），因此**派发路径上它必然非空**；实现里该字段按可空读取，**未观察到空值样本**，空值时 Provider 应如何表现**未规定**。
- **MUST** 页数上限 **6** 与 `image_hashes` 的元素个数同源（`08-tasks.md` §1.5）：主机在创建任务时已经挡住 7 页以上，Provider **MUST NOT** 再放宽。
- **MAY** Provider 额外接受单页旧形态 `image_hash`（实现里为了兼容早期派发形态保留了这条回退：`image_hashes` 为空时才看它）。**MUST NOT** 依赖它：新派发一律给数组。
- **MUST** Provider 取图只能走内容寻址的图片下载路径（`07-images.md`）：主机**只给 hash，不给字节**（`image_fetch` 能力的现状语义）。

### 2.3 幂等与超时（实现现状）

- **MUST** 派发**不做重试**：没有「同一 `job_id` 再发一次」的通路。超时即收口为失败（§5.2），要重做只能由用户/客户端重新提交任务或走 `retry`（`08-tasks.md` §1.9）。
- **MUST** 幂等在**任务**这一层，不在帧这一层：同一个 `task_id` 重复提交由 `08-tasks.md` §1.3 的幂等规则拦掉，因此**不会**因为重复提交而产生第二个 `job_id`。
- **MUST** Provider 对**同一个 `job_id`** 重复回帧**毫无意义**：第一次认领之后这一单就结束了，后续同 `job_id` 的回帧一律被丢弃（§3.5）。
- **MUST** 超时判定用**主机自己的时钟**，不用 Provider 的时间戳；Provider **MUST NOT** 假定 `deadline_ms` 是「剩余时间」——它是**总预算**，从帧构造那一刻算起（含网络与排队）。
- **SHOULD** Provider 在自认可能超过 `deadline_ms` 时**尽早**回一条失败帧（真实错误码），而不是等到主机超时：两者的用户可见差别是「原因明确」与「只说超时」。

## 3. `recognition_result`（Provider → 主机，定向回报）

### 3.1 触发与顺序（实现现状）

- **MUST** 触发：Provider 完成（或放弃）一次 `recognition_job` 后，用**同一 `job_id`** 回一条帧；**成功与失败都回**，失败不得回一个空题目数组（那会被记成「没识别到题」，把真因盖掉）。
- **MUST** 主机侧的处理**只有一件事**：按 `job_id` 认领并唤醒等待中的那一单；`type` 为 `recognition_result` 但 `job_id` 缺失/为空 → **什么都不做**。
- **MUST** 该帧**不产生任何应答**：主机收到后**不得**回任何帧（没有 `ack`、没有错误体），也**不得**因为畸形帧断开连接（`01-conventions.md` §1.4：未知/坏帧静默丢弃）。
- **MUST** **无法认领**的回帧（`job_id` 不存在、已超时、已因掉线判过失败）**一律丢弃**，不得把结果塞回已经结束的任务（迟到不算数）。
- **MUST** 回帧是**普通客户端帧**：一样受「客户端帧大小上限 1 MiB」约束（几十道题的量级远小于它）。

### 3.2 成功帧字段表（`ok: true`）

| 字段 | 类型 | 可空 | 取值域 | 语义 | 依据 |
|---|---|---|---|---|---|
| `type` | str | 否 | 恒 `"recognition_result"` | 判别键 | 回帧构造 |
| `job_id` | str | 否 | 必须等于收到的 `recognition_job.job_id` | 认领键 | 回帧构造 |
| `ok` | bool | 否 | `true` | **必须显式为 `true`**：缺失、非布尔或 `false` 都按失败帧走（§3.3） | 回帧解析 |
| `questions` | obj[] | 否 | 长度 ≥ 1（成功帧）；元素见 §3.4 | 识别出的题目，顺序即 `ordinal` 的权威顺序 | 回帧解析 |
| `ai_provider` | str | 是 | 供应商标识串 | 落库为会话的 AI 自述；缺失 → `null` | 回帧解析 |
| `ai_model` | str | 是 | 模型名 | 同上；缺失 → `null` | 回帧解析 |
| `latency_ms` | int | 是 | ≥ 0，毫秒 | **Provider 自己那一次 AI 调用的耗时**（不是主机等回包的时间）；缺失 → `null` | 回帧解析 |
| `from_cache` | bool | 是 | `true` / `false` | 本次是否命中 Provider 侧的结果缓存；缺失/非布尔 → `false` | 回帧解析 |

- **MUST** `questions` **空数组 = 失败**：主机把它收口成 `no_question_found`（§5.2），**不是**「识别成功但没题」。Provider 想表达「没识别到题」也应回 `ok: true` + 空数组（与真失败区分开）。
- **MUST** 单页才允许命中 Provider 侧缓存（多页组合无法用首页 hash 区分后续页不同的两次识别）；`from_cache` 只是自述，主机**MUST NOT** 据此跳过落库。
- **MUST** `ai_provider` / `ai_model` / `latency_ms` 是**会话级**元信息：主机把它们写在会话行上（客户端历史里的「模型 / 耗时」就是这三列）。

### 3.3 失败帧字段表（`ok: false`）

| 字段 | 类型 | 可空 | 取值域 | 语义 |
|---|---|---|---|---|
| `type` | str | 否 | 恒 `"recognition_result"` | 判别键 |
| `job_id` | str | 否 | 同 §3.2 | 认领键 |
| `ok` | bool | 否 | `false`（**或整个键缺失**，等价于失败） | 失败开关 |
| `error_code` | str | 否 | 见 §5.2 的码表 | Provider 自报的原因；**不在码表里的一律落 `internal`** |
| `error_message` | str | 是 | 自由文本 | 展示用；缺失或全空白时主机给一句兜底文案 |

- **MUST** 失败帧**必须带真实错误码**：`error_code` 落在协议码表内就原样采用，落在表外（Provider 自己发明的码）一律收口成 `internal` —— 否则客户端拿到一个不认识的码，按码分支的文案全部落空（`09-errors.md` §1.2 允许客户端容忍陌生码，但**不允许主机**把它当正常路径传下去）。
- **MUST** `error_message` **不是契约**：主机只把它当展示文本，客户端 **MUST NOT** 按文本分支（`09-errors.md` §1.2）。

### 3.4 `questions[]` 元素字段表

题目的键名**逐个等于**数据模型里题目表的列名（去掉由持有方分配的 id、会话外键、时间戳与同步时钟列）。列级定义见 `schema/schema-v1.json`；**MUST NOT** 引入第二套字段名。

| 字段 | 类型 | 可空 | 取值域 | 缺项/坏值时的行为（实现现状） |
|---|---|---|---|---|
| `ordinal` | int | 是 | 0 起连续 | 缺项 → 用**数组下标**（数组本身就是顺序） |
| `question_no` | str | 是 | 自由文本（原卷题号） | 缺项/非字符串 → 无 |
| `stem` | str | 否 | 题干 | 缺项/非字符串 → 空串 |
| `material` | str | 否 | 阅读材料 | 缺项/非字符串 → 空串 |
| `type` | str | 否 | `single` / `multi` / `judge` / `blank` / `subjective` | 缺项/空串 → 列默认值 `unknown`；**MUST NOT** 做取值映射或白名单（原样落库） |
| `options_json` | str | 是 | **JSON 文本**，形状 `[{"label":"A","text":"…"}]` | 原样透传，服务端**不解析再序列化** |
| `choice_json` | str | 是 | **JSON 文本**，形状 `["A","C"]` | 同上 |
| `answer_text` | str | 是 | 答案文本 | 缺项/非字符串 → 无 |
| `analysis` | str | 否 | 解析 | 缺项/非字符串 → 空串 |
| `confidence` | num | 否 | 0–1 | 缺项 → `0.5` |
| `need_review` | int | 否 | `0` / `1` | 缺项 → `0` |
| `answer_in_image` | int | 否 | `0` / `1` | 缺项 → `0` |
| `incomplete` | int | 否 | `0` / `1` | 缺项 → `0` |
| `answer_guessed` | int | 否 | `0` / `1` | 缺项 → `0` |
| `warnings_json` | str | 是 | **JSON 文本**，字符串数组 | 原样透传 |

- **MUST** 布尔语义的列在线缆上用整数 `0` / `1`（表里是 `INTEGER` + `CHECK (col IN (0,1))`），**MUST NOT** 混用布尔。
- **MUST** `options_json` / `choice_json` / `warnings_json` 是**字符串化的 JSON**，不是嵌套对象/数组：客户端自己解析（标绿靠 `choice_json ∩ options_json[].label`）。
- **MUST** 字段缺项、类型不对**不得让整单失败**：按上表逐字段兜底，坏字段当缺项（一个坏字段让整单失败，用户看到的是「识别不出来」，真因却在某道题的一个键上）。
- **MUST** 主机 **MUST NOT** 对题目做改名、映射、白名单或补题目序号之外的重排：`ordinal` 由主机在追加写入时统一顺延（多页/重复识别的场景），Provider 一律**从 0 起**给。
- **MAY** Provider 在帧里额外带上其它键；主机 **MUST** 忽略不认识的键（`01-conventions.md` §1.4）。

### 3.5 认领与迟到回帧（实现现状）

- **MUST** 认领是**一次性**的：一条回帧认领成功后，等待方立即被唤醒，该 `job_id` 从待办里摘除。
- **MUST** 下列三类回帧**一律丢弃**（视为迟到，不得改动任何状态）：① 已超时收口的 `job_id`；② 已因 Provider 掉线判失败的 `job_id`；③ 从未派发过的 `job_id`。
- **MUST** 超时的实现顺序是「**先摘待办、再唤醒等待方**」，所以「超时判定」与「回帧认领」之间**不存在**两者都成立的窗口 —— 客户端不会看到同一单既超时又成功。
- **MUST NOT** 依赖「回帧一定在 `deadline_ms` 内到」：网络与 Provider 排队都可能把它推到超时之后，此时结果被丢弃、任务落 `ai_timeout`，**这是契约行为**，不是 bug。

### 3.6 回帧与任务/会话的落库（实现现状）

- **MUST** 成功：会话写 `done`，题目按 `questions[]` 落库，会话级元信息（`ai_provider` / `ai_model` / `latency_ms`）写会话行，`question_count` 等于落库题目数。
- **MUST** 失败：任务行与会话行**同时**写同一个 `error_code`（只写一侧的话，客户端存下来的历史会显示成「没有错误码的失败」），`error_message` 同理（`08-tasks.md` §1.8）。
- **MUST** 任务终态与 `08-tasks.md` §1.1 的状态机一致：成功 `done`、失败 `failed`；两者都要照常广播对应的 `task_update` / `task_result` / `task_failed`（`05-websocket.md` §2.1）。

## 4. `provider_online`（主机 → 所有客户端，广播）

### 4.1 触发（实现现状）

- **MUST** 三种触发各广播**恰好一次**：① 有连接自述 `role: "provider"` 且登记成功；② 登记失效（那条连接断开、且**当前登记的确实是它**）；③ 被另一个 Provider 顶替（内容是新的自述值、`online: true`）。
- **MUST** 「迟到的关闭」（注销时当前登记的已不是这条连接）**MUST NOT** 触发广播 —— 否则刚连上的新 Provider 会被广播成掉线。
- **MUST** 广播发生在**登记/注销完成之后**，且**等所有帧发完**再继续（与 `device_revoked` 同一纪律：先广播、再关连接）。

### 4.2 字段表

| 字段 | 类型 | 可空 | 取值域 | 语义 |
|---|---|---|---|---|
| `type` | str | 否 | 恒 `"provider_online"` | 判别键 |
| `online` | bool | 否 | `true` / `false` | 现在**有没有** Provider 登记 |
| `ai_configured` | bool | 否 | `true` / `false` | 在线时 = Provider 的自述值；**离线时恒 `false`**（没有自述的来源） |
| `ai_model` | str | **是** | 模型名 | 在线时 = Provider 的自述值；**离线时恒 `null`** |

- **MUST** 上表 4 个键**键恒在**：离线帧也要带 `ai_configured: false` 与 `ai_model: null`，**MUST NOT** 省略。
- **MUST** `ai_provider` **MUST NOT** 出现在本帧里（它只在自述帧与 `/info` 的加法字段里出现）；客户端 **MUST NOT** 期待它。
- **MUST NOT** 把本帧做成「在线状态查询」：它是**变化通知**，没有请求-应答形态；要拿当前值请读 `/api/v2/info`（§6.3）。

### 4.3 广播面与客户端要求（实现现状）

- **MUST** 广播给**当时所有已连接客户端**，**包括刚登记的那个 Provider 自己**、也包括没有 `role` 的普通客户端：没有订阅、没有按设备过滤、也没有按角色过滤（与 `05-websocket.md` §2.1 的广播语义一致）。
- **MUST** 普通（v1）客户端对未知 `type` **静默丢弃**：收到本帧 **MUST NOT** 报错、断连或弹提示（`01-conventions.md` §1.4）。这正是本帧能做成**纯加法**的前提。
- **MUST** 客户端 **MUST** 以「最后一条」为准，并且 **MUST NOT** 因为一条 `online: false` 就清掉已经拿到的识别结果或配对信息 —— 它与配对状态、任务结果都无关。
- **MUST** 断线期间漏掉的 `provider_online` **不会重发**（与 `task_update` 一样）：客户端重连后 **SHOULD** 用一次 `/api/v2/info` 重建基线（`05-websocket.md` §3.2 的补偿）。

## 5. 失败与错误码

### 5.1 `provider_offline`（v2 新增的任务错误码）

- **MUST** `provider_offline` 是**任务错误码空间**的取值（`09-errors.md` §1.4），出现在任务查询响应的 `error_code`、`task_failed` 事件的 `error_code`，以及会话行的 `error_code`；它**MUST NOT** 被当作 HTTP 错误响应的 `code` 使用。
- **MUST** 它表示**用户能自己解决**的状态：「电脑端没有开在线的识别服务」。**MUST NOT** 把它归并到 `internal` —— 那会让用户只看到一句「内部错误」，永远查不到真因。
- **MUST** 以下两种收口都用它：① 派发时**没有** Provider 在线；② 派发中 Provider **掉线**（或帧根本没发出去）。
- **MUST** 它**不在 v1 的任务码表内**（v1 表是 `ai_timeout` / `ai_auth` / `ai_rate_limited` / `ai_bad_response` / `ai_quota_exceeded` / `no_question_found` / `internal`）。对 v1 客户端它是**陌生码**：客户端 **MUST** 按「通用失败」展示、保留原始 `code` 便于排查，**MUST NOT** 崩溃或当成成功（`09-errors.md` §1.2）。
- **MUST** **不许回退成假成功**：没有 Provider 时就失败，**MUST NOT** 用任何内置的固定答案、示例结果或缓存冒充识别结果。
- **MUST** 任务行与会话行**都保留**（`status = failed` + 双侧同码），因此用户可以经 `08-tasks.md` §1.9 的重试端点重新排队；Provider 上线后重试才可能成功。

### 5.2 派发的五种收口

| 情况 | `error_code` | `error_message`（现状文案，**非契约**） | 是否等待 |
|---|---|---|---|
| 派发时没有 Provider 在线 | `provider_offline` | 「电脑端没有在线的识别 Provider（请打开电脑端并确认已连接）」 | **不等待**（判定是一次登记查询） |
| 派发中 Provider 掉线 / 帧发不出去 | `provider_offline` | 「Provider 在识别过程中断开」 | **不等待**（掉线即刻收口） |
| 等满 `deadline_ms` 未回包 | `ai_timeout` | 「Provider ⟨N⟩ ms 内没有回包」 | 等满 `deadline_ms` |
| Provider 回 `ok: false` | 它自报的 `error_code`；不在码表内 → `internal` | 它自报的 `error_message`；空白时用兜底文案 | 不等 |
| Provider 回 `ok: true` 但 `questions` 为空 | `no_question_found` | 「Provider 没有识别到题目」 | 不等 |
| 回帧不是 JSON 对象 | `internal` | 「Provider 回包不是 JSON 对象」 | 不等 |

- **MUST** 「没有 Provider 在线」的收口**必须是立刻的**：判定只查一次登记，**MUST NOT** 等满 `deadline_ms`、**MUST NOT** 排队等 Provider 上线。验收口径：从提交任务到任务落终态 **≤ 2 秒**（这是**验收上限**，不是协议常量；实测在百毫秒量级）。
- **MUST** 「派发中掉线」同样**立刻**收口，**MUST NOT** 等自然超时：用户看到「转 90 秒」而真相是「Provider 早就不在了」，是不可接受的（实现里所有发给它的未收口作业按掉线一次性收口）。
- **MUST** 无论哪一条收口，都按 `08-tasks.md` §1.8 把 `error_code` 同时写进任务行与会话行，并广播 `task_update`（`failed`）与 `task_failed`。

### 5.3 与 HTTP 面的关系

- **MUST** 派发失败**不影响** `POST /api/v1/tasks` 的响应形态：它照旧 `202` + `status: queued`（`08-tasks.md` §1.2），失败只体现在随后的任务终态与广播里。v2 目标里的「未配 AI 前置拒绝」是另一件事（`11-capabilities.md` §1.7 第 4 条），与「Provider 离线」不合并。

## 6. `/api/v2/*` 的版本协商与 `/ws` 的例外

### 6.1 实现现状：按**路径**选主版本

- **MUST** 请求头 `X-QS-Client-Version` 缺失或只有空白 → **放行**，不做任何版本检查（v1 行为，v2 面继续有效）。
- **MUST** 带了该头时，取它的**主版本**（按 `.` 切分的第一段），与**该路径自己的**主版本比较：`/api/v2/*` 比 **2**、`/api/v1/*` 比 **1**。
- **MUST** `/api/v1/*` 在 v2 主机上**固定以主版本 1 参与协商**，**MUST NOT** 拿主机的应用版本（2.x）去比 —— 否则任何 v1 老客户端都会被 426 挡住，「v2 主机同时提供 v1 兼容子集」这条硬要求直接不成立（见 `10-versioning.md` §1.6）。
- **MUST** 主版本不匹配 → **426** + 错误体；现状的 `code` 仍是 `version_mismatch`（**v2 目标**才是 `protocol_mismatch`，见 §6.4）；双方版本号仍只出现在 `message` 文本里。
- **MUST** 主版本比较对**不可解析**的值同样判为不匹配（`2.x.y`、`abc` 都 → 426）；**MUST NOT** 做数字归一化或前缀匹配。
- **MUST** 检查顺序：**先鉴权、后版本**（401 覆盖 426），且版本检查在**路由之前**（路径不存在但版本不符 → 426，不是 404）。
- **MUST** v2 面的鉴权覆盖面与 v1 **完全相同**：`/api/v2/*` 上除豁免清单内的端点外一律要 Bearer token（历史缺陷：只判 `/api/v1/` 前缀时 `/api/v2/*` 整段裸奔）。
- **MUST** 每一个 `/api/*` 响应（**含错误响应**）都带 `X-QS-Server-Version`；**MUST** 客户端把它当**诊断信息**，**MUST NOT** 用它做兼容性判定。
- **MUST** 未实现的 v2 路径返回普通的 **404**（不是协议错误体）：v2 客户端**MUST NOT** 把 404 当网络故障无限重试。

### 6.2 `/ws` **不做**版本协商（v1 冻结语义，v2 不变）

- **MUST** `/ws` 的握手**完全绕过**版本协商：握手路径不读 `X-QS-Client-Version`、不参与主版本比较、**不会**因为版本不符返回 426；主版本不一致也能建连。
- **MUST** `/ws` 的握手响应**不带** `X-QS-Server-Version`（「每个响应都带」那条只针对 HTTP）。
- **MUST** `/ws` 上的版本信息**只**走 `hello` 帧的 `protocol_version`；而它自述的是**兼容层**的主版本（现状 `1`），与「这条连接是 v2 客户端」无关。
- **MUST** 客户端 **MUST NOT** 用 WS 建连成功当作「版本兼容」的证据；也 **MUST NOT** 因为 WS 不带响应版本头而报错（`02-transport.md` §1.8、`05-websocket.md` §1.1）。
- **MUST** Provider 的握手与普通客户端**同一条规则**：同样只认 `Authorization` 头、同样不协商版本。

### 6.3 `/api/v2/info`（v2 面的自述与协商入口）

- **MUST** 它是要鉴权豁免的（与 v1 的 `/info` 同级）：客户端靠它在**还没配对**或令牌不可用时也能问出「对端是谁」。
- **MUST** 它是 v1 `/info` 的**超集**：v1 字段的键名与取值一字不改（`device_id` / `device_name` / `platform` / `app_version` / `ai_configured` / `active_collection_id` / `active_collection_name` / `capabilities`），另加 v2 的加法字段。
- **MUST** `protocol_version` 在 v2 面自述 **2**（v1 面自述 1）：两处取值同源、主版本一致。
- **MUST** v2 的加法字段（键恒在）：
  | 字段 | 类型 | 可空 | 语义 |
  |---|---|---|---|
  | `v1_compat` | bool | 否 | 主机是否**同时**提供 v1 兼容子集；现状恒 `true` |
  | `provider_online` | bool | 否 | 现在有没有登记中的 Provider |
  | `provider_model` | str | 是 | 在线 Provider 自述的模型名；离线恒 `null` |
- **MUST** `capabilities` 的语义仍是 `11-capabilities.md` 的现状（常量数组、顺序不是契约）；v2 目标里的「按能力计算得出」**未实现**。
- **MUST** 客户端用**探测**判对端版本：能连上但响应里**没有** v2 加法字段 → 判定对端是 v1 主机，**MUST NOT** 拿 `/api/v2/*` 的 404 反复重试（`versions/compat-matrix.md` 矩阵一第 3 行）。

### 6.4 v2 目标（**未实现**，逐条标注）

1. **v2 目标** 协商头 `X-QS-Protocol: <major>.<minor>`（请求与响应都带），协商依据是**协议版本**而非应用版本；`X-QS-Client-Version` 降为纯诊断。
2. **v2 目标** 不兼容响应改为 `426 protocol_mismatch`，且**对所有请求生效**（含不带协商头的：按「最低支持版本」处理，不再一律放行）；版本区间进 body 的可机读位置（现状只有 `version_mismatch` 与文本 `message`）。
3. **v2 目标** 路径主版本与协商头主版本**必须一致**（不一致即协议错误）。
4. **v2 目标** `/api/v2/*` 的完整端点面（现状只落地了 `/info` 与 `sync/ops`；其余路径是 404）。
5. **v2 目标** 大 JSON 的 gzip 与 NDJSON 流式分页协商。
6. **v2 目标** 错误体扩展为 `{code, message, retryable, retry_after_seconds?, request_id?, details?}`。

## 7. v1 兼容层硬要求（与 Provider 相关的部分）

v2 主机**同时**提供 v1 兼容子集（完整清单见 `10-versioning.md` §1.6 与 `versions/compat-matrix.md` §1.1）。就本域而言：

- **MUST** v1 客户端**一行不改**也能跑完整链路：配对 → 上传 → 建任务 → 轮询/WS 拿结果。**MUST NOT** 要求 v1 客户端理解 Provider 概念。
- **MUST** `/api/v1/*` 的路径、字段、状态码、错误码**逐字兼容 v1 语义**（不是「只留路径」）；v1 面**不加** Provider 相关字段（v1 的 `/info` 一个键都不加，`provider_online` 只出现在 `/api/v2/info`）。
- **MUST** v1 面的 `hello` **不带** Provider 字段、`protocol_version` 自述 **1**、且**不做**版本协商（§6.2）。
- **MUST** v1 客户端在 Provider 离线时拿到的是**任务失败**（`error_code = provider_offline`）+ 一次 `task_failed` 广播；它不认识这个码，按 `09-errors.md` §1.2 落到通用失败文案即可 —— 这是**允许且预期**的降级，**MUST NOT** 因此判兼容层不成立。
- **MUST NOT** 兼容层为此改成第二条代码路径/第二套语义：同一路径在 v1 与 v2 下分别符合各自规范。
- **MUST** 兼容层在 `X-QS-Client-Version` 上**不做**应用版本比对（§6.1 的第三条）：这是现状缺陷（`versions/compat-matrix.md` §1.1 末尾的风险条），v2 主机 **MUST** 显式废止它，否则「v1 客户端照常工作」这条硬要求不成立。
- **MUST** 兼容层的**语义**（`tasks/active` 在 `done` 时直接带 `session`、本机任务不暴露 `task_id` 且 `task_id == session_id`、合集删除方向不对称、缓存与同图去重只对单页、多页硬上限 6）保持不变；Provider 派发只改「谁来执行」，**MUST NOT** 改 v1 面的响应形状。

## 8. 数值表

| 项 | 值 | 依据 |
|---|---|---|
| 同时登记的 Provider 数 | **1**（登记即顶替） | 在线登记处 |
| 派发帧的键数 | **8**（`type` / `job_id` / `task_id` / `session_id` / `collection_id` / `image_hashes` / `deadline_ms` / `created_at`） | 派发构造 |
| `deadline_ms` 默认值 | **90000** 毫秒 | 协议冻结的默认等回包时限 |
| 没有 Provider 时的收口耗时 | **立刻**（验收上限 **2 秒**） | 无 Provider 分支 |
| 掉线收口耗时 | **立刻**（不等 `deadline_ms`） | 掉线分支 |
| `hello` 自述的加法键数 | **4**（`role` / `ai_configured` / `ai_provider` / `ai_model`） | 自述帧构造 |
| `provider_online` 的键数 | **4**（`type` / `online` / `ai_configured` / `ai_model`） | 广播构造 |
| `provider_online` 的触发次数 | 登记 / 注销 / 顶替各 **1 次** | 广播调用点 |
| `questions[]` 元素的协议键数 | **15** | 题目表的线缆投影 |
| v2 面 `protocol_version` | **2**（v1 面 **1**，两处独立自述） | 主机选项常量 |
| v2 已落地的路径 | `/api/v2/info`、`/api/v2/sync/ops` | 路由表 |
| 客户端帧大小上限 | **1048576** 字节（1 MiB） | 宿主读循环 |
| Provider 侧并发 | **1**（一次一个作业） | Provider 处理循环 |
| Provider 连接重连退避 | 1s / 2s / 5s / 10s / 30s（此后保持 30s） | 与 `05-websocket.md` §3.2 同源 |

## 9. 一致性向量覆盖（28 步，域 `v2-provider`）

**本域现在有向量了**：`conformance/vectors/provider.ndjson` **28 步**，头行声明域 `v2-provider`。
**C# 主机（`QuizSyncServer`）全 28 步通过**（回放器打真 HTTP + 真 WS，不引用任何一端的客户端实现）。

覆盖范围：Provider 角色登记与 `provider_online` 广播、`recognition_job` 定向单播与字段、
成功/失败回帧（含 `questions[]` 的题目列级往返）、认领与迟到回帧、掉线后的 `provider_offline` 快速失败。

> 历史记录（保留，因为它教过一次教训）：本文件较早的一版把覆盖写成 `websocket.ndjson` 的 22–38 步 ——
> **那些步骤不存在**，属不实声明，已整段删除；随后一版又写成「本域目前零覆盖」并列出两个前置。
> 写覆盖表时必须回头数一遍向量文件的行数与本仓的真实状态。

### 9.1 两个前置都已落地（2026-09-28）

1. **向量分域**：向量头行新增 `domain` 字段，`check_vectors.py` 增加第 ⑤ 项校验
   （取值只认 `v1` 与 `v2-provider`，缺省 `v1`），`conformance/README.md` §1 增加「域」小节。
   纪律：v1 的 6 组向量要求**所有实现全绿**；`v2-provider` 只由**带 Provider 角色**的实现回放 ——
   冻结在 v1 的参考实现**明确不参与**（它根本没有 Provider 这个概念，让它回放等于要求冻结实现长出 v2 能力）。
2. **回放器补能力**：`ws.target`（一条向量里并存两条 WS 连接：设备侧与 Provider 侧）、
   `connect: "provider"`（用 `$providerToken` 握手，并按 §1.2 发 Provider 自述帧）、
   `ws.expect` 从收帧里取变量（例如 `job_id`，供后续步骤引用）、按域切换识别执行方
   （provider 域走**真派发路径**，不再固定成内置夹具）。

### 9.2 强度要说清楚：这是「实现侧全绿」，不是「三端裁判」

参考回放器（Dart，冻结在 v1）不参与本域，所以本域的裁判强度是
「**C# 主机全步通过** + 域机制保证别的实现不会被误判」，而不是 v1 那种「所有实现都必须全绿」。

| 规则 | 现在的依据 | 性质 |
|---|---|---|
| 登记 / 派发 / 回帧 / 掉线收紧（28 步） | `provider.ndjson`，C# 回放器全步通过 | **域内裁判**（`v2-provider`） |
| `/api/v2/info` 加法字段与在线状态同步 | 服务端集成测试（真 WS + 真 HTTP + 假 AI） | 单端 |
| Provider 真的能调 AI 并回传结果 | 服务端集成测试 + 真 Key 的一次识别（2321 ms） | 单端 |

## 10. 文档-实现分叉与待确认

### 10.1 分叉

| 现有文档怎么写 | 实现是什么 | 处置 |
|---|---|---|
| `08-tasks.md` §1.12 第 3 条写「Provider 不在线时任务**停在 `queued` 并退避重试**」 | 实现是**立刻**落 `failed` + `provider_offline`（≤2 秒），**不排队等待**；重做要靠用户/客户端重新提交或走 `retry` | 本文件按**实现**写（§5.1 / §5.2），并把 `08-tasks.md` 那句标注为**已被本域取代的 v2 目标措辞**（v1 冻结文本不改，改动只发生在 v2 线） |
| `versions/compat-matrix.md` §4 最后一行把「任务执行方离线」的 v2 目标写成「停在 `queued` 退避重试」 | 同上一行：实际是立刻失败 + 明确错误码 + 任务保留可重试 | **本次已按实现改写** `compat-matrix.md` §4 末行（原文那句作废，理由写在那一行里） |
| 「Provider 离线时任务保留不丢」这句决策措辞容易被读成「任务留在 `queued`」 | 「保留」指的是**任务行与会话行不丢**（可查询、可重试），不是状态停在 `queued` | 本文件 §5.1 末条写明「保留 = 可查可重试」 |
| `05-websocket.md` §2.1 的服务端 → 客户端事件表里没有 `provider_online` | 实现会广播它（登记/注销/顶替各一次） | 本文件 §4 定义；`05-websocket.md` 只加一条指向本文件的引用（v1 表不改） |
| `10-versioning.md` §1.5 把 `X-QS-Protocol` 与 `protocol_mismatch` 写成「v2 变更」 | 两者**都未实现**；实际落地的是「按路径选主版本 + 保留 `version_mismatch` 码」 | 本文件 §6.1 按实现写，§6.4 把未实现项逐条标成 **v2 目标** |
| `10-versioning.md` §1.3 写「`/ws` 不参与版本协商」 | 实现一致（握手绕过中间件）；但**曾经**因为版本闸门跑在每个请求上而回归过 | 本文件 §6.2 保留为 **MUST**，并要求一条**针对 `/ws` 的**负向用例（历史教训：靠别的用例顺带覆盖会漏） |
| `11-capabilities.md` §1.3 把 `ai_configured` 写成「主机侧配置非空」 | v2 面改成「有 Provider 时以**它的**自述为准，没有时退回主机启动配置」 | 本文件 §1.4 写明；`/api/v1/info` 的**形态**不变（取值来源变了） |

### 10.2 待确认（会改变本文件结论的问题）

1. **`collection_id` 空值时怎么办**（§2.2 注）：派发路径上必非空，但实现允许为空，未观察到样本；空值时 Provider 的行为**未规定**。
2. **`ai_provider` 的取值域**：现状是自由文本（形如 `openai-compatible`），没有枚举；是否收紧成固定取值集未定。
3. **`latency_ms` 的口径**：现状是 Provider 那一次 AI 调用的耗时（不含取图与排队）；是否改成端到端耗时未定 —— 客户端把它当「识别耗时」展示，口径不同会让用户看到偏小的数字。
4. **多页识别的缓存**：现状只对单页允许命中缓存；多页是否有别的复用路径未定（`08-tasks.md` §1.4 的复用是**主机侧**的，与 Provider 侧缓存是两件事）。
5. **主机自述 `ai_configured` 与 Provider 自述不一致时的优先级**：现状是「在线以 Provider 为准」；Provider 自述 `false` 而主机启动配置为 `true` 时会**显示为未配置**，是否符合预期未确认。
6. **Provider 的 `device_id` 契约**：实现里 Provider 用一个**固定字面量**（与界面客户端不同），而 v2 目标是「每安装生成一次的 UUID」（`01-conventions.md` §1.3）；两者如何衔接未定。
7. **顶替期间的 `online:false` 是否该被抑制**（§1.3）：现状允许出现，客户端要求「以最后一条为准」；是否改成「重连窗口不发中间态」未定。
8. **`/ws` 是否需要一条显式的「不做版本协商」握手头负向用例**（§9 缺口 4）：属回放器能力问题，不是规范问题。
