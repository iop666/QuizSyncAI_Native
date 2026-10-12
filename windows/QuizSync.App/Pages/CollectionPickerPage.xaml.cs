using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using QuizSync.App.Theme;
using QuizSync.Data;

namespace QuizSync.App.Pages;

/// <summary>
/// 任务合集选择页（1.1.0 的 <c>CollectionPickerPage</c>，ui-spec-desktop §2.3.1）。
///
/// **首次使用门**：一个合集都没有时，应用启动直接进这一页（1.1.0 的 <c>CollectionGate</c>
/// 就是这么做的）—— 「识别结果归属于某个合集」是这个应用的第一个心智模型，
/// 没有合集就没有「当前合集」，顶栏那枚药丸会一直是红的。
///
/// 文案逐字照规格；排序规则也照做：**上次使用的合集排首位**，其余保持原序。
/// </summary>
public sealed partial class CollectionPickerPage : Page
{
    private QuizDatabase? _database;
    private bool _firstRun;

    public CollectionPickerPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += (_, _) =>
        {
            _database?.Dispose();
            _database = null;
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _database = QuizDatabase.OpenOrCreate(AppDataDirectory.DatabasePath());
        }
        catch (Exception error)
        {
            CrashLog.Write("合集页打不开本地库", error);
            PageTitle.Text = "打不开本地数据库";
            ContentHost.Children.Add(SettingsUi.InfoNote(error.Message, this, warn: true));
            return;
        }

        // 判据与 MainWindow.StartPage() **必须同源**（StartupProbe）。
        // 原来这里是 `List().Count == 0`：读失败与确实为空会得到同一个结论，
        // 而两处各写一遍的话，改了启动页忘了这里就又分叉了。
        int alive;
        try
        {
            alive = CollectionStore.List(_database).Count;
        }
        catch (Exception error)
        {
            CrashLog.Write("合集页读合集失败", error);
            PageTitle.Text = "打不开本地数据库";
            ContentHost.Children.Add(SettingsUi.InfoNote(error.Message, this, warn: true));
            return;
        }

        _firstRun = StartupProbe.Decide(readSucceeded: true, alive) == StartupLanding.FirstUse;
        PageTitle.Text = _firstRun ? "新建任务合集" : "选择任务合集";
        Render();
    }

    private void Render()
    {
        if (_database is null)
        {
            return;
        }

        ContentHost.Children.Clear();
        var collections = Sorted(CollectionStore.List(_database));

        if (_firstRun)
        {
            ContentHost.Children.Add(Card("第一次使用",
                "识别结果都归属于某个「任务合集」，方便以后导出与查找。先给这次任务起个名字吧。", host: null));
            ContentHost.Children.Add(SettingsUi.Gap(16));
        }

        ContentHost.Children.Add(CreateCard());
        ContentHost.Children.Add(SettingsUi.Gap(16));
        ContentHost.Children.Add(ListCard(collections));
    }

    /// <summary>上次使用的合集排首位，其余保持原序（collection_picker_page.dart:106-116）。</summary>
    private static IReadOnlyList<RecordedCollection> Sorted(IReadOnlyList<RecordedCollection> collections)
    {
        var lastId = AppSettings.Load().CurrentCollectionId;
        if (lastId is null)
        {
            return collections;
        }

        return [.. collections.OrderByDescending(c => c.CollectionId == lastId)];
    }

    /// <summary>SectionCard：标题 + 说明 + 内容（规格 §2.3.1 的三张卡片都是这个形状）。</summary>
    private Border Card(string title, string? subtitle, FrameworkElement? host, params UIElement[] children)
    {
        var column = new StackPanel { Padding = new Thickness(18, 16, 18, 16) };
        column.Children.Add(new TextBlock
        {
            Text = title,
            FontFamily = UiFont.Demibold,
            FontSize = QsFontSizes.Subtitle,
            Foreground = ShellTheme.Brush("QsOnSurfaceBrush", this),
        });

        if (!string.IsNullOrEmpty(subtitle))
        {
            var note = SettingsUi.RowNote(subtitle!, host ?? this);
            note.Margin = new Thickness(0, 6, 0, 0);
            column.Children.Add(note);
        }

        if (children.Length > 0)
        {
            column.Children.Add(SettingsUi.Gap(16));
            foreach (var child in children)
            {
                column.Children.Add(child);
            }
        }

        return new Border
        {
            CornerRadius = new CornerRadius(16),
            BorderThickness = new Thickness(1),
            BorderBrush = ShellTheme.Brush("QsCardStrokeBrush", this),
            Background = ShellTheme.Brush("QsSurfaceBrush", this),
            Child = column,
        };
    }

    private Border CreateCard()
    {
        var input = new TextBox
        {
            PlaceholderText = "合集名称",
            Style = (Style)Application.Current.Resources["QsTextBoxStyle"],
            MinWidth = 320,
        };

        var create = new Button
        {
            Content = "新建并进入",
            Style = (Style)Application.Current.Resources["QsPrimaryButtonStyle"],
        };

        void Submit()
        {
            var name = input.Text?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                // 1.1.0 是 SnackBar「请先给合集起个名字」；这里就地改占位文字更直接。
                input.PlaceholderText = "请先给合集起个名字";
                return;
            }

            var store = new LocalStore(_database!, SyncUploader.DeviceId);
            var id = CollectionStore.Create(store, name, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            Enter(id, $"已创建并进入合集「{name}」");
        }

        create.Click += (_, _) => Submit();
        input.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                Submit();
            }
        };

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(input, 0);
        row.Children.Add(input);
        create.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(create, 1);
        row.Children.Add(create);

        return Card("新建合集", "例如「期末复习」「第三章作业」", null, row);
    }

    private Border ListCard(IReadOnlyList<RecordedCollection> collections)
    {
        if (collections.Count == 0)
        {
            var empty = new Controls.EmptyStateView
            {
                Glyph = "\uE8B7",
                Title = "还没有合集",
                Message = "在上面输入名字新建一个，之后每次打开都可以直接进入。",
            };
            return Card("已有合集", "点击进入后，本次的所有识别都会记在这个合集里", null, empty);
        }

        var list = new StackPanel();
        foreach (var collection in collections)
        {
            var captured = collection;
            list.Children.Add(SettingsUi.Row(
                collection.Name,
                CountNote(collection),
                this,
                trailing: Row(collection, captured),
                glyph: "\uE8B7"));
        }

        return Card("已有合集", "点击进入后，本次的所有识别都会记在这个合集里", null, list);
    }

    private static string CountNote(RecordedCollection _) =>
        // 1.1.0：加载中显示「…」，出错空串；这一版直接同步查，失败就空串。
        "识别记录";

    private StackPanel Row(RecordedCollection collection, RecordedCollection captured)
    {
        var enter = new Button
        {
            Content = "进入",
            Background = ShellTheme.Alpha("QsBrandPrimaryBrush", 0x1F, this),
            Foreground = ShellTheme.Brush("QsBrandPrimaryBrush", this),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 6, 16, 6),
        };
        enter.Click += (_, _) => Enter(captured.CollectionId, $"已进入合集「{captured.Name}」");

        var delete = new Button
        {
            Style = (Style)Application.Current.Resources["QsIconButtonStyle"],
            Content = new FontIcon { Glyph = "\uE74D", FontSize = 18 },
        };
        ToolTipService.SetToolTip(delete, "删除合集");
        delete.Click += async (_, _) => await DeleteAsync(captured);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(delete);
        row.Children.Add(enter);
        return row;
    }

    private async Task DeleteAsync(RecordedCollection collection)
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

        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            var store = new LocalStore(_database, SyncUploader.DeviceId);
            CollectionStore.Delete(store, collection.CollectionId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var settings = AppSettings.Load();
            if (settings.CurrentCollectionId == collection.CollectionId)
            {
                settings.CurrentCollectionId = null;
                settings.Save();
            }

            _firstRun = CollectionStore.List(_database).Count == 0;
            PageTitle.Text = _firstRun ? "新建任务合集" : "选择任务合集";
            Render();
            App.Note($"已删除合集「{collection.Name}」");
        }
        catch (Exception error)
        {
            App.Note($"删除合集失败：{error.Message}");
        }
    }

    /// <summary>进入某个合集：写进设置 → 进主界面。**这是「当前合集」唯一的落库点。**</summary>
    private void Enter(string collectionId, string note)
    {
        var settings = AppSettings.Load();
        settings.CurrentCollectionId = collectionId;
        settings.Save();
        App.Note(note);
        if (Frame is { } frame)
        {
            // 首次使用门 → 主界面：同样是压栈方向，走原生 DrillIn。
            QsAnimations.NavigateForward(frame, typeof(HomePage));
        }
    }
}
