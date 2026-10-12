# 10 版本与协商

> 状态：v1 现状（**按实现反推**）+ v2 目标（标注清楚哪句是现状、哪句是 v2 变更）。
> 强制级别：**MUST** = 三端必须做到；**SHOULD** = 有理由才能偏离；**MAY** = 可选。
> 「三端」= 主机（内嵌服务端的一端）、独立部署的服务端、客户端。未标「v2」的条目 = v1 现状且继续有效。

## 1. 规则

### 1.1 协议版本 ≠ 应用版本
- **MUST** 分清两个独立概念，**MUST NOT** 互相替代：
  - **协议版本** `protocol_version`：线缆契约版本。现状 = 整数 **1**，由主机在 `/api/v1/info`、配对响应、WS `hello` 三处自述，取值同源。
  - **应用版本**：产品发布号，形如 `MAJOR.MINOR.PATCH`（现状 `1.0.0`），出现在请求头 `X-QS-Client-Version`、响应头 `X-QS-Server-Version`、body 的 `app_version`。
- **MUST NOT** 用应用版本判断线缆兼容性 —— 现状的 426 检查恰恰用的是应用版本（见 §4）。
- **MUST** 客户端把读到的 `protocol_version` 仅作记录 / 展示；现状没有任何一端依据它分支。
- **v2 变更** 协商改以**协议版本**为唯一依据。

### 1.2 现状：固定路径前缀 + 应用版本头
- **MUST** 所有 HTTP 端点路径前缀恒为 `/api/v1/`（现状：硬编码在路由表里，不存在其它版本前缀）。
- **MUST** 客户端在每个 HTTP 请求上带 `X-QS-Client-Version: <应用版本>`。
- **MUST** 客户端在 WS 握手时带 `X-QS-Client-Version` 与 `X-QS-Device-Id`；后者只用于排障，**MUST NOT** 被当作鉴权依据（设备身份一律由 token 反查）。
- **MUST** 主机在**每一个** `/api/*` 响应上带 `X-QS-Server-Version: <应用版本>`，**错误响应也带**。

### 1.3 现状：协商规则
- **MUST** 请求**没带** `X-QS-Client-Version`（缺失或只有空白）→ **放行**，不做任何版本检查。这是过渡策略：老客户端与手工调试请求不被挡在门外。
- **MUST** 带了该头时，取两端的**主版本** = 版本串按 `.` 切分后的第一段，**字符串相等**才放行。
- **MUST** 主版本不等 → **426 `version_mismatch`**；body 是标准错误体，双方版本号**只出现在 `message` 文本里**，没有独立的版本字段。
- **MUST** 比较是字符串比较，不做数字归一化：`1` 与 `1.0.0` 同主版本；`01.0.0`、`v1.0.0` 与 `1.0.0` **不同**主版本（会 426）。
- **MUST** 检查顺序：**先鉴权、后版本**。同一请求既缺 token 又版本不符 → 返回 401，不是 426。
- **MUST** 版本检查在**路由之前**：路径不存在但版本不符时返回 426，而不是 404。
- **MUST** `/ws` **不参与**版本协商（现状：握手路径绕过中间件），因此 WS 上不会出现 426，也没有 `X-QS-Server-Version`。
- **MUST** 客户端把 426 当作终态的不兼容错误展示并提示两端升级，**MUST NOT** 静默重试或降级。

### 1.4 现状：`/api/v1/info` 自述
- **MUST** `GET /api/v1/info` 不需要鉴权，至少给出 `protocol_version`（整数）与 `app_version`（字符串）。
- **MUST** 现状还给出：`device_id`、`device_name`、`platform`（主机自报 `"windows"`）、`ai_configured`、`active_collection_id`、`active_collection_name`、`capabilities`。
- **MUST** `capabilities` 现状是固定数组 `["analyze","sync","image_fetch","collections","multipage"]`；**顺序不是契约**（按包含关系判定）。
- **MUST** 配对响应（200 与 409）与 WS `hello` 都带 `protocol_version`。
- **v2 变更** `/api/v2/info` 的 `capabilities` 由能力**计算**得出，并额外给出 `protocol{min,max}`；客户端按能力分支（现状：客户端不按 `capabilities` 分支）。

### 1.5 v2 变更：路径主版本 + `X-QS-Protocol`
- **v2 变更** 主版本进 URL：`/api/v2/*`；同主版本内**只增不改不删** —— 只允许追加新字段、新端点、新事件，已有语义不得改。
- **v2 变更** 协商字段为 `X-QS-Protocol: <major>.<minor>`（例 `2.3`，请求与响应都带），协商依据是协议版本；`X-QS-Client-Version` 保留为纯诊断信息。
- **v2 变更** 主版本不同 → **426 `protocol_mismatch`**，且**对所有请求生效**（含没带协商头的：按「最低支持版本」处理，不再一律放行）。
- **v2 变更** 未知字段、未知事件一律忽略，不得因此报错（与 v1 同级要求，写入 v2 正文）。
- **v2 变更** 错误体扩展为 `{code, message, retryable, retry_after_seconds?, request_id?, details?}`（不再把可机读信息埋进 `message`）；版本不匹配时双方版本的具体字段位置在 v2 正文里定义。
- **v2 变更** 字段废弃必须带 `deprecated_since`，并至少保留一个大版本；删除只能发生在新主版本里。

### 1.6 v1 兼容层（v2 主线必须同时提供）
- **MUST** v2 服务端**同时**提供 `/api/v1/*` 兼容子集，至少覆盖：`info`、`pair`、`images`（上传 / 下载）、`tasks`（创建 / 查询 / 重试）、`tasks/active`、`sessions/{id}/reanalyze`、`collections`（列表 / 选择）、`devices`（列表 / 吊销）、`sync/ops`（推 / 拉）、`sync/snapshot`、`/ws`。
- **MUST** 兼容层逐字兼容 v1 的**语义**，不只是路径：
  - `tasks/active` 在 `done` 时**直接带 `session`**（v2 内部所有任务都有任务行，由兼容层拼出等价响应体）；
  - 主机自己发起的任务在 v1 视图里**不暴露** `task_id`，且 `task_id == session_id`（v2 内部两者独立，由兼容层映射）；
  - 合集删除方向不对称（主机删合集 → 客户端**不跟随删除**）保持不变；
  - 缓存与同图去重**只对单页**生效；多页硬上限 **6** 不变。
- **MUST NOT** 兼容层引入第二套语义：同一路径在 v1 与 v2 下必须分别符合各自规范。

### 1.7 废弃流程
- **MUST** 每个主版本保留上一版的兼容子集，直到该版本正式下线（下线时间与矩阵见 `versions/compat-matrix.md`）。
- **MUST** 协议版本号只在 `versions/CHANGELOG.md` 声明一次，本文件引用它，不重复定义。
- **SHOULD** 下线前至少一个大版本周期内可辨识（现状：没有任何废弃机制，端点只增不减）。

## 2. 数值表

| 项 | 值 | 依据 |
|---|---|---|
| 现状协议版本 | 整数 `1`（`/info`、配对响应、WS `hello` 三处同源） | 服务端配置默认值 |
| 现状路径前缀 | `/api/v1/`（硬编码，无其它前缀） | 路由表 |
| 请求头 | `X-QS-Client-Version: <应用版本>`（全部 HTTP 请求 + WS 握手） | 客户端请求头注入 |
| 响应头 | `X-QS-Server-Version: <应用版本>`（全部 `/api/*` 响应，含错误） | 响应中间件 |
| 主版本比较口径 | 按 `.` 切分取第一段，字符串相等（不归一化数字） | 协商中间件 |
| 不兼容响应 | 426 `version_mismatch`；双方版本只在 `message` 文本里 | 协商中间件 |
| 缺头行为 | 放行（不检查） | 协商中间件 |
| 检查顺序 | 鉴权优先（401 覆盖 426） | 中间件顺序 |
| `/ws` | 不协商版本、不带响应版本头 | 中间件提前返回 |
| 默认应用版本 | `1.0.0`（由宿主注入为产品版本，与响应头、`app_version` 同源） | 服务端配置默认值 |
| `capabilities` | 固定 5 项：`analyze`、`sync`、`image_fetch`、`collections`、`multipage` | `/api/v1/info` |
| v2 目标路径 | `/api/v2/*`（含协商头的 `X-QS-Protocol: <major>.<minor>`） | v2 方案 |
| v2 目标不兼容响应 | 426 `protocol_mismatch`，对**所有**请求生效（含无协商头） | v2 方案 |

## 3. 一致性向量覆盖

| 规则 | 覆盖它的向量 |
|---|---|
| 主版本不同 → 426 `version_mismatch` | `auth.ndjson` 22（`X-QS-Client-Version: 2.0.0` 打 `/api/v1/info`） |
| 不带版本头 → 放行 | `auth.ndjson` 23（同一端点不带该头 → 200） |
| `/info` 自述 `protocol_version` = 1 | `auth.ndjson` 1 |
| 配对响应带 `protocol_version` | `auth.ndjson` 5 |
| 版本检查发生在鉴权之后、路由之前 | `auth.ndjson` 22（`/info` 无需鉴权仍 426）、2、3（缺/错 token 先回 401） |
| 设备身份由 token 反查（`X-QS-Device-Id` 不是鉴权依据） | `sync.ndjson` 3、4（op 的 `device_id` 必须等于已认证设备，冒充被拒） |
| 响应头 `X-QS-Server-Version` | **缺口**：向量断言表没有「响应头」断言 |
| `/ws` 不协商版本 | **缺口**：`ws` 操作待实现 |
| `X-QS-Protocol` / `/api/v2/*` | **缺口**：v2 向量未建 |

## 4. 文档-实现分叉

| 现有文档怎么写 | 实现是什么 | 处置 |
|---|---|---|
| 「主版本不一致时服务端返回 426 并**在 body 里给出双方版本**」 | body 只有 `{code:"version_mismatch", message:"客户端版本 X 与服务端 Y 主版本不一致…", retry_after_seconds:null}`：双方版本只嵌在 `message` 文本里，**没有**独立字段 | 规范按现状写「版本号只在 `message`」；v1 冻结不新增字段，v2 的 `protocol_mismatch` 才给可机读字段 |
| 「客户端在每个请求带上 `X-QS-Client-Version`；服务端响应带 `X-QS-Server-Version`」（措辞覆盖所有请求） | `/ws` 绕过中间件：握手既不检查版本、也不带响应版本头 | 规范限定为「`/api/*` 请求与响应」；WS 的版本信息只走 `hello` 的 `protocol_version` |
| 只写「主版本」，没说是谁的版本 | 比较的是**应用版本**的主版本（客户端头 vs 服务端自报的应用版本），与 `protocol_version` 无关 | 明确写「现状 426 比错对象」；v2 改为按协议版本协商 |
| 「主版本不一致」隐含只在带了头时判定 | 实现明确对**缺头请求放行**，并被向量 23 钉住 | 规范把「缺头放行」写成现状行为；v2 目标改为「默认按最低支持版本处理」 |
| （未提）`protocol_version` 的用途与消费方 | 没有任何一端依据它分支；客户端只在 `/info` 与 WS `hello` 里记录它 | 规范标注它是**自述字段**，不构成协商依据 |
| （未提）版本检查与鉴权的先后 | 先鉴权后版本：401 覆盖 426 | 规范写明顺序，避免客户端把 401 误读成版本问题 |
