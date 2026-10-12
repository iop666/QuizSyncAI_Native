using Microsoft.Data.Sqlite;

namespace QuizSync.Server.Core.Storage;

/// <summary>图片元数据（对应 `images` 表；`local_path` 是本地专属列，不进同步）。</summary>
public sealed record ImageRecord(
    string Hash, long Size, string Mime, int? Width, int? Height, string? LocalPath, long CreatedAt, string? UploadedBy);

/// <summary>`images` 表读写。</summary>
public sealed class ImageRepository(QuizSyncDatabase db)
{
    private readonly QuizSyncDatabase _db = db;

    public ImageRecord? Find(string hash)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT hash, size, mime, width, height, local_path, created_at, uploaded_by
            FROM images WHERE hash = $h
            """;
        cmd.Parameters.AddWithValue("$h", hash);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new ImageRecord(
            Hash: reader.GetString(0),
            Size: reader.GetInt64(1),
            Mime: reader.GetString(2),
            Width: reader.IsDBNull(3) ? null : reader.GetInt32(3),
            Height: reader.IsDBNull(4) ? null : reader.GetInt32(4),
            LocalPath: reader.IsDBNull(5) ? null : reader.GetString(5),
            CreatedAt: reader.GetInt64(6),
            UploadedBy: reader.IsDBNull(7) ? null : reader.GetString(7));
    }

    /// <summary>
    /// 落库。**`created_at` 沿用首次上传的时间**（同字节重传不刷新），
    /// 宽高也沿用既有值（本实现不解码图片，传 null 会把历史值抹掉）。
    /// </summary>
    public void Upsert(ImageRecord image)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO images (hash, size, mime, width, height, local_path, created_at, uploaded_by)
            VALUES ($h, $size, $mime, $w, $ht, $path, $at, $by)
            ON CONFLICT(hash) DO UPDATE SET
                size = excluded.size,
                mime = excluded.mime,
                width = COALESCE(excluded.width, images.width),
                height = COALESCE(excluded.height, images.height),
                local_path = COALESCE(excluded.local_path, images.local_path),
                uploaded_by = excluded.uploaded_by
            """;
        cmd.Parameters.AddWithValue("$h", image.Hash);
        cmd.Parameters.AddWithValue("$size", image.Size);
        cmd.Parameters.AddWithValue("$mime", image.Mime);
        cmd.Parameters.AddWithValue("$w", (object?)image.Width ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ht", (object?)image.Height ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$path", (object?)image.LocalPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$at", image.CreatedAt);
        cmd.Parameters.AddWithValue("$by", (object?)image.UploadedBy ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
}
