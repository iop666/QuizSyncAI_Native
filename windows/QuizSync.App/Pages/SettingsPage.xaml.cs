using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using QuizSync.App.Theme;

namespace QuizSync.App.Pages;

/// <summary>设置页的 10 个分类。**顺序即界面顺序**（ui-spec-desktop §3.1）。</summary>
internal enum SettingsTab
{
    Guide,
    Display,
    Api,
    Device,
    Recognition,
    Ball,
    FloatWindow,
    Hotkey,
    Data,
    About,
}

/// <summary>
/// 设置页外壳：左导航 + 内容区。分类内容在 <see cref="SettingsTabs"/> 里拼。
///
/// **2.0 改造**：左导航与返回按钮换成原生 <see cref="NavigationView"/>
/// （原来是一套手搓的 <c>Button</c> 列表 + <c>SizeChanged</c> 里算断点）。
/// 十个分类、每个设置项的功能与落库键位**一个都没动** —— 只换外衣（design-system-plan §3）。
///
/// 1.1.0 的结构（<c>settings_page.dart:13-136,139-211</c>）：
/// 左栏 232（内容区宽度 &lt; 880 时收成 60 的图标栏），右栏内容居中限宽 940、内边距 水平 48 垂直 28，
/// 切分类时右侧滚动位置归零（<c>_scroll.jumpTo(0)</c>）。
/// 这三条现在分别由 <c>OpenPaneLength</c>、<c>ExpandedModeThresholdWidth</c>、
/// <c>ContentScroll.ChangeView</c> 承担。
/// </summary>
public sealed partial class SettingsPage : Page
{
    // 图标码位逐个对过**官方名表**（WinUI-Gallery 的 Samples/Iconography/IconsData.json，
    // 1533 条 Name/Code/Tags）—— 图标名不能靠记忆，原来就有四个是错的：
    //   * E9D9 = Diagnostic（体检/心率）当成了「API 配置」；
    //   * E701 = Wifi 当成了「连接设备」（页面是跟手机配对，不是无线信号）；
    //   * E91B = Photo 当成了「悬浮球」（和「识别设置」的 Picture 语义重复）；
    //   * E8A7 = OpenInNewWindow（外链）当成了「悬浮窗」。
    // 现在换成：钥匙(Permissions) / 连接(Connect) / 手指点触(TouchPointer) / 窗口(NewWindow)。
    // 悬浮球那个中间返工过一次：先换成 E91F FullCircleMask，用户说「太奇怪」——
    // 空心大圆在 16px 下读不出「球」，改成 E7C9 TouchPointer 表达「能点、能拖」。
    private static readonly (SettingsTab Tab, string Label, string Glyph)[] Categories =
    [
        (SettingsTab.Guide, "使用说明", "\uE82D"),        // Dictionary
        (SettingsTab.Display, "显示设置", "\uE790"),      // Color
        (SettingsTab.Api, "API 配置", "\uE8D7"),          // Permissions（钥匙）
        (SettingsTab.Device, "连接设备", "\uE703"),       // Connect
        (SettingsTab.Recognition, "识别设置", "\uE8B9"),  // Picture
        (SettingsTab.Ball, "悬浮球设置", "\uE7C8"),       // Record（实心圆点）
        (SettingsTab.FloatWindow, "悬浮窗设置", "\uE78B"),// NewWindow
        (SettingsTab.Hotkey, "热键设置", "\uE765"),       // KeyboardClassic
        (SettingsTab.Data, "数据管理", "\uE8B7"),         // Folder
        (SettingsTab.About, "关于", "\uE946"),            // Info
    ];

    private SettingsTab _tab = SettingsTab.Guide;

    /// <summary>构造函数里设 <c>SelectedItem</c> 会触发一次 <c>SelectionChanged</c>；
    /// 那一次不该重复渲染（内容还没建）。</summary>
    private bool _initializing = true;

    public SettingsPage()
    {
        InitializeComponent();
        VersionText.Text = $"{QuizSync.Provider.AppWindowScope.Title} v{VersionOf()}";
        BuildNav();
        // 先渲染、再把 _initializing 放下来：首次渲染整页有自己的入场动效（PageEntrance），
        // 内容区不该再叠一次淡入。顺序反过来的话首次渲染会多播一次（看着像闪）。
        Render();
        _initializing = false;
        Loaded += (_, _) =>
        {
            // 停在设置页时热键被**刻意吞掉**（1.1.0 的语义，见 `App.HotkeySuppressed` 的说明）：
            // 否则在「热键设置」里按 F8 / F9 会当场截屏，用户根本没法录入新热键。
            App.HotkeySuppressed = true;
            QsAnimations.PageEntrance(this);
        };
        Unloaded += (_, _) => App.HotkeySuppressed = false;
    }

    /// <summary>
    /// 版本号取自程序集（**不写死**）：1.1.0 底部那行是 <c>'$kAppName v$kAppVersion'</c>，
    /// 2.0 没有单独的版本常量 —— 写死一个数字就成了发明，直接读程序集版本，
    /// 位置与文案格式和 1.1.0 保持一致。
    /// </summary>
    private static string VersionOf()
    {
        var version = typeof(SettingsPage).Assembly.GetName().Version;
        return version is null ? "0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    // ===================================================================
    // 左导航（原生 NavigationView）
    // ===================================================================
    private void BuildNav()
    {
        Nav.MenuItems.Clear();

        foreach (var (tab, label, glyph) in Categories)
        {
            var item = new NavigationViewItem
            {
                Content = label,
                Tag = tab,
                Icon = new FontIcon { Glyph = glyph },
            };
            ToolTipService.SetToolTip(item, label);
            Nav.MenuItems.Add(item);
        }

        Nav.SelectedItem = Nav.MenuItems[0];
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is SettingsTab tab)
        {
            _tab = tab;
        }

        if (_initializing)
        {
            return;
        }

        // 切分类时右侧滚动位置归零（1.1.0 settings_page.dart:70-75）。
        ContentScroll.ChangeView(null, 0, null);
        Render();
    }

    private void OnNavBackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args) => GoBack();

    private void GoBack()
    {
        // 返回走原生 SlideFromLeft（2.0 的动效层，见 Theme/QsAnimations.cs）。
        if (Frame is { } frame && QsAnimations.NavigateBack(frame))
        {
            return;
        }

        if (Frame is { } fallback)
        {
            QsAnimations.NavigateForward(fallback, typeof(HomePage));
        }
    }

    // ===================================================================
    // 内容
    // ===================================================================
    private void Render()
    {
        ContentHost.Children.Clear();
        SettingsTabs.Build(_tab, ContentHost, this);

        // 原地换内容：只淡入不位移（切分类时右侧滚动刚好被重置到顶部，
        // 再叠一个位移会看成抖动）。首次渲染不播 —— 那时整页还有自己的入场动效。
        if (!_initializing)
        {
            QsAnimations.ContentSwap(ContentHost);
        }
    }

    /// <summary>重新渲染当前分类（分类里改完设置后调它）。</summary>
    internal void Refresh() => Render();
}
