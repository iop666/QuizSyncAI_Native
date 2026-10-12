# schema —— 本地数据库 schema（SQLite）与线缆 JSON Schema

本目录放两类互不相同的「schema」，别混用：

| 类别 | 文件 | 管什么 | 正文 |
|---|---|---|---|
| **本地数据库 schema** | `schema-v1.sql`、`schema-v1.json` | 三端各自那个本地库的**物理结构**：表、列、类型、约束、索引、同步语义 | 本文件 |
| 线缆 JSON Schema | `index.json`、`envelope.json`、`entities/`、`messages/`（待写） | 线上消息的**结构** | 文末附录 |

## 1. 用途与边界

- **用途**：三端（主机 / 客户端 / 独立服务端）重写时对齐本地库。三端**各自持有一个本地库**，结构必须逐字一致，否则同步与快照会在列这一层就对不上。
- **是**：表名 / 列名 / 列序 / 类型 / `NOT NULL` / 默认值 / 主键 / `CHECK` / 索引名与索引列 / 各列的同步角色。
- **不是**：同步算法本身（Lamport 排序、逐字段 LWW、水位、快照、GC、离线队列）—— 那在 `spec/06-sync.md`；也不是消息结构（`schema/envelope.json` 那一类）。
- **权威性**：`schema-v1.sql` 的 DDL 与现有实现建库时**执行的语句逐字一致**（含标识符双引号、可空列的显式 `NULL`、布尔列的 `CHECK`）。结构经真实引擎建库后逐列核对：11 表 / 113 列 / 13 索引 / 0 外键，差集为空。
- **纪律**：本目录不出现任何一端的语言、框架、包名与仓库内路径；只出现协议词汇与本地库的物理命名（与 `spec/04-http-api.md` 的约定一致）。

### 文件

| 文件 | 内容 | 怎么用 |
|---|---|---|
| `schema-v1.sql` | 完整 DDL：11 张 `CREATE TABLE` + 13 条 `CREATE INDEX` + 可选 FTS 段 + `PRAGMA user_version = 3` | 直接建库；或作为迁移/自愈的目标结构比对基线 |
| `schema-v1.json` | 机器可读形态：`schema_version` + 每张表的 `columns`（`name`/`type`/`not_null`/`default`/`primary_key`/`check`/`role`/`json_shape`/`logical_fk`）、`indexes`、`sync`、`migrations`、`self_heal_columns`、`fts` | 生成模型 / 做差异检查（CI 里比对实现建出的库） |

- `schema-v1.json` 是**纯 ASCII**（中文走 `\uXXXX` 转义），因此 `python -c "import json;json.load(open('schema-v1.json'))"` 在 GBK 控制台也能解析；`json.load` 解出来的仍是中文。
- `schema-v1.sql` 里的行内注释仅供阅读，**不参与结构比对**；比对用 `PRAGMA table_info` + `sqlite_master`（对照 `schema-v1.json`）。
- 版本号写在 SQLite 的 `PRAGMA user_version` 上（本 schema = **3**），而不是靠文件名。

### 怎么复核（三端实现都可以照做）

1. 解析性：`python -c "import json;json.load(open('schema-v1.json'))"`。
2. 建库：用 SQLite 执行一遍 `schema-v1.sql`（得到一个空库），断言 `PRAGMA user_version` = `schema_version`。
3. 逐表比对：对每张表取 `PRAGMA table_info(<表>)`，与 `schema-v1.json` 里该表的 `columns` 比 **列序 / 列名 / 类型 / `notnull` / `dflt_value` / `pk`**；差集必须为空。
4. 约束比对：取 `sqlite_master` 里该表的 `CREATE TABLE` 原文，比对每列的 `CHECK` 是否与 `check` 字段一致。
5. 索引比对：取 `sqlite_master` 里 `type='index' AND tbl_name=<表> AND sql IS NOT NULL` 的名字集合与 `indexes` 比；**不要**把 `sqlite_autoindex_*`（主键自动索引）算进去。
6. 可选段：FTS 段建不出来（引擎无 FTS5 / trigram）不算失败，但要保证检索走 `LIKE` 兜底。

## 2. 命名与类型约定

| 项 | 约定 |
|---|---|
| 表名 / 列名 | 一律 `snake_case` 全小写（`session_images`、`field_clocks_json`、`answer_in_image`）；表名单复数是历史事实，不要「纠正」：`devices`/`images`/`collections`/`sessions`/`session_images`/`questions`/`sync_ops`/`tasks`/`settings` 用复数，**`peer_state` 与 `ai_usage` 是单数** |
| 类型 | 只用三种：`TEXT`（字符串 / id / hash / 枚举 / JSON 文本）、`INTEGER`（整数、UTC 毫秒、布尔 0/1、Lamport）、`REAL`（目前只有 `questions.confidence`） |
| 主键 | 都是 `TEXT`；取值形态见 `spec/01-conventions.md` §1.3 —— `session_id` / `question_id` / `collection_id` / `session_image_id` / `task_id` / `op_id` 是 UUIDv4，`images.hash` 是 sha256 十六进制，**`devices.device_id` 在 v1 是固定字面量**（`windows-local` / `android-local`，v2 才改成每安装生成的 UUID），快照 op 的 `entity_id` 是固定串 `global`。**没有自增整数主键**，也不要依赖 `rowid`（唯一例外是 FTS 外部内容表按 `rowid` 关联 `questions`） |
| 时间戳 | `INTEGER`，**UTC 毫秒**，列名以 `_at` 结尾（`created_at` / `updated_at` / `deleted_at` / `paired_at` / `last_seen_at` / `revoked_at` / `started_at` / `finished_at` / `called_at` / `last_sync_at`）。唯一不以 `_at` 结尾的时钟是 `lamport` —— 它是逻辑时钟，**不是时间**，排因果一律看它（`spec/06-sync.md`） |
| 布尔 | 落成 `INTEGER`，取 0/1，并带 `CHECK ("<列名>" IN (0, 1))`；线上 op 里也是 0/1（接收端应同时容忍 `true`/`false`） |
| 软删除 | `deleted_at INTEGER NULL`：非空 = 已删除。删除同时产生一条 `op_type = 'delete'` 的 op；一切读取（历史、导出、检索）都带 `deleted_at IS NULL`。没有 `deleted_at` 的表不支持删除同步（见 §4） |
| `field_clocks_json` | `{"<列名>": {"l": <lamport>, "d": "<device_id>"}}`，**只记录被显式写过的字段**（空为 `{}`）；行级 `lamport` / `updated_by` 取所有字段时钟里的最大值。字段没有单独记录过时，回落到行级 `lamport` / `updated_by` 比较。规则见 `spec/06-sync.md` |
| 其它 `*_json` 列 | `TEXT`，存 JSON 文本；形态见 `schema-v1.json` 的 `json_shape`。⚠️ **`sync_ops.fields_json` 同名不同型**：库里是 JSON **文本**，线上是 JSON **对象**（`conformance/vectors/sync.ndjson` 里就是对象） |
| 外键 | **一条都没有**（`foreign_key_list` 全空）。`*_id` 列是逻辑引用（`schema-v1.json` 的 `logical_fk` 只是指向说明），没有级联、没有校验；引用完整性由各端自己做 |
| 可空列的写法 | `schema-v1.sql` 对可空列显式写 `NULL`（跟随实现）；语义与省略该关键字完全相同 |

## 3. 同步语义落在哪些列上

| 列 | 含义 | 出现于 |
|---|---|---|
| `lamport` | 写这行的逻辑时钟（字段级 LWW 的行级基准） | 4 张字段级 LWW 表 |
| `field_clocks_json` | 逐字段写入时钟 | 同上 |
| `updated_by` | 最后写这行的 `device_id` | 同上 |
| `deleted_at` | 墓碑（软删除） | 同上 |
| `revoked_at` | 设备吊销时间（不是墓碑：被吊销的设备行仍然在） | `devices` |
| `sent_lamport` / `acked_lamport` / `last_sync_at` | 本机与某个对端的同步水位线 | `peer_state` |

**两张同步实体表没有 `lamport` / `field_clocks_json`**：`images` 与 `devices`。它们不做字段级 LWW —— 按主键合并、后到者写入（`versions/v1-input/data-model.md` §2.2 的例外）。这是**契约事实**，不是遗漏；重写时不要顺手给它们加时钟列（那会改同步语义，必须 +1 版本）。

## 4. 本地专属列与本地专属表（不进同步）

「不进同步」= 这个值永远不出现在 `sync_ops.fields_json` 里，**并且**收到带这个键的远端 op 时必须忽略。**两个方向都要做**（契约只写了前一个方向）。

| 位置 | 为什么不进同步 |
|---|---|
| `images.local_path` | 「哪台机器上存着这个文件」是各端本地事实。补传成功写回它**不产生 op**；本地文件被清理时把它置 `NULL`，也只是断开关联 |
| `devices.token_hash` | token 的 sha256，只由持有 token 的一方（主机）保存；明文密钥与 token 一律不入库 |
| `tasks.*`（整表） | 本机任务与离线队列：任务由发起端与主机各自管理；`payload_json` 记 `{"image_hashes": [...], "collection_id": "..."}` 供断网重连后原样补跑 |
| `settings.*`（整表） | 本机设置与本地协调状态。「不含密钥」。实现里在用的键（键名不是协议的一部分，但三端语义要对齐）：`pull_cursor:<peer_device_id>`（本机从该对端拉到的最大 `lamport`）、`pending_overwrite:<question_id>`（AI 新结果的「待覆盖」暂存）、当前选中的合集、配对限流的失败计数与锁定截止时间 |
| `peer_state.*`（整表） | 它描述的就是「本机与某个对端同步到哪了」 |
| `ai_usage.*`（整表） | 每台机器自己的用量记账 |

**注意 `peer_state` 没有拉取游标列**：`acked_lamport` 的含义是「对端已确认收到我的 op」，**不能**拿它当拉取游标（会被误判成「对端确认了一切」，从而在折叠时删掉从未推送出去的 op）。拉取游标放 `settings` 的 `pull_cursor:<peer_device_id>`。

## 5. 表 → 是否参与同步 → 依据

| 表 | 参与同步 | op 实体 | 字段级 LWW | 墓碑 | 依据 |
|---|---|---|---|---|---|
| `sessions` | ✅ | `session` | ✅ | `deleted_at` | `versions/v1-input/data-model.md` §2.2/§2.7；写入路径为 session 生成恰好 1 条 op/次 |
| `questions` | ✅ | `question` | ✅ | `deleted_at` | 同上（question）；`analysis_edited` / `answer_edited` 保护用户手改（§2.3） |
| `collections` | ✅ | `collection` | ✅ | `deleted_at` | 契约 §1 的 `collections` + §2.2 |
| `session_images` | ✅ | `session_image` | ✅ | `deleted_at` | 契约 §1 的 `session_images`（多页页序） |
| `images` | ✅（仅元数据） | `image` | ❌ 按主键（`hash`）合并 | 无 | 契约 §2.4「`images` 的元数据随 op 同步（不含文件本身）」；表里没有时钟列 |
| `devices` | ✅ | `device` | ❌ 按主键合并 | 无（吊销用 `revoked_at`，且**不可被旧数据复活**） | 契约 §1 `devices`（配对 / 吊销要跨端可见） |
| `sync_ops` | ❌ 它是载荷本身 | `snapshot`（折叠标记，`entity_id = 'global'`） | — | — | `spec/06-sync.md` §1；契约 §2.6 |
| `peer_state` | ❌ 本机协调状态 | — | — | — | 契约 §2.5「每个对端一条 `peer_state`」 |
| `tasks` | ❌ | — | — | — | 契约 §2.8 离线队列 |
| `settings` | ❌ | — | — | — | 契约 §1「本机设置（不含密钥）」 |
| `ai_usage` | ❌ | — | — | — | 本机用量 |

汇总：**11 表 = 6 张同步实体表（4 张字段级 LWW + 2 张按主键合并）+ 1 张同步载荷表 + 4 张纯本地表**。op 实体的取值集合是 `session` / `question` / `image` / `device` / `collection` / `session_image` / `snapshot` 七个，不多不少。

## 6. 实现版本 → `schema_version` 对照

| 实现（产品版本 / 开发阶段） | `schema_version` | 结构变更 | 依据 |
|---|---|---|---|
| 内部开发线（首个核心版本，未对外交付） | **1** | 9 表 + 10 索引 + FTS 虚表与 3 个触发器 | 里程碑验收记录（「全部 9 表 + 10 索引」）；v1 结构 = 本 schema 去掉 v1→v2 新增的两张表与四个列（见 `schema-v1.json` 的 `migrations`） |
| 内部开发线（合集与多页识别，M8 起） | **2** | +`collections`、`session_images`；`sessions.collection_id`；`questions.incomplete`、`answer_guessed`；`tasks.payload_json` | 契约 §3 的 v1→v2 迁移表（6 条） |
| 对外版本 **1.0.0** 起（材料列随 1.0.0 出厂） | **3** | +`questions.material`（默认 `''`） | 契约 §3 的 v2→v3 迁移表；实现方记录：1.0.0 起出厂即 `user_version = 3` |
| **1.1.0** | **3**（不变） | 无表结构变更；新增「打开时幂等补列」自愈 | 实现方记录（实测用户真实库都是 3） |
| **1.2.x**（当前 1.2.1） | **3**（不变） | 无表结构变更 | 同上 |

结论：`schema_version` 与产品版本**不是一回事**，三端只认 `user_version`；1.0.0 之后至今没有发生过结构变更（v1/v2 只出现在内部开发线）。

### 迁移与兼容约定

1. **版本只增不减**：改表 → `schema_version` +1，并且三端**同步**递增。
2. **只允许兼容变更**：新增表 / 新增可空列 / 新增带默认值的列。改列名、改类型、加 `NOT NULL` 而无默认值、删列都必须走新的主版本并配数据搬迁。
3. **升级路径**：v1→v2→v3 的语句见 `schema-v1.json` 的 `migrations`（与契约 §3 的迁移表一致）。
4. **打开时幂等补列（必须照做）**：历史上 v1→v2 的升级漏加过 `tasks.payload_json`，而库已经被标成 `user_version = 3`，升级钩子再也不会跑，于是那台机器永久缺列（离线队列一读就报「没有这一列」）。因此三端都要在**每次打开库时**按 `schema-v1.json` 的 `self_heal_columns` 清单检查 `PRAGMA table_info`，缺列就 `ALTER TABLE ... ADD COLUMN`（表不存在时跳过，交给升级迁移）：
   `collections.deleted_at`、`sessions.collection_id`、`questions.incomplete`、`questions.answer_guessed`、`questions.material`、`tasks.payload_json`。
5. **跨端版本不一致**：读到主机的版本比本机**新**时提示「主机数据库版本较新」，比本机**旧**时提示较旧，**不要直接崩**（契约 §3）。
6. 改结构必须同一次改动里改完：`schema-v1.sql` + `schema-v1.json` + 契约正文 + 升级迁移 + 迁移测试（用真实的旧版库文件断言「升级后数据不丢」）。

## 7. 文档-实现分叉

对照物：本仓库的现行契约副本 `versions/v1-input/data-model.md` §1（与实现方的现行文档同源）与实现**实际建出的库**。

**先说结论**：表集合（11）、列集合（113）、列序、索引集合（13）**完全一致**，没有多列也没有少列；分叉全部在**约束细节与写法**上。

| # | 契约怎么写 | 实现是什么 | 处置 |
|---|---|---|---|
| 1 | `CREATE INDEX idx_images_created ON images(created_at DESC)`、`idx_collections_created ... DESC`、`idx_sessions_created ... DESC`（3 处带 `DESC`） | 三条索引都是**不带 `DESC`** 的普通索引（倒序由查询的 `ORDER BY` 负责） | 本 schema 按实现写（无 `DESC`）；契约该去掉 `DESC`。单列索引上 SQLite 会反向扫描来满足 `ORDER BY ... DESC`，功能上无差别 |
| 2 | `lamport INTEGER NOT NULL`（`collections` / `sessions` / `session_images` / `questions` 四处） | `INTEGER NOT NULL DEFAULT 0` | 按实现写。差异有实际影响：带默认值意味着「插入时不给 `lamport`」合法，会得到 0 |
| 3 | 布尔列只写 `INTEGER NOT NULL DEFAULT 0`，**没提 `CHECK`** | 8 个布尔列都带 `CHECK ("<列名>" IN (0, 1))`：`sessions.cached`、`questions.need_review` / `answer_in_image` / `incomplete` / `answer_guessed` / `analysis_edited` / `answer_edited`、`ai_usage.ok` | 按实现写进本 schema。⚠️ 三端要注意：**写入非 0/1 的值会被数据库拒绝**（不是静默存下） |
| 4 | 可空列不写 `NULL` 关键字 | 每个可空列显式写 `NULL` | 纯写法差异，语义相同；本 schema 跟随实现（便于与实现逐字对拍） |
| 5 | `CREATE VIRTUAL TABLE questions_fts USING fts5(stem, analysis, content='questions', content_rowid='rowid')` —— **没有分词器** | 多一个 `tokenize='trigram'`（默认分词器切不开中文，整段 CJK 会变成一个词，检索等于失效） | 按实现写。契约必须补上 `tokenize='trigram'`，否则照契约实现的一端中文搜索会坏 |
| 6 | 只写了虚表本身 | 还建了 3 个触发器（`questions_fts_ai` / `_ad` / `_au`）保持同步；另有 4 张由引擎自建的影子表（`questions_fts_config` / `_data` / `_docsize` / `_idx`） | 本 schema 收录虚表与 3 个触发器；影子表由引擎自动创建，各端**不要手写，也不要当成业务表** |
| 7 | §2.4 只写了「补传成功后写入 `local_path`，本身**不产生 op**」（单向） | 两个方向都做：生成 op 时剔除 `local_path` / `token_hash`，**应用远端 op 时也剔除**（否则对端能覆盖我的本地路径） | 契约要写成双向要求（本文件 §4 已按实现写） |
| 8 | §3 的 v1→v2 迁移表里一直有 `tasks.payload_json` | 早期版本的升级路径**漏了**这一列，导致「从 v1 升上来且已被标成 3」的库永久缺列；现已补上，并新增打开时自愈 | 三端都要实现 §6 第 4 条的「打开时幂等补列」，不能只依赖 `user_version` |
| 9 | §1 把 FTS 虚表与普通表混排在同一段 SQL 里，读起来像必建 | FTS 段是**可失败的**：引擎不支持 FTS5 / trigram 时整段跳过，检索退化为 `LIKE`，其余功能不受影响 | 本 schema 把 FTS 单列一段并标注「可选能力」 |

另有两条**不是分叉但容易踩**的事实，一并记在这里：① `sync_ops.fields_json` 在库里是 TEXT、线上是对象（§2）；② `images` / `devices` 没有时钟列是设计而非遗漏（§3）。

## 8. 待确认

1. **独立部署的服务端要不要持有全部 11 表**？本 schema 按「三端同一份结构」编写；如果独立服务端只需要 `sync_ops` + `images` + `devices` + `settings` 这类子集，需要在这里明确写下来（否则三端会出现两套结构）。
2. **`images` 要不要墓碑**？现状：图片行永不删除，删文件只是把 `local_path` 置 `NULL`，也**没有** `image` 的 `delete` op。若三端都希望「删图能同步」，那是结构变更（`schema_version` +1），需要先拍板。
3. **契约副本要不要按本文件修**？`versions/v1-input/data-model.md` 是现状输入材料，本目录没有改它。§7 的 9 条处置（尤其是 `DESC`、`lamport` 默认值、`CHECK`、`tokenize='trigram'`）需要在 Phase 1 的 `versions/v1-snapshot.md` 里落定。
4. **线上 `fields_json` 的类型**：向量里是对象，但 `spec/06-sync.md` 正文没明写「对象」；建议补一句，避免一端发字符串一端发对象。
5. **「打开时幂等补列」要不要进规范正文**？现在只写在 `schema-v1.json` 的 `self_heal_columns` 里。
6. **是否还有 `schema_version < 3` 的真实用户库**？实现方记录说实测的真实库都是 3（v1/v2 只在内部开发线）；在拿到更多样本前，升级路径与自愈清单都保留。

---

## 附录：线缆 JSON Schema（本目录的另一类产物）

组织方式：`envelope.json`（WebSocket 信封与 HTTP 错误体）+ 每种消息一个 schema；实体 schema 被消息 `$ref` 引用。

| 路径 | 内容 | 状态 |
|---|---|---|
| `index.json` | 全部 schema 的索引（CI 按它逐个校验 examples） | 待写 |
| `envelope.json` | WS 信封 `{v, type, id, ts, corr, payload}`、HTTP 错误体 | 待写 |
| `entities/` | 会话、题目、选项、合集、设备、op、任务… | 待写 |
| `messages/` | 每个 HTTP 端点与每个 WS 事件的请求/响应 | 待写 |

### 纪律

- **v2.0 不引入代码生成流水线**：手写模型 + 向量回放保证一致，避免提前引入构建复杂度。
- Schema 是**校验用**的，不是规范正文：语义写在 `spec/`，Schema 只保证结构。两者冲突时以 `spec/` 为准并立刻修 Schema。
- 每个 schema 必须给出 `additionalProperties` 的明确选择（默认允许未知字段，与「未知字段一律忽略」的协商规则一致）。

> 与本地数据库 schema 的关系：线缆 schema 管**线上消息**，`schema-v1.*` 管**本地库**。两者的字段域刻意对齐（op 的 `fields_json` 键 = 实体列名，`spec/06-sync.md` §1），但**类型未必相同**（见 §2 的 `fields_json` 一条）。
