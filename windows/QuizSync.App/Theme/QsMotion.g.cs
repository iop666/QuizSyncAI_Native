// 本文件由 tools/gen_design_tokens.py 从 design/tokens.json 生成，请勿手改。
// 校验：跑一次 tools/gen_design_tokens.py，加参数 check。
//
// 动效的唯一来源是 design/tokens.json 的 motion 段：任何时长都不得超过 400ms，
// 由生成器断言（用户明确禁止「无意义的持续动画 / 页面乱飞 / 大量弹跳」）。
// 页面与控件里**不允许**再出现动画时长/曲线的字面量。
#nullable enable

using System;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace QuizSync.App.Theme;

/// <summary>跨端动效 token 的 Windows 侧取值。</summary>
internal static class QsMotion
{
    /// <summary>fast：120ms</summary>
    public const double FastMs = 120;
    /// <summary>normal：200ms</summary>
    public const double NormalMs = 200;
    /// <summary>slow：300ms</summary>
    public const double SlowMs = 300;

    /// <summary>入场位移：8px（上限 24）</summary>
    public const double EnterOffset = 8;

    /// <summary>曲线：入场用 Decelerate，出场用 Accelerate，两者都在场用 Standard。</summary>
    /// <summary>standard：cubic-bezier(0.2, 0.0, 0.0, 1.0)</summary>
    public static KeySpline Standard() => new()
    {
        ControlPoint1 = new Point(0.2, 0.0),
        ControlPoint2 = new Point(0.0, 1.0),
    };

    /// <summary>decelerate：cubic-bezier(0.1, 0.9, 0.2, 1.0)</summary>
    public static KeySpline Decelerate() => new()
    {
        ControlPoint1 = new Point(0.1, 0.9),
        ControlPoint2 = new Point(0.2, 1.0),
    };

    /// <summary>accelerate：cubic-bezier(0.9, 0.1, 1.0, 0.2)</summary>
    public static KeySpline Accelerate() => new()
    {
        ControlPoint1 = new Point(0.9, 0.1),
        ControlPoint2 = new Point(1.0, 0.2),
    };

    /// <summary>常用时长的 TimeSpan 形式；默认 normal，需要更短/更长时显式给参数。</summary>
    public static TimeSpan Duration(bool slow = false, bool fast = false) =>
        TimeSpan.FromMilliseconds(fast ? FastMs : slow ? SlowMs : NormalMs);

    /// <summary>
    /// 系统「辅助功能 → 视觉效果 → 动画效果」关掉了吗。
    /// **每个自绘动画都必须先问这一句**：尊重这个开关是无障碍要求，不是可选项。
    /// UISettings 实例缓存住（每次都 new 会走一次 COM 创建，动画路径上不划算）。
    /// </summary>
    public static bool AnimationsDisabled()
    {
        try
        {
            _settings ??= new UISettings();
            return !_settings.AnimationsEnabled;
        }
        catch (Exception)
        {
            // 拿不到系统设置时按「用户不要动画」处理：宁可不动，也不要动错。
            return true;
        }
    }

    private static UISettings? _settings;
}
