using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using QuizSync.Server.Core.Storage;

namespace QuizSync.Server.Core.Tasks;

/// <summary>任务行（`tasks` 表；本地协调表，**不参与同步**）。</summary>
public sealed record TaskRecord(
    string TaskId,
    string ImageHash,
    string SourceDevice,
    string Status,
    int Attempts,
    string? ErrorCode,
    string? SessionId,
    long CreatedAt,
    long? StartedAt,
    long? FinishedAt,
    string? PayloadJson);

/// <summary>任务状态机的五个取值（协议冻结，不许加别的）。</summary>
public static class TaskStatus
{
    public const string Queued = "queued";
    public const string Analyzing = "analyzing";
    public const string Done = "done";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

/// <summary>`tasks` 表读写。</summary>
public sealed class TaskRepository(QuizSyncDatabase db)
{
    private readonly QuizSyncDatabase _db = db;

    public TaskRecord? Find(string taskId)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT task_id, image_hash, source_device, status, attempts, error_code, session_id,
                   created_at, started_at, finished_at, payload_json
            FROM tasks WHERE task_id = $id
            """;
        cmd.Parameters.AddWithValue("$id", taskId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    /// <summary>排队中的任务数 —— 队列深度闸门就看它（`maxQueueDepth`，默认 20）。</summary>
    public int QueuedCount()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM tasks WHERE status = 'queued'";
        return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public void Insert(TaskRecord task)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tasks (task_id, image_hash, source_device, status, attempts, error_code, session_id,
                               created_at, started_at, finished_at, payload_json)
            VALUES ($id, $hash, $src, $status, $attempts, $err, $session, $at, $started, $finished, $payload)
            """;
        cmd.Parameters.AddWithValue("$id", task.TaskId);
        cmd.Parameters.AddWithValue("$hash", task.ImageHash);
        cmd.Parameters.AddWithValue("$src", task.SourceDevice);
        cmd.Parameters.AddWithValue("$status", task.Status);
        cmd.Parameters.AddWithValue("$attempts", task.Attempts);
        cmd.Parameters.AddWithValue("$err", (object?)task.ErrorCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$session", (object?)task.SessionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$at", task.CreatedAt);
        cmd.Parameters.AddWithValue("$started", (object?)task.StartedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$finished", (object?)task.FinishedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$payload", (object?)task.PayloadJson ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void SetStatus(string taskId, string status, long? startedAt = null, long? finishedAt = null, string? errorCode = null)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            UPDATE tasks SET status = $status,
                             started_at = COALESCE($started, started_at),
                             finished_at = COALESCE($finished, finished_at),
                             error_code = $err
            WHERE task_id = $id
            """;
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$started", (object?)startedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$finished", (object?)finishedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$err", (object?)errorCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", taskId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>重试：只对 `failed` 生效（v1 行为 —— 非 failed 的任务重试是空操作）。</summary>
    public bool Retry(string taskId)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            UPDATE tasks SET status = 'queued', error_code = NULL
            WHERE task_id = $id AND status = 'failed'
            """;
        cmd.Parameters.AddWithValue("$id", taskId);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>取最早的一条排队任务（队列串行执行：并发恒 1）。</summary>
    public TaskRecord? Dequeue()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT task_id, image_hash, source_device, status, attempts, error_code, session_id,
                   created_at, started_at, finished_at, payload_json
            FROM tasks WHERE status = 'queued' ORDER BY created_at LIMIT 1
            """;
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    private static TaskRecord Map(SqliteDataReader r) => new(
        TaskId: r.GetString(0),
        ImageHash: r.GetString(1),
        SourceDevice: r.GetString(2),
        Status: r.GetString(3),
        Attempts: r.GetInt32(4),
        ErrorCode: r.IsDBNull(5) ? null : r.GetString(5),
        SessionId: r.IsDBNull(6) ? null : r.GetString(6),
        CreatedAt: r.GetInt64(7),
        StartedAt: r.IsDBNull(8) ? null : r.GetInt64(8),
        FinishedAt: r.IsDBNull(9) ? null : r.GetInt64(9),
        PayloadJson: r.IsDBNull(10) ? null : r.GetString(10));
}
