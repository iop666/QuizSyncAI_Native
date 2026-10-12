using QuizSync.Core;
using Xunit;

namespace QuizSync.Tests;

/// <summary>
/// `UiText.StagingHint` —— 多页暂存条的提示语（规格 ui-spec-desktop §1.2.2 三选一）。
///
/// 为什么值得单独测：这段文案**曾经在两处各写了一份**（`QuizSync.Core` 的这里，
/// 与 `QuizSync.App` 的 `MultipageStaging.Hint()`），改一处忘另一处就会出现
/// 自相矛盾的两句提示。现在 App 侧改成委托到这里，所以这里的期望值就是**唯一的一份**。
///
/// 期望值从规格抄，不从实现读 —— 否则实现改错了用例照样绿。
/// </summary>
public sealed class UiTextStagingHintTests
{
    [Fact]
    public void 零页时提示怎么开始()
    {
        Assert.Equal(
            "按「多页模式」热键开始攒页；抓满 6 张会自动上传识别",
            UiText.StagingHint(0, 6));
    }

    [Fact]
    public void 攒到一半时提示继续加页()
    {
        Assert.Equal(
            "继续按「多页模式」加页；抓满 6 张自动上传，或按「截屏识别」结束多页立刻上传",
            UiText.StagingHint(3, 6));
    }

    [Fact]
    public void 抓满时提示正在自动上传()
    {
        Assert.Equal(
            "已抓满 6 张，正在自动上传识别；也可以按「截屏识别」立刻上传",
            UiText.StagingHint(6, 6));
    }

    [Fact]
    public void 超过上限也走抓满那一支()
    {
        // 边界：`staged >= limit`。写成 `== limit` 的话，越界一页就会掉回「继续加页」，
        // 而那一刻其实已经在自动上传了 —— 两句话自相矛盾。
        Assert.Equal(UiText.StagingHint(6, 6), UiText.StagingHint(7, 6));
    }

    [Fact]
    public void 上限改了文案里的数字跟着变()
    {
        // 上限是可配的（设置里 1–6），文案里的数字必须来自参数而不是写死 6。
        Assert.Contains("3 张", UiText.StagingHint(0, 3), StringComparison.Ordinal);
        Assert.Contains("3 张", UiText.StagingHint(5, 3), StringComparison.Ordinal);
    }
}
