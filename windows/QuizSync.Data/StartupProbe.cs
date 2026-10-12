namespace QuizSync.Data;

/// <summary>
/// 启动时**该落在哪一页**。三态，而不是布尔。
///
/// ⚠️ **口径（复核后更正，别再读成「修了个 bug」）**：这是一次**防御性加固**，不是缺陷修复。
/// 原来的 `MainWindow.StartPage()` 写成
/// `try { 开库; Count == 0 ? 门页 : 主界面 } catch { 主界面 }` ——
/// **读异常本来就已经回主界面**，所以「读失败被判成首次使用」这件事**并没有发生过**。
/// 曾经据此写的「原 bug」叙事是错的，已撤回。
///
/// 那一次「启动落在门页」的真实机制（与读失败无关）：探针把
/// <see cref="AppDataDirectory.OverrideVariable"/> 指到一个空目录，
/// <see cref="AppDataDirectory.Ensure"/> 便在那里**新建了一个空库** ——
/// 库能正常打开、查询也不抛，就是**真的 0 行**。
/// **所以本次加固并不能防住那一次**（那一路没有异常可判），别把它当成那个现象的解法。
///
/// 那它买到的是什么：把「异常」与「空」**显式分开**、读失败时**留一行日志**
///（原来的 `catch` 是静默的，出事时日志里什么都没有、只能靠猜），
/// 以及**两处调用点共用同一判据**（原来各写一遍 = 改一处漏一处）。
/// </summary>
public enum StartupLanding
{
    /// <summary>确实一个活着的合集都没有 → 首次使用门页（引导新建）。</summary>
    FirstUse,

    /// <summary>有活着的合集 → 正常进主界面。</summary>
    Main,

    /// <summary>读不出来（异常）→ 进主界面，**并且调用方必须留一行日志**。绝不进首次使用门页。</summary>
    MainAfterReadFailure,
}

/// <summary>一次启动探测的结果：落点 + 读到的活合集数 + 失败原因（成功时为 <c>null</c>）。</summary>
/// <param name="Landing">该落在哪一页。</param>
/// <param name="AliveCollections">读到的活合集数；读失败时为 0，**但那时它没有意义**，别拿它判断。</param>
/// <param name="Failure">读失败的原因；成功时为 <c>null</c>。</param>
public readonly record struct StartupProbeResult(
    StartupLanding Landing,
    int AliveCollections,
    Exception? Failure);

/// <summary>
/// 启动探测：读一次本地库，判断该进首次使用门页还是主界面。
///
/// **为什么放在 <c>QuizSync.Data</c> 而不是 <c>QuizSync.App</c>**：
/// 判据必须能被单元测试钉住，而 `QuizSync.Tests` **不引用** `QuizSync.App`
/// （那是个 WinUI exe，不是测试项目的依赖 —— `tools/verify_windows_ui.ps1` 的注释里记着
/// 「`dotnet test` 不构建 `QuizSync.App`」，所以 App 里的编译错误会溜过测试）。
/// 放在共用项目里，判据才有测试兜底。
///
/// **日志不在这里记**：`CrashLog` 在 App 层，Data 层不认识它。
/// <see cref="Probe"/> 把异常原样放进 <see cref="StartupProbeResult.Failure"/>，由调用方落日志。
/// </summary>
public static class StartupProbe
{
    /// <summary>
    /// 纯判据：读成功且一个都没有 = 首次使用；读成功且有 = 主界面；**读失败 = 主界面（不是首次使用）**。
    /// 第三条与旧实现的**行为一致**（旧代码的 <c>catch</c> 也回主界面），
    /// 这里只是把它从「隐式」变成「显式」，好让它有测试兜底、也好让调用方记得记日志。
    /// </summary>
    public static StartupLanding Decide(bool readSucceeded, int aliveCollections)
    {
        if (!readSucceeded)
        {
            return StartupLanding.MainAfterReadFailure;
        }

        return aliveCollections == 0 ? StartupLanding.FirstUse : StartupLanding.Main;
    }

    /// <summary>
    /// 真去读一次。<paramref name="databasePath"/> 打不开或查询抛了都**不往外抛** ——
    /// 启动路径上抛异常只会变成「双击没反应」，所以把失败作为结果返回。
    /// </summary>
    public static StartupProbeResult Probe(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        try
        {
            using var database = QuizDatabase.OpenOrCreate(databasePath);
            var alive = CollectionStore.List(database).Count;
            return new StartupProbeResult(Decide(readSucceeded: true, alive), alive, null);
        }
        catch (Exception error)
        {
            return new StartupProbeResult(StartupLanding.MainAfterReadFailure, 0, error);
        }
    }
}
