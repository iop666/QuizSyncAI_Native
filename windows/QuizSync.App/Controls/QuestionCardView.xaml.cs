using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using QuizSync.App.Theme;
using QuizSync.Core;
using Windows.UI;

namespace QuizSync.App.Controls;

/// <summary>
/// 题目卡片。逐项复刻 1.1.0 <c>packages/quizsync_ui/lib/src/question_card.dart</c>。
/// 每段样式后面标了它在规格文档里的条目号（ui-spec-desktop §4.2 的 ①–⑩）。
///
/// **一个已知的保真缺口**：1.1.0 用 <c>MathText</c> 渲染 LaTeX / Markdown 表 / 化学式小标，
/// 这一版先按纯文本渲染（换行 + 行高一致）。公式渲染是独立的一大块，另行排期，
/// 不要在这里假装已经支持了。
/// </summary>
public sealed partial class QuestionCardView : UserControl
{
    /// <summary>
    /// 注意**不要**把 <see cref="Question"/> 暴露成 public 属性：WinUI 的 XAML 类型信息
    /// 生成器（XamlTypeInfo.g.cs）会为 UserControl 的 public 属性生成构造代码，
    /// 而 <see cref="Question"/> 是带 <c>required</c> 成员的 record，生成出来的
    /// <c>new Question()</c> 通不过编译（CS9035）。这里刻意只留私有字段。
    /// </summary>
    private Question? _question;

    private double _fontSize = 16;
    private int _weight = 500;
    private bool _alwaysShowConfidence;
    private bool _materialOpen;

    public QuestionCardView()
    {
        InitializeComponent();
    }

    /// <summary>题库正文字号默认值（1.1.0 <c>settings.app.fontSize</c> 默认 16，范围 12–32）。</summary>
    public const double DefaultFontSize = QsFontSizes.Subtitle;

    /// <summary>题库正文字重默认值。1.1.0 构造函数的字面默认是 400，但全新安装走
    /// <c>fromMap</c> 的兜底常量 500，实际生效的是 **500**（ui-spec-desktop §5.4 的注）。</summary>
    public const int DefaultFontWeight = 500;

    public void Render(Question question, double fontSize = DefaultFontSize, int fontWeight = DefaultFontWeight, bool alwaysShowConfidence = false)
    {
        _question = question;
        _fontSize = fontSize;
        _weight = fontWeight;
        _alwaysShowConfidence = alwaysShowConfidence;
        _materialOpen = false;
        Build();
    }

    // ===================================================================
    // 组装
    // ===================================================================
    private void Build()
    {
        Body.Children.Clear();
        if (_question is not { } q)
        {
            return;
        }

        var hit = Highlight.Compute(q);

        // 卡片外框：题目不全 → 2px incomplete 描边（question_card.dart:50-57）。
        Card.BorderThickness = new Thickness(q.Incomplete ? 2 : 1);
        Card.BorderBrush = ShellTheme.Brush(q.Incomplete ? "QsIncompleteBrush" : "QsCardStrokeBrush", this);

        // ① 题目不全横幅
        if (q.Incomplete)
        {
            Body.Children.Add(IncompleteBanner(q));
            Body.Children.Add(Gap(8));
        }

        // ② 头部
        Body.Children.Add(Header(q, hit));
        Body.Children.Add(Gap(12));

        // ③ 阅读材料（默认折叠）
        if (UiText.MaterialLineCount(q.Material) > 0)
        {
            Body.Children.Add(MaterialPanel(q));
            Body.Children.Add(Gap(12));
        }

        // ④ 题干
        Body.Children.Add(Paragraph(q.Stem, _fontSize, _weight, ShellTheme.Brush("QsCardFgBrush", this), 1.6));

        // ⑤ 选项
        if (q.Options.Count > 0)
        {
            Body.Children.Add(Gap(12));
            Body.Children.Add(hit.IsJudge ? JudgeOptions(q, hit) : OptionRows(q, hit));
        }

        // ⑥ 答案块（answer.text 非空）
        if (hit.HighlightAnswerText)
        {
            Body.Children.Add(Gap(12));
            Body.Children.Add(AnswerBlock(q, hit));
        }

        // ⑦ 无答案提示
        if (hit.NoAnswer)
        {
            Body.Children.Add(Gap(12));
            Body.Children.Add(NoAnswerHint());
        }

        // ⑧ 解析块
        if (!string.IsNullOrWhiteSpace(q.Analysis))
        {
            Body.Children.Add(Gap(12));
            Body.Children.Add(AnalysisBlock(q));
        }

        // ⑨ AI 告警块
        foreach (var warning in q.Warnings.Where(w => !string.IsNullOrWhiteSpace(w)))
        {
            Body.Children.Add(WarningBlock(warning));
        }

        // ⑩ 已人工修改
        if (q is { } withEdits && HasManualEdits(withEdits))
        {
            Body.Children.Add(Gap(8));
            Body.Children.Add(new StatusPill
            {
                Label = "已人工修改",
                Glyph = "\uE70F",
                PillColor = ShellTheme.Color("QsBrandPrimaryBrush", this),
                Filled = false,
                HorizontalAlignment = HorizontalAlignment.Left,
            });
        }
    }

    /// <summary>1.1.0 里 `answerEdited || analysisEdited` 才显示「已人工修改」
    /// （question_card.dart:113-121）。这两个标记在 `questions` 表里是 `answer_edited` /
    /// `analysis_edited`；C# 的 <see cref="Question"/> 目前没有对应字段，
    /// 所以这里恒为 false —— **如实留白，不假装支持**（见 docs/DECISIONS.md）。</summary>
    private static bool HasManualEdits(Question _) => false;

    // ===================================================================
    // ① 题目不全横幅（question_card.dart:136-166）
    // ===================================================================
    private Border IncompleteBanner(Question q)
    {
        var accent = ShellTheme.Brush("QsIncompleteBrush", this);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(Glyph("\uE7BA", 16, accent));
        row.Children.Add(new TextBlock
        {
            Text = UiText.IncompleteBanner(q.AnswerGuessed),
            FontFamily = UiFont.Demibold,
            FontSize = _fontSize * 0.82,
            LineHeight = _fontSize * 0.82 * 1.4,
            Foreground = accent,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        });

        return new Border
        {
            Background = ShellTheme.Brush("QsIncompleteBgBrush", this),
            BorderBrush = accent,
            BorderThickness = new Thickness(4, 0, 0, 0),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 7, 10, 7),
            Child = row,
        };
    }

    // ===================================================================
    // ② 头部（question_card.dart:168-265）
    // ===================================================================
    private WrapPanel Header(Question q, HighlightResult hit)
    {
        var secondary = ShellTheme.Brush("QsOnSurfaceVariantBrush", this);
        var wrap = new WrapPanel { HorizontalSpacing = 8, VerticalSpacing = 4 };

        // 1 序号（不带点号）
        wrap.Children.Add(new TextBlock
        {
            Text = (q.Ordinal + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
            FontFamily = UiFont.Bold,
            FontSize = QsFontSizes.Caption,
            LineHeight = 15,
            Foreground = ShellTheme.Brush("QsBrandPrimaryBrush", this),
            VerticalAlignment = VerticalAlignment.Center,
        });

        // 2 题号徽标
        wrap.Children.Add(Badge(
            ShellTheme.Brush("QsBrandPrimaryBrush", this),
            border: null,
            string.IsNullOrWhiteSpace(q.QuestionNo) ? $"第 {q.Ordinal + 1} 题" : q.QuestionNo!,
            new SolidColorBrush(Colors.White),
            UiFont.Bold,
            paddingH: 9));

        // 3 题型
        wrap.Children.Add(new StatusPill
        {
            Label = UiText.QuestionTypeLabel(q.Type.Wire()),
            PillColor = ShellTheme.Color("QsOnSurfaceVariantBrush", this),
            Filled = false,
        });

        // 4 AI 猜测答案
        if (q.AnswerGuessed)
        {
            var accent = ShellTheme.Brush("QsIncompleteBrush", this);
            wrap.Children.Add(Badge(
                ShellTheme.Brush("QsIncompleteBgBrush", this), accent, "AI 猜测答案", accent, UiFont.Bold));
        }

        // 5 AI 不确定，建议复核（这组颜色**不随明暗变化**）
        if (hit.NeedsReview)
        {
            var accent = ShellTheme.Brush("QsReviewBadgeBrush", this);
            wrap.Children.Add(Badge(
                ShellTheme.Brush("QsReviewBadgeBgBrush", this), accent, "AI 不确定，建议复核", accent, UiFont.Demibold));
        }

        // 6 图中已印答案
        if (q.AnswerInImage)
        {
            wrap.Children.Add(new StatusPill
            {
                Label = "图中已印答案",
                Glyph = "\uE8B9",
                PillColor = ShellTheme.Color("QsOnSurfaceVariantBrush", this),
                Filled = false,
            });
        }

        // 7 把握率
        if (q.Confidence > 0 && (_alwaysShowConfidence || q.Confidence < 1))
        {
            wrap.Children.Add(Tiny(UiText.ConfidenceLabel(q.Confidence), secondary));
        }

        // 8 时间
        wrap.Children.Add(Tiny(UiText.FormatClock(q.UpdatedAt), secondary));

        return wrap;
    }

    // ===================================================================
    // ③ 阅读材料（question_card.dart:613-698）
    // ===================================================================
    private Border MaterialPanel(Question q)
    {
        var lines = UiText.MaterialLineCount(q.Material);
        var column = new StackPanel();

        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        head.Children.Add(Glyph("\uE82D", 15, ShellTheme.Brush("QsReviewBadgeBrush", this)));
        head.Children.Add(new TextBlock
        {
            Text = UiText.MaterialTitle(_materialOpen, lines),
            FontFamily = UiFont.Bold,
            FontSize = _fontSize * 0.8,
            LineHeight = _fontSize * 0.8 * 1.3,
            Foreground = ShellTheme.Brush("QsOnSurfaceBrush", this),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        });

        var toggleContent = new Grid();
        toggleContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toggleContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(head, 0);
        toggleContent.Children.Add(head);

        var chevron = Glyph(_materialOpen ? "\uE70E" : "\uE70D", 18, ShellTheme.Brush("QsOnSurfaceVariantBrush", this));
        chevron.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(chevron, 1);
        toggleContent.Children.Add(chevron);

        var toggle = new Button
        {
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(12, 10, 12, 10),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = toggleContent,
        };
        toggle.Click += (_, _) =>
        {
            _materialOpen = !_materialOpen;
            Build();
        };
        column.Children.Add(toggle);

        if (_materialOpen)
        {
            var body = Paragraph(q.Material, _fontSize * 0.92, 400, ShellTheme.Brush("QsOnSurfaceBrush", this), 1.65);
            body.Margin = new Thickness(12, 0, 12, 12);
            column.Children.Add(body);
        }

        return new Border
        {
            Background = ShellTheme.Brush("QsMaterialBgBrush", this),
            BorderBrush = ShellTheme.Brush("QsMaterialStrokeBrush", this),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = column,
        };
    }

    // ===================================================================
    // ⑤ 选项。判断题 → 「对 / 错」两块（:280-339）；其他 → 一行一个（:341-413）
    // ===================================================================
    private StackPanel JudgeOptions(Question q, HighlightResult hit)
    {
        var options = q.Options.Count > 0
            ? q.Options
            : [new Option("对", "对"), new Option("错", "错")];

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        foreach (var option in options)
        {
            row.Children.Add(JudgeButton(option.Label, hit.OptionLabels.Contains(option.Label), hit));
        }

        return row;
    }

    private Border JudgeButton(string label, bool selected, HighlightResult hit)
    {
        var accent = AccentBrush(hit);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(new TextBlock
        {
            Text = label,
            FontFamily = selected ? UiFont.Bold : UiFont.Regular,
            FontSize = _fontSize,
            Foreground = selected ? accent : ShellTheme.Brush("QsCardFgBrush", this),
            VerticalAlignment = VerticalAlignment.Center,
        });

        if (selected)
        {
            row.Children.Add(Glyph("\uE73E", 18, accent));
        }

        return new Border
        {
            Padding = new Thickness(26, 10, 26, 10),
            CornerRadius = new CornerRadius(10),
            Background = selected ? AccentBackgroundBrush(hit) : null,
            BorderBrush = selected ? accent : ShellTheme.Brush("QsJudgeIdleStrokeBrush", this),
            BorderThickness = new Thickness(selected ? 2 : 1),
            Child = row,
        };
    }

    private StackPanel OptionRows(Question q, HighlightResult hit)
    {
        var column = new StackPanel();
        foreach (var option in q.Options)
        {
            var selected = hit.OptionLabels.Contains(option.Label);
            var accent = AccentBrush(hit);
            var fg = selected ? accent : ShellTheme.Brush("QsCardFgBrush", this);

            // 选项号圆标：直径 fontSize × 1.25 的圆
            var badgeSize = _fontSize * 1.25;
            var badge = new Border
            {
                Width = badgeSize,
                Height = badgeSize,
                CornerRadius = new CornerRadius(badgeSize / 2),
                Background = selected
                    ? new SolidColorBrush(WithAlpha(ShellTheme.Color(AccentKey(hit), this), 0x29))
                    : ShellTheme.Alpha("QsCardFgBrush", 0x0D, this),
                VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock
                {
                    Text = option.Label,
                    FontFamily = UiFont.Bold,
                    FontSize = _fontSize * 0.78,
                    Foreground = fg,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid.SetColumn(badge, 0);
            row.Children.Add(badge);

            var text = Paragraph(option.Text, _fontSize, 400, fg, 1.5);
            text.Margin = new Thickness(10, 1, 0, 0);
            Grid.SetColumn(text, 1);
            row.Children.Add(text);

            if (selected)
            {
                var check = Glyph("\uE73E", _fontSize, accent);
                check.Margin = new Thickness(8, 0, 0, 0);
                check.VerticalAlignment = VerticalAlignment.Top;
                Grid.SetColumn(check, 2);
                row.Children.Add(check);
            }

            column.Children.Add(new Border
            {
                Margin = new Thickness(0, 0, 0, 6),
                Padding = new Thickness(12, 10, 12, 10),
                CornerRadius = new CornerRadius(10),
                Background = selected ? AccentBackgroundBrush(hit) : null,
                BorderBrush = selected ? accent : ShellTheme.Brush("QsOptionMutedBarBrush", this),
                BorderThickness = new Thickness(4, 0, 0, 0),
                Child = row,
            });
        }

        return column;
    }

    // ===================================================================
    // ⑥ 答案块（question_card.dart:450-538）
    // ===================================================================
    private StackPanel AnswerBlock(Question q, HighlightResult hit)
    {
        var accent = AccentBrush(hit);
        var column = new StackPanel();

        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        head.Children.Add(new TextBlock
        {
            Text = "答案",
            FontFamily = UiFont.Bold,
            FontSize = _fontSize * 0.78,
            Foreground = ShellTheme.Brush("QsOnSurfaceVariantBrush", this),
        });
        if (q.AnswerGuessed)
        {
            head.Children.Add(new TextBlock
            {
                Text = "（AI 猜测）",
                FontFamily = UiFont.Bold,
                FontSize = _fontSize * 0.78,
                Foreground = ShellTheme.Brush("QsIncompleteBrush", this),
            });
        }

        column.Children.Add(head);
        column.Children.Add(Gap(5));

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = Paragraph(q.AnswerText ?? string.Empty, _fontSize, 600, accent, 1.5);
        Grid.SetColumn(text, 0);
        row.Children.Add(text);

        // 需要复核时图标从 check_circle 换成 help_outline（question_card.dart:486-489）
        var icon = Glyph(hit.NeedsReview ? "\uE897" : "\uE930", _fontSize, accent);
        icon.Margin = new Thickness(8, 0, 0, 0);
        icon.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(icon, 1);
        row.Children.Add(icon);

        column.Children.Add(new Border
        {
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(10),
            Background = AccentBackgroundBrush(hit),
            BorderBrush = accent,
            BorderThickness = new Thickness(4, 0, 0, 0),
            Child = row,
        });

        return column;
    }

    // ===================================================================
    // ⑦ 无答案提示（question_card.dart:586-610）
    // ===================================================================
    private Border NoAnswerHint()
    {
        var fg = ShellTheme.Brush("QsNoAnswerFgBrush", this);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(Glyph("\uE897", 16, fg));
        row.Children.Add(new TextBlock
        {
            Text = "未识别出答案",
            FontSize = _fontSize * 0.9,
            Foreground = fg,
            VerticalAlignment = VerticalAlignment.Center,
        });

        return new Border
        {
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(10),
            Background = ShellTheme.Brush("QsNoAnswerBgBrush", this),
            BorderBrush = ShellTheme.Brush("QsNoAnswerStrokeBrush", this),
            BorderThickness = new Thickness(1),
            Child = row,
        };
    }

    // ===================================================================
    // ⑧ 解析块（question_card.dart:416-448）
    // ===================================================================
    private Border AnalysisBlock(Question q)
    {
        var secondary = ShellTheme.Brush("QsOnSurfaceVariantBrush", this);
        var column = new StackPanel();

        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        head.Children.Add(Glyph("\uEA80", 14, secondary));
        head.Children.Add(new TextBlock
        {
            Text = "解析",
            FontFamily = UiFont.Bold,
            FontSize = _fontSize * 0.78,
            Foreground = secondary,
            VerticalAlignment = VerticalAlignment.Center,
        });
        column.Children.Add(head);
        column.Children.Add(Gap(6));
        column.Children.Add(Paragraph(q.Analysis, _fontSize * 0.92, 400, ShellTheme.Brush("QsCardFgBrush", this), 1.6));

        return new Border
        {
            Padding = new Thickness(12, 10, 12, 12),
            CornerRadius = new CornerRadius(10),
            Background = ShellTheme.Brush("QsAnalysisBgBrush", this),
            BorderBrush = ShellTheme.Brush("QsAnalysisStrokeBrush", this),
            BorderThickness = new Thickness(1),
            Child = column,
        };
    }

    // ===================================================================
    // ⑨ AI 告警块（question_card.dart:549-584）
    // ===================================================================
    private Border WarningBlock(string warning)
    {
        var accent = ShellTheme.Brush("QsReviewBadgeBrush", this);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(Glyph("\uE946", 15, accent));
        row.Children.Add(new TextBlock
        {
            Text = warning,
            FontSize = _fontSize * 0.82,
            LineHeight = _fontSize * 0.82 * 1.5,
            Foreground = ShellTheme.Brush("QsWarnTextBrush", this),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Top,
        });

        return new Border
        {
            Margin = new Thickness(0, 6, 0, 0),
            Padding = new Thickness(10, 8, 10, 8),
            CornerRadius = new CornerRadius(10),
            Background = ShellTheme.Alpha("QsReviewBadgeBgBrush", 0x8C, this),
            BorderBrush = ShellTheme.Alpha("QsReviewBadgeBrush", 0x59, this),
            BorderThickness = new Thickness(1),
            Child = row,
        };
    }

    // ===================================================================
    // 小工具
    // ===================================================================

    /// <summary>命中色：需要复核 → 黄色系，否则绿色系。**只由 HighlightResult 决定。**</summary>
    private static string AccentKey(HighlightResult hit) => hit.NeedsReview ? "QsHitYellowBrush" : "QsHitGreenBrush";

    private static string AccentBgKey(HighlightResult hit) => hit.NeedsReview ? "QsHitYellowBgBrush" : "QsHitGreenBgBrush";

    private Brush AccentBrush(HighlightResult hit) => ShellTheme.Brush(AccentKey(hit), this);

    private Brush AccentBackgroundBrush(HighlightResult hit) => ShellTheme.Brush(AccentBgKey(hit), this);

    /// <summary>正文段落。<paramref name="lineHeight"/> 是 Flutter 的 <c>height</c> 倍数，
    /// WinUI 的 <c>LineHeight</c> 要的是**像素**，所以乘一次字号。</summary>
    /// <summary>
    /// 正文（题干 / 选项 / 答案 / 材料 / 解析都走这里）。
    ///
    /// **公式与化学式在这一层解决**：交给 <see cref="MathTextBlock"/>，规则对齐 1.1.0 的 `MathText`
    /// （桌面规格 §4.4）。在此之前这里只做 `Text = text`，所以题目里的 `$x^2-2x+3$`、
    /// `ZnCO3`、Markdown 表格全都是**原样显示**的源码 —— 用户的原话是「什么都很原始」。
    /// 纯文本仍然只生成一个 TextBlock（1.1.0 的快路径），没有额外开销。
    /// </summary>
    private static FrameworkElement Paragraph(string text, double size, int weight, Brush foreground, double lineHeight)
    {
        return MathTextBlock.Create(text, size, foreground, weight, lineHeight);
    }

    private static TextBlock Tiny(string text, Brush foreground) => new()
    {
        Text = text,
        FontSize = QsFontSizes.Caption,
        Foreground = foreground,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static FontIcon Glyph(string glyph, double size, Brush foreground) => new()
    {
        Glyph = glyph,
        FontSize = size,
        Foreground = foreground,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static Border Gap(double height) => new() { Height = height };

    /// <summary>
    /// 做一枚**胶囊**徽标（题号 / AI 猜测答案 / AI 不确定建议复核）。
    /// 圆角一律交给 <see cref="PillShape"/>：1.1.0 的 999 在 Flutter 里会被夹成半高，
    /// 而 WinUI 的 <c>CornerRadius</c> 不夹 —— 直接写 999 会画成椭圆。
    /// </summary>
    private static Border Badge(Brush background, Brush? border, string text, Brush foreground, FontFamily family, double paddingH = 8)
    {
        var badge = new Border
        {
            Background = background,
            BorderBrush = border,
            BorderThickness = new Thickness(border is null ? 0 : 1),
            Padding = new Thickness(paddingH, 3, paddingH, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                FontFamily = family,
                FontSize = QsFontSizes.Caption,
                LineHeight = 14,
                Foreground = foreground,
            },
        };
        PillShape.MakeCapsule(badge);
        return badge;
    }

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);
}
