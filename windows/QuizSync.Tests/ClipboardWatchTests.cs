using QuizSync.Provider;
using Xunit;

namespace QuizSync.Tests;

/// <summary>
/// 剪贴板监听的判据（`ClipboardWatchPolicy`）与钩子的失败行为。
///
/// 真正的监听是 Win32 窗口消息，测试进程里构造不出来 —— 所以判据被抽成纯函数，
/// 而那三步正是**唯一会判错的地方**：
/// ① 关掉还识别 = 设置不生效；② 同一张图反复识别 = 每次复制都重跑一遍 AI（花用户的钱）。
/// </summary>
public sealed class ClipboardWatchTests
{
    [Fact]
    public void Disabled_means_never_analyze()
    {
        Assert.False(ClipboardWatchPolicy.ShouldAnalyze(enabled: false, "sha256:aaa", null));
        Assert.False(ClipboardWatchPolicy.ShouldAnalyze(enabled: false, "sha256:aaa", "sha256:bbb"));
    }

    [Fact]
    public void A_new_image_is_analyzed()
    {
        Assert.True(ClipboardWatchPolicy.ShouldAnalyze(enabled: true, "sha256:aaa", null));
        Assert.True(ClipboardWatchPolicy.ShouldAnalyze(enabled: true, "sha256:aaa", "sha256:bbb"));
    }

    [Fact]
    public void The_same_image_is_not_analyzed_twice()
    {
        // 复制同一张图、或本程序自己往剪贴板写东西触发的那次更新，都不该再跑一次识别。
        Assert.False(ClipboardWatchPolicy.ShouldAnalyze(enabled: true, "sha256:aaa", "sha256:aaa"));
    }

    [Fact]
    public void Nothing_readable_means_nothing_to_analyze()
    {
        // 剪贴板里是文本（含「复制本题答案」自己写进去的那些）→ 读图返回 null。
        Assert.False(ClipboardWatchPolicy.ShouldAnalyze(enabled: true, null, null));
        Assert.False(ClipboardWatchPolicy.ShouldAnalyze(enabled: true, string.Empty, null));
    }

    [Fact]
    public void A_hook_without_a_window_reports_that_it_is_not_listening()
    {
        // hwnd = 0（没有窗口 / 窗口已销毁）：注册必然失败。
        // 这一条钉住的是**不许假装在听** —— `Listening` 为 false 时界面/日志要如实说。
        using var hook = new ClipboardWatchHook(IntPtr.Zero, () => { });
        Assert.False(hook.Listening);
        hook.Dispose(); // 再来一次也不能抛（Dispose 必须幂等）
    }
}
