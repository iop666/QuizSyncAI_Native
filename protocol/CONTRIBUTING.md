# 贡献指南（QuizSyncProtocol）

## 依赖方向

只出不进：`QuizSyncAI` / `QuizSyncServer` → `QuizSyncProtocol`。本仓库**零依赖**（不需要任何运行时、包管理器、构建脚本）。

## 提交规范

- 中文提交信息，格式 `<域>: <做什么>`；域取 `spec` / `schema` / `examples` / `vectors` / `versions` / `docs` / `chore`。
- **规范、Schema、示例、向量必须同一次提交一起改**。只改一样视为未完成。
- 一次提交只做一件事；不要把「重写某节」和「顺手补个错别字」混在一起。

## 版本号唯一来源

- 协议版本只在 `versions/CHANGELOG.md` 声明（当前：v1 快照待反推，v2 起草中）。
- 代码/实现里不得出现第二处协议版本定义；实现应当在启动时读取协商结果而不是硬编码。

## 评审红线（违反即拒绝）

1. **出现任何一端的实现词汇**：具体语言、框架、包名、仓库内路径、类名、控件名。规范必须语言中立（SQLite 表名/列名、JSON 字段名、HTTP 头名、端口号、路由路径不算实现词汇，它们是契约本身）。
2. **语义变更没有对应的向量**：改一条规则就要有能证明它的向量。
3. **放宽已定的硬约束**：上传 2MB 上限、多页 6 页上限、每设备每分钟 30 次上传、配对码 5 分钟有效期、5 次/分、10 次失败锁 60 秒 —— 改这些必须走决策记录（ADR）而不是随手改数字。
4. **动冻结版本**：已打 tag 的主版本里只能追加，不得修改既有语义。
5. **把「待验证假设」写成「契约」**：尚未在真实设备/真实环境验证过的行为，必须显式标注为假设。
6. **删错误码**：错误码是客户端分支的依据，只能标记废弃，不能删除。

## 发布

冻结一个主版本 = 打 tag `vX.Y.0` + 在 `versions/CHANGELOG.md` 写明冻结范围与兼容矩阵 + 在 `versions/compat-matrix.md` 补上该版本的行。

## CI 会检查什么

`.github/workflows/ci.yml` 只做**数据自检**：不构建、不安装任何一端的实现（本仓库零依赖），全部逻辑在 `conformance/` 下的两个 Python 脚本里（只用标准库）。

| 脚本 | 检查 |
|---|---|
| `conformance/check_vectors.py` | ① `conformance/vectors/*.ndjson` 一行一个 JSON 对象（可解析）；② `step` 从 1 起**连续**递增；③ 同一文件内 `step` **唯一**；④ 出现的 `$变量` 必须在同文件**更早**的步骤里被 `capture` 过，或属于内置变量（`pairingCode` / `port`） |
| `conformance/check_language_neutral.py` | ⑤ `spec/`、`schema/`、`examples/` 下的 Markdown 不含实现绑定词汇：`dart` `flutter` `kotlin` `shelf` `drift` `dio` `riverpod` `pubspec` `quizsync_core` `quizsync_ui` `packages/` `apps/`，以及**任何 Windows 盘符路径**（大小写不敏感） |

任一检查失败 = CI 红；两个 job 都是独立的，能一眼看出是向量写坏了还是规范里混进了实现词汇。

本机自检（不需要装任何包）：

```bash
python conformance/check_vectors.py
python conformance/check_language_neutral.py
```

⑤ 的匹配是**子串**级的：`studio` 会命中 `dio`、`apps/` 会命中路径片段。命中了先分辨是不是真的实现词汇，是就改写；不是就换个中立写法。SQLite 表名/列名、JSON 字段名、HTTP 头名、端口号、路由路径是**契约本身**，不在禁用范围内。
