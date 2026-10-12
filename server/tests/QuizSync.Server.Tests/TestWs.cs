using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace QuizSync.Server.Tests;

/// <summary>
/// 测试用的**真** WS 客户端（`ClientWebSocket`，不是 mock）：收帧进队列，`ExpectAsync` 按 `type` 等，
/// `Has` 用来做负向断言（「这条连接永远没收到过某类帧」）。
///
/// 收帧循环必须自己拼**整条消息**（分帧是常态）：只读一帧会把 JSON 截断，
/// 表现成「服务端明明发了，测试说没收到」。
/// </summary>
internal sealed class TestWs : IAsyncDisposable
{
    private readonly ClientWebSocket _socket = new();
    private readonly List<JsonObject> _inbox = [];
    private IReadOnlyDictionary<string, IEnumerable<string>>? _handshakeHeaders;

    private TestWs()
    {
    }

    /// <summary>握手的 HTTP 状态码（打开 <c>CollectHttpResponseDetails</c> 之后才有值）。</summary>
    public System.Net.HttpStatusCode? HandshakeStatus { get; private set; }

    /// <summary>客户端这边的连接状态（失败诊断用：Aborted/Closed 说明连接已经没了）。</summary>
    public WebSocketState State => _socket.State;

    public bool HandshakeHasHeader(string name) =>
        _handshakeHeaders is not null &&
        _handshakeHeaders.Keys.Any(key => key.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static async Task<TestWs> ConnectAsync(
        string baseUrl, string token, JsonObject? hello = null, string? clientVersion = null)
    {
        var client = new TestWs();
        client._socket.Options.SetRequestHeader("Authorization", $"Bearer {token}");
        if (clientVersion is not null)
        {
            client._socket.Options.SetRequestHeader("X-QS-Client-Version", clientVersion);
        }

        // 握手响应的状态码与响应头（用来钉住「/ws 不做版本协商、也没有 X-QS-Server-Version」）。
        client._socket.Options.CollectHttpResponseDetails = true;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client._socket.ConnectAsync(
            new Uri(baseUrl.Replace("http://", "ws://", StringComparison.Ordinal) + "/ws"), cts.Token);

        client.HandshakeStatus = client._socket.HttpStatusCode;
        client._handshakeHeaders = client._socket.HttpResponseHeaders;

        _ = client.ReceiveLoopAsync();
        if (hello is not null)
        {
            await client.SendAsync(hello);
        }

        return client;
    }

    public async Task SendAsync(JsonObject frame)
    {
        var bytes = Encoding.UTF8.GetBytes(frame.ToJsonString());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cts.Token);
    }

    /// <summary>等一条指定 `type` 的帧（取最早的一条，并丢掉它之前的）。</summary>
    public async Task<JsonObject> ExpectAsync(string type, int maxMs = 15_000)
    {
        var deadline = Environment.TickCount64 + maxMs;
        while (Environment.TickCount64 < deadline)
        {
            lock (_inbox)
            {
                for (var i = 0; i < _inbox.Count; i++)
                {
                    if (_inbox[i]["type"]?.ToString() == type)
                    {
                        var found = _inbox[i];
                        _inbox.RemoveRange(0, i + 1);
                        return found;
                    }
                }
            }

            await Task.Delay(20);
        }

        string dump;
        lock (_inbox)
        {
            dump = string.Join(" | ", _inbox.Select(m => m.ToJsonString()));
        }

        throw new Xunit.Sdk.XunitException($"{maxMs} ms 内没等到 type={type} 的帧；收到：{dump}");
    }

    /// <summary>这条连接**到现在为止**收到过某类帧吗（负向断言用；不做等待）。</summary>
    public bool Has(string type)
    {
        lock (_inbox)
        {
            return _inbox.Any(message => message["type"]?.ToString() == type);
        }
    }

    /// <summary>还没被取走、且类型匹配的帧（不弹出）—— 用来断言「收到过的这类帧里没有某一种」。</summary>
    public IReadOnlyList<JsonObject> Received(string type)
    {
        lock (_inbox)
        {
            return _inbox.Where(message => message["type"]?.ToString() == type).ToList();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "测试结束", cts.Token);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            // 已经断了：算「拔线」。
        }

        _socket.Dispose();
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[8 * 1024];
        try
        {
            while (_socket.State == WebSocketState.Open)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(buffer, CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        // 服务端主动关：**必须回一个**，否则它的 CloseAsync 会一直等应答。
                        try
                        {
                            await _socket.CloseOutputAsync(
                                WebSocketCloseStatus.NormalClosure, "ack", CancellationToken.None);
                        }
                        catch (WebSocketException)
                        {
                            // 已经断了。
                        }

                        return;
                    }

                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                try
                {
                    if (JsonNode.Parse(Encoding.UTF8.GetString(message.ToArray())) is JsonObject frame)
                    {
                        lock (_inbox)
                        {
                            _inbox.Add(frame);
                        }
                    }
                }
                catch (System.Text.Json.JsonException)
                {
                    // 非 JSON 帧：协议不发，忽略。
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // 连接断了，收帧循环到此为止。
        }
    }
}
