using System.Collections.Concurrent;

namespace QuizSync.Server.Core.Protocol;

/// <summary>
/// 按 key 的滑动窗口限流（v1 的上传限流与配对限流都是这个形状：N 次 / 60 秒）。
///
/// 语义与 v1 逐条对齐：
/// - **先判后记**：被拒的调用不消耗额度，但**通过检查的调用无论最终成功与否都记一次**
///   （上传处理器在体积校验之前就记账 —— 空的、超限的上传同样占额度）；
/// - 窗口内第 N+1 次 → 拒绝，并给出「最早那次还要多久过期」的秒数（向上取整）。
/// </summary>
public sealed class SlidingWindowRateLimiter(int limit, long windowMs)
{
    private readonly ConcurrentDictionary<string, Queue<long>> _windows = new();
    private readonly int _limit = limit;
    private readonly long _windowMs = windowMs;

    public bool TryAcquire(string key, long nowMs, out int retryAfterSeconds)
    {
        var queue = _windows.GetOrAdd(key, _ => new Queue<long>());
        lock (queue)
        {
            while (queue.Count > 0 && nowMs - queue.Peek() > _windowMs)
            {
                queue.Dequeue();
            }

            if (queue.Count >= _limit)
            {
                retryAfterSeconds = (int)Math.Ceiling((_windowMs - (nowMs - queue.Peek())) / 1000.0);
                return false;
            }

            queue.Enqueue(nowMs);
            retryAfterSeconds = 0;
            return true;
        }
    }
}
