# 01 通用约定

> 状态：v1 现状（**按实现反推**）+ v2 目标（标注清楚哪句是现状、哪句是 v2 变更）。
> 强制级别：**MUST** = 三端必须做到；**SHOULD** = 有理由才能偏离；**MAY** = 可选。
> 「三端」= 主机（内嵌服务端的一端）、独立部署的服务端、客户端。未标「v2」的条目 = v1 现状且继续有效。

## 1. 规则

### 1.1 命名
- **MUST** JSON 字段名一律 `snake_case` 全小写：`image_hash`、`session_id`、`retry_after_seconds`、`field_clocks_json`、`active_collection_id`。
- **MUST** 字段名区分大小写、精确匹配；不提供 `imageHash` 一类驼峰别名。
- **MUST** 时间戳（毫秒 epoch）字段名以 `_at` 结尾：`created_at`、`updated_at`、`deleted_at`、`paired_at`、`last_seen_at`、`revoked_at` 等。唯一例外是 WS 心跳的 `ts`（现状）。
- **MUST** 时长字段按单位给后缀：毫秒用 `_ms`（`latency_ms`）、秒用 `_seconds`（`retry_after_seconds`）；**MUST NOT** 把时长写成 `*_at`。
- **MUST** 列表字段用复数名：`ops`、`questions`、`collections`、`devices`、`sessions`、`session_images`、`images`、`image_hashes`、`capabilities`。
- **MUST** HTTP 路径全小写、`/` 分层，前缀 `/api/v1/`；动作段放在资源 id 之后（`/api/v1/sessions/{id}/reanalyze`、`/api/v1/collections/{id}/select`）。
- **MUST** 错误 `code` 为全小写 `snake_case`：`invalid_request`、`unauthorized`、`revoked`、`not_found`、`invalid_code`、`code_expired`、`rate_limited`、`payload_too_large`、`version_mismatch`、`no_active_collection`、`queue_full`、`too_many_pages`。
- **MUST** 枚举取值是固定小写字符串：状态 `queued|analyzing|done|failed|cancelled`；`op_type` `upsert|delete`；`entity` `session|question|image|device|collection|session_image|snapshot`。
- **SHOULD** 同步载荷里 `fields_json` 的键与实体列名同形（现状：就是 SQLite 列名），逐字段 LWW 不再需要一层字段映射。

### 1.2 时间
- **MUST** 所有时间戳是 UTC 毫秒 epoch 整数（int64）：不用秒、不用 ISO 8601 字符串、不带时区后缀。
- **MUST** 各端取自己的本机时钟，**不做**时钟对齐；因此时间戳只用于展示与「有没有变过」的比较。
- **MUST NOT** 用时间戳做因果判定（新旧、冲突胜负一律用 `lamport`）。
- **MUST** 服务端不得依据客户端给的时间戳拒绝请求（现状：`created_at` 被忽略或原样入库）。

### 1.3 标识符
- **MUST** `task_id`、`op_id`、`session_id`、`question_id`、`collection_id`、`session_image_id` 为 UUIDv4 小写十六进制、带连字符（8-4-4-4-12）。
- **v1 现状（必须照做，v2 要改）**：`device_id` 与配对响应里的 `server_device_id` 是**固定字面量**（Windows 端 `windows-local`、Android 端 `android-local`），不是每安装生成的 UUID —— 这正是「同一时间只支持一台安卓」的根源（第二台手机配对会顶掉第一台）。**v2 变更**：改为每安装生成一次的 UUID。
- **MUST** `entity_id` 形态随 `entity` 而定：会话 / 题目 / 合集 / 页图片 / 设备是 UUIDv4，`image` 是 `image_hash`（sha256 十六进制），`snapshot` 是固定串 `"global"`。
- **MUST** `token` 为 32 随机字节的 64 位小写十六进制；服务端只保存 `sha256(token)` 的十六进制，明文 token 只在配对响应里出现一次。
- **MUST** `image_hash` = sha256(压缩后 JPEG 字节) 的小写十六进制（64 位），内容寻址：同字节必得同 hash，服务端按 hash 去重并回 `existed`。
- **MUST** `GET /api/v1/images/{hash}` 只接受 `^[0-9a-f]{64}$`；不合规一律 404 `not_found`（现状：hash 会被当成文件名，必须挡住路径拼接）。
- **SHOULD** 客户端重试时**保持** `task_id`、`op_id` 不变（它们就是幂等键）。
- **MUST NOT** 校验方仅因 id 不是 UUIDv4 形态就拒绝请求（现状：服务端不校验 id 形态，只校验图片 hash 形态；向量里大量使用 `t-1`、`q-sync-1` 这类合成 id）。
- **v1 现状（v2 变更）** 主机自己发起的任务，`task_id` 等于 `session_id`；v2 起两者独立生成，由 v1 兼容层映射。

### 1.4 未知与缺失字段
- **MUST** 忽略未知字段：解析端只读自己认识的键，**MUST NOT** 因未知键报错或拒绝请求。
- **MUST** 忽略未知的 WS 事件 `type`（静默丢弃，不断连接、不报错）。
- **MUST** 未知的枚举取值不得导致请求失败（现状：分别回落到 `queued`、`snapshot`、`upsert`）。
- **MUST** 可选字段缺失与显式 `null` 等价（都按「无」处理）。
- **SHOULD** 同主版本内允许**纯加法**字段；老实现忽略它必须仍然正确（例如任务状态探测响应里的 `collections`、`ops_lamport`）。

### 1.5 编码与内容类型
- **MUST** 除图片上传（`multipart/form-data`）与图片下载（`image/jpeg` 二进制）外，请求体与响应体都是 UTF-8 JSON；响应头 `Content-Type: application/json; charset=utf-8`（成功与错误一致）。
- **MUST** 图片上传的字段名固定 `file`；图片下载的响应体是原始字节，不是 JSON 包装。
- **MUST** 服务端按 UTF-8 解码请求体（现状：忽略 `Content-Type` 里声明的 charset）；非 UTF-8 字节 = 400 `invalid_request`。
- **MUST** 解析 JSON 的端点上，body 不是合法 JSON 对象（含空体、数组、非法 UTF-8）一律 400 `invalid_request`，不得 500。
- **MAY** 响应里 JSON 对象的键序、空白与缩进不进契约。

### 1.6 错误体
- **MUST** 错误响应体形状固定为 `{"code":…,"message":…,"retry_after_seconds":…}`；无重试建议时该键**仍存在**且为 `null`。
- **MUST** `retry_after_seconds` 是「再过多少整秒可重试」，**MUST NOT** 当绝对时间戳用。
- **MUST** 客户端按 `code` 分支；`message` 只用于展示，文案随时可改，不是契约。
- **MUST** HTTP 状态码与 `code` 成对出现（全表见 `09-errors.md`）。
- **MUST NOT** 在成功响应里塞 `code`/`message`（现状：成功体只有业务字段）。
- **v1 现状例外** 重复配对的 409 **不是**错误体：body 是新 token + `already_paired: true`（见 `03-auth-pairing.md`）。

### 1.7 大小写
- **MUST** HTTP 头名不区分大小写（`Authorization` ≡ `authorization`）。
- **MUST** 认证头按字面发送 `Authorization: Bearer <token>`：方案名 `Bearer ` 区分大小写（现状只认这一种拼写）。
- **MUST** 路径与 JSON 字段名区分大小写（`/api/v1/info` ≠ `/API/v1/INFO`）。
- **MUST** `code`、枚举值、`platform` 一律小写（主机自报 `"windows"`，客户端自报 `"android"`）；`device_name` 等自由文本原样保留、不折叠大小写。

## 2. 数值表

| 项 | 值 | 依据 |
|---|---|---|
| 时间戳单位 | 毫秒（UTC epoch，int64） | 取本机 epoch 毫秒 |
| `token` 长度 | 64 位小写十六进制（32 随机字节） | 配对签发时的随机数生成 |
| `token` 落库形态 | `sha256(token)` 十六进制；明文只在配对响应出现一次 | 设备表的 token 哈希列 |
| `image_hash` | sha256(JPEG 字节) 小写十六进制，64 位 | 上传处理器落盘前的哈希 |
| UUID | v4，小写，8-4-4-4-12 | 标识符生成器 |
| `cached` 取值 | 会话实体里是整数 `0/1`；任务响应里是布尔 `true/false`（解析端两者都收） | 会话实体序列化 / 任务响应 |
| `platform` 取值 | `windows`（主机）/ `android`（客户端） | `/api/v1/info` 与配对请求 |
| 未固定项 | JSON 端点请求体上限、字段长度上限、`device_name` 长度、`retry_after_seconds` 上限 | 未在实现中固定 |

## 3. 一致性向量覆盖

| 规则 | 覆盖它的向量 |
|---|---|
| 错误体形状 + `retry_after_seconds` 为整秒 | `auth.ndjson` 19（429 + `^[0-9]+$`）、`images.ndjson` 12 |
| 路径前缀 `/api/v1/…`、JSON 字段名 snake_case | 四组向量全部步骤的 `do`/`expect`（间接） |
| op 载荷 `fields_json` 的键 = 列名 | `sync.ndjson` 5、7、12（`image_hash`、`question_count`、`answer_text`、`updated_by`） |
| `image_hash` = 64 位小写十六进制、内容寻址 | `images.ndjson` 2（`^[0-9a-f]{64}$`）、4、5（`body_sha256` = `$hash`） |
| `token` = 64 位小写十六进制 | `auth.ndjson` 5（`^[0-9a-f]{64}$`） |
| 同状态码下 `code` 是唯一分支依据（`message` 可变） | `auth.ndjson` 2、3（缺 token 与 token 认不出同为 401 `unauthorized`，只有 message 不同） |
| 未知字段被忽略 | **缺口**：向量格式没有「插入未知字段仍须成功」的操作 |
| 大小写敏感（路径 / 字段名 / `Bearer` 拼写） | **缺口** |
| 时间戳为毫秒 `*_at` | **缺口**：四组向量都没对 `*_at` 字段做断言（`sync.ndjson` 只在 op 载荷里作为**输入**出现 `created_at`/`updated_at`） |

## 4. 文档-实现分叉

| 现有文档怎么写 | 实现是什么 | 处置 |
|---|---|---|
| 错误体统一 `{code, message, retry_after_seconds}`，配对失败表列出 `409 already_paired` | 409 的 body 没有 `code`/`message`，只有 `already_paired: true` 与新 token | 规范写明这是**唯一**的非错误体 4xx；`already_paired` 是布尔标志，不是 `code` |
| 时间戳字段名统一 `*_at` | WS 心跳用 `ts` | 规范把 `ts` 保留为唯一例外并标注 |
| 配对请求字段表含 `app_version` | 校验只要求 `code`/`device_id`/`device_name`/`platform` 非空；缺 `app_version` 照样成功（值记为空串） | 规范把 `app_version` 标为 SHOULD 上报、缺失不拒绝 |
| （未提）布尔与整数的混用 | 同一个 `cached`：会话实体 `0/1`，任务响应布尔 | 规范要求解析端**两者都收**；统一成哪种形态留给 v2 决定 |
| （未提）同一个值在不同载荷里字段名不同 | 上传响应用 `image_hash`，图片实体用 `hash` | 规范按现状分别写明；是否统一留给 v2 决定 |
