using System.Globalization;
using System.Text;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using QuizSync.App.Controls;
using QuizSync.App.Theme;
using QuizSync.Core;
using QuizSync.Data;
using QuizSync.Provider;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace QuizSync.App.Pages;

/// <summary>
/// 主界面。结构与逐项尺寸照 1.1.0 的 <c>HomePage</c>
/// （<c>soundcode/apps/desktop/lib/ui/home_page.dart</c>）；XAML 顶部的注释里有层级树。
///
/// 这里只放「界面 + 交互」，识别链路放在 <see cref="RunCaptureAsync"/>，
/// 它与命令行 <c>QuizSync.Provider.Cli analyze</c> 走**同一套内核**
/// （<c>ScreenJpeg</c> → <c>AnalysisEngine</c> → <c>ResponseParser</c>）。
/// </summary>
public sealed partial class HomePage : Page
{
    /// <summary>历史列表上限。1.1.0 <c>RecentSessions(limit: 50)</c>。</summary>
    private const int SessionLimit = 50;

    /// <summary>会话头在内容区宽度 &lt; 620 时把 4 个图标按钮收成「更多操作」（home_page.dart:1023-1024）。</summary>
    private const double CompactBreakpoint = 620;

    private QuizDatabase? _database;
    private IReadOnlyList<RecordedSession> _sessions = [];
    private string? _selectedId;
    private string? _currentCollectionId;
    private bool _showAll;
    private DispatcherTimer? _ticker;
    private int _elapsedSeconds;
    private bool _busy;

    /// <summary>有没有对话框开着。WinUI 同时只允许一个 ContentDialog，第二个会抛异常。</summary>
    private bool _dialogOpen;

    public HomePage()
    {
        InitializeComponent();
        // 顶栏两枚药丸的圆角必须按实测尺寸算：XAML 里的 999 在 WinUI 里会退化成椭圆。
        PillShape.MakeCapsule(CollectionPillButton);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        // 「显示设置」改完立刻重画题目卡片（字号 / 字重真的落到卡片上）。
        App.DisplaySettingsChanged += OnDisplaySettingsChanged;
        // 悬浮窗的两个动作**转发到这里执行**：浮层是视图，它不自己再写一份重新识别/复制。
        App.ReanalyzeRequested += OnFloatReanalyzeRequested;
        App.CopyRequested += OnFloatCopyRequested;
    }

    /// <summary>
    /// 悬浮窗点了「重新识别」：找到那条会话，走**主界面自己那份**实现。
    /// 按 id 找而不是按下标：悬浮窗的列表与主界面的选择可能不同步，
    /// 用下标会认到另一条会话上（那是最难查的一类 bug）。
    /// </summary>
    private void OnFloatReanalyzeRequested(string sessionId)
    {
        if (_sessions.FirstOrDefault(s => s.SessionId == sessionId) is { } session)
        {
            _ = RetrySessionAsync(session);
        }
    }

    /// <summary>悬浮窗点了「复制识别内容」：与主界面的「复制全部」共用同一段组装。</summary>
    private void OnFloatCopyRequested(string sessionId)
    {
        if (_database is null)
        {
            return;
        }

        var questions = new AnalysisCache(_database).QuestionsOfSession(sessionId);
        if (questions.Count == 0)
        {
            App.Note("这次识别没有题目可复制");
            return;
        }

        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(ComposeSessionText(questions));
        Clipboard.SetContent(package);
        App.Note($"已复制 {questions.Count} 道题");
    }

    /// <summary>
    /// 字号 / 字重 / 主题改了：题目卡片要**当场**按新值重画。
    /// 不重画的话用户只能看到设置页里的预览变了、回主界面还是老样子 ——
    /// 那正是 1.1.0 被反复投诉的「调了没反应」。
    /// </summary>
    private void OnDisplaySettingsChanged()
    {
        if (_database is null)
        {
            return;
        }

        ShowSelectedSession();
    }

    // ===================================================================
    // 生命周期
    // ===================================================================
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // 本地库用**应用自己的**目录：服务端那个库是另一套结构（没有 analysis_cache）。
            _database = QuizDatabase.OpenOrCreate(AppDataDirectory.DatabasePath());
        }
        catch (Exception error)
        {
            ShowFatal($"打不开本地数据库：{error.Message}");
            return;
        }

        try
        {
            // 当前合集：优先用上次用的那个（1.1.0 把 selection 记在设置里）；
            // 它已经不在了（被删）就退回列表第一个；一个都没有就保持 null。
            var settings = AppSettings.Load();
            var collections = CollectionStore.List(_database);
            _currentCollectionId = collections.Any(c => c.CollectionId == settings.CurrentCollectionId)
                ? settings.CurrentCollectionId
                : collections.Count > 0 ? collections[0].CollectionId : null;

            // 解析结果**写回设置**：不写回的话设置里可能一直躺着一个已删除的合集 id，
            // 而新记录按 `CollectionForNewSession` 落到列表第一个上 —— 两边不一致，
            // 表现就是「识别完了，当前合集里还是看不到」（第 170 轮）。
            if (!string.Equals(settings.CurrentCollectionId, _currentCollectionId, StringComparison.Ordinal))
            {
                settings.CurrentCollectionId = _currentCollectionId;
                settings.Save();
            }
        }
        catch (Exception error)
        {
            ShowFatal($"读取任务合集失败：{error.Message}");
            return;
        }

        Reload();
        UpdateChrome();
        // 多页暂存条要跟着页数变（F9 / 悬浮球长按 / 托盘都能加页）。
        App.Staging.Changed += UpdateStagingBar;
        UpdateStagingBar();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        App.Staging.Changed -= UpdateStagingBar;
        _ticker?.Stop();
        _ticker = null;
        App.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        App.ReanalyzeRequested -= OnFloatReanalyzeRequested;
        App.CopyRequested -= OnFloatCopyRequested;
        _database?.Dispose();
        _database = null;
    }

    private bool IsDark => ActualTheme == ElementTheme.Dark;

    // ===================================================================
    // 顶栏 / 侧栏上的常驻元素
    // ===================================================================
    private void UpdateChrome()
    {
        // 热键药丸（home_page.dart:580-594）：有键 → primary；没有 → error，文案「无可用热键」
        var label = App.CaptureHotkeyLabel;
        var usable = !string.IsNullOrWhiteSpace(label);
        HotkeyPill.Glyph = "\uE765";
        HotkeyPill.Label = usable ? label! : "无可用热键";
        HotkeyPill.PillColor = usable
            ? ShellTheme.Color("QsBrandPrimaryBrush", this)
            : ShellTheme.Color("QsStatusFailedBrush", this);
        HotkeyPill.Filled = false;

        // 合集药丸（home_page.dart:408-471）
        var collection = CurrentCollection();
        var name = collection?.Name;
        var all = _showAll && collection is not null;
        CollectionLabel.Text = name is null ? "未选择合集" : all ? $"{name} · 全部记录" : name;
        CollectionGlyph.Glyph = all ? "\uE838" : "\uE8B7";
        var pillColor = name is null
            ? ShellTheme.Color("QsStatusFailedBrush", this)
            : ShellTheme.Color("QsBrandPrimaryBrush", this);
        CollectionLabel.Foreground = new SolidColorBrush(pillColor);
        CollectionGlyph.Foreground = new SolidColorBrush(pillColor);
        var wash = WithAlpha(pillColor, 0x1A);
        var stroke = WithAlpha(pillColor, 0x59);
        CollectionPillButton.Background = new SolidColorBrush(wash);
        CollectionPillButton.BorderBrush = new SolidColorBrush(stroke);

        // 悬停 / 按下时 Fluent 会拿主题资源把底色换掉（换成灰的，与药丸配色不搭）。
        // 就地覆盖这三个键，让交互态也用同一个色系。
        CollectionPillButton.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(WithAlpha(pillColor, 0x2E));
        CollectionPillButton.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(WithAlpha(pillColor, 0x3D));
        CollectionPillButton.Resources["ButtonBorderBrushPointerOver"] = new SolidColorBrush(stroke);
        CollectionPillButton.Resources["ButtonBorderBrushPressed"] = new SolidColorBrush(stroke);

        // 计数（用户 2026-09-27 要求）：从原来的「历史记录」标题右侧药丸，改成
        // 显示在「当前合集 / 全部记录」两个按钮**右边**的一行文字：`当前 N / 共 M 条记录`。
        //   * N = **当前合集**里的条数；* M = 本机全部记录的条数。
        // 两个数都**不随范围切换而变**（切到「全部记录」时 N 仍是当前合集的条数）——
        // 这一对数字回答的是「我选中的合集有多少 / 库里一共多少」，
        // 如果跟着切换变，切一次两个数就一起变，反而看不出合集之间的差别。
        // ⚠️ 两个数都受 SessionLimit 限制，与列表一致：不给用户一个「点了也看不到那么多」的数字。
        if (_database is not null)
        {
            var currentCount = _showAll
                ? CollectionStore.SessionsOf(_database, _currentCollectionId, SessionLimit).Count
                : _sessions.Count;
            var totalCount = _showAll
                ? _sessions.Count
                : SessionRecorder.RecentSessions(_database, SessionLimit).Count;
            SessionCountText.Text = $"当前 {currentCount} / 共 {totalCount} 条记录";
        }
        else
        {
            SessionCountText.Text = string.Empty;
        }

        // 导出按钮只在有当前合集时出现（home_page.dart:69）
        ExportButton.Visibility = collection is null ? Visibility.Collapsed : Visibility.Visible;

        // 欢迎态的动作（home_page.dart:338-344）
        WelcomeState.Glyph = "\uE722";
        WelcomeState.Title = "准备好搜题了";
        WelcomeState.Message = usable
            ? $"按 {label} 截屏，\n或用 Win+Shift+S 截图（自动识别）、Ctrl+V 粘贴、把图片拖进窗口。"
            : "按（热键被其他程序占用，可在设置页更换）截屏，\n或用 Win+Shift+S 截图（自动识别）、Ctrl+V 粘贴、把图片拖进窗口。";
        BuildWelcomeActions();
    }

    private void BuildWelcomeActions()
    {
        WelcomeState.Actions.Children.Clear();

        var capture = new Button
        {
            Content = WithGlyph("\uE722", "立即截屏搜题"),
            Style = (Style)Application.Current.Resources["QsPrimaryButtonStyle"],
        };
        capture.Click += OnCaptureClick;
        WelcomeState.Actions.Children.Add(capture);

        var settings = new Button
        {
            Content = WithGlyph("\uE713", "打开设置"),
            Style = (Style)Application.Current.Resources["QsOutlinedActionStyle"],
        };
        settings.Click += OnSettingsClick;
        WelcomeState.Actions.Children.Add(settings);

        WelcomeState.RefreshActionVisibility();
    }

    private static StackPanel WithGlyph(string glyph, string text)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        panel.Children.Add(new FontIcon { Glyph = glyph, FontSize = 18 });
        panel.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    private void ShowFatal(string message) =>
        ShowEmptyPane("\uE783", message, "本地数据目录不可写时会出现这种情况；检查 %LOCALAPPDATA%\\QuizSyncAI 的权限。");

    // ===================================================================
    // 数据装载
    // ===================================================================
    private void Reload()
    {
        if (_database is null)
        {
            return;
        }

        try
        {
            _sessions = _showAll
                ? SessionRecorder.RecentSessions(_database, SessionLimit)
                : CollectionStore.SessionsOf(_database, _currentCollectionId, SessionLimit);
        }
        catch (Exception error)
        {
            ShowFatal($"读取历史记录失败：{error.Message}");
            return;
        }

        if (_selectedId is null || _sessions.All(s => s.SessionId != _selectedId))
        {
            _selectedId = _sessions.Count > 0 ? _sessions[0].SessionId : null;
        }

        BuildSessionList();
        ShowSelectedSession();
        UpdateChrome();
    }

    private RecordedCollection? CurrentCollection() =>
        _currentCollectionId is null
            ? null
            : CollectionStore.List(_database!).FirstOrDefault(c => c.CollectionId == _currentCollectionId);

    /// <summary>
    /// 新识别出来的记录该归入哪个合集 —— **与左侧列表看到的那个合集严格同一个**。
    ///
    /// 为什么必须有这个函数（第 170 轮，用户报「api识别后当前合计看不到（也看不到图片缩略图），
    /// 悬浮窗也不显示内容」）：三个 `SessionRecorder.Record(...)` 调用点**都没传 `collectionId`**，
    /// 于是每一条新记录都是「未归类」。而主界面左侧列表、悬浮窗读的都是
    /// `CollectionStore.SessionsOf(当前合集)` —— 新记录一条都不在里面：
    /// 主界面显示「当前 0 / 共 19 条记录」、没有缩略图（没有会话项就没有图），
    /// 悬浮窗显示「还没有识别记录」。用户看到的就是「识别成功了，但哪儿都没有」。
    ///
    /// 口径与 <see cref="OnLoaded"/> 里解析 `_currentCollectionId` 的规则**逐字一致**
    /// （设置里的那个还在就用它；它被删了就用列表第一个；一个合集都没有才为 null）——
    /// 两份规则如果分叉，就会出现「识别进去的合集不是列表在看的合集」。
    ///
    /// 契约依据：`docs/SPEC.md` §2（「Windows 每次打开都必须先进入一个合集」、
    /// 「未选中合集时不允许开始任何识别」）。「未归类」只会由**删除合集**产生
    /// （「删除合集不级联删除其下记录，记录回到「全部记录」」）。
    /// </summary>
    private static string? CollectionForNewSession(QuizDatabase database)
    {
        var collections = CollectionStore.List(database);
        var current = AppSettings.Load().CurrentCollectionId;
        if (current is { Length: > 0 } && collections.Any(c => c.CollectionId == current))
        {
            return current;
        }

        return collections.Count > 0 ? collections[0].CollectionId : null;
    }

    // ===================================================================
    // 左侧会话列表（home_page.dart:657-765）
    // ===================================================================
    private void BuildSessionList()
    {
        SessionList.Children.Clear();

        if (_sessions.Count == 0)
        {
            SessionList.Visibility = Visibility.Collapsed;
            SidebarEmpty.Visibility = Visibility.Visible;

            var collection = CurrentCollection();
            var hotkey = App.CaptureHotkeyLabel ?? "全局热键";
            if (_showAll)
            {
                SidebarEmpty.Glyph = "\uE81C";
                SidebarEmpty.Title = "还没有识别记录";
                SidebarEmpty.Message = $"按 {hotkey} 截屏搜题，\n或把图片直接拖进窗口。";
            }
            else if (collection is null)
            {
                SidebarEmpty.Glyph = "\uE8B7";
                SidebarEmpty.Title = "这个合集还没有记录";
                SidebarEmpty.Message = $"按 {hotkey} 截屏搜题，结果会记进当前合集。";
            }
            else
            {
                SidebarEmpty.Glyph = "\uE8B7";
                SidebarEmpty.Title = "这个合集还没有记录";
                SidebarEmpty.Message = $"按 {hotkey} 截屏搜题，结果会记进「{collection.Name}」。\n也可以切到「全部记录」看看以前搜过的。";
            }

            return;
        }

        SessionList.Visibility = Visibility.Visible;
        SidebarEmpty.Visibility = Visibility.Collapsed;

        foreach (var session in _sessions)
        {
            SessionList.Children.Add(BuildSessionTile(session));
        }
    }

    /// <summary>
    /// 会话列表项 <c>_SessionTile</c>（home_page.dart:767-937）：
    /// 选中底 primary@10% + 1px primary@55% 描边；缩略图 54×42 圆角 8；
    /// 标题 bodyMedium+w600（失败时用状态色）；副行 = 6×6 状态圆点 + 时间 · 状态 · 来源。
    /// </summary>
    private Button BuildSessionTile(RecordedSession session)
    {
        var style = UiText.Status(session.Status, IsDark);
        var statusColor = ColorFromArgb(style.Argb);
        var selected = session.SessionId == _selectedId;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 缩略图：有本地图就显示，否则用状态图标占位
        var thumb = new Border
        {
            Width = 54,
            Height = 42,
            CornerRadius = new CornerRadius(8),
            Background = ShellTheme.Brush("QsSurfaceContainerHighestBrush", this),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var localPath = _database is null ? null : ImagePruner.GetLocalPath(_database, session.ImageHash);
        if (localPath is not null && File.Exists(localPath))
        {
            thumb.Child = new Image
            {
                Source = new BitmapImage(new Uri(localPath)),
                Stretch = Stretch.UniformToFill,
            };
        }
        else
        {
            thumb.Child = new FontIcon
            {
                Glyph = style.Glyph,
                FontSize = QsFontSizes.Title,
                Foreground = new SolidColorBrush(WithAlpha(statusColor, 0xCC)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        Grid.SetColumn(thumb, 0);
        grid.Children.Add(thumb);

        var column = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        column.Children.Add(new TextBlock
        {
            Text = UiText.SessionHeadline(session.Status, session.ErrorCode, session.ErrorMessage, session.QuestionCount),
            FontFamily = UiFont.Demibold,
            FontSize = QsFontSizes.Body,
            Foreground = session.Status == "failed"
                ? new SolidColorBrush(statusColor)
                : ShellTheme.Brush("QsOnSurfaceBrush", this),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        column.Children.Add(new Border { Height = 3 });

        var meta = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        meta.Children.Add(new Border
        {
            Width = 6,
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(statusColor),
            VerticalAlignment = VerticalAlignment.Center,
        });
        meta.Children.Add(new TextBlock
        {
            Text = $"{UiText.FormatClock(session.CreatedAt)} · {style.Label} · {UiText.SourceLabel(session.SourceDevice, SyncUploader.DeviceId)}",
            FontSize = QsFontSizes.Caption,
            Foreground = ShellTheme.Brush("QsOnSurfaceVariantBrush", this),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        column.Children.Add(meta);

        Grid.SetColumn(column, 1);
        grid.Children.Add(column);

        // 尾部：失败 → 重新分析；识别中 → 转圈；其他 → 右箭头
        FrameworkElement tail;
        if (session.Status == "failed")
        {
            var retry = new Button
            {
                Style = (Style)Application.Current.Resources["QsIconButtonStyle"],
                Content = new FontIcon { Glyph = "\uE72C", FontSize = 18 },
            };
            ToolTipService.SetToolTip(retry, "重新分析");
            retry.Click += (_, _) => _ = RetrySessionAsync(session);
            tail = retry;
        }
        else if (session.Status == "analyzing" || session.Status == "queued")
        {
            tail = new ProgressRing
            {
                IsActive = true,
                Width = 14,
                Height = 14,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(statusColor),
            };
        }
        else
        {
            tail = new FontIcon
            {
                Glyph = "\uE76C",
                FontSize = QsFontSizes.Title,
                Foreground = ShellTheme.Brush("QsOutlineBrush", this),
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        Grid.SetColumn(tail, 2);
        grid.Children.Add(tail);

        // 选中态：底色 primary@10% + 1px primary@55% 描边；未选中**显式给透明画刷**
        // （不要传 null —— 传 null 会把这几个属性留成「未设值」，实测列表项量出来的
        //  外框会比选中项小一圈：280×60 vs 262×42）。
        var tile = new Button
        {
            Style = (Style)Application.Current.Resources["QsSessionTileStyle"],
            Background = selected
                ? ShellTheme.Alpha("QsBrandPrimaryBrush", 0x1A, this)
                : new SolidColorBrush(Colors.Transparent),
            BorderBrush = selected
                ? ShellTheme.Alpha("QsBrandPrimaryBrush", 0x8C, this)
                : new SolidColorBrush(Colors.Transparent),
            Content = grid,
        };
        tile.Click += (_, _) =>
        {
            _selectedId = session.SessionId;
            BuildSessionList();
            ShowSelectedSession();
        };

        return tile;
    }

    // ===================================================================
    // 右侧内容区（home_page.dart:996-1606）
    // ===================================================================
    private void ShowSelectedSession()
    {
        QuestionList.Children.Clear();

        var session = _sessions.FirstOrDefault(s => s.SessionId == _selectedId);
        if (session is null)
        {
            WelcomePane.Visibility = Visibility.Visible;
            SessionHeader.Visibility = Visibility.Collapsed;
            // 分隔线跟着会话头走：会话头不显示时它没有东西可分隔，留着就会与顶栏那条描边叠成 2px。
            SessionHeaderDivider.Visibility = Visibility.Collapsed;
            QuestionsScroll.Visibility = Visibility.Collapsed;
            QuestionsEmptyPane.Visibility = Visibility.Collapsed;
            AnalyzingPane.Visibility = Visibility.Collapsed;
            return;
        }

        WelcomePane.Visibility = Visibility.Collapsed;
        SessionHeader.Visibility = Visibility.Visible;
        SessionHeaderDivider.Visibility = Visibility.Visible;
        AnalyzingPane.Visibility = Visibility.Collapsed;
        QuestionsScroll.Visibility = Visibility.Visible;
        QuestionsEmptyPane.Visibility = Visibility.Collapsed;

        BuildHeader(session);

        IReadOnlyList<Question> questions = _database is null
            ? []
            : new AnalysisCache(_database).QuestionsOfSession(session.SessionId);

        if (questions.Count == 0)
        {
            QuestionsScroll.Visibility = Visibility.Collapsed;
            QuestionsEmptyPane.Visibility = Visibility.Visible;
            QuestionsEmpty.Glyph = session.Status == "failed" ? "\uE783" : "\uE721";
            QuestionsEmpty.Title = session.Status == "cancelled"
                ? "这次识别已取消"
                : session.Status == "failed" ? "识别失败" : "本次没有识别到题目";
            // 规格里这句是「可以换张更清晰的截图，或框选题目区域后重新识别。」——
            // 但**「框选题目区域后重试」这个入口在 2.0 上还没做**（判据 `UiText.CanCropRetry`
            // 早就照规格实现好了，却没有任何界面用它）。
            // 所以这里先只说做得到的那一半：**不承诺界面上没有的东西** ——
            // 用户照着提示去找框选、结果找不到，比少一句提示更糟。
            // 等框选入口接上之后，把这句改回规格原文。
            QuestionsEmpty.Message = session.Status == "failed"
                ? UiText.ErrorReason(session.ErrorCode, session.ErrorMessage)
                : "可以换张更清晰的截图再识别一次。";
            QuestionsEmpty.Actions.Children.Clear();
            QuestionsEmpty.RefreshActionVisibility();
        }
        else
        {
            var fontSize = App.QuestionFontSize;
            var weight = App.QuestionFontWeight;
            foreach (var question in questions)
            {
                var card = new QuestionCardView();
                card.Render(question, fontSize, weight);
                QuestionList.Children.Add(card);
            }
        }

        // 位置文案（用户 2026-09-27 要求「简化」）：原来是「第 3 / 12 条记录」，
        // 现在缩成「3 / 12」—— 它就贴在上一个/下一个图标按钮中间，
        // 「第…条记录」这层包装是冗余的（省下来的宽度还给题目区）。
        NavPosition.Text = _sessions.Count == 0
            ? "0 / 0"
            : $"{_navIndex + 1} / {_sessions.Count}";
        NavPrevButton.IsEnabled = _navIndex < _sessions.Count - 1;
        NavNextButton.IsEnabled = _navIndex > 0;
        // 「回到本次」这个功能用户 2026-09-27 要求**去掉**了（它的出现/消失会让正在点的
        // 上一条/下一条当场位移，体验不值当）。导航现在只有上一条/下一条两个方向键。
    }

    /// <summary>
    /// 字号 / 字重改了之后重画题目卡片。设置页拖完滑块调它 ——
    /// 「改了设置立刻生效」是硬要求（1.1.0 的用户反馈里「调了没反应」是最早被报上来的那类问题）。
    /// </summary>
    internal void RefreshQuestionStyle() => ShowSelectedSession();

    private void BuildHeader(RecordedSession session)
    {
        var style = UiText.Status(session.Status, IsDark);
        var statusColor = ColorFromArgb(style.Argb);
        HeaderPills.Children.Clear();

        HeaderPills.Children.Add(new TextBlock
        {
            Text = UiText.FormatDateMinute(session.CreatedAt),
            FontFamily = UiFont.Demibold,
            FontSize = QsFontSizes.Subtitle,
            Foreground = ShellTheme.Brush("QsOnSurfaceBrush", this),
            VerticalAlignment = VerticalAlignment.Center,
        });

        // 模型紧跟在**识别时间后面**（用户 2026-09-27 要求）。
        // 为什么放这一行而不是原来「药丸行下面单独一行」：它只是一个补充信息，
        // 单独占一行会在题目上方多出一条几乎没有内容的横带，而这一行本来就有富余的横向空间。
        var model = session.AiModel?.Trim();
        if (!string.IsNullOrEmpty(model))
        {
            // 用 StatusPill 而不是裸 TextBlock（用户反馈「特别是模型那一行没对齐」）：
            // 这一行是 WrapPanel，子元素按**行顶对齐**，裸 TextBlock 与旁边药丸的中心线对不上；
            // 做成同一种药丸之后，高度与对齐方式由控件自己保证，不会随字号漂移。
            HeaderPills.Children.Add(new StatusPill
            {
                Label = $"模型 {model}",
                PillColor = ShellTheme.Color("QsOnSurfaceVariantBrush", this),
                Filled = false,
            });
        }

        HeaderPills.Children.Add(new StatusPill
        {
            Glyph = style.Glyph,
            Label = style.Label,
            PillColor = statusColor,
            Filled = true,
        });

        HeaderPills.Children.Add(new StatusPill
        {
            Label = UiText.SourceLabel(session.SourceDevice, SyncUploader.DeviceId),
            PillColor = ShellTheme.Color("QsOnSurfaceVariantBrush", this),
            Filled = false,
        });

        if (session.QuestionCount > 0)
        {
            HeaderPills.Children.Add(new StatusPill
            {
                Label = $"{session.QuestionCount} 题",
                PillColor = ShellTheme.Color("QsOnSurfaceVariantBrush", this),
                Filled = false,
            });
        }

        if (session.LatencyMs > 0)
        {
            HeaderPills.Children.Add(new StatusPill
            {
                Label = $"{(session.LatencyMs / 1000.0).ToString("0.0", CultureInfo.InvariantCulture)}s",
                PillColor = ShellTheme.Color("QsOnSurfaceVariantBrush", this),
                Filled = false,
            });
        }

    }

    /// <summary>内容区宽度跨过 620 时在「4 个图标按钮」与「更多操作」之间切换（home_page.dart:1023-1024,1091-1110）。</summary>
    private void OnContentPaneSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var compact = e.NewSize.Width < CompactBreakpoint;
        HeaderActionsWide.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        HeaderActionsNarrow.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowEmptyPane(string glyph, string title, string message)
    {
        WelcomePane.Visibility = Visibility.Collapsed;
        SessionHeader.Visibility = Visibility.Collapsed;
        SessionHeaderDivider.Visibility = Visibility.Collapsed;
        QuestionsScroll.Visibility = Visibility.Collapsed;
        AnalyzingPane.Visibility = Visibility.Collapsed;
        QuestionsEmptyPane.Visibility = Visibility.Visible;
        QuestionsEmpty.Glyph = glyph;
        QuestionsEmpty.Title = title;
        QuestionsEmpty.Message = message;
    }

    // ===================================================================
    // 底部导航条（home_page.dart:1230-1276 / ui_pkg/src/session_nav.dart）
    // 列表按时间倒序（最新在前）：上一次 = 更早那条（索引 +1），下一次 = 更晚那条（索引 -1）
    // ===================================================================
    private int _navIndex;

    private void SelectByIndex(int index)
    {
        if (index < 0 || index >= _sessions.Count)
        {
            return;
        }

        _navIndex = index;
        _selectedId = _sessions[index].SessionId;
        BuildSessionList();
        ShowSelectedSession();
    }

    private void OnNavPrevClick(object sender, RoutedEventArgs e) => SelectByIndex(_navIndex + 1);

    private void OnNavNextClick(object sender, RoutedEventArgs e) => SelectByIndex(_navIndex - 1);

    // ===================================================================
    // 截屏搜题（与 QuizSync.Provider.Cli analyze 同一套内核）
    // ===================================================================
    private async void OnCaptureClick(object sender, RoutedEventArgs e) => await RunCaptureAsync();

    internal async Task RunCaptureAsync()
    {
        if (_busy)
        {
            return;
        }

        var config = AiSettings.Load();
        if (!AiSettings.IsUsable(config))
        {
            ShowEmptyPane("\uE713", "还没有配置 AI", AiSettings.ExplainMissing());
            return;
        }

        _busy = true;
        CaptureButton.IsEnabled = false;
        ShowAnalyzing();

        try
        {
            // 截屏要在 UI 线程上同步做（内部会临时藏起自己的窗口）。
            var shot = ScreenJpeg.Capture(AppWindowScope.Title);
            if (shot is null)
            {
                ShowEmptyPane("\uE783", "截屏失败", "拿不到屏幕尺寸，可能是显示设置异常。");
                return;
            }

            var outcome = await Task.Run(async () =>
            {
                using var database = QuizDatabase.OpenOrCreate(AppDataDirectory.DatabasePath());
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds + 30) };
                var engine = new AnalysisEngine(
                    new HttpAiProvider(config.ProviderId, http),
                    new AnalysisCache(database),
                    new QuotaGuard(database),
                    deviceId: SyncUploader.DeviceId);

                var result = await engine.AnalyzeImageAsync(shot.Jpeg, shot.ImageHash, config).ConfigureAwait(false);
                if (!result.Ok)
                {
                    return new CaptureResult(result, null, null);
                }

                var store = new LocalStore(database, SyncUploader.DeviceId);
                var sessionId = SessionRecorder.Record(
                    store,
                    shot.ImageHash,
                    SyncUploader.DeviceId,
                    [.. result.Questions.Select(Map)],
                    now: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    aiProvider: config.ProviderId,
                    aiModel: config.Model,
                    latencyMs: result.LatencyMs,
                    cached: result.FromCache,
                    collectionId: CollectionForNewSession(database));

                // 图片落盘 + 建 `images` 行 + 写 local_path。三者必须一起做：
                // 只写文件不建行的话 local_path 的 UPDATE 会打到空处，缩略图永远没有（M15 的同一类坑）。
                ImageStore.Save(
                    database,
                    AppDataDirectory.ImagesDirectory(),
                    shot.ImageHash,
                    shot.Jpeg,
                    SyncUploader.DeviceId,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

                return new CaptureResult(result, sessionId, await TryPushAsync(database).ConfigureAwait(false));
            }).ConfigureAwait(true);

            StopAnalyzing();

            if (!outcome.Outcome.Ok)
            {
                // **失败必须留现场**（第 168 轮）：识别失败原来在日志里一个字都没有 ——
                // 界面上只有一句「识别失败」，用户报「等十几秒然后失败」时我们连错误码、
                // 耗时、AI 原文都拿不到，只能靠猜。这一行让下一次失败当场可查。
                CrashLog.Write(
                    $"本机识别失败：{outcome.Outcome.ErrorCode} · {outcome.Outcome.ErrorMessage}"
                    + $"｜图 {shot.Jpeg.Length} 字节 {shot.Width}×{shot.Height}"
                    + $"｜模型 {config.Model}｜耗时 {outcome.Outcome.LatencyMs} ms"
                    + $"｜调用 {outcome.Outcome.ProviderCalls} 次"
                    + $"｜AI 原文 {DescribeRaw(outcome.Outcome.RawText)}");
                ShowEmptyPane(
                    "\uE783",
                    "识别失败",
                    UiText.ErrorReason(outcome.Outcome.ErrorCode, outcome.Outcome.ErrorMessage));
                return;
            }

            // 成功也留一行（含同步结果）——「识别到了但手机没看到」这类问题就靠这一行分辨。
            CrashLog.Write(
                $"本机识别完成：{outcome.Outcome.Questions.Count} 道题 · {outcome.Outcome.LatencyMs} ms"
                + $" · 模型 {config.Model} · 缓存 {outcome.Outcome.FromCache}；同步：{outcome.PushNotice ?? "(未尝试)"}");
            if (outcome.PushNotice is { Length: > 0 } notice && LooksLikePushProblem(notice))
            {
                // 只在这种**用户会以为"手机能看到了"却其实没推上去**的情况下打扰他。
                App.Note($"识别成功，但没能同步到主机：{notice}");
            }

            Reload();
            if (outcome.SessionId is not null)
            {
                _selectedId = outcome.SessionId;
                _navIndex = 0;
                BuildSessionList();
                ShowSelectedSession();
            }
        }
        catch (Exception error)
        {
            StopAnalyzing();
            ShowEmptyPane("\uE783", "识别失败", error.Message);
        }
        finally
        {
            _busy = false;
            CaptureButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// 结束多页：把暂存页**一次**上传识别（F8 在多页模式里的语义，ui-spec-desktop §3.9）。
    ///
    /// 与单张识别共用同一套内核（<c>AnalyzeImagesAsync</c>），区别只有两点：
    /// * 多页**不走缓存**（内核里 `pages.Count == 1` 才允许命中缓存）；
    /// * 会话的 `image_hash` 取第一页（缩略图与「重新分析」都用它），其余页也落盘备查。
    /// </summary>
    internal async Task RunMultipageAsync(IReadOnlyList<CapturedJpeg> pages) =>
        await RunAnalysisAsync(pages, "多页识别");

    /// <summary>
    /// 托盘的「从剪贴板读取」：把剪贴板里的图当成一页走同一条分析链。
    /// 与截屏/多页**共用** <see cref="RunAnalysisAsync"/>，不另写一份。
    /// </summary>
    internal async Task RunClipboardAsync(byte[] jpeg, string imageHash) =>
        await RunAnalysisAsync([new CapturedJpeg(jpeg, imageHash.Replace("sha256:", string.Empty, StringComparison.Ordinal), 0, 0, 0, 0)], "剪贴板识别");

    private async Task RunAnalysisAsync(IReadOnlyList<CapturedJpeg> pages, string label)
    {
        if (_busy || pages.Count == 0)
        {
            return;
        }

        var config = AiSettings.Load();
        if (!AiSettings.IsUsable(config))
        {
            ShowEmptyPane("\uE713", "还没有配置 AI", AiSettings.ExplainMissing());
            return;
        }

        _busy = true;
        CaptureButton.IsEnabled = false;
        ShowAnalyzing();

        try
        {
            var outcome = await Task.Run(async () =>
            {
                using var database = QuizDatabase.OpenOrCreate(AppDataDirectory.DatabasePath());
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds + 30) };
                var engine = new AnalysisEngine(
                    new HttpAiProvider(config.ProviderId, http),
                    new AnalysisCache(database),
                    new QuotaGuard(database),
                    deviceId: SyncUploader.DeviceId);

                var first = pages[0];
                var result = await engine
                    .AnalyzeImagesAsync([.. pages.Select(p => p.Jpeg)], first.ImageHash, config, useCache: false)
                    .ConfigureAwait(false);

                if (!result.Ok)
                {
                    return new CaptureResult(result, null, null);
                }

                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var store = new LocalStore(database, SyncUploader.DeviceId);
                var sessionId = SessionRecorder.Record(
                    store,
                    first.ImageHash,
                    SyncUploader.DeviceId,
                    [.. result.Questions.Select(Map)],
                    now: now,
                    aiProvider: config.ProviderId,
                    aiModel: config.Model,
                    latencyMs: result.LatencyMs,
                    cached: false,
                    collectionId: CollectionForNewSession(database));

                // 每一页都落盘：第一页供缩略图 / 重新分析，其余页留档。
                foreach (var page in pages)
                {
                    ImageStore.Save(database, AppDataDirectory.ImagesDirectory(), page.ImageHash, page.Jpeg,
                        SyncUploader.DeviceId, now);
                }

                return new CaptureResult(result, sessionId, await TryPushAsync(database).ConfigureAwait(false));
            }).ConfigureAwait(true);

            StopAnalyzing();
            App.SetBallState(BallState.Idle);

            if (!outcome.Outcome.Ok)
            {
                // 与单张识别同一个规矩：失败留现场（错误码 + 耗时 + AI 原文）。
                CrashLog.Write(
                    $"{label}失败：{outcome.Outcome.ErrorCode} · {outcome.Outcome.ErrorMessage}"
                    + $"｜{pages.Count} 页｜模型 {config.Model}｜耗时 {outcome.Outcome.LatencyMs} ms"
                    + $"｜调用 {outcome.Outcome.ProviderCalls} 次｜AI 原文 {DescribeRaw(outcome.Outcome.RawText)}");
                ShowEmptyPane("\uE783", $"{label}失败",
                    UiText.ErrorReason(outcome.Outcome.ErrorCode, outcome.Outcome.ErrorMessage));
                return;
            }

            Reload();
            if (outcome.SessionId is not null)
            {
                _selectedId = outcome.SessionId;
                _navIndex = 0;
                BuildSessionList();
                ShowSelectedSession();
            }

            CrashLog.Write(
                $"{label}完成：{pages.Count} 页 → {outcome.Outcome.Questions.Count} 道题"
                + $" · {outcome.Outcome.LatencyMs} ms；同步：{outcome.PushNotice ?? "(未尝试)"}");
            App.Note($"{label}完成：{pages.Count} 页 → {outcome.Outcome.Questions.Count} 道题"
                + (outcome.PushNotice is { Length: > 0 } multiNotice && LooksLikePushProblem(multiNotice)
                    ? $"（没同步到主机：{multiNotice}）"
                    : string.Empty));
        }
        catch (Exception error)
        {
            StopAnalyzing();
            ShowEmptyPane("\uE783", $"{label}失败", error.Message);
        }
        finally
        {
            _busy = false;
            CaptureButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// 刷新多页暂存条。文案逐字照 ui-spec-desktop §1.2.2：
    /// 有暂存才出现，底色 = 主色 @7%，左侧「多页暂存：N/M 页」+ 提示语 + 清空按钮。
    /// </summary>
    private void UpdateStagingBar()
    {
        var staging = App.Staging;
        var count = staging.Count;

        // 默认态（用户 2026-09-27 要求）：**显示「单张识别」**，而不是整条隐藏。
        // 原因：指示器常驻在顶栏热键药丸后面，忽隐忽现会让后面所有元素左右跳；
        // 而且「现在是什么模式」本身就是用户需要一眼看到的信息。
        //
        // ⚠️ 默认态**不穿药丸外衣**（用户反馈「单张识别那块的按钮异常」）：
        // 上一版无论哪种状态都带 1px 描边 + 底色，于是「单张识别」看起来像一个**按钮**，
        // 点下去却什么都不发生 —— 一个长得像按钮但不能点的东西本身就是缺陷。
        // 现在只有「多页模式」才给药丸外衣与清空按钮：那时它确实有事可做。
        if (count == 0)
        {
            StagingCount.Text = "单张识别";
            StagingGlyph.Glyph = "\uE722";                       // 相机：一张图
            ClearStagingButton.Visibility = Visibility.Collapsed;
            StagingIndicator.BorderThickness = new Thickness(0);
            StagingIndicator.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            StagingIndicator.Padding = new Thickness(4, 3, 4, 3);
            ToolTipService.SetToolTip(
                StagingIndicator,
                "当前是单张识别。长按热键 / 长按悬浮球可进入「一题多页」，那时这里显示已暂存页数。");
            PillShape.MakeCapsule(StagingIndicator);
            return;
        }

        // 多页模式：显示暂存内容（页数 + 上限），并把完整提示语放进 tooltip。
        // 提示语不再横排在这一行里：顶栏只有 32 逻辑像素高，塞进整句会把标题挤没。
        StagingCount.Text = $"多页 {count}/{staging.Limit} 页";
        StagingGlyph.Glyph = "\uE81E";                            // 多页图标
        ClearStagingButton.Visibility = Visibility.Visible;
        StagingIndicator.BorderThickness = new Thickness(1);
        StagingIndicator.Background = ShellTheme.Alpha("QsBrandPrimaryBrush", 0x12, this);
        StagingIndicator.BorderBrush = ShellTheme.Brush("QsOutlineBrush", this);
        StagingIndicator.Padding = new Thickness(9, 3, 4, 3);
        ToolTipService.SetToolTip(StagingIndicator, staging.Hint());
        // 胶囊形按**实测高度**算（见 Theme/PillShape.cs 的说明：WinUI 不会把 999 夹到半高，
        // 直接写 999 会画成椭圆）。放在最后调用，此时高度已经量过。
        PillShape.MakeCapsule(StagingIndicator);
    }

    private sealed record CaptureResult(AnalysisOutcome Outcome, string? SessionId, string? PushNotice);

    /// <summary>`Question` → 落库字段。题型走 <c>QuestionTypes.Wire</c>（协议值是 single/multi/…）。</summary>
    private static RecordedQuestion Map(Question question) => new(
        Ordinal: question.Ordinal,
        Stem: question.Stem,
        Type: question.Type.Wire(),
        QuestionNo: question.QuestionNo,
        AnswerText: question.AnswerText,
        Analysis: question.Analysis,
        Choice: question.Choice,
        Confidence: question.Confidence,
        NeedReview: question.NeedReview,
        Material: question.Material,
        Options: question.Options,
        Incomplete: question.Incomplete,
        AnswerGuessed: question.AnswerGuessed,
        AnswerInImage: question.AnswerInImage,
        Warnings: question.Warnings);

    /// <summary>AI 原文的前若干字符（失败现场里最有用的一段；为空时说清是空的）。</summary>
    private static string DescribeRaw(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "(空)";
        }

        var flat = raw.Replace('\r', ' ').Replace('\n', ' ');
        return flat.Length <= 200 ? flat : flat[..200] + "…";
    }

    /// <summary>`TryPushAsync` 的那句话是不是「没推上去」（而不是「本来就没改动」）。</summary>
    private static bool LooksLikePushProblem(string notice) =>
        notice.Contains("只留在本机", StringComparison.Ordinal)
        || notice.Contains("失败", StringComparison.Ordinal)
        || notice.Contains("读不到", StringComparison.Ordinal);

    /// <summary>推给主机。任何失败只返回一句话、不抛（本地记录已经落库了）。</summary>
    private static async Task<string?> TryPushAsync(QuizDatabase database)    {
        try
        {
            var probe = await new QuizSync.ServerBridge.HostDiscovery().ProbeAsync().ConfigureAwait(false);
            if (probe is null)
            {
                // **再探一次才敢说"主机没起来"**（第 170 轮实测撞到的假红）：
                // 识别完成的那一刻进程里正忙（内嵌宿主刚起、补推刚推完几十条 op、
                // 池线程不够用时 800 ms 的探活超时就会到），于是这里报出
                // 「主机未启动：改动只留在本机」—— 而同一份日志里 3 秒前刚有一行
                // 「已推送 65 条改动」。这种假红会让人以为同步链路坏了。
                // 隔 400 ms 再探一次仍然没有，才认为主机真的不在。
                await Task.Delay(400).ConfigureAwait(false);
                probe = await new QuizSync.ServerBridge.HostDiscovery().ProbeAsync().ConfigureAwait(false);
                if (probe is null)
                {
                    return "主机未启动：改动只留在本机";
                }
            }

            var directory = HostDataDirectory.Find();
            var controlToken = directory is null ? null : QuizSync.ServerBridge.HostControl.FindControlToken(directory);
            if (controlToken is null)
            {
                return "读不到控制令牌：改动只留在本机";
            }

            return await SyncUploader.PushPendingAsync(database, probe.BaseUrl, controlToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            return $"推送失败（改动仍在本机）：{error.Message}";
        }
    }

    private void ShowAnalyzing()
    {
        AnalyzingPane.Visibility = Visibility.Visible;
        SessionHeader.Visibility = Visibility.Collapsed;
        SessionHeaderDivider.Visibility = Visibility.Collapsed;
        QuestionsScroll.Visibility = Visibility.Collapsed;
        QuestionsEmptyPane.Visibility = Visibility.Collapsed;
        WelcomePane.Visibility = Visibility.Collapsed;

        _elapsedSeconds = 0;
        UpdateElapsed();
        // 识别期间悬浮球要变黄（待识别蓝 / 识别中黄 / 多页模式绿，ui-spec-desktop §6.6）——
        // 球是用户在别的窗口里唯一能看见的进度反馈，一直蓝着等于没有反馈。
        App.SetBallState(BallState.Detecting);
        // 悬浮窗的「识别进行中」提示（规格：贴在内容区下部）读的就是这一个全局状态。
        App.SetAnalyzing(true);
        _ticker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _ticker.Tick += (_, _) =>
        {
            _elapsedSeconds++;
            UpdateElapsed();
        };
        _ticker.Start();
    }

    private void UpdateElapsed()
    {
        var timeout = AiSettings.Load().TimeoutSeconds;
        AnalyzingElapsed.Text = $"已耗时 {UiText.FormatSeconds(_elapsedSeconds)}（超时上限 {UiText.FormatSeconds(timeout)}）";
    }

    private void StopAnalyzing()
    {
        _ticker?.Stop();
        _ticker = null;
        AnalyzingPane.Visibility = Visibility.Collapsed;
        App.SetBallState(BallState.Idle);
        App.SetAnalyzing(false);
        // 结果已经落库：悬浮窗重读一次就能看到新记录（它自己不参与识别流程）。
        App.RefreshFloatWindow();
    }

    private void OnCancelAnalyzeClick(object sender, RoutedEventArgs e) => StopAnalyzing();

    private void OnClearStagingClick(object sender, RoutedEventArgs e) => App.ClearStaging();

    // ===================================================================
    // 会话头四个动作（home_page.dart:1091-1110）—— 本轮把「复制全部 / 导出本次 / 删除本次」
    // 做成真的，「重新分析」重读原图重跑 AI。
    // 没接上的一律走 NotYet 明确提示，**不做静默空操作**。
    // ===================================================================
    /// <summary>
    /// 顶栏「导出当前合集」：把当前合集下**所有**识别记录导成一个 Markdown。
    ///
    /// 组装与另存为都在 `ExportSupport.ExportCollectionAsync` —— 设置页的「按合集导出」
    /// 走的是**同一个**方法（原来那边是「还没接上」占位，两处入口一个能用一个不能用）。
    /// 没有合集 / 合集里没有记录时**明确说出来**，不静默返回。
    /// </summary>
    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (_database is null)
        {
            return;
        }

        var collectionId = AppSettings.Load().CurrentCollectionId;
        if (string.IsNullOrEmpty(collectionId))
        {
            App.Note("还没有选合集，先点顶栏的合集名字选一个");
            return;
        }

        try
        {
            App.Note(await ExportSupport.ExportCollectionAsync(_database, collectionId));
        }
        catch (Exception error)
        {
            App.Note($"导出失败：{error.Message}");
        }
    }

    /// <summary>
    /// 顶栏「搜索历史题目」：弹一个搜索框 + 结果列表，点结果跳到那条记录。
    ///
    /// 搜的是**题干 / 解析 / 答案**三处（1.1.0 的搜索范围），走
    /// <see cref="AnalysisCache.SearchQuestions"/>。结果行显示「日期 · 第 N 题 · 题干摘要」。
    /// </summary>
    private async void OnSearchClick(object sender, RoutedEventArgs e)
    {
        if (_database is null)
        {
            return;
        }

        var input = new TextBox
        {
            PlaceholderText = "搜题干 / 解析 / 答案，回车开始",
            Style = (Style)Application.Current.Resources["QsTextBoxStyle"],
        };

        var results = new StackPanel { Spacing = 6 };
        var hint = new TextBlock
        {
            Text = "输入关键字后按回车。",
            Foreground = ShellTheme.Brush("QsOnSurfaceVariantBrush", this),
            FontSize = QsFontSizes.Caption,
        };

        var body = new StackPanel { Spacing = 12, MinWidth = 460 };
        body.Children.Add(input);
        body.Children.Add(hint);
        body.Children.Add(new ScrollViewer
        {
            MaxHeight = 420,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = results,
        });

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "搜索历史题目",
            Content = body,
            CloseButtonText = "关闭",
        };

        var cache = new AnalysisCache(_database);
        var sessions = _sessions.ToDictionary(s => s.SessionId);

        void RunSearch()
        {
            results.Children.Clear();
            var keyword = input.Text?.Trim();
            if (string.IsNullOrEmpty(keyword))
            {
                hint.Text = "输入关键字后按回车。";
                return;
            }

            var hits = cache.SearchQuestions(keyword);
            if (hits.Count == 0)
            {
                hint.Text = $"没有匹配「{keyword}」的题目。";
                return;
            }

            hint.Text = $"匹配 {hits.Count} 道题。";
            foreach (var (sessionId, ordinal) in hits)
            {
                var question = cache.QuestionsOfSession(sessionId).FirstOrDefault(q => q.Ordinal == ordinal);
                if (question is null)
                {
                    continue;
                }

                var when = sessions.TryGetValue(sessionId, out var session)
                    ? UiText.FormatDateMinute(session.CreatedAt)
                    : sessionId;

                var row = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(12, 8, 12, 8),
                    CornerRadius = new CornerRadius(10),
                    BorderThickness = new Thickness(0),
                    Background = ShellTheme.Alpha("QsBrandPrimaryBrush", 0x10, this),
                };

                var caption = new StackPanel { Spacing = 2 };
                caption.Children.Add(new TextBlock
                {
                    Text = $"{when} · 第 {ordinal + 1} 题",
                    FontSize = QsFontSizes.Caption,
                    Foreground = ShellTheme.Brush("QsOnSurfaceVariantBrush", this),
                });
                caption.Children.Add(new TextBlock
                {
                    Text = UiText.Ellipsis(question.Stem, 60),
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = ShellTheme.Brush("QsOnSurfaceBrush", this),
                });
                row.Content = caption;

                var target = sessionId;
                row.Click += (_, _) =>
                {
                    // 跳到那条记录：选中 + 定位到该题（解析从 0 开始，所以用 ordinal）。
                    _selectedId = target;
                    _navIndex = ordinal;
                    dialog.Hide();
                    BuildSessionList();
                    ShowSelectedSession();
                    App.Note($"已跳到第 {ordinal + 1} 题");
                };

                results.Children.Add(row);
            }
        }

        input.KeyDown += (_, args) =>
        {
            if (args.Key == Windows.System.VirtualKey.Enter)
            {
                RunSearch();
            }
        };

        input.Loaded += (_, _) => input.Focus(FocusState.Programmatic);
        await ShowDialogAsync(dialog);
    }

    private void OnRetrySessionClick(object sender, RoutedEventArgs e)
    {
        if (SelectedSession() is { } session)
        {
            _ = RetrySessionAsync(session);
        }
    }

    private void OnCopyAllClick(object sender, RoutedEventArgs e)
    {
        if (_database is null || SelectedSession() is not { } session)
        {
            return;
        }

        var questions = new AnalysisCache(_database).QuestionsOfSession(session.SessionId);
        if (questions.Count == 0)
        {
            // 1.1.0 的空态文案（home_page.dart:1206）。
            App.Note("这次识别没有题目可复制");
            return;
        }

        var text = ComposeSessionText(questions);
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(text);
        Clipboard.SetContent(package);
        App.Note($"已复制 {questions.Count} 道题");
    }

    /// <summary>
    /// 组装「复制全部」的文本。1.1.0 的格式（home_page.dart:1091-1110）：
    /// 每题 <c>【题号】题干 / 选项 / 答案：/ 解析：</c>。
    /// </summary>
    private static string ComposeSessionText(IReadOnlyList<Question> questions)
    {
        var builder = new StringBuilder();
        foreach (var question in questions)
        {
            builder.Append('【').Append(question.Ordinal + 1).Append('】').AppendLine(question.Stem);

            foreach (var option in question.Options)
            {
                builder.Append(option.Label).Append(". ").AppendLine(option.Text);
            }

            var answer = !string.IsNullOrWhiteSpace(question.AnswerText)
                ? question.AnswerText!
                : string.Join(" ", question.Choice);
            builder.Append("答案：").AppendLine(string.IsNullOrWhiteSpace(answer) ? "（AI 未给出）" : answer);

            if (!string.IsNullOrWhiteSpace(question.Analysis))
            {
                builder.Append("解析：").AppendLine(question.Analysis);
            }

            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>顶栏「导出本次」：当前会话 → Markdown（对话选择与组装都在 `ExportSupport`）。</summary>
    private async void OnExportSessionClick(object sender, RoutedEventArgs e)
    {
        if (_database is null || SelectedSession() is not { } session)
        {
            return;
        }

        try
        {
            App.Note(await ExportSupport.ExportSessionAsync(_database, session));
        }
        catch (Exception error)
        {
            App.Note($"导出失败：{error.Message}");
        }
    }

    /// <summary>
    /// 单次识别的 Markdown 组装**已搬到 `ExportSupport.ComposeSessionMarkdown`** ——
    /// 原来顶栏「导出本次/导出合集」与设置页两处各有一份，必然分叉（导出那次就是）。
    /// </summary>

    private async void OnDeleteSessionClick(object sender, RoutedEventArgs e)
    {
        if (_database is null || SelectedSession() is not { } session)
        {
            return;
        }

        // 1.1.0 的二次确认（home_page.dart:1136-1148）。
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "删除这条记录？",
            Content = new TextBlock
            {
                Text = "删除后不会出现在历史列表里（软删除，30 天后彻底清理）。",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            var store = new LocalStore(_database, SyncUploader.DeviceId);
            store.DeleteLocal(SyncEntities.Session, session.SessionId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            _selectedId = null;
            _navIndex = 0;
            Reload();
            App.Note("已删除该条记录");
        }
        catch (Exception error)
        {
            App.Note($"删除失败：{error.Message}");
        }
    }

    /// <summary>当前选中的会话（四个动作都作用在它上面）。</summary>
    private RecordedSession? SelectedSession() =>
        _sessions.FirstOrDefault(s => s.SessionId == _selectedId);

    /// <summary>
    /// 开一个对话框，并**挡住重入**。
    ///
    /// 为什么必须挡：WinUI 同时只允许一个 <c>ContentDialog</c>，第二个
    /// <c>ShowAsync()</c> 会直接抛。而这个调用点全是 <c>async void</c>（事件处理器），
    /// 抛出去没人接 → **整个进程崩**（实测：探针第一次留下未关闭的弹窗，第二次再点，
    /// 应用以 APPCRASH / 故障模块 combase.dll 退出）。用户手快连点两下是同样后果。
    ///
    /// 所以：重入直接忽略，异常就地兜住并写进窗口标题，绝不往上抛。
    /// </summary>
    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        if (_dialogOpen)
        {
            return ContentDialogResult.None;
        }

        _dialogOpen = true;
        try
        {
            return await dialog.ShowAsync();
        }
        catch (Exception error)
        {
            App.Note($"弹窗打不开：{error.Message}");
            return ContentDialogResult.None;
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    /// <summary>
    /// 合集切换弹窗。复刻 home_page.dart 的 <c>showCollectionPicker</c> /
    /// collection_picker_page.dart:265-403：Dialog(maxWidth 680 / maxHeight 620)，
    /// 列表每行 = 选中圆标 + 名字 + 删除按钮，底部一行「新建合集名称 + 新建并进入 + 取消」。
    /// </summary>
    private async void OnCollectionPillClick(object sender, RoutedEventArgs e) =>
        await ShowCollectionPickerAsync();

    /// <summary>
    /// 真正开弹窗的地方。**托盘的「切换任务合集」与顶栏药丸都走这里** ——
    /// 一分叉就必然有一条是错的（这是这个仓库反复踩到的模式）。
    /// </summary>
    internal async Task ShowCollectionPickerAsync()
    {
        if (_database is null)
        {
            // **不静默返回**：数据库还没就绪时也要让用户知道，否则表现就是「点了没反应」。
            App.Note("页面还没准备好，稍后再试一次「切换任务合集」");
            return;
        }

        var dialog = new ContentDialog { XamlRoot = XamlRoot };
        dialog.Content = BuildCollectionPicker(dialog);
        await ShowDialogAsync(dialog);
    }

    private StackPanel BuildCollectionPicker(ContentDialog dialog)
    {
        var root = new StackPanel { Width = 560 };

        root.Children.Add(new TextBlock
        {
            Text = "切换任务合集",
            FontFamily = UiFont.Demibold,
            FontSize = QsFontSizes.Title,
            Foreground = ShellTheme.Brush("QsOnSurfaceBrush", this),
        });
        root.Children.Add(new Border { Height = 8 });
        root.Children.Add(new TextBlock
        {
            Text = "切换后，新的识别会记进所选合集；手机上也会同步看到。",
            FontSize = QsFontSizes.Caption,
            Foreground = ShellTheme.Brush("QsOnSurfaceVariantBrush", this),
            TextWrapping = TextWrapping.Wrap,
        });
        root.Children.Add(new Border { Height = 16 });

        var list = new StackPanel { MaxHeight = 380 };
        var scroller = new ScrollViewer
        {
            MaxHeight = 380,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = list,
        };
        root.Children.Add(scroller);
        root.Children.Add(new Border { Height = 12, BorderBrush = ShellTheme.Brush("QsCardStrokeBrush", this), BorderThickness = new Thickness(0, 0, 0, 1) });
        root.Children.Add(new Border { Height = 12 });

        var newName = new TextBox
        {
            PlaceholderText = "新建合集名称",
            Style = (Style)Application.Current.Resources["QsTextBoxStyle"],
        };

        var create = new Button
        {
            Content = "新建并进入",
            Style = (Style)Application.Current.Resources["QsPrimaryButtonStyle"],
        };

        var cancel = new Button
        {
            Content = "取消",
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Foreground = ShellTheme.Brush("QsOnSurfaceVariantBrush", this),
        };
        cancel.Click += (_, _) => dialog.Hide();

        var buttons = new Grid();
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(newName, 0);
        buttons.Children.Add(newName);
        var gap1 = new Border { Width = 12 };
        Grid.SetColumn(gap1, 1);
        buttons.Children.Add(gap1);
        Grid.SetColumn(create, 2);
        buttons.Children.Add(create);
        var gap2 = new Border { Width = 8 };
        Grid.SetColumn(gap2, 3);
        buttons.Children.Add(gap2);
        root.Children.Add(buttons);

        void Rebuild()
        {
            list.Children.Clear();
            var collections = CollectionStore.List(_database!);

            if (collections.Count == 0)
            {
                list.Children.Add(new TextBlock
                {
                    Text = "还没有合集",
                    FontFamily = UiFont.Demibold,
                    FontSize = QsFontSizes.Body,
                    Margin = new Thickness(4, 8, 4, 4),
                    Foreground = ShellTheme.Brush("QsOnSurfaceBrush", this),
                });
                list.Children.Add(new TextBlock
                {
                    Text = "在下面输入名字新建一个，之后每次打开都可以直接进入。",
                    FontSize = QsFontSizes.Caption,
                    Margin = new Thickness(4, 0, 4, 8),
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = ShellTheme.Brush("QsOnSurfaceVariantBrush", this),
                });
            }

            foreach (var collection in collections)
            {
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var selected = collection.CollectionId == _currentCollectionId;
                var radio = new FontIcon
                {
                    Glyph = selected ? "\uE73D" : "\uECCA", // RadioBtnOn / RadioBtnOff
                    FontSize = QsFontSizes.Title,
                    Margin = new Thickness(4, 0, 10, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = selected
                        ? ShellTheme.Brush("QsBrandPrimaryBrush", this)
                        : ShellTheme.Brush("QsOnSurfaceVariantBrush", this),
                };
                Grid.SetColumn(radio, 0);
                row.Children.Add(radio);

                var name = new TextBlock
                {
                    Text = collection.Name,
                    FontSize = QsFontSizes.Body,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = ShellTheme.Brush("QsOnSurfaceBrush", this),
                };
                Grid.SetColumn(name, 1);
                row.Children.Add(name);

                var remove = new Button
                {
                    Style = (Style)Application.Current.Resources["QsIconButtonStyle"],
                    Content = new FontIcon { Glyph = "\uE74D", FontSize = 18 },
                };
                ToolTipService.SetToolTip(remove, "删除合集");
                var captured = collection;
                remove.Click += async (_, _) => await ConfirmDeleteCollectionAsync(captured, Rebuild);
                Grid.SetColumn(remove, 2);
                row.Children.Add(remove);

                var item = new Button
                {
                    HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch,
                    Background = new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(4, 6, 4, 6),
                    Content = row,
                };
                item.Click += (_, _) =>
                {
                    SetCurrentCollection(collection.CollectionId);
                    dialog.Hide();
                };

                list.Children.Add(item);
            }
        }

        create.Click += (_, _) =>
        {
            var name = newName.Text?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                // 1.1.0 这里是 SnackBar「请先给合集起个名字」；对话框里就地提示更直接。
                newName.PlaceholderText = "请先给合集起个名字";
                return;
            }

            var store = new LocalStore(_database!, SyncUploader.DeviceId);
            var id = CollectionStore.Create(store, name, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            SetCurrentCollection(id);
            dialog.Hide();
        };

        Rebuild();
        return root;
    }

    private void SetCurrentCollection(string? collectionId)
    {
        _currentCollectionId = collectionId;
        var settings = AppSettings.Load();
        settings.CurrentCollectionId = collectionId;
        settings.Save();
        Reload();
        UpdateChrome();
    }

    private async Task ConfirmDeleteCollectionAsync(RecordedCollection collection, Action afterDelete)
    {
        if (_database is null)
        {
            return;
        }

        var count = CollectionStore.SessionsOf(_database, collection.CollectionId, 1000).Count;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "删除合集",
            Content = new TextBlock
            {
                Text = count == 0
                    ? $"确定删除合集「{collection.Name}」？"
                    : $"合集「{collection.Name}」下有 {count} 条识别记录。\n删除合集不会删掉这些记录，它们会回到「全部记录」里。",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        var store = new LocalStore(_database, SyncUploader.DeviceId);
        CollectionStore.Delete(store, collection.CollectionId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (_currentCollectionId == collection.CollectionId)
        {
            _currentCollectionId = null;
            var settings = AppSettings.Load();
            settings.CurrentCollectionId = null;
            settings.Save();
        }

        afterDelete();
        Reload();
        UpdateChrome();
    }

    private void OnScopeChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        _showAll = sender.SelectedItem != sender.Items[0];
        Reload();
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (Frame is { } frame)
        {
            // 压栈打开：走原生 DrillIn 转场（Theme/QsAnimations.cs）。
            QsAnimations.NavigateForward(frame, typeof(SettingsPage));
        }
    }

    /// <summary>
    /// 重新分析：读回本机存的原图，重跑一次 AI。
    ///
    /// 1.1.0 的语义是「重读原图重跑 AI」（home_page.dart 的 <c>onRetry</c>），把这一次的结果换掉。
    /// 2.0 的做法：**先把旧的那条软删除，再记一条新的并选中它** —— 「重新分析」的结果应当替换掉
    /// 那条失败记录，而不是在历史里留两条看起来一样的记录。
    ///
    /// 原图不在了（被缓存上限清掉）时**明确说出来**，不假装重试成功。
    /// </summary>
    private async Task RetrySessionAsync(RecordedSession session)
    {
        if (_database is null || _busy)
        {
            return;
        }

        var config = AiSettings.Load();
        if (!AiSettings.IsUsable(config))
        {
            App.Note("还没有配置 AI，先去「设置 → API 配置」填好 Key");
            return;
        }

        var path = ImagePruner.GetLocalPath(_database, session.ImageHash);
        if (path is null || !File.Exists(path))
        {
            App.Note("原图已不在本机（被图片缓存清理了），无法重新分析");
            return;
        }

        _busy = true;
        CaptureButton.IsEnabled = false;
        ShowAnalyzing();

        try
        {
            var jpeg = await File.ReadAllBytesAsync(path);
            var outcome = await Task.Run(async () =>
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds + 30) };
                var engine = new AnalysisEngine(
                    new HttpAiProvider(config.ProviderId, http),
                    new AnalysisCache(_database),
                    new QuotaGuard(_database),
                    deviceId: SyncUploader.DeviceId);

                var result = await engine.AnalyzeImageAsync(jpeg, session.ImageHash, config).ConfigureAwait(false);
                if (!result.Ok)
                {
                    return new CaptureResult(result, null, null);
                }

                var store = new LocalStore(_database, SyncUploader.DeviceId);
                // 先软删旧的，再记新的：列表里不会出现两条一样的记录。
                store.DeleteLocal(SyncEntities.Session, session.SessionId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                var newSessionId = SessionRecorder.Record(
                    store,
                    session.ImageHash,
                    SyncUploader.DeviceId,
                    [.. result.Questions.Select(Map)],
                    now: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    aiProvider: config.ProviderId,
                    aiModel: config.Model,
                    latencyMs: result.LatencyMs,
                    cached: result.FromCache,
                    // 重新分析**保持它原来的合集**（那条记录当时归在哪儿就还在哪儿）；
                    // 原来那条是「未归类」时，才落到当前合集的兜底口径上。
                    collectionId: session.CollectionId ?? CollectionForNewSession(_database));

                return new CaptureResult(result, newSessionId, await TryPushAsync(_database).ConfigureAwait(false));
            }).ConfigureAwait(true);

            StopAnalyzing();

            if (!outcome.Outcome.Ok)
            {
                ShowEmptyPane("\uE783", "识别失败",
                    UiText.ErrorReason(outcome.Outcome.ErrorCode, outcome.Outcome.ErrorMessage));
                return;
            }

            _selectedId = outcome.SessionId;
            _navIndex = 0;
            Reload();
            BuildSessionList();
            ShowSelectedSession();
            App.Note($"已重新分析：{outcome.Outcome.Questions.Count} 道题");
        }
        catch (Exception error)
        {
            StopAnalyzing();
            App.Note($"重新分析失败：{error.Message}");
        }
        finally
        {
            _busy = false;
            CaptureButton.IsEnabled = true;
        }
    }

    private void NotYet(string what) =>
        ShowEmptyPane("\uE713", $"{what}：还没接上", "这一步的界面位置已经留好，功能会在后续里程碑接上（见 docs/progress.md）。");

    // ===================================================================
    // 小工具
    // ===================================================================
    private static Color ColorFromArgb(uint argb) => Color.FromArgb(
        (byte)((argb >> 24) & 0xFF),
        (byte)((argb >> 16) & 0xFF),
        (byte)((argb >> 8) & 0xFF),
        (byte)(argb & 0xFF));

    private static Color WithAlpha(Color color, byte alpha) =>
        Color.FromArgb(alpha, color.R, color.G, color.B);
}
