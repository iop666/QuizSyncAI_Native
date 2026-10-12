using Microsoft.Data.Sqlite;
using QuizSync.Data;
using Xunit;

namespace QuizSync.Tests;

/// <summary>
/// <see cref="StartupProbe"/> 的行为钉桩。
///
/// ⚠️ **口径（复核后更正）**：这些**不是「修了个 bug」的回归测试**，是**防御性加固**的钉桩。
/// 复核逐行读过原实现：`try { 开库; Count == 0 ? 门页 : 主界面 } catch { 主界面 }` ——
/// **读异常本来就已经回主界面**，「读失败被当成首次使用」并没有发生过。
///
/// 曾有过的例外现象是另一回事：探针把 `QS_APP_DATA` 指到空目录后
/// <c>AppDataDirectory.Ensure()</c> **新建了一个空库**（能正常打开、查询不抛，就是真的 0 行），
/// 于是进了首次使用门页。**那一路没有异常，本组测试覆盖不到它，加固也防不住它。**
///
/// 这组钉两件事：
/// * 三态判据把「异常」与「空」分开，且读失败**不会**落到首次使用；
/// * <see cref="StartupProbe.Probe"/> **不把异常吞成空结果**（只测纯函数的话，这一条会溜过去）。
/// </summary>
public sealed class StartupProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"qs-probe-{Guid.NewGuid():N}");

    public StartupProbeTests() => Directory.CreateDirectory(_root);

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

    // -------------------------------------------------------------------
    // 纯判据
    // -------------------------------------------------------------------

    /// <summary>读失败 → 主界面。**与旧实现的隐式行为一致**，这里把它显式钉住。</summary>
    [Fact]
    public void Decide_treats_a_failed_read_as_main_not_first_use()
    {
        var landing = StartupProbe.Decide(readSucceeded: false, aliveCollections: 0);

        Assert.Equal(StartupLanding.MainAfterReadFailure, landing);
        Assert.NotEqual(StartupLanding.FirstUse, landing);
    }

    /// <summary>读成功且真的一个都没有 —— 这时才该进首次使用门页。</summary>
    [Fact]
    public void Decide_treats_a_genuine_empty_as_first_use()
    {
        Assert.Equal(StartupLanding.FirstUse, StartupProbe.Decide(readSucceeded: true, aliveCollections: 0));
    }

    /// <summary>读成功且有合集 → 主界面。</summary>
    [Fact]
    public void Decide_treats_a_non_empty_as_main()
    {
        Assert.Equal(StartupLanding.Main, StartupProbe.Decide(readSucceeded: true, aliveCollections: 3));
    }

    // -------------------------------------------------------------------
    // 真读一次 —— 构造「读失败」
    // -------------------------------------------------------------------

    /// <summary>
    /// **构造真的读失败**：把库路径指向一个**目录**。
    /// `QuizDatabase.OpenOrCreate` 会先 `File.Exists(dir)` = false → 走建库 → `SqliteConnection.Open()` 抛。
    /// 钉住「<see cref="StartupProbe.Probe"/> 把失败当失败报出来，而不是吞成空结果」。
    /// </summary>
    [Fact]
    public void Probe_reports_failure_instead_of_landing_on_first_use()
    {
        var directoryAsDatabase = Path.Combine(_root, "这是一个目录不是库");
        Directory.CreateDirectory(directoryAsDatabase);

        var result = StartupProbe.Probe(directoryAsDatabase);

        Assert.NotNull(result.Failure);
        Assert.Equal(StartupLanding.MainAfterReadFailure, result.Landing);
        Assert.NotEqual(StartupLanding.FirstUse, result.Landing);
    }

    /// <summary>真建一个空库（有表结构、没合集）→ 这才是首次使用。</summary>
    [Fact]
    public void Probe_lands_on_first_use_for_a_real_but_empty_database()
    {
        var path = Path.Combine(_root, "empty.db");
        using (QuizDatabase.OpenOrCreate(path))
        {
        }

        var result = StartupProbe.Probe(path);

        Assert.Null(result.Failure);
        Assert.Equal(0, result.AliveCollections);
        Assert.Equal(StartupLanding.FirstUse, result.Landing);
    }

    /// <summary>有活合集 → 主界面；且墓碑不算活（与 <c>CollectionStore.List</c> 同源）。</summary>
    [Fact]
    public void Probe_lands_on_main_when_a_live_collection_exists()
    {
        var path = Path.Combine(_root, "with-data.db");
        using (var database = QuizDatabase.OpenOrCreate(path))
        {
            var store = new LocalStore(database, "windows-local");
            CollectionStore.Create(store, "验收合集", now: 1_790_000_000_000);
        }

        var result = StartupProbe.Probe(path);

        Assert.Null(result.Failure);
        Assert.Equal(1, result.AliveCollections);
        Assert.Equal(StartupLanding.Main, result.Landing);
    }
}
