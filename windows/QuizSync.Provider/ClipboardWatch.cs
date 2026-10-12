using System.Runtime.InteropServices;

namespace QuizSync.Provider;

/// <summary>
/// 「剪贴板里出现了新图片，要不要送去识别」这三步判据（1.1.0 `ClipboardWatcher._pollOnce` 的等价物）：
/// ① 设置里的「剪贴板监听」开着；② 真的读到了一张图；③ 这张图与上一次识别过的**不是同一张**。
///
/// 抽成纯函数是为了能单测 —— 真正的监听是 Win32 消息，测试进程里构造不出来，
/// 而这三步是**唯一会判错的地方**（关掉还识别 = 设置不生效；同一张图反复识别 = 每次复制都重跑）。
/// </summary>
public static class ClipboardWatchPolicy
{
    /// <param name="enabled">设置里的「剪贴板监听」。</param>
    /// <param name="hash">本次读到的图片摘要；读不出图（剪贴板里是文本等）时为 null。</param>
    /// <param name="lastHash">上一次已经送去识别的那张图的摘要。</param>
    public static bool ShouldAnalyze(bool enabled, string? hash, string? lastHash) =>
        enabled
        && !string.IsNullOrEmpty(hash)
        && !string.Equals(hash, lastHash, StringComparison.Ordinal);
}

/// <summary>
/// 系统剪贴板更新通知：`AddClipboardFormatListener(hwnd)` + `WM_CLIPBOARDUPDATE (0x031D)`。
///
/// **为什么不用 1.1.0 那种 700ms 轮询 `GetClipboardSequenceNumber`**：那是纯 Dart 侧拿不到窗口消息
/// 时的退路（定时器 + 轮询，最坏晚 700ms 才反应）。原生侧有现成的窗口消息，事件驱动既不用定时器，
/// 也不会漏掉「复制完立刻切走」的场景。
///
/// 子类化用 comctl32 的 `SetWindowSubclass`（与 `Theme/WindowSizing` 同一理由）：换 WNDPROC 要把原
/// 过程存下来再手工转发，漏一条消息就是一类诡异 bug，而 `DefSubclassProc` 会替我们转发没处理的消息。
/// ⚠️ 子类化 id **必须与别的子类化错开** —— `WindowSizing` 用的是 1，这里是 2。
/// </summary>
public sealed class ClipboardWatchHook : IDisposable
{
    private const uint WmClipboardUpdate = 0x031D;
    private const nuint SubclassId = 2;

    private readonly IntPtr _hwnd;
    private readonly Action _onUpdate;
    private readonly SubclassProcDelegate _proc;   // 必须存字段：委托被 GC 回收后窗口过程就崩了
    private bool _listening;
    private bool _disposed;

    public ClipboardWatchHook(IntPtr hwnd, Action onUpdate)
    {
        _hwnd = hwnd;
        _onUpdate = onUpdate;
        _proc = SubclassProc;
        // 注册与子类化是两件事：注册失败时通知不会来，所以**把它暴露出去**，
        // 让界面能如实说「没在听」，而不是让用户以为功能开着。
        _listening = AddClipboardFormatListener(hwnd);
        SetWindowSubclass(hwnd, _proc, SubclassId, IntPtr.Zero);
    }

    /// <summary>系统是否接受了剪贴板监听注册。</summary>
    public bool Listening => _listening;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        RemoveWindowSubclass(_hwnd, _proc, SubclassId);
        if (_listening)
        {
            RemoveClipboardFormatListener(_hwnd);
            _listening = false;
        }
    }

    private IntPtr SubclassProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam, nuint id, IntPtr data)
    {
        if (message == WmClipboardUpdate)
        {
            _onUpdate();
        }

        return DefSubclassProc(hWnd, message, wParam, lParam);
    }

    private delegate IntPtr SubclassProcDelegate(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam, nuint id, IntPtr data);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProcDelegate callback, nuint id, IntPtr data);

    [DllImport("comctl32.dll")]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProcDelegate callback, nuint id);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hWnd);
}
