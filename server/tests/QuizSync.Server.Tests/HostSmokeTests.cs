using System.Net.Http.Json;
using System.Text.Json;
using QuizSync.Server.Core;

namespace QuizSync.Server.Tests;

/// <summary>
/// 回环集成测试：起一个**真的** Host（端口 0 → 系统分配），用真 HTTP 打它。
/// 这是 Phase 2 起所有 Server 行为的验收方式（见 src/README.md）。
/// </summary>
public sealed class HostSmokeTests
{
    private static async Task<(QuizSyncHost Host, HttpClient Client)> StartAsync()
    {
        var host = await QuizSyncHost.StartAsync(new ServerOptions
        {
            PreferredPort = 0,
            ServerName = "TEST-HOST",
            AppVersion = "2.0.0",
        });
        return (host, new HttpClient { BaseAddress = new Uri(host.BaseUrl) });
    }

    [Fact]
    public async Task Health_returns_ok_with_protocol_version()
    {
        var (host, client) = await StartAsync();
        await using (host)
        using (client)
        {
            var resp = await client.GetAsync(new Uri("/health", UriKind.Relative));
            Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
            var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("ok", json.GetProperty("status").GetString());
            Assert.Equal(ServerOptions.ProtocolVersion, json.GetProperty("protocol_version").GetInt32());
        }
    }

    [Fact]
    public async Task V2Info_reports_protocol_2_and_v1_compat()
    {
        var (host, client) = await StartAsync();
        await using (host)
        using (client)
        {
            var json = await client.GetFromJsonAsync<JsonElement>("/api/v2/info");
            Assert.Equal(ServerOptions.ProtocolVersion, json.GetProperty("protocol_version").GetInt32());
            Assert.Equal("TEST-HOST", json.GetProperty("device_name").GetString());
            Assert.True(json.GetProperty("v1_compat").GetBoolean());
        }
    }

    [Fact]
    public async Task V1Info_compat_shim_keeps_v1_field_names_and_version()
    {
        var (host, client) = await StartAsync();
        await using (host)
        using (client)
        {
            var json = await client.GetFromJsonAsync<JsonElement>("/api/v1/info");
            // v1 客户端按 protocol_version=1 判分支，这里必须仍是 1。
            Assert.Equal(ServerOptions.V1ProtocolVersion, json.GetProperty("protocol_version").GetInt32());
            Assert.Equal("windows", json.GetProperty("platform").GetString());
            var caps = json.GetProperty("capabilities").EnumerateArray().Select(x => x.GetString()).ToList();
            Assert.Contains("analyze", caps);
            Assert.Contains("sync", caps);
        }
    }

    [Fact]
    public async Task Preferred_port_zero_picks_a_free_port_and_reports_it()
    {
        var (host, client) = await StartAsync();
        await using (host)
        using (client)
        {
            Assert.InRange(host.Port, 1024, 65535);
            Assert.Contains(host.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), host.BaseUrl, StringComparison.Ordinal);
        }
    }
}
