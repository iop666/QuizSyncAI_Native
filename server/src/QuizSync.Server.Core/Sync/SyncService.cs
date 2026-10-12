using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using QuizSync.Server.Core.Storage;

namespace QuizSync.Server.Core.Sync;

/// <summary>推 op 的结果计数（`applied` / `rejected`）。</summary>
public sealed record PushOutcome(int Applied, int Rejected);

/// <summary>拉 op 的一页。</summary>
public sealed record OpsPage(IReadOnlyList<SyncOp> Ops, bool HasMore, long? NextCursor);

/// <summary>
/// op 中继与快照。行为对 `spec/04-http-api.md` §1.3.12–§1.3.14 与 `sync.ndjson`：
///
/// - **归属校验**：`op.device_id` 必须等于调用者，否则计入 `rejected`（不落地）；
///   声称由主机自己产生的 op 被**静默跳过**（applied/rejected 都不计）；
/// - 拉取按 `from_device` + 游标（`since_lamport`，`cursor` 可覆盖它）升序给页，
///   页大小上限 500，`has_more` / `next_cursor` 都按实际页给；
/// - 快照按**会话**分页（`limit` 默认 200 / 上限 1000 + `offset`），
///   题目与页序只带本页会话；图片只给元数据且**不含 `local_path`**。
/// </summary>
public sealed class SyncService(QuizSyncDatabase db, SyncApplier applier, IClock clock)
{
    private readonly QuizSyncDatabase _db = db;
    private readonly SyncApplier _applier = applier;
    private readonly IClock _clock = clock;

    public const int OpsPageSize = 500;

    public PushOutcome Push(string callerDeviceId, JsonArray rawOps, string serverDeviceId)
    {
        var applied = 0;
        var rejected = 0;
        foreach (var raw in rawOps)
        {
            var op = SyncOp.FromJson(raw);
            if (op is null)
            {
                continue;
            }

            // 主机自己的 op 本来就在本地日志里，客户端只是在回推 —— 静默跳过。
            if (string.Equals(op.DeviceId, serverDeviceId, StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.Equals(op.DeviceId, callerDeviceId, StringComparison.Ordinal))
            {
                rejected++;
                continue;
            }

            if (_applier.Apply(op) == ApplyResult.Applied)
            {
                applied++;
            }
        }

        return new PushOutcome(applied, rejected);
    }

    public OpsPage Pull(string fromDevice, long sinceLamport)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT op_id, device_id, lamport, entity, entity_id, op_type, fields_json, created_at
            FROM sync_ops
            WHERE device_id = $dev AND lamport > $since
            ORDER BY lamport ASC, op_id ASC
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$dev", fromDevice);
        cmd.Parameters.AddWithValue("$since", sinceLamport);
        cmd.Parameters.AddWithValue("$limit", OpsPageSize + 1);

        var ops = new List<SyncOp>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var fields = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
                foreach (var (key, value) in JsonNode.Parse(reader.GetString(6)) as JsonObject ?? [])
                {
                    fields[key] = value?.DeepClone();
                }

                ops.Add(new SyncOp(
                    OpId: reader.GetString(0),
                    DeviceId: reader.GetString(1),
                    Lamport: reader.GetInt64(2),
                    Entity: reader.GetString(3),
                    EntityId: reader.GetString(4),
                    OpType: reader.GetString(5),
                    Fields: fields,
                    CreatedAt: reader.GetInt64(7)));
            }
        }

        var hasMore = ops.Count > OpsPageSize;
        var page = hasMore ? ops.Take(OpsPageSize).ToList() : ops;
        return new OpsPage(page, hasMore, page.Count == 0 ? null : page[^1].Lamport);
    }

    public long Watermark()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(lamport), 0) FROM sync_ops";
        return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public JsonObject Snapshot(int limit, int offset)
    {
        var sessionIds = new List<string>();
        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = """
                SELECT session_id FROM sessions WHERE deleted_at IS NULL
                ORDER BY created_at ASC, session_id ASC LIMIT $limit OFFSET $offset
                """;
            cmd.Parameters.AddWithValue("$limit", limit + 1);
            cmd.Parameters.AddWithValue("$offset", offset);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                sessionIds.Add(reader.GetString(0));
            }
        }

        var hasMore = sessionIds.Count > limit;
        var pageIds = hasMore ? sessionIds.Take(limit).ToList() : sessionIds;

        var sessions = new JsonArray();
        var questions = new JsonArray();
        var sessionImages = new JsonArray();
        foreach (var id in pageIds)
        {
            var session = ReadRow("sessions", "session_id", id);
            if (session is not null)
            {
                sessions.Add(session);
            }

            foreach (var q in ReadChildren("questions", "session_id", id))
            {
                questions.Add(q);
            }

            foreach (var p in ReadChildren("session_images", "session_id", id))
            {
                sessionImages.Add(p);
            }
        }

        var images = new JsonArray();
        foreach (var row in ReadAll("images"))
        {
            // `local_path` 是本地专属列：同步载荷里**必须剔除**（sync.ndjson 32）。
            row.Remove("local_path");
            images.Add(row);
        }

        var collections = new JsonArray();
        foreach (var row in ReadAll("collections"))
        {
            if (row["deleted_at"] is null)
            {
                collections.Add(row);
            }
        }

        var devices = new JsonArray();
        foreach (var d in ReadAll("devices"))
        {
            d.Remove("token_hash"); // 令牌哈希绝不外传。
            devices.Add(d);
        }

        return new JsonObject
        {
            ["sessions"] = sessions,
            ["questions"] = questions,
            ["session_images"] = sessionImages,
            ["collections"] = collections,
            ["images"] = images,
            ["devices"] = devices,
            ["watermark"] = Watermark(),
            ["has_more"] = hasMore,
            ["next_offset"] = hasMore ? offset + pageIds.Count : null,
        };
    }

    private JsonObject? ReadRow(string table, string idColumn, string id)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = $"SELECT * FROM {table} WHERE {idColumn} = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? RowToJson(reader) : null;
    }

    private List<JsonObject> ReadChildren(string table, string parentColumn, string parentId)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = $"SELECT * FROM {table} WHERE {parentColumn} = $id AND deleted_at IS NULL";
        cmd.Parameters.AddWithValue("$id", parentId);
        using var reader = cmd.ExecuteReader();
        var rows = new List<JsonObject>();
        while (reader.Read())
        {
            rows.Add(RowToJson(reader));
        }

        return rows;
    }

    private List<JsonObject> ReadAll(string table)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = $"SELECT * FROM {table}";
        using var reader = cmd.ExecuteReader();
        var rows = new List<JsonObject>();
        while (reader.Read())
        {
            rows.Add(RowToJson(reader));
        }

        return rows;
    }

    private static JsonObject RowToJson(SqliteDataReader reader)
    {
        var obj = new JsonObject();
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var name = reader.GetName(i);
            if (reader.IsDBNull(i))
            {
                obj[name] = null;
                continue;
            }

            var value = reader.GetValue(i);
            obj[name] = value switch
            {
                long l => JsonValue.Create(l),
                int n => JsonValue.Create((long)n),
                double d => JsonValue.Create(d),
                byte[] bytes => JsonValue.Create(Convert.ToBase64String(bytes)),
                _ => JsonValue.Create(value.ToString()),
            };
        }

        return obj;
    }
}
