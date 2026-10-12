using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace QuizSync.App.Theme;

/// <summary>
/// 2.0 的动效层（P5）。**重构前 Windows 侧的动画数量实测是 0** —— 这一层是从零建的，
/// 所以它同时也是「动画纪律」的唯一入口：
///
/// ① 时长/曲线/位移**一律**取 <see cref="QsMotion"/>（由 <c>tools/gen_design_tokens.py</c>
///    从 <c>design/tokens.json</c> 生成），这里不许出现字面量；
/// ② 每个动画都要先问 <see cref="QsMotion.AnimationsDisabled"/> —— 尊重系统
///    「辅助功能 → 视觉效果 → 动画效果」是无障碍要求，不是可选项；
/// ③ 只做三种动效，且都有明确目的：
///    * <see cref="Navigations"/> 走 WinUI **原生转场**（前进 DrillIn / 返回 SlideFromLeft），
///      目的是让「压栈了一层」这件事看得见；
///    * <see cref="PageEntrance"/> 页面首帧淡入 + 8px 上移，目的是遮住首帧布局抖动；
///    * <see cref="ContentSwap"/> 原地换内容纯淡入（120ms），目的是让「内容被替换了」有反馈。
///
/// **刻意不做**（用户明确禁止）：弹跳、回弹、缩放、页面横向乱飞、任何循环/持续动画、
/// 列表逐项错峰入场（那属于「为了好看而加动画」，而且会让操作变慢）。
/// </summary>
internal static class QsAnimations
{
    // ===================================================================
    // 导航转场（原生 NavigationTransitionInfo）
    // ===================================================================

    /// <summary>
    /// 压栈打开一个页面，用 WinUI 原生的「钻入」转场（<see cref="DrillInNavigationTransitionInfo"/>）。
    ///
    /// 为什么不自己写滑动动画：原生转场会**同时**处理新页进入、旧页退出、以及
    /// 与输入方向的关系，自绘的版本在快速连点时会互相打架（后退键连按两下就能看出来）。
    /// </summary>
    internal static bool NavigateForward(Frame frame, Type pageType, object? parameter = null)
    {
        if (QsMotion.AnimationsDisabled())
        {
            return frame.Navigate(pageType, parameter, new SuppressNavigationTransitionInfo());
        }

        return frame.Navigate(pageType, parameter, new DrillInNavigationTransitionInfo());
    }

    /// <summary>
    /// 返回上一层。系统动画关掉时用 <see cref="SuppressNavigationTransitionInfo"/> 直接切，
    /// 而不是「照旧播动画但把时长设成 0」—— 后者仍会走一遍转场管线。
    /// </summary>
    internal static bool NavigateBack(Frame frame)
    {
        if (!frame.CanGoBack)
        {
            return false;
        }

        if (QsMotion.AnimationsDisabled())
        {
            frame.GoBack(new SuppressNavigationTransitionInfo());
            return true;
        }

        frame.GoBack(new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromLeft });
        return true;
    }

    // ===================================================================
    // 页面入场 / 内容替换
    // ===================================================================

    /// <summary>
    /// 页面首帧入场：透明度 0→1 + 从下方 <see cref="QsMotion.EnterOffset"/> 像素归位。
    ///
    /// 在页面的 <c>Loaded</c> 里调一次。**每次都从 0 开始**是有意的：只播一次、
    /// 不随滚动或数据变化重播（重播就是「无意义的持续动画」）。
    /// </summary>
    internal static void PageEntrance(UIElement element)
    {
        Run(element, opacityFrom: 0, offsetFrom: QsMotion.EnterOffset, durationMs: QsMotion.NormalMs);
    }

    /// <summary>
    /// 原地换内容（设置页切分类）：只淡入，不位移。
    ///
    /// 为什么给位移 0：内容替换发生在同一个容器里，位移会让人以为「页面滚动了」——
    /// 而这里滚动位置恰好被重置到顶部，两个动作叠在一起会像抖动。
    /// </summary>
    internal static void ContentSwap(UIElement element)
    {
        Run(element, opacityFrom: 0, offsetFrom: 0, durationMs: QsMotion.FastMs);
    }

    /// <summary>
    /// 三种动效共用的实现：淡入 + 上移归位，一条 Storyboard 两个 DoubleAnimation。
    /// </summary>
    private static void Run(UIElement element, double opacityFrom, double offsetFrom, double durationMs)
    {
        if (QsMotion.AnimationsDisabled())
        {
            // 关键：动画关掉时**必须把元素恢复成完全可见**。少这一句就是「用户关了动画，页面空白」。
            element.Opacity = 1;
            if (element is FrameworkElement offElement && offElement.RenderTransform is TranslateTransform offTransform)
            {
                offTransform.Y = 0;
            }

            return;
        }

        var duration = new Duration(TimeSpan.FromMilliseconds(durationMs));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var storyboard = new Storyboard();

        var fade = new DoubleAnimation
        {
            From = opacityFrom,
            To = 1,
            Duration = duration,
            EasingFunction = ease,
        };
        Storyboard.SetTarget(fade, element);
        Storyboard.SetTargetProperty(fade, "Opacity");
        storyboard.Children.Add(fade);

        if (offsetFrom != 0)
        {
            // 位移要落在 RenderTransform 上（改 Margin 会触发布局，整页重排 = 掉帧）。
            var translate = element.RenderTransform as TranslateTransform ?? new TranslateTransform();
            element.RenderTransform = translate;
            translate.Y = offsetFrom;

            var slide = new DoubleAnimation
            {
                From = offsetFrom,
                To = 0,
                Duration = duration,
                EasingFunction = ease,
            };
            Storyboard.SetTarget(slide, element);
            Storyboard.SetTargetProperty(slide, "(UIElement.RenderTransform).(TranslateTransform.Y)");
            storyboard.Children.Add(slide);
        }

        storyboard.Begin();
    }
}
