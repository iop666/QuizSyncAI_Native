using Microsoft.Data.Sqlite;

namespace QuizSync.Server.Core.Storage;

/// <summary>设备行（对应 `devices` 表）。令牌只存 <see cref="TokenHash"/>。</summary>
public sealed record DeviceRecord(
    string DeviceId,
    string Name,
    string Platform,
    string? TokenHash,
    long PairedAt,
    long? LastSeenAt,
    long? RevokedAt,
    string? AppVersion)
{
    public bool IsRevoked => RevokedAt is not null;
}

/// <summary>
/// `devices` 与 `settings` 两张表的读写。
///
/// 本地 SQLite 是同步 API，这里就写成同步的：**不做假异步**（本地库延迟微秒级，
/// 包一层 Task.Run 只会把线程池当摆设）。
/// </summary>
public sealed class DeviceRepository(QuizSyncDatabase db)
{
    private readonly QuizSyncDatabase _db = db;

    public DeviceRecord? Find(string deviceId)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT device_id, name, platform, token_hash, paired_at, last_seen_at, revoked_at, app_version
            FROM devices WHERE device_id = $id
            """;
        cmd.Parameters.AddWithValue("$id", deviceId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public DeviceRecord? FindByTokenHash(string tokenHash)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT device_id, name, platform, token_hash, paired_at, last_seen_at, revoked_at, app_version
            FROM devices WHERE token_hash = $hash
            """;
        cmd.Parameters.AddWithValue("$hash", tokenHash);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public IReadOnlyList<DeviceRecord> List()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT device_id, name, platform, token_hash, paired_at, last_seen_at, revoked_at, app_version
            FROM devices ORDER BY paired_at
            """;
        using var reader = cmd.ExecuteReader();
        var list = new List<DeviceRecord>();
        while (reader.Read())
        {
            list.Add(Map(reader));
        }

        return list;
    }

    /// <summary>
    /// 配对/重新配对：写设备行。`pairedAt` 由调用方给（**重新配对保留首次配对时间**），
    /// 并**清空 revoked_at**（配对成功即解除吊销，与 v1 行为一致）。
    /// </summary>
    public void Upsert(DeviceRecord device)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO devices (device_id, name, platform, token_hash, paired_at, last_seen_at, revoked_at, app_version)
            VALUES ($id, $name, $platform, $hash, $paired, $seen, NULL, $ver)
            ON CONFLICT(device_id) DO UPDATE SET
                name = excluded.name,
                platform = excluded.platform,
                token_hash = excluded.token_hash,
                last_seen_at = excluded.last_seen_at,
                revoked_at = NULL,
                app_version = excluded.app_version
            """;
        cmd.Parameters.AddWithValue("$id", device.DeviceId);
        cmd.Parameters.AddWithValue("$name", device.Name);
        cmd.Parameters.AddWithValue("$platform", device.Platform);
        cmd.Parameters.AddWithValue("$hash", (object?)device.TokenHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$paired", device.PairedAt);
        cmd.Parameters.AddWithValue("$seen", (object?)device.LastSeenAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ver", (object?)device.AppVersion ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void Touch(string deviceId, long nowMs)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "UPDATE devices SET last_seen_at = $now WHERE device_id = $id";
        cmd.Parameters.AddWithValue("$now", nowMs);
        cmd.Parameters.AddWithValue("$id", deviceId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>吊销。**不校验存在性**：不存在的 id 也是无操作成功（v1 行为，向量 auth 20）。</summary>
    public void Revoke(string deviceId, long nowMs)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "UPDATE devices SET revoked_at = $now WHERE device_id = $id";
        cmd.Parameters.AddWithValue("$now", nowMs);
        cmd.Parameters.AddWithValue("$id", deviceId);
        cmd.ExecuteNonQuery();
    }

    public string? GetSetting(string key)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? null : (string)value;
    }

    public void SetSetting(string key, string value)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO settings (key, value) VALUES ($k, $v)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value
            """;
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    private static DeviceRecord Map(SqliteDataReader r) => new(
        DeviceId: r.GetString(0),
        Name: r.GetString(1),
        Platform: r.GetString(2),
        TokenHash: r.IsDBNull(3) ? null : r.GetString(3),
        PairedAt: r.GetInt64(4),
        LastSeenAt: r.IsDBNull(5) ? null : r.GetInt64(5),
        RevokedAt: r.IsDBNull(6) ? null : r.GetInt64(6),
        AppVersion: r.IsDBNull(7) ? null : r.GetString(7));
}
