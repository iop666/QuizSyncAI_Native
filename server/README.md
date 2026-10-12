# QuizSyncServer

**QuizSync AI 的唯一 Host**：监听、配对与鉴权、设备注册表、图片 Blob、任务队列与结果缓存、op 中继与水位、状态/配置/日志、把识别任务派发给在线 Provider。

两个产物：

- `quizsync-server` —— 命令行程序（可 `--hidden` 后台运行）
- `QuizSync.Server.Core` —— 库，供 Windows 客户端**内嵌**（没有独立 Server 时同进程直通）

## 1. 是 / 不是

| | 内容 |
|---|---|
| **是** | Host 的全部职责：传输（HTTP / WebSocket）、配对与令牌、设备注册表、图片 Blob、任务队列与结果缓存、op 中继与水位、状态 / 配置 / 日志、识别任务派发（含 Provider 离线语义） |
| **不是** | 任何 UI（含托盘与通知外壳）、AI 调用与 API Key、截屏与全局热键、悬浮窗 |
| **不是** | 题库与同步冲突算法：op 对它**不透明**，只按 Lamport 顺序记录、转发与裁剪 |
| **不是** | 任何一端的界面代码（Windows 界面在 `QuizSyncAI`，安卓界面在 `QuizSyncAI`） |

**边界一句话**：Server 永远不知道「答案对不对」，也永远不持有 API Key —— 识别能力属于 Provider（Windows 客户端）。

## 2. 依赖方向

```
QuizSyncServer ──► QuizSyncProtocol         （规范 + 一致性向量，唯一共享物）
QuizSyncAI(Windows) ──► QuizSync.Server.Core （内嵌模式）+ QuizSyncProtocol
```

Server **不依赖**任何客户端代码；客户端**不引用** Server 的实现细节（只引用 `QuizSync.Server.Core` 的公开接口）。

## 3. 形态与互斥

| 形态 | 说明 |
|---|---|
| 独立 | CLI 常驻，**随用户登录启动**（`setup` 安装/移除启动项）；客户端检测不到时一键拉起 |
| 内嵌 | 客户端进程内加载 `QuizSync.Server.Core`，同进程直通（允许性能优化，但行为必须与独立模式逐字段一致） |
| 单机 | 没有 Server、不联网、不配对（Windows 单机使用） |

互斥：**命名互斥体 + 端口所有权**，任一启动顺序下同一时刻只有一个 Host。

**为什么不做 Windows 服务**：① 现有密钥是 DPAPI **CurrentUser**，服务账户解不开；② 服务无法把配对码/二维码展示给用户，配对会退化成「改配置文件」；③ 防火墙与权限提示在用户登录期更友好。

## 4. 数据目录

`<程序目录>\QuizSyncServer\`

| 文件 | 内容 |
|---|---|
| `config.json` | 端口、随登录启动、日志级别等 |
| `state.json` | 设备与令牌（只存令牌哈希）、任务、op 水位 |
| `images\<sha256>.jpg` | 图片 Blob（内容寻址） |
| `runtime.json` | `pid` / `port` / `control_token` —— 客户端探活与本地控制用 |
| `logs\` | 日志 |

**不引入 SQLite**：op 对 Server 不透明，只需按 Lamport 顺序追加与游标读取；体量成为问题时再单独立项。

## 5. CLI（计划中的命令集）

| 命令 | 用途 |
|---|---|
| `setup` | 首次向导：端口、随登录启动、防火墙提示 |
| `run` | 前台运行（`--hidden` 后台） |
| `start` / `stop` / `status` | 后台进程控制 |
| `pair` | 生成配对码 + 二维码 + `quizsync://pair?...` 链接 + 有效期 |
| `devices list\|revoke <id>\|rename` | 设备注册表管理 |
| `provider status\|ping` | Provider（Windows 客户端）是否在线 |
| `config get\|set\|list` | 配置读写 |
| `doctor` | 端口 / 防火墙 / 版本 / 连通性 / 时钟偏移 / Provider 在线 / 旧数据目录检测 |
| `log tail [--follow]` | 日志 |
| `version` / `help` | —— |

全局 `--json` 供脚本使用。**明确不做**：`api`（AI Key 归 Provider）、任何 UI、任何 AI / 截屏 / 热键子命令。

## 6. 资源与升级

- 目标：空闲 RSS ≤40MB、稳态 CPU ≤0.5%、句柄平稳、启动 ≤200ms。
- 升级：Server 只随**协议版本**升级；升级前自动备份 `state.json`；**不自动更新**。
- 旧数据迁移：从旧 Dart 版服务端目录（`config.json` / `state.json` / `images`）一次性迁移，`doctor` 检测并提示。

## 7. 前置

| 项 | 状态 |
|---|---|
| .NET SDK | **10.0.x**（`dotnet --version` 能打印即可）。开发机上若装的是 per-user 版本、没进 PATH，用它的绝对路径调用即可 —— 本文件不写死任何机器路径 |
| Git / GitHub CLI | 可选（提交与发布用） |

### 构建与测试

```powershell
dotnet build -v q          # 警告即错误（Directory.Build.props）
dotnet test  --nologo      # 回环集成测试：起真 Host、打真 HTTP
dotnet run --project src\QuizSync.Server.Cli -- --doctor   # 启动自检
```

## 8. 当前状态（Phase 2 · 起步）

- [x] 仓库与目录骨架
- [x] `README.md` / `CONTRIBUTING.md`：职责、绝不放入什么、依赖方向、版本规则
- [x] Phase 2：Kestrel 宿主（端口 8765 + 向上探测 6 个、绑定 `0.0.0.0`、端口 0 = 系统分配）+ `/health`
- [x] Phase 2：`/api/v2/info` 与 `/api/v1/info` 兼容层（各自的 `protocol_version` 与字段形态）
- [x] Phase 2：CLI 参数解析（`--port/--bind/--data/--doctor/--version/--help`）+ 分层纪律（Core 不读 argv、不写控制台）
- [x] Phase 2：配对 / 设备 / 令牌 / 限流 / 锁定（失败计数与锁定期**跨重启保留**）
- [x] Phase 2：Blob 存储（流式限长两阶段、内容寻址、下载路径白名单、按设备限流）
- [x] Phase 2：任务队列（串行执行、`task_id` 幂等、同图同页序复用、队列深度、失败码）
- [x] Phase 2：op 中继与水位、逐字段 LWW、墓碑、快照分页
- [x] Phase 2：`/ws`（hello、任务状态与结果、合集变更、吊销后主动断连、同设备踢旧）
- [x] **一致性向量回放：6 组 120 步全绿**（auth / images / tasks / sync / errors / websocket；用本仓的 C# 回放器打真 HTTP + 真 WS）
- [ ] Phase 2：CLI 全命令集（`pair`/`devices`/`config`/`status`/`doctor` 的完整输出）+ `setup` 向导
- [ ] Phase 2：`/api/v2/*` 的真实 v2 语义（`X-QS-Protocol` 协商、gzip、NDJSON 分页）
- [x] Phase 2：Provider 派发（AI 归 Windows 客户端的 Provider 角色；Server 只派发与回传）
      —— `/ws` 上 `hello.role="provider"` 登记 → `recognition_job` → `recognition_result` 回填；
      没有 Provider 在线时任务**立刻** `provider_offline`（**绝不回退假识别器**），
      假识别器只能靠 `--analyzer fixture` 显式开（`--analyzer-timeout` 默认 90000）
- [ ] CI：工作流已就位但**推不上去**（凭据缺 `workflow` scope），暂存本地分支 `ci-pending`

一致性向量与规范在 [QuizSyncProtocol](https://github.com/iop666/QuizSyncProtocol)；**向量是实现的唯一裁判**。

## 9. 版本规则

- tag 从 **`2.0.0`** 起，与客户端 2.0 对齐。
- **不复用 1.x 的 `server-1.0.x` 编号**：那是旧 Dart 服务端的发布号，重号会造成升级与排障混淆。
- 版本号唯一来源：`src/QuizSync.Server.Cli` 的项目文件；`doctor` 与 `--version` 从这里读。

## 10. 许可

MIT，见 [LICENSE](LICENSE)。
