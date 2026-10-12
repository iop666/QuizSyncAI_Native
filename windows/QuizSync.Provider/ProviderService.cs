using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using QuizSync.Core;
using QuizSync.Data;
using QuizSync.ServerBridge;

namespace QuizSync.Provider;

/// <summary>
/// **无界面的 Provider**：替主机分析手机传上来的图。
///
/// 它解决的是 2.0 的一个结构性缺口：主机（`QuizSyncServer`）按架构**不持有 API Key**
/// （Key 是 DPAPI 加密、只有本机用户能解开），所以「提交任务 → 出结果」这条链必须有
/// 一个**本机进程**去做 AI。这个类就是那个进程的行为：连上主机的 `/ws`，
/// 自述自己是 Provider，收到 `recognition_job` 就取图 → 调 AI → 把题目回传。
///
/// 三条必须守住的语义：
/// * **绝不假装成功**：拿不到图、AI 报错、解析失败，一律回 `ok:false` + 真实错误码。
///   回一个空题目列表会被主机记成 `no_question_found`，症状是「识别不出题」——
///   而真因可能是 Key 过期，用户永远查不到。
/// * **一次只跑一个任务**：主机侧的队列是串行的（并发恒 1），这里也用信号量收着，
///   免得某次任务卡住时后面排队的结果乱序回传。
/// * **断线要重连，鉴权失败不重连**：401 说明令牌已被吊销/主机换过库，
///   重连一万次也没用，只能让人重新配对（协议 `spec/05-websocket.md` §3.2）。
/// </summary>
public sealed class ProviderService : IDisposable
{
    private static readonly TimeSpan[] Backoff =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5),
         TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    private readonly HostSession _session;
    private readonly AiConfig _config;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _oneJobAtATime = new(1, 1);
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    public ProviderService(HostSession session, AiConfig config, Action<string>? log = null)
    {
        _session = session;
        _config = config;
        _log = log ?? (_ => { });
    }

    /// <summary>
    /// 连上主机并长期服务。返回 false = **鉴权失败**（需要重新配对，调用方应退出而不是重试）。
    /// </summary>
    public async Task<bool> RunAsync(CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var connected = false;
            try
            {
                using var socket = new ClientWebSocket();
                socket.Options.SetRequestHeader("Authorization", $"Bearer {_session.Token}");
                socket.Options.SetRequestHeader("X-QS-Device-Id", HostSession.ProviderDeviceId);
                socket.Options.SetRequestHeader("X-QS-Client-Version", "1.1.0-native");

                await socket.ConnectAsync(_session.WebSocketUri, cancellationToken).ConfigureAwait(false);
                connected = true;
                attempt = 0;
                _log($"已连上主机 {_session.BaseUrl}（{_config.ProviderId} / {_config.Model}），等待识别任务…");

                await SendAsync(socket, ProviderProtocol.Hello(_config), cancellationToken).ConfigureAwait(false);
                await ReceiveLoopAsync(socket, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return true;
            }
            catch (Exception error) when (error is WebSocketException or HttpRequestException or InvalidOperationException)
            {
                // 握手被拒（401）说明令牌不可用：重连再多次也是同样的结果。
                if (!connected && !await TokenUsableAsync(cancellationToken).ConfigureAwait(false))
                {
                    _log("主机不认这个令牌（可能已被吊销，或主机换过数据库）——请重新配对后再启动。");
                    return false;
                }

                _log($"连接中断：{error.Message}");
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return true;
            }

            var wait = Backoff[Math.Min(attempt, Backoff.Length - 1)];
            attempt++;
            _log($"{wait.TotalSeconds:0} 秒后重连…");
            await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        var text = new StringBuilder();

        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            text.Clear();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                text.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            }
            while (!result.EndOfMessage);

            JsonObject? message;
            try
            {
                message = JsonNode.Parse(text.ToString()) as JsonObject;
            }
            catch (System.Text.Json.JsonException)
            {
                // 协议 §2.2：无法解析的帧**静默丢弃**，不断连。
                continue;
            }

            switch (message?["type"]?.ToString())
            {
                case "recognition_job":
                    // 不 await：要能同时回 `ping`。任务本身由信号量串行化。
                    _ = HandleJobAsync(socket, message, cancellationToken);
                    break;

                case "ping":
                    await SendAsync(socket, new JsonObject
                    {
                        ["type"] = "pong",
                        ["ts"] = message["ts"]?.GetValue<long>() ?? 0L,
                    }, cancellationToken).ConfigureAwait(false);
                    break;

                default:
                    // `hello` / `task_update` / `task_result` / … 对 Provider 没有动作。
                    break;
            }
        }
    }

    /// <summary>收到派发：取图 → 调 AI → 回传。**任何失败都如实回 `ok:false`**。</summary>
    private async Task HandleJobAsync(ClientWebSocket socket, JsonObject job, CancellationToken cancellationToken)
    {
        var jobId = job["job_id"]?.ToString() ?? string.Empty;
        await _oneJobAtATime.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var hashes = new List<string>();
            if (job["image_hashes"] is JsonArray pages)
            {
                hashes.AddRange(pages.Select(page => page?.ToString() ?? string.Empty).Where(hash => hash.Length > 0));
            }

            if (hashes.Count == 0 && job["image_hash"]?.ToString() is { Length: > 0 } single)
            {
                hashes.Add(single);
            }

            if (hashes.Count == 0)
            {
                await SendAsync(socket, ProviderProtocol.Failure(jobId, "internal", "派发里没有图片 hash"), cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            _log($"收到任务 {job["task_id"]}：{hashes.Count} 张图，开始识别…");

            var images = new List<byte[]>();
            foreach (var hash in hashes)
            {
                images.Add(await _session.Client.DownloadImageAsync(hash, cancellationToken).ConfigureAwait(false));
            }

            using var database = QuizDatabase.OpenOrCreate(AppDataDirectory.DatabasePath());
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(_config.TimeoutSeconds + 30) };
            var engine = new AnalysisEngine(
                new HttpAiProvider(_config.ProviderId, http),
                new AnalysisCache(database),
                new QuotaGuard(database),
                deviceId: HostSession.ProviderDeviceId);

            // 单页才允许命中缓存（多页组合无法用第一页的 hash 区分后续页不同的两次识别）。
            var outcome = await engine
                .AnalyzeImagesAsync(images, hashes[0], _config, useCache: images.Count == 1, cancellationToken)
                .ConfigureAwait(false);

            if (outcome.ErrorCode is not null)
            {
                _log($"任务 {job["task_id"]} 失败：{outcome.ErrorCode} {outcome.ErrorMessage}");
                await SendAsync(socket, ProviderProtocol.Failure(jobId, outcome.ErrorCode, outcome.ErrorMessage ?? "识别失败"), cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            _log($"任务 {job["task_id"]} 完成：{outcome.Questions.Count} 道题"
                + (outcome.FromCache ? "（命中缓存）" : $"（{outcome.LatencyMs} ms）"));

            await SendAsync(socket, ProviderProtocol.Result(jobId, _config, outcome), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 正在退出：不用回，主机那边会自己超时。
        }
        catch (Exception error)
        {
            _log($"任务 {jobId} 异常：{error.Message}");
            try
            {
                await SendAsync(socket, ProviderProtocol.Failure(jobId, "internal", error.Message), CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception sendError) when (sendError is WebSocketException or ObjectDisposedException)
            {
                // 连接已经没了：主机侧超时兜底。
            }
        }
        finally
        {
            _oneJobAtATime.Release();
        }
    }

    /// <summary>发一帧。**必须过 `_sendGate`**：`ClientWebSocket` 不允许两处并发 Send。</summary>
    private async Task SendAsync(ClientWebSocket socket, JsonObject message, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>令牌还认不认（用需要鉴权的端点问，不用免鉴权的 `/info`）。</summary>
    private async Task<bool> TokenUsableAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _session.Client.DevicesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (HostException error) when (error.StatusCode == 401)
        {
            return false;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            // 主机没在跑：不是鉴权问题，继续重连。
            return true;
        }
    }

    public void Dispose()
    {
        _oneJobAtATime.Dispose();
        _sendGate.Dispose();
    }
}
