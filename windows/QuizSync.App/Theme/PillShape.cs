using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace QuizSync.App.Theme;

/// <summary>
/// 把 Border 变成**真胶囊**（两端半圆）。
///
/// ⚠️ 这里踩过一个被用户当场指出来的观感 bug，必须写清楚：
/// 1.1.0 里药丸的圆角是 <c>AppRadius.chip = 999</c>，那在 Flutter 里是 <c>StadiumBorder</c> 语义 ——
/// **会自动夹到「半高」**，所以画出来是两头半圆的胶囊。
/// 但 **WinUI 的 <c>CornerRadius</c> 不做这个夹取**：当半径 > 宽高的一半时，
/// 圆角矩形会**退化成椭圆**。照抄 999 的结果是每一个药丸都成了椭圆（实测取证：
/// 「已完成」药丸 139×48，「圆角 999」在距顶 6px 处宽度只有 77px，
/// 胶囊应为约 123px，而椭圆公式算出 92px、逐行与实测吻合）。
///
/// 所以：**XAML 里写 999 只是标记，真正的半径必须按实测尺寸算**，
/// 统一走 <see cref="MakeCapsule"/>（半径 = min(宽,高)/2，尺寸一变就重算）。
/// </summary>
internal static class PillShape
{
    /// <summary>让这个 Border 变成胶囊。尺寸变化时自动重算半径。</summary>
    public static void MakeCapsule(Border border)
    {
        void Update()
        {
            var radius = Math.Min(border.ActualWidth, border.ActualHeight) / 2;
            if (radius > 0 && Math.Abs(border.CornerRadius.TopLeft - radius) > 0.5)
            {
                border.CornerRadius = new CornerRadius(radius);
            }
        }

        border.SizeChanged += (_, _) => Update();

        // 首次可能还没量过尺寸，Loaded 后再算一次。
        if (border.IsLoaded)
        {
            Update();
        }
        else
        {
            border.Loaded += (_, _) => Update();
        }
    }

    /// <summary>页面级：把某个名字下的 Border 全变成胶囊（找不到就跳过）。</summary>
    public static void MakeCapsule(FrameworkElement root, string name)
    {
        if (root.FindName(name) is Border border)
        {
            MakeCapsule(border);
        }
    }

    /// <summary>
    /// Button 这类控件自己也有 <c>CornerRadius</c>（悬停/按下时露出来的那层底就是它）。
    /// 只把里面的 Border 改成胶囊是不够的 —— 鼠标一悬停就会露出椭圆形的底。
    /// </summary>
    public static void MakeCapsule(Control control)
    {
        void Update()
        {
            var radius = Math.Min(control.ActualWidth, control.ActualHeight) / 2;
            if (radius > 0 && Math.Abs(control.CornerRadius.TopLeft - radius) > 0.5)
            {
                control.CornerRadius = new CornerRadius(radius);
            }
        }

        control.SizeChanged += (_, _) => Update();
        if (control.IsLoaded)
        {
            Update();
        }
        else
        {
            control.Loaded += (_, _) => Update();
        }
    }
}
