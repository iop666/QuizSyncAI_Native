using System.Globalization;
using QuizSync.Data;
using Xunit;

namespace QuizSync.Tests;

/// <summary>
/// 任务合集（1.1.0）：建 / 列 / **软删** / 按合集筛选会话。
///
/// 1.1.0 的语义是「**删合集不删记录**，记录回到『全部记录』」—— 所以删除用例除了断言
/// 合集从列表里消失，还要断言 `sessions` 行仍然在、`deleted_at` 仍然为空。
/// 所有写都走 `LocalStore.WriteLocal`（要生成 op，否则手机端拉不到）。
/// </summary>
public sealed class CollectionStoreTests
{
    private static (LocalStore Store, QuizDatabase Database) NewStore(string deviceId = "windows-local")
    {
        var database = QuizDatabase.CreateFromProtocolSchema(":memory:");
        return (new LocalStore(database, deviceId), database);
    }

    private static bool CollectionRowExists(QuizDatabase database, string collectionId)
    {
        using var command = database.Connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM collections WHERE collection_id = $id";
        command.Parameters.AddWithValue("$id", collectionId);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    private static bool SessionRowExists(QuizDatabase database, string sessionId)
    {
        using var command = database.Connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sessions WHERE session_id = $id";
        command.Parameters.AddWithValue("$id", sessionId);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    private static long? CollectionDeletedAt(QuizDatabase database, string collectionId)
    {
        using var command = database.Connection.CreateCommand();
        command.CommandText = "SELECT deleted_at FROM collections WHERE collection_id = $id";
        command.Parameters.AddWithValue("$id", collectionId);
        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static long? SessionDeletedAt(QuizDatabase database, string sessionId)
    {
        using var command = database.Connection.CreateCommand();
        command.CommandText = "SELECT deleted_at FROM sessions WHERE session_id = $id";
        command.Parameters.AddWithValue("$id", sessionId);
        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    [Fact]
    public void Create_returns_an_id_that_the_list_shows()
    {
        var (store, database) = NewStore();

        var collectionId = CollectionStore.Create(store, "期末复习", now: 1000);

        var collection = Assert.Single(CollectionStore.List(database));
        Assert.Equal(collectionId, collection.CollectionId);
        Assert.Equal("期末复习", collection.Name);
        Assert.Equal(1000L, collection.CreatedAt);
        Assert.Equal(1000L, collection.UpdatedAt);

        // 建合集也要生成 op：手机端就是靠它看到主机的新合集的。
        var op = store.LocalOpsSince(0).Single(o => o.Entity == SyncEntities.Collection);
        Assert.Equal(SyncOpTypes.Upsert, op.OpType);
        Assert.Equal(collectionId, op.EntityId);
        Assert.Equal("期末复习", op.Fields["name"]?.ToString());
        Assert.Equal("windows-local", op.Fields["updated_by"]?.ToString());

        // 空名字建出来只会在界面上变成一个没法认的条目，数据层直接挡住。
        Assert.Throws<ArgumentException>(() => CollectionStore.Create(store, "   ", now: 1001));
    }

    [Fact]
    public void List_is_newest_first()
    {
        var (store, database) = NewStore();

        CollectionStore.Create(store, "旧合集", now: 1000);
        CollectionStore.Create(store, "新合集", now: 2000);

        Assert.Equal(["新合集", "旧合集"], CollectionStore.List(database).Select(c => c.Name));
    }

    [Fact]
    public void Soft_delete_hides_the_collection_but_keeps_its_sessions()
    {
        var (store, database) = NewStore();
        var collectionId = CollectionStore.Create(store, "期末复习", now: 1000);
        var sessionId = SessionRecorder.Record(
            store, "sha256:a", "windows-local", [], now: 1100, collectionId: collectionId);

        Assert.Single(CollectionStore.List(database));
        Assert.Single(CollectionStore.SessionsOf(database, collectionId));

        CollectionStore.Delete(store, collectionId, deletedAt: 2000);

        // ① 合集从列表里消失；
        Assert.Empty(CollectionStore.List(database));
        // ② 但**不物理删行**（否则同步语义里的墓碑与 30 天 GC 就无从谈起）；
        Assert.True(CollectionRowExists(database, collectionId));
        Assert.Equal(2000L, CollectionDeletedAt(database, collectionId));
        // ③ 记录没被级联删，仍然在「全部记录」里（1.1.0：删合集不删记录）。
        Assert.True(SessionRowExists(database, sessionId));
        Assert.Null(SessionDeletedAt(database, sessionId));
        Assert.Equal(sessionId, Assert.Single(SessionRecorder.RecentSessions(database)).SessionId);

        // 删除同样要生成 delete op（手机端靠它看到合集没了）。
        var op = store.LocalOpsSince(0).Single(o => o.Entity == SyncEntities.Collection && o.OpType == SyncOpTypes.Delete);
        Assert.Equal(collectionId, op.EntityId);
        Assert.Equal(2000L, op.Fields["deleted_at"]);
    }

    [Fact]
    public void Delete_is_idempotent_and_a_missing_collection_is_not_created_by_it()
    {
        var (store, database) = NewStore();
        var collectionId = CollectionStore.Create(store, "合集", now: 1000);
        CollectionStore.Delete(store, collectionId, deletedAt: 2000);
        var opsAfterFirstDelete = store.LocalOpsSince(0).Count;

        // 再删一次：不产生第二条 op，墓碑时间也不许往后推
        //（反复推墓碑会永久阻塞 30 天 GC —— 与 Dart 侧 `deleteCollection` 同一条理由）。
        CollectionStore.Delete(store, collectionId, deletedAt: 3000);

        Assert.Equal(opsAfterFirstDelete, store.LocalOpsSince(0).Count);
        Assert.Equal(2000L, CollectionDeletedAt(database, collectionId));

        // 删一个不存在的 id：什么都不做，更不要插出一行只有 deleted_at 的残缺记录。
        CollectionStore.Delete(store, "c-nope", deletedAt: 4000);
        Assert.False(CollectionRowExists(database, "c-nope"));
        Assert.Equal(opsAfterFirstDelete, store.LocalOpsSince(0).Count);
    }

    [Fact]
    public void Sessions_are_filtered_by_collection()
    {
        var (store, database) = NewStore();
        var first = CollectionStore.Create(store, "期末", now: 1);
        var second = CollectionStore.Create(store, "期中", now: 2);
        var inFirst = SessionRecorder.Record(store, "sha256:1", "windows-local", [], now: 10, collectionId: first);
        var inSecond = SessionRecorder.Record(store, "sha256:2", "windows-local", [], now: 11, collectionId: second);
        var loose = SessionRecorder.Record(store, "sha256:3", "windows-local", [], now: 12);

        // 命中：只返回该合集的会话。
        Assert.Equal([inFirst], CollectionStore.SessionsOf(database, first).Select(s => s.SessionId));
        Assert.Equal([inSecond], CollectionStore.SessionsOf(database, second).Select(s => s.SessionId));
        // 不命中：别的合集的、以及未分类（collection_id 为 NULL）的都不算。
        Assert.DoesNotContain(loose, CollectionStore.SessionsOf(database, first).Select(s => s.SessionId));
        Assert.Empty(CollectionStore.SessionsOf(database, "c-不存在"));
        // 不过滤（collectionId == null）：三条都在。
        Assert.Equal(3, CollectionStore.SessionsOf(database, null).Count);
    }

    [Fact]
    public void SetSessionCollection_moves_a_session_and_generates_an_op()
    {
        var (store, database) = NewStore();
        var collectionId = CollectionStore.Create(store, "合集", now: 1);
        var sessionId = SessionRecorder.Record(store, "sha256:x", "windows-local", [], now: 2);
        var opsBefore = store.LocalOpsSince(0).Count;

        CollectionStore.SetSessionCollection(store, sessionId, collectionId, now: 3);

        Assert.Equal([sessionId], CollectionStore.SessionsOf(database, collectionId).Select(s => s.SessionId));

        // 直接 UPDATE 是不会生成 op 的 —— 手机端就永远看不到这条记录换了合集。
        var op = store.LocalOpsSince(0).Skip(opsBefore).Single();
        Assert.Equal(SyncEntities.Session, op.Entity);
        Assert.Equal(sessionId, op.EntityId);
        Assert.Equal(collectionId, op.Fields["collection_id"]?.ToString());

        // 会话不存在 → 明确报错，而不是插出一行残缺记录再被 NOT NULL 挡下。
        Assert.Throws<InvalidOperationException>(
            () => CollectionStore.SetSessionCollection(store, "s-不存在", collectionId, now: 4));
    }
}
