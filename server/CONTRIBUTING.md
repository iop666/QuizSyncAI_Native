# 贡献指南（QuizSyncServer）

## 依赖方向

```
QuizSyncServer ──► QuizSyncProtocol   （只依赖规范与一致性向量）
```

- **不得**依赖 `QuizSyncAI`（产品仓库）的任何代码；客户端要用 Server 的能力时，只能引用 `QuizSync.Server.Core` 的公开接口。
- **不得**把协议结构在本地复制一份改：结构以 `QuizSyncProtocol/schema` 为准，Server 只实现行为。
- **不得**让 Server 需要 API Key：识别属于 Provider。任何「顺手在服务端加个 AI 调用」的改动一律拒绝。

## 提交规范

- 中文提交信息，格式 `<域>: <做什么>`；域取 `cli` / `core` / `transport` / `pairing` / `blob` / `tasks` / `ops` / `docs` / `ci` / `chore`。
- 行为改动必须同一次提交带上测试；涉及协议行为的还要带上向量回放结果。
- 一次提交只做一件事。

## 版本号唯一来源

- Server 版本只在 `src/QuizSync.Server.Cli` 的项目文件里声明一次；`--version`、`doctor`、日志头都从它读，**不许**在别处硬编码。
- 协议版本是另一个数字：Server 声明自己支持的协议版本区间（与 `QuizSyncProtocol/versions/CHANGELOG.md` 对应），两者不得混用。
- Server 只随协议版本升级。

## 发布红线

1. **交付物里不得出现任何密钥或令牌明文**：`state.json` 只存令牌哈希；`config.json` 不得含 API Key。
2. **不得把用户数据打进交付物**：`images\`、`logs\`、`state.json` 属于运行期数据，不随包发布。
3. **不得引入 Windows 服务形态**（理由见 README §3）。
4. **不得自动更新**：升级由用户显式执行；升级前必须自动备份 `state.json`。
5. **不得放宽协议硬约束**（上传 2MB、每设备每分钟 30 次、配对码 5 分钟 / 5 次每分钟 / 10 次失败锁 60 秒）—— 改这些要改规范，不是改这里。
6. **不得让内嵌模式与独立模式行为分叉**：同一套向量必须跑两遍且结果逐字段一致。

## 发布流程

1. `dotnet test` 全绿 + 向量回放 100% 通过；
2. 冷启动 / 空闲 RSS / CPU 采样记录进 CHANGELOG；
3. 打 tag `X.Y.Z`，交付物与 `QuizSyncAI` 的发布说明互相引用。
