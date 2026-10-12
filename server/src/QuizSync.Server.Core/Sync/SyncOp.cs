using System.Text.Json;
using System.Text.Json.Nodes;

namespace QuizSync.Server.Core.Sync;

/// <summary>同步实体（`entity` 字段的取值）。</summary>
public static class SyncEntities
{
    public const string Session = "session";
    public const string Question = "question";
    public const string Collection = "collection";
    public const string SessionImage = "session_image";
    public const string Image = "image";
    public const string Device = "device";
    public const string Snapshot = "snapshot";
}

/// <summary>op 类型。</summary>
public static class SyncOpTypes
{
    public const string Upsert = "upsert";
    public const string Delete = "delete";
}

/// <summary>
/// 一条同步 op（`spec/06-sync.md`）。`fields_json` **只含本次真正改动的字段**，
/// 键是数据库列名 —— 字段级 LWW 就是按这些键逐列比较的。
/// </summary>
public sealed record SyncOp(
    string OpId,
    string DeviceId,
    long Lamport,
    string Entity,
    string EntityId,
    string OpType,
    IReadOnlyDictionary<string, JsonNode?> Fields,
    long CreatedAt)
{
    public static SyncOp? FromJson(JsonNode? node)
    {
        if (node is not JsonObject obj)
        {
            return null;
        }

        var fields = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        if (obj["fields_json"] is JsonObject fieldObj)
        {
            foreach (var (key, value) in fieldObj)
            {
                fields[key] = value?.DeepClone();
            }
        }

        return new SyncOp(
            OpId: obj["op_id"]?.ToString() ?? string.Empty,
            DeviceId: obj["device_id"]?.ToString() ?? string.Empty,
            Lamport: obj["lamport"]?.GetValue<long>() ?? 0,
            Entity: obj["entity"]?.ToString() ?? SyncEntities.Snapshot,
            EntityId: obj["entity_id"]?.ToString() ?? string.Empty,
            OpType: obj["op_type"]?.ToString() ?? SyncOpTypes.Upsert,
            Fields: fields,
            CreatedAt: obj["created_at"]?.GetValue<long>() ?? 0);
    }

    public JsonObject ToJson()
    {
        var fields = new JsonObject();
        foreach (var (key, value) in Fields)
        {
            fields[key] = value?.DeepClone();
        }

        return new JsonObject
        {
            ["op_id"] = OpId,
            ["device_id"] = DeviceId,
            ["lamport"] = Lamport,
            ["entity"] = Entity,
            ["entity_id"] = EntityId,
            ["op_type"] = OpType,
            ["fields_json"] = fields,
            ["created_at"] = CreatedAt,
        };
    }

    /// <summary>字段值的字符串形态（列都是 TEXT/INTEGER/REAL，这里统一按 JSON 文本存）。</summary>
    public static string? ScalarText(JsonNode? node) => node switch
    {
        null => null,
        JsonValue value when value.TryGetValue<string>(out var s) => s,
        JsonValue value => value.ToJsonString(),
        _ => node.ToJsonString(),
    };
}

/// <summary>应用一条 op 的结果。</summary>
public enum ApplyResult
{
    /// <summary>已入库（新 op，字段按 LWW 落地）。</summary>
    Applied,

    /// <summary>`op_id` 已存在 —— 重复投递，直接忽略。</summary>
    Duplicate,
}
