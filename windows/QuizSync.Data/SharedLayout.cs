namespace QuizSync.Data;

/// <summary>
/// 桌面端与主机服务端的**共享数据布局**：`%LOCALAPPDATA%\QuizSyncAI`。
///
/// **为什么这一层必须在下游共用的项目里**（本轮从 `QuizSync.App` 搬下来）：
/// 2.0 里同时有**三个**进程要按同一套约定找文件 ——
/// Windows 界面（`QuizSync.App`）、无界面的 Provider（`QuizSync.Provider.Cli`）、
/// 主机服务端（`QuizSyncServer`，独立仓库）。各自抄一份的后果上一轮已经发生过一次：
/// 界面用 `AppContext.BaseDirectory/userdata`、服务端用共享目录，于是「显示配对码 / 推送」
/// 整条链对真实用户是**断的**，而我当时的验证全靠 `QS_SERVER_DATA` 把它们接上
/// —— 那是开发者的私有手段，不是用户能做的事。
///
/// 布局：
///   `server\`  ← **主机服务端**的库、`control.token`（服务端自己还有 images/、日志）
///   `app\`     ← 桌面应用与 Provider 的本地库、`ai.json`、`device.token`
/// 两个库的文件名都叫 `quizsync.db` 但**用途不同**（一个是被同步的本地权威库，
/// 一个是主机的中转库），绝不能混指。
/// </summary>
public static class SharedLayout
{
    /// <summary>共享根：`%LOCALAPPDATA%\QuizSyncAI`。</summary>
    public static string Root
    {
        get
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local, "QuizSyncAI");
        }
    }

    /// <summary>主机服务端的数据目录（库 + `control.token` + `images/`）。</summary>
    public static string ServerDirectory => Path.Combine(Root, "server");

    /// <summary>桌面应用 / Provider 的数据目录（本地库 + `ai.json` + `device.token` + 水位）。</summary>
    public static string AppDirectory => Path.Combine(Root, "app");
}

/// <summary>
/// **应用自己的**数据目录（保证存在）—— 与「主机服务端数据目录」是两件事。
///
/// 为什么必须分开：`quizsync.db` 这个名字在两边都一样，但**服务端库与客户端库不是一回事**
/// （服务端多 `devices`/`tasks` 这类中转表，客户端有 `questions`/`sessions` 等被同步的表）。
/// 指错的那次报错是 `no such column: prompt_version` —— 因为服务端库里压根没有 `analysis_cache`。
///
/// 服务端目录**只用来读 `control.token`**（配对），本地库一律走这里。
/// </summary>
public static class AppDataDirectory
{
    /// <summary>显式指定应用数据目录（脚本与验证用）。与主机侧的 `QS_SERVER_DATA` 同一约定。</summary>
    public const string OverrideVariable = "QS_APP_DATA";

    /// <summary>本地库目录（保证存在）。</summary>
    public static string Ensure()
    {
        var overridden = Environment.GetEnvironmentVariable(OverrideVariable);
        var directory = string.IsNullOrWhiteSpace(overridden) ? SharedLayout.AppDirectory : overridden;
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>本地库文件路径。</summary>
    public static string DatabasePath() => Path.Combine(Ensure(), "quizsync.db");

    /// <summary>本地图片目录（缩略图 / 原图）。对应 `images.local_path` 指向的文件。</summary>
    public static string ImagesDirectory() => Path.Combine(Ensure(), "images");

    /// <summary>导出目录（1.1.0 的「导出合集」默认落在这里）。</summary>
    public static string ExportsDirectory() => Path.Combine(Ensure(), "exports");

    /// <summary>备份目录（「一键备份」默认落在这里；与 1.1.0 的 `backups/` 同名）。</summary>
    public static string BackupsDirectory() => Path.Combine(Ensure(), "backups");
}

/// <summary>
/// 找**主机服务端**的数据目录（要读它的 `control.token` 才能走本机控制面）。
///
/// 解析顺序（找到第一个含 `control.token` 的目录就算命中）：
/// ① 环境变量 `QS_SERVER_DATA`（显式指定，脚本与验证用）；
/// ② 共享目录 `<root>\server`（正常运行时的位置，服务端 CLI 默认也写这里）；
/// ③ 老位置 `AppContext.BaseDirectory/userdata`（开发时直接在 exe 旁边跑服务端）。
/// </summary>
public static class HostDataDirectory
{
    public const string OverrideVariable = "QS_SERVER_DATA";

    /// <summary>找过的所有候选目录（找不到时报给用户看，不给一句「失败了」了事）。</summary>
    public static IReadOnlyList<string> Candidates()
    {
        var list = new List<string>();
        var fromEnvironment = Environment.GetEnvironmentVariable(OverrideVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            list.Add(fromEnvironment);
        }

        list.Add(SharedLayout.ServerDirectory);
        list.Add(Path.Combine(AppContext.BaseDirectory, "userdata"));
        return list;
    }

    /// <summary>返回第一个含 `control.token` 的候选目录；都没有则 null。</summary>
    public static string? Find()
    {
        foreach (var candidate in Candidates())
        {
            if (File.Exists(Path.Combine(candidate, "control.token")))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>找不到时给用户看的话：把找过的目录与可操作的做法都写出来。</summary>
    public static string ExplainMissing()
    {
        var looked = string.Join("、", Candidates());
        return $"没找到主机服务端的数据目录（找过：{looked}）。"
             + $"请先启动主机服务端（quizsync-server run），或用环境变量 {OverrideVariable} 指向它的数据目录。";
    }

    /// <summary>
    /// 起主机时该**写**哪个目录：显式覆盖优先，否则共享目录。
    ///
    /// 与 <see cref="Find"/> 的区别必须刻在心上：那个回答「哪里已经有一份」（要求已经存在
    /// `control.token`），这个回答「没有就写这里」。**启动主机时两者必须一致** ——
    /// 否则界面读的是 A、主机写的是 B，表现就是「主机明明起来了，配对码永远读不到」
    /// （第 167 轮把主机内嵌进应用后，这条路径变成了默认路径，所以补上这个入口）。
    /// </summary>
    public static string ForStarting()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable(OverrideVariable);
        return string.IsNullOrWhiteSpace(fromEnvironment) ? SharedLayout.ServerDirectory : fromEnvironment;
    }
}
