# QuizSyncProtocol

**QuizSync AI 的通信规范仓库**：HTTP / WebSocket 契约、消息结构、JSON Schema、协议版本与兼容策略、语言无关的一致性测试向量。

> 一句话边界：这个仓库是**纸面 + 数据**，不含任何可执行的产品逻辑。

## 1. 是 / 不是

| | 内容 |
|---|---|
| **是** | 三端唯一共享物：Windows 客户端、Android 客户端、QuizSyncServer 都照这一份纸实现 |
| **是** | 产品语义（例如「合集删除方向不对称」「缓存与同图去重只对单页生效」）—— 语义属于规范 |
| **不是** | UI、数据库实现、ViewModel、AI SDK、平台代码、任何一端的实现代码 |
| **不是** | 任何技术栈的词汇：规范里不出现具体语言、框架、包管理器、目录名、类名 |

**依赖方向**：`QuizSyncAI`、`QuizSyncServer` → `QuizSyncProtocol`（只出不进，本仓库不依赖任何仓库）。

## 2. 三种部署形态（规范必须同时成立）

```
独立模式   Windows 客户端 ──loopback(HTTP/WS)──► QuizSyncServer ◄──LAN── Android 客户端
内嵌模式   Windows 客户端【内嵌 Server.Core，同进程】            ◄──LAN── Android 客户端
单机模式   只有 Windows 客户端（不联网、不配对）
```

三种形态下同一套消息的行为必须**逐字段一致**（允许存在时延差异，且差异要记录在案）。

## 3. 目录

| 路径 | 内容 | 状态 |
|---|---|---|
| `docs/` | `00-overview.md`（架构/角色/术语）、`01-deployment.md`（三种部署形态） | 待写（Phase 1） |
| `spec/` | 规范正文，每个域一个文件 —— 见 [`spec/README.md`](spec/README.md) | **已就位（11 份）** |
| `schema/` | 数据库 schema（SQLite DDL + 机器可读 JSON）+ 索引 | 见 `schema/README.md` |
| `examples/` | 线协议报文样例（HTTP / WS / 错误，与向量逐条对应） | 见 `examples/README.md` |
| `versions/` | `v1-input/`、`v1-snapshot.md`、`compat-matrix.md`、`CHANGELOG.md`（`v2-draft/` 待写） | **已就位（除 v2-draft）** |
| `conformance/` | 语言无关的一致性向量（5 组 101 步，全绿）+ 回放说明 + 自检脚本 | **已就位** |

## 4. 版本规则

| 项 | 规则 |
|---|---|
| 形态 | `MAJOR.MINOR`；HTTP 路径带主版本（`/api/v2/...`） |
| 冻结 | 打 tag 即冻结（如 `v2.0.0`）；冻结后**只允许追加**（新字段 / 新端点 / 新事件），不得改已有语义 |
| 协商 | 客户端与 Server 协商协议版本；**未知字段、未知事件一律忽略**，不得因此报错 |
| 兼容层 | 每个主版本必须保留上一版的兼容子集，直到该版本正式下线（见 `versions/compat-matrix.md`） |
| 唯一来源 | 协议版本只在 `versions/CHANGELOG.md` 声明一次，`spec/10-versioning.md` 引用它 |
| 与产品版本无关 | 协议版本 ≠ 应用版本：换实现不动协议，扩协议不必然发版 |

## 5. 怎么用它实现一端

1. 读 `docs/00-overview.md` 与 `spec/01-conventions.md`；
2. 按 `spec/` 逐域实现 —— **不要照抄任何一端的现有代码**；
3. 用 `conformance/vectors` 回放：**向量是三端实现的唯一裁判**。向量失败就是实现错；确实要改向量时，必须同一次提交里改规范并写明理由；
4. 交付前跑 `versions/compat-matrix.md` 里属于自己的那一行。

## 6. 当前状态（Phase 1 · 规范与向量已就位）

- [x] 仓库与目录骨架
- [x] `versions/v1-input/`：四份契约副本（来历与剥离规则见该目录 `README.md`）
- [x] `versions/v1-snapshot.md`：按**现有实现**反推的现状规范（306 行，含 16 条文档-实现分叉 + 7 条新发现）
- [x] `spec/` 11 份 + `schema/` + `examples/`
- [x] `conformance/vectors` 5 组 101 步（auth 23 / images 14 / tasks 18 / sync 32 / errors 14）+ Dart 参考回放器**全绿**
- [x] `versions/compat-matrix.md` + `versions/CHANGELOG.md`（v2 变更收集 61 条）
- [ ] CI：向量格式与语言中立自检脚本已就位（`conformance/check_*.py`），**工作流文件待授权 `workflow` scope 后推送**
- [ ] `docs/00-overview.md` / `01-deployment.md`、`versions/v2-draft/`、WS 向量组

## 7. 许可

MIT，见 [LICENSE](LICENSE)。
