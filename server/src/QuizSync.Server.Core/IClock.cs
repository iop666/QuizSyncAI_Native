namespace QuizSync.Server.Core;

/// <summary>
/// 时钟。协议里所有与时间有关的行为（配对码有效期、限流窗口、锁定期、
/// 任务超时）都必须走这里，**不许直接读系统时间** —— 否则一致性向量里那些
/// 「推进 5 分钟看配对码过期」的步骤根本没法回放。v1 的 Dart 实现同样是注入时钟。
/// </summary>
public interface IClock
{
    /// <summary>UTC 毫秒（epoch）。</summary>
    long NowMs { get; }
}

/// <summary>生产用：真时间。</summary>
public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();

    public long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

/// <summary>测试/回放用：可以被人为推进的时钟。</summary>
public sealed class MutableClock : IClock
{
    private long _nowMs;

    public MutableClock(long startMs = 1_700_000_000_000) => _nowMs = startMs;

    public long NowMs => Interlocked.Read(ref _nowMs);

    public void Advance(long deltaMs) => Interlocked.Add(ref _nowMs, deltaMs);

    public void Set(long nowMs) => Interlocked.Exchange(ref _nowMs, nowMs);
}
