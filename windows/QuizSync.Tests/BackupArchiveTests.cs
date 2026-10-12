using System.IO.Compression;
using System.Text;
using Microsoft.Data.Sqlite;
using QuizSync.Data;
using Xunit;

namespace QuizSync.Tests;

/// <summary>
/// 备份与恢复（`BackupArchive`）。
///
/// 这组测试的重点**不是**「能压缩能解压」，而是三条踩出来的保护语义（1.1.0 的实现里都有，
/// 每一条都对应一次真实事故）：
/// ① **旧包兼容**：1.1.0 的包**没有 manifest**，只有 `quizsync.db*` + `images/**`；
/// ② **包里没有库就不许动现有数据**（原实现无条件改名 → 恢复一个只含图片的包会让用户的库消失）；
/// ③ **zip-slip 防护**：条目名里的 `../` 能写到目录之外（构造的包 = 任意文件写入）。
///
/// 还有一条运行期的：**库被占用时不许硬换**（Windows 上 sqlite 打开的库文件不带
/// FILE_SHARE_DELETE，直接改名是 `OS Error 32`，表现为「点了恢复什么都没发生」）→ 退到 `.restore-pending`。
/// </summary>
public sealed class BackupArchiveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"qs-backup-{Guid.NewGuid():N}");

    public BackupArchiveTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清不掉不该让测试变红。
        }
    }

    private string DbPath => Path.Combine(_root, "quizsync.db");

    private string ImagesDir => Path.Combine(_root, "images");

    /// <summary>造一个带标记合集的库；返回标记名，恢复后用它判断「换的是哪个库」。</summary>
    private static string CreateDatabase(string path, string marker)
    {
        using var database = QuizDatabase.OpenOrCreate(path);
        using var command = database.Connection.CreateCommand();
        command.CommandText =
            "INSERT INTO collections (collection_id, name, created_at, updated_at, updated_by, deleted_at) "
            + "VALUES ($id, $name, 1, 1, 'test', NULL);";
        command.Parameters.AddWithValue("$id", marker);
        command.Parameters.AddWithValue("$name", "标记 " + marker);
        command.ExecuteNonQuery();
        return marker;
    }

    private static string ReadMarker(string path)
    {
        using var database = QuizDatabase.OpenExisting(path);
        using var command = database.Connection.CreateCommand();
        command.CommandText = "SELECT collection_id FROM collections ORDER BY rowid LIMIT 1;";
        return command.ExecuteScalar() as string ?? string.Empty;
    }

    /// <summary>
    /// **1.1.0 形状的包**：只有库 + 图片，**没有 manifest**。
    /// 这是「旧备份兼容」的判据 —— 新端读它不能因为找不到清单就拒绝。
    /// </summary>
    private string CreateLegacyZip(string name, string marker, bool includeDatabase = true)
    {
        var zipPath = Path.Combine(_root, name);
        var source = Path.Combine(_root, "legacy-source.db");
        if (includeDatabase)
        {
            if (File.Exists(source))
            {
                File.Delete(source);
            }

            CreateDatabase(source, marker);
        }

        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        if (includeDatabase)
        {
            archive.CreateEntryFromFile(source, "quizsync.db", CompressionLevel.Optimal);
        }

        var imageEntry = archive.CreateEntry("images/photo.jpg", CompressionLevel.Optimal);
        using (var stream = imageEntry.Open())
        {
            stream.Write(Encoding.UTF8.GetBytes("fake-jpeg-bytes"));
        }

        return zipPath;
    }

    // -------------------------------------------------------------------
    // ① 旧包兼容
    // -------------------------------------------------------------------

    [Fact]
    public void Reads_a_legacy_zip_that_has_no_manifest()
    {
        var zip = CreateLegacyZip("legacy.zip", "c-legacy");

        var manifest = BackupArchive.Inspect(zip);

        Assert.True(manifest.HasDatabase);
        Assert.Equal("legacy", manifest.Format);
        Assert.Equal(1, manifest.Images);
        Assert.Null(manifest.CreatedAt); // 旧包没有时间戳 —— 如实留白，不许编
    }

    [Fact]
    public void Restores_a_legacy_zip_and_swaps_the_database()
    {
        CreateDatabase(DbPath, "c-current");
        var zip = CreateLegacyZip("legacy-restore.zip", "c-legacy");

        var outcome = BackupArchive.Restore(zip, DbPath, ImagesDir);

        Assert.True(outcome.Applied);
        Assert.Null(outcome.Reason);
        Assert.Equal("c-legacy", ReadMarker(DbPath));
        Assert.True(File.Exists(Path.Combine(ImagesDir, "photo.jpg")));
    }

    // -------------------------------------------------------------------
    // ② 包里没有库 → 现有数据一个字节都不许动
    // -------------------------------------------------------------------

    [Fact]
    public void A_zip_without_a_database_never_touches_the_current_data()
    {
        CreateDatabase(DbPath, "c-keep-me");
        var zip = CreateLegacyZip("images-only.zip", "c-ignored", includeDatabase: false);

        var outcome = BackupArchive.Restore(zip, DbPath, ImagesDir);

        Assert.False(outcome.Applied);
        Assert.False(outcome.PendingRestart);
        Assert.NotNull(outcome.Reason);
        // 关键判据：库还在、内容还是原来那条。
        Assert.True(File.Exists(DbPath));
        Assert.Equal("c-keep-me", ReadMarker(DbPath));
        // 图片照样补上（那是包里有、而本地缺的东西）。
        Assert.True(File.Exists(Path.Combine(ImagesDir, "photo.jpg")));
    }

    // -------------------------------------------------------------------
    // ③ zip-slip
    // -------------------------------------------------------------------

    [Fact]
    public void Zip_slip_entries_are_dropped()
    {
        var zipPath = Path.Combine(_root, "slip.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            // 恶意条目：解到 images/ 外面去
            var evil = archive.CreateEntry("images/../escaped.txt", CompressionLevel.Optimal);
            using (var stream = evil.Open())
            {
                stream.Write(Encoding.UTF8.GetBytes("pwned"));
            }

            var fine = archive.CreateEntry("images/ok.txt", CompressionLevel.Optimal);
            using (var stream = fine.Open())
            {
                stream.Write(Encoding.UTF8.GetBytes("fine"));
            }
        }

        CreateDatabase(DbPath, "c-current");
        var outcome = BackupArchive.Restore(zipPath, DbPath, ImagesDir);

        // 只补了合法的那一张
        Assert.Equal(1, outcome.Images);
        Assert.True(File.Exists(Path.Combine(ImagesDir, "ok.txt")));
        Assert.False(File.Exists(Path.Combine(_root, "escaped.txt")));
    }

    // -------------------------------------------------------------------
    // 往返：自己写的包自己能读回来
    // -------------------------------------------------------------------

    [Fact]
    public void Round_trips_its_own_backup()
    {
        CreateDatabase(DbPath, "c-original");
        Directory.CreateDirectory(ImagesDir);
        File.WriteAllText(Path.Combine(ImagesDir, "a.jpg"), "jpeg-a");

        var zip = Path.Combine(_root, "backup.zip");
        var created = BackupArchive.Create(DbPath, ImagesDir, zip);
        Assert.True(created.HasDatabase);
        Assert.Equal("v2", created.Format);
        Assert.Equal(1, created.Images);

        // 换掉库里的内容（模拟用户后来改了数据），再恢复回去
        using (var database = QuizDatabase.OpenExisting(DbPath))
        {
            using var command = database.Connection.CreateCommand();
            command.CommandText = "DELETE FROM collections;";
            command.ExecuteNonQuery();
        }

        var outcome = BackupArchive.Restore(zip, DbPath, ImagesDir);

        Assert.True(outcome.Applied);
        Assert.Equal("c-original", ReadMarker(DbPath));
        Assert.True(File.Exists(Path.Combine(ImagesDir, "a.jpg")));
    }

    // -------------------------------------------------------------------
    // 运行期保护：库被占用 → 暂存 → 下次启动换入
    // -------------------------------------------------------------------

    [Fact]
    public void A_locked_database_is_staged_and_applied_at_next_start()
    {
        CreateDatabase(DbPath, "c-current");
        var zip = CreateLegacyZip("locked.zip", "c-staged");

        // 独占打开：模拟「应用正开着」，这时直接换文件必然失败。
        using (var hold = new FileStream(DbPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var outcome = BackupArchive.Restore(zip, DbPath, ImagesDir);
            Assert.False(outcome.Applied);
            Assert.True(outcome.PendingRestart);
            Assert.True(BackupArchive.HasPendingRestore(DbPath));
        }

        // 下一次启动（打开数据库之前）应当把它换入
        Assert.True(BackupArchive.ApplyPendingRestore(DbPath));
        Assert.False(BackupArchive.HasPendingRestore(DbPath));
        Assert.Equal("c-staged", ReadMarker(DbPath));
    }

    [Fact]
    public void Apply_pending_restore_is_a_no_op_when_nothing_is_staged()
    {
        CreateDatabase(DbPath, "c-untouched");

        Assert.False(BackupArchive.HasPendingRestore(DbPath));
        Assert.False(BackupArchive.ApplyPendingRestore(DbPath));
        Assert.Equal("c-untouched", ReadMarker(DbPath));
    }
}
