namespace QuizSync.Provider;

/// <summary>
/// 托盘右键菜单里的一项。**顺序即界面顺序**，出处 ui-spec-desktop §1.4 的 13 项表
/// （<c>desktop/lib/main.dart:677-714</c>）。
/// </summary>
public enum TrayCommand
{
    /// <summary>有暂存时文案变成「结束多页并识别 (F8)」，否则「截取屏幕并识别 (F8)」。</summary>
    Capture,

    /// <summary>热键被占用时文案变成「多页识别（热键被占用）」。</summary>
    MultiPage,

    /// <summary>「清空多页暂存区（N 页）」。</summary>
    ClearStaging,

    /// <summary>「从剪贴板读取」。</summary>
    Clipboard,

    /// <summary>勾选项：开 → 「隐藏悬浮球」，关 → 「显示悬浮球」。</summary>
    ToggleBall,

    /// <summary>勾选项：开 → 「隐藏悬浮窗」，关 → 「显示悬浮窗」。</summary>
    ToggleFloatWindow,

    /// <summary>「切换任务合集…」。</summary>
    SwitchCollection,

    /// <summary>「显示主窗口」。</summary>
    ShowWindow,

    /// <summary>「设置」。</summary>
    Settings,

    /// <summary>「退出」。</summary>
    Exit,
}

/// <summary>
/// 托盘菜单的**纯模型**：布局、id ↔ 命令的映射、默认文案。
///
/// **刻意与 <see cref="TrayIcon"/> 分开**：那一位是 Win32（`Shell_NotifyIcon`、
/// 隐藏窗口、`TrackPopupMenuEx`），在非 Windows 上是不可调用的；
/// 而这一位一条 Win32 都不碰，纯数据 + 纯函数 ——
/// 分开之后它就能被**单测直接钉住**，也不必给测试项目挂「仅 Windows」的平台标注。
///
/// 为什么值得为「一张表」单独建个类：id → 命令是「布局下标 + 1」的手写规则，
/// **差一位**的表现是「点了 A 执行了 B」，在托盘菜单上极难被发现（菜单不方便自动化点击）。
/// 把它变成纯函数之后，这条规则就有回归测试了。
/// </summary>
public static class TrayMenu
{
    /// <summary>菜单布局，顺序即界面顺序。<c>null</c> = 分隔线。</summary>
    public static readonly IReadOnlyList<TrayCommand?> Layout =
    [
        TrayCommand.Capture,
        TrayCommand.MultiPage,
        TrayCommand.ClearStaging,
        TrayCommand.Clipboard,
        null,
        TrayCommand.ToggleBall,
        TrayCommand.ToggleFloatWindow,
        null,
        TrayCommand.SwitchCollection,
        TrayCommand.ShowWindow,
        TrayCommand.Settings,
        null,
        TrayCommand.Exit,
    ];

    /// <summary>
    /// 菜单项 id → 命令。id 的规则是「<see cref="Layout"/> 的下标 + 1」；
    /// 分隔线、越界 id 一律返回 null（不会误触发命令）。
    /// </summary>
    public static TrayCommand? CommandForMenuId(int menuId) =>
        menuId >= 1 && menuId <= Layout.Count && Layout[menuId - 1] is { } command ? command : null;

    /// <summary>菜单项的 id（布局下标 + 1）。分隔线没有 id（返回 0）。</summary>
    public static int MenuIdOf(TrayCommand command)
    {
        for (var i = 0; i < Layout.Count; i++)
        {
            if (Layout[i] == command)
            {
                return i + 1;
            }
        }

        return 0;
    }

    /// <summary>
    /// 初始文案（调用方随后会用 <c>SetLabel</c> 换成带热键 / 带页数的动态文案）。
    /// 逐字照 §1.4 的表。
    /// </summary>
    public static string DefaultLabel(TrayCommand command) => command switch
    {
        TrayCommand.Capture => "截取屏幕并识别",
        TrayCommand.MultiPage => "多页识别",
        TrayCommand.ClearStaging => "清空多页暂存区（0 页）",
        TrayCommand.Clipboard => "从剪贴板读取",
        TrayCommand.ToggleBall => "显示悬浮球",
        TrayCommand.ToggleFloatWindow => "显示悬浮窗",
        TrayCommand.SwitchCollection => "切换任务合集…",
        TrayCommand.ShowWindow => "显示主窗口",
        TrayCommand.Settings => "设置",
        TrayCommand.Exit => "退出",
        _ => command.ToString(),
    };
}
