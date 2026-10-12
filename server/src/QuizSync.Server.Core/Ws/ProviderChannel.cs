using System.Net.WebSockets;
using System.Text.Json.Nodes;

namespace QuizSync.Server.Core.Ws;

/// <summary>
/// Provider 的在线状态。广播 `provider_online` 与 `/info.ai_configured` 都用它。
/// 离线时三项分别是 `false / false / null`（没有 Provider = 没有自述）。
/// </summary>
public sealed record ProviderState(bool Online, bool AiConfigured, string? AiProvider, string? AiModel);

/// <summary>派发为什么结束 —— 语义由 <see cref="QuizSync.Server.Core.Tasks.ProviderAnalyzer"/> 翻成协议错误码。</summary>
public enum DispatchStatus
{
    /// <summary>Provider 回了一帧（内容对不对由调用方判）。</summary>
    Replied,

    /// <summary>没有 Provider 在线：立刻失败，**不挂死、不回退 fixture**。</summary>
    NoProvider,

    /// <summary>等到 `deadline_ms` 还没回包。</summary>
    Timeout,

    /// <summary>派发中 Provider 断了（或方向帧根本没发出去）。</summary>
    Disconnected,
}

/// <summary>一次派发的结果：<see cref="DispatchStatus.Replied"/> 时 <see cref="Frame"/> 是 Provider 的回帧。</summary>
public sealed record DispatchOutcome(DispatchStatus Status, JsonObject? Frame);

/// <summary>
/// Provider 通道：**登记唯一一个 Provider、把识别作业派给它、把回帧接回等待中的那一单**。
///
/// 纪律（都是踩过或必然会踩的坑）：
/// - **只允许一个 Provider**：后来的顶掉先前的登记（旧连接不断、只是不再收 job）；
/// - **注销必须与登记同源**：只有「当前登记的就是这条连接」时才真的摘除 ——
///   否则同一台设备重连时，旧连接迟到的关闭会把**新** Provider 判成掉线
///   （`WebSocketHub.Remove` 是同一条纪律）；
/// - **掉线 = 未完成的 job 全部立刻失败**（`Disconnected`），不能等自然超时：
///   用户看到的会是「转 90 秒」，而真相是「Provider 早就不在了」；
/// - 回帧按 `job_id` 认领：**已超时/已判失败的 job 的回帧直接丢掉**（迟到不算数）。
/// </summary>
public sealed class ProviderChannel(WebSocketHub hub)
{
    private readonly WebSocketHub _hub = hub;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);

    private Registration? _current;

    /// <summary>当前状态（无 Provider 时恒为「离线」）。</summary>
    public ProviderState State
    {
        get
        {
            lock (_gate)
            {
                return _current is null
                    ? new ProviderState(false, false, null, null)
                    : new ProviderState(true, _current.AiConfigured, _current.AiProvider, _current.AiModel);
            }
        }
    }

    /// <summary>
    /// 登记 Provider（连上后 `hello` 里自述 `role: "provider"`）。
    /// 返回**新状态**：调用方据此广播 `provider_online`（被顶掉的那条连接不断开）。
    /// </summary>
    public ProviderState Register(string deviceId, WebSocket socket, bool aiConfigured, string? aiProvider, string? aiModel)
    {
        lock (_gate)
        {
            _current = new Registration(deviceId, socket, aiConfigured, aiProvider, aiModel);
        }

        return new ProviderState(true, aiConfigured, aiProvider, aiModel);
    }

    /// <summary>
    /// 注销 Provider。返回 null = **这次注销不算数**（当前登记的不是这条连接）：
    /// 调用方据此**不广播**，否则「迟到的关闭」会把刚连上的新 Provider 广播成掉线。
    /// 真的掉线时，所有发给它的未完成 job 立刻按 `Disconnected` 收口。
    /// </summary>
    public ProviderState? Unregister(string deviceId, WebSocket socket)
    {
        List<Pending> orphans;
        lock (_gate)
        {
            if (_current is null ||
                _current.DeviceId != deviceId ||
                !ReferenceEquals(_current.Socket, socket))
            {
                return null;
            }

            _current = null;
            orphans = TakePending(deviceId);
        }

        FailAll(orphans, DispatchStatus.Disconnected);
        return new ProviderState(false, false, null, null);
    }

    /// <summary>
    /// 派发一个作业并等回包（最长 <paramref name="timeoutMs"/>，也就是帧里的 `deadline_ms`）。
    ///
    /// **没有 Provider 在线时立刻返回 `NoProvider`**（≤ 一次字典查找的时间，不挂死）。
    /// 这一步的 `await` 只看 deadline：调用方传进来的 <paramref name="ct"/> 只用于发送阶段
    /// （现在的调用方给的就是 `CancellationToken.None` —— 任务失败要落库，不能被取消掉）。
    /// </summary>
    public async Task<DispatchOutcome> DispatchAsync(
        JsonObject job, string jobId, int timeoutMs, CancellationToken ct)
    {
        Pending pending;
        Registration target;
        lock (_gate)
        {
            if (_current is null)
            {
                // 没有 Provider 在线：立刻失败（**绝不回退 fixture** —— 那是「真人拿到胡话」的老缺陷）。
                return new DispatchOutcome(DispatchStatus.NoProvider, null);
            }

            target = _current;
            pending = new Pending(target.DeviceId, new TaskCompletionSource<DispatchOutcome>(
                TaskCreationOptions.RunContinuationsAsynchronously));
            _pending[jobId] = pending;
        }

        if (!await _hub.SendToAsync(target.DeviceId, job, ct).ConfigureAwait(false))
        {
            // 登记还在、连接其实已经发不出去了（掉线的窗口期）：按掉线收口，不挂死。
            return Abandon(jobId, DispatchStatus.Disconnected);
        }

        // 超时：先把这一单从待办里摘掉（迟到的回帧就不算数了），再唤醒等待方。
        using var deadline = new CancellationTokenSource(timeoutMs);
        using var registration = deadline.Token.Register(() =>
        {
            if (TryTake(jobId, out var expired))
            {
                expired.Completion.TrySetResult(new DispatchOutcome(DispatchStatus.Timeout, null));
            }
        });

        return await pending.Completion.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// 接 Provider 的回帧（`recognition_result`）。返回 false = 这个 `job_id` 已经不认了
    /// （超时过、或 Provider 掉线时判过失败）—— 迟到不算数，也不能把结果塞回已结束的任务。
    /// </summary>
    public bool Complete(string jobId, JsonObject frame)
    {
        if (!TryTake(jobId, out var pending))
        {
            return false;
        }

        pending.Completion.TrySetResult(new DispatchOutcome(DispatchStatus.Replied, frame));
        return true;
    }

    /// <summary>在线判定（宿主在 `/ws` 读循环里用不到它，CLI 的 doctor 与测试用）。</summary>
    public bool IsOnline
    {
        get
        {
            lock (_gate)
            {
                return _current is not null;
            }
        }
    }

    private DispatchOutcome Abandon(string jobId, DispatchStatus status)
    {
        if (TryTake(jobId, out var pending))
        {
            pending.Completion.TrySetResult(new DispatchOutcome(status, null));
        }

        return new DispatchOutcome(status, null);
    }

    private bool TryTake(string jobId, out Pending pending)
    {
        lock (_gate)
        {
            if (_pending.Remove(jobId, out var found))
            {
                pending = found;
                return true;
            }
        }

        pending = null!;
        return false;
    }

    /// <summary>摘掉某个 Provider 的全部待办（调用方持有 <see cref="_gate"/>）。</summary>
    private List<Pending> TakePending(string deviceId)
    {
        var orphans = new List<Pending>();
        foreach (var jobId in _pending
                     .Where(pair => pair.Value.DeviceId == deviceId)
                     .Select(pair => pair.Key)
                     .ToList())
        {
            orphans.Add(_pending[jobId]);
            _pending.Remove(jobId);
        }

        return orphans;
    }

    private static void FailAll(IEnumerable<Pending> pending, DispatchStatus status)
    {
        foreach (var item in pending)
        {
            item.Completion.TrySetResult(new DispatchOutcome(status, null));
        }
    }

    private sealed record Registration(string DeviceId, WebSocket Socket, bool AiConfigured, string? AiProvider, string? AiModel);

    private sealed record Pending(string DeviceId, TaskCompletionSource<DispatchOutcome> Completion);
}
