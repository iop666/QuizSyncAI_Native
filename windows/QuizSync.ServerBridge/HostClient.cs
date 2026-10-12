using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

namespace QuizSync.ServerBridge;

/// <summary>
/// 客户端 → 主机的 HTTP 接口（v1 兼容子集，与 Android 侧 `HostApi` 同一批端点）。
///
/// 只做 I/O：拼请求、解析 JSON、把 HTTP 失败变成异常；业务判断不在这里。
/// </summary>
public sealed class HostClient(string baseUrl, HttpClient? client = null)
{
    private readonly HttpClient _client = client ?? new HttpClient();
    private readonly string _base = baseUrl.TrimEnd('/');

    public string? Token { get; private set; }

    public void UseToken(string? token) => Token = token;

    /// <summary>`GET /health`（免鉴权）。</summary>
    public async Task<JsonObject?> HealthAsync(CancellationToken cancellationToken = default) =>
        await SendAsync(HttpMethod.Get, "/health", null, authenticated: false, cancellationToken).ConfigureAwait(false);

    /// <summary>`GET /api/v1/devices`（已配对设备列表；界面「连接设备」用）。</summary>
    public Task<JsonObject?> DevicesAsync(CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, "/api/v1/devices", null, authenticated: true, cancellationToken);

    /// <summary>
    /// `DELETE /api/v1/devices/{id}`（吊销：**软删**，服务端置 `revoked_at`，之后该设备的令牌一律 401）。
    /// 服务端对不存在的 id 返回 404 → 抛 <see cref="HostException"/>，由调用方如实说出来。
    /// </summary>
    public Task<JsonObject?> RevokeDeviceAsync(string deviceId, CancellationToken cancellationToken = default) =>
        SendAsync(
            HttpMethod.Delete,
            $"/api/v1/devices/{Uri.EscapeDataString(deviceId)}",
            null,
            authenticated: true,
            cancellationToken);

    /// <summary>`POST /api/v1/pair`；成功（200/409）时**自动记住 token**。</summary>
    public async Task<JsonObject?> PairAsync(
        string code, string deviceId, string deviceName, string platform = "windows", string appVersion = "1.1.0-native",
        CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["code"] = code,
            ["device_id"] = deviceId,
            ["device_name"] = deviceName,
            ["platform"] = platform,
            ["app_version"] = appVersion,
        };
        var result = await PairOrConflictAsync(body, cancellationToken).ConfigureAwait(false);
        if (result?["token"]?.ToString() is { Length: > 0 } token)
        {
            Token = token;
        }

        return result;
    }

    /// <summary>
    /// 配对请求，**并把 409 当成功**。
    ///
    /// 409 = 同一个 `device_id` 重复配对：协议里算成功（服务端照样发新 token，旧的作废），
    /// 而且这个 409 的响应体**就是**正常的配对响应 —— 实测：
    /// `{"token":"…","server_device_id":"…","already_paired":true}`。
    ///
    /// 这条协议规则放在这里、而不是每个调用方各写一遍：原来两个调用方（`QuizSync.Provider` 的
    /// `HostSession`、桌面端的 `SyncUploader`）都自己 `JsonNode.Parse(error.Message)`，
    /// 而 `Message` 是给人看的拼串 —— 两边**同时**踩中，表现是「token 一过期，识别服务就起不来，
    /// 报一个 JSON 解析错误」（第 167 轮实测，与主机内嵌无关：独立进程的主机一样复现）。
    /// </summary>
    private async Task<JsonObject?> PairOrConflictAsync(JsonObject body, CancellationToken cancellationToken)
    {
        try
        {
            return await SendAsync(HttpMethod.Post, "/api/v1/pair", body, authenticated: false, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HostException error) when (error.StatusCode == 409 && error.BodyJson is { } alreadyPaired)
        {
            return alreadyPaired;
        }
    }

    /// <summary>`POST /api/v1/images`（multipart，字段名固定 `file`；内容寻址去重）。</summary>
    public async Task<JsonObject?> UploadImageAsync(
        byte[] jpeg, string filename = "page.jpg", CancellationToken cancellationToken = default)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(jpeg);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        content.Add(file, "file", filename);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_base}/api/v1/images") { Content = content };
        if (Token is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {Token}");
        }

        return await ReadAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// `GET /api/v1/images/{hash}` —— 取图片字节。
    ///
    /// 为什么需要它：Provider（本机无界面识别服务）拿到的是**图片 hash**（手机在另一台设备上传的），
    /// 它本地没有那些文件，必须回主机取原始字节才能喂给 AI。
    /// </summary>
    public async Task<byte[]> DownloadImageAsync(string hash, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_base}/api/v1/images/{hash}");
        if (Token is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {Token}");
        }

        using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // 错误体是 JSON（`{code,message}`），成功体是**图片字节** —— 别混着解析。
            var code = "image_unavailable";
            var message = $"取图失败（HTTP {(int)response.StatusCode}）";
            try
            {
                if (JsonNode.Parse(System.Text.Encoding.UTF8.GetString(bytes)) is JsonObject error)
                {
                    code = error["code"]?.ToString() ?? code;
                    message = error["message"]?.ToString() ?? message;
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // 不是 JSON：保持上面那句通用文案。
            }

            throw new HostException((int)response.StatusCode, code, message);
        }

        return bytes;
    }

    /// <summary>`POST /api/v1/tasks`（恒 202；`status` 是 queued / done）。</summary>
    public async Task<JsonObject?> CreateTaskAsync(
        string taskId, string imageHash, string sourceDevice, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["task_id"] = taskId,
            ["image_hash"] = imageHash,
            ["source_device"] = sourceDevice,
        };
        return await SendAsync(HttpMethod.Post, "/api/v1/tasks", body, authenticated: true, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>`GET /api/v1/tasks/&lt;id&gt;`。</summary>
    public Task<JsonObject?> GetTaskAsync(string taskId, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, $"/api/v1/tasks/{taskId}", null, authenticated: true, cancellationToken);

    /// <summary>轮询到终态（done / failed）；超时抛异常。</summary>
    public async Task<JsonObject?> AwaitTaskAsync(
        string taskId, TimeSpan timeout, TimeSpan? interval = null, CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        var step = interval ?? TimeSpan.FromMilliseconds(100);
        JsonObject? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            last = await GetTaskAsync(taskId, cancellationToken).ConfigureAwait(false);
            var status = last?["status"]?.ToString();
            if (status is "done" or "failed")
            {
                return last;
            }

            await Task.Delay(step, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"任务 {taskId} 在 {timeout} 内没到终态，最后状态：{last?.ToJsonString()}");
    }

    /// <summary>`POST /api/v1/sync/ops`（推 op；返回 applied / rejected 计数）。</summary>
    public async Task<JsonObject?> PushOpsAsync(string opsJson, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_base}/api/v1/sync/ops")
        {
            Content = new StringContent(opsJson, Encoding.UTF8, "application/json"),
        };
        if (Token is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {Token}");
        }

        return await ReadAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonObject?> SendAsync(
        HttpMethod method, string path, JsonObject? body, bool authenticated, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, $"{_base}{path}");
        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        if (authenticated && Token is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {Token}");
        }

        return await ReadAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonObject?> ReadAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        JsonObject? json = null;
        try
        {
            json = JsonNode.Parse(text) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            json = null;
        }

        if (!response.IsSuccessStatusCode)
        {
            var code = json?["code"]?.ToString() ?? ((int)response.StatusCode).ToString();
            var message = json?["message"]?.ToString() ?? text;
            // 响应体原文一定要带上：调用方要按协议处理某些"错误"（比如 409 重复配对）就得读它。
            throw new HostException((int)response.StatusCode, code, message, text);
        }

        return json;
    }
}

/// <summary>
/// 主机返回的错误（协议里的 `{code,message,retry_after_seconds?}`）。
///
/// ⚠️ <see cref="Exception.Message"/> 是**给人看的拼串**（`"{code} ({status}): {body}"`），
/// **不能当响应体解析** —— 第 167 轮实测：两个调用方都用 `JsonNode.Parse(error.Message)` 取
/// 409 响应里的 token，而它会先读到一个 JSON 数字（状态码）再撞上 ` (`，于是抛
/// `'(' is invalid after a single JSON value. Expected end of data. LineNumber: 0 | BytePositionInLine: 4`，
/// 用户看到的是「识别服务刚起来就退出，报了一个 JSON 错误」。
/// 要响应体请用 <see cref="Body"/> / <see cref="BodyJson"/>。
/// </summary>
public sealed class HostException(int statusCode, string code, string message, string? body = null)
    : Exception($"{code} ({statusCode}): {message}")
{
    public int StatusCode { get; } = statusCode;

    public string Code { get; } = code;

    /// <summary>响应体原文。**可能不是 JSON**（反向代理、404 页面之类），别假设它能解析。</summary>
    public string? Body { get; } = body;

    /// <summary>响应体解析出来的 JSON 对象；不是 JSON 就是 null（**不抛异常**）。</summary>
    public JsonObject? BodyJson
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Body))
            {
                return null;
            }

            try
            {
                return JsonNode.Parse(Body) as JsonObject;
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }
        }
    }
}

/// <summary>控制面（回环 + `X-QS-Control`）：读配对码、刷新配对码。</summary>
public sealed class HostControl(string baseUrl, string controlToken, HttpClient? client = null)
{
    private readonly HttpClient _client = client ?? new HttpClient();

    public static string? FindControlToken(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, "control.token");
        return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
    }

    /// <summary>
    /// 读数据目录里的控制令牌；没有（或形态不对）就生成一个**写盘**再返回。
    ///
    /// 算法必须与 `QuizSync.Server.Cli` 的 `ReadOrCreateControlToken` 逐字一致
    /// （32 字节 = 64 位 hex，写完不带换行）：同一个数据目录今天被 CLI 起、明天被内嵌宿主起，
    /// 令牌一变，界面上的「检测并启动主机」「已配对设备」就再也连不上了 ——
    /// 而且失败表现是 `control_denied`，看起来像鉴权坏了，不像「两个进程各写了一份令牌」。
    /// </summary>
    public static string ReadOrCreateControlToken(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        if (FindControlToken(dataDirectory) is { Length: QuizSync.Server.Core.Tokens.TokenBytes * 2 } existing)
        {
            return existing;
        }

        var token = QuizSync.Server.Core.Tokens.NewToken();
        File.WriteAllText(Path.Combine(dataDirectory, "control.token"), token);
        return token;
    }

    public Task<JsonObject?> PairCodeAsync(CancellationToken cancellationToken = default) =>
        CallAsync("/api/v1/pair/code", cancellationToken);

    public Task<JsonObject?> RefreshPairCodeAsync(CancellationToken cancellationToken = default) =>
        CallAsync("/api/v1/pair/code/refresh", cancellationToken);

    private async Task<JsonObject?> CallAsync(string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}{path}");
        request.Headers.TryAddWithoutValidation("X-QS-Control", controlToken);
        using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HostException((int)response.StatusCode, "control_denied", text);
        }

        return JsonNode.Parse(text) as JsonObject;
    }
}
