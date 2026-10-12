using System.Diagnostics;
using QuizSync.ServerBridge;
using Xunit;

namespace QuizSync.Tests;

/// <summary>
/// 「启动识别服务」（`QuizSync.Provider.Cli`）—— 与 <see cref="HostLauncherTests"/> 同一套路：
/// **真的起主机、真的把 Provider 拉起来**，因为这件事的价值恰恰在于「进程真的起来了、主机真的认到它了」，
/// 用 mock 测等于什么都没测。
///
/// 收尾必须杀干净：留一个 Provider 进程会顶掉用户自己那个（协议里是单 Provider 语义）。
/// 与 <see cref="HostLauncherTests"/> / <see cref="EmbeddedHostTests"/> 同属一个 collection：
/// 三者都占 8765–8770 或写 `HostLauncher` 的进程级内嵌引用，并行会互相踩。
/// </summary>
[Collection("host-launcher")]
public sealed class ProviderLauncherTests
{
    [Fact]
    public void Resolve_prefers_the_environment_override()
    {
        var fake = Path.Combine(Path.GetTempPath(), $"qs-provider-{Guid.NewGuid():N}.dll");
        File.WriteAllText(fake, "not a real dll — 只验路径解析，不验能不能跑");
        var previous = Environment.GetEnvironmentVariable("QS_PROVIDER_DLL");

        try
        {
            Environment.SetEnvironmentVariable("QS_PROVIDER_DLL", fake);
            Assert.Equal(fake, ProviderLauncher.ResolveProviderDll());
        }
        finally
        {
            Environment.SetEnvironmentVariable("QS_PROVIDER_DLL", previous);
            File.Delete(fake);
        }
    }

    [Fact]
    public void Resolve_reports_where_it_looked_when_nothing_is_found()
    {
        // 没有程序时必须给出「找过哪里」—— 否则用户只能从零开始猜（这条是踩出来的纪律）。
        var hint = ProviderLauncher.SearchedHint();
        Assert.Contains("QS_PROVIDER_DLL", hint);
        Assert.Contains("QuizSync.Provider.Cli", hint);
    }

    [Fact]
    public async Task IsOnline_reports_null_when_the_host_is_unreachable()
    {
        // 「在线 / 不在线 / 问不出来」是三件事。把「问不出来」当成 false，会得出
        // 「主机没起来却说识别服务不在线」这种假结论 —— 所以这里要求它是 null，不是 false。
        Assert.Null(await ProviderLauncher.IsOnlineAsync("http://127.0.0.1:1"));
    }

    [Fact]
    public async Task Host_confirms_a_provider_started_by_the_launcher()
    {
        var dll = ProviderLauncher.ResolveProviderDll();
        Assert.False(string.IsNullOrWhiteSpace(dll), "找不到识别服务程序：" + ProviderLauncher.SearchedHint());

        // ⚠️ 主机必须起在**默认端口区间**：Provider CLI 的主机发现只扫 8765–8770（它没有 --port 参数），
        // 所以这条不能像 HostLauncherTests 那样自己挑空端口 —— 挑到 8797 就永远拉不起来。
        var dataDirectory = Path.Combine(Path.GetTempPath(), $"qs-provider-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDirectory);

        var host = await HostLauncher.EnsureRunningAsync(
            HostDiscovery.BasePort, dataDirectory, timeoutMs: 25000);
        // 第 167 轮起默认是**进程内嵌**（`Embedded`），所以这里三种都算「有一台主机在跑」。
        // 这条测试要证的从来不是"起了个进程"，而是「主机真的认到了 Provider」——
        // 内嵌宿主与独立进程对 Provider 而言完全一样（都是回环上的一个 HTTP/WS 端点），
        // 这一点正是内嵌可行的根据。
        Assert.True(
            host.Status is HostLaunchStatus.Embedded or HostLaunchStatus.Started or HostLaunchStatus.AlreadyRunning,
            host.Detail);
        Assert.NotNull(host.BaseUrl);

        ProviderLaunchOutcome outcome = null!;
        try
        {
            // 主机数据目录只在我们**真的起了自己的主机**时才传：
            // 若 8765 上已经有别人的主机（`AlreadyRunning`），它的库与我们的临时目录对不上，
            // 传了反而让 Provider 用错 control.token 配不上对 —— 那种情况让它走默认位置即可。
            var ownHost = host.Status is HostLaunchStatus.Embedded or HostLaunchStatus.Started;
            var providerDataDirectory = ownHost ? dataDirectory : null;

            // `serverDataDirectory` 会把主机数据目录通过 `QS_SERVER_DATA` 传给子进程 ——
            // Provider 的 `provider` 子命令不读 `--data`，靠的就是这个环境变量找 control.token。
            outcome = await ProviderLauncher.EnsureOnlineAsync(
                host.BaseUrl!, serverDataDirectory: providerDataDirectory, timeoutMs: 30000);

            Assert.True(
                outcome.Status is ProviderLaunchStatus.Started or ProviderLaunchStatus.AlreadyOnline,
                $"{outcome.Status}：{outcome.Detail}"
                + "（Started 需要本机已经配好 AI Key —— 没配 Key 的 Provider 会以退出码 2 立刻退出，这是协议定的）");

            // 判据只有一条：**主机自己说它在**（不拿「进程还在跑」当在线）。
            Assert.True(await ProviderLauncher.IsOnlineAsync(host.BaseUrl!) is true);

            // 第 169 轮：设置页那个「重启连接服务」靠 `TryStopStarted` 才真的能重启 ——
            // 不先关掉旧的，`EnsureOnlineAsync` 会看到 `provider_online = true` 直接返回
            // 「本来就在线」，重启就成了空操作。
            // 同时它必须**只关自己起的那个**：`AlreadyOnline`（`setup --autostart` 或用户手开）
            // 时一律不动手 —— 在别人的进程上动手比「重启没生效」更坏。
            var stopped = ProviderLauncher.TryStopStarted();
            if (outcome.Status is ProviderLaunchStatus.Started)
            {
                Assert.True(stopped, "本应用拉起来的识别服务应当能被关掉");

                var offline = false;
                for (var attempt = 0; attempt < 20 && !offline; attempt++)
                {
                    await Task.Delay(300);
                    offline = await ProviderLauncher.IsOnlineAsync(host.BaseUrl!) is false;
                }

                Assert.True(offline, "关掉识别服务之后，主机应当如实报 provider_online=false");
            }
            else
            {
                Assert.False(stopped, "识别服务不是本应用起的（AlreadyOnline）：绝不许关别人的进程");
            }
        }
        finally
        {
            if (outcome?.ProcessId is { } providerPid)
            {
                Kill(providerPid);
            }

            if (host.ProcessId is { } hostPid)
            {
                Kill(hostPid);
            }

            // 内嵌宿主也要收掉：留着它会把 8765 一直占住，
            // 后面的用例与用户自己的实例都会莫名其妙连不上（正是这组测试最怕的事）。
            await HostLauncher.ReleaseEmbeddedAsync();

            try
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
            catch (IOException)
            {
                // 临时目录清不掉不该让测试变红
            }
        }
    }

    private static void Kill(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch (ArgumentException)
        {
            // 已经退出了
        }
    }
}
