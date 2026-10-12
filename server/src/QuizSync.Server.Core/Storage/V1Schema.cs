namespace QuizSync.Server.Core.Storage;

/// <summary>
/// v1 库结构（与 Dart 版逐列对齐：列名即同步 op 的字段名）。
///
/// **与 `QuizSyncProtocol/schema/schema-v1.sql` 必须保持一致** —— 那个文件是
/// 三端对齐用的对外 schema，这里是本实现的内嵌副本。改动时两边一起改，
/// `SchemaParityTests` 会把「对外 schema 里出现的表/列」与这里逐项比对。
/// </summary>
public static class V1Schema
{
    public const int SchemaVersion = 1;

    public const string Ddl = """
        CREATE TABLE IF NOT EXISTS devices (
            device_id TEXT NOT NULL PRIMARY KEY,
            name TEXT NOT NULL,
            platform TEXT NOT NULL,
            token_hash TEXT NULL,
            paired_at INTEGER NOT NULL,
            last_seen_at INTEGER NULL,
            revoked_at INTEGER NULL,
            app_version TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS images (
            hash TEXT NOT NULL PRIMARY KEY,
            size INTEGER NOT NULL,
            mime TEXT NOT NULL,
            width INTEGER NULL,
            height INTEGER NULL,
            local_path TEXT NULL,
            created_at INTEGER NOT NULL,
            uploaded_by TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_images_created ON images (created_at);

        CREATE TABLE IF NOT EXISTS collections (
            collection_id TEXT NOT NULL PRIMARY KEY,
            name TEXT NOT NULL,
            created_at INTEGER NOT NULL,
            updated_at INTEGER NOT NULL,
            updated_by TEXT NOT NULL,
            lamport INTEGER NOT NULL DEFAULT 0,
            field_clocks_json TEXT NULL,
            deleted_at INTEGER NULL
        );

        CREATE TABLE IF NOT EXISTS sessions (
            session_id TEXT NOT NULL PRIMARY KEY,
            image_hash TEXT NOT NULL,
            source_device TEXT NOT NULL,
            status TEXT NOT NULL,
            question_count INTEGER NOT NULL DEFAULT 0,
            cached INTEGER NOT NULL DEFAULT 0,
            collection_id TEXT NULL,
            created_at INTEGER NOT NULL,
            updated_at INTEGER NOT NULL,
            updated_by TEXT NOT NULL,
            lamport INTEGER NOT NULL DEFAULT 0,
            field_clocks_json TEXT NULL,
            deleted_at INTEGER NULL,
            task_id TEXT NULL,
            error_message TEXT NULL,
            latency_ms INTEGER NULL,
            -- 第十三轮补齐（schema_diff.py 量化出来的分叉）：协议 schema 里 sessions 有这 5 列。
            -- 缺了它们，客户端写的 ai_provider / ai_model / prompt_version / raw_response / error_code
            -- 落不进来 —— 手机端历史里看不到模型就是这条的直接表现。
            ai_provider TEXT NULL,
            ai_model TEXT NULL,
            prompt_version TEXT NULL,
            raw_response TEXT NULL,
            error_code TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS session_images (
            session_image_id TEXT NOT NULL PRIMARY KEY,
            session_id TEXT NOT NULL,
            ordinal INTEGER NOT NULL,
            image_hash TEXT NOT NULL,
            created_at INTEGER NOT NULL,
            -- 协议里这两列是 NOT NULL（每张图也参与 LWW）。加列的同时必须改插入点，
            -- 否则 SessionRepository 的 INSERT 会违反约束 -> 建任务 500（第二十七轮实测）。
            updated_at INTEGER NOT NULL,
            updated_by TEXT NOT NULL,
            lamport INTEGER NOT NULL DEFAULT 0,
            field_clocks_json TEXT NULL,
            deleted_at INTEGER NULL
        );

        CREATE TABLE IF NOT EXISTS questions (
            question_id TEXT NOT NULL PRIMARY KEY,
            session_id TEXT NOT NULL,
            ordinal INTEGER NOT NULL,
            question_no TEXT NULL,
            stem TEXT NOT NULL DEFAULT '',
            material TEXT NOT NULL DEFAULT '',
            type TEXT NOT NULL DEFAULT 'unknown',
            options_json TEXT NULL,
            choice_json TEXT NULL,
            -- 协议 schema 里 answer_text 是 **NULL 可空**（选择题的答案在 choice_json）：
            -- 原来这里写 NOT NULL DEFAULT ''，于是选择题的 op 永远落不进服务端
            -- （第十二轮实测 400 `NOT NULL constraint failed: questions.answer_text`）。
            answer_text TEXT NULL,
            analysis TEXT NOT NULL DEFAULT '',
            confidence REAL NOT NULL DEFAULT 0.5,
            need_review INTEGER NOT NULL DEFAULT 0,
            answer_in_image INTEGER NOT NULL DEFAULT 0,
            incomplete INTEGER NOT NULL DEFAULT 0,
            answer_guessed INTEGER NOT NULL DEFAULT 0,
            warnings_json TEXT NULL,
            analysis_edited INTEGER NOT NULL DEFAULT 0,
            answer_edited INTEGER NOT NULL DEFAULT 0,
            created_at INTEGER NOT NULL,
            updated_at INTEGER NOT NULL,
            updated_by TEXT NOT NULL,
            lamport INTEGER NOT NULL DEFAULT 0,
            field_clocks_json TEXT NULL,
            deleted_at INTEGER NULL
        );

        CREATE TABLE IF NOT EXISTS tasks (
            task_id TEXT NOT NULL PRIMARY KEY,
            image_hash TEXT NOT NULL,
            source_device TEXT NOT NULL,
            status TEXT NOT NULL,
            attempts INTEGER NOT NULL DEFAULT 0,
            error_code TEXT NULL,
            session_id TEXT NULL,
            created_at INTEGER NOT NULL,
            started_at INTEGER NULL,
            finished_at INTEGER NULL,
            payload_json TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS sync_ops (
            op_id TEXT NOT NULL PRIMARY KEY,
            device_id TEXT NOT NULL,
            lamport INTEGER NOT NULL,
            entity TEXT NOT NULL,
            entity_id TEXT NOT NULL,
            op_type TEXT NOT NULL,
            fields_json TEXT NOT NULL,
            created_at INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_sync_ops_device_lamport ON sync_ops (device_id, lamport);
        CREATE INDEX IF NOT EXISTS idx_sync_ops_lamport ON sync_ops (lamport);

        CREATE TABLE IF NOT EXISTS settings (
            key TEXT NOT NULL PRIMARY KEY,
            value TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS peer_state (
            -- 第十三轮量化出的最后一处差异：协议用 peer_device_id / last_sync_at，
            -- 服务端原来叫 device_id / last_seen_at —— **同一个概念两个名字**（不是缺列）。
            -- 本表在服务端代码里同样没有任何读写点（只有 DDL），所以改名不影响任何路径。
            peer_device_id TEXT NOT NULL PRIMARY KEY,
            sent_lamport INTEGER NOT NULL DEFAULT 0,
            acked_lamport INTEGER NOT NULL DEFAULT 0,
            last_sync_at INTEGER NULL
        );

        CREATE TABLE IF NOT EXISTS ai_usage (
            id TEXT NOT NULL PRIMARY KEY,
            called_at INTEGER NOT NULL,
            model TEXT NOT NULL,
            -- 第十三轮 schema_diff 量化出的差异：协议里 ai_usage 有这四列，服务端原来没有；
            -- 反过来服务端多了 prompt_tokens/completion_tokens，协议里没有。
            -- 本表在服务端代码里**没有任何读写点**（只有 DDL），所以这是纯 DDL 对齐，不影响任何路径。
            prompt_version TEXT NOT NULL,
            image_hash TEXT NOT NULL,
            ok INTEGER NOT NULL CHECK (ok IN (0, 1)),
            error_code TEXT NULL,
            latency_ms INTEGER NULL
        );
        CREATE INDEX IF NOT EXISTS idx_usage_called ON ai_usage (called_at);
        """;
}
