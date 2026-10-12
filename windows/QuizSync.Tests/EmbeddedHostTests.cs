using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using QuizSync.ServerBridge;
using Xunit;

namespace QuizSync.Tests;

/// <summary>
/// **进程内嵌的宿主**（第 167 轮）：`QuizSync.Server.Core` 的 Kestrel 宿主跑在**测试进程自己里面**。
///
/// 这组测试要钉住三件事，每一件都是「看起来能跑但其实错了」的高发区：
/// ① 它真的是个**能用**的主机 —— `/health` 与 v1 兼容层（Android 走的就是 v1）都得通，
///    控制面令牌还得能读配对码（令牌算法与 CLI 对不上的话，界面上的「检测并启动主机」会报 control_denied，
///    看起来像鉴权坏了，其实是两个进程各写了一份令牌）；
/// ② 「先探活，绝不重复起第二个」这条**在进程内同样成立** —— 探活是 HTTP，
///    问自己也是问得到的，所以第二次调用必须是 `AlreadyRunning`；
/// ③ 收尾真的把端口放掉（不 dispose 时 SQLite 连接与监听端口要等进程结束才放开）。
///
/// 端口刻意错开：本套件里 `HostLauncherTests` 用 8797/8798/8802、`ProviderLauncherTests` 用 8765，
/// 而 xUnit 默认**并行跑测试类** —— 撞端口会变成"随机红"。
/// 与 `HostLauncherTests` 同属一个 collection：两者都会写 `HostLauncher` 的进程级内嵌引用。
/// </summary>
[Collection("host-launcher")]
public sealed class EmbeddedHostTests
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(10) };

    private static string NewDataDirectory() =>
        Path.Combine(Path.GetTempPath(), $"qs-embedded-{Guid.NewGuid():N}");

    /// <summary>
    /// 临时目录清不掉不该让测试变红 —— 但**必须先把宿主 dispose 掉**再删：
    /// 库文件还被 SQLite 持有的时候，Windows 上删目录必然失败。
    /// </summary>
    private static void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static async Task<bool> HealthOkAsync(string baseUrl)
    {
        try
        {
            using var response = await Client.GetAsync($"{baseUrl}/health");
            return response.StatusCode == HttpStatusCode.OK;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
    }

    [Fact]
    public async Task Serves_health_and_the_v1_compatibility_layer_from_this_process()
    {
        var dataDirectory = NewDataDirectory();
        var host = await EmbeddedHost.StartAsync(dataDirectory, port: 0);

        try
        {
            Assert.True(host.Port > 0, "端口 0 是「随便挑一个」，起来之后必须回填真实端口");

            using var health = await Client.GetAsync($"{host.BaseUrl}/health");
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
            var healthJson = JsonNode.Parse(await health.Content.ReadAsStringAsync())!.AsObject();
            Assert.Equal("ok", healthJson["status"]!.GetValue<string>());
            Assert.Equal(2, healthJson["protocol_version"]!.GetValue<int>());

            // v2 的加法字段（1.1.0 的客户端不认它，但它是「这台主机是 2.0」的标志之一）。
            using var info = await Client.GetAsync($"{host.BaseUrl}/api/v2/info");
            Assert.Equal(HttpStatusCode.OK, info.StatusCode);

            Assert.Equal(6, host.PairingCode.Length);
            Assert.All(host.PairingCode, c => Assert.True(char.IsAsciiDigit(c)));
        }
        finally
        {
            await host.DisposeAsync();
            TryDelete(dataDirectory);
        }
    }

    [Fact]
    public async Task Control_token_written_here_is_the_one_the_control_plane_accepts()
    {
        // 令牌算法必须与 `QuizSync.Server.Cli` 的 `ReadOrCreateControlToken` 一致：
        // 同一个数据目录今天被 CLI 起、明天被内嵌宿主起，令牌一变界面就再也连不上
        // （而且报的是 control_denied，看着像鉴权坏了）。
        var dataDirectory = NewDataDirectory();
        var host = await EmbeddedHost.StartAsync(dataDirectory, port: 0);

        try
        {
            var token = HostControl.FindControlToken(dataDirectory);
            Assert.NotNull(token);
            Assert.Equal(64, token!.Length); // 32 字节 = 64 位 hex

            var control = new HostControl(host.BaseUrl, token);
            var pairCode = await control.PairCodeAsync();
            Assert.NotNull(pairCode);
            Assert.Equal(host.PairingCode, pairCode!["code"]!.GetValue<string>());

            // 负向对照：拿一个错的令牌必须被拒（否则这个"能用"的结论什么都证明不了）。
            var wrong = new HostControl(host.BaseUrl, new string('0', 64));
            await Assert.ThrowsAsync<HostException>(() => wrong.PairCodeAsync());
        }
        finally
        {
            await host.DisposeAsync();
            TryDelete(dataDirectory);
        }
    }

    [Fact]
    public async Task Ensure_running_reuses_the_embedded_host_instead_of_starting_a_second_one()
    {
        // 两个宿主抢同一个数据目录是灾难（`/ws` 与库都是单写者语义）。探活走 HTTP，
        // 所以「问自己」也要能命中 —— 命中就必须是 AlreadyRunning，且不再新起一个。
        const int port = 8799;
        var dataDirectory = NewDataDirectory();

        try
        {
            var first = await HostLauncher.EnsureRunningAsync(
                port, dataDirectory, timeoutMs: 20000, mode: HostLaunchMode.Embedded);
            var second = await HostLauncher.EnsureRunningAsync(
                port, dataDirectory, timeoutMs: 5000, mode: HostLaunchMode.Embedded);

            Assert.Equal(HostLaunchStatus.Embedded, first.Status);
            Assert.NotNull(first.Embedded);
            Assert.Null(first.ProcessId); // 一个进程都没起 —— 这就是「内嵌」的定义
            Assert.Equal(HostLaunchStatus.AlreadyRunning, second.Status);
            Assert.Null(second.Embedded);

            // 两次拿到的是**同一个**宿主实例（第二次没有再内嵌一个）。
            Assert.Same(first.Embedded, HostLauncher.Embedded);
        }
        finally
        {
            await HostLauncher.ReleaseEmbeddedAsync();
            TryDelete(dataDirectory);
        }
    }

    [Fact]
    public async Task Embedded_only_mode_reports_a_failure_instead_of_spawning_a_process()
    {
        // 负向对照：`HostLaunchMode.Embedded` 不允许「悄悄换个方式」。
        // 不给数据目录 = 会开内存库（起来看着正常、重启丢光），必须当场报错而不是起进程。
        var outcome = await HostLauncher.EnsureRunningAsync(
            port: 8800,
            serverDataDirectory: null,
            timeoutMs: 5000,
            mode: HostLaunchMode.Embedded);

        Assert.Equal(HostLaunchStatus.Failed, outcome.Status);
        Assert.Contains("数据目录", outcome.Detail);
        Assert.Null(outcome.ProcessId);
        Assert.Null(outcome.Embedded);
    }

    [Fact]
    public async Task Disposing_the_host_stops_serving()
    {
        // 「真的是它在监听」的负向对照：dispose 之后同一个地址必须连不上。
        const int port = 8801;
        var dataDirectory = NewDataDirectory();
        var host = await EmbeddedHost.StartAsync(dataDirectory, port);

        try
        {
            Assert.True(await HealthOkAsync(host.BaseUrl), "刚起来就该能应答");

            await host.DisposeAsync();

            Assert.False(await HealthOkAsync(host.BaseUrl), "dispose 之后不该还在应答");
        }
        finally
        {
            TryDelete(dataDirectory);
        }
    }
}
