using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using QuizSync.App.Theme;
using QuizSync.Provider;

using QuizSync.Data;
namespace QuizSync.App;

/// <summary>
/// 设置页里那些**还没有专属存储文件**的项（`<app>/app.json`）。
///
/// 为什么不塞进 `ui.json` / `ball.json` / `ai.json`：那三个文件各自对应一个已经有主的主题
/// （界面字号字重主题 / 悬浮球外观 / AI 配置），把识别、悬浮窗、热键混进去会让以后每加一项
/// 都要动已有结构。这里的口径与 `BallSettings` 一致：**一个主题一个文件**。
///
/// ⚠️ **这个类里存的项分两类，界面上必须分清楚**（见 `docs/DECISIONS.md` 与设置页里的提示）：
/// - **真接上的**：`SaveImages` / `ImageCacheLimit`（识别流程真的读它）、
///   `MultiPageLimit`（多页暂存区读它）、`ClipboardWatch`（剪贴板监听真的在听）、
///   `ConnectEnabled`（真的决定要不要拉起主机）、`CaptureHotkey` / `MultipageHotkey`
///   （真的向系统重新注册）；
/// - **还没接上的**：`UiScale`（WinUI 没有 Flutter 那套虚拟画布）——
///   界面上**显式写明**，不做静默空操作。
/// </summary>
internal sealed class AppSettings
{
    private const string FileName = "app.json";

    /// <summary>多页识别的硬上限（1.1.0 `kHardMaxPagesPerTask = 6`）。</summary>
    internal const int HardMaxPagesPerTask = 6;

    /// <summary>本地图片缓存上限的可选档位；0 = 不设限（1.1.0 `_cacheChoices`）。</summary>
    internal static readonly int[] ImageCacheChoices = [30, 60, 120, 0];

    /// <summary>界面缩放档位（1.1.0 `kUiScalePresets`）。</summary>
    internal static readonly double[] UiScalePresets =
        [0.5, 0.75, 1.0, 1.25, 1.5, 1.75, 2.0, 2.25, 2.5, 2.75, 3.0];

    // ===================================================================
    // 识别设置
    // ===================================================================

    /// <summary>剪贴板监听（1.1.0 默认开）。**真接上**：`ClipboardWatchHook` 监听 WM_CLIPBOARDUPDATE。</summary>
    internal bool ClipboardWatch { get; set; } = true;

    /// <summary>保存图片文件到本地（默认开）。**真接上**：关掉后识别流程不写图。</summary>
    internal bool SaveImages { get; set; } = true;

    /// <summary>多页识别页数上限（默认 6，范围 1–6）。**真接上**：`MultipageStaging` 读它。</summary>
    internal int MultiPageLimit { get; set; } = HardMaxPagesPerTask;

    /// <summary>本地图片缓存上限（默认 60，0 = 不设限）。**真接上**：识别完按它剪枝。</summary>
    internal int ImageCacheLimit { get; set; } = 60;

    // ===================================================================
    // 显示设置（原本在 UiSettings.cs 里 —— 已合并进来，见下面的说明）
    // ===================================================================

    /// <summary>字号：1.1.0 默认 16，范围 12–32（`ui_pkg/src/settings.dart:46,119,207`）。**真接上**。</summary>
    internal double FontSize { get; set; } = DefaultFontSize;

    /// <summary>
    /// 题目字重。1.1.0 里构造函数的字面默认是 400，但全新安装走 `fromMap` 的兜底常量
    /// **500**，所以实际生效值是 500（ui-spec-desktop §5.4 的注）。档位 300/400/500/600/700。
    /// **真接上**：改完立刻重画主界面题目卡片。
    /// </summary>
    internal int FontWeight { get; set; } = DefaultFontWeight;

    /// <summary>明暗：`system` / `light` / `dark`（1.1.0 的「主题」三态）。**真接上**。</summary>
    internal string Theme { get; set; } = "system";

    /// <summary>上次进入的任务合集（1.1.0 把当前合集记在设置里，下次打开直接进）。**真接上**。</summary>
    internal string? CurrentCollectionId { get; set; }

    internal const double DefaultFontSize = QsFontSizes.Subtitle;

    internal const int DefaultFontWeight = 500;

    internal const double MinFontSize = QsFontSizes.Caption;

    internal const double MaxFontSize = 32;

    internal const int MinFontWeight = 200;

    internal const int MaxFontWeight = 800;

    /// <summary>界面上可选的档位（1.1.0 `kQuestionFontWeights = [300,400,500,600,700]`）。</summary>
    internal static readonly int[] FontWeightSteps = [300, 400, 500, 600, 700];

    /// <summary>界面缩放（1.1.0 默认 1.0）。**还没接上**：WinUI 没有 Flutter 那套虚拟画布，
    /// 设置页里如实写明这一点（这一项的值会存下来，但不改变界面缩放）。</summary>
    internal double UiScale { get; set; } = 1.0;

    // ===================================================================
    // 连接设备（开关真的决定要不要拉起独立的主机进程，见 SettingsTabs.SetConnectEnabled）
    // ===================================================================

    internal bool ConnectEnabled { get; set; }

    // ===================================================================
    // 悬浮窗（1.1.0 的 `float_window_*` 一整套；窗口本身见 Pages/FloatWindow.xaml）
    // ===================================================================

    internal bool FloatWindowEnabled { get; set; }

    internal bool FloatWindowTopmost { get; set; } = true;

    internal bool FloatWindowLocked { get; set; }

    /// <summary>
    /// 外观 id：`portrait` / `landscape`。
    ///
    /// ⚠️ 1.1.0 的第三种外观 `tall`（加长外观 9:30）**已在第 170 轮按用户要求删除** ——
    /// 用户原话「悬浮窗删除加长外观选项」。老配置文件里可能还写着 `tall`，
    /// 所以 <see cref="Load"/> 会把它**归一到 `portrait`**：不归一的话设置页那句
    /// `FloatAspects.First(a =&gt; a.Id == …)` 会直接抛 `InvalidOperationException`，
    /// 表现是「进了悬浮窗设置页就崩」。
    /// </summary>
    internal string FloatWindowAspect { get; set; } = DefaultAspect;

    /// <summary>允许的外观 id（白名单；设置页与窗口两份表都以它为准）。</summary>
    internal static readonly string[] AspectIds = ["portrait", "landscape"];

    internal const string DefaultAspect = "portrait";

    /// <summary>
    /// 显示比例的**唯一区间**（用户 2026-09-30 要求：「显示比例调节功能只能在 1.0-2.0 之间」）。
    ///
    /// 为什么必须定死下限（不只是「用户不想更小」）：小于 1.0 时窗口逻辑宽度会小到
    /// 顶栏放不下那一排按钮 —— 实测 0.5 时窗口只有 171 逻辑像素宽，而 5 个图标按钮
    /// 自身就要 158 + 内边距 20，于是最右那个按钮**被切到窗外**（`锁定位置` 的可点宽度
    /// 60 → 36 像素），底部按钮同样被切。用户看到的就是「点了没反应」。
    /// </summary>
    internal const double MinScale = 1.0;

    internal const double MaxScale = 2.0;

    internal double FloatWindowScale { get; set; } = MinScale;

    internal double FloatWindowFontScale { get; set; } = 1.0;

    /// <summary>配色 id：`white` / `mint` / `sky` / `lilac` / `sand` / `rose`。</summary>
    internal string FloatWindowPalette { get; set; } = "white";

    /// <summary>明暗：`followApp` / `light` / `dark`。</summary>
    internal string FloatWindowTheme { get; set; } = "followApp";

    /// <summary>true = 默认模式（极简，1.1.0 的默认值）。</summary>
    internal bool FloatWindowMinimal { get; set; } = true;

    // ⚠️ `float_opacity`（透明度）**已在第 170 轮按用户要求整项删除**
    // （用户原话「悬浮窗删掉透明度调整功能」）。老配置文件里那个键会被忽略，
    // 下次保存时它自然消失；窗口始终按 100% 不透明画（见 `FloatWindow.ApplyChrome`）。

    /// <summary>
    /// 窗口左上角位置（**逻辑像素**；`-1` = 从没摆过 → 用默认位置：屏幕右侧、距右边缘 24）。
    /// 与 1.1.0 的 `float_x` / `float_y` 同义 —— 设置页的「恢复默认位置」就是把这两个写回 `-1`。
    /// </summary>
    internal double FloatWindowX { get; set; } = -1;

    internal double FloatWindowY { get; set; } = -1;

    // ===================================================================
    // 热键设置。null = 用默认键。
    // ===================================================================

    /// <summary>截屏识别的自定义组合键。**真接上**：改完立刻向系统重新注册。</summary>
    internal HotkeyChord? CaptureHotkey { get; set; }

    /// <summary>多页模式的自定义组合键。**真接上**：与截屏键同一条路径注册。</summary>
    internal HotkeyChord? MultipageHotkey { get; set; }

    // ===================================================================
    // 隐私告知（首次保存 API Key 前弹一次，1.1.0 同款）
    // ===================================================================

    internal bool PrivacyAcknowledged { get; set; }

    private static string FilePath => Path.Combine(AppDataDirectory.Ensure(), FileName);

    /// <summary>老版本的界面设置文件（字号 / 字重 / 主题 / 当前合集）。只为**迁移**读它，不再往它写。</summary>
    private static string LegacyFilePath => Path.Combine(AppDataDirectory.Ensure(), "ui.json");

    /// <summary>读设置。文件不存在或坏了都退回默认值（不因为一个坏配置让设置页打不开）。</summary>
    internal static AppSettings Load()
    {
        var settings = new AppSettings();
        if (!File.Exists(FilePath))
        {
            // 迁移：老安装只有 ui.json（字号 / 字重 / 主题 / 当前合集），
            // 不能因为换了文件名就把用户的字号与主题丢掉。
            settings.ReadLegacyUiJson();
            return settings;
        }

        try
        {
            if (JsonNode.Parse(File.ReadAllText(FilePath)) is JsonObject json)
            {
                settings.ReadDisplay(json);
                settings.ClipboardWatch = json["clipboard_watch"]?.GetValue<bool>() ?? settings.ClipboardWatch;
                settings.SaveImages = json["save_images"]?.GetValue<bool>() ?? settings.SaveImages;
                settings.MultiPageLimit = Math.Clamp(
                    json["multi_page_limit"]?.GetValue<int>() ?? settings.MultiPageLimit, 1, HardMaxPagesPerTask);
                settings.ImageCacheLimit = Math.Clamp(
                    json["image_cache_limit"]?.GetValue<int>() ?? settings.ImageCacheLimit, 0, 100000);
                settings.UiScale = Math.Clamp(
                    json["ui_scale"]?.GetValue<double>() ?? settings.UiScale, 0.5, 3.0);
                settings.ConnectEnabled = json["connect_enabled"]?.GetValue<bool>() ?? settings.ConnectEnabled;

                settings.FloatWindowEnabled = json["float_enabled"]?.GetValue<bool>() ?? settings.FloatWindowEnabled;
                settings.FloatWindowTopmost = json["float_topmost"]?.GetValue<bool>() ?? settings.FloatWindowTopmost;
                settings.FloatWindowLocked = json["float_locked"]?.GetValue<bool>() ?? settings.FloatWindowLocked;
                // 外观：白名单归一（老配置里的 `tall` 已不存在 → 退回竖屏），
                // 比例夹到唯一的区间里（见 `MinScale` / `MaxScale` 的说明）。
                settings.FloatWindowAspect = json["float_aspect"]?.ToString() is { Length: > 0 } aspect
                    && AspectIds.Contains(aspect, StringComparer.Ordinal)
                    ? aspect : DefaultAspect;
                settings.FloatWindowScale = Math.Clamp(
                    json["float_scale"]?.GetValue<double>() ?? settings.FloatWindowScale, MinScale, MaxScale);
                settings.FloatWindowFontScale = Math.Clamp(
                    json["float_font_scale"]?.GetValue<double>() ?? settings.FloatWindowFontScale, 0.6, 2.0);
                settings.FloatWindowPalette = json["float_palette"]?.ToString() is { Length: > 0 } palette
                    ? palette : settings.FloatWindowPalette;
                settings.FloatWindowTheme = json["float_theme"]?.ToString() is { Length: > 0 } theme
                    ? theme : settings.FloatWindowTheme;
                settings.FloatWindowMinimal = json["float_minimal"]?.GetValue<bool>() ?? settings.FloatWindowMinimal;
                // 位置：**不夹取**（多屏时坐标可以是负的、也可以很大）；`-1` 是「从没摆过」的哨兵值。
                settings.FloatWindowX = json["float_x"]?.GetValue<double>() ?? settings.FloatWindowX;
                settings.FloatWindowY = json["float_y"]?.GetValue<double>() ?? settings.FloatWindowY;

                settings.CaptureHotkey = ReadChord(json["capture_hotkey"]);
                settings.MultipageHotkey = ReadChord(json["multipage_hotkey"]);
                settings.PrivacyAcknowledged =
                    json["privacy_acknowledged"]?.GetValue<bool>() ?? settings.PrivacyAcknowledged;
            }
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException)
        {
            return new AppSettings();
        }

        return settings;
    }

    internal void Save()
    {
        var json = new JsonObject
        {
            ["font_size"] = FontSize,
            ["font_weight"] = FontWeight,
            ["theme"] = Theme,
            ["current_collection_id"] = CurrentCollectionId,
            ["clipboard_watch"] = ClipboardWatch,
            ["save_images"] = SaveImages,
            ["multi_page_limit"] = MultiPageLimit,
            ["image_cache_limit"] = ImageCacheLimit,
            ["ui_scale"] = UiScale,
            ["connect_enabled"] = ConnectEnabled,
            ["float_enabled"] = FloatWindowEnabled,
            ["float_topmost"] = FloatWindowTopmost,
            ["float_locked"] = FloatWindowLocked,
            ["float_aspect"] = FloatWindowAspect,
            ["float_scale"] = FloatWindowScale,
            ["float_font_scale"] = FloatWindowFontScale,
            ["float_palette"] = FloatWindowPalette,
            ["float_theme"] = FloatWindowTheme,
            ["float_minimal"] = FloatWindowMinimal,
            ["float_x"] = FloatWindowX,
            ["float_y"] = FloatWindowY,
            ["capture_hotkey"] = WriteChord(CaptureHotkey),
            ["multipage_hotkey"] = WriteChord(MultipageHotkey),
            ["privacy_acknowledged"] = PrivacyAcknowledged,
        };

        File.WriteAllText(FilePath, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>显示设置那四项（字号 / 字重 / 主题 / 当前合集）。</summary>
    private void ReadDisplay(JsonObject json)
    {
        if (json["font_size"]?.GetValue<double>() is { } size)
        {
            FontSize = Math.Clamp(size, MinFontSize, MaxFontSize);
        }

        if (json["font_weight"]?.GetValue<int>() is { } weight)
        {
            FontWeight = Math.Clamp(weight, MinFontWeight, MaxFontWeight);
        }

        if (json["theme"]?.ToString() is { Length: > 0 } theme)
        {
            Theme = theme;
        }

        CurrentCollectionId = json["current_collection_id"]?.ToString();
    }

    /// <summary>
    /// 从老文件 `ui.json` 迁移显示设置（旧版把字号 / 字重 / 主题 / 当前合集放那儿）。
    /// 只在 `app.json` 还不存在时走这条路；迁移后**不再写回** ui.json，避免两份同时存在。
    /// </summary>
    private void ReadLegacyUiJson()
    {
        if (!File.Exists(LegacyFilePath))
        {
            return;
        }

        try
        {
            if (JsonNode.Parse(File.ReadAllText(LegacyFilePath)) is JsonObject json)
            {
                ReadDisplay(json);
            }
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException)
        {
            // 老文件坏了就按默认值走，不让设置页打不开。
        }
    }

    /// <summary>组合键落盘：标签 + 虚拟键码 + 修饰键（够原样重建一个 `HotkeyChord`）。</summary>
    private static JsonObject? WriteChord(HotkeyChord? chord) => chord is null
        ? null
        : new JsonObject
        {
            ["label"] = chord.Label,
            ["vk"] = (int)chord.VirtualKey,
            ["mods"] = (int)chord.Modifiers,
        };

    private static HotkeyChord? ReadChord(JsonNode? node)
    {
        if (node is not JsonObject json)
        {
            return null;
        }

        var label = json["label"]?.ToString();
        if (string.IsNullOrWhiteSpace(label))
        {
            return null;
        }

        // 虚拟键码非法（0 = 没主键）时当作没存过：宁可退回默认键，也不要注册一个坏组合。
        var vk = json["vk"]?.GetValue<int>() ?? 0;
        if (vk <= 0)
        {
            return null;
        }

        return new HotkeyChord(label, (uint)vk, (HotkeyModifiers)(json["mods"]?.GetValue<int>() ?? 0));
    }
}
