using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using QuizSync.App.Theme;

namespace QuizSync.App.Controls;

/// <summary>
/// 空状态。复刻 1.1.0 `packages/quizsync_ui/lib/src/widgets.dart` 的 `EmptyState`。
/// 结构与间距见同名 .xaml 顶部的注释。
/// </summary>
public sealed partial class EmptyStateView : UserControl
{
    public EmptyStateView()
    {
        InitializeComponent();
        Apply();
    }

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(EmptyStateView),
        new PropertyMetadata("\uE721", (d, _) => ((EmptyStateView)d).Apply()));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(EmptyStateView),
        new PropertyMetadata(string.Empty, (d, _) => ((EmptyStateView)d).Apply()));

    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message), typeof(string), typeof(EmptyStateView),
        new PropertyMetadata(null, (d, _) => ((EmptyStateView)d).Apply()));

    /// <summary>Segoe Fluent Icons 字形。</summary>
    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Message
    {
        get => (string?)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    /// <summary>动作行；调用方往里塞按钮。空则整行不显示。</summary>
    public StackPanel Actions => ActionsHost;

    private void Apply()
    {
        if (TitleText is null)
        {
            return;
        }

        var primary = ShellTheme.Brush("QsBrandPrimaryBrush", this);
        IconDisc.Background = ShellTheme.Alpha("QsBrandPrimaryBrush", 0x1A, this);
        EmptyGlyph.Glyph = Glyph;
        EmptyGlyph.Foreground = new SolidColorBrush(ShellTheme.Color("QsBrandPrimaryBrush", this)) { Opacity = 0.9 };

        TitleText.Text = Title ?? string.Empty;
        MessageText.Text = Message ?? string.Empty;
        MessageText.Visibility = string.IsNullOrEmpty(Message) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>动作行里没有孩子时把它整行收掉（复刻 1.1.0 的 <c>if (actions.isNotEmpty)</c>）。</summary>
    public void RefreshActionVisibility() =>
        ActionsHost.Visibility = ActionsHost.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
}
