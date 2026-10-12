using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QuizSync.Server.Core;

namespace QuizSync.Server.Tests;

/// <summary>
/// 图片上传/下载 —— 与 `conformance/vectors/images.ndjson` 逐条对应。
/// </summary>
public sealed class ImageTests
{
    private static async Task<(QuizSyncHost Host, HttpClient Client)> StartAsync()
    {
        var host = await QuizSyncHost.StartAsync(new ServerOptions
        {
            PreferredPort = 0,
            ServerName = "TEST-HOST",
            AppVersion = "2.0.0",
        });
        var client = new HttpClient { BaseAddress = new Uri(host.BaseUrl) };
        var token = await PairAsync(host, client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (host, client);
    }

    private static async Task<string> PairAsync(QuizSyncHost host, HttpClient client)
    {
        var body = $$"""
            {"code":"{{host.Pairing.Code}}","device_id":"android-device-1","device_name":"Pixel 7","platform":"android","app_version":"1.0.0"}
            """;
        var resp = await client.PostAsync("/api/v1/pair", new StringContent(body, Encoding.UTF8, "application/json"));
        var json = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        return json.GetProperty("token").GetString()!;
    }

    private static byte[] FakeJpeg(int size)
    {
        var bytes = new byte[size];
        if (size >= 4)
        {
            bytes[0] = 0xFF;
            bytes[1] = 0xD8;
            bytes[2] = 0xFF;
            bytes[3] = 0xD9;
        }

        return bytes;
    }

    private static MultipartFormDataContent Multipart(byte[] file, string filename = "a.jpg")
    {
        var content = new MultipartFormDataContent();
        var part = new ByteArrayContent(file);
        part.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        content.Add(part, "file", filename);
        return content;
    }

    private static async Task<JsonElement> ExpectAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"期望 {(int)expected}，实际 {(int)response.StatusCode}：{text}");
        return text.Length == 0 ? default : JsonSerializer.Deserialize<JsonElement>(text);
    }

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    [Fact]
    public async Task Upload_then_download_round_trips_by_content_hash()
    {
        var (host, client) = await StartAsync();
        await using (host)
        using (client)
        {
            var bytes = FakeJpeg(1024);
            var first = await ExpectAsync(await client.PostAsync("/api/v1/images", Multipart(bytes)), HttpStatusCode.OK);
            var hash = first.GetProperty("image_hash").GetString()!;
            Assert.Equal(Sha256Hex(bytes), hash);
            Assert.Equal(1024, first.GetProperty("size").GetInt32());
            Assert.False(first.GetProperty("existed").GetBoolean());

            var download = await client.GetAsync($"/api/v1/images/{hash}");
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            Assert.Equal("image/jpeg", download.Content.Headers.ContentType!.MediaType);
            Assert.Equal(bytes, await download.Content.ReadAsByteArrayAsync());
        }
    }

    [Fact]
    public async Task Same_bytes_upload_again_is_existed_true_with_same_hash()
    {
        var (host, client) = await StartAsync();
        await using (host)
        using (client)
        {
            var bytes = FakeJpeg(2048);
            var first = await ExpectAsync(await client.PostAsync("/api/v1/images", Multipart(bytes)), HttpStatusCode.OK);
            var second = await ExpectAsync(await client.PostAsync("/api/v1/images", Multipart(bytes)), HttpStatusCode.OK);
            Assert.True(second.GetProperty("existed").GetBoolean());
            Assert.Equal(first.GetProperty("image_hash").GetString(), second.GetProperty("image_hash").GetString());
        }
    }

    [Fact]
    public async Task Upload_reports_real_width_and_height()
    {
        // `spec/07-images.md` §1.8 第 3 条：上传时**真算**尺寸，不再恒 null。
        var (host, client) = await StartAsync();
        await using (host)
        using (client)
        {
            var bytes = RealJpeg();
            var first = await ExpectAsync(await client.PostAsync("/api/v1/images", Multipart(bytes)), HttpStatusCode.OK);
            Assert.Equal(1600, first.GetProperty("width").GetInt32());
            Assert.Equal(900, first.GetProperty("height").GetInt32());

            // 同字节重传：`existed: true`，尺寸**不许被清掉**（协议 §1.7 点过这条风险）。
            var second = await ExpectAsync(await client.PostAsync("/api/v1/images", Multipart(bytes)), HttpStatusCode.OK);
            Assert.True(second.GetProperty("existed").GetBoolean());
            Assert.Equal(1600, second.GetProperty("width").GetInt32());
            Assert.Equal(900, second.GetProperty("height").GetInt32());
        }
    }

    [Fact]
    public async Task Unreadable_image_keeps_dimensions_null_instead_of_guessing()
    {
        // `FakeJpeg` 只有 SOI/EOI，没有 SOF：解不出来就必须如实回 null。
        var (host, client) = await StartAsync();
        await using (host)
        using (client)
        {
            var json = await ExpectAsync(
                await client.PostAsync("/api/v1/images", Multipart(FakeJpeg(1024))), HttpStatusCode.OK);
            Assert.Equal(JsonValueKind.Null, json.GetProperty("width").ValueKind);
            Assert.Equal(JsonValueKind.Null, json.GetProperty("height").ValueKind);
        }
    }

    /// <summary>ffmpeg 真实编码的文件头（1600×900，截到 SOF0 + 3 字节）。</summary>
    private static byte[] RealJpeg() => Convert.FromBase64String(
        "/9j/4AAQSkZJRgABAgAAAQABAAD//gAPTGF2YzYzLjYuMTAxAP/bAEMACA4OEA4QExMTExMTFhUWFxcXFhYWFhcXFxkZGR0dHRkZGRcXGRkcHB0dICEgHh4dHiEhIyMjKiooKDExMjw8SP/EANkAAQABBQEBAAAAAAAAAAAAAAAHAwYCAQgFBAEBAAIDAQEBAAAAAAAAAAAAAAYDAgcBBQgEEAEAAQEDAwwOCAQDBQkBAQEAAQIDEQQhMRIWExWjYVFk4wVBklKRFNPiY9KiU4QHRcPRIqEGYoEy4Rdl5KRxQjPwI7HCNUM0JLLBgrODdLRzRJM2EQEAAQICAwoMAwYFAgUCBwEAAQIDBBEhFhUSBTGjkuNkUtOR0kHiU2NRE2EUogYipOFiMqHRcUKBI7EzZfDBcoJDk7Ik5DWztHSDRTQXc//AABEIA4QGQAMB");

    [Theory]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000")] // 形态合法但不存在
    [InlineData("not-a-hash")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")] // 大写不算合法形态
    [InlineData("..%5C..%5Csecret")] // 目录穿越尝试（Windows 反斜杠）
    public async Task Bad_or_unknown_hashes_are_404(string hash)
    {
        var (host, client) = await StartAsync();
        await using (host)
        using (client)
        {
            var resp = await client.GetAsync($"/api/v1/images/{hash}");
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
            Assert.Equal("not_found", (await ExpectAsync(resp, HttpStatusCode.NotFound)).GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task Empty_file_is_invalid_request()
    {
        var (host, client) = await StartAsync();
        await using (host)
        using (client)
        {
            var resp = await client.PostAsync("/api/v1/images", Multipart([]));
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            Assert.Equal("invalid_request", (await ExpectAsync(resp, HttpStatusCode.BadRequest)).GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task Oversized_upload_is_413_and_does_not_land()
    {
        var (host, client) = await StartAsync();
        await using (host)
        using (client)
        {
            var bytes = FakeJpeg(2 * 1024 * 1024 + 1);
            var resp = await client.PostAsync("/api/v1/images", Multipart(bytes));
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, resp.StatusCode);
            Assert.Equal("payload_too_large", (await ExpectAsync(resp, HttpStatusCode.RequestEntityTooLarge)).GetProperty("code").GetString());
            Assert.Null(host.Images2.Find(Sha256Hex(bytes)));
        }
    }

    [Fact]
    public async Task Uploads_are_rate_limited_per_device_at_30_per_minute()
    {
        var (host, client) = await StartAsync();
        await using (host)
        using (client)
        {
            var bytes = FakeJpeg(512);
            for (var i = 0; i < 30; i++)
            {
                var ok = await client.PostAsync("/api/v1/images", Multipart(bytes));
                Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            }

            var blocked = await client.PostAsync("/api/v1/images", Multipart(bytes));
            Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
            var json = await ExpectAsync(blocked, HttpStatusCode.TooManyRequests);
            Assert.Equal("rate_limited", json.GetProperty("code").GetString());
            Assert.True(json.GetProperty("retry_after_seconds").GetInt32() > 0);
        }
    }

    [Fact]
    public async Task Upload_and_download_require_a_token()
    {
        var clock = new MutableClock();
        var host = await QuizSyncHost.StartAsync(new ServerOptions { PreferredPort = 0 }, clock);
        await using (host)
        using (var anonymous = new HttpClient { BaseAddress = new Uri(host.BaseUrl) })
        {
            var upload = await anonymous.PostAsync("/api/v1/images", Multipart(FakeJpeg(64)));
            Assert.Equal(HttpStatusCode.Unauthorized, upload.StatusCode);

            var download = await anonymous.GetAsync($"/api/v1/images/{new string('a', 64)}");
            Assert.Equal(HttpStatusCode.Unauthorized, download.StatusCode);
        }
    }

    [Fact]
    public async Task Rejected_uploads_still_consume_the_rate_budget()
    {
        // v1 口径：限流在体积判定之前 → 空文件/超限的请求同样占额度。
        var (host, client) = await StartAsync();
        await using (host)
        using (client)
        {
            for (var i = 0; i < 29; i++)
            {
                var empty = await client.PostAsync("/api/v1/images", Multipart([]));
                Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
            }

            var last = await client.PostAsync("/api/v1/images", Multipart(FakeJpeg(256)));
            Assert.Equal(HttpStatusCode.OK, last.StatusCode);

            var blocked = await client.PostAsync("/api/v1/images", Multipart(FakeJpeg(512)));
            Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        }
    }
}
