using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using QuizSync.Server.Core;
using QuizSync.Server.Core.Tasks;
using QuizSync.Server.Core.Ws;
using Xunit.Sdk;

namespace QuizSync.Server.Tests;

/// <summary>
/// 「识别派发给 Provider」整条链路的验收：**真 Host + 真 ClientWebSocket + 真 HTTP**，
/// 不 mock 任何一层（假识别器换成真派发之后，这一层以前完全没有测试覆盖）。
///
/// 覆盖：派发帧齐全 → 回包逐字段落库 → 无 Provider 立刻失败 → 等超时 → Provider 报错 →
/// 掉线把未完成的 job 立刻判失败 → `/info` 的合集与 AI 自述 → `/tasks/active` 真实快照 →
/// 普通连接收不到派发帧。
/// </summary>
public sealed class ProviderDispatchTests
{
    private const string SampleStem = "下列说法正确的是";
    private const string SampleMaterial = "材料：某工厂把 20% 的利润投入研发……";
    private const string SampleAnalysis = "解析：由材料可知选 A";
    private const string SampleOptionsJson = """[{"label":"A","text":"甲"},{"label":"B","text":"乙"}]""";
    private const string SampleChoiceJson = """["A"]""";
    private const string SampleWarningsJson = """["题干疑似截断"]""";

    private static async Task<(QuizSyncHost Host, HttpClient Client)> StartAsync(int analyzerTimeoutMs = ProviderAnalyzer.DefaultTimeoutMs)
    {
        var host = await QuizSyncHost.StartAsync(
            new ServerOptions
            {
                PreferredPort = 0,
                ServerName = "PROVIDER-HOST",
                DeviceId = "server-device-1",
                // AiConfigured 故意留 false：有 Provider 在线时 /info 必须用 Provider 的自述值。
            },
            clock: null,
            analyzerFactory: hub => new ProviderAnalyzer(hub.Providers, SystemClock.Instance, analyzerTimeoutMs));

        return (host, new HttpClient { BaseAddress = new Uri(host.BaseUrl), Timeout = TimeSpan.FromSeconds(20) });
    }

    private static async Task<string> PairAsync(HttpClient client, QuizSyncHost host, string deviceId)
    {
        var body = $$"""
            {"code":"{{host.Pairing.Code}}","device_id":"{{deviceId}}","device_name":"{{deviceId}}","platform":"windows","app_version":"2.0.0"}
            """;
        using var response = await client.PostAsync("/api/v1/pair", new StringContent(body, Encoding.UTF8, "application/json"));
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        return json["token"]!.GetValue<string>();
    }

    private static void Authorize(HttpClient client, string token) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    /// <summary>
    /// 建一个合集并选中它。合集**没有 HTTP 创建端点**（协议里靠同步 op 建），所以这里直接写库
    /// （与向量回放的 `seed` 同一条路），「选中」仍走真 HTTP —— 那正是 /info 断言要覆盖的路径。
    /// </summary>
    private static async Task SeedActiveCollectionAsync(QuizSyncHost host, HttpClient client, string id, string name)
    {
        using (SqliteCommand command = host.Database.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO collections (collection_id, name, created_at, updated_at, updated_by, lamport)
                VALUES ($id, $name, $now, $now, 'seed', 0)
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$now", host.Clock.NowMs);
            command.ExecuteNonQuery();
        }

        using var response = await client.PostAsync($"/api/v1/collections/{id}/select", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static byte[] FakeJpeg(int size)
    {
        var bytes = new byte[size];
        bytes[0] = 0xFF;
        bytes[1] = 0xD8;
        bytes[2] = 0xFF;
        bytes[3] = 0xD9;
        return bytes;
    }

    private static async Task<string> UploadAsync(HttpClient client, int size)
    {
        using var content = new MultipartFormDataContent();
        var part = new ByteArrayContent(FakeJpeg(size));
        part.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        content.Add(part, "file", "q.jpg");

        using var response = await client.PostAsync("/api/v1/images", content);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"上传失败：{(int)response.StatusCode} {text}");
        return JsonNode.Parse(text)!["image_hash"]!.GetValue<string>();
    }

    private static async Task<JsonObject> SubmitAsync(HttpClient client, string taskId, params string[] hashes)
    {
        var body = new JsonObject
        {
            ["task_id"] = taskId,
            ["image_hash"] = hashes[0],
            ["image_hashes"] = new JsonArray([.. hashes.Select(h => (JsonNode)JsonValue.Create(h)!)]),
            ["source_device"] = "android-device-1",
        };

        using var response = await client.PostAsync(
            "/api/v1/tasks", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        var text = await response.Content.ReadAsStringAsync();
        // 恒 202（协议）：失败也走 202 + 状态，只有校验错误才 4xx。
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, $"建任务返回 {(int)response.StatusCode}：{text}");
        return JsonNode.Parse(text)!.AsObject();
    }

    /// <summary>轮询任务视图直到指定状态（**不许只等一次**：任务是异步推进的）。</summary>
    private static async Task<JsonObject> WaitTaskAsync(
        HttpClient client, string taskId, string status, int maxMs = 20_000, int intervalMs = 50)
    {
        var deadline = Environment.TickCount64 + maxMs;
        JsonObject? last = null;
        while (Environment.TickCount64 < deadline)
        {
            // 不用 GetStringAsync：5xx 时它只报「500」，看不到服务端说了什么。
            using var response = await client.GetAsync($"/api/v1/tasks/{taskId}");
            var text = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new XunitException($"/tasks/{taskId} 返回 {(int)response.StatusCode}：{text}");
            }

            last = JsonNode.Parse(text)!.AsObject();
            if (last["status"]?.ToString() == status)
            {
                return last;
            }

            await Task.Delay(intervalMs);
        }

        throw new XunitException($"任务 {taskId} 没在 {maxMs} ms 内变成 {status}：{last?.ToJsonString()}");
    }

    private static async Task<JsonObject> WaitActiveAsync(HttpClient client, string status, int maxMs = 20_000)
    {
        var deadline = Environment.TickCount64 + maxMs;
        JsonObject? last = null;
        while (Environment.TickCount64 < deadline)
        {
            last = JsonNode.Parse(await client.GetStringAsync("/api/v1/tasks/active"))!.AsObject();
            if (last["status"]?.ToString() == status)
            {
                return last;
            }

            await Task.Delay(25);
        }

        throw new XunitException($"/tasks/active 没在 {maxMs} ms 内变成 {status}：{last?.ToJsonString()}");
    }

    private static JsonObject ProviderHello(
        string deviceId, bool aiConfigured = true,
        string? aiProvider = "openai-compatible", string? aiModel = "deepseek-chat") => new()
    {
        ["type"] = "hello",
        ["device_id"] = deviceId,
        ["app_version"] = "2.0.0",
        ["platform"] = "windows",
        ["role"] = "provider",
        ["ai_configured"] = aiConfigured,
        ["ai_provider"] = aiProvider,
        ["ai_model"] = aiModel,
    };

    /// <summary>Provider 要回的那道题：字段就是协议 `questions` 表的列名（**每次新建**，JsonNode 只能有一个父节点）。</summary>
    private static JsonObject SampleQuestion() => new()
    {
        ["ordinal"] = 0,
        ["question_no"] = "1",
        ["stem"] = SampleStem,
        ["material"] = SampleMaterial,
        ["type"] = "single",
        ["options_json"] = SampleOptionsJson,
        ["choice_json"] = SampleChoiceJson,
        ["answer_text"] = "A",
        ["analysis"] = SampleAnalysis,
        ["confidence"] = 0.92,
        ["need_review"] = 1,
        ["answer_in_image"] = 1,
        ["incomplete"] = 1,
        ["answer_guessed"] = 1,
        ["warnings_json"] = SampleWarningsJson,
    };

    private static JsonObject RecognitionResult(string jobId)
    {
        return new JsonObject
        {
            ["type"] = "recognition_result",
            ["job_id"] = jobId,
            ["ok"] = true,
            ["ai_provider"] = "openai-compatible",
            ["ai_model"] = "deepseek-chat",
            ["latency_ms"] = 1234,
            ["from_cache"] = false,
            ["questions"] = new JsonArray(SampleQuestion()),
        };
    }

    private static JsonObject RecognitionFailure(string jobId, string errorCode, string errorMessage) => new()
    {
        ["type"] = "recognition_result",
        ["job_id"] = jobId,
        ["ok"] = false,
        ["error_code"] = errorCode,
        ["error_message"] = errorMessage,
    };

    [Fact]
    public async Task Provider_receives_the_job_and_every_question_column_survives_the_round_trip()
    {
        var (host, client) = await StartAsync();
        await using (host)
        using (client)
        {
            var providerToken = await PairAsync(client, host, "windows-provider-1");
            Authorize(client, await PairAsync(client, host, "android-device-1"));
            // 选中合集走**真 HTTP**（要 Bearer，所以必须排在配对之后）。
            await SeedActiveCollectionAsync(host, client, "c-prov", "识别用合集");

            await using var provider = await TestWs.ConnectAsync(
                host.BaseUrl, providerToken, ProviderHello("windows-provider-1"));

            // 注册就要广播 provider_online（Provider 自己也在广播范围内）。
            var online = await provider.ExpectAsync("provider_online");
            Assert.True(online["online"]!.GetValue<bool>());
            Assert.True(online["ai_configured"]!.GetValue<bool>());
            Assert.Equal("deepseek-chat", online["ai_model"]!.GetValue<string>());

            var hash = await UploadAsync(client, 2048);
            var submitted = await SubmitAsync(client, "t-prov-1", hash);
            var sessionId = submitted["session_id"]!.GetValue<string>();

            var job = await provider.ExpectAsync("recognition_job");
            Assert.Equal("t-prov-1", job["task_id"]!.GetValue<string>());
            Assert.Equal(sessionId, job["session_id"]!.GetValue<string>());
            Assert.Equal("c-prov", job["collection_id"]!.GetValue<string>());
            Assert.Equal([hash], job["image_hashes"]!.AsArray().Select(n => n!.GetValue<string>()));
            Assert.Equal(ProviderAnalyzer.DefaultTimeoutMs, job["deadline_ms"]!.GetValue<int>());
            Assert.True(job["created_at"]!.GetValue<long>() > 0, "created_at 必须是毫秒时间戳");

            var jobId = job["job_id"]!.GetValue<string>();
            Assert.True(Guid.TryParse(jobId, out var parsedJob) && parsedJob.Version == 4, $"job_id 不是 UUID v4：{jobId}");

            await provider.SendAsync(RecognitionResult(jobId));

            var view = await WaitTaskAsync(client, "t-prov-1", "done");
            var session = view["session"]!.AsObject();
            var question = session["questions"]!.AsArray()[0]!.AsObject();

            // 逐字段读回：这几列以前**根本没写进库**，手机端标绿 / 材料折叠 / 标黄全靠它们。
            Assert.Equal(0, question["ordinal"]!.GetValue<long>());
            Assert.Equal("1", question["question_no"]!.GetValue<string>());
            Assert.Equal(SampleStem, question["stem"]!.GetValue<string>());
            Assert.Equal(SampleMaterial, question["material"]!.GetValue<string>());
            // 原样落库，不做映射（协议取值是 single|multi|judge|blank|subjective）。
            Assert.Equal("single", question["type"]!.GetValue<string>());
            Assert.Equal(SampleOptionsJson, question["options_json"]!.GetValue<string>());
            Assert.Equal(SampleChoiceJson, question["choice_json"]!.GetValue<string>());
            Assert.Equal("A", question["answer_text"]!.GetValue<string>());
            Assert.Equal(SampleAnalysis, question["analysis"]!.GetValue<string>());
            Assert.Equal(0.92, question["confidence"]!.GetValue<double>(), 3);
            Assert.Equal(1, question["need_review"]!.GetValue<long>());
            Assert.Equal(1, question["answer_in_image"]!.GetValue<long>());
            Assert.Equal(1, question["incomplete"]!.GetValue<long>());
            Assert.Equal(1, question["answer_guessed"]!.GetValue<long>());
            Assert.Equal(SampleWarningsJson, question["warnings_json"]!.GetValue<string>());
            Assert.Equal(sessionId, question["session_id"]!.GetValue<string>());

            // 会话级元信息：手机端历史里的「模型 / 耗时」就是这几列。
            Assert.Equal("openai-compatible", session["ai_provider"]!.GetValue<string>());
            Assert.Equal("deepseek-chat", session["ai_model"]!.GetValue<string>());
            Assert.Equal(1234, session["latency_ms"]!.GetValue<long>());
            Assert.Equal(1, session["question_count"]!.GetValue<long>());
            Assert.Equal([hash], session["image_hashes"]!.AsArray().Select(n => n!.GetValue<string>()));
        }
    }

    [Fact]
    public async Task No_provider_fails_immediately_with_provider_offline()
    {
        // 时钟用**默认的 90 秒**：这条用例要证明的是「没有 Provider 时立刻失败」，
        // 而不是「等满 deadline 之后报个别的原因」。
        var (host, client) = await StartAsync();
        await using (host)
        using (client)
        {
            Authorize(client, await PairAsync(client, host, "android-device-1"));
            await SeedActiveCollectionAsync(host, client, "c-offline", "离线合集");

            var hash = await UploadAsync(client, 1024);
            var started = Stopwatch.StartNew();
            await SubmitAsync(client, "t-offline", hash);
            var view = await WaitTaskAsync(client, "t-offline", "failed", intervalMs: 25);
            started.Stop();

            Assert.Equal("provider_offline", view["error_code"]!.GetValue<string>());
            // 会话上也要有同一个码（只写 error_message 的话，手机存下来的是「没有 error_code 的失败」）。
            Assert.Equal("provider_offline", view["session"]!["error_code"]!.GetValue<string>());
            Assert.True(
                started.ElapsedMilliseconds <= 2000,
                $"没有 Provider 时必须立刻失败，实测耗时 {started.ElapsedMilliseconds} ms（接近 deadline 说明在干等）");
        }
    }

    [Fact]
    public async Task Silent_provider_times_out_with_ai_timeout()
    {
        var (host, client) = await StartAsync(analyzerTimeoutMs: 300);
        await using (host)
        using (client)
        {
            var providerToken = await PairAsync(client, host, "windows-provider-1");
            Authorize(client, await PairAsync(client, host, "android-device-1"));
            await SeedActiveCollectionAsync(host, client, "c-timeout", "超时合集");

            await using var provider = await TestWs.ConnectAsync(
                host.BaseUrl, providerToken, ProviderHello("windows-provider-1"));

            var hash = await UploadAsync(client, 1024);
            await SubmitAsync(client, "t-timeout", hash);

            // 收到作业但**一声不吭**（模拟 Provider 卡住）。
            var job = await provider.ExpectAsync("recognition_job");
            Assert.Equal(300, job["deadline_ms"]!.GetValue<int>());

            var view = await WaitTaskAsync(client, "t-timeout", "failed");
            Assert.Equal("ai_timeout", view["error_code"]!.GetValue<string>());
            Assert.Equal("ai_timeout", view["session"]!["error_code"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task Provider_reported_auth_failure_lands_on_both_task_and_session()
    {
        var (host, client) = await StartAsync();
        await using (host)
        using (client)
        {
            var providerToken = await PairAsync(client, host, "windows-provider-1");
            Authorize(client, await PairAsync(client, host, "android-device-1"));
            await SeedActiveCollectionAsync(host, client, "c-authfail", "鉴权失败合集");

            await using var provider = await TestWs.ConnectAsync(
                host.BaseUrl, providerToken, ProviderHello("windows-provider-1"));

            var hash = await UploadAsync(client, 1024);
            await SubmitAsync(client, "t-authfail", hash);
            var job = await provider.ExpectAsync("recognition_job");
            await provider.SendAsync(RecognitionFailure(
                job["job_id"]!.GetValue<string>(), "ai_auth", "API key 无效或已过期"));

            var view = await WaitTaskAsync(client, "t-authfail", "failed");
            Assert.Equal("ai_auth", view["error_code"]!.GetValue<string>());
            Assert.Equal("ai_auth", view["session"]!["error_code"]!.GetValue<string>());
            Assert.Equal("API key 无效或已过期", view["error_message"]!.GetValue<string>());
            Assert.Equal("API key 无效或已过期", view["session"]!["error_message"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task Unknown_provider_error_code_falls_back_to_internal()
    {
        // 码表（协议 §3.4）之外的码必须落成 internal：否则手机拿到一个它不认识的码，
        // 按码给文案的分支全部落空。
        var (host, client) = await StartAsync();
        await using (host)
        using (client)
        {
            var providerToken = await PairAsync(client, host, "windows-provider-1");
            Authorize(client, await PairAsync(client, host, "android-device-1"));
            await SeedActiveCollectionAsync(host, client, "c-unknown", "未知错误合集");

            await using var provider = await TestWs.ConnectAsync(
                host.BaseUrl, providerToken, ProviderHello("windows-provider-1"));

            var hash = await UploadAsync(client, 1024);
            await SubmitAsync(client, "t-unknown", hash);
            var job = await provider.ExpectAsync("recognition_job");
            await provider.SendAsync(RecognitionFailure(
                job["job_id"]!.GetValue<string>(), "something_new", "端侧没见过的失败"));

            var view = await WaitTaskAsync(client, "t-unknown", "failed");
            Assert.Equal("internal", view["error_code"]!.GetValue<string>());
            Assert.Equal("端侧没见过的失败", view["session"]!["error_message"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task Provider_disconnect_fails_the_job_at_once_and_broadcasts_offline()
    {
        var (host, client) = await StartAsync();
        await using (host)
        using (client)
        {
            var providerToken = await PairAsync(client, host, "windows-provider-1");
            var androidToken = await PairAsync(client, host, "android-device-1");
            Authorize(client, androidToken);
            await SeedActiveCollectionAsync(host, client, "c-drop", "掉线合集");

            // 普通客户端：用来看 provider_online 的广播（它对未知 type 必须静默丢弃）。
            await using var android = await TestWs.ConnectAsync(host.BaseUrl, androidToken);
            await android.ExpectAsync("hello");

            var provider = await TestWs.ConnectAsync(host.BaseUrl, providerToken, ProviderHello("windows-provider-1"));
            Assert.True((await android.ExpectAsync("provider_online"))["online"]!.GetValue<bool>());

            var hash = await UploadAsync(client, 1024);
            await SubmitAsync(client, "t-drop", hash);
            await provider.ExpectAsync("recognition_job");

            // Provider 在识别中直接拔线（不回包）。
            var started = Stopwatch.StartNew();
            await provider.DisposeAsync();
            var view = await WaitTaskAsync(client, "t-drop", "failed", intervalMs: 25);
            started.Stop();

            Assert.Equal("provider_offline", view["error_code"]!.GetValue<string>());
            Assert.True(
                started.ElapsedMilliseconds <= 2000,
                $"掉线要立刻判失败，实测 {started.ElapsedMilliseconds} ms（接近 90 秒说明在干等 deadline）");

            var offline = await android.ExpectAsync("provider_online");
            Assert.False(offline["online"]!.GetValue<bool>());
            Assert.Null(offline["ai_model"]);
        }
    }

    [Fact]
    public async Task Plain_client_never_receives_recognition_job()
    {
        var (host, client) = await StartAsync();
        await using (host)
        using (client)
        {
            var providerToken = await PairAsync(client, host, "windows-provider-1");
            var androidToken = await PairAsync(client, host, "android-device-1");
            Authorize(client, androidToken);
            await SeedActiveCollectionAsync(host, client, "c-plain", "普通客户端合集");

            // 普通客户端：`hello` **没有 role**（老客户端本来就长这样）——行为必须一字不变。
            await using var android = await TestWs.ConnectAsync(host.BaseUrl, androidToken, new JsonObject
            {
                ["type"] = "hello",
                ["device_id"] = "android-device-1",
                ["app_version"] = "1.0.0",
                ["platform"] = "android",
            });

            await using var provider = await TestWs.ConnectAsync(
                host.BaseUrl, providerToken, ProviderHello("windows-provider-1"));

            // 先确认这条普通连接**确实在收广播**（Provider 注册会广播 provider_online），
            // 再谈「它没收到 recognition_job」——否则「没收到」可能只是连接还没建好。
            Assert.True((await android.ExpectAsync("provider_online"))["online"]!.GetValue<bool>());

            var hash = await UploadAsync(client, 1024);
            await SubmitAsync(client, "t-plain", hash);

            // 作业只发给 Provider；普通客户端收到的是进度广播（先拿到它，证明这条连接是活的）。
            var job = await provider.ExpectAsync("recognition_job");
            try
            {
                Assert.Equal("task_update", (await android.ExpectAsync("task_update"))["type"]!.GetValue<string>());
            }
            catch (XunitException ex)
            {
                // 这条用例在全量并发跑测时偶发（100 次里约 1 次）：把「连接还在不在」一起报出来，
                // 否则只能看到「没等到帧」，分不清是「广播丢了」还是「连接早断了」。
                throw new XunitException(
                    $"{ex.Message}\nandroid 连接状态={android.State}；主机侧在线设备=[{string.Join("、", host.Hub.ConnectedDeviceIds)}]");
            }

            await provider.SendAsync(RecognitionResult(job["job_id"]!.GetValue<string>()));
            await WaitTaskAsync(client, "t-plain", "done");
            await android.ExpectAsync("task_result");

            Assert.False(android.Has("recognition_job"), "普通连接不该收到 recognition_job");
        }
    }

    [Fact]
    public async Task A_second_provider_takes_over_and_the_first_stops_receiving_jobs()
    {
        var (host, client) = await StartAsync();
        await using (host)
        using (client)
        {
            var firstToken = await PairAsync(client, host, "windows-provider-1");
            var secondToken = await PairAsync(client, host, "windows-provider-2");
            Authorize(client, await PairAsync(client, host, "android-device-1"));
            await SeedActiveCollectionAsync(host, client, "c-takeover", "顶替合集");

            await using var first = await TestWs.ConnectAsync(
                host.BaseUrl, firstToken, ProviderHello("windows-provider-1"));
            Assert.True((await first.ExpectAsync("provider_online"))["online"]!.GetValue<bool>());

            // 第二个 Provider 顶掉第一个：登记换成新的，模型自述也跟着换。
            await using var second = await TestWs.ConnectAsync(
                host.BaseUrl, secondToken, ProviderHello("windows-provider-2", aiModel: "gpt-4o"));
            var online = await second.ExpectAsync("provider_online");
            Assert.True(online["online"]!.GetValue<bool>());
            Assert.Equal("gpt-4o", online["ai_model"]!.GetValue<string>());

            // 顶替是**静默**的：旧连接不断开、也不该看到「掉线」（它只是不再收 job）。
            Assert.DoesNotContain(
                first.Received("provider_online"),
                message => !message["online"]!.GetValue<bool>());

            var hash = await UploadAsync(client, 1024);
            await SubmitAsync(client, "t-takeover", hash);

            var job = await second.ExpectAsync("recognition_job");
            await second.SendAsync(RecognitionResult(job["job_id"]!.GetValue<string>()));
            await WaitTaskAsync(client, "t-takeover", "done");

            Assert.False(first.Has("recognition_job"), "被顶掉的 Provider 不该再收到 job");
        }
    }

    [Fact]
    public async Task Reconnecting_the_same_provider_device_keeps_it_online()
    {
        var (host, client) = await StartAsync();
        await using (host)
        using (client)
        {
            var providerToken = await PairAsync(client, host, "windows-provider-1");
            var androidToken = await PairAsync(client, host, "android-device-1");
            Authorize(client, androidToken);
            await SeedActiveCollectionAsync(host, client, "c-reconnect", "重连合集");

            await using var android = await TestWs.ConnectAsync(host.BaseUrl, androidToken);
            await android.ExpectAsync("hello");

            // 同一 device_id 再连一次：服务端踢掉旧连接（每个 device_id 只留一条）。
            var stale = await TestWs.ConnectAsync(host.BaseUrl, providerToken, ProviderHello("windows-provider-1"));
            Assert.True((await android.ExpectAsync("provider_online"))["online"]!.GetValue<bool>());
            await using var fresh = await TestWs.ConnectAsync(
                host.BaseUrl, providerToken, ProviderHello("windows-provider-1"));
            await stale.DisposeAsync();

            // 给「旧连接迟到的关闭」留出跑到服务端的时间：**它绝不能把新 Provider 判成掉线**
            // （注销只在「当前登记的仍是那条连接」时生效，与 Hub.Remove 同一条纪律）。
            //
            // 注意：重连过程中出现一条 `online:false` 是**正常**的（旧连接被踢掉到新连接登记之间，
            // 确实没有 Provider），所以这里断言的是**终态**：主机认为在线、手机最后听到的也是在线。
            await Task.Delay(400);
            Assert.True(
                (JsonNode.Parse(await client.GetStringAsync("/api/v2/info"))!)["provider_online"]!.GetValue<bool>(),
                "重连之后主机必须仍认为 Provider 在线");
            var onlineFrames = android.Received("provider_online");
            Assert.NotEmpty(onlineFrames);
            Assert.True(
                onlineFrames[^1]["online"]!.GetValue<bool>(),
                "重连之后手机最后听到的必须是「在线」");

            // 而且真的派发得出去（主机若把它判掉线，这里会落 provider_offline）。
            var hash = await UploadAsync(client, 1024);
            await SubmitAsync(client, "t-reconnect", hash);
            var job = await fresh.ExpectAsync("recognition_job");
            await fresh.SendAsync(RecognitionResult(job["job_id"]!.GetValue<string>()));
            Assert.Equal("done", (await WaitTaskAsync(client, "t-reconnect", "done"))["status"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task Info_reports_the_selected_collection_and_the_online_providers_ai_state()
    {
        var (host, client) = await StartAsync();
        await using (host)
        using (client)
        {
            // 还没有合集、也没有 Provider：合集字段是 null，ai_configured 退回启动参数（false）。
            var before = JsonNode.Parse(await client.GetStringAsync("/api/v1/info"))!;
            Assert.Null(before["active_collection_id"]);
            Assert.Null(before["active_collection_name"]);
            Assert.False(before["ai_configured"]!.GetValue<bool>());

            // 配一个设备并把客户端授权好（选中合集要 Bearer），但**不连 Provider**。
            var providerToken = await PairAsync(client, host, "windows-provider-1");
            Authorize(client, providerToken);
            await SeedActiveCollectionAsync(host, client, "c-info", "期末复习");

            var after = JsonNode.Parse(await client.GetStringAsync("/api/v1/info"))!;
            Assert.Equal("c-info", after["active_collection_id"]!.GetValue<string>());
            Assert.Equal("期末复习", after["active_collection_name"]!.GetValue<string>());

            var v2 = JsonNode.Parse(await client.GetStringAsync("/api/v2/info"))!;
            Assert.Equal("c-info", v2["active_collection_id"]!.GetValue<string>());
            Assert.False(v2["provider_online"]!.GetValue<bool>());

            // Provider 自述「已配 AI」→ /info 跟着它（主机自己不知道自己有没有 key）。
            var provider = await TestWs.ConnectAsync(host.BaseUrl, providerToken, ProviderHello("windows-provider-1"));
            Assert.True((await WaitInfoAiConfiguredAsync(client, expected: true))["ai_configured"]!.GetValue<bool>());
            Assert.True((JsonNode.Parse(await client.GetStringAsync("/api/v2/info"))!)["provider_online"]!.GetValue<bool>());
            Assert.Equal("deepseek-chat", (JsonNode.Parse(await client.GetStringAsync("/api/v2/info"))!)["provider_model"]!.GetValue<string>());

            // 掉线后退回启动参数（false）。
            await provider.DisposeAsync();
            Assert.False((await WaitInfoAiConfiguredAsync(client, expected: false))["ai_configured"]!.GetValue<bool>());
        }
    }

    /// <summary>`/info` 是**没有缓存**的只读端点，但注册广播与它之间没有顺序保证，所以重试到期望值。</summary>
    private static async Task<JsonObject> WaitInfoAiConfiguredAsync(HttpClient client, bool expected, int maxMs = 10_000)
    {
        var deadline = Environment.TickCount64 + maxMs;
        JsonObject? last = null;
        while (Environment.TickCount64 < deadline)
        {
            last = JsonNode.Parse(await client.GetStringAsync("/api/v1/info"))!.AsObject();
            if (last["ai_configured"]!.GetValue<bool>() == expected)
            {
                return last;
            }

            await Task.Delay(25);
        }

        throw new XunitException($"/info 的 ai_configured 没在 {maxMs} ms 内变成 {expected}：{last?.ToJsonString()}");
    }

    [Fact]
    public async Task Active_task_snapshot_follows_the_running_task()
    {
        var (host, client) = await StartAsync();
        await using (host)
        using (client)
        {
            var providerToken = await PairAsync(client, host, "windows-provider-1");
            Authorize(client, await PairAsync(client, host, "android-device-1"));
            await SeedActiveCollectionAsync(host, client, "c-active", "进行中合集");

            // 没有进行中的任务时是 idle（合集与 ops 水位仍要给）。
            var idle = JsonNode.Parse(await client.GetStringAsync("/api/v1/tasks/active"))!.AsObject();
            Assert.Equal("idle", idle["status"]!.GetValue<string>());
            Assert.Equal("c-active", idle["active_collection_id"]!.GetValue<string>());
            Assert.Single(idle["collections"]!.AsArray());
            Assert.NotNull(idle["ops_lamport"]);

            await using var provider = await TestWs.ConnectAsync(
                host.BaseUrl, providerToken, ProviderHello("windows-provider-1"));

            var hash1 = await UploadAsync(client, 1024);
            var hash2 = await UploadAsync(client, 2048);
            await SubmitAsync(client, "t-active", hash1, hash2);
            var job = await provider.ExpectAsync("recognition_job");

            // 识别中（Provider 还没回）：状态不再是 idle，页数必须等于页序长度。
            var analyzing = await WaitActiveAsync(client, "analyzing");
            Assert.Equal("t-active", analyzing["task_id"]!.GetValue<string>());
            Assert.Equal(job["session_id"]!.GetValue<string>(), analyzing["session_id"]!.GetValue<string>());
            Assert.Equal(2, analyzing["image_count"]!.GetValue<int>());
            Assert.Equal("c-active", analyzing["active_collection_id"]!.GetValue<string>());
            Assert.True(analyzing["updated_at"]!.GetValue<long>() > 0);
            Assert.Null(analyzing["session"]);

            await provider.SendAsync(RecognitionResult(job["job_id"]!.GetValue<string>()));

            var done = await WaitActiveAsync(client, "done");
            Assert.Equal(2, done["image_count"]!.GetValue<int>());
            Assert.NotNull(done["session"]);
            Assert.Equal(1, done["session"]!["question_count"]!.GetValue<long>());
            Assert.Equal(job["session_id"]!.GetValue<string>(), done["session"]!["session_id"]!.GetValue<string>());

            // 快照是内存态、且**可以重复读**：同一份 session 节点挂进第二个响应体会抛异常
            // （表现为空体 500），所以这里连读两次。
            var again = await WaitActiveAsync(client, "done");
            Assert.Equal(1, again["session"]!["question_count"]!.GetValue<long>());
        }
    }
}
