using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using QuizSync.Server.Core;

namespace QuizSync.Server.Tests;

/// <summary>
/// 本机控制面（**不属于 LAN 协议**）：CLI 靠它读配对码 / 关服务。
/// 规矩：只服务回环地址，而且要带对控制令牌。
/// </summary>
public sealed class ControlPlaneTests
{
    private static async Task<QuizSyncHost> StartAsync(string? controlToken)
    {
        return await QuizSyncHost.StartAsync(new ServerOptions
        {
            PreferredPort = 0,
            ServerName = "TEST-HOST",
            DeviceId = "server-device-1",
            ControlToken = controlToken,
        });
    }

    [Fact]
    public async Task Pair_code_requires_the_control_token()
    {
        var host = await StartAsync("a" + new string('b', 63));
        await using (host)
        using (var client = new HttpClient { BaseAddress = new Uri(host.BaseUrl) })
        {
            var noToken = await client.GetAsync("/api/v1/pair/code");
            Assert.Equal(HttpStatusCode.Forbidden, noToken.StatusCode);
            var body = JsonSerializer.Deserialize<JsonElement>(await noToken.Content.ReadAsStringAsync());
            Assert.Equal("forbidden", body.GetProperty("code").GetString());

            client.DefaultRequestHeaders.Add("X-QS-Control", "wrong");
            var wrong = await client.GetAsync("/api/v1/pair/code");
            Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        }
    }

    [Fact]
    public async Task Pair_code_returns_the_live_code_and_pair_uri()
    {
        const string token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var host = await StartAsync(token);
        await using (host)
        using (var client = new HttpClient { BaseAddress = new Uri(host.BaseUrl) })
        {
            client.DefaultRequestHeaders.Add("X-QS-Control", token);
            var json = await client.GetFromJsonAsync<JsonElement>("/api/v1/pair/code");
            Assert.Equal(host.Pairing.Code, json.GetProperty("code").GetString());
            Assert.Equal("server-device-1", json.GetProperty("device_id").GetString());
            Assert.Contains($"code={host.Pairing.Code}", json.GetProperty("pair_uri").GetString()!, StringComparison.Ordinal);
            Assert.Equal(host.Port, json.GetProperty("port").GetInt32());
        }
    }

    [Fact]
    public async Task Control_plane_is_unavailable_when_no_token_is_configured()
    {
        var host = await StartAsync(null);
        await using (host)
        using (var client = new HttpClient { BaseAddress = new Uri(host.BaseUrl) })
        {
            client.DefaultRequestHeaders.Add("X-QS-Control", "whatever");
            var resp = await client.GetAsync("/api/v1/pair/code");
            Assert.Equal(HttpStatusCode.NotImplemented, resp.StatusCode);
        }
    }

    [Fact]
    public async Task Refresh_rotates_the_code_and_keeps_it_six_digits()
    {
        const string token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var host = await StartAsync(token);
        await using (host)
        using (var client = new HttpClient { BaseAddress = new Uri(host.BaseUrl) })
        {
            client.DefaultRequestHeaders.Add("X-QS-Control", token);
            var before = host.Pairing.Code;
            var json = await (await client.PostAsync("/api/v1/pair/code/refresh", null))
                .Content.ReadFromJsonAsync<JsonElement>();
            var after = json.GetProperty("code").GetString()!;
            Assert.Matches("^[0-9]{6}$", after);
            Assert.NotEqual(before, after);
            Assert.Equal(host.Pairing.Code, after);
        }
    }

    [Fact]
    public async Task Control_plane_endpoints_do_not_need_a_device_bearer()
    {
        // 否则 CLI 得先配对才能读配对码 —— 死循环。
        const string token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var host = await StartAsync(token);
        await using (host)
        using (var client = new HttpClient { BaseAddress = new Uri(host.BaseUrl) })
        {
            client.DefaultRequestHeaders.Add("X-QS-Control", token);
            var resp = await client.GetAsync("/api/v1/pair/code");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }
    }
}
