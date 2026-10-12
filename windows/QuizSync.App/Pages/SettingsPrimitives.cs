using CommunityToolkit.WinUI.Controls;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using QuizSync.App.Controls;
using QuizSync.App.Theme;
using Windows.UI;

namespace QuizSync.App.Pages;

/// <summary>
/// 设置页的基础控件层。**2.0 起改用 Windows 11 原生控件**：
/// 行/字段/开关都是 <see cref="SettingsCard"/>，段头是 Win11 设置页的段头样式，
/// 提示条是 <see cref="InfoBar"/>。
///
/// **为什么要单独一层**：1.1.0 的设置页是「一层基础控件 + 十个分类各拼一遍」，
/// 不是十个页面各写一套。这样间距、描边、字号只有一个出处 ——
/// 本仓库反复踩的坑正是「同一份清单抄在 N 处，改一处漏一处」。
/// **换外衣时这一层就是唯一的改动点**：<c>SettingsTabs.cs</c>（十个分类）基本不动。
///
/// 组织方式照 <c>docs/design-system-plan.md</c> §3：
/// <c>NavigationView</c>（左导航）+ <c>ScrollViewer</c> + <c>SettingsCard</c>（右侧内容），
/// 信息组织照 Windows 11 设置页的习惯 ——
/// 参照物是 WinUI-Gallery 自己的设置页：**段头用 TextBlock，扁平行用 SettingsCard**。
///
/// 间距约定（来自 Gallery <c>Pages/SettingsPage.xaml</c> L11-L21）：
/// 卡片之间 4；段头 <c>Margin=1,30,0,6</c> 且 BasedOn BodyStrong。
/// </summary>
internal static class SettingsUi
{
    /// <summary>卡片之间的间距（Gallery 的 <c>SettingsCardSpacing</c> = 4）。</summary>
    private const double CardSpacing = 4;

    /// <summary>页面标题（26 / w600）。1.1.0 的 <c>settings_page</c> 页头。</summary>
    public static TextBlock Title(string text, FrameworkElement host) => new()
    {
        Text = text,
        FontFamily = UiFont.Demibold,
        FontSize = QsFontSizes.TitleLarge,
        LineHeight = 34,
        Foreground = ShellTheme.Brush("QsOnSurfaceBrush", host),
        TextWrapping = TextWrapping.Wrap,
    };

    /// <summary>页面说明（13.5 / height 1.5）。</summary>
    public static TextBlock PageNote(string text, FrameworkElement host) => new()
    {
        Text = text,
        FontSize = QsFontSizes.Body,
        LineHeight = 20,
        Foreground = ShellTheme.Brush("QsOnSurfaceVariantBrush", host),
        TextWrapping = TextWrapping.Wrap,
    };

    /// <summary>行标题（14.5 / w500）。**独立使用时才走它** —— SettingsCard 内部用自己的排版。</summary>
    public static TextBlock RowTitle(string text, FrameworkElement host) => new()
    {
        Text = text,
        FontSize = QsFontSizes.Body,
        LineHeight = 20,
        Foreground = ShellTheme.Brush("QsOnSurfaceBrush", host),
        TextWrapping = TextWrapping.Wrap,
    };

    /// <summary>行说明（12.5 / height 1.5）。</summary>
    public static TextBlock RowNote(string text, FrameworkElement host) => new()
    {
        Text = text,
        FontSize = QsFontSizes.Caption,
        LineHeight = 19,
        Foreground = ShellTheme.Brush("QsOnSurfaceVariantBrush", host),
        TextWrapping = TextWrapping.Wrap,
    };

    /// <summary>间距。</summary>
    public static Border Gap(double height) => new() { Height = height };

    /// <summary>
    /// 页面段：标题 26 → 说明（间距 8）→ 分隔线（上间距 16）→ 间距 20 → 子模块。
    /// 这是**一个分类的页头**（每个分类一个 Section 起头），不是 Win11 的段头。
    /// </summary>
    public static StackPanel Section(string title, string? note, FrameworkElement host, params UIElement[] blocks)
    {
        var column = new StackPanel();
        column.Children.Add(Title(title, host));

        if (!string.IsNullOrEmpty(note))
        {
            column.Children.Add(Gap(8));
            column.Children.Add(PageNote(note!, host));
        }

        column.Children.Add(Gap(16));
        column.Children.Add(new Border
        {
            Height = 1,
            Background = ShellTheme.Brush("QsOutlineBrush", host),
        });
        column.Children.Add(Gap(20));

        for (var i = 0; i < blocks.Length; i++)
        {
            if (i > 0)
            {
                column.Children.Add(Gap(16));
            }

            column.Children.Add(blocks[i]);
        }

        return column;
    }

    /// <summary>
    /// 段头（Win11 设置页那个「外观与行为」式的标题）：图标 16 + 标题 14/w600。
    ///
    /// 上间距 **12**、下间距 6。Gallery 用的是 30（`SettingsSectionHeaderTextBlockStyle` 的
    /// Margin=1,30,0,6），但**我们有 10 个分类、每页还有多个分组**，照搬 30 会变成
    /// 「一张卡片到下一个分组标题」42 逻辑像素的空档（30 + 块间 Gap），用户当场反馈
    /// 「设置页每个模块距离太远」。12 保留分组层次，同时把页面压紧。
    /// </summary>
    public static StackPanel GroupHeader(string title, string? glyph, FrameworkElement host)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(1, 12, 0, 6),
        };

        if (!string.IsNullOrEmpty(glyph))
        {
            row.Children.Add(new FontIcon
            {
                Glyph = glyph!,
                FontSize = QsFontSizes.Subtitle,
                Foreground = ShellTheme.Brush("QsBrandPrimaryBrush", host),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        row.Children.Add(new TextBlock
        {
            Text = title,
            FontFamily = UiFont.Demibold,
            FontSize = QsFontSizes.Body,
            Foreground = ShellTheme.Brush("QsOnSurfaceBrush", host),
            VerticalAlignment = VerticalAlignment.Center,
        });

        return row;
    }

    /// <summary>
    /// 分组：**Win11 设置页的组织方式** —— 段头（图标 + 标题 + 可选说明）+ 一组
    /// <see cref="SettingsCard"/>，卡片间距 4。
    ///
    /// 原来是「一张大卡片 + 主色标题带 + 行间分隔线」（1.1.0 的样子）。
    /// 换成原生形态后：每个设置项是一张独立的 SettingsCard，段头独立于卡片之上 ——
    /// 这正是 WinUI-Gallery 自己设置页的做法（段头 <c>TextBlock</c> + 扁平行 <c>SettingsCard</c>）。
    /// **行为不变**：原来能看见的行现在一样全部可见（没有折叠）。
    /// </summary>
    public static StackPanel Group(string title, string glyph, string? description, FrameworkElement host, params UIElement[] rows)
    {
        var column = new StackPanel();
        column.Children.Add(GroupHeader(title, glyph, host));

        if (!string.IsNullOrEmpty(description))
        {
            var note = RowNote(description!, host);
            note.Margin = new Thickness(1, 0, 0, 8);
            column.Children.Add(note);
        }

        for (var i = 0; i < rows.Length; i++)
        {
            if (i > 0)
            {
                column.Children.Add(Gap(CardSpacing));
            }

            column.Children.Add(rows[i]);
        }

        return column;
    }

    /// <summary>
    /// 短控件行：<see cref="SettingsCard"/> —— 左侧 Header/Description（+ 可选图标），右侧 Content。
    /// 拖尾控件原来手动加 16 的左边距，现在交给 SettingsCard 的 <c>SettingsCardActionIconMargin</c> 体系。
    /// </summary>
    public static SettingsCard Row(string title, string? subtitle, FrameworkElement host, FrameworkElement? trailing = null, string? glyph = null)
    {
        var card = new SettingsCard
        {
            Header = title,
            IsTabStop = false,
        };

        if (!string.IsNullOrEmpty(subtitle))
        {
            card.Description = subtitle;
        }

        if (!string.IsNullOrEmpty(glyph))
        {
            card.HeaderIcon = new FontIcon { Glyph = glyph! };
        }

        if (trailing is not null)
        {
            card.Content = trailing;
        }

        return card;
    }

    /// <summary>
    /// 宽控件行：标题 + 说明在上，控件在**下方**占满宽度。
    /// 用 <see cref="SettingsCard.ContentAlignment"/> = Vertical 表达这个语义
    /// （枚举的官方说明正是 "The Content is vertically aligned."）。
    /// </summary>
    public static SettingsCard Field(string title, string? subtitle, FrameworkElement host, FrameworkElement control, double maxWidth = 320)
    {
        control.MaxWidth = maxWidth;
        control.HorizontalAlignment = HorizontalAlignment.Left;

        var card = new SettingsCard
        {
            Header = title,
            Content = control,
            ContentAlignment = CommunityToolkit.WinUI.Controls.ContentAlignment.Vertical,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            IsTabStop = false,
        };

        if (!string.IsNullOrEmpty(subtitle))
        {
            card.Description = subtitle;
        }

        return card;
    }

    /// <summary>开关行。<paramref name="onChanged"/> 收到新值，由分类自己决定怎么落库。</summary>
    public static SettingsCard Switch(string title, string? subtitle, FrameworkElement host, bool value, Action<bool> onChanged)
    {
        var toggle = new ToggleSwitch
        {
            IsOn = value,
            OnContent = null,
            OffContent = null,
            MinWidth = 0,
        };
        toggle.Toggled += (_, _) => onChanged(toggle.IsOn);
        return Row(title, subtitle, host, toggle);
    }

    /// <summary>
    /// 警示/提示条：改用原生 <see cref="InfoBar"/>（原来是一块自绘的圆角 Border）。
    /// <paramref name="warn"/> = true 走 Warning，否则 Informational。
    /// 不显示关闭按钮 —— 这些提示是常驻说明，不是可关闭的通知。
    /// </summary>
    public static InfoBar InfoNote(string text, FrameworkElement host, bool warn = false) => new()
    {
        Message = text,
        Severity = warn ? InfoBarSeverity.Warning : InfoBarSeverity.Informational,
        IsOpen = true,
        IsClosable = false,
        IsIconVisible = true,
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };

    /// <summary>状态圆点：默认 8×8 圆（SettingsStatusDot）。原生没有对应件，保留自绘。</summary>
    public static Border StatusDot(Color color, double size = 8) => new()
    {
        Width = size,
        Height = size,
        CornerRadius = new CornerRadius(size / 2),
        Background = new SolidColorBrush(color),
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>进度条：改用原生 <see cref="ProgressBar"/>（原来是自绘的两层 Border）。</summary>
    public static ProgressBar ProgressBar(double ratio, FrameworkElement host) => new()
    {
        Minimum = 0,
        Maximum = 1,
        Value = Math.Clamp(ratio, 0, 1),
        Width = 260,
        HorizontalAlignment = HorizontalAlignment.Left,
    };

    /// <summary>一排胶囊按钮（用在一组互斥选项上）。原生 <c>SelectorBar</c> 语义更近但需要逐项接线，暂留自绘。</summary>
    public static WrapPanel Segments(IReadOnlyList<(string Label, bool Selected, Action OnPick)> items, FrameworkElement host)
    {
        var wrap = new WrapPanel { HorizontalSpacing = 8, VerticalSpacing = 8 };
        foreach (var (label, selected, onPick) in items)
        {
            var button = new Button
            {
                Content = label,
                Padding = new Thickness(16, 6, 16, 6),
                FontFamily = selected ? UiFont.Demibold : UiFont.Regular,
                Background = selected
                    ? ShellTheme.Alpha("QsBrandPrimaryBrush", 0x1F, host)
                    : new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(selected ? 2 : 1),
                BorderBrush = selected
                    ? ShellTheme.Brush("QsBrandPrimaryBrush", host)
                    : ShellTheme.Brush("QsOutlineBrush", host),
                Foreground = selected
                    ? ShellTheme.Brush("QsBrandPrimaryBrush", host)
                    : ShellTheme.Brush("QsOnSurfaceBrush", host),
            };
            PillShape.MakeCapsule(button);
            var pick = onPick;
            button.Click += (_, _) => pick();
            wrap.Children.Add(button);
        }

        return wrap;
    }

    /// <summary>「还没接上」的占位行：**必须出声**，不许静默空操作。</summary>
    public static InfoBar NotYet(string what, FrameworkElement host) =>
        InfoNote($"还没接上：{what}（界面位置已按 1.1.0 留好，功能在后续里程碑补）", host, warn: true);
}
