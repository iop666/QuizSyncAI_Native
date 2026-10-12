using CommunityToolkit.WinUI.Controls;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using QuizSync.App.Theme;
using QuizSync.Core;
using QuizSync.Provider;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

using QuizSync.Data;
namespace QuizSync.App.Pages;

/// <summary>
/// 设置页 10 个分类的内容。**每一条的文案、默认值、范围都照 ui-spec-desktop §3.2–§3.11**
/// （那一节本身逐条标了 1.1.0 的出处行号）。
///
/// 两类设置在这里是分开的，不许混：
/// * **真接上的** —— 接到 <see cref="AppSettings"/> / <see cref="BallSettings"/> /
///   <see cref="AiSettings"/>，改完立刻生效；
/// * **位置留好但还没接的** —— 界面按规格做出来，但点下去必须**明确出声**
///   （走 <see cref="SettingsUi.NotYet"/>），**绝不静默空操作、也不假装已生效**。
///   2.0 属于这一类的：悬浮窗整块（题目浮层还没做）、导出/备份、配对二维码与设备列表。
/// </summary>
internal static class SettingsTabs
{
    private const string Fine = "\uE738";       // 占位：FontIcon 只用于分组标题
    private static SettingsPage Host = null!;
    private static Panel Content = null!;

    public static void Build(SettingsTab tab, Panel content, SettingsPage host)
    {
        Content = content;
        Host = host;

        // **只画选中的那一个分类**，并且各自兜底：一个设置页无论怎样都不该把整个进程带走。
        // 实测过一次 APPCRASH（`Microsoft.UI.Xaml.dll` / `0xc000027b`，STOWED_EXCEPTION）：
        // 点「API 配置」当场闪退，事件日志只给模块名与偏移量，**读不出是哪一行** ——
        // 用户看到的就是「点一下就没」。这里把异常变成一张写清原因的卡片 + 一行 crash.log。
        //
        // ⚠️ 这里必须是**按 tab 分派**：上一版我图省事写成十个 `Safe(...)` 顺序调用，
        // 于是每个分类都被画进同一个面板、用户只看得到第一个「使用说明」——
        // 那是我自己引入的回归（用户当场报「所有页面都打不开」）。分派与兜底两件事都要。
        Action? build = tab switch
        {
            SettingsTab.Guide => Guide,
            SettingsTab.Display => Display,
            SettingsTab.Api => Api,
            SettingsTab.Device => Device,
            SettingsTab.Recognition => Recognition,
            SettingsTab.Ball => Ball,
            SettingsTab.FloatWindow => FloatWindow,
            SettingsTab.Hotkey => Hotkey,
            SettingsTab.Data => Data,
            SettingsTab.About => About,
            _ => null,
        };

        if (build is not null)
        {
            Safe(TitleOf(tab), build);
        }
    }

    /// <summary>出错卡片上显示的分类名（与左栏导航一致）。</summary>
    private static string TitleOf(SettingsTab tab) => tab switch
    {
        SettingsTab.Guide => "使用说明",
        SettingsTab.Display => "显示设置",
        SettingsTab.Api => "API 配置",
        SettingsTab.Device => "连接设备",
        SettingsTab.Recognition => "识别设置",
        SettingsTab.Ball => "悬浮球设置",
        SettingsTab.FloatWindow => "悬浮窗设置",
        SettingsTab.Hotkey => "热键设置",
        SettingsTab.Data => "数据管理",
        SettingsTab.About => "关于",
        _ => "这个分类",
    };

    /// <summary>构建一个分类；出错就画一张卡片说明原因，并写进 crash.log，不让它冒到 XAML 层。</summary>
    private static void Safe(string title, Action build)
    {
        try
        {
            build();
        }
        catch (Exception error)
        {
            CrashLog.Write($"{title} 页面构建失败", error);
            Add(SettingsUi.Section(
                title,
                "这个分类没能画出来，原因写在下面（也记进了程序目录的 logs\\crash.log）。",
                Host,
                SettingsUi.Group("错误", "\uE783", null, Host,
                    SettingsUi.Row(error.GetType().Name, error.Message, Host))));
        }
    }

    private static void Add(UIElement block)
    {
        if (Content.Children.Count > 0)
        {
            // 块间距 10（原来 16）：与段头的 12 上边距合起来约 22 逻辑像素 ——
            // 用户反馈「每个模块距离太远」，16+26=42 太空。
            Content.Children.Add(SettingsUi.Gap(10));
        }

        Content.Children.Add(block);
    }

    // ===================================================================
    // 1 使用说明（§3.2）
    // ===================================================================
    private static void Guide()
    {
        Add(SettingsUi.Section(
            "使用说明",
            "先看「怎么把题目交给它」，再按下面的分类说明逐项设置。第一次使用建议按顺序：API 配置 → 连接设备 → 识别设置。",
            Host,
            SettingsUi.Group("可识别的五种方式", "\uE9D5",
                "识别结果都写进「当前合集」，并同步到已配对的手机。", Host,

                SettingsUi.Row("① 全局热键截屏（最常用）",
                    "默认 F8：截当前屏幕 → 自动识别。键被占用时会自动换到备用键，真实生效的键显示在「热键设置」里。",
                    Host, glyph: "\uE765"),
                SettingsUi.Row("② 桌面悬浮球",
                    "单击 = 截屏识别；左键长按 500ms 或右键 = 加一页（进入多页识别）。球会吸附到屏幕左右边缘，截屏时自动隐藏。",
                    Host, glyph: "\uE91B"),
                SettingsUi.Row("③ 桌面悬浮窗",
                    "贴在桌面上的结果小窗：窗内直接识别、加页、翻上一条/下一条、调字号、重新识别，不用切回主窗口。",
                    Host, glyph: "\uE8A7"),
                SettingsUi.Row("④ 剪贴板 / 拖入 / Ctrl+V",
                    "开启剪贴板监听后，复制一张图就会自动识别；也可以把图片文件拖进主窗口，或在主窗口里按 Ctrl+V 粘贴。",
                    Host, glyph: "\uE77F"),
                SettingsUi.Row("⑤ 手机端上传",
                    "在「连接设备」里打开开关并完成配对后，手机悬浮球截屏或从相册选图，都会送到这台电脑识别，两端同时显示结果。",
                    Host, glyph: "\uE8EA")),

            SettingsUi.Group("多页识别怎么用", "\uE8A9",
                "一套卷子/一本书需要截好几屏时用它：所有页作为一次识别提交，题目跨页也能对齐。", Host,

                SettingsUi.Row("开始与结束",
                    "加页：默认 F9（多页模式）—— 按第一下进入多页并抓第一张，继续按接着加；也可以用悬浮球长按/右键、或悬浮窗的「多页识别」。\n"
                    + "结束并上传：按默认 F8（截屏识别）即结束多页，把已抓的图一次上传识别；悬浮球单击、悬浮窗的「结束多页识别」同样是这个动作。",
                    Host),
                SettingsUi.Row("页数上限",
                    "在「识别设置」里调（默认 6 页，硬上限 6 页）。抓满 6 张会自动上传识别，不用再按键；没满就按 F8 结束并上传。",
                    Host),
                SettingsUi.Row("随时取消",
                    "悬浮窗的「取消多页」会丢掉暂存页、退出多页模式，不会上传；识别进行中还能点最上层的「取消识别」中止这一次。",
                    Host)),

            SettingsUi.Group("各项设置怎么用", "\uE713", null, Host,
                SettingsUi.Row("显示设置", "界面外观、整体缩放，以及题目正文字号与字重。", Host, glyph: "\uE790"),
                SettingsUi.Row("API 配置", "AI 服务、API Key 与调用限制。", Host, glyph: "\uE9D9"),
                SettingsUi.Row("连接设备", "与手机的局域网连接、配对与设备。", Host, glyph: "\uE701"),
                SettingsUi.Row("识别设置", "自动识别、多页识别与本地图片缓存。", Host, glyph: "\uE8B9"),
                SettingsUi.Row("悬浮球设置",
                    "悬浮球的开关、大小、透明度与描边（描边颜色跟随当前状态主色：待识别=蓝 / 识别中=黄 / 多页=绿）。",
                    Host, glyph: "\uE91B"),
                SettingsUi.Row("悬浮窗设置", "悬浮窗的开关、位置、比例与显示模式。", Host, glyph: "\uE8A7"),
                SettingsUi.Row("热键设置", "截屏识别与多页模式两个快捷键。", Host, glyph: "\uE765"),
                SettingsUi.Row("数据管理", "任务合集、导出、备份与日志。", Host, glyph: "\uE8B7"),
                SettingsUi.Row("关于", "版本、技术栈与开源许可。", Host, glyph: "\uE946")),

            SettingsUi.Group("常见问题", "\uE897", null, Host,
                SettingsUi.Row("按了热键没反应？",
                    "热键是全局注册的，被别的程序占用时本程序会自动换到下一个可用候选键；真实生效的键在「热键设置」里。另外停在热键设置页时按键会被刻意吞掉（避免录入时误触发）。", Host),
                SettingsUi.Row("悬浮球 / 悬浮窗不见了？",
                    "到对应分类里确认开关是打开的；悬浮球还可以在托盘右键菜单里一键显示。", Host),
                SettingsUi.Row("手机连不上？",
                    "先在电脑上启动主机服务端（`quizsync-server run`，默认监听 8765；端口被占用会自动退到 8766–8770）。" +
                    "再确认手机与电脑在同一个局域网、Windows 防火墙放行了那个端口。" +
                    "配对码与已配对设备在「连接设备」里。", Host))));
    }

    // ===================================================================
    // 2 显示设置（§3.3）
    // ===================================================================
    private static void Display()
    {
        var settings = AppSettings.Load();

        Add(SettingsUi.Section(
            "显示设置",
            "界面外观，以及题目正文字号与字重。",
            Host,

            SettingsUi.Group("外观", "\uE790", null, Host,
                SettingsUi.Field("主题", "跟随系统时，随 Windows 的浅色 / 深色设置切换", Host,
                    SettingsUi.Segments(
                    [
                        ("跟随系统", settings.Theme == "system", () => SetTheme("system")),
                        ("浅色", settings.Theme == "light", () => SetTheme("light")),
                        ("深色", settings.Theme == "dark", () => SetTheme("dark")),
                    ], Host), 420),
                SettingsUi.Field("界面缩放", $"当前 {(int)Math.Round(settings.UiScale * 100)}%", Host,
                    ScaleDropdown(settings.UiScale), 320)),

            SettingsUi.Group("题目显示", "\uE8D2",
                "题干、选项、答案与解析的正文字号与字重。", Host,
                FontSizeField(settings),
                FontWeightField(settings)),

            // 这一项是 1.1.0 有、2.0 还没做到的：**如实写在界面上**，不让它当哑开关。
            // （WinUI 没有 Flutter 的虚拟画布：那一套是「按更小的逻辑尺寸排版再整体放大」，
            //   而 WinUI 的 RenderTransform 不改排版，硬做会得到「内容被裁掉」而不是缩放。）
            SettingsUi.InfoNote(
                "「界面缩放」这一项还没接上：值会存下来，但当前版本不改界面大小。"
                + "题目的字号请用下面的「题目显示」调整；整窗大小直接拖窗口边缘即可。", Host)));
    }

    private static void SetTheme(string theme)
    {
        var settings = AppSettings.Load();
        settings.Theme = theme;
        settings.Save();
        App.ApplyTheme(theme);
        Host.Refresh();
    }

    private static ComboBox ScaleDropdown(double current)
    {
        var combo = new ComboBox { Style = (Style)Application.Current.Resources["QsComboBoxStyle"], MinWidth = 200 };
        double[] presets = [0.5, 0.75, 1.0, 1.25, 1.5, 1.75, 2.0, 2.25, 2.5, 2.75, 3.0];
        foreach (var preset in presets)
        {
            combo.Items.Add(new ComboBoxItem { Content = $"{(int)Math.Round(preset * 100)}%", Tag = preset });
            if (Math.Abs(preset - current) < 0.001)
            {
                combo.SelectedIndex = combo.Items.Count - 1;
            }
        }

        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: double value })
            {
                var settings = AppSettings.Load();
                settings.UiScale = value;
                settings.Save();
                Host.Refresh();
            }
        };

        return combo;
    }

    private static SettingsCard FontSizeField(AppSettings settings)
    {
        var number = new TextBlock
        {
            Text = $"{(int)Math.Round(settings.FontSize)}",
            Width = 36,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = UiFont.Demibold,
            Foreground = ShellTheme.Brush("QsOnSurfaceBrush", Host),
        };

        var slider = new Slider
        {
            Minimum = 12,
            Maximum = 32,
            StepFrequency = 1,
            Value = settings.FontSize,
            Width = 320,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // 拖动中只改草稿，松手才落库（1.1.0 的 onChangeEnd 语义）。
        slider.ValueChanged += (_, e) => number.Text = $"{(int)Math.Round(e.NewValue)}";
        slider.AddHandler(UIElement.PointerReleasedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) =>
            {
                var current = AppSettings.Load();
                current.FontSize = slider.Value;
                current.Save();
                App.ApplyQuestionStyle(slider.Value, current.FontWeight);
            }), handledEventsToo: true);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        row.Children.Add(new TextBlock { Text = "小", FontSize = QsFontSizes.Caption, VerticalAlignment = VerticalAlignment.Center, Foreground = ShellTheme.Brush("QsOnSurfaceVariantBrush", Host) });
        row.Children.Add(slider);
        row.Children.Add(new TextBlock { Text = "大", FontSize = QsFontSizes.Caption, VerticalAlignment = VerticalAlignment.Center, Foreground = ShellTheme.Brush("QsOnSurfaceVariantBrush", Host) });
        row.Children.Add(number);

        return SettingsUi.Field("题目字号", $"当前 {(int)Math.Round(settings.FontSize)}", Host, row, 520);
    }

    private static SettingsCard FontWeightField(AppSettings settings)
    {
        var number = new TextBlock
        {
            Text = WeightName(settings.FontWeight),
            Width = 88,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = UiFont.Demibold,
            Foreground = ShellTheme.Brush("QsOnSurfaceBrush", Host),
        };

        var slider = new Slider
        {
            Minimum = 300,
            Maximum = 700,
            StepFrequency = 100,
            Value = Math.Clamp(settings.FontWeight, 300, 700),
            Width = 320,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var preview = new Border
        {
            Padding = new Thickness(16),
            CornerRadius = new CornerRadius(10),
            Background = ShellTheme.Brush("QsSurfaceBrush", Host),
            BorderThickness = new Thickness(1),
            BorderBrush = ShellTheme.Brush("QsOutlineBrush", Host),
        };
        var previewText = new TextBlock
        {
            Text = "3. 第 12 题  下列关于函数的说法正确的是（　）",
            FontSize = settings.FontSize,
            LineHeight = settings.FontSize * 1.6,
            Foreground = ShellTheme.Brush("QsCardFgBrush", Host),
        };
        preview.Child = previewText;

        void Sync(double weight)
        {
            number.Text = WeightName((int)weight);
            previewText.FontFamily = UiFont.For((int)weight);
        }

        slider.ValueChanged += (_, e) => Sync(e.NewValue);
        slider.AddHandler(UIElement.PointerReleasedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) =>
            {
                var current = AppSettings.Load();
                current.FontWeight = (int)Math.Round(slider.Value);
                current.Save();
                App.ApplyQuestionStyle(current.FontSize, slider.Value);
            }), handledEventsToo: true);
        Sync(slider.Value);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        row.Children.Add(new TextBlock { Text = "细", FontSize = QsFontSizes.Caption, VerticalAlignment = VerticalAlignment.Center, Foreground = ShellTheme.Brush("QsOnSurfaceVariantBrush", Host) });
        row.Children.Add(slider);
        row.Children.Add(new TextBlock { Text = "粗", FontSize = QsFontSizes.Caption, VerticalAlignment = VerticalAlignment.Center, Foreground = ShellTheme.Brush("QsOnSurfaceVariantBrush", Host) });
        row.Children.Add(number);

        var column = new StackPanel();
        column.Children.Add(row);
        column.Children.Add(SettingsUi.Gap(16));
        column.Children.Add(preview);
        column.Children.Add(SettingsUi.Gap(8));

        var field = SettingsUi.Field("题目字重", $"当前 {WeightName(settings.FontWeight)}（{settings.FontWeight}）", Host,
            new Border { Child = column }, 520);
        return field;
    }

    /// <summary>档位中文（1.1.0 display_settings_page.dart:206-212 逐字）。</summary>
    private static string WeightName(int weight) => weight switch
    {
        <= 300 => "细",
        <= 400 => "常规",
        <= 500 => "中等",
        <= 600 => "半粗",
        _ => "粗",
    };

    // ===================================================================
    // 3 API 配置（§3.4）
    // ===================================================================
    private static void Api()
    {
        var config = AiSettings.LoadSaved() ?? AiSettings.Load();
        var dailyLimit = AiSettings.LoadDailyLimit();
        var usage = new QuizSync.Data.QuotaGuard(
            QuizSync.Data.QuizDatabase.OpenOrCreate(AppDataDirectory.DatabasePath()), dailyLimit).UsedToday();

        Add(SettingsUi.Section(
            "API 配置",
            "默认已按 DeepSeek 填好模型名与地址（deepseek-flash），通常只要粘一个 API Key 就能用。"
            + "API Key 用 Windows DPAPI 加密保存在本机，界面上只回显尾 4 位。",
            Host,

            SettingsUi.Group("AI 服务", "\uE943", null, Host,
                SettingsUi.Field("Provider",
                    "OpenAI 兼容接口同时覆盖 DashScope / GLM-4V / DeepSeek 等", Host,
                    ProviderDropdown(config.ProviderId), 420),
                SettingsUi.Field("API Key", TailNote(config.ApiKey), Host, ApiKeyField(config), 480),
                SettingsUi.Field("模型名称",
                    "必须是能看图的多模态模型；默认已填 DeepSeek 的 deepseek-flash", Host,
                    TextInput(config.Model, "deepseek-flash", value =>
                    {
                        var current = AiSettings.LoadSaved() ?? AiSettings.Load();
                        AiSettings.Save(current with { Model = value });
                    }), 420),
                SettingsUi.Field("Base URL",
                    "默认已填 DeepSeek 官方地址；留空使用该服务的默认地址", Host,
                    TextInput(config.BaseUrl, "https://api.deepseek.com", value =>
                    {
                        var current = AiSettings.LoadSaved() ?? AiSettings.Load();
                        AiSettings.Save(current with { BaseUrl = value });
                    }), 480)),

            SettingsUi.Group("高级设置", "\uE713", null, Host,
                SettingsUi.Field("请求超时", null, Host,
                    IntDropdown([30, 60, 90, 120, 180], config.TimeoutSeconds, "秒", value =>
                    {
                        var current = AiSettings.LoadSaved() ?? AiSettings.Load();
                        AiSettings.Save(current with { TimeoutSeconds = value });
                    }), 220),
                SettingsUi.Field("每日调用上限", null, Host,
                    IntDropdown([0, 50, 100, 200, 500, 1000], dailyLimit, "次", AiSettings.SaveDailyLimit, zeroLabel: "不设上限"), 220),
                SettingsUi.Field("今日用量",
                    dailyLimit <= 0 ? "不含缓存命中；当前不设上限，用量只做统计" : "不含缓存命中", Host,
                    UsageBlock(usage, dailyLimit), 480))));
    }

    private static string TailNote(string apiKey) => string.IsNullOrEmpty(apiKey)
        ? "未设置"
        : $"已设置（尾 4 位 ****{apiKey[^Math.Min(4, apiKey.Length)..]}）";

    private static ComboBox ProviderDropdown(string current)
    {
        var combo = new ComboBox { Style = (Style)Application.Current.Resources["QsComboBoxStyle"], MinWidth = 260 };
        (string Id, string Label)[] providers =
        [
            (AiProviders.OpenAiCompatible, "OpenAI 兼容（默认）"),
            (AiProviders.Anthropic, "Anthropic Claude"),
            (AiProviders.Gemini, "Google Gemini"),
        ];

        foreach (var (id, label) in providers)
        {
            combo.Items.Add(new ComboBoxItem { Content = label, Tag = id });
            if (id == current)
            {
                combo.SelectedIndex = combo.Items.Count - 1;
            }
        }

        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: string id })
            {
                var current2 = AiSettings.LoadSaved() ?? AiSettings.Load();
                AiSettings.Save(current2 with { ProviderId = id });
            }
        };

        return combo;
    }

    private static Grid ApiKeyField(AiConfig config)
    {
        var box = new PasswordBox
        {
            PlaceholderText = "sk-…",
            Password = config.ApiKey,
            // ⚠️ **PasswordBox 不能套 TargetType=TextBox 的样式**：WinUI 会抛
            // `COMException (0x800F1000): 没有检测到已安装的组件。Cannot apply a Style with
            // TargetType 'TextBox' to an object of type 'PasswordBox'` —— 这正是「点一下
            // API 配置就崩」的根因（真机 crash.log 里的原始栈）。
            // 这里先不套样式（用控件默认外观）；要和旁边两个输入框完全一致，
            // 需要补一个 `QsPasswordBoxStyle`（TargetType=PasswordBox）—— 列在下一轮。
            MinWidth = 360,
        };

        var reveal = new Button
        {
            Style = (Style)Application.Current.Resources["QsIconButtonStyle"],
            Content = new FontIcon { Glyph = "\uE7B3", FontSize = 18 },
        };
        ToolTipService.SetToolTip(reveal, "显示输入内容");

        var save = new Button
        {
            Style = (Style)Application.Current.Resources["QsIconButtonStyle"],
            Content = new FontIcon { Glyph = "\uE74E", FontSize = 18 },
        };
        ToolTipService.SetToolTip(save, "保存 API Key（点击或在该输入框内按回车）");

        void Commit()
        {
            var current = AiSettings.LoadSaved() ?? AiSettings.Load();
            AiSettings.Save(current with { ApiKey = box.Password ?? string.Empty });
            App.Note("API Key 已保存");
            Host.Refresh();
        }

        save.Click += (_, _) => Commit();
        reveal.Click += (_, _) =>
        {
            // WinUI 的 PasswordBox 没有 inner 切换；就地换成明文查看（不改数据）。
            App.Note(box.Password is { Length: > 0 } key ? $"当前 Key 尾 4 位：{key[^Math.Min(4, key.Length)..]}" : "当前没有设置 API Key");
        };

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(box, 0);
        row.Children.Add(box);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(reveal);
        buttons.Children.Add(save);
        Grid.SetColumn(buttons, 1);
        row.Children.Add(buttons);
        return row;
    }

    private static TextBox TextInput(string value, string hint, Action<string> onCommit)
    {
        var box = new TextBox
        {
            Text = value,
            PlaceholderText = hint,
            Style = (Style)Application.Current.Resources["QsTextBoxStyle"],
            MinWidth = 360,
        };
        box.LostFocus += (_, _) => onCommit(box.Text ?? string.Empty);
        return box;
    }

    private static ComboBox IntDropdown(int[] options, int current, string suffix, Action<int> onPick, string? zeroLabel = null)
    {
        var combo = new ComboBox { Style = (Style)Application.Current.Resources["QsComboBoxStyle"], MinWidth = 140 };
        foreach (var option in options)
        {
            var label = option == 0 && zeroLabel is not null
                ? zeroLabel
                : option <= 0 ? option.ToString() : $"{option} {suffix}";
            combo.Items.Add(new ComboBoxItem { Content = label, Tag = option });
            if (option == current)
            {
                combo.SelectedIndex = combo.Items.Count - 1;
            }
        }

        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: int value })
            {
                onPick(value);
            }
        };

        return combo;
    }

    private static StackPanel UsageBlock(int usage, int limit)
    {
        var column = new StackPanel();
        var ratio = limit <= 0 ? 0 : (double)usage / limit;
        column.Children.Add(SettingsUi.ProgressBar(ratio, Host));
        column.Children.Add(SettingsUi.Gap(16));
        column.Children.Add(new TextBlock
        {
            Text = limit <= 0 ? $"{usage} 次" : $"{usage} / {limit} 次",
            FontFamily = UiFont.Demibold,
            FontSize = QsFontSizes.Body,
            Foreground = ShellTheme.Brush("QsOnSurfaceBrush", Host),
        });
        return column;
    }

    // ===================================================================
    // 4 连接设备（§3.5）
    // ===================================================================
    private static void Device()
    {
        var settings = AppSettings.Load();
        var host = HostDirectory();

        // SPEC §2.2 的口径：「开启连接设备」是**这一整块的总开关**。
        // 关闭时（默认）只留这个开关与一句「当前不可用」，连接状态与设备管理整块不渲染；
        // 打开时才真的把主机拉起来。
        //
        // 第 169 轮（用户要求）：「开启连接设备」就是**启动整个连接服务**的那一个按钮 ——
        // 点一下主机与识别服务一起起来，不再让用户自己去找「检测并启动主机」「启动识别服务」
        // 那两个手动按钮（原来那一整块「主机服务端」已删除），另给它一个「重启连接服务」。
        var blocks = new List<UIElement>
        {
            SettingsUi.Group("局域网连接", "\uE701", null, Host,
                SettingsUi.Switch("开启连接设备",
                    settings.ConnectEnabled
                        ? "已开启：点这个开关就同时启动主机与识别服务；局域网可连，退出应用即停止"
                        : "已关闭（默认）：不启动主机、不监听任何端口。打开即自动启动主机与识别服务",
                    Host, settings.ConnectEnabled, SetConnectEnabled)),
        };

        if (settings.ConnectEnabled)
        {
            // 「当前连接状态」原来是**写死**的「服务未启动」一行 —— 主机明明在跑（甚至就在本应用里跑）
            // 它也这么写。第 167 轮把主机内嵌进本应用之后，这句假话更容易被撞见，
            // 所以改成**真的探活一次**（回环 + `HostDiscovery`，与启动路径同一个判据）。
            var hostStatus = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = QsFontSizes.Caption,
                Foreground = ShellTheme.Brush("QsOnSurfaceVariantBrush", Host),
                Text = "正在检测…",
            };

            // 配对块（二维码 + 6 位配对码 + 倒计时 + 复制链接）**排在状态之前**：
            // 用户进这个分类十有八九就是为了拿配对码给手机扫（SPEC §2.3「一进这个分类就自动刷新」）。
            blocks.Add(PairingBlock(host));

            blocks.Add(SettingsUi.Group("当前连接状态", "\uE703", null, Host,
                SettingsUi.Row("主机状态", null, Host, hostStatus),
                // 重启：主机与识别服务一起重来。主机卡住 / 换了网络 / 手机连不上时，
                // 用户不必退出应用再打开。
                SettingsUi.Row("重启连接服务",
                    "主机与识别服务一起重启（只重启本应用启动的那些；独立主机进程与别人起的识别服务不动）。",
                    Host,
                    TextButton("重启连接服务", "\uE777", () => _ = RestartConnectionAsync()), glyph: "\uE777")));
            _ = RefreshHostStatusAsync(hostStatus, host);

            blocks.Add(SettingsUi.Group("已配对设备", "\uE8EA", null, Host,
                DeviceListBlock()));
        }
        else
        {
            blocks.Add(SettingsUi.InfoNote(
                "当前不可用：打开上面的「开启连接设备」后，主机就会在本应用里跑起来，"
                + "下面才会出现连接状态、配对码与已配对设备。", Host));
        }

        // SPEC：三个分支（未开启 / 已开启但服务未启动 / 服务已启动）都要有这条说明。
        blocks.Add(SettingsUi.InfoNote(
            "同一时间只支持连接一台安卓设备。换手机时请先在下方「已配对设备」里吊销旧设备，再用新手机扫码配对；"
            + "直接配对新手机，旧手机会立刻掉线并需要重新扫码。", Host));

        Add(SettingsUi.Section(
            "连接设备",
            "手机与电脑需在同一局域网；扫码或手动输入地址 + 配对码即可配对。",
            Host,
            blocks.ToArray()));
    }

    /// <summary>
    /// 总开关真的做事：**打开就把整套连接服务拉起来**（主机 + 识别服务，第 169 轮起一步到位），
    /// 不是只存一个值 —— 也不要求用户再去下面点「启动主机」「启动识别服务」。
    ///
    /// 关掉时**不去杀已经在跑的主机**：那可能是 `setup --apply --autostart` 拉起来的，
    /// 也可能是用户自己开的另一个实例 —— 在别人的进程上动手比「关掉之后仍连得上」更坏，
    /// 所以如实说清楚，让用户自己决定要不要停。
    /// </summary>
    private static void SetConnectEnabled(bool value)
    {
        var settings = AppSettings.Load();
        settings.ConnectEnabled = value;
        settings.Save();

        if (value)
        {
            _ = StartConnectionAsync(restart: false);
            return;
        }

        App.Note("已关闭「开启连接设备」：本应用不再拉起主机与识别服务；已经在运行的（独立主机进程 / 别人起的识别服务）不受影响");
        Host.Refresh();
    }

    /// <summary>
    /// 「开启连接设备」与「重启连接服务」共用的一条路径（实现在 `App.StartConnectionAsync`）：
    /// **主机 + 识别服务一起管**，结果逐种情况报给用户，然后重画这一页让配对块与状态跟着更新。
    /// </summary>
    private static async Task StartConnectionAsync(bool restart)
    {
        App.Note(restart ? "正在重启连接服务…" : "正在启动连接服务…");

        try
        {
            App.Note(await App.StartConnectionAsync(restart));
        }
        catch (Exception error)
        {
            App.Note($"连接服务启动失败：{error.Message}");
        }

        App.Instance?.ReloadSettingsPage();
    }

    private static Task RestartConnectionAsync() => StartConnectionAsync(restart: true);

    /// <summary>服务端数据目录（有 control.token 才算找到）。</summary>
    private static string? HostDirectory() => HostDataDirectory.Find();

    // ===================================================================
    // 连接设备 · 配对块（二维码 + 配对码 + 倒计时 + 复制链接）
    // ===================================================================

    /// <summary>当前这一块正在显示的东西（刷新共用一份，避免界面与二维码各算各的）。</summary>
    private sealed record PairingView(string Payload, string Code, long ExpiresAtMs, string Address, int Port);

    /// <summary>倒计时用的秒针。**同一时间只有一个** —— 每次重建这一页先停掉上一支（别留悬挂计时器）。</summary>
    private static DispatcherTimer? _pairTicker;
    private static PairingView? _pairing;

    /// <summary>
    /// 配对块（`SPEC.md` §2.3 / `docs/protocol.md` §2.1）。
    ///
    /// 用户 2026-09-29 反馈：「windows的连接界面没有二维码等显示，我说的做一块要嵌入进来」。
    /// 规格里这一块一直存在（连接状态 / 监听端口与局域网地址 / 配对二维码与 6 位配对码
    /// 含有效期倒计时、刷新、复制配对链接 **一进这个分类就自动刷新**），
    /// 原生 2.0 这一版只做了「总开关 + 主机状态 + 设备列表」，把配对这一半漏了。
    ///
    /// 二维码内容 = `PairQr.BuildPayload(...)`：`quizsync://pair?host=<局域网 IP>&port=…&code=…&sid=…&v=1`。
    /// **host 必须是局域网 IP**（不是服务端控制面那句 `127.0.0.1`），否则手机扫到只会连自己。
    /// </summary>
    private static StackPanel PairingBlock(string? hostDirectory)
    {
        var qrImage = new Image
        {
            // **不缩放**：二维码被非整数倍拉伸会糊掉模块边缘，手机就扫不出来（尺寸在加载后按矩阵设）。
            Stretch = Stretch.None,
            Source = null,
        };

        var qrFrame = new Border
        {
            Padding = new Thickness(6),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Colors.White),
            BorderThickness = new Thickness(1),
            BorderBrush = ShellTheme.Brush("QsCardStrokeBrush", Host),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = qrImage,
        };

        var codeText = new TextBlock
        {
            FontSize = QsFontSizes.TitleLarge,
            FontFamily = UiFont.Demibold,
            IsTextSelectionEnabled = true,
            Foreground = ShellTheme.Brush("QsOnSurfaceBrush", Host),
        };

        var countdown = new TextBlock
        {
            FontSize = QsFontSizes.Caption,
            Foreground = ShellTheme.Brush("QsOnSurfaceVariantBrush", Host),
            TextWrapping = TextWrapping.Wrap,
        };

        var address = new TextBlock
        {
            FontSize = QsFontSizes.Caption,
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ShellTheme.Brush("QsOnSurfaceVariantBrush", Host),
        };

        var status = new TextBlock
        {
            FontSize = QsFontSizes.CaptionSmall,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ShellTheme.Brush("QsOnSurfaceVariantBrush", Host),
        };

        var right = new StackPanel { Spacing = 6 };
        right.Children.Add(codeText);
        right.Children.Add(countdown);
        right.Children.Add(address);

        var body = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        body.Children.Add(qrFrame);
        body.Children.Add(right);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(TextButton("刷新配对码", "\uE72C", () => _ = LoadPairingAsync(
            qrImage, codeText, countdown, address, status, refresh: true)));
        buttons.Children.Add(TextButton("复制配对链接", "\uE8C8", () => CopyPairLink(status)));

        var block = new StackPanel { Spacing = 10 };
        block.Children.Add(body);
        block.Children.Add(buttons);
        block.Children.Add(status);

        // **一进这个分类就自动刷新**（SPEC §2.3 的明文要求）——用户不必先点一下才知道配对码。
        _ = LoadPairingAsync(qrImage, codeText, countdown, address, status, refresh: false);
        return block;
    }

    private static async Task LoadPairingAsync(
        Image qrImage, TextBlock codeText, TextBlock countdown, TextBlock address, TextBlock status, bool refresh)
    {
        status.Text = refresh ? "正在刷新配对码…" : "正在准备配对码…";

        var host = await QuizSync.ServerBridge.HostLauncher.EnsureRunningAsync(
            serverDataDirectory: HostDataDirectory.ForStarting());
        if (host.BaseUrl is null)
        {
            status.Text = $"拿不到配对码：主机没起来（{host.Detail}）";
            return;
        }

        var hostDataDirectory = HostDirectory() ?? HostDataDirectory.ForStarting();
        var controlToken = QuizSync.ServerBridge.HostControl.FindControlToken(hostDataDirectory);
        if (string.IsNullOrWhiteSpace(controlToken))
        {
            status.Text = $"拿不到配对码：找不到控制令牌（{Path.Combine(hostDataDirectory, "control.token")}）";
            return;
        }

        try
        {
            var control = new QuizSync.ServerBridge.HostControl(host.BaseUrl, controlToken!);
            if (refresh)
            {
                // `POST /pair/code/refresh` 只回 `code` + `expires_at`（端口与设备 id 都缺），
                // 所以刷新之后**再读一次完整记录** —— 否则二维码里的端口会退回默认 8765，
                // 主机实际在 8766–8770 时手机就扫到一个错地址。
                await control.RefreshPairCodeAsync().ConfigureAwait(true);
            }

            var payload = await control.PairCodeAsync().ConfigureAwait(true);

            var code = payload?["code"]?.ToString();
            if (string.IsNullOrWhiteSpace(code))
            {
                status.Text = "拿不到配对码：主机没有返回 code（它可能是旧版本主机）";
                return;
            }

            var port = payload?["port"]?.GetValue<int>() ?? QuizSync.ServerBridge.HostDiscovery.BasePort;
            var deviceId = payload?["device_id"]?.ToString();
            var expiresAt = payload?["expires_at"]?.GetValue<long>() ?? 0L;

            // **局域网地址优先**：服务端给的 pair_uri 里 host 是 127.0.0.1（那是给本机客户端的），
            // 画成二维码手机扫不到。拿不到局域网地址时才退到主机回环地址并说清楚。
            var lan = QuizSync.ServerBridge.LanAddress.Find();
            var hostName = lan ?? new Uri(host.BaseUrl).Host;
            var pairPayload = PairQr.BuildPayload(hostName, port, code!, deviceId);

            var matrix = PairQr.Encode(pairPayload);
            const int moduleSize = 6;
            const int quietZone = 4;
            var side = (matrix.Size + (quietZone * 2)) * moduleSize;
            qrImage.Source = await RenderQrAsync(matrix, moduleSize, quietZone).ConfigureAwait(true);
            qrImage.Width = side;
            qrImage.Height = side;

            codeText.Text = code!;
            address.Text = lan is null
                ? $"地址：{hostName}:{port}（找不到局域网地址，手机可能连不上这台电脑）"
                : $"地址：{lan}:{port}";
            status.Text = lan is null
                ? "二维码已生成，但本机没有可用的局域网地址：请确认已连上 Wi-Fi/网线。"
                : "手机扫上面的二维码即可配对（也可以在手机上手动输入地址与配对码）。";

            _pairing = new PairingView(pairPayload, code!, expiresAt, lan ?? hostName, port);
            StartPairCountdown(countdown);
        }
        catch (Exception error)
        {
            status.Text = $"拿不到配对码：{error.Message}";
        }
    }

    /// <summary>
    /// 有效期倒计时（SEC §2.3 的「含有效期倒计时」）。计时器**全局只留一支**：
    /// 每次重建这一页先停掉上一支 —— 留悬挂计时器是这个仓库踩过的坑（M25 的 disposable 审计）。
    /// </summary>
    private static void StartPairCountdown(TextBlock countdown)
    {
        _pairTicker?.Stop();
        _pairTicker = null;

        void Tick()
        {
            if (_pairing is not { } current)
            {
                return;
            }

            if (current.ExpiresAtMs <= 0)
            {
                countdown.Text = "有效期：以主机为准（5 分钟内有效）";
                return;
            }

            var left = current.ExpiresAtMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            countdown.Text = left <= 0
                ? "已过期：点上面的「刷新配对码」换一个"
                : $"还剩 {(int)(left / 1000)} 秒过期（过期后点「刷新配对码」）";
        }

        Tick();
        _pairTicker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _pairTicker.Tick += (_, _) => Tick();
        _pairTicker.Start();
    }

    /// <summary>复制配对链接：手机上「方式二：粘贴配对链接」用的就是这段文本（比扫码更方便）。</summary>
    private static void CopyPairLink(TextBlock status)
    {
        if (_pairing is not { } current)
        {
            status.Text = "还没有可复制的配对链接：先等二维码出来。";
            return;
        }

        try
        {
            var package = new DataPackage();
            package.SetText(current.Payload);
            Clipboard.SetContent(package);
            status.Text = $"已复制配对链接（{current.Payload}），粘到手机的「粘贴配对链接」里即可。";
        }
        catch (Exception error)
        {
            status.Text = $"复制失败：{error.Message}";
        }
    }

    /// <summary>
    /// 二维码矩阵 → `SoftwareBitmapSource`。**纯手写像素**（不引 GDI+ / 图片编解码）：
    /// 每格 [moduleSize] 像素、四周留 [quietZone] 格静区。
    ///
    /// 三个刻意为之的点：
    /// * 静区按规范给足 4 格（`protocol.md` 没写，但 QR 规范要求 ≥4，少了相机对不上焦）；
    /// * 图片**按整数像素画**、界面里 `Stretch = None`：二维码被非整数倍缩放会糊掉模块边缘，
    ///   手机扫不出来的第一嫌疑就是它；
    /// * 用 `SoftwareBitmap` + `CryptographicBuffer` 这条 WinRT 正路填像素 ——
    ///   `WriteableBitmap.PixelBuffer.AsStream()` 在 WinUI 3 的投影里**没有**这个扩展方法
    ///   （实测编译不过：`IBuffer` 上没有 `AsStream`）。
    /// </summary>
    private static async Task<SoftwareBitmapSource> RenderQrAsync(PairQr.Matrix matrix, int moduleSize, int quietZone)
    {
        var side = (matrix.Size + (quietZone * 2)) * moduleSize;
        var pixels = new byte[side * side * 4];

        for (var y = 0; y < side; y++)
        {
            for (var x = 0; x < side; x++)
            {
                var moduleX = (x / moduleSize) - quietZone;
                var moduleY = (y / moduleSize) - quietZone;
                var black = moduleX >= 0 && moduleY >= 0
                    && moduleX < matrix.Size && moduleY < matrix.Size
                    && matrix[moduleX, moduleY];

                var offset = ((y * side) + x) * 4;
                var value = black ? (byte)0x00 : (byte)0xFF;
                pixels[offset] = value;       // B
                pixels[offset + 1] = value;   // G
                pixels[offset + 2] = value;   // R
                pixels[offset + 3] = 0xFF;    // A
            }
        }

        var buffer = Windows.Security.Cryptography.CryptographicBuffer.CreateFromByteArray(pixels);
        using var software = Windows.Graphics.Imaging.SoftwareBitmap.CreateCopyFromBuffer(
            buffer,
            Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
            side,
            side,
            Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);

        var source = new SoftwareBitmapSource();
        await source.SetBitmapAsync(software).AsTask().ConfigureAwait(true);
        return source;
    }


    /// <summary>
    /// 「已配对设备」：真的列出主机认得的设备，并且能**吊销**（服务端 `GET` / `DELETE /api/v1/devices`）。
    ///
    /// 原来这里是 `NotYet("需要先接上服务端")`，而那句理由早就过期：主机会一键拉起，
    /// 桌面端自己也有设备令牌（`SyncUploader.ConnectAsync` 复用它）。更糟的是同一页的说明文字
    /// 还叫用户「在下方『已配对设备』里吊销旧设备」—— 指着一个不存在的东西。
    ///
    /// 走的是**控制面拿令牌 → 桌面端自己的设备身份**这条路，不新增第二条鉴权路径。
    /// </summary>
    private static StackPanel DeviceListBlock()
    {
        var status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = QsFontSizes.Caption,
            Foreground = ShellTheme.Brush("QsOnSurfaceVariantBrush", Host),
        };

        var list = new StackPanel { Spacing = 8 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(TextButton("刷新设备列表", "\uE72C", () => _ = LoadDevicesAsync(status, list)));

        var block = new StackPanel { Spacing = 10 };
        block.Children.Add(buttons);
        block.Children.Add(status);
        block.Children.Add(list);

        // 进这一页就自动读一次，用户不必先点「刷新」才知道有没有设备。
        _ = LoadDevicesAsync(status, list);
        return block;
    }

    private static async Task LoadDevicesAsync(TextBlock status, StackPanel list)
    {
        status.Text = "正在读取主机上的设备…";
        list.Children.Clear();

        // 先确保主机在跑：不在跑就没得读（与总开关「开启连接设备」走同一条路径）。
        var host = await QuizSync.ServerBridge.HostLauncher.EnsureRunningAsync(
            serverDataDirectory: HostDataDirectory.ForStarting());
        if (host.BaseUrl is null)
        {
            status.Text = $"读不到设备：主机没起来（{host.Detail}）";
            return;
        }

        // 令牌从**已经有一份的那个目录**读（`Find`）；一份都没有时按「起主机时会写哪里」读（`ForStarting`）。
        // 两者不一致的话，会出现「主机在跑但永远读不到设备」这种最费解的状态。
        var hostDataDirectory = HostDirectory() ?? HostDataDirectory.ForStarting();
        var controlToken = QuizSync.ServerBridge.HostControl.FindControlToken(hostDataDirectory);
        if (string.IsNullOrWhiteSpace(controlToken))
        {
            status.Text = $"读不到设备：找不到控制令牌（{Path.Combine(hostDataDirectory, "control.token")}）";
            return;
        }

        try
        {
            var devices = await SyncUploader.ListDevicesAsync(host.BaseUrl, controlToken!).ConfigureAwait(true);
            if (devices.Count == 0)
            {
                status.Text = "主机上还没有配对的设备。";
                return;
            }

            status.Text = $"共 {devices.Count} 台（来自 {host.BaseUrl}）";
            foreach (var device in devices)
            {
                list.Children.Add(DeviceRow(device, host.BaseUrl!, controlToken!, status, list));
            }
        }
        catch (Exception error)
        {
            status.Text = $"读设备列表失败：{error.Message}";
        }
    }

    private static SettingsCard DeviceRow(
        PairedDevice device, string baseUrl, string controlToken, TextBlock status, StackPanel list)
    {
        var seen = device.LastSeenAt <= 0 ? "还没连过" : $"最后在线 {HistoryExport.FormatTime(device.LastSeenAt)}";
        var subtitle = $"{device.Platform} · 配对于 {HistoryExport.FormatTime(device.PairedAt)} · {seen}"
                       + (device.Revoked ? " · 已吊销" : string.Empty);

        var revoke = TextButton(
            device.Revoked ? "已吊销" : "吊销",
            "\uE8D7",
            () => _ = RevokeDeviceAsync(device, baseUrl, controlToken, status, list));
        revoke.IsEnabled = !device.Revoked;

        return SettingsUi.Row(device.Name, subtitle, Host, revoke, glyph: "\uE8EA");
    }

    private static async Task RevokeDeviceAsync(
        PairedDevice device, string baseUrl, string controlToken, TextBlock status, StackPanel list)
    {
        if (!await ConfirmAsync(
                $"吊销「{device.Name}」？",
                "吊销后这台设备的令牌立刻失效，手机上要重新扫码配对。\n这是软删除：记录还在，只是不再能用。",
                "吊销"))
        {
            return;
        }

        try
        {
            App.Note(await SyncUploader.RevokeDeviceAsync(baseUrl, controlToken, device));
            await LoadDevicesAsync(status, list); // 列表立刻反映真实状态，不做「看起来成功了」
        }
        catch (Exception error)
        {
            App.Note($"吊销失败：{error.Message}");
        }
    }

    // ===================================================================
    // 5 识别设置（§3.6）
    // ===================================================================
    private static void Recognition()
    {
        var settings = AppSettings.Load();

        Add(SettingsUi.Section(
            "识别设置",
            "截图后怎么识别、一次识别多少页、本机保留多少张图。",
            Host,

            SettingsUi.Group("自动识别", "\uE945", null, Host,
                SettingsUi.Switch("剪贴板监听",
                    "检测到新图片后自动分析（Win+Shift+S 截完即出结果）",
                    Host, settings.ClipboardWatch, value =>
                    {
                        var current = AppSettings.Load();
                        current.ClipboardWatch = value;
                        current.Save();
                    })),

            SettingsUi.Group("图片处理", "\uEB9F", null, Host,
                SettingsUi.Switch("保存图片文件到本地",
                    "关闭后只保留文本结果；重试与框选会不可用",
                    Host, settings.SaveImages, value =>
                    {
                        var current = AppSettings.Load();
                        current.SaveImages = value;
                        current.Save();
                    })),

            SettingsUi.Group("多页识别", "\uE81E",
                "一道题要截好几张（题干 + 选项 + 图）时，攒够页数一次上传。", Host,
                MultiPageField(settings.MultiPageLimit)),

            SettingsUi.Group("本地图片缓存", "\uE838", null, Host,
                SettingsUi.Field("本地图片缓存上限",
                    settings.ImageCacheLimit <= 0
                        ? "当前：不设限（截图会一直留着，占用磁盘会持续增长）"
                        : $"当前：最多保留 {settings.ImageCacheLimit} 张，超出的旧截图在识别完成后自动清理",
                    Host,
                    SettingsUi.Segments(
                    [
                        ("30 张", settings.ImageCacheLimit == 30, () => SetCache(30)),
                        ("60 张", settings.ImageCacheLimit == 60, () => SetCache(60)),
                        ("120 张", settings.ImageCacheLimit == 120, () => SetCache(120)),
                        ("不设限", settings.ImageCacheLimit == 0, () => SetCache(0)),
                    ], Host))),

            SettingsUi.InfoNote(
                "剪贴板监听只认新出现的图片：读到就自动分析，但不会把主窗口抢到前台"
                + "（结果进历史与悬浮窗）；同一张图不会反复识别。不用这个通路时关掉它更省事。", Host)));
    }

    private static void SetCache(int limit)
    {
        var current = AppSettings.Load();
        current.ImageCacheLimit = limit;
        current.Save();
        Host.Refresh();
    }

    private static SettingsCard MultiPageField(int limit)
    {
        var number = new TextBlock
        {
            Text = $"{limit} 页",
            Width = 48,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = UiFont.Demibold,
            Foreground = ShellTheme.Brush("QsOnSurfaceBrush", Host),
        };

        var slider = new Slider
        {
            Minimum = 1,
            Maximum = 6,
            StepFrequency = 1,
            Value = Math.Clamp(limit, 1, 6),
            Width = 260,
            VerticalAlignment = VerticalAlignment.Center,
        };
        slider.ValueChanged += (_, e) => number.Text = $"{(int)Math.Round(e.NewValue)} 页";
        slider.AddHandler(UIElement.PointerReleasedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) =>
            {
                var current = AppSettings.Load();
                current.MultiPageLimit = (int)Math.Round(slider.Value);
                current.Save();
            }), handledEventsToo: true);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        row.Children.Add(new TextBlock { Text = "1", FontSize = QsFontSizes.Caption, VerticalAlignment = VerticalAlignment.Center, Foreground = ShellTheme.Brush("QsOnSurfaceVariantBrush", Host) });
        row.Children.Add(slider);
        row.Children.Add(new TextBlock { Text = "6", FontSize = QsFontSizes.Caption, VerticalAlignment = VerticalAlignment.Center, Foreground = ShellTheme.Brush("QsOnSurfaceVariantBrush", Host) });
        row.Children.Add(number);

        return SettingsUi.Field("多页识别页数上限",
            "抓满上限会自动上传本次全部页面；硬上限 6 页", Host, row);
    }

    // ===================================================================
    // 6 悬浮球设置（§3.7）
    // ===================================================================
    private static void Ball()
    {
        var ball = BallSettings.Load();

        Add(SettingsUi.Section(
            "悬浮球设置",
            "桌面上的圆形悬浮球：随时单击截屏识别，不用切回主窗口。点击逻辑与手机端一致。",
            Host,

            SettingsUi.Group("悬浮球", "\uE7C8", null, Host,
                SettingsUi.Switch("显示悬浮球",
                    "置顶在所有窗口之上；单击识别，左键长按 / 右键追加一页",
                    Host, ball.Enabled, value => SaveBall(b => b.Enabled = value)),
                BallSlider("大小", "当前 {0}（逻辑像素，范围 32–96）", ball.Diameter, 32, 96, 16, "小", "大", 40,
                    value => SaveBall(b => b.Diameter = (int)Math.Round(value))),
                BallSlider("透明度", "当前 {0}%", ball.Opacity, 20, 100, 16, null, null, 52,
                    value => SaveBall(b => b.Opacity = (int)Math.Round(value)), suffix: "%")),

            SettingsUi.Group("描边", "\uE91F",
                "描边画在球的外面，颜色 = 当前状态主色压暗 28%（悬浮球与热键互不影响）。", Host,
                SettingsUi.Switch("开启描边", ball.Stroke ? "已开启（默认开启）" : "已关闭",
                    Host, ball.Stroke, value => SaveBall(b => b.Stroke = value)),
                BallSlider("描边宽度", "当前 {0}", ball.StrokeWidth, 0, 10, 20, null, null, 52,
                    value => SaveBall(b => b.StrokeWidth = (int)Math.Round(value))),
                BallSlider("描边透明度", "当前 {0}%", ball.StrokeOpacity, 0, 100, 20, null, null, 52,
                    value => SaveBall(b => b.StrokeOpacity = (int)Math.Round(value)), suffix: "%")),

            SettingsUi.Group("状态与主色", "\uE790", null, Host,
                StateRow(BallState.Idle, "空闲：单击截屏识别，左键长按 / 右键追加一页"),
                StateRow(BallState.Detecting, "正在识别上一张：此时点击只会提示稍候"),
                StateRow(BallState.MultiPage, "已开始攒页：单击收尾识别本次全部页面")),

            SettingsUi.InfoNote(
                "默认大小 40、透明度 70%。悬浮球与热键互不影响：两者都可以触发识别，热键设置页里暂停的是全局热键。", Host)));
    }

    private static void SaveBall(Action<BallSettings> mutate)
    {
        var ball = BallSettings.Load();
        mutate(ball);
        ball.Save();
        // 「改了设置立刻生效」：重建球（1.1.0 的用户反馈里「调了没反应」是最早被报上来的那类问题）。
        (Application.Current as App)?.ApplyBallSettings();
        Host.Refresh();
    }

    private static SettingsCard StateRow(BallState state, string note)
    {
        var color = Color.FromArgb(
            (byte)((state.Argb() >> 24) & 0xFF), (byte)((state.Argb() >> 16) & 0xFF),
            (byte)((state.Argb() >> 8) & 0xFF), (byte)(state.Argb() & 0xFF));
        return SettingsUi.Row(state.Label(), note, Host, SettingsUi.StatusDot(color));
    }

    private static SettingsCard BallSlider(
        string title, string subtitleFormat, double value,
        double min, double max, double step, string? left, string? right, double boxWidth,
        Action<double> onCommit, string suffix = "")
    {
        var number = new TextBlock
        {
            Text = subtitleFormat.Contains("{0}") ? string.Format(subtitleFormat, (int)Math.Round(value)) .Replace("当前 ", "") : value.ToString(),
            Width = boxWidth,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = UiFont.Demibold,
            Foreground = ShellTheme.Brush("QsOnSurfaceBrush", Host),
        };

        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            StepFrequency = step,
            Value = Math.Clamp(value, min, max),
            Width = 320,
            VerticalAlignment = VerticalAlignment.Center,
        };

        void Sync(double v) => number.Text = $"{(int)Math.Round(v)}{suffix}";
        slider.ValueChanged += (_, e) => Sync(e.NewValue);
        slider.AddHandler(UIElement.PointerReleasedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => onCommit(slider.Value)), handledEventsToo: true);
        Sync(slider.Value);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        if (left is not null)
        {
            row.Children.Add(new TextBlock { Text = left, FontSize = QsFontSizes.Caption, VerticalAlignment = VerticalAlignment.Center, Foreground = ShellTheme.Brush("QsOnSurfaceVariantBrush", Host) });
        }

        row.Children.Add(slider);
        if (right is not null)
        {
            row.Children.Add(new TextBlock { Text = right, FontSize = QsFontSizes.Caption, VerticalAlignment = VerticalAlignment.Center, Foreground = ShellTheme.Brush("QsOnSurfaceVariantBrush", Host) });
        }

        row.Children.Add(number);

        var subtitle = subtitleFormat.Replace("{0}", $"{Math.Round(value)}{suffix}");
        return SettingsUi.Field(title, subtitle, Host, row, 520);
    }

    // ===================================================================
    // 7 悬浮窗设置（§3.8 逐项：开关 / 置顶 / 锁定 / 归位 + 三种外观 + 两档滑块
    //   + 6 套配色 + 明暗三段 + 极简开关 + 透明度 + 页尾说明）
    // ===================================================================
    private static void FloatWindow()
    {
        var settings = AppSettings.Load();

        Add(SettingsUi.Section(
            "悬浮窗设置",
            "桌面上的识别结果小窗：不用切回主窗口，就能看答案、接着识别、翻上一条/下一条。"
            + "默认关闭，打开后会记住状态。默认显示默认模式（只看题目与答案，纯文本、可拖选复制）；"
            + "需要解析与阅读材料时切到「详细解析模式」（与主界面同一套题目卡片）。"
            + "公式的显示分两种情况：默认模式是纯文本，公式按 LaTeX 源码原样显示"
            + "（例如 $x^2-1$ 会原样画出来）；详细解析模式会排版公式（与主窗口同一套渲染）。",
            Host,

            SettingsUi.Group("悬浮窗", "\uE78B", null, Host,
                SettingsUi.Switch("显示悬浮窗", "默认关闭；打开后按下面的外观显示，并记住开关状态",
                    Host, settings.FloatWindowEnabled,
                    value => SaveFloat(s => s.FloatWindowEnabled = value)),
                SettingsUi.Switch("置顶显示",
                    settings.FloatWindowTopmost ? "已置顶（默认）" : "不置顶，会被其他窗口盖住",
                    Host, settings.FloatWindowTopmost,
                    value => SaveFloat(s => s.FloatWindowTopmost = value)),
                SettingsUi.Switch("锁定位置",
                    settings.FloatWindowLocked
                        ? "已锁定：拖不动（窗内「锁定位置」按钮也能切换）"
                        : "未锁定：按住顶部第一栏即可拖动",
                    Host, settings.FloatWindowLocked,
                    value => SaveFloat(s => s.FloatWindowLocked = value)),
                FloatHomeRow()),

            SettingsUi.Group("外观", "\uE799",
                "不支持自由拉伸，只在下面两种外观里选；每种外观都能用下面的「显示比例」整体放大缩小。",
                Host,
                AspectRow("portrait", settings),
                AspectRow("landscape", settings),
                FloatScaleField(settings),
                FloatFontScaleField(settings)),

            SettingsUi.Group("配色与模式", "\uE790",
                "悬浮窗自带 6 套配色，默认是浅色系。题目卡片里的标绿/标黄仍按契约走（那是信息，不是装饰）。",
                Host,
                FloatPaletteField(settings),
                FloatThemeField(settings),
                SettingsUi.Switch("详细解析模式",
                    !settings.FloatWindowMinimal
                        ? "已开启：和主界面一样列出全部选项、解析与阅读材料"
                        : "已关闭（默认模式）：只看题目与答案，一题一段纯文本，可以直接拖选复制",
                    Host, !settings.FloatWindowMinimal,
                    // 开关是**反的**：打开开关 = 关掉极简（1.1.0 的 `value: !app.floatWindowMinimal`）。
                    value => SaveFloat(s => s.FloatWindowMinimal = !value))),

            FloatFooterNote()));
    }

    /// <summary>两种外观的定义（与 `Pages/FloatWindow.xaml.cs` 的 <c>Aspects</c> 同源；改一处必须改两处，
    /// 所以这里刻意把「宽高比 / 基准宽」也写全，方便对照）。
    /// 第 170 轮按用户要求删掉了 `tall`（加长外观 9:30）—— 老配置里的 `tall` 由
    /// `AppSettings.Load` 归一到 `portrait`，所以这里的 `First(...)` 不会再遇到未知 id。</summary>
    private static readonly (string Id, string Label, string Hint, double Ratio, double BaseWidth)[] FloatAspects =
    [
        ("portrait", "标准外观（竖屏 9:20）", "342 × 760", 9.0 / 20.0, 342),
        ("landscape", "横向外观（20:9）", "560 × 252", 20.0 / 9.0, 560),
    ];

    private static readonly (string Id, string Label, string Hex)[] FloatPalettes =
    [
        ("white", "纯净白（默认）", "#64748B"),
        ("mint", "薄荷绿", "#0F9D58"),
        ("sky", "天空蓝", "#2563EB"),
        ("lilac", "丁香紫", "#7C3AED"),
        ("sand", "暖沙", "#D97706"),
        ("rose", "樱粉", "#DB2777"),
    ];

    /// <summary>「归位」：把 x/y 写回 -1（= 从没摆过），窗口下次按默认位置显示。</summary>
    private static SettingsCard FloatHomeRow()
    {
        var button = new Button
        {
            Content = new TextBlock { Text = "恢复默认位置", FontSize = QsFontSizes.Caption },
            Padding = new Thickness(12, 6, 12, 6),
            VerticalAlignment = VerticalAlignment.Center,
        };
        button.Click += (_, _) =>
        {
            // `SaveFloat` 已经会「存盘 + 立刻应用」，应用那一步会重新算几何 ——
            // 所以这里只写 -1，不需要再让窗口自己搬一次（两处都搬会互相打架）。
            SaveFloat(s =>
            {
                s.FloatWindowX = -1;
                s.FloatWindowY = -1;
            });
            App.Note("悬浮窗位置已恢复默认（屏幕右上：距右边缘 24、距顶 120）");
        };

        return SettingsUi.Row("归位", "恢复默认位置：屏幕右上（距右边缘 24、距顶 120）", Host, button);
    }

    /// <summary>一行外观。选中态：`RadioBtnOn` + 「使用中」；未选中：`RadioBtnOff` + 尺寸提示。</summary>
    private static SettingsCard AspectRow(string id, AppSettings settings)
    {
        var aspect = FloatAspects.First(a => a.Id == id);
        var selected = settings.FloatWindowAspect == id;

        var width = (int)Math.Round(aspect.BaseWidth * settings.FloatWindowScale);
        var height = (int)Math.Round(width / aspect.Ratio);
        var subtitle = selected
            ? $"已选择 · 当前窗口 {width}×{height}（{aspect.Hint} @{(int)Math.Round(settings.FloatWindowScale * 100)}%）"
            : $"{aspect.Hint} 逻辑像素 @100%";

        FrameworkElement? trailing = selected
            ? new TextBlock
            {
                Text = "使用中",
                FontSize = QsFontSizes.CaptionSmall,
                FontFamily = UiFont.Demibold,
                Foreground = ShellTheme.Brush("QsBrandPrimaryBrush", Host),
                VerticalAlignment = VerticalAlignment.Center,
            }
            : null;

        var row = SettingsUi.Row(
            aspect.Label,
            subtitle,
            Host,
            trailing,
            selected ? "\uECCB" : "\uECCA");

        // 整行可点：`SettingsCard` 的 `IsClickEnabled` 让它整块可点（与 1.1.0 的可点行一致）。
        row.IsClickEnabled = true;
        row.Click += (_, _) =>
        {
            SaveFloat(s => s.FloatWindowAspect = id);
            App.Note($"悬浮窗外观：{aspect.Label}");
            // 外观换了要把整页重建（三个行的选中态与「使用中」都要跟着变）。
            App.Instance?.ReloadSettingsPage();
        };
        return row;
    }

    /// <summary>
    /// 显示比例滑块。**区间 100%–200%**（用户 2026-09-30：「显示比例调节功能只能在 1.0-2.0 之间」）。
    ///
    /// 下限不再是 50% —— 除了用户不想更小，<see cref="AppSettings.MinScale"/> 那里记了
    /// 一个硬理由：小于 1.0 时窗口逻辑宽度会小到顶栏放不下按钮排，最右的按钮被切到窗外、
    /// 点了没反应（实测 0.5 时 `锁定位置` 只剩 36/60 像素在窗内）。
    /// </summary>
    private static SettingsCard FloatScaleField(AppSettings settings)
    {
        return FloatSliderField(
            title: "显示比例",
            maxWidth: 520,
            minimum: AppSettings.MinScale,
            maximum: AppSettings.MaxScale,
            // 20 档 = 每档 5%（1.0 / 1.05 / … / 2.0），与 1.1.0 的「档位感」一致。
            divisions: 20,
            value: settings.FloatWindowScale,
            subtitle: () =>
            {
                var current = AppSettings.Load();
                var aspect = FloatAspects.First(a => a.Id == current.FloatWindowAspect);
                var width = (int)Math.Round(aspect.BaseWidth * current.FloatWindowScale);
                var height = (int)Math.Round(width / aspect.Ratio);
                return $"当前 {(int)Math.Round(current.FloatWindowScale * 100)}%，窗口约 {width}×{height} 逻辑像素"
                     + $"（{aspect.Hint}，可调范围 100%–200%）";
            },
            apply: value => SaveFloat(
                s => s.FloatWindowScale = Math.Clamp(value, AppSettings.MinScale, AppSettings.MaxScale)));
    }

    private static SettingsCard FloatFontScaleField(AppSettings settings)
    {
        return FloatSliderField(
            title: "窗内字符大小",
            maxWidth: 520,
            minimum: 0.6,
            maximum: 2.0,
            divisions: 14,
            value: settings.FloatWindowFontScale,
            // 规格里这句是刻意写进副标题的：窗内**没有**字号按钮，只能在这里调。
            subtitle: () =>
                $"当前 {(int)Math.Round(AppSettings.Load().FloatWindowFontScale * 100)}%（只能在这里调：窗内不再有字号按钮）",
            apply: value => SaveFloat(s => s.FloatWindowFontScale = Math.Clamp(value, 0.6, 2.0)));
    }

    // ⚠️ 第 170 轮：`FloatOpacityField`（「透明度」滑块）**整项删除**，
    // 连同它写进 app.json 的 `float_opacity` 键（见 `AppSettings`）——
    // 用户原话「悬浮窗删掉透明度调整功能」。原来这里调的是 `Root.Opacity`，
    // 半透明会让压在白底题目上的文字一起变淡，可读性反而更差。

    /// <summary>
    /// 滑块行的统一实现（1.1.0 是 `Slider + 数值框`，拖动中只改显示、松手才落库）。
    /// **`departments` 与步进**：`divisions` 是 1.1.0 的分段数，换算成 WinUI 的 `StepFrequency`
    /// （= (max-min)/divisions），这样两端的「档位感」一致。
    /// </summary>
    private static SettingsCard FloatSliderField(
        string title,
        double maxWidth,
        double minimum,
        double maximum,
        int divisions,
        double value,
        Func<string> subtitle,
        Action<double> apply)
    {
        var number = new TextBlock
        {
            Text = $"{(int)Math.Round(value * 100)}%",
            Width = 52,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = UiFont.Demibold,
            Foreground = ShellTheme.Brush("QsOnSurfaceBrush", Host),
        };

        var slider = new Slider
        {
            Minimum = minimum,
            Maximum = maximum,
            StepFrequency = (maximum - minimum) / divisions,
            Value = value,
            Width = 320,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // 拖动中只改数值显示，松手才落库 + 应用（1.1.0 的 onChangeEnd 语义）。
        slider.ValueChanged += (_, e) => number.Text = $"{(int)Math.Round(e.NewValue * 100)}%";
        slider.AddHandler(UIElement.PointerReleasedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => apply(slider.Value)),
            handledEventsToo: true);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        row.Children.Add(new TextBlock
        {
            Text = "小",
            FontSize = QsFontSizes.Caption,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = ShellTheme.Brush("QsOnSurfaceVariantBrush", Host),
        });
        row.Children.Add(slider);
        row.Children.Add(new TextBlock
        {
            Text = "大",
            FontSize = QsFontSizes.Caption,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = ShellTheme.Brush("QsOnSurfaceVariantBrush", Host),
        });
        row.Children.Add(number);

        return SettingsUi.Field(title, subtitle(), Host, row, maxWidth);
    }

    /// <summary>6 套配色：一排圆角胶囊（色块 + 名称 + 选中打勾）。</summary>
    private static SettingsCard FloatPaletteField(AppSettings settings)
    {
        // 6 个胶囊排成 **3×2 网格**（1.1.0 用的是 Wrap，6 项固定时两者等价）。
        // 不用 `WrapPanel`：它在纯 C# 文件里解析不到（`Microsoft.UI.Xaml.Controls` 里没有这个类型；
        // 题目卡片能用是因为它的 XAML 命名空间映射给生成类补了 using）—— 这里不引入这个不确定性。
        var grid = new Grid { RowSpacing = 8, ColumnSpacing = 8 };
        for (var i = 0; i < 3; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }

        for (var i = 0; i < 2; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        for (var index = 0; index < FloatPalettes.Length; index++)
        {
            var palette = FloatPalettes[index];
            var selected = settings.FloatWindowPalette == palette.Id;
            var label = new TextBlock
            {
                Text = palette.Label,
                FontSize = QsFontSizes.CaptionSmall,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            content.Children.Add(new Border
            {
                Width = 12,
                Height = 12,
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(ParseHex(palette.Hex)),
                VerticalAlignment = VerticalAlignment.Center,
            });
            content.Children.Add(label);
            if (selected)
            {
                content.Children.Add(new FontIcon
                {
                    Glyph = "\uE73E",
                    FontSize = QsFontSizes.Caption,
                    Foreground = ShellTheme.Brush("QsBrandPrimaryBrush", Host),
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }

            var chip = new Border
            {
                Padding = new Thickness(8, 6, 14, 6),
                // 胶囊一律走 PillShape：WinUI 的 CornerRadius 不夹半高，写 999 会画成椭圆（踩过）。
                Background = new SolidColorBrush(Blend(ParseHex(palette.Hex), Colors.White, 0.16)),
                BorderBrush = selected
                    ? ShellTheme.Brush("QsBrandPrimaryBrush", Host)
                    : ShellTheme.Brush("QsCardStrokeBrush", Host),
                BorderThickness = new Thickness(selected ? 2 : 1),
                Child = content,
            };
            PillShape.MakeCapsule(chip);

            chip.Tapped += (_, _) =>
            {
                SaveFloat(s =>
                {
                    s.FloatWindowPalette = palette.Id;
                    // **只改配色，不碰明暗**（用户 2026-09-29 报「一换颜色就自动跳深色」）。
                    // 这里原来抄的是 1.1.0 更早一版的写法：选 `white` / `lilac` 时顺手把
                    // `float_theme` 设成 dark。那条规则在 M33 就被**反掉**了 ——
                    // `docs/DECISIONS.md` 写着「配色只决定色系（6 套全是浅色），明暗由明暗模式决定，
                    // 两者正交」，理由正是「选『明亮 + 石墨』时按哪个？」这种打架。
                    // 现在的配色种子是各色系的中性色（`white` 是中性灰蓝），浅底深字本来就清楚，
                    // 没有「必须配深色才读得清」这回事。
                });
                App.Note($"悬浮窗配色：{palette.Label}");
                App.Instance?.ReloadSettingsPage();
            };

            Grid.SetColumn(chip, index % 3);
            Grid.SetRow(chip, index / 3);
            grid.Children.Add(chip);
        }

        var current = FloatPalettes.First(p => p.Id == settings.FloatWindowPalette);
        // 副标题里**不再有**「已配深色模式」这类话：配色与明暗正交，选哪套配色都不改明暗
        // （用户 2026-09-29：「一换颜色就自动跳深色」）。
        var subtitle = $"当前：{current.Label}"
            + (settings.FloatWindowPalette == "white" ? "（默认）" : string.Empty);

        return SettingsUi.Field("配色", subtitle, Host, grid, 620);
    }

    /// <summary>明暗模式三段：跟随软件设置 / 明亮 / 深色。
    /// 用 <c>RadioButtons</c> 而不是 `SelectorBar`：后者在装不下时会把项**收进溢出菜单**，
    /// 而溢出项不进 UIA 树 —— 实测三段里少一段（验收脚本抓到「明亮」找不到），
    /// 也意味着屏幕阅读器读不到它。`RadioButtons` 每项都是标准名字，没有这个问题。</summary>
    private static SettingsCard FloatThemeField(AppSettings settings)
    {
        var options = new (string Id, string Label)[]
        {
            ("followApp", "跟随软件设置"),
            ("light", "明亮"),
            ("dark", "深色"),
        };

        var radios = new RadioButtons
        {
            MaxColumns = 3,
            VerticalAlignment = VerticalAlignment.Center,
        };
        foreach (var (_, label) in options)
        {
            radios.Items.Add(label);
        }

        radios.SelectedIndex = Array.FindIndex(options, o => o.Id == settings.FloatWindowTheme) is var index && index >= 0
            ? index
            : 0;
        radios.SelectionChanged += (_, _) =>
        {
            var picked = radios.SelectedIndex;
            if (picked >= 0 && picked < options.Length)
            {
                SaveFloat(s => s.FloatWindowTheme = options[picked].Id);
            }
        };

        return SettingsUi.Field("明暗模式", ThemeLabel(settings.FloatWindowTheme), Host, radios, 620);
    }

    private static string ThemeLabel(string id) => id switch
    {
        "light" => "当前：明亮",
        "dark" => "当前：深色",
        _ => "当前：跟随软件设置",
    };

    /// <summary>页尾说明（规格 §3.8 末尾逐字）。</summary>
    private static TextBlock FloatFooterNote()
    {
        return new TextBlock
        {
            Text = "顶部第一栏：左边是「第几次识别 / 时间」，右边一排图标按钮（鼠标悬停会显示名称），"
                + "顺序是 下一条 · 上一条 · 重新识别 · 默认模式 · 锁定位置。"
                + "底部是「识别一张」「多页识别」两个按钮（默认模式下多一个「复制识别内容」，排在最后；"
                + "多页时变成「结束并上传」「继续添加页」「取消多页」）。滚轮滚动内容，识别进行中的提示贴在内容区下部。"
                + "开关悬浮窗也可以在托盘右键菜单里切。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = QsFontSizes.CaptionSmall,
            LineHeight = 18,
            Foreground = ShellTheme.Brush("QsOnSurfaceVariantBrush", Host),
            Margin = new Thickness(0, 4, 0, 0),
        };
    }

    private static Color ParseHex(string hex)
    {
        var value = hex.TrimStart('#');
        return Color.FromArgb(
            255,
            Convert.ToByte(value[..2], 16),
            Convert.ToByte(value.Substring(2, 2), 16),
            Convert.ToByte(value.Substring(4, 2), 16));
    }

    private static Color Blend(Color seed, Color basis, double alpha) => Color.FromArgb(
        255,
        (byte)Math.Round((seed.R * alpha) + (basis.R * (1 - alpha))),
        (byte)Math.Round((seed.G * alpha) + (basis.G * (1 - alpha))),
        (byte)Math.Round((seed.B * alpha) + (basis.B * (1 - alpha))));

    /// <summary>
    /// 悬浮窗的设置项：**存盘 + 立刻生效**。
    /// 与悬浮球同一条纪律 —— 1.1.0 里「调了没反应」是最早被报上来的那类问题；
    /// 这里不做「只落库不生效」的假开关。
    /// </summary>
    private static void SaveFloat(Action<AppSettings> mutate)
    {
        var current = AppSettings.Load();
        mutate(current);
        current.Save();
        App.Instance?.ApplyFloatWindowSettings();
    }

    // ===================================================================
    // 8 热键设置（§3.9）
    // ===================================================================
    private static void Hotkey()
    {
        var settings = AppSettings.Load();
        // **真实生效的键优先**：被别的程序占用时会自动换键，`App` 里那个才是「按下去会发生什么」的依据。
        var capture = App.CaptureHotkeyLabel ?? settings.CaptureHotkey?.Label ?? "未注册";
        var multipage = App.MultipageHotkeyLabel ?? settings.MultipageHotkey?.Label ?? "未注册";

        Add(SettingsUi.Section(
            "热键设置",
            "两个用途各自注册一个全局热键，默认是 F8（截屏识别）与 F9（多页模式），在后台随时可按；"
            + "被其他程序占用时会自动换到下一个可用候选键。",
            Host,

            SettingsUi.InfoNote(
                "⏸ 停在本页时按键不会有任何反应（避免设置热键时误触发截屏）；离开本页、或切到别的程序，热键立刻恢复。", Host),

            SettingsUi.Group("截屏识别", "\uE8B9",
                "不在多页模式时＝截一张屏立刻识别；正在多页模式里时＝结束多页，把已抓的图一起上传识别。", Host,
                SettingsUi.Row("截取屏幕并识别", $"当前：{capture}", Host,
                    WithKeycap(capture, TextButton("更改…", "\uE70F", () => _ = ChangeHotkeyAsync(captureSlot: true))))),

            SettingsUi.Group("多页模式", "\uE81E",
                "一道题跨了几屏时：按第一下进入多页并抓第一张，继续按追加；抓满上限自动上传识别，"
                + "不满就按「截屏识别」结束并上传。", Host,
                SettingsUi.Row("多页模式", $"当前：{multipage}", Host,
                    WithKeycap(multipage, TextButton("更改…", "\uE70F", () => _ = ChangeHotkeyAsync(captureSlot: false))))),

            SettingsUi.Group("其他", "\uE712", null, Host,
                SettingsUi.Row("恢复默认热键", "把两个用途都改回默认键：截屏识别 F8 · 多页模式 F9", Host,
                    TextButton("恢复默认", "\uE777", () => _ = ResetHotkeysAsync())),
                SettingsUi.Row("托盘菜单", "热键被占用时，托盘右键菜单里有同样的两项操作", Host))));
    }

    /// <summary>
    /// 改一个热键：弹录入框 → 校验 → 落盘 → **立刻重新注册**（换键必须先注销，见 `ApplyCaptureHotkey`）。
    /// 每一步的结果都报出来：注册失败时会说清是被占用还是组合键不合法，绝不假装改成功。
    /// </summary>
    private static async Task ChangeHotkeyAsync(bool captureSlot)
    {
        var which = captureSlot ? "截屏识别" : "多页模式";
        if (await CaptureHotkeyAsync(which) is not { } chord)
        {
            App.Note($"{which}的热键没有改");
            return;
        }

        var settings = AppSettings.Load();
        if (captureSlot)
        {
            settings.CaptureHotkey = chord;
        }
        else
        {
            settings.MultipageHotkey = chord;
        }

        settings.Save();

        var status = captureSlot ? App.ApplyCaptureHotkey(chord) : App.ApplyMultipageHotkey(chord);
        App.Note($"{which}的热键：{status}");
        App.Instance?.ReloadSettingsPage();
    }

    /// <summary>恢复默认热键（F8 / F9）：清掉自定义值、落盘、重新注册，并把两个用途各自的真实状态报出来。</summary>
    private static async Task ResetHotkeysAsync()
    {
        if (!await ConfirmAsync(
                "恢复默认热键？",
                "两个用途都会改回默认：截屏识别 F8、多页模式 F9。\n当前自定义的键会被丢弃。",
                "恢复默认"))
        {
            return;
        }

        var settings = AppSettings.Load();
        settings.CaptureHotkey = null;
        settings.MultipageHotkey = null;
        settings.Save();

        var capture = App.ApplyCaptureHotkey(null);
        var multipage = App.ApplyMultipageHotkey(null);
        App.Note($"已恢复默认热键 —— 截屏识别：{capture}；多页模式：{multipage}");
        App.Instance?.ReloadSettingsPage();
    }

    /// <summary>只按修饰键（Ctrl / Alt / Shift / Win 及其左右键）时不算组合键，继续等下一次按键。</summary>
    private static bool IsModifierKey(Windows.System.VirtualKey key) =>
        (uint)key is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;

    /// <summary>当前按下的修饰键（读的是**当场**的键盘状态，不是 `KeyRoutedEventArgs` 里的字段）。</summary>
    private static (HotkeyModifiers Modifiers, string Prefix) CurrentModifiers()
    {
        var modifiers = HotkeyModifiers.None;
        var parts = new List<string>();

        if (Down(Windows.System.VirtualKey.Control))
        {
            modifiers |= HotkeyModifiers.Control;
            parts.Add("Ctrl");
        }

        if (Down(Windows.System.VirtualKey.Menu))
        {
            modifiers |= HotkeyModifiers.Alt;
            parts.Add("Alt");
        }

        if (Down(Windows.System.VirtualKey.Shift))
        {
            modifiers |= HotkeyModifiers.Shift;
            parts.Add("Shift");
        }

        if (Down(Windows.System.VirtualKey.LeftWindows) || Down(Windows.System.VirtualKey.RightWindows))
        {
            modifiers |= HotkeyModifiers.Win;
            parts.Add("Win");
        }

        return (modifiers, parts.Count == 0 ? string.Empty : string.Join("+", parts) + "+");
    }

    private static bool Down(Windows.System.VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    /// <summary>键名（用于标签，也用于落盘后重建）。不认识的键返回 null —— 由调用方**说出原因**。</summary>
    private static string? KeyName(Windows.System.VirtualKey key)
    {
        var vk = (uint)key;
        if (vk is >= 0x70 and <= 0x87)
        {
            return $"F{vk - 0x70 + 1}";
        }

        if (vk is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A)
        {
            return ((char)vk).ToString();
        }

        return key switch
        {
            Windows.System.VirtualKey.Space => "Space",
            Windows.System.VirtualKey.Tab => "Tab",
            Windows.System.VirtualKey.Enter => "Enter",
            Windows.System.VirtualKey.Escape => "Esc",
            Windows.System.VirtualKey.Insert => "Insert",
            Windows.System.VirtualKey.Delete => "Delete",
            Windows.System.VirtualKey.Home => "Home",
            Windows.System.VirtualKey.End => "End",
            Windows.System.VirtualKey.PageUp => "PageUp",
            Windows.System.VirtualKey.PageDown => "PageDown",
            Windows.System.VirtualKey.Left => "Left",
            Windows.System.VirtualKey.Right => "Right",
            Windows.System.VirtualKey.Up => "Up",
            Windows.System.VirtualKey.Down => "Down",
            _ => null,
        };
    }

    /// <summary>
    /// 「按下你想要的组合键」录入框。返回 null = 用户取消。
    ///
    /// 三条要点（都是这个项目踩过的）：
    /// 1. 用 `AddHandler(..., handledEventsToo: true)` 抓按键 —— 焦点在 `TextBox` 上时按键会被它先吃掉；
    /// 2. 录入期间热键是**被吞掉**的（`App.HotkeySuppressed` 由设置页置位），
    ///    否则按 F8 会当场截屏、按 F9 会直接跳走，用户根本没法录入；
    /// 3. 非法组合**当场说原因**并继续等（不是默默失败）：只按修饰键、单按字母、键名不认识，三种各有各的说法。
    /// </summary>
    private static async Task<HotkeyChord?> CaptureHotkeyAsync(string which)
    {
        if (_dialogOpen)
        {
            return null;
        }

        _dialogOpen = true;
        try
        {
            var box = new TextBox
            {
                Text = "等待按键…",
                IsReadOnly = true,
                FontFamily = UiFont.Demibold,
                FontSize = QsFontSizes.Body,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };

            var error = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Foreground = ShellTheme.Brush("QsErrorBrush", Host),
                Visibility = Visibility.Collapsed,
            };

            var panel = new StackPanel { Spacing = 10 };
            panel.Children.Add(new TextBlock
            {
                Text = "点一下下面的框，再按下你想要的组合键。\n"
                       + "单按 F1–F12 可以；字母与数字请加 Ctrl / Alt / Shift / Win。",
                TextWrapping = TextWrapping.Wrap,
            });
            panel.Children.Add(box);
            panel.Children.Add(error);

            HotkeyChord? picked = null;
            var dialog = new ContentDialog
            {
                Title = $"设置「{which}」热键",
                Content = panel,
                PrimaryButtonText = "保存",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                IsPrimaryButtonEnabled = false,
                XamlRoot = Host.XamlRoot,
            };

            void ShowProblem(string text)
            {
                picked = null;
                dialog.IsPrimaryButtonEnabled = false;
                error.Text = text;
                error.Visibility = Visibility.Visible;
            }

            void OnKey(object sender, KeyRoutedEventArgs args)
            {
                if (IsModifierKey(args.Key))
                {
                    return; // 只按了修饰键 —— 继续等主键
                }

                var name = KeyName(args.Key);
                if (name is null)
                {
                    ShowProblem("这个键暂时不支持，换一个（支持 F1–F24、字母、数字与常用编辑键）");
                    args.Handled = true;
                    return;
                }

                var (modifiers, prefix) = CurrentModifiers();
                var chord = new HotkeyChord(prefix + name, (uint)args.Key, modifiers);
                if (HotkeyRules.RejectReason(chord) is { } reason)
                {
                    box.Text = chord.Label;
                    ShowProblem(reason);
                    args.Handled = true;
                    return;
                }

                picked = chord;
                box.Text = chord.Label;
                error.Visibility = Visibility.Collapsed;
                dialog.IsPrimaryButtonEnabled = true;
                args.Handled = true;
            }

            dialog.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnKey), handledEventsToo: true);
            dialog.Opened += (_, _) => box.Focus(FocusState.Programmatic);

            return await dialog.ShowAsync() == ContentDialogResult.Primary ? picked : null;
        }
        catch (Exception error)
        {
            App.Note($"热键录入框打不开：{error.Message}");
            return null;
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    /// <summary>行尾的「动作按钮 + 当前键位药丸」组合（`Row` 的 trailing 只有一格）。</summary>
    private static StackPanel WithKeycap(string label, FrameworkElement action)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(action);
        row.Children.Add(Keycap(label));
        return row;
    }

    private static Border Keycap(string label) => new()    {
        Padding = new Thickness(8, 4, 8, 4),
        CornerRadius = new CornerRadius(6),
        BorderThickness = new Thickness(1),
        BorderBrush = ShellTheme.Brush("QsOutlineBrush", Host),
        Background = ShellTheme.Brush("QsSurfaceBrush", Host),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = label,
            FontSize = QsFontSizes.Caption,
            FontFamily = UiFont.Demibold,
            Foreground = ShellTheme.Brush("QsOnSurfaceBrush", Host),
        },
    };

    // ===================================================================
    // 9 数据管理（§3.10）
    // ===================================================================
    private static void Data()
    {
        var directory = AppDataDirectory.Ensure();

        Add(SettingsUi.Section(
            "数据管理",
            "任务集合、导出与备份。数据默认就在软件所在目录下，导出时会让你自己选保存位置。",
            Host,

            SettingsUi.Group("任务集合", "\uE8B7", null, Host,
                SettingsUi.Row("当前集合", CollectionNote(), Host,
                    TextButton("切换任务合集…", "\uE8AB", () => App.Note("请回主界面点顶栏的合集药丸切换"))),
                SettingsUi.Row("按合集导出", "把当前合集里所有识别记录导成一个 Markdown（与主界面顶栏那个按钮同一条路径）", Host,
                    TextButton("导出合集", "\uE8A5", () => _ = ExportCollectionAsync()))),

            SettingsUi.Group("数据与备份", "\uE8F4", null, Host,
                SettingsUi.Row("一键备份", "数据库 + 图片打包成 zip，存到数据目录 backups/ 下", Host,
                    TextButton("立即备份", "\uE8F4", () => _ = BackupAsync()), glyph: "\uE8F4"),
                SettingsUi.Row("从最近备份恢复", "恢复前自动为当前数据留底；兼容 1.1.0 的旧备份包", Host,
                    TextButton("从备份恢复", "\uE777", () => _ = RestoreAsync()), glyph: "\uE777"),
                SettingsUi.Row("导出全部历史", "全部识别记录（含未分类），按识别顺序；两种格式自选", Host,
                    TwoButtons(
                        ("Markdown", "\uE8A5", () => _ = ExportAllAsync(asJson: false)),
                        ("JSON", "\uE943", () => _ = ExportAllAsync(asJson: true)))),
                SettingsUi.Row("导出日志", "不含 API Key 与 Token（导出前自动脱敏，脱敏规则见 `HistoryExport.RedactSecrets`）", Host,
                    TextButton("导出日志", "\uE8A5", () => _ = ExportLogsAsync()), glyph: "\uE8A5")),

            SettingsUi.Group("数据位置", "\uE8B7", null, Host,
                SettingsUi.Row("应用数据目录", directory, Host,
                    TextButton("打开目录", "\uE838", () => OpenFolder(directory)))),

            SettingsUi.InfoNote("恢复会覆盖当前数据、吊销会让手机的 token 立即失效：这两类操作都会先弹确认框。", Host)));
    }

    /// <summary>一行里放两个动作按钮（设置页原来只有一个 trailing 位置，导入导出这种要两个格式）。</summary>
    private static StackPanel TwoButtons(params (string Label, string Glyph, Action OnClick)[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var (label, glyph, onClick) in buttons)
        {
            row.Children.Add(TextButton(label, glyph, onClick));
        }

        return row;
    }

    /// <summary>
    /// 「按合集导出」：与主界面顶栏的「导出当前合集」走**同一个** `ExportSupport.ExportCollectionAsync` ——
    /// 这正是原来那处「顶栏能用、设置页写还没接上」的不一致。
    /// </summary>
    private static async Task ExportCollectionAsync()
    {
        var id = AppSettings.Load().CurrentCollectionId;
        if (string.IsNullOrEmpty(id))
        {
            App.Note("还没有选合集，先回主界面点顶栏的合集名字选一个");
            return;
        }

        try
        {
            using var database = QuizDatabase.OpenOrCreate(AppDataDirectory.DatabasePath());
            App.Note(await ExportSupport.ExportCollectionAsync(database, id));
        }
        catch (Exception error)
        {
            App.Note($"导出失败：{error.Message}");
        }
    }

    /// <summary>「导出全部历史」：Markdown 或 JSON（两种格式由两个按钮区分，不靠猜后缀）。</summary>
    private static async Task ExportAllAsync(bool asJson)
    {
        try
        {
            using var database = QuizDatabase.OpenOrCreate(AppDataDirectory.DatabasePath());
            App.Note(await ExportSupport.ExportAllHistoryAsync(database, asJson));
        }
        catch (Exception error)
        {
            App.Note($"导出失败：{error.Message}");
        }
    }

    /// <summary>「导出日志」：把 `logs\` 下所有文件拼成一个 txt，**过脱敏**再落盘。</summary>
    private static async Task ExportLogsAsync()
    {
        try
        {
            App.Note(await ExportSupport.ExportLogsAsync());
        }
        catch (Exception error)
        {
            App.Note($"导出日志失败：{error.Message}");
        }
    }

    /// <summary>
    /// 「一键备份」：库 + 图片 → `backups/quizsync-backup-<时间戳>.zip`。
    /// 非破坏性操作，**不弹确认框**（弹了反而让人以为有风险）。
    /// </summary>
    private static async Task BackupAsync()
    {
        try
        {
            var target = Path.Combine(
                AppDataDirectory.BackupsDirectory(),
                $"quizsync-backup-{DateTime.Now:yyyyMMdd-HHmmss}.zip");

            var result = await Task.Run(() => BackupArchive.Create(
                AppDataDirectory.DatabasePath(),
                AppDataDirectory.ImagesDirectory(),
                target));

            App.Note($"已备份 {result.Entries} 个条目（图片 {result.Images} 张）→ {target}");
        }
        catch (Exception error)
        {
            App.Note($"备份失败：{error.Message}");
        }
    }

    /// <summary>
    /// 「从最近备份恢复」：取 `backups/` 下最新的 zip，确认后恢复。
    /// 结果分三种说法（**不能一句「恢复成功」糊过去**）：
    /// 已换库（要重启）/ 已暂存（下次启动换入）/ 没换成（带原因）。
    /// </summary>
    private static async Task RestoreAsync()
    {
        var directory = AppDataDirectory.BackupsDirectory();
        var newest = Directory.Exists(directory)
            ? new DirectoryInfo(directory).GetFiles("*.zip")
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .FirstOrDefault()
            : null;

        if (newest is null)
        {
            App.Note($"没有可恢复的备份：{directory} 下还没有 zip");
            return;
        }

        string describe;
        try
        {
            var manifest = BackupArchive.Inspect(newest.FullName);
            var when = manifest.CreatedAt is null ? "无时间戳（1.1.0 旧包）" : manifest.CreatedAt;
            var format = manifest.Format == "legacy" ? "1.1.0 旧格式" : "2.0 格式";
            describe = $"{newest.Name}（{format}，{when}，图片 {manifest.Images} 张）"
                + (manifest.HasDatabase ? string.Empty : "\n⚠ 这个包里没有数据库，恢复只会补图片、不会动现有数据");
        }
        catch (Exception error)
        {
            App.Note($"这个备份包读不开：{error.Message}");
            return;
        }

        var confirmed = await ConfirmAsync(
            "从备份恢复？",
            $"将用 {describe} 覆盖当前数据。\n恢复前会自动为当前数据留底（`.pre-restore-<时间戳>`）。",
            "恢复");

        if (!confirmed)
        {
            return;
        }

        try
        {
            var outcome = await Task.Run(() => BackupArchive.Restore(
                newest.FullName,
                AppDataDirectory.DatabasePath(),
                AppDataDirectory.ImagesDirectory()));

            App.Note(outcome switch
            {
                { Applied: true } => $"已恢复（图片 {outcome.Images} 张）：重启应用后生效",
                { PendingRestart: true } => $"已暂存（图片 {outcome.Images} 张）：下次启动时自动换入",
                _ => outcome.Reason ?? "恢复没有生效",
            });
        }
        catch (Exception error)
        {
            App.Note($"恢复失败：{error.Message}");
        }
    }

    private static bool _dialogOpen;

    /// <summary>
    /// 设置页的确认框。**WinUI 同时只允许一个 `ContentDialog`**，第二个会抛异常 ——
    /// 这个坑在 2.0 上真炸过一次（整个进程 APPCRASH），所以入口有重入守卫。
    /// </summary>
    private static async Task<bool> ConfirmAsync(string title, string body, string primary)
    {
        if (_dialogOpen)
        {
            return false;
        }

        _dialogOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = primary,
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Host.XamlRoot,
            };
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch (Exception error)
        {
            App.Note($"确认框打不开：{error.Message}");
            return false;
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    /// <summary>
    /// <summary>
    /// 「主机状态」那一行：真的探活一次再说自己是什么状态。
    ///
    /// 三种必须分得开，因为它们对用户的影响完全不同：
    /// **没在跑**（要说清楚开哪个开关）、**跑在本应用里**（关掉应用就断）、
    /// **跑在独立进程里**（关掉应用它照旧在）。1.1.0 只有一种，2.0 起有两种，
    /// 混成一句「服务已启动」等于没告诉用户「关掉应用手机还连不连得上」。
    /// </summary>
    private static async Task RefreshHostStatusAsync(TextBlock status, string? dataDirectory)
    {
        try
        {
            var probe = await new QuizSync.ServerBridge.HostDiscovery().ProbeAsync();
            var embedded = QuizSync.ServerBridge.HostLauncher.Embedded;

            if (probe is null)
            {
                status.Text = "未运行。打开上面的「开启连接设备」即可（主机与识别服务一起启动）。";
                return;
            }

            var where = embedded is not null
                        && string.Equals(embedded.BaseUrl, probe.BaseUrl, StringComparison.OrdinalIgnoreCase)
                ? "本应用内嵌（退出应用即停止）"
                : "独立进程（退出本应用它照旧运行）";

            // 识别服务在线与否也要写出来：手机截屏的链路少它就走不通（主机自己不持有 API Key）。
            var online = await QuizSync.ServerBridge.ProviderLauncher.IsOnlineAsync(probe.BaseUrl);
            var provider = online switch
            {
                true => "识别服务在线",
                false => "识别服务不在线：点「重启连接服务」，或去 API 配置里填 Key",
                null => "识别服务状态未知（主机没回 /api/v2/info）",
            };

            status.Text = $"运行中：{probe.BaseUrl} · {where}"
                + $"\n{provider}"
                + $"\n{SyncLine()}"
                + $"\n数据目录：{dataDirectory ?? "找不到服务端数据目录"}";
        }
        catch (Exception error)
        {
            status.Text = $"探测主机失败：{error.Message}";
        }
    }

    /// <summary>
    /// 一句话说明「本机改动推上去了没有」+ 上一次补推的结果（第 170 轮）。
    ///
    /// 为什么必须摆在界面上：用户报的是「安卓也不同步」，而这条链路原来**只写日志** ——
    /// 识别完弹出的是「推送失败（改动仍在本机）：unauthorized (401): token 无效」，
    /// 30 秒一次的补推又按内容去重只写一行 `crash.log`，界面上一个字都看不到。
    /// 实测：2026-09-30 01:39/01:40 两次识别各 5 / 17 道题，一条都没上主机（水位停在 38），
    /// 手机端自然什么都没有，而用户完全不知道发生了什么。
    /// </summary>
    private static string SyncLine()
    {
        try
        {
            using var database = QuizSync.Data.QuizDatabase.OpenOrCreate(AppDataDirectory.DatabasePath());
            var pending = SyncUploader.PendingCount(database);
            var line = pending == 0
                ? "同步：本机改动都已推给主机"
                : $"同步：还有 {pending} 条改动没推上去（每 30 秒自动补推一次）";

            // 上次补推失败过就把原因写出来（成功的那句不重复写，界面已经够长）。
            if (App.LastSyncState is { Length: > 0 } last && last.Contains('败', StringComparison.Ordinal))
            {
                line += $"\n上次补推：{last}";
            }

            return line;
        }
        catch (Exception error)
        {
            return $"同步：读不到本地库（{error.Message}）";
        }
    }

    /// <summary>
    /// 第 169 轮起「启动 / 重启主机与识别服务」只有一条路径（`App.StartConnectionAsync`）——
    /// 原来那两个手动按钮（「检测并启动主机」「启动识别服务」）连同它们的方法一起删掉了：
    /// 总开关已经一键做完全部事情，留着只会让人以为「不点它就连不上」。
    /// </summary>
    private static string CollectionNote()
    {
        var id = AppSettings.Load().CurrentCollectionId;
        if (string.IsNullOrEmpty(id))
        {
            return "未选择合集";
        }

        try
        {
            using var database = QuizSync.Data.QuizDatabase.OpenOrCreate(AppDataDirectory.DatabasePath());
            var name = QuizSync.Data.CollectionStore.List(database)
                .FirstOrDefault(c => c.CollectionId == id)?.Name;
            return name is null ? "未选择合集" : $"当前合集：{name}";
        }
        catch (Exception)
        {
            return "未选择合集";
        }
    }

    private static Button TextButton(string label, string glyph, Action onClick)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 16 });
        content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });

        var button = new Button
        {
            Content = content,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Foreground = ShellTheme.Brush("QsBrandPrimaryBrush", Host),
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    private static void OpenFolder(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch (Exception error)
        {
            App.Note($"打开目录失败：{error.Message}");
        }
    }

    // ===================================================================
    // 10 关于（§3.11）
    // ===================================================================
    private static void About()
    {
        var version = typeof(SettingsTabs).Assembly.GetName().Version;
        var versionText = version is null ? "v0.0" : $"v{version.Major}.{version.Minor}.{version.Build}";

        var logo = new Border
        {
            Width = 72,
            Height = 72,
            Padding = new Thickness(4),
            CornerRadius = new CornerRadius(16),
            BorderThickness = new Thickness(1),
            BorderBrush = ShellTheme.Brush("QsOutlineBrush", Host),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        logo.Child = new Image
        {
            Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/app_icon.png")),
            Stretch = Stretch.Uniform,
        };

        var header = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 520 };
        header.Children.Add(logo);
        header.Children.Add(SettingsUi.Gap(16));
        header.Children.Add(new TextBlock
        {
            Text = AppWindowScope.Title,
            FontSize = QsFontSizes.Title,
            FontFamily = UiFont.Demibold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = ShellTheme.Brush("QsOnSurfaceBrush", Host),
        });
        header.Children.Add(SettingsUi.Gap(4));
        header.Children.Add(new TextBlock
        {
            Text = $"Windows 桌面版 · {versionText}",
            FontSize = QsFontSizes.Body,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = ShellTheme.Brush("QsOnSurfaceVariantBrush", Host),
        });
        header.Children.Add(SettingsUi.Gap(2));
        header.Children.Add(new TextBlock
        {
            Text = "QuizSync AI · 开源地址见下方",
            FontSize = QsFontSizes.Body,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = ShellTheme.Brush("QsOnSurfaceVariantBrush", Host),
        });
        header.Children.Add(SettingsUi.Gap(16));
        header.Children.Add(new TextBlock
        {
            Text = "Windows 截屏 → 多模态 AI 识别题目并给出答案与解析 → 结果同步到手机。",
            FontSize = QsFontSizes.Body,
            LineHeight = 20,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = ShellTheme.Brush("QsOnSurfaceVariantBrush", Host),
        });

        Add(header);

        Add(SettingsUi.Section(string.Empty, null, Host,
            SettingsUi.Group("项目", "\uE943", null, Host,
                SettingsUi.Row("GitHub 仓库", "https://github.com/iop666/QuizSyncAI", Host,
                    TextButton("打开", "\uE8A7", () => OpenUrl("https://github.com/iop666/QuizSyncAI")))),

            SettingsUi.Group("技术", "\uE950", null, Host,
                SettingsUi.Row("C# / .NET 10 + WinUI 3",
                    "Windows 端为原生实现：Win32 分层窗口（悬浮球 / 悬浮窗）、自绘题目卡片、原生托盘与全局热键", Host),
                SettingsUi.Row("Kotlin + Jetpack Compose",
                    "Android 端为原生实现：MediaProjection 截屏 + 悬浮球，任务提交与结果同步复用同一套协议", Host),
                SettingsUi.Row("协议与数据模型",
                    "两端共用 QuizSyncProtocol 的 schema 与一致性向量（120 步）作为唯一判据", Host)),

            SettingsUi.Group("字体与许可", "\uE8D2", null, Host,
                SettingsUi.Row("界面字体",
                    "系统默认字体（Segoe UI Variable / Segoe UI）—— 2.0 起不再随包字体，"
                    + "所以界面文字与系统其它应用一致。", Host, glyph: "\uE8D2"),
                SettingsUi.Row("第三方字体",
                    "界面不使用随包字体，因此不再需要 MiSans 的授权说明。",
                    Host, glyph: "\uE8D7"),
                SettingsUi.Row("Fluent 图标（Segoe Fluent Icons）",
                    "随 Windows 分发，无需额外授权", Host)),

            SettingsUi.InfoNote(
                "当前版本不带自动更新：升级直接安装新版安装包即可，卸载不会删除你的数据。", Host),

            SettingsUi.Group("赞助支持", "\uEB51",
                "这个工具完全免费、开源，不弹广告，也不上传你的题目与截图；所有识别结果与图片都只存在你自己的电脑上。", Host,
                SettingsUi.Row("爱发电", "https://afdian.com/a/iop666", Host,
                    TextButton("打开", "\uE8A7", () => OpenUrl("https://afdian.com/a/iop666"))))));
    }

    private static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (Exception error)
        {
            App.Note($"打开链接失败：{error.Message}");
        }
    }
}
