using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace QuizSync.Data;

/// <summary>备份包的信息（读包时得到，不落地）。</summary>
/// <param name="HasDatabase">包里有没有 `quizsync.db`。**没有库的包绝不能覆盖现有数据**（见 <see cref="BackupArchive.Restore"/>）。</param>
/// <param name="Format">`legacy`（1.1.0 的形状：zip 里只有库三件套 + `images/**`，**没有 manifest**）或 `v2`（带 `manifest.json`）。</param>
public sealed record BackupManifest(bool HasDatabase, string Format, int Entries, long Bytes, int Images, string? CreatedAt);

/// <summary>恢复的结果。</summary>
/// <param name="Applied">已经换库（调用方需要重启应用加载新库）。</param>
/// <param name="PendingRestart">现库被占用，已暂存为 `.restore-pending`，下次启动换入。</param>
/// <param name="Reason">没做成时的原因（成功时为 null）。给用户看的一句话。</param>
public sealed record RestoreOutcome(bool Applied, bool PendingRestart, int Images, string? Reason);

/// <summary>
/// 备份与恢复（数据库 + 图片目录 → zip；从 zip 还原）。
///
/// **格式与 1.1.0 逐字兼容**（`packages/quizsync_core/lib/util/backup.dart` 是源头）：
/// zip 里是 `quizsync.db` / `quizsync.db-wal` / `quizsync.db-shm` 三个文件 + `images/**`，
/// **没有 manifest** —— 所以「新端要能读旧包」这件事的实质是「别指望包里有清单」。
/// 本实现另外**写**一个 `manifest.json`（旧端会忽略它，因为它只挑自己认识的条目读），
/// 读的时候**允许没有**：没有就当 legacy，这就是旧包的兼容路径。
///
/// 三条保护语义直接照抄 1.1.0（它们都是踩出来的，不是设计洁癖）：
/// ① **包里没有库就不许动现有数据** —— 原实现无条件改名，恢复一个只含图片的包会让用户的库消失；
/// ② **zip-slip 防护** —— 条目名里的 `../` 能把文件写到目录之外（构造的包 = 任意文件写入）；
/// ③ **被占用时暂存** —— Windows 上 sqlite 打开的库文件不带 FILE_SHARE_DELETE，
///    运行期直接改名会 `OS Error 32`（表现为「点了恢复什么都没发生」），所以退到 `.restore-pending`。
/// </summary>
public static class BackupArchive
{
    private const string DatabaseEntry = "quizsync.db";
    private const string ManifestEntry = "manifest.json";
    private const string ImagesPrefix = "images/";

    private static readonly string[] DatabaseSuffixes = ["", "-wal", "-shm"];

    // ===================================================================
    // 备份
    // ===================================================================

    /// <summary>
    /// 生成备份包。写之前先对 WAL 做一次 checkpoint（`TRUNCATE`）：
    /// 直接复制三件套在「正在写」的时刻可能拿到不一致的快照。
    /// </summary>
    public static BackupManifest Create(string dbPath, string imagesDir, string outPath)
    {
        Checkpoint(dbPath);

        var directory = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var entries = 0;
        var bytes = 0L;
        var images = 0;

        using (var archive = ZipFile.Open(outPath, ZipArchiveMode.Create))
        {
            foreach (var suffix in DatabaseSuffixes)
            {
                var source = dbPath + suffix;
                if (!File.Exists(source))
                {
                    continue;
                }

                var entry = archive.CreateEntry(DatabaseEntry + suffix, CompressionLevel.Optimal);
                using var target = entry.Open();
                // `FileShare.ReadWrite`：备份是**只读复制**，不该因为别的进程（或连接池）正开着库就失败。
                using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                input.CopyTo(target);
                entries++;
                bytes += new FileInfo(source).Length;
            }

            if (Directory.Exists(imagesDir))
            {
                var root = Path.GetFullPath(imagesDir);
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                    var entry = archive.CreateEntry(ImagesPrefix + relative, CompressionLevel.Optimal);
                    using var target = entry.Open();
                    using var input = File.OpenRead(file);
                    input.CopyTo(target);
                    entries++;
                    images++;
                    bytes += new FileInfo(file).Length;
                }
            }

            // 清单是**加法**：旧端只挑 `quizsync.db*` 与 `images/` 读，多一个条目它不认识就跳过。
            var manifest = archive.CreateEntry(ManifestEntry, CompressionLevel.Optimal);
            using (var stream = manifest.Open())
            {
                var json = new JsonObject
                {
                    ["format"] = "quizsync-backup",
                    ["version"] = 2,
                    ["created_at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ["has_database"] = File.Exists(dbPath),
                    ["images"] = images,
                };
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.Write(json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
            entries++;
        }

        return new BackupManifest(
            File.Exists(dbPath),
            "v2",
            entries,
            bytes,
            images,
            DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm"));
    }

    /// <summary>读一个包的信息（不落地、不改任何数据）。</summary>
    public static BackupManifest Inspect(string zipPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);

        var hasDatabase = false;
        var images = 0;
        var bytes = 0L;
        string? createdAt = null;
        var format = "legacy";

        foreach (var entry in archive.Entries)
        {
            bytes += entry.Length;
            if (entry.FullName == DatabaseEntry)
            {
                hasDatabase = entry.Length > 0;
            }
            else if (entry.FullName.StartsWith(ImagesPrefix, StringComparison.Ordinal))
            {
                images++;
            }
            else if (entry.FullName == ManifestEntry)
            {
                format = "v2";
                try
                {
                    using var stream = entry.Open();
                    using var reader = new StreamReader(stream);
                    var json = JsonNode.Parse(reader.ReadToEnd()) as JsonObject;
                    var stamp = json?["created_at"]?.GetValue<long>();
                    if (stamp is { } ms)
                    {
                        createdAt = DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime
                            .ToString("yyyy-MM-dd HH:mm");
                    }
                }
                catch (Exception error) when (error is JsonException or IOException or InvalidOperationException)
                {
                    // 清单坏了不影响读包（旧的 legacy 包压根没有它）—— 如实留白，不假装有时间。
                    createdAt = null;
                }
            }
        }

        return new BackupManifest(hasDatabase, format, archive.Entries.Count, bytes, images, createdAt);
    }

    // ===================================================================
    // 恢复
    // ===================================================================

    /// <summary>
    /// 从包恢复。**恢复前会把当前数据留底**（`<db>.pre-restore-<时间戳>`），
    /// 失败也只退到「什么都没改」或「已暂存」，绝不让用户的数据凭空消失。
    /// </summary>
    public static RestoreOutcome Restore(string zipPath, string dbPath, string imagesDir)
    {
        var manifest = Inspect(zipPath);

        // ① 没有库的包：**只补图片，绝不动库**。
        if (!manifest.HasDatabase)
        {
            var copiedOnlyImages = ExtractImages(zipPath, imagesDir);
            return new RestoreOutcome(
                Applied: false,
                PendingRestart: false,
                Images: copiedOnlyImages,
                Reason: "这个包里没有数据库，只补了图片；现有数据没有被改动");
        }

        // ② 先把库三件套落到临时文件，**确认写完了**再换（写一半就换 = 半个库）。
        var staged = dbPath + ".restore";
        foreach (var suffix in DatabaseSuffixes)
        {
            TryDelete(staged + suffix);
        }

        using (var archive = ZipFile.OpenRead(zipPath))
        {
            foreach (var entry in archive.Entries)
            {
                var match = DatabaseSuffixes.FirstOrDefault(s => entry.FullName == DatabaseEntry + s);
                if (match is null)
                {
                    continue;
                }

                var target = staged + match;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
            }
        }

        if (!File.Exists(staged) || new FileInfo(staged).Length == 0)
        {
            foreach (var suffix in DatabaseSuffixes)
            {
                TryDelete(staged + suffix);
            }

            return new RestoreOutcome(false, false, 0, "包里的数据库是空的，什么都没改");
        }

        // ③ 换库。拿不到独占就暂存，等下次启动换入。
        var pending = dbPath + ".restore-pending";
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        try
        {
            foreach (var suffix in DatabaseSuffixes)
            {
                var current = dbPath + suffix;
                if (File.Exists(current))
                {
                    File.Move(current, $"{current}.pre-restore-{now}");
                }
            }

            foreach (var suffix in DatabaseSuffixes)
            {
                var stagedFile = staged + suffix;
                if (File.Exists(stagedFile))
                {
                    File.Move(stagedFile, dbPath + suffix);
                }
            }
        }
        catch (IOException)
        {
            TryDelete(pending);
            File.Move(staged, pending);
            var pendingImages = ExtractImages(zipPath, imagesDir);
            return new RestoreOutcome(
                Applied: false,
                PendingRestart: true,
                Images: pendingImages,
                Reason: "数据库正被占用（应用开着），已暂存；下次启动时自动换入");
        }

        return new RestoreOutcome(true, false, ExtractImages(zipPath, imagesDir), null);
    }

    /// <summary>有没有「暂存的恢复」等着下次启动换入。</summary>
    public static bool HasPendingRestore(string dbPath) =>
        File.Exists(dbPath + ".restore-pending") && new FileInfo(dbPath + ".restore-pending").Length > 0;

    /// <summary>
    /// 启动时（打开数据库**之前**）调用：把暂存的库换入。返回 true = 确实换入了。
    /// 换不掉就放弃本次恢复，**绝不删用户数据**（与 1.1.0 同一取舍）。
    /// </summary>
    public static bool ApplyPendingRestore(string dbPath)
    {
        var pending = dbPath + ".restore-pending";
        if (!File.Exists(pending) || new FileInfo(pending).Length == 0)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var suffix in DatabaseSuffixes)
        {
            var current = dbPath + suffix;
            if (!File.Exists(current))
            {
                continue;
            }

            try
            {
                File.Move(current, $"{current}.pre-restore-{now}");
            }
            catch (IOException)
            {
                return false;
            }
        }

        try
        {
            File.Move(pending, dbPath);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    // ===================================================================
    // 内部
    // ===================================================================

    /// <summary>
    /// 解出 `images/**`。**zip-slip 防护照抄 1.1.0**：条目名里的 `..` / 空段一律跳过，
    /// 规范化之后仍必须在图片目录内 —— 构造的备份包否则就是任意文件写入。
    /// </summary>
    private static int ExtractImages(string zipPath, string imagesDir)
    {
        Directory.CreateDirectory(imagesDir);
        var root = Path.GetFullPath(imagesDir);
        var copied = 0;

        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            if (!entry.FullName.StartsWith(ImagesPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var relative = entry.FullName[ImagesPrefix.Length..];
            var segments = relative.Split('/');
            if (segments.Length == 0
                || segments.Any(s => s.Length == 0 || s == ".." || s == "."))
            {
                continue;
            }

            var target = Path.GetFullPath(Path.Combine(root, Path.Combine(segments)));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
            copied++;
        }

        return copied;
    }

    /// <summary>把 WAL 落进主库文件，让「复制三件套」拿到一致快照。失败就算了（照旧复制）。</summary>
    private static void Checkpoint(string dbPath)
    {
        if (!File.Exists(dbPath))
        {
            return;
        }

        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWrite,
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            command.ExecuteNonQuery();
            // ⚠️ **必须清池**：`Dispose()` 只是把连接还回池子，池里那条仍然持有库文件的句柄 ——
            //    紧接着的复制会以「文件正被另一个进程使用」失败（实测：往返用例直接红）。
            SqliteConnection.ClearPool(connection);
        }
        catch (SqliteException)
        {
            // 库坏了/被锁着：备份仍然照做，只是少了这一步一致性保证。
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 删不掉就留在那儿；后面用 `overwrite: true` 覆盖。
        }
    }
}
