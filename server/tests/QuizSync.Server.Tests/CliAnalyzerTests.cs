using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Xunit.Sdk;

namespace QuizSync.Server.Tests;

/// <summary>
/// CLI 的识别器选择：`run` **默认派发给 Provider**，假识别器只能靠 `--analyzer fixture` 显式开。
///
/// 用例把 CLI 当**独立进程**跑（默认值是参数解析里的东西，只调函数测不出来），然后走真 HTTP：
/// 配对 → 上传 → 建任务 → 看结果是「fixture 的那 1 道题」还是「provider_offline」。
/// 这一对用例合起来就是「fixture 只在显式开启时才用」的证据。
/// </summary>
public sealed class CliAnalyzerTests
{
    /// <summary>
    /// CLI 产物就在测试输出目录里（测试项目引用了 CLI 工程，构建顺序有保证）。
    /// 与 <see cref="SetupWizardTests"/> 用的是同一条约定：往上找 bin 目录会拿到**陈旧二进制**。
    /// </summary>
    private static string CliDll()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "QuizSync.Server.Cli.dll");
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException($"测试输出目录里没有 CLI 产物：{path}");
    }

    /// <summary>优先 `QS_DOTNET`（本机 SDK 没进 PATH 时用），否则退化到 PATH 里的 `dotnet`（CI）。</summary>
    private static string Dotnet() =>
        Environment.GetEnvironmentVariable("QS_DOTNET") is { Length: > 0 } configured ? configured : "dotnet";

    [Fact]
    public async Task Default_run_dispatches_to_a_provider_and_therefore_fails_without_one()
    {
        var dataDir = Directory.CreateTempSubdirectory("qs-cli-provider-").FullName;

        // **不传 --analyzer**：走默认值。没有 Provider 在线的真机上，这一单必须是
        // provider_offline —— 而不是悄悄用 fixture 编一道题出来。
        await using var server = await CliServer.StartAsync(dataDir, analyzer: null);
        var view = await SubmitAndWaitAsync(server.BaseUrl, dataDir, "t-cli-default");

        Assert.Equal("failed", view["status"]!.GetValue<string>());
        Assert.Equal("provider_offline", view["error_code"]!.GetValue<string>());
        Assert.Equal("provider_offline", view["session"]!["error_code"]!.GetValue<string>());
        Assert.Contains("派发给电脑端 Provider", server.Log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fixture_analyzer_is_used_only_when_the_flag_asks_for_it()
    {
        var dataDir = Directory.CreateTempSubdirectory("qs-cli-fixture-").FullName;

        await using var server = await CliServer.StartAsync(dataDir, analyzer: "fixture");
        var view = await SubmitAndWaitAsync(server.BaseUrl, dataDir, "t-cli-fixture");

        Assert.Equal("done", view["status"]!.GetValue<string>());
        var session = view["session"]!.AsObject();
        Assert.Equal(1, session["question_count"]!.GetValue<long>());
        var question = session["questions"]!.AsArray()[0]!.AsObject();
        Assert.Equal("第 1 题（fixture）", question["stem"]!.GetValue<string>());
        Assert.Contains("fixture（假识别器", server.Log, StringComparison.Ordinal);
    }

    [Fact]
    public void Help_documents_the_analyzer_flags_and_their_default()
    {
        using var process = Process.Start(new ProcessStartInfo(Dotnet())
        {
            RedirectStandardOutput = true,
            StandardOutputEncoding = Encoding.UTF8,
            ArgumentList = { CliDll(), "--help" },
        })!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(30_000);

        Assert.Equal(0, process.ExitCode);
        Assert.Contains("--analyzer <fixture|provider>", output, StringComparison.Ordinal);
        Assert.Contains("--analyzer-timeout <ms>", output, StringComparison.Ordinal);
        // 帮助文本必须讲清「假识别器只能显式开」的理由，否则下一个人会以为默认是它。
        Assert.Contains("它永远成功", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// 起一个真的 CLI `run` 进程（`--port 0` 让系统分配端口），等它打印出监听地址。
    /// </summary>
    private sealed class CliServer : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly StringBuilder _log;

        private CliServer(Process process, string baseUrl, StringBuilder log)
        {
            _process = process;
            BaseUrl = baseUrl;
            _log = log;
        }

        public string BaseUrl { get; }

        public string Log
        {
            get
            {
                lock (_log)
                {
                    return _log.ToString();
                }
            }
        }

        public static async Task<CliServer> StartAsync(string dataDir, string? analyzer)
        {
            var info = new ProcessStartInfo(Dotnet())
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // CLI 的中文输出是 UTF-8，别让测试按控制台代码页解。
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            info.ArgumentList.Add(CliDll());
            info.ArgumentList.Add("run");
            info.ArgumentList.Add("--data");
            info.ArgumentList.Add(dataDir);
            info.ArgumentList.Add("--port");
            info.ArgumentList.Add("0");
            if (analyzer is not null)
            {
                info.ArgumentList.Add("--analyzer");
                info.ArgumentList.Add(analyzer);
            }

            var process = Process.Start(info)!;
            var log = new StringBuilder();
            _ = Task.Run(async () =>
            {
                while (await process.StandardOutput.ReadLineAsync() is { } line)
                {
                    lock (log)
                    {
                        log.AppendLine(line);
                    }
                }
            });
            _ = Task.Run(async () =>
            {
                while (await process.StandardError.ReadLineAsync() is { } line)
                {
                    lock (log)
                    {
                        log.AppendLine(line);
                    }
                }
            });

            const string marker = "已启动：";
            var deadline = Environment.TickCount64 + 30_000;
            while (Environment.TickCount64 < deadline)
            {
                string text;
                lock (log)
                {
                    text = log.ToString();
                }

                var index = text.IndexOf(marker + "http://", StringComparison.Ordinal);
                if (index >= 0)
                {
                    var start = index + marker.Length;
                    var end = text.IndexOfAny(['\r', '\n'], start);
                    return new CliServer(process, text[start..(end < 0 ? text.Length : end)].Trim(), log);
                }

                await Task.Delay(100);
            }

            Kill(process);
            throw new XunitException($"CLI 没在 30 秒内报出监听地址，输出：{log}");
        }

        public async ValueTask DisposeAsync()
        {
            Kill(_process);
            await _process.WaitForExitAsync();
            _process.Dispose();
        }

        private static void Kill(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
            {
                // 已经退了。
            }
        }
    }

    /// <summary>
    /// 走完整链路提交一单并把任务视图轮询到终态：控制令牌 → 配对码 → 配对 → 建合集（同步 op）
    /// → 选中合集 → 上传一页图 → 建任务。
    /// </summary>
    private static async Task<JsonObject> SubmitAndWaitAsync(string baseUrl, string dataDir, string taskId)
    {
        using var client = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(20) };

        // 与 `pair` 子命令同一条路：读数据目录里的 control.token，再打本机控制面拿配对码。
        var controlToken = File.ReadAllText(Path.Combine(dataDir, "control.token")).Trim();
        client.DefaultRequestHeaders.Add("X-QS-Control", controlToken);
        var pairCode = JsonNode.Parse(await client.GetStringAsync("/api/v1/pair/code"))!["code"]!.GetValue<string>();
        client.DefaultRequestHeaders.Remove("X-QS-Control");

        var pairBody = $$"""
            {"code":"{{pairCode}}","device_id":"android-device-1","device_name":"Pixel 7","platform":"android","app_version":"1.0.0"}
            """;
        var paired = await client.PostAsync("/api/v1/pair", new StringContent(pairBody, Encoding.UTF8, "application/json"));
        var token = JsonNode.Parse(await paired.Content.ReadAsStringAsync())!["token"]!.GetValue<string>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // 合集：协议里靠同步 op 建（没有 HTTP 创建端点），推一条与 sync.ndjson 第 5 步同形的 op。
        var ops = """
            {"ops":[{"op_id":"op-cli-collection","device_id":"android-device-1","lamport":1,"entity":"collection","entity_id":"c-cli","op_type":"upsert","fields_json":{"name":"CLI 合集","created_at":1,"updated_at":1,"updated_by":"android-device-1"}}]}
            """;
        var pushed = await client.PostAsync(
            "/api/v1/sync/ops", new StringContent(ops, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, pushed.StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.PostAsync("/api/v1/collections/c-cli/select", null)).StatusCode);

        using var image = new MultipartFormDataContent();
        var part = new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xD9, 1, 2, 3, 4]);
        part.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        image.Add(part, "file", "q.jpg");
        using var uploaded = await client.PostAsync("/api/v1/images", image);
        var uploadedText = await uploaded.Content.ReadAsStringAsync();
        Assert.True(uploaded.StatusCode == HttpStatusCode.OK, $"上传失败：{(int)uploaded.StatusCode} {uploadedText}");
        var hash = JsonNode.Parse(uploadedText)!["image_hash"]!.GetValue<string>();

        var taskBody = $$"""
            {"task_id":"{{taskId}}","image_hash":"{{hash}}","source_device":"android-device-1"}
            """;
        using var submitted = await client.PostAsync(
            "/api/v1/tasks", new StringContent(taskBody, Encoding.UTF8, "application/json"));
        Assert.True(
            submitted.StatusCode == HttpStatusCode.Accepted,
            $"建任务返回 {(int)submitted.StatusCode}：{await submitted.Content.ReadAsStringAsync()}");

        var deadline = Environment.TickCount64 + 30_000;
        JsonObject? view = null;
        while (Environment.TickCount64 < deadline)
        {
            view = JsonNode.Parse(await client.GetStringAsync($"/api/v1/tasks/{taskId}"))!.AsObject();
            if (view["status"]?.ToString() is "done" or "failed")
            {
                return view;
            }

            await Task.Delay(100);
        }

        throw new XunitException($"任务 {taskId} 没在 30 秒内到终态：{view?.ToJsonString()}");
    }
}
