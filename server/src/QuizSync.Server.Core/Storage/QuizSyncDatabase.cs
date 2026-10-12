using Microsoft.Data.Sqlite;

namespace QuizSync.Server.Core.Storage;

/// <summary>
/// 本地库（SQLite）。表结构与 <c>data-model.md</c> / <c>schema/schema-v1.sql</c> 一致：
/// **列名就是同步 op 的字段名**（`fields_json` 的键），所以这里不许改名。
///
/// 与 v1 一致的口径：
/// - 时间戳一律 UTC 毫秒整数；
/// - 软删除用可空的 `deleted_at`；
/// - 参与同步的表额外有 `lamport` / `field_clocks_json` / `updated_by`；
/// - **本地专属列不进同步**：`images.local_path`、`devices.token_hash`、
///   `settings.*`、`peer_state.*`、`tasks.*`。
/// </summary>
public sealed class QuizSyncDatabase : IDisposable
{
    private readonly SqliteConnection _connection;

    /// <summary>建/放命令的**连接级互斥**（见 <see cref="CreateCommand"/>）。</summary>
    private readonly Lock _commandsGate = new();

    private QuizSyncDatabase(SqliteConnection connection) => _connection = connection;

    /// <summary>打开（或新建）库。`path` 为 <c>:memory:</c> 时是内存库（测试用）。</summary>
    public static QuizSyncDatabase Open(string path)
    {
        // 注意：不能用裸的 `:memory:` + shared cache —— 那样**同一个进程里所有**
        // 内存库都是同一份数据，测试之间会互相污染（实测：前一个用例配过对的设备
        // 会让后一个用例的首次配对变成 409）。给每个内存库一个唯一名字。
        var connectionString = path == ":memory:"
            ? $"Data Source=file:qs{Guid.NewGuid():N}?mode=memory&cache=shared"
            : new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            }.ToString();

        var connection = new SqliteConnection(connectionString);
        connection.Open();
        var db = new QuizSyncDatabase(connection);
        db.Execute("PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;");
        db.CreateSchema();
        return db;
    }

    /// <summary>
    /// 底层连接。**只给「不是建命令」的用途**（事务、PRAGMA、测试里的原生 SQL 检查）；
    /// 建命令一律走 <see cref="CreateCommand"/>，理由见它的注释。
    /// </summary>
    public SqliteConnection Connection => _connection;

    /// <summary>
    /// 建一条命令（**唯一正确的建法**）。
    ///
    /// 为什么不能直接用 `Connection.CreateCommand()`：Microsoft.Data.Sqlite 的 `SqliteConnection`
    /// 内部用一个 `List&lt;SqliteCommand&gt;` 记账，`SqliteCommand` 的**构造与 `Dispose` 都会改那个列表**，
    /// 而 `RemoveCommand` 是「先 IndexOf 再 RemoveAt」——**这套记账不是线程安全的**。
    /// 本实现全进程共用一条连接（Kestrel 的请求线程 + 任务队列线程 + WS 读循环 + Provider 派发），
    /// 两个线程同时建/放命令时实测会抛：
    /// <c>ArgumentOutOfRangeException: Index was out of range ... at SqliteConnection.RemoveCommand</c>，
    /// 表现是**随机某个请求 500**（全量测试 5 次里能踩中 1–2 次），且与业务逻辑毫无关系。
    ///
    /// 这里只把**记账**圈进锁里（建 + 释放），执行阶段不互斥：SQLite 本身支持一条连接上
    /// 有多个活动语句，真正需要串行的只有那本列表。
    /// </summary>
    public SqliteCommand CreateCommand()
    {
        lock (_commandsGate)
        {
            return new GatedCommand(_connection, _commandsGate);
        }
    }

    private void Execute(string sql)
    {
        using var cmd = CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private void CreateSchema() => Execute(V1Schema.Ddl);

    public void Dispose() => _connection.Dispose();

    /// <summary>把「从连接的记账表里注销自己」也圈进同一把锁的命令（<see cref="CreateCommand"/>）。</summary>
    private sealed class GatedCommand(SqliteConnection connection, Lock gate) : SqliteCommand(null, connection)
    {
        protected override void Dispose(bool disposing)
        {
            lock (gate)
            {
                base.Dispose(disposing);
            }
        }
    }
}
