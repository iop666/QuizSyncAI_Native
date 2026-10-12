using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using QuizSync.Server.Core;
using QuizSync.Server.Core.Protocol;

namespace QuizSync.Server.Tests;

/// <summary>
/// 握手与版本协商。两处口径**不一样**，而且都必须真打一遍：
///
/// - `/ws` **不做版本协商**（`spec/05-websocket.md` §1.1）：主版本不一致也能建连，
///   也没有 `X-QS-Server-Version` 响应头 —— 「每个响应都带服务端版本」那条只针对 HTTP；
/// - HTTP 上**按路径选口径**：`/api/v1/*` 比 1（v1 冻结语义），`/api/v2/*` 比 2。
///
/// 由来（实测）：版本闸门原来跑在**每一个**请求上、且无条件拿 v1 的口径（1）去比，
/// 于是任何自述 2.x 的客户端连 `/ws` 必然拿到 426 ——
/// `The server returned status code '426' when status code '101' was expected.`
/// 1.x 客户端自述的是 1.0.0，所以这个握手拒绝一直没暴露，直到 2.0 的另一端第一次真连。
/// </summary>
public sealed class HandshakeVersionTests
{
    private static async Task<(QuizSyncHost Host, HttpClient Client)> StartAsync()
    {
        var host = await QuizSyncHost.StartAsync(new ServerOptions
        {
            PreferredPort = 0,
            ServerName = "HANDSHAKE-HOST",
            DeviceId = "server-device-1",
            AppVersion = "2.0.0",
        });
        return (host, new HttpClient { BaseAddress = new Uri(host.BaseUrl) });
    }

    private static async Task<string> PairAsync(HttpClient client, QuizSyncHost host, string deviceId)
    {
        var body = $$"""
            {"code":"{{host.Pairing.Code}}","device_id":"{{deviceId}}","device_name":"{{deviceId}}","platform":"windows","app_version":"2.0.0"}
            """;
        using var response = await client.PostAsync(
            "/api/v1/pair", new StringContent(body, Encoding.UTF8, "application/json"));
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!["token"]!.GetValue<string>();
    }

    [Fact]
    public async Task Ws_accepts_a_2x_client_and_does_not_negotiate_the_version()
    {
        var (host, client) = await StartAsync();
        await using (host)
        using (client)
        {
            var token = await PairAsync(client, host, "windows-provider-1");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            // 带 `X-QS-Client-Version: 2.0.0` 连 `/ws`：**必须建连成功**（426 会让 2.0 客户端全部连不上）。
            await using var socket = await TestWs.ConnectAsync(host.BaseUrl, token, clientVersion: "2.0.0");

            var hello = await socket.ExpectAsync("hello");
            Assert.Equal("server-device-1", hello["server_device_id"]!.GetValue<string>());
            // hello 自述的协议版本仍是**兼容层**的 1（老客户端只认 1）——与这里的版本闸门无关。
            Assert.Equal(ProtocolVersion.V1, hello["protocol_version"]!.GetValue<int>());

            Assert.Equal(HttpStatusCode.SwitchingProtocols, socket.HandshakeStatus);
            Assert.False(
                socket.HandshakeHasHeader("X-QS-Server-Version"),
                "WS 上不做版本协商，握手响应不该带 X-QS-Server-Version");
        }
    }

    [Fact]
    public async Task Http_version_gate_picks_the_major_by_path()
    {
        var (host, client) = await StartAsync();
        await using (host)
        using (client)
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation("X-QS-Client-Version", "2.0.0");

            // v1 路径上仍然 426（修 /ws 不许顺手放宽 v1 的冻结语义）。
            using var v1 = await client.GetAsync("/api/v1/info");
            Assert.Equal(HttpStatusCode.UpgradeRequired, v1.StatusCode);
            Assert.Equal(
                "version_mismatch",
                JsonNode.Parse(await v1.Content.ReadAsStringAsync())!["code"]!.GetValue<string>());
            // HTTP 上服务端版本头照旧要有（客户端做诊断用）。
            Assert.Equal("2.0.0", v1.Headers.GetValues("X-QS-Server-Version").Single());

            // v2 路径按 v2 的主版本比：同一个客户端的 2.0.0 在这里是**匹配**的。
            using var v2 = await client.GetAsync("/api/v2/info");
            Assert.Equal(HttpStatusCode.OK, v2.StatusCode);
        }
    }
}
