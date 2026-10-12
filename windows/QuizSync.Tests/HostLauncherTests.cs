using System.Diagnostics;
using QuizSync.ServerBridge;
using Xunit;

namespace QuizSync.Tests;

/// <summary>
/// 「一键拉起主机」（Phase 7-④）**的独立进程那条路**。
///
/// 第 167 轮起默认路径改成**进程内嵌**（<see cref="EmbeddedHostTests"/> 覆盖它），
/// 所以这组测试显式用 <see cref="HostLaunchMode.Standalone"/> —— 它们要证的正是
/// 「进程真的起来了、真的就绪了」，用 mock 测等于什么都没测。
/// 收尾必须杀干净：留一个后台主机进程会让下一次测试与用户的实例互相打架（这个项目踩过）。
///
/// 与 <see cref="EmbeddedHostTests"/> 同属一个 collection：两者都会写
/// `HostLauncher` 的进程级内嵌引用，并行跑会互相把对方的宿主关掉（表现为"随机红"）。
/// </summary>
[Collection("host-launcher")]
public sealed class HostLauncherTests
{
    [Fact]
    public void Resolve_prefers_the_environment_override()
    {
        var fake = Path.Combine(Path.GetTempPath(), $"qs-host-{Guid.NewGuid():N}.dll");
        File.WriteAllText(fake, "not a real dll — 只验路径解析，不验能不能跑");
        var previous = Environment.GetEnvironmentVariable("QS_SERVER_DLL");

        try
        {
            Environment.SetEnvironmentVariable("QS_SERVER_DLL", fake);
            Assert.Equal(fake, HostLauncher.ResolveServerDll());
        }
        finally
        {
            Environment.SetEnvironmentVariable("QS_SERVER_DLL", previous);
            File.Delete(fake);
        }
    }

    [Fact]
    public void Resolve_reports_where_it_looked_when_nothing_is_found()
    {
        // 没有程序时必须给出「找过哪里」—— 否则用户只能从零开始猜（这条是踩出来的纪律）。
        var hint = HostLauncher.SearchedHint();
        Assert.Contains("QS_SERVER_DLL", hint);
        Assert.Contains("QuizSyncServer", hint);
    }

    [Fact]
    public async Task Starts_a_real_host_on_a_free_port_and_reports_its_url()
    {
        var dll = HostLauncher.ResolveServerDll();
        Assert.False(string.IsNullOrWhiteSpace(dll), "找不到主机程序：" + HostLauncher.SearchedHint());

        // 刻意用一个**非默认**端口：既不和用户可能在跑的 8765 抢，
        // 也正好验「给了具体端口就只认那个端口」这条语义。
        const int port = 8797;
        var dataDirectory = Path.Combine(Path.GetTempPath(), $"qs-host-data-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDirectory);

        var outcome = await HostLauncher.EnsureRunningAsync(
            port, dataDirectory, timeoutMs: 25000, mode: HostLaunchMode.Standalone);

        try
        {
            Assert.True(
                outcome.Status is HostLaunchStatus.Started or HostLaunchStatus.AlreadyRunning,
                $"{outcome.Status}：{outcome.Detail}");
            Assert.NotNull(outcome.BaseUrl);
            Assert.Contains(port.ToString(System.Globalization.CultureInfo.InvariantCulture), outcome.BaseUrl!);
            // 起来了就该有数据目录里的东西（control.token 是主机启动时写的）
            Assert.True(File.Exists(Path.Combine(dataDirectory, "control.token")),
                "主机起来了却没写 control.token —— 数据目录可能没传对");
        }
        finally
        {
            if (outcome.ProcessId is { } pid)
            {
                try
                {
                    Process.GetProcessById(pid).Kill(entireProcessTree: true);
                    Process.GetProcessById(pid).WaitForExit(5000);
                }
                catch (ArgumentException)
                {
                    // 已经退出了
                }
            }

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

    [Fact]
    public async Task Reuses_an_already_running_host_instead_of_starting_a_second_one()
    {
        // 同一端口连拉两次：第二次必须是 `AlreadyRunning`，而且**不能再起一个进程**。
        // 两个主机抢同一个数据目录是灾难（协议里 `/ws` 与库都是单写者语义）。
        var dll = HostLauncher.ResolveServerDll();
        Assert.False(string.IsNullOrWhiteSpace(dll), "找不到主机程序：" + HostLauncher.SearchedHint());

        const int port = 8798;
        var dataDirectory = Path.Combine(Path.GetTempPath(), $"qs-host-reuse-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDirectory);

        var first = await HostLauncher.EnsureRunningAsync(
            port, dataDirectory, timeoutMs: 25000, mode: HostLaunchMode.Standalone);
        var second = await HostLauncher.EnsureRunningAsync(
            port, dataDirectory, timeoutMs: 5000, mode: HostLaunchMode.Standalone);

        try
        {
            Assert.True(first.Status is HostLaunchStatus.Started or HostLaunchStatus.AlreadyRunning, first.Detail);
            Assert.Equal(HostLaunchStatus.AlreadyRunning, second.Status);
            Assert.Null(second.ProcessId); // 没起新进程
        }
        finally
        {
            if (first.ProcessId is { } pid)
            {
                try
                {
                    Process.GetProcessById(pid).Kill(entireProcessTree: true);
                    Process.GetProcessById(pid).WaitForExit(5000);
                }
                catch (ArgumentException)
                {
                }
            }

            try
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task Default_mode_embeds_the_host_instead_of_starting_a_process()
    {
        // 默认口径（第 167 轮）：用户只需要**一个应用** —— 没探到主机时在本进程内嵌起一个，
        // 而不是在应用之外再拉一个程序出来（那要求用户装两样、更新两遍，
        // 还多一层「找不到主机程序」的失败面）。这条一旦悄悄回退成"起进程"，
        // 「装一次、更新一次」的承诺就没了，所以在**默认参数**上钉一遍。
        const int port = 8802;
        var dataDirectory = Path.Combine(Path.GetTempPath(), $"qs-host-embed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDirectory);

        try
        {
            var outcome = await HostLauncher.EnsureRunningAsync(port, dataDirectory, timeoutMs: 20000);

            Assert.Equal(HostLaunchStatus.Embedded, outcome.Status);
            Assert.Null(outcome.ProcessId); // 一个子进程都没起
            Assert.NotNull(outcome.Embedded);
            Assert.Contains(port.ToString(System.Globalization.CultureInfo.InvariantCulture), outcome.BaseUrl!);
            Assert.True(File.Exists(Path.Combine(dataDirectory, "control.token")));
        }
        finally
        {
            await HostLauncher.ReleaseEmbeddedAsync();

            try
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
