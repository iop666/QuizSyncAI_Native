namespace QuizSync.Data;

/// <summary>
/// 本地图片文件（`images.local_path` 那一列对应的磁盘文件）。
///
/// **为什么要单独一个类**：写文件和建库行是两件事，而本仓库已经在这上面栽过一次
/// （M15：服务端上传处理器「写完文件又去查库」，写文件**不会**建 `images` 行，
/// 于是拿到的 `local_path` 恒为 null，手机传过来的图永远没有缩略图）。
/// 所以这里把「落盘 + 建行 + 写 local_path」锁成一个动作，不允许调用方只做一半。
///
/// `local_path` 是**本地专属列**：不进同步 op、也不被远端 op 覆盖，
/// 读写它的入口只有本类和 <see cref="ImagePruner"/>。
/// </summary>
public static class ImageStore
{
    /// <summary>某个 hash 的磁盘路径。约定扩展名 `.jpg`（我们的截图一律编码成 JPEG）。</summary>
    public static string PathFor(string directory, string hash) =>
        Path.Combine(directory, $"{hash}.jpg");

    /// <summary>
    /// 落盘并记 `local_path`，返回文件的绝对路径。
    ///
    /// `images` 行不存在时会**先建行**（`hash` / `size` / `mime` / `created_at` / `uploaded_by`
    /// 都是 NOT NULL）；已存在则只更新 `local_path`（其它列由上传/同步链路负责，这里不覆盖）。
    /// </summary>
    public static string Save(
        QuizDatabase database,
        string directory,
        string hash,
        byte[] jpeg,
        string uploadedBy,
        long now)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        ArgumentNullException.ThrowIfNull(jpeg);

        Directory.CreateDirectory(directory);
        var path = PathFor(directory, hash);
        File.WriteAllBytes(path, jpeg);

        using var command = database.Connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO images (hash, size, mime, local_path, created_at, uploaded_by)
            VALUES ($hash, $size, $mime, $path, $now, $by)
            ON CONFLICT(hash) DO UPDATE SET local_path = excluded.local_path
            """;
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$size", jpeg.Length);
        command.Parameters.AddWithValue("$mime", "image/jpeg");
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$by", uploadedBy);
        command.ExecuteNonQuery();

        return path;
    }
}
