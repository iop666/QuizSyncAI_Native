using System.Diagnostics;
using System.Text.Json.Nodes;
using Xunit;

namespace QuizSync.Tests;

/// <summary>
/// 无界面入口（`--headless`）的自检：**真的把 CLI 跑起来**（另一个进程），
/// 对着真实 C# Server 看它报的 JSON 对不对。
/// </summary>
public sealed class ProviderCliTests
{
    private static string ServerDll()
    {
        var configured = Environment.GetEnvironmentVariable("QS_SERVER_DLL");
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            candidates.Add(configured);
        }

        var probe = new DirectoryInfo(AppContext.BaseDirectory);
        while (probe is not null)
        {
            candidates.Add(Path.Combine(probe.FullName, "server", "src", "QuizSync.Server.Cli", "bin", "Release", "publish", "QuizSync.Server.Cli.dll"));
            probe = probe.Parent;
        }

        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("找不到服务端产物（见 ServerBridgeEndToEndTests 的说明）");
    }

    private static string CliDll()
    {
        var probe = new DirectoryInfo(AppContext.BaseDirectory);
        while (probe is not null)
        {
            // TFM 不写死：CLI 工程的 TargetFramework 是 net10.0-windows，
            // 硬编码 net10.0 时活仓库只因为一个残留目录才通过，干净构建下这里必红
            // （独立复核在干净副本里实测 3 条红）。按 net10.0* 找目录即可。
            var binDebug = Path.Combine(probe.FullName, "QuizSync.Provider.Cli", "bin", "Debug");
            if (Directory.Exists(binDebug))
            {
                foreach (var tfm in Directory.EnumerateDirectories(binDebug, "net10.0*"))
                {
                    var candidate = Path.Combine(tfm, "QuizSync.Provider.Cli.dll");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }

            probe = probe.Parent;
        }

        throw new FileNotFoundException("找不到 Provider CLI 产物（先 dotnet build）");
    }

    private static string Dotnet() => Environment.GetEnvironmentVariable("QS_DOTNET")
        ?? throw new InvalidOperationException("请设置 QS_DOTNET 指向 dotnet 可执行文件");

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static (string Output, int ExitCode) RunCli(params string[] args)
    {
        var info = new ProcessStartInfo(Dotnet()) { RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(CliDll());
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit(30_000);
        return (output + error, process.ExitCode);
    }

    [Fact]
    public void Version_and_help_work_without_any_configuration()
    {
        var (versionOut, versionCode) = RunCli("version", "--json");
        Assert.Equal(0, versionCode);
        var json = JsonNode.Parse(versionOut)!;
        Assert.Equal("provider", json["component"]!.ToString());
        Assert.Equal("2.0.0", json["protocol"]!.ToString());

        var (helpOut, helpCode) = RunCli("--help");
        Assert.Equal(0, helpCode);
        Assert.Contains("doctor", helpOut, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_command_fails_loudly()
    {
        var (output, code) = RunCli("nonsense");
        Assert.Equal(1, code);
        Assert.Contains("未知命令", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Doctor_against_a_real_server_reports_host_control_plane_and_local_sources()
    {
        var dataDir = Directory.CreateTempSubdirectory("qs-provider-cli-").FullName;
        var port = FreePort();
        using var server = Process.Start(new ProcessStartInfo(Dotnet())
        {
            ArgumentList = { ServerDll(), "run", "--port", port.ToString(), "--data", dataDir },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;

        try
        {
            // 等主机起来（最多 20 秒）。
            var healthy = false;
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
            while (DateTimeOffset.UtcNow < deadline && !healthy)
            {
                healthy = await new QuizSync.ServerBridge.HostDiscovery(http)
                    .ProbePortAsync("127.0.0.1", port) is not null;
                if (!healthy)
                {
                    await Task.Delay(250);
                }
            }

            Assert.True(healthy, "服务端没起来");

            // CLI 只探默认端口段（8765+），所以这里直接断言「本机数据目录那块」是准的；
            // 主机发现走的是同一条 HostDiscovery 代码路径（已由 Bridge 的 E2E 覆盖）。
            var (output, code) = RunCli("doctor", "--data", dataDir, "--json");
            Assert.Equal(0, code);

            var report = JsonNode.Parse(output.Substring(output.IndexOf('{', StringComparison.Ordinal)))!;
            Assert.Equal(dataDir, report["data_dir"]!.ToString());
            Assert.True(report["data_dir_exists"]!.GetValue<bool>());
            Assert.True(report["control_token_present"]!.GetValue<bool>()); // 服务端刚写过 control.token
            Assert.Equal("ok", report["local_db"]!.ToString());             // 库已建好
            Assert.StartsWith("8x8", report["capture"]!.ToString(), StringComparison.Ordinal); // 真截了一块
            Assert.Matches("^v1-[0-9a-f]{8}$", report["prompt_version"]!.ToString());
            // 绝不该出现密钥字段。
            Assert.False(((JsonObject)report).ContainsKey("api_key"));
        }
        finally
        {
            server.Kill(entireProcessTree: true);
            server.WaitForExit(5000);
        }
    }

    /// <summary>
    /// `sessions`：本地识别记录的**合集归属**必须能一眼看出来。
    ///
    /// 第 170 轮加这条命令，就是因为用户报的「api 识别后当前合集看不到」在界面上**什么都不显示**
    /// （左侧列表空、悬浮窗写「还没有识别记录」）：真凶是三个 `SessionRecorder.Record(...)`
    /// 调用点都没传 `collectionId`，记录全成了「未归类」。这条用例把「挂了合集的」与
    /// 「未归类的」两条都写进一个临时库，断言命令把归属逐条报对 ——
    /// 以后谁再把 `collectionId` 漏掉，这条就会红。
    /// </summary>
    [Fact]
    public void Sessions_command_reports_collection_attribution()
    {
        var dataDir = Directory.CreateTempSubdirectory("qs-sessions-").FullName;
        string collectionId;
        using (var database = Data.QuizDatabase.OpenOrCreate(Path.Combine(dataDir, "quizsync.db")))
        {
            var store = new Data.LocalStore(database, "windows-desktop");
            collectionId = Data.CollectionStore.Create(store, "期末复习", now: 1000);
            Data.SessionRecorder.Record(
                store, "sha256:a", "windows-desktop", [], now: 2000, collectionId: collectionId);
            Data.SessionRecorder.Record(store, "sha256:b", "windows-desktop", [], now: 3000);
        }

        var (output, code) = RunCli("sessions", "--data", dataDir, "--json");
        Assert.Equal(0, code);

        var json = JsonNode.Parse(output.Substring(output.IndexOf('{', StringComparison.Ordinal)))!;
        Assert.Equal(2, json["total"]!.GetValue<int>());
        Assert.Equal(1, json["unassigned"]!.GetValue<int>());

        var collections = json["collections"]!.AsArray();
        Assert.Contains(collections, c => c!["name"]!.ToString() == "期末复习" && c["count"]!.GetValue<int>() == 1);
        Assert.Contains(collections, c => c!["name"]!.ToString() == "未归类" && c["count"]!.GetValue<int>() == 1);

        // 逐条的归属也要对得上：挂了的报合集 id，没挂的报 null。
        var sessions = json["sessions"]!.AsArray();
        Assert.Contains(sessions, s => s!["collection_id"]?.ToString() == collectionId);
        Assert.Contains(sessions, s => s!["collection_id"] is null);
    }
}
