using QuizSync.Server.Core;
using QuizSync.Server.Core.Tasks;

namespace QuizSync.ServerBridge;

/// <summary>
/// **本进程内**的主机（`QuizSyncHost` + Kestrel）。
///
/// 为什么要有它：2.0 的目标形态本来就是「客户端探不到主机就**内嵌** `Server.Core` 起进程内宿主」
/// （`docs/NATIVE-2.0-PLAN.md` §3 的拓扑图与 §8 第 440 行），`QuizSync.Server.Core.csproj` 里也写明
/// 「Core 自己承载 HTTP 宿主（内嵌 Kestrel）：它既被 CLI 加载，**也会被桌面端进程内嵌**」。
/// 但这条线一直没接线（第 141 轮甚至把 Phase 7 第④项改口径成「一键拉起独立进程」），
/// 于是用户要装两样东西、更新两遍，还多出一层「找不到主机程序」的失败面。第 167 轮按原口径接上。
///
/// **对客户端完全不可见**：它照旧监听 `0.0.0.0:8765`（回环上就是 `127.0.0.1:8765`），
/// 手机、桌面端自己的 `HostClient`、控制面令牌这条路一个字节都没变 ——
/// 变的只是「谁在监听」：以前是 `QuizSync.Server.Cli` 进程，现在是应用进程自己。
/// 所以配对、同步、矩阵用例都不需要任何改动，也**不该**有改动。
///
/// **代价写清楚**：宿主活在这个进程里，所以**应用真的退出，手机就断了**
/// （1.1.0 时代就是这个语义）。要「关掉客户端、手机还能连」，就走 `HostLaunchMode.Standalone`
/// 的独立进程那条路 —— 两条路互斥，靠「先探活」保证不会同时有两个宿主抢同一个数据目录。
/// </summary>
public sealed class EmbeddedHost : IAsyncDisposable
{
    /// <summary>
    /// 等 Provider 回包的上限。默认值 = 协议冻结的 `deadline_ms`，
    /// 与 CLI 的 `--analyzer-timeout` 默认值一致（那边叫 `CliArgs.DefaultAnalyzerTimeoutMs`）。
    /// </summary>
    public const int DefaultAnalyzerTimeoutMs = 90_000;

    private readonly QuizSyncHost _host;

    private EmbeddedHost(QuizSyncHost host, string dataDirectory)
    {
        _host = host;
        DataDirectory = dataDirectory;
    }

    /// <summary>回环基址（`http://127.0.0.1:<port>`）。</summary>
    public string BaseUrl => _host.BaseUrl;

    /// <summary>实际绑定到的端口（默认端口被占时会向上让，与 CLI 一致）。</summary>
    public int Port => _host.Port;

    /// <summary>当前配对码（6 位）。给界面与探针看，别写进日志以外的地方。</summary>
    public string PairingCode => _host.Pairing.Code;

    /// <summary>宿主用的数据目录（`quizsync.db` 与 `control.token` 都在这里）。</summary>
    public string DataDirectory { get; }

    /// <summary>
    /// 在本进程内起一个宿主并返回它。**调用方负责 <see cref="DisposeAsync"/>**
    /// （应用退出时收尾；不 dispose 的话 SQLite 连接与监听端口要等进程结束才放开）。
    /// </summary>
    /// <param name="dataDirectory">
    /// 数据目录。**必须与桌面端界面读的是同一个** —— 否则界面读不到宿主写的配对码
    /// （这个项目为这件事踩过一次）。也**不许为空**：空值会让 Core 开内存库，
    /// 起来看着一切正常，重启却把配对与历史全丢了。
    /// </param>
    /// <param name="port">期望端口（默认 8765）。默认端口允许向上让到 8770，指定端口只认那一个。</param>
    /// <param name="aiConfigured">
    /// 「这台电脑配了 AI Key 吗」——只在 Provider 没在线时作为 `/info` 的兜底值用
    /// （在线时以 Provider 自述为准）。
    /// </param>
    public static async Task<EmbeddedHost> StartAsync(
        string dataDirectory,
        int port = HostDiscovery.BasePort,
        bool aiConfigured = false,
        int analyzerTimeoutMs = DefaultAnalyzerTimeoutMs,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            throw new ArgumentException(
                "内嵌宿主必须给数据目录：空值会开内存库，重启就把配对与历史丢了", nameof(dataDirectory));
        }

        var options = new ServerOptions
        {
            DataDirectory = dataDirectory,
            PreferredPort = port,
            // 与探活同一套语义（HostLauncher 那条注释）：默认端口扫 8765–8770，
            // 指定端口只认那一个 —— 否则「我要它跑在 8797」会被悄悄挪到 8798 而调用方以为成功了。
            PortRange = port == HostDiscovery.BasePort ? ServerOptions.DefaultPortRange : 1,
            ControlToken = HostControl.ReadOrCreateControlToken(dataDirectory),
            AiConfigured = aiConfigured,
        };

        // 识别器与 CLI 的 `run` 是**同一条**：派发给在线 Provider。
        // **绝不用 FixtureAnalyzer** —— 那是测试/演示用的假识别器，会给真人回「第 1 题（fixture）」
        // 这种胡话，而且永远成功、没有任何报错（CLI 那边为此专门写了注释）。
        var host = await QuizSyncHost.StartAsync(
            options,
            clock: null,
            analyzerFactory: hub => new ProviderAnalyzer(
                hub.Providers, SystemClock.Instance, analyzerTimeoutMs),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return new EmbeddedHost(host, dataDirectory);
    }

    public ValueTask DisposeAsync() => _host.DisposeAsync();
}
