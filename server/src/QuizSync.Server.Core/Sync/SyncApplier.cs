using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using QuizSync.Server.Core.Storage;

namespace QuizSync.Server.Core.Sync;

/// <summary>
/// 把对端推来的 op 落到本地库（**逐字段 LWW**）。语义对 <c>spec/06-sync.md</c> 与
/// `sync.ndjson`：
///
/// - 比较键是 **(lamport, device_id)** 的字典序；**相等即判负**（不覆盖）；
/// - 每个字段各记一条时钟 `{"l": lamport, "d": deviceId}` 存在 `field_clocks_json`；
/// - `delete` 是「写 `deleted_at`」的一种 upsert，同样受 LWW 约束（墓碑）；
/// - `op_id` 幂等：重复投递直接忽略（**applied 不增**）；
/// - 新行直接按 op 的字段建（缺的列走 DEFAULT）。
///
/// **字段名必须白名单**：`fields_json` 的键来自网络，直接拼进 SQL 就是注入。
/// </summary>
public sealed class SyncApplier(QuizSyncDatabase db)
{
    private static readonly Dictionary<string, (string Table, string IdColumn)> Entities = new(StringComparer.Ordinal)
    {
        [SyncEntities.Session] = ("sessions", "session_id"),
        [SyncEntities.Question] = ("questions", "question_id"),
        [SyncEntities.Collection] = ("collections", "collection_id"),
        [SyncEntities.SessionImage] = ("session_images", "session_image_id"),
    };

    private readonly QuizSyncDatabase _db = db;
    private readonly Dictionary<string, HashSet<string>> _columnCache = new(StringComparer.Ordinal);

    /// <summary>
    /// 某张表**实际存在**的列 —— 唯一来源是**库本身**（`PRAGMA table_info`），不再手写第二份清单。
    ///
    /// 原来这里有一份手写的列白名单，与 `V1Schema` 的 DDL 是**两处维护**：
    /// 第十四轮实测，DDL 已补齐 `ai_model`，这份清单没补，于是字段被**静默丢掉**
    /// （op 收下了、`applied` 照加、库里却是 NULL）。第十轮客户端 `LocalStore` 白名单
    /// 漏 `ai_model` 是**同一个毛病**。本项目的教训：**同一份字段清单散落在两处，
    /// 不一致时静默丢数据、不报错** —— 所以收敛到「从表里读」。
    ///
    /// 仍然防注入：列名取自库、不是取自网络；网络来的字段名只能**匹配**已有列，匹配不上就跳过。
    /// </summary>
    private HashSet<string> AllowedColumns(string table)
    {
        if (_columnCache.TryGetValue(table, out var cached))
        {
            return cached;
        }

        var columns = new HashSet<string>(StringComparer.Ordinal);
        using var command = _db.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\")";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }

        _columnCache[table] = columns;
        return columns;
    }

    public ApplyResult Apply(SyncOp op)
    {
        if (OpExists(op.OpId))
        {
            return ApplyResult.Duplicate;
        }

        InsertOpLog(op);

        if (!Entities.TryGetValue(op.Entity, out var target))
        {
            // 未知实体（含 snapshot）：只记账，不落地。对端靠 `op_id` 幂等，
            // 靠 `lamport` 推水位。
            return ApplyResult.Applied;
        }

        ApplyEntity(op, target);
        return ApplyResult.Applied;
    }

    private bool OpExists(string opId)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sync_ops WHERE op_id = $id LIMIT 1";
        cmd.Parameters.AddWithValue("$id", opId);
        return cmd.ExecuteScalar() is not null;
    }

    private void InsertOpLog(SyncOp op)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO sync_ops (op_id, device_id, lamport, entity, entity_id, op_type, fields_json, created_at)
            VALUES ($id, $dev, $l, $entity, $eid, $type, $fields, $at)
            """;
        cmd.Parameters.AddWithValue("$id", op.OpId);
        cmd.Parameters.AddWithValue("$dev", op.DeviceId);
        cmd.Parameters.AddWithValue("$l", op.Lamport);
        cmd.Parameters.AddWithValue("$entity", op.Entity);
        cmd.Parameters.AddWithValue("$eid", op.EntityId);
        cmd.Parameters.AddWithValue("$type", op.OpType);
        cmd.Parameters.AddWithValue("$fields", op.ToJson()["fields_json"]!.ToJsonString());
        cmd.Parameters.AddWithValue("$at", op.CreatedAt);
        cmd.ExecuteNonQuery();
    }

    private void ApplyEntity(SyncOp op, (string Table, string IdColumn) target)
    {
        var (table, idColumn) = target;
        var allowed = AllowedColumns(table);

        // 1) 读既有行：字段时钟 + 行水位。
        var clocks = new Dictionary<string, (long L, string D)>(StringComparer.Ordinal);
        long? rowLamport = null;
        using (var read = _db.CreateCommand())
        {
            read.CommandText = $"SELECT lamport, field_clocks_json FROM {table} WHERE {idColumn} = $id";
            read.Parameters.AddWithValue("$id", op.EntityId);
            using var reader = read.ExecuteReader();
            if (reader.Read())
            {
                rowLamport = reader.GetInt64(0);
                if (!reader.IsDBNull(1))
                {
                    foreach (var (key, value) in JsonNode.Parse(reader.GetString(1)) as JsonObject ?? [])
                    {
                        if (value is JsonObject clock)
                        {
                            clocks[key] = (
                                clock["l"]?.GetValue<long>() ?? 0,
                                clock["d"]?.ToString() ?? string.Empty);
                        }
                    }
                }
            }
        }

        // 2) 逐字段判胜负（(lamport, device_id) 严格大于才写）。
        var winners = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var (field, value) in op.Fields)
        {
            if (!allowed.Contains(field))
            {
                continue;
            }

            if (clocks.TryGetValue(field, out var clock))
            {
                // (lamport, device_id) 严格大于才写；**相等即判负**（不覆盖）。
                if (Compare(op.Lamport, op.DeviceId, clock.L, clock.D) <= 0)
                {
                    continue;
                }
            }

            winners[field] = value;
            clocks[field] = (op.Lamport, op.DeviceId);
        }

        var newLamport = Math.Max(rowLamport ?? 0, op.Lamport);
        var clocksJson = new JsonObject();
        foreach (var (field, clock) in clocks)
        {
            clocksJson[field] = new JsonObject { ["l"] = clock.L, ["d"] = clock.D };
        }

        if (rowLamport is null)
        {
            // 新行：op 的字段 + 主键（没被 op 覆盖的列走 DEFAULT，但 NOT NULL 且无默认的
            // 列必须由 op 提供 —— 缺了就让 SQLite 报错，别静默塞假值）。
            var names = new List<string> { idColumn };
            var values = new List<object> { op.EntityId };
            foreach (var (field, value) in winners)
            {
                names.Add(field);
                values.Add(Bind(value));
            }

            names.Add("lamport");
            values.Add(newLamport);
            names.Add("field_clocks_json");
            values.Add(clocksJson.ToJsonString());

            using var insert = _db.CreateCommand();
            insert.CommandText =
                $"INSERT INTO {table} ({string.Join(", ", names)}) VALUES ({string.Join(", ", names.Select((_, i) => "$p" + i))})";
            for (var i = 0; i < values.Count; i++)
            {
                insert.Parameters.AddWithValue("$p" + i, values[i]);
            }

            insert.ExecuteNonQuery();
            return;
        }

        // 既有行：只更新赢下的字段 + 时钟 + 行水位。
        var sets = winners.Keys.Select((f, i) => $"{f} = $w{i}").ToList();
        using (var update = _db.CreateCommand())
        {
            var i = 0;
            foreach (var (_, value) in winners)
            {
                update.Parameters.AddWithValue("$w" + i, Bind(value));
                i++;
            }

            update.Parameters.AddWithValue("$lamport", newLamport);
            update.Parameters.AddWithValue("$clocks", clocksJson.ToJsonString());
            update.Parameters.AddWithValue("$id", op.EntityId);
            sets.Add("lamport = $lamport");
            sets.Add("field_clocks_json = $clocks");
            update.CommandText = $"UPDATE {table} SET {string.Join(", ", sets)} WHERE {idColumn} = $id";
            update.ExecuteNonQuery();
        }
    }

    private static int Compare(long lamportA, string deviceA, long lamportB, string deviceB)
    {
        var byLamport = lamportA.CompareTo(lamportB);
        return byLamport != 0 ? byLamport : string.CompareOrdinal(deviceA, deviceB);
    }

    private static object Bind(JsonNode? node)
    {
        if (node is null)
        {
            return DBNull.Value;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var s))
            {
                return s;
            }

            if (value.TryGetValue<long>(out var l))
            {
                return l;
            }

            if (value.TryGetValue<double>(out var d))
            {
                return d;
            }

            if (value.TryGetValue<bool>(out var b))
            {
                return b ? 1 : 0;
            }
        }

        return node.ToJsonString();
    }
}
