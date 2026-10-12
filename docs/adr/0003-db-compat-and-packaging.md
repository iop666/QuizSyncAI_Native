# ADR 0003 · 数据库兼容、包名与升级路径

- **状态**：已接受（2026-09-26，用户拍板）
- **决策编号**：D3
- **相关**：`docs/NATIVE-2.0-PLAN.md` §9、§12、§13、附录 C；`docs/data-model.md`（schemaVersion 3 契约）

## 背景

原生 2.0 要面对三件事：

1. **用户机器上已经有数据**：Windows 端 `userdata\quizsync.db`（drift schemaVersion 3，11 表 + FTS5 `trigram` 虚表）、`images\<sha256>.jpg`、`backups\`、`exports\`；手机端有自己的镜像库与离线队列。
2. **SQLite 能力不确定**：中文检索依赖 `FTS5 + tokenize='trigram'`（默认 `unicode61` 会把整段中文当一个 token）。Windows 侧若用系统 SQLite、Android 侧若用 ROM 自带 SQLite，FTS5 与分词器可用性都无法保证。
3. **Android 包名已固定**：`applicationId = com.quizsync.android`（M24 换名时已明确「旧版必须卸载重装」），而 `namespace` 与 Kotlin 包路径仍是 `com.example.quizsync_android`。

## 决策

1. **数据库 schema 保持与 drift v3 逐字一致**（表名、列名、类型、索引、FTS 虚表、触发器、`field_clocks_json` 语义、`deleted_at` 语义），**不做 v4 迁移**。原生实现直接打开现有库。
2. **两端自带 SQLite**：
   - Windows：`Microsoft.Data.Sqlite` + `SQLitePCLRaw`（`e_sqlite3`，含 FTS5）；
   - Android：Room + `androidx.sqlite` 的 **`BundledSQLiteDriver`**（自带较新 SQLite，保证 FTS5 + trigram）。
   **不使用系统/ROM 自带 SQLite。**
3. **FTS5 回退方案**（Phase 3 spike 决定是否启用）：若 `BundledSQLiteDriver` 在目标机型上无法启用 FTS5+trigram，则回退为「预分词 n-gram 列 + FTS4 / LIKE」。两方案必须通过**同一组检索测试**（中文 ≥3 字命中、<3 字退化 LIKE、墓碑不出现在结果、`LIKE` 转义）。
4. **Android 包名沿用 `com.quizsync.android`**（`namespace` 与 Kotlin 包路径顺手统一为 `com.quizsync.android`，修掉现状的命名分裂）。升级路径固定为：
   **卸载旧版 → 安装新版 → 重新配对 → 从电脑端拉回历史与合集。**
5. **升级前的数据提醒（写进 2.0 升级说明与界面提示）**：卸载会清掉手机端**还没上传成功的离线队列**（上限 20 条 / 5 次尝试后死信）。升级前先让手机在电脑在线时跑空队列，或确认没有待上传的图。
6. **版本起点**：协议 `v2.0.0`、客户端 `2.0.0`、Server `2.0.0`。Flutter 线冻结在 `1.1.0`（tag `v1.1.0-flutter`，`flutter` 分支锁定）。**不得沿用 `dist\` 目录里的最大号**（该目录同时存在 Windows-only 的 1.1.3 与双端统一的 1.1.0）。
7. **并存期单写者保护**：native 版启动时等待 Flutter 版已知的命名信号量 `QuizSyncAI_SingleInstance_7C6E1F42`；检测到旧版在跑就提示并不写库。首次运行采用**复制式迁移**（先复制库与图片，再切换），不做原地升级。
8. **备份格式向后兼容**：现有备份是「zip：`quizsync.db` + `-wal` + `-shm` + `images/**`，无 manifest、无版本字段」。原生端**必须能读旧 zip**；新格式可加 manifest，但读取路径要兼容无 manifest 的旧包；恢复仍遵守「包里没有库则不动现有数据」与 zip-slip 防护。
9. **数据目录与图片**：Server 侧 Blob 放 `QuizSyncServer\images\`；客户端本地缓存目录保留（供离线识别与「磁盘不足时结果优先」）；`images.local_path` 仍是**本地专属列**（不进 op、不被远端覆盖，写它必须走显式接口）。

## 理由

- 不做 v4 迁移，才能让「升级后历史/合集/设置原样都在」成为**可验证**的承诺（`migration_test` 口径：升级后数据不丢，并有红→绿用例）。
- 自带 SQLite 是唯一能保证 FTS5 + trigram 一致行为的做法；否则中文检索会在不同机型上随机失效。
- 包名不变 + 卸载重装是既有决策（`SPEC.md §10`）的延续；手机端数据本质是 Windows 端的镜像，重装后重新同步即可，损失可控。
- 单写者保护解决的是「Flutter 1.1.0 与 native 2.0 同时运行互写同一个库」这个真实风险（现有单实例信号量只防止同一 exe 的多开，管不了两个不同 exe）。

## 后果

**正面**：旧数据零迁移成本；中文检索行为确定；升级路径清晰；并存期不会写坏库。

**代价**：
- 两端各自捆绑一份 SQLite（体积增加；Android APK 会大几百 KB 到 1MB 量级）。
- 手机用户需要卸载重装并重新配对（需在升级说明里显著提示，并提醒先跑空离线队列）。
- 复制式迁移会短期占用双份磁盘空间（迁移完成后可清理旧副本）。

## 验收

1. **双向旧库回归**：Flutter 1.1.0 写的真实 `quizsync.db` → 原生端打开 → 数据完整（会话/题目/合集/设置/图片路径）→ 原生端写入后 → Flutter 1.1.0 仍能正常打开与显示。
2. 中文检索：≥3 字命中、<3 字退化 LIKE、墓碑不出现在结果（FTS5 与回退方案各跑一遍）。
3. 旧备份 zip（无 manifest）能被原生端成功恢复；「包里没有库」时不改动现有数据。
4. 升级流程演练：卸载旧 APK → 装新 APK → 重新配对 → 历史与合集从电脑端拉回 → 离线队列为空时数据无损失。
5. 并存期：Flutter 版运行时启动 native 版，native 版不写库并给出提示；反之亦然。
