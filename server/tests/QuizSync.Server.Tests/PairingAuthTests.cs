using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using QuizSync.Server.Core;

namespace QuizSync.Server.Tests;

/// <summary>
/// 配对与鉴权的行为测试 —— 与 `conformance/vectors/auth.ndjson` 逐条对应。
/// 真 Host（端口 0）+ 真 HTTP + **可控时钟**（配对码过期 / 限流窗口靠它推进）。
/// </summary>
public sealed class PairingAuthTests
{
    private static async Task<(QuizSyncHost Host, HttpClient Client, MutableClock Clock)> StartAsync()
    {
        var clock = new MutableClock();
        var host = await QuizSyncHost.StartAsync(
            new ServerOptions { PreferredPort = 0, ServerName = "TEST-HOST", AppVersion = "2.0.0" },
            clock);
        return (host, new HttpClient { BaseAddress = new Uri(host.BaseUrl) }, clock);
    }

    private static StringContent Json(string text) => new(text, Encoding.UTF8, "application/json");

    private static string PairBody(string code, string deviceId = "android-device-1") =>
        $$"""
        {"code":"{{code}}","device_id":"{{deviceId}}","device_name":"Pixel 7","platform":"android","app_version":"1.0.0"}
        """;

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    /// <summary>断言状态码，并把响应体带进失败信息（否则只知道码不对、不知道为什么）。</summary>
    private static async Task<JsonElement> ExpectAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == expected,
            $"期望 {(int)expected}，实际 {(int)response.StatusCode}：{text}");
        return JsonSerializer.Deserialize<JsonElement>(text);
    }

    private static void Authorize(HttpClient client, string token) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    [Fact]
    public async Task Pair_with_correct_code_returns_64_hex_token_and_works()
    {
        var (host, client, _) = await StartAsync();
        await using (host)
        using (client)
        {
            var json = await ExpectAsync(
                await client.PostAsync("/api/v1/pair", Json(PairBody(host.Pairing.Code))), HttpStatusCode.OK);
            var token = json.GetProperty("token").GetString()!;
            Assert.Matches("^[0-9a-f]{64}$", token);
            Assert.Equal("TEST-HOST", json.GetProperty("server_name").GetString());

            Authorize(client, token);
            var collections = await client.GetAsync("/api/v1/collections");
            Assert.Equal(HttpStatusCode.OK, collections.StatusCode);
        }
    }

    [Fact]
    public async Task Token_is_stored_as_sha256_only()
    {
        var (host, client, _) = await StartAsync();
        await using (host)
        using (client)
        {
            var resp = await client.PostAsync("/api/v1/pair", Json(PairBody(host.Pairing.Code)));
            var token = (await BodyAsync(resp)).GetProperty("token").GetString()!;

            var device = host.Devices.Find("android-device-1");
            Assert.NotNull(device);
            Assert.NotEqual(token, device!.TokenHash);
            Assert.Equal(Core.Tokens.Sha256Hex(token), device.TokenHash);
        }
    }

    [Fact]
    public async Task Missing_and_bogus_tokens_are_unauthorized()
    {
        var (host, client, _) = await StartAsync();
        await using (host)
        using (client)
        {
            var noToken = await client.PostAsync("/api/v1/tasks", Json("{}"));
            Assert.Equal(HttpStatusCode.Unauthorized, noToken.StatusCode);
            Assert.Equal("unauthorized", (await BodyAsync(noToken)).GetProperty("code").GetString());

            Authorize(client, new string('0', 64));
            var bogus = await client.PostAsync("/api/v1/tasks", Json("{}"));
            Assert.Equal(HttpStatusCode.Unauthorized, bogus.StatusCode);
            Assert.Equal("unauthorized", (await BodyAsync(bogus)).GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task Malformed_authorization_header_is_missing_not_invalid()
    {
        var (host, client, _) = await StartAsync();
        await using (host)
        using (client)
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Token abc");
            var resp = await client.GetAsync("/api/v1/collections");
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
            Assert.Equal("unauthorized", (await BodyAsync(resp)).GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task Wrong_code_is_invalid_code()
    {
        var (host, client, _) = await StartAsync();
        await using (host)
        using (client)
        {
            var wrong = host.Pairing.Code == "000000" ? "111111" : "000000";
            var resp = await client.PostAsync("/api/v1/pair", Json(PairBody(wrong)));
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
            Assert.Equal("invalid_code", (await BodyAsync(resp)).GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task Expired_code_is_code_expired_after_ttl()
    {
        var (host, client, clock) = await StartAsync();
        await using (host)
        using (client)
        {
            var code = host.Pairing.Code;
            clock.Advance(5 * 60 * 1000 + 1); // TTL 5 分钟
            var resp = await client.PostAsync("/api/v1/pair", Json(PairBody(code)));
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
            var json = await BodyAsync(resp);
            Assert.Equal("code_expired", json.GetProperty("code").GetString());
            // 现状语义：retry_after_seconds 是「已过期秒数」，不是「多久后可重试」。
            Assert.True(json.GetProperty("retry_after_seconds").GetInt32() >= 0);
        }
    }

    [Fact]
    public async Task Repairing_same_device_is_409_with_new_token_and_old_token_dies()
    {
        var (host, client, _) = await StartAsync();
        await using (host)
        using (client)
        {
            var first = (await BodyAsync(await client.PostAsync("/api/v1/pair", Json(PairBody(host.Pairing.Code)))))
                .GetProperty("token").GetString()!;

            var second = await client.PostAsync("/api/v1/pair", Json(PairBody(host.Pairing.Code)));
            Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
            var secondJson = await BodyAsync(second);
            Assert.True(secondJson.GetProperty("already_paired").GetBoolean());
            var newToken = secondJson.GetProperty("token").GetString()!;
            Assert.NotEqual(first, newToken);

            Authorize(client, first);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/collections")).StatusCode);

            Authorize(client, newToken);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/collections")).StatusCode);
        }
    }

    [Fact]
    public async Task Sixth_attempt_within_a_minute_is_rate_limited()
    {
        var (host, client, _) = await StartAsync();
        await using (host)
        using (client)
        {
            var wrong = host.Pairing.Code == "000000" ? "111111" : "000000";
            for (var i = 0; i < 5; i++)
            {
                var attempt = await client.PostAsync("/api/v1/pair", Json(PairBody(wrong)));
                Assert.Equal(HttpStatusCode.Unauthorized, attempt.StatusCode);
            }

            var sixth = await client.PostAsync("/api/v1/pair", Json(PairBody(wrong)));
            Assert.Equal(HttpStatusCode.TooManyRequests, sixth.StatusCode);
            var json = await BodyAsync(sixth);
            Assert.Equal("rate_limited", json.GetProperty("code").GetString());
            Assert.True(json.GetProperty("retry_after_seconds").GetInt32() > 0, "限流必须给出可重试秒数");
        }
    }

    [Fact]
    public async Task Revoked_device_gets_revoked_code()
    {
        var (host, client, _) = await StartAsync();
        await using (host)
        using (client)
        {
            var token = (await BodyAsync(await client.PostAsync("/api/v1/pair", Json(PairBody(host.Pairing.Code)))))
                .GetProperty("token").GetString()!;
            Authorize(client, token);

            var revoke = await client.DeleteAsync("/api/v1/devices/android-device-1");
            Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
            Assert.Equal("android-device-1", (await BodyAsync(revoke)).GetProperty("revoked").GetString());

            var after = await client.GetAsync("/api/v1/collections");
            Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
            Assert.Equal("revoked", (await BodyAsync(after)).GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task Device_list_never_leaks_token_hash()
    {
        var (host, client, _) = await StartAsync();
        await using (host)
        using (client)
        {
            Authorize(client, (await BodyAsync(await client.PostAsync("/api/v1/pair", Json(PairBody(host.Pairing.Code)))))
                .GetProperty("token").GetString()!);
            var text = await (await client.GetAsync("/api/v1/devices")).Content.ReadAsStringAsync();
            Assert.DoesNotContain("token_hash", text, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(null, HttpStatusCode.OK)]          // 不带版本头：放行
    [InlineData("1.9.9", HttpStatusCode.OK)]        // 同主版本（v1 兼容层固定以 1 协商）
    [InlineData("2.0.0", HttpStatusCode.UpgradeRequired)]
    [InlineData("0.9.0", HttpStatusCode.UpgradeRequired)]
    [InlineData("abc", HttpStatusCode.UpgradeRequired)] // 不可解析也算不符
    public async Task Client_version_negotiation_matches_v1(string? version, HttpStatusCode expected)
    {
        var (host, client, _) = await StartAsync();
        await using (host)
        using (client)
        {
            if (version is not null)
            {
                client.DefaultRequestHeaders.TryAddWithoutValidation("X-QS-Client-Version", version);
            }

            var resp = await client.GetAsync("/api/v1/info");
            Assert.Equal(expected, resp.StatusCode);
            if (expected == HttpStatusCode.UpgradeRequired)
            {
                Assert.Equal("version_mismatch", (await BodyAsync(resp)).GetProperty("code").GetString());
            }

            Assert.Equal("2.0.0", resp.Headers.GetValues("X-QS-Server-Version").Single());
        }
    }
}
