using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using QuizSync.App.Theme;
using QuizSync.Provider;

using QuizSync.Data;
namespace QuizSync.App;

/// <summary>
/// 外壳主窗口：只做导航宿主（Frame），页面在 <c>Pages/</c> 下。
///
/// 结构上有个**刻意保持的不对称**（照 1.1.0）：主界面（<see cref="Pages.HomePage"/>）
/// 是「顶栏 + 侧栏 + 内容区」，**没有左侧主导航**；带左栏导航的是设置页，
/// 而且设置页是**压栈打开的新页面**（1.1.0 走 <c>Navigator.push</c>），
/// 所以这里用 Frame 导航而不是在主界面里切内容。
/// </summary>
public sealed partial class MainWindow : Window
{
    /// <summary>默认尺寸 1180×760、最小 860×520（1.1.0 <c>main.dart:198,205</c>）。</summary>
    private const int DefaultWidth = 1180;
    private const int DefaultHeight = 760;
    private const int MinWidth = 860;
    private const int MinHeight = 520;

    /// <summary>自定义标题栏那条 1–2px 缝的补偿；系统标题栏时保持 <c>null</c>。</summary>
    private TitleBarTrim? _titleBarTrim;

    public MainWindow()
    {
        InitializeComponent();
        // 标题只认唯一的那个常量（XAML 里那份只是设计器预览用的写法，这里覆盖掉）。
        Title = AppWindowScope.Title;
        ApplyTitleBar();
        WindowSizing.Apply(this, DefaultWidth, DefaultHeight, MinWidth, MinHeight);
        // 原生系统背板（Mica / 亚克力）。不支持的机器（Win10 没有 Mica）自动退回纯色画布 ——
        // 判据是「当前操作系统」，与打包方式无关，见 Theme/QsBackdrop.cs 的说明。
        var backdrop = QsBackdrop.Apply(this);
        // 这两条都是**按机器分叉**的，而界面上看不出来（背板被不透明画布盖着、
        // 不支持自定义标题栏时只是回到系统栏）。写一行日志，省得靠推断
        // （取证方式与未处理异常一致，见 CrashLog 的说明）。
        CrashLog.Write(
            $"外壳分支：标题栏={(_titleBarTrim is null ? "系统标题栏" : "自定义 TitleBar")}"
            + $"  背板={backdrop}  OS={Environment.OSVersion.Version}");
        Closed += (_, _) => _titleBarTrim?.Dispose();
        RootFrame.Navigate(StartPage());
    }

    /// <summary>
    /// 页面宿主 Frame。**窗口内容外面还包着一层 Grid（标题栏）**，
    /// 所以外部一律走这里，别再写 `window.Content is Frame`（那会静默失效）。
    /// </summary>
    internal Frame ShellFrame => RootFrame;

    /// <summary>
    /// 换成自定义标题栏（WinAppSDK 1.7 的 <c>TitleBar</c> 控件），照 WinUI-Gallery 的
    /// <c>MainWindow.xaml.cs</c> L148-L151。
    ///
    /// 两条与 Gallery 不同的取舍，都有理由：
    /// ① <c>AppWindowTitleBar.IsCustomizationSupported()</c> 为假时**什么都不做**，
    ///    并把我们那个 <c>TitleBar</c> 元素收起来 —— 否则窗口上会出现**两条**标题栏；
    /// ② 高度用 <c>Standard</c> 而不是 Gallery 的 <c>Tall</c>：Gallery 的标题栏里塞了搜索框，
    ///    我们只有图标 + 文字，Tall 白多 16px。
    /// </summary>
    private void ApplyTitleBar()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            AppTitleBar.Visibility = Visibility.Collapsed;
            return;
        }

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;

        // 官方 issue microsoft-ui-xaml#9934 的补偿：补在**标题栏正下方**那个元素上（这里是 Frame）。
        _titleBarTrim = TitleBarTrim.Attach(this, RootFrame);
    }

    /// <summary>
    /// 设置窗口标题。**必须同时改我们自己的 <c>TitleBar</c>**：
    /// 自定义标题栏之后 <c>Window.Title</c> 只剩「任务栏 / 任务视图 / UIA」在用，
    /// 屏幕上看得见的那一行是 <c>AppTitleBar.Title</c> —— 只改前者等于把提示藏了，
    /// 而本仓库的纪律是这类提示**绝不能静默吞掉**（原来没有日志面板，标题就是提示位）。
    /// </summary>
    internal void ShowTitle(string text)
    {
        Title = text;
        AppTitleBar.Title = text;
    }

    /// <summary>
    /// 首次使用门（1.1.0 的 <c>CollectionGate</c>，ui-spec-desktop §2.3.1）：
    /// **一个合集都没有就直接进「新建任务合集」页**，而不是先给一个红色的「未选择合集」主界面。
    ///
    /// 「识别结果归属于某个合集」是这个应用的第一个心智模型，1.1.0 用一整页来立它。
    ///
    /// ⚠️ **判据不许写成「合集数 == 0」**（本轮修的 bug）：那句话把
    /// 「库里确实没有」与「这次没读出来」混成一件事，读失败时用户会看到自己的合集全没了。
    /// 判据与日志现在统一走 <see cref="QuizSync.Data.StartupProbe"/>（三态，且能单测），
    /// <see cref="Pages.CollectionPickerPage"/> 用的是同一个判据 —— 两份判据必然打架。
    /// </summary>
    private static Type StartPage()
    {
        var probe = QuizSync.Data.StartupProbe.Probe(AppDataDirectory.DatabasePath());

        if (probe.Failure is { } failure)
        {
            // 既有方式：漏到 XAML 层的异常一律先落一行日志（见 CrashLog 的注释）。
            // 这里不再是静默 catch —— 上一次出这个现象时，日志里什么都没有，只能靠猜。
            CrashLog.Write("启动时读合集失败（按主界面处理，不进首次使用门页）", failure);
        }

        return probe.Landing == QuizSync.Data.StartupLanding.FirstUse
            ? typeof(Pages.CollectionPickerPage)
            : typeof(Pages.HomePage);
    }
}
