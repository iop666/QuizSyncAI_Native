using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace QuizSync.App.Controls;

/// <summary>
/// 横向换行面板。语义严格对齐 Flutter 的 <c>Wrap(spacing:, runSpacing:, crossAxisAlignment:)</c>
/// —— 1.1.0 的题目卡片头部、暂存条、失败面板的动作行全靠它排版，
/// 而 WinUI 3 里**没有**现成的等价控件（<c>ItemsWrapGrid</c>/<c>VariableSizedWrapGrid</c>
/// 都是给列表用的、会强行等尺寸，塞一组宽度不一的药丸会变形）。
///
/// 行为：
/// * <see cref="HorizontalSpacing"/> = 同一行内相邻孩子之间的间距（Flutter 的 <c>spacing</c>）；
/// * <see cref="VerticalSpacing"/> = 行与行之间的间距（Flutter 的 <c>runSpacing</c>）；
/// * 每个孩子按自身期望尺寸摆放，**不拉伸**；
/// * 一行放不下就换行，不裁剪、不缩放。
/// </summary>
public sealed partial class WrapPanel : Panel
{
    public static readonly DependencyProperty HorizontalSpacingProperty = DependencyProperty.Register(
        nameof(HorizontalSpacing), typeof(double), typeof(WrapPanel),
        new PropertyMetadata(0d, (d, _) => ((WrapPanel)d).InvalidateMeasure()));

    public static readonly DependencyProperty VerticalSpacingProperty = DependencyProperty.Register(
        nameof(VerticalSpacing), typeof(double), typeof(WrapPanel),
        new PropertyMetadata(0d, (d, _) => ((WrapPanel)d).InvalidateMeasure()));

    public double HorizontalSpacing
    {
        get => (double)GetValue(HorizontalSpacingProperty);
        set => SetValue(HorizontalSpacingProperty, value);
    }

    public double VerticalSpacing
    {
        get => (double)GetValue(VerticalSpacingProperty);
        set => SetValue(VerticalSpacingProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var maxWidth = double.IsInfinity(availableSize.Width) ? double.PositiveInfinity : availableSize.Width;

        double lineWidth = 0;
        double lineHeight = 0;
        double totalWidth = 0;
        double totalHeight = 0;
        var anyLine = false;

        foreach (var child in Children)
        {
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var desired = child.DesiredSize;

            // 该换行了（且不是本行第一个 —— 单个超宽元素也允许独占一行，不裁掉）。
            if (anyLine && lineWidth + HorizontalSpacing + desired.Width > maxWidth)
            {
                totalWidth = Math.Max(totalWidth, lineWidth);
                totalHeight += lineHeight + VerticalSpacing;
                lineWidth = desired.Width;
                lineHeight = desired.Height;
            }
            else
            {
                lineWidth = anyLine ? lineWidth + HorizontalSpacing + desired.Width : desired.Width;
                lineHeight = Math.Max(lineHeight, desired.Height);
                anyLine = true;
            }
        }

        if (anyLine)
        {
            totalWidth = Math.Max(totalWidth, lineWidth);
            totalHeight += lineHeight;
        }

        return new Size(totalWidth, totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var maxWidth = double.IsInfinity(finalSize.Width) ? double.PositiveInfinity : finalSize.Width;

        double x = 0;
        double y = 0;
        double lineHeight = 0;
        var anyLine = false;

        foreach (var child in Children)
        {
            var desired = child.DesiredSize;

            if (anyLine && x + HorizontalSpacing + desired.Width > maxWidth)
            {
                y += lineHeight + VerticalSpacing;
                x = 0;
                lineHeight = 0;
            }
            else if (anyLine)
            {
                x += HorizontalSpacing;
            }

            child.Arrange(new Rect(x, y, desired.Width, desired.Height));
            x += desired.Width;
            lineHeight = Math.Max(lineHeight, desired.Height);
            anyLine = true;
        }

        // 面板本身按内容高度收拢（不铺满），这样塞进竖向 StackPanel 时不会撑出空白。
        return new Size(finalSize.Width, double.IsInfinity(finalSize.Height) ? y + lineHeight : finalSize.Height);
    }
}
