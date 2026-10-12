using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using QuizSync.App.Theme;
using Windows.UI;

namespace QuizSync.App.Controls;

/// <summary>
/// 状态药丸。复刻 1.1.0 `packages/quizsync_ui/lib/src/widgets.dart:94-102` 的 `StatusPill`。
///
/// 两种形态：
/// * <see cref="Filled"/> = true  → 底色 = <see cref="PillColor"/> @12%，无描边，文字白；
/// * <see cref="Filled"/> = false → 底色透明，1px 描边 = <see cref="PillColor"/> @55%，文字主色。
///
/// 颜色**由调用方给**（会话状态色、标绿/标黄等），这里不做任何主题推导 ——
/// 1.1.0 里这些色也是调用方传进来的常量，不随种子色变。
/// </summary>
public sealed partial class StatusPill : UserControl
{
    public StatusPill()
    {
        InitializeComponent();
        // 药丸的圆角必须按实测尺寸算：XAML 里的 999 在 WinUI 里会退化成椭圆（见 PillShape 的注释）。
        PillShape.MakeCapsule(PillShell);
        Apply();
    }

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(StatusPill),
        new PropertyMetadata(string.Empty, (d, _) => ((StatusPill)d).Apply()));

    public static readonly DependencyProperty PillColorProperty = DependencyProperty.Register(
        nameof(PillColor), typeof(Color), typeof(StatusPill),
        new PropertyMetadata(Colors.Gray, (d, _) => ((StatusPill)d).Apply()));

    public static readonly DependencyProperty FilledProperty = DependencyProperty.Register(
        nameof(Filled), typeof(bool), typeof(StatusPill),
        new PropertyMetadata(false, (d, _) => ((StatusPill)d).Apply()));

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(StatusPill),
        new PropertyMetadata(null, (d, _) => ((StatusPill)d).Apply()));

    /// <summary>药丸文字。</summary>
    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>主色（文字色 / 描边色）。</summary>
    public Color PillColor
    {
        get => (Color)GetValue(PillColorProperty);
        set => SetValue(PillColorProperty, value);
    }

    /// <summary>true = 实心底 + 白字；false = 描边 + 主色字。</summary>
    public bool Filled
    {
        get => (bool)GetValue(FilledProperty);
        set => SetValue(FilledProperty, value);
    }

    /// <summary>可选的 Segoe Fluent Icons 字形（如 <c>"\uE8B7"</c>）。</summary>
    public string? Glyph
    {
        get => (string?)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    private void Apply()
    {
        if (CaptionText is null)
        {
            return;
        }

        CaptionText.Text = Label ?? string.Empty;
        CaptionText.Foreground = new SolidColorBrush(Filled ? Colors.White : PillColor);

        PillShell.Background = Filled ? new SolidColorBrush(WithAlpha(PillColor, 0x1F)) : null;
        PillShell.BorderBrush = Filled ? null : new SolidColorBrush(WithAlpha(PillColor, 0x8C));
        PillShell.BorderThickness = Filled ? new Thickness(0) : new Thickness(1);

        if (string.IsNullOrEmpty(Glyph))
        {
            GlyphIcon.Visibility = Visibility.Collapsed;
        }
        else
        {
            GlyphIcon.Glyph = Glyph!;
            GlyphIcon.Foreground = CaptionText.Foreground;
            GlyphIcon.Visibility = Visibility.Visible;
        }
    }

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);
}
