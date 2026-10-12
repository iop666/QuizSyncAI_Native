using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using QuizSync.Server.Core;
using QuizSync.Server.Core.Storage;

namespace QuizSync.Server.Tests;

/// <summary>
/// 本地库的**并发**访问：全进程共用一条 SQLite 连接（Kestrel 请求线程 + 任务队列线程 +
/// WS 读循环），而 Microsoft.Data.Sqlite 的 `SqliteConnection` 内部用 `List&lt;SqliteCommand&gt;`
/// 记账、`SqliteCommand` 的构造与 `Dispose` 都会改那个列表 —— **那套记账不是线程安全的**。
///
/// 实测（修之前，在本机跑全量测试）：约 5 次里有 1–2 次随机某个请求 500，异常是
/// <c>ArgumentOutOfRangeException: Index was out of range ... at SqliteConnection.RemoveCommand</c>。
/// 所以建命令一律走 <see cref="QuizSyncDatabase.CreateCommand"/>（建 + 释放都圈在同一把锁里）。
///
/// 这条用例就是它的回归：多线程同时建/用/放命令，任何一次异常都直接失败。
/// </summary>
public sealed class DatabaseConcurrencyTests
{
    [Fact]
    public async Task Concurrent_command_creation_and_disposal_never_corrupts_the_connection()
    {
        var host = await QuizSyncHost.StartAsync(new ServerOptions
        {
            PreferredPort = 0,
            ServerName = "CONCURRENCY-HOST",
            DeviceId = "server-device-1",
        });

        await using (host)
        {
            // 先造一行真实数据：读路径要走 SessionJson（它一次要建三条命令，窗口最大）。
            using (var seed = host.Database.CreateCommand())
            {
                seed.CommandText = """
                    INSERT INTO sessions (session_id, image_hash, source_device, status, question_count,
                                          cached, collection_id, created_at, updated_at, updated_by, lamport)
                    VALUES ('s-conc', 'h-conc', 'tester', 'done', 1, 0, 'c-conc', 1, 1, 'tester', 0)
                    """;
                seed.ExecuteNonQuery();
            }

            using (var question = host.Database.CreateCommand())
            {
                question.CommandText = """
                    INSERT INTO questions (question_id, session_id, ordinal, stem, type, created_at,
                                           updated_at, updated_by)
                    VALUES ('q-conc', 's-conc', 0, '并发用题', 'single', 1, 1, 'tester')
                    """;
                question.ExecuteNonQuery();
            }

            var failures = new List<Exception>();
            var workers = new List<Task>();
            for (var worker = 0; worker < 8; worker++)
            {
                workers.Add(Task.Run(() =>
                {
                    try
                    {
                        for (var i = 0; i < 250; i++)
                        {
                            // 读（含多命令的 SessionJson）、写、认证查设备，三条路径都要过一遍。
                            _ = host.Sessions.SessionJson("s-conc");
                            host.Devices.Touch("server-device-1", 1 + i);
                            _ = host.Devices.Find("server-device-1");
                            _ = host.TaskRows.QueuedCount();
                        }
                    }
                    catch (Exception ex)
                    {
                        lock (failures)
                        {
                            failures.Add(ex);
                        }
                    }
                }));
            }

            await Task.WhenAll(workers);

            Assert.True(
                failures.Count == 0,
                $"并发建/放命令时崩了 {failures.Count} 次，第一条：{failures.FirstOrDefault()}");
        }
    }

    [Fact]
    public async Task Two_clients_polling_while_a_task_runs_do_not_500()
    {
        // 端到端的同一条回归：HTTP 轮询（多个客户端）与任务队列同时访问同一条连接。
        var host = await QuizSyncHost.StartAsync(new ServerOptions
        {
            PreferredPort = 0,
            ServerName = "CONCURRENCY-HOST",
            DeviceId = "server-device-1",
        });

        await using (host)
        using (var client = new HttpClient { BaseAddress = new Uri(host.BaseUrl) })
        {
            var logger = host.Database.CreateCommand();
            using (logger)
            {
                logger.CommandText = """
                    INSERT INTO collections (collection_id, name, created_at, updated_at, updated_by, lamport)
                    VALUES ('c-conc', '并发合集', 1, 1, 'tester', 0)
                    """;
                logger.ExecuteNonQuery();
            }

            var pairBody = $$"""
                {"code":"{{host.Pairing.Code}}","device_id":"android-device-1","device_name":"Pixel 7","platform":"android","app_version":"1.0.0"}
                """;
            using var paired = await client.PostAsync(
                "/api/v1/pair", new StringContent(pairBody, Encoding.UTF8, "application/json"));
            var token = JsonNode.Parse(await paired.Content.ReadAsStringAsync())!["token"]!.GetValue<string>();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var select = await client.PostAsync("/api/v1/collections/c-conc/select", null);
            Assert.Equal(System.Net.HttpStatusCode.OK, select.StatusCode);

            using var upload = new MultipartFormDataContent();
            var file = new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xD9, 1, 2, 3, 4]);
            file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            upload.Add(file, "file", "q.jpg");
            using var uploaded = await client.PostAsync("/api/v1/images", upload);
            var hash = JsonNode.Parse(await uploaded.Content.ReadAsStringAsync())!["image_hash"]!.GetValue<string>();

            // 四个客户端同时轮询 /tasks/active 与 /info：每个请求都会建/放命令。
            var pollers = new List<Task>();
            for (var i = 0; i < 4; i++)
            {
                pollers.Add(Task.Run(async () =>
                {
                    for (var n = 0; n < 60; n++)
                    {
                        using var active = await client.GetAsync("/api/v1/tasks/active");
                        Assert.Equal(System.Net.HttpStatusCode.OK, active.StatusCode);
                        using var info = await client.GetAsync("/api/v1/info");
                        Assert.Equal(System.Net.HttpStatusCode.OK, info.StatusCode);
                    }
                }));
            }

            var taskBody = $$"""
                {"task_id":"t-conc","image_hash":"{{hash}}","source_device":"android-device-1"}
                """;
            using var submitted = await client.PostAsync(
                "/api/v1/tasks", new StringContent(taskBody, Encoding.UTF8, "application/json"));
            Assert.Equal(System.Net.HttpStatusCode.Accepted, submitted.StatusCode);

            await Task.WhenAll(pollers);
        }
    }
}
