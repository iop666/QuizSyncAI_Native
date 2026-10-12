namespace QuizSync.Data;

/// <summary>库里的一行合集（1.1.0 的「任务合集」）。列名取自协议 `collections` 表。</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "协议里这个实体就叫 collection（表名 collections），RecordedCollection 是「库里的一行合集」，"
        + "不是 .NET 意义上的集合类型；改名会与协议词汇脱节。")]
public sealed record RecordedCollection(
    string CollectionId,
    string Name,
    long CreatedAt,
    long UpdatedAt);

/// <summary>
/// 任务合集的读写（`collections` 表）。
///
/// **为什么单独一个文件，而不是并进 `SessionRecorder`**：`SessionRecorder` 的职责是
/// 「把一次识别落成 sessions + questions」，与合集没有从属关系（会话只是**可以**挂一个合集）；
/// 合集的列清单（name / created_at / updated_at / updated_by / deleted_at）和题目那一套毫无重叠，
/// 放同一处只会让人以为「改题目投影时要顺手改合集」，而这正是本仓库反复踩过的坑
/// （同一份清单散在 N 处，改一处漏一处）。反过来说，**会话的列清单不在这里**：
/// 它只有 `SessionRecorder.QuerySessions` 一份，本类只加一个「合集视角」的入口
/// （<see cref="SessionsOf"/>），调用同一个实现。
///
/// 三条不变量：
/// * 写一律走 `LocalStore.WriteLocal` —— 否则不生成 op，手机端永远拉不到这个合集；
/// * 删除是**墓碑**（写 `deleted_at`），不物理删行，**也不级联删 sessions**：
///   1.1.0 的语义是「删合集不删记录，记录回到『全部记录』」；
/// * 合集与手机的同步还有一层主机侧的「活跃列表镜像」（M18：安卓端只增改不删），
///   那是另一条链路，与本类的本地读写无关。
/// </summary>
public static class CollectionStore
{
    /// <summary>合集列表（新的在前）。墓碑（`deleted_at` 非空）不算 —— 与 Dart 侧 `listCollections` 同序。</summary>
    public static IReadOnlyList<RecordedCollection> List(QuizDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        using var command = database.Connection.CreateCommand();
        command.CommandText =
            """
            SELECT collection_id, name, created_at, updated_at
              FROM collections
             WHERE deleted_at IS NULL
             ORDER BY created_at DESC, rowid DESC
            """;

        var collections = new List<RecordedCollection>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            collections.Add(new RecordedCollection(
                CollectionId: reader.GetString(0),
                Name: reader.GetString(1),
                CreatedAt: reader.GetInt64(2),
                UpdatedAt: reader.GetInt64(3)));
        }

        return collections;
    }

    /// <summary>建一个合集，返回 collection_id（`c-` 前缀，与 `s-` / `q-` 同风格）。</summary>
    public static string Create(LocalStore store, string name, long now)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var collectionId = $"c-{Guid.NewGuid():N}";
        store.WriteLocal(SyncEntities.Collection, collectionId, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = name,
            ["created_at"] = now,
            ["updated_at"] = now,
            ["updated_by"] = store.DeviceId,
        });

        return collectionId;
    }

    /// <summary>
    /// 软删除：写 `deleted_at`（走 `DeleteLocal` → 生成 delete op），**不物理删行、不级联删 sessions**。
    /// 幂等：已经不存在的、或已经是墓碑的合集再删一次**不产生第二条 op**
    /// （墓碑时间被反复往后推会永久阻塞 30 天 GC —— 与 Dart 侧 `deleteCollection` 同一条理由）。
    /// </summary>
    public static void Delete(LocalStore store, string collectionId, long deletedAt)
    {
        ArgumentNullException.ThrowIfNull(store);

        var row = store.Read(SyncEntities.Collection, collectionId);
        if (row is null || row.Value.Values.GetValueOrDefault("deleted_at") is not null)
        {
            return;
        }

        store.DeleteLocal(SyncEntities.Collection, collectionId, deletedAt);
    }

    /// <summary>
    /// 按合集筛选会话（`collectionId == null` = 不过滤）。
    /// 实现就是 <see cref="SessionRecorder.QuerySessions"/> —— 会话投影只有那一份，
    /// 这里只是把「某个合集的记录」这个读法放到合集 API 里，别处再抄一条 SELECT 就又多一份清单。
    /// </summary>
    public static IReadOnlyList<RecordedSession> SessionsOf(
        QuizDatabase database, string? collectionId, int limit = 50) =>
        SessionRecorder.QuerySessions(database, limit, collectionId);

    /// <summary>
    /// 把一条会话挂到某个合集（或取消挂：`collectionId = null`）。
    /// 必须走 `WriteLocal`：`sessions.collection_id` 是要同步的列，直接 UPDATE 就生成不了 op。
    /// 会话不存在时**明确报错**（而不是插出一行只有 collection_id 的残缺记录再被 NOT NULL 挡下）。
    /// </summary>
    public static void SetSessionCollection(LocalStore store, string sessionId, string? collectionId, long now)
    {
        ArgumentNullException.ThrowIfNull(store);

        if (store.Read(SyncEntities.Session, sessionId) is null)
        {
            throw new InvalidOperationException($"会话不存在：{sessionId}（合集归属只能挂在已经落库的会话上）");
        }

        store.WriteLocal(SyncEntities.Session, sessionId, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["collection_id"] = collectionId,
            ["updated_at"] = now,
            ["updated_by"] = store.DeviceId,
        });
    }
}
