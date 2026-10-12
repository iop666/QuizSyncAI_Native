using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QuizSync.Server.Core.Ws;

/// <summary>
/// WebSocket 事件推送中枢（`spec/05-websocket.md`）。
///
/// 与 v1 一致的口径：
/// - **每个 device_id 只保留一条连接**：同一台设备再连一次，旧的会被关掉；
/// - 事件是**单向**的（服务端 → 客户端）；客户端发来的 `hello`/`pong`/`ack`/`push_ops`
///   只被读取，不做业务（op 推送走 HTTP）——**唯一例外**是自述 `role: "provider"` 的 `hello`
///   与 `recognition_result`（识别派发，见 <see cref="ProviderChannel"/>）；
/// - 服务端**从不**发 `ops` 事件（客户端那条分支是死分支，向量有负向断言钉住）。
/// </summary>
public sealed class WebSocketHub
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly ConcurrentDictionary<string, Connection> _connections = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> ConnectedDeviceIds => _connections.Keys.ToList();

    /// <summary>
    /// Provider 通道（识别派发）。挂在 Hub 上而不是另起一个字段，是为了**只有一份**：
    /// 宿主登记 Provider、识别器等回包，两边拿到的必须是同一个对象
    /// （两份的话表现是「Provider 明明在线，任务却永远 provider_offline」）。
    /// </summary>
    public ProviderChannel Providers => field ??= new ProviderChannel(this);

    /// <summary>登记一条连接并**踢掉同一设备的旧连接**。</summary>
    public async Task AddAsync(string deviceId, WebSocket socket)
    {
        if (_connections.TryRemove(deviceId, out var previous))
        {
            await CloseAsync(previous.Socket, "同一设备的新连接已建立").ConfigureAwait(false);
        }

        _connections[deviceId] = new Connection(socket);
    }

    public void Remove(string deviceId, WebSocket socket)
    {
        // 迟到的 onDone 不能把新连接误删（v1 的同款防护）。
        if (_connections.TryGetValue(deviceId, out var current) && ReferenceEquals(current.Socket, socket))
        {
            _connections.TryRemove(deviceId, out _);
        }
    }

    public void Send(string deviceId, JsonObject message)
    {
        if (_connections.TryGetValue(deviceId, out var connection))
        {
            _ = SendAsync(connection, message, CancellationToken.None);
        }
    }

    public void Broadcast(JsonObject message)
    {
        foreach (var connection in _connections.Values)
        {
            _ = SendAsync(connection, message, CancellationToken.None);
        }
    }

    /// <summary>
    /// 广播并**等所有帧发完**。需要「先广播再关连接」的顺序时用它
    /// （吊销设备就是：先给 device_revoked，再关连接；用 fire-and-forget 会抢跑）。
    /// </summary>
    public async Task BroadcastAsync(JsonObject message)
    {
        foreach (var connection in _connections.Values.ToList())
        {
            await SendAsync(connection, message, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 按 `device_id` 定向发一帧并**等它发完**，返回「到底发出去没有」。
    ///
    /// 与 <see cref="Send"/> 的区别就是可判定：派发识别任务时，帧有没有真的发出去决定这一单
    /// 是**立刻**以 `provider_offline` 失败，还是继续等回包 —— 不能靠「大概发出去了」。
    /// </summary>
    public async Task<bool> SendToAsync(string deviceId, JsonObject message, CancellationToken ct = default)
    {
        if (!_connections.TryGetValue(deviceId, out var connection))
        {
            return false;
        }

        return await SendAsync(connection, message, ct).ConfigureAwait(false);
    }

    /// <summary>关掉某台设备的连接（吊销设备后调用）。</summary>
    public async Task CloseForAsync(string deviceId)
    {
        if (_connections.TryRemove(deviceId, out var connection))
        {
            await CloseAsync(connection.Socket, "设备已被吊销").ConfigureAwait(false);
        }
    }

    /// <summary>返回是否真的写出去了（对端断了 / 对方没在打开状态都是 false）。</summary>
    private static async Task<bool> SendAsync(Connection connection, JsonObject message, CancellationToken ct)
    {
        var socket = connection.Socket;
        if (socket.State != WebSocketState.Open)
        {
            return false;
        }

        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, Json));

        // **同一连接的帧必须串行发**：.NET 的 SendAsync 在「已有一次发送没完成」时抛
        // InvalidOperationException（不是 WebSocketException，catch 不住）。原来只有广播一个
        // 发送方时撞不上；现在「广播 task_update」与「定向派发 recognition_job」会同时打在
        // Provider 那条连接上，必须排队。
        await connection.SendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (socket.State != WebSocketState.Open)
            {
                return false;
            }

            await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // 对端已经断了：连接会在读循环里被清掉。
            return false;
        }
        finally
        {
            connection.SendGate.Release();
        }
    }

    private static async Task CloseAsync(WebSocket socket, string reason)
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                // 关闭握手要等对端回帧 —— **给个上限**：客户端不回应时不能让
                // 调用方（例如吊销设备的那个 HTTP 请求）一直挂着。
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, cts.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            // 已经关了，或者对端没回应：到此为止。
        }
    }

    /// <summary>一条连接 + 它的发送闸门（闸门见 <see cref="SendAsync"/> 里为什么必须有）。</summary>
    private sealed class Connection(WebSocket socket)
    {
        public WebSocket Socket { get; } = socket;

        public SemaphoreSlim SendGate { get; } = new(1, 1);
    }
}
