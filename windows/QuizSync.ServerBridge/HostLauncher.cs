using System.Diagnostics;

namespace QuizSync.ServerBridge;

/// <summary>拉主机的方式。</summary>
public enum HostLaunchMode
{
    /// <summary>
    /// 默认：探活 → 没有就**在本进程内嵌**起一个（一个应用、一次更新就够了）；
    /// 内嵌失败才退回独立进程，并把两条原因都报出来。
    /// </summary>
    Auto,

    /// <summary>
    /// 只允许内嵌。测试与「应用自己就是主机」的调用方用它 —— 内嵌失败必须原样报出来，
    /// 不许悄悄退化成另一种方式（那样测试就测不到它以为在测的东西）。
    /// </summary>
    Embedded,

    /// <summary>
    /// 只允许独立进程（第 167 轮之前的行为）。
    /// 给「客户端退出后手机还要能连」的场景用，也给进程路径自己的测试用。
    /// </summary>
    Standalone,
}

/// <summary>拉起主机的结局。**每一种都要能对用户说清楚**，不许"点了没反应"。</summary>
public enum HostLaunchStatus
{
    /// <summary>本来就有主机在跑（探活命中）—— 不需要拉起。</summary>
    AlreadyRunning,

    /// <summary>这次是我们拉起来的，并且已经就绪。</summary>
    Started,

    /// <summary>
    /// 这次是**在本应用进程内**把宿主起起来的（第 167 轮起的默认路径）。
    /// 与 <see cref="Started"/> 分开报：界面上「多了一个进程」和「本应用自己就是主机」
    /// 是两件不同的事，混成一个状态就没法说清楚「关掉应用手机还连不连得上」。
    /// </summary>
    Embedded,

    /// <summary>找不到主机程序（没发布过 / 路径不对）。**不猜、不静默**，把找过的路径一并报出来。</summary>
    NotFound,

    /// <summary>起来了但没在超时内就绪。</summary>
    Timeout,

    /// <summary>启动过程本身就失败了（权限、端口全占、内嵌抛异常…）。</summary>
    Failed,
}

/// <param name="ProcessId">这次拉起来的进程号（<see cref="HostLaunchStatus.Started"/> 时非空；测试与排查要用）。</param>
/// <param name="ElapsedMs">从发起拉起到探活成功的毫秒数。</param>
/// <param name="Detail">给人看的一句话（失败时是原因 + 试过的路径）。</param>
/// <param name="Embedded">
/// 本次内嵌起来的宿主（<see cref="HostLaunchStatus.Embedded"/> 时非空）。**生命周期归调用方** ——
/// 应用退出时 dispose 它；`HostLauncher` 自己也留了一份引用（<see cref="Embedded"/>）方便收尾。
/// </param>
public sealed record HostLaunchOutcome(
    HostLaunchStatus Status,
    string? BaseUrl,
    int ElapsedMs,
    string Detail,
    int? ProcessId = null,
    EmbeddedHost? Embedded = null);

/// <summary>
/// 「客户端检测不到主机就把它拉起来」。
///
/// **默认内嵌**（第 167 轮改）：没有主机在跑时，就在**本应用进程内**起宿主
/// （<see cref="EmbeddedHost"/>，也就是 `QuizSync.Server.Core` 的 `QuizSyncHost` + Kestrel）。
/// 理由：2.0 的目标拓扑本来就是「没探到就内嵌 `Server.Core` 起进程内宿主」
/// （`docs/NATIVE-2.0-PLAN.md` §3 / §8），用户在应用之外不该再维护第二个程序：
/// 装一次、更新一次、少一层「找不到主机程序」的失败面。
/// 「关掉客户端手机还能连」仍然要独立进程 —— 那种场景显式用 <see cref="HostLaunchMode.Standalone"/>，
/// 或者照旧跑 `QuizSync.Server.Cli run`（探活会命中它，本方法直接返回 `AlreadyRunning`，绝不重复起）。
///
/// **与 `setup --autostart` 的分工**：那条是"登录时自动起"（用户装过一次就一直有效）；
/// 这条是"没装启动项、或者主机挂了"时的兜底 —— 两者互补，都要有。
///
/// **绝不静默**：内嵌失败、找不到程序、超时、启动失败，每一种都有明确的一句话 + 原因。
/// </summary>
public static class HostLauncher
{
    private static EmbeddedHost? _embedded;

    /// <summary>本进程内嵌的宿主（没起过就是 null）。给应用退出时收尾用。</summary>
    public static EmbeddedHost? Embedded => _embedded;

    /// <summary>
    /// 关掉本进程内嵌的宿主（应用退出时调；没起过就什么都不做）。
    ///
    /// 收尾要**真的 dispose**：不 dispose 的话 SQLite 连接与监听端口要等进程结束才放开，
    /// 而「退出应用 → 立刻再启动一次」是很常见的动作（端口 TIME_WAIT 会让第二次起来慢一截）。
    /// </summary>
    public static async ValueTask ReleaseEmbeddedAsync()
    {
        var host = Interlocked.Exchange(ref _embedded, null);
        if (host is not null)
        {
            await host.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>主机程序的位置。`QS_SERVER_DLL` 优先（脚本与测试用），否则按仓库相对布局找。</summary>
    public static string? ResolveServerDll()
    {
        var configured = Environment.GetEnvironmentVariable("QS_SERVER_DLL");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        // 从应用所在目录逐级往上找**本仓内的 `server/`**（协议与服务端已并入本仓），
        // 再退回兄弟目录 `QuizSyncServer`（兼容旧的并列布局）。两种构建产物都试：
        // publish（Release 发布）优先，其次 Debug 直出 —— 开发机上往往是后者。
        var relative = new[]
        {
            Path.Combine("src", "QuizSync.Server.Cli", "bin", "Release", "publish", "QuizSync.Server.Cli.dll"),
            Path.Combine("src", "QuizSync.Server.Cli", "bin", "Debug", "net10.0", "QuizSync.Server.Cli.dll"),
        };

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 8 && directory is not null; depth++)
        {
            foreach (var tail in relative)
            {
                var inRepo = Path.Combine(directory.FullName, "server", tail);
                if (File.Exists(inRepo))
                {
                    return inRepo;
                }

                var sibling = Path.Combine(directory.FullName, "QuizSyncServer", tail);
                if (File.Exists(sibling))
                {
                    return sibling;
                }
            }

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>找过哪些地方（失败时原样报给用户，省得他从零开始猜）。</summary>
    public static string SearchedHint() =>
        "环境变量 QS_SERVER_DLL；以及从应用目录逐级向上找本仓 `server/`（或兄弟仓 `QuizSyncServer`）下的 "
        + "`src/QuizSync.Server.Cli/bin/{Release/publish,Debug/net10.0}/QuizSync.Server.Cli.dll`";

    /// <summary>
    /// 探活的**唯一入口**（拉起前后都用它，避免"起来了没有"有两套定义）。
    ///
    /// ⚠️ 端口语义：给了**默认端口**时扫整个 8765–8770（主机可能因为端口占用退到后面几个）；
    /// 给了**具体端口**时只认那一个 —— 否则「我要它在 8797 上跑」会被 8765 上已有的主机
    /// 判成「本来就在运行」，于是新端口上什么都没有，而调用方以为成功了。
    /// </summary>
    private static async Task<HostProbe?> ProbeAsync(int port, CancellationToken cancellationToken)
    {
        var discovery = new HostDiscovery();
        return port == HostDiscovery.BasePort
            ? await discovery.ProbeAsync(cancellationToken: cancellationToken).ConfigureAwait(false)
            : await discovery.ProbePortAsync("127.0.0.1", port, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 确保有一台主机在跑：先探活；没有就按 <paramref name="mode"/> 起一台，然后等它就绪。
    /// </summary>
    /// <param name="port">期望端口（默认 8765；用默认值时探活会扫 8765–8770）。</param>
    /// <param name="serverDataDirectory">主机数据目录（**必须与界面读的是同一个**，否则界面读不到配对码）。</param>
    /// <param name="mode">允许用哪种方式（默认先内嵌、失败再退回独立进程）。</param>
    public static async Task<HostLaunchOutcome> EnsureRunningAsync(
        int port = HostDiscovery.BasePort,
        string? serverDataDirectory = null,
        int timeoutMs = 15000,
        HostLaunchMode mode = HostLaunchMode.Auto,
        CancellationToken cancellationToken = default)
    {
        // ① 已经有人听着了：直接用，绝不重复起第二个（两个主机抢同一个数据目录是灾难）。
        if (await ProbeAsync(port, cancellationToken).ConfigureAwait(false) is { } probe)
        {
            return new HostLaunchOutcome(
                HostLaunchStatus.AlreadyRunning, probe.BaseUrl, 0, "主机本来就在运行");
        }

        // ② 内嵌（默认路径）：起在**本进程内**，用户不用在应用之外再维护一个程序。
        string? embedFailure = null;
        if (mode is HostLaunchMode.Auto or HostLaunchMode.Embedded)
        {
            var embedded = await TryEmbedAsync(port, serverDataDirectory, cancellationToken)
                .ConfigureAwait(false);
            if (embedded.Status is not HostLaunchStatus.Failed)
            {
                return embedded;
            }

            if (mode is HostLaunchMode.Embedded)
            {
                return embedded;
            }

            // Auto：内嵌失败不当作最终结论 —— 独立进程那条路多半还能走通（比如 ASP.NET 宿主
            // 在某个环境里起不来，而 CLI 进程起得来）。两条原因都要带给用户。
            embedFailure = embedded.Detail;
        }

        // ③ 找程序
        var dll = ResolveServerDll();
        if (dll is null)
        {
            return new HostLaunchOutcome(
                HostLaunchStatus.NotFound, null, 0,
                (embedFailure is null ? string.Empty : embedFailure + "；")
                + "找不到主机程序：" + SearchedHint());
        }

        // ④ 起进程：publish 出来的 apphost（.exe）优先，没有就用 dotnet 跑 dll。
        var apphost = Path.ChangeExtension(dll, ".exe");
        var dotnet = Environment.GetEnvironmentVariable("QS_DOTNET");
        if (string.IsNullOrWhiteSpace(dotnet))
        {
            dotnet = "dotnet";
        }

        var useApphost = File.Exists(apphost);
        var fileName = useApphost ? apphost : dotnet;
        var arguments = useApphost
            ? $"run --port {port}"
            : $"\"{dll}\" run --port {port}";
        if (!string.IsNullOrWhiteSpace(serverDataDirectory))
        {
            arguments += $" --data \"{serverDataDirectory}\"";
        }

        var stopwatch = Stopwatch.StartNew();
        Process process;
        try
        {
            process = Process.Start(new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(dll)!,
            })!;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new HostLaunchOutcome(
                HostLaunchStatus.Failed, null, (int)stopwatch.ElapsedMilliseconds,
                (embedFailure is null ? string.Empty : embedFailure + "；")
                + $"启动主机进程失败：{error.Message}");
        }

        // ⑤ 等就绪（探活同一套判据 —— 不另发明"起来了没有"的定义）
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            if (process.HasExited)
            {
                return new HostLaunchOutcome(
                    HostLaunchStatus.Failed, null, (int)stopwatch.ElapsedMilliseconds,
                    (embedFailure is null ? string.Empty : embedFailure + "；")
                    + $"主机进程刚起来就退出了（退出码 {process.ExitCode}）");
            }

            if (await ProbeAsync(port, cancellationToken).ConfigureAwait(false) is { } ready)
            {
                return new HostLaunchOutcome(
                    HostLaunchStatus.Started, ready.BaseUrl, (int)stopwatch.ElapsedMilliseconds,
                    $"已启动主机进程：{ready.BaseUrl}", process.Id);
            }

            await Task.Delay(300, cancellationToken).ConfigureAwait(false);
        }

        return new HostLaunchOutcome(
            HostLaunchStatus.Timeout, null, (int)stopwatch.ElapsedMilliseconds,
            (embedFailure is null ? string.Empty : embedFailure + "；")
            + $"主机起来了但 {timeoutMs} ms 内没就绪（进程 {process.Id} 仍在跑，可以稍后重试）",
            process.Id);
    }

    /// <summary>
    /// 内嵌那条路（②）。失败返回 <see cref="HostLaunchStatus.Failed"/> 而不是抛 ——
    /// 调用方要能决定「换个方式再试」还是「就此报错」。
    /// </summary>
    private static async Task<HostLaunchOutcome> TryEmbedAsync(
        int port, string? serverDataDirectory, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(serverDataDirectory))
        {
            // 没给数据目录就**不内嵌**：Core 会开内存库，起来看着一切正常、重启丢光。
            // 这种"看起来成功"的失败比直接报错坏得多。
            return new HostLaunchOutcome(
                HostLaunchStatus.Failed, null, 0,
                "内嵌宿主需要数据目录（调用方没给）—— 空数据目录会开内存库，重启即丢配对与历史");
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var host = await EmbeddedHost.StartAsync(
                serverDataDirectory, port, cancellationToken: cancellationToken).ConfigureAwait(false);

            // 进程级留一份引用：应用退出时 `ReleaseEmbeddedAsync` 靠它收尾。
            var previous = Interlocked.Exchange(ref _embedded, host);
            if (previous is not null)
            {
                // 同进程里已经内嵌过一个（重复调用）：关掉旧的，绝不留下两个宿主抢同一个数据目录。
                await previous.DisposeAsync().ConfigureAwait(false);
            }

            return new HostLaunchOutcome(
                HostLaunchStatus.Embedded, host.BaseUrl, (int)stopwatch.ElapsedMilliseconds,
                $"主机已内嵌在本应用进程内：{host.BaseUrl}（退出应用即停止）",
                Embedded: host);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return new HostLaunchOutcome(
                HostLaunchStatus.Failed, null, (int)stopwatch.ElapsedMilliseconds,
                $"内嵌宿主失败：{error.Message}");
        }
    }
}
