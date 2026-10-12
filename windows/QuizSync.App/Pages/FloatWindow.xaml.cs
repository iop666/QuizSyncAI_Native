using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using QuizSync.App.Controls;
using QuizSync.App.Theme;
using QuizSync.Core;
using QuizSync.Data;
using Windows.Graphics;
using Windows.UI;

namespace QuizSync.App.Pages;

/// <summary>
/// 桌面悬浮窗的宿主窗口（规格 `ui-spec-desktop` §2.5 → SPEC §6.1–6.5）。
///
/// **它是真的 XAML 窗口，不是 Win32 分层窗口** —— 与悬浮球刻意不同：
/// 浮层里要放按钮、要能拖选文本、要跟随主题，用原生窗口这几件事都是白拿的；
/// 计划 §Phase 6 第③项就是这么定的（悬浮球继续走分层窗口）。
///
/// 设计上的一条硬约束：**这个窗口是"视图"，不是第二条数据通路。**
/// 它自己只做两件事 —— 从库里读（与主界面同一批静态 store）、把动作转给 `App` 上已有的入口
/// （`RequestCapture` / `RequestMultipage` / `ClearStaging` / `RequestReanalyzeFor` / `RequestCopyFor`）。
/// 重新识别与复制**没有在这里重写一遍**：`App` 发请求，主界面用它自己那份实现去执行
/// （两份实现漂移过一次就够受了）。
/// </summary>
internal sealed partial class FloatWindow : Window
{
    /// <summary>
    /// 两种外观：id / 标签 / 提示 / 宽高比 / 基准宽（1.1.0 `kFloatWindowAspects` 减去 `tall`）。
    ///
    /// ⚠️ 第 170 轮按用户要求**删掉了「加长外观（竖屏 9:30）」**（原话「悬浮窗删除加长外观选项」）。
    /// 与 `SettingsTabs.FloatAspects` 必须同源 —— 改一处必须改两处；老配置里的 `tall`
    /// 由 `AppSettings.Load` 归一到 `portrait`（这里 `AspectOf` 的兜底是第二道保险）。
    /// </summary>
    private static readonly (string Id, string Label, string Hint, double Ratio, double BaseWidth)[] Aspects =
    [
        ("portrait", "标准外观（竖屏 9:20）", "342 × 760", 9.0 / 20.0, 342),
        ("landscape", "横向外观（20:9）", "560 × 252", 20.0 / 9.0, 560),
    ];

    /// <summary>6 套配色。`white` 的种子是**中性灰蓝**，不是绿色 —— 见 `docs/DECISIONS.md` 里
    /// 「中性配色不能给绿色种子」那条（1.1.0 的 M33 踩过：选了「纯净白」满窗发绿）。</summary>
    private static readonly (string Id, string Label, Color Seed)[] Palettes =
    [
        ("white", "纯净白（默认）", Color.FromArgb(255, 0x64, 0x74, 0x8B)),
        ("mint", "薄荷绿", Color.FromArgb(255, 0x0F, 0x9D, 0x58)),
        ("sky", "天空蓝", Color.FromArgb(255, 0x25, 0x63, 0xEB)),
        ("lilac", "丁香紫", Color.FromArgb(255, 0x7C, 0x3A, 0xED)),
        ("sand", "暖沙", Color.FromArgb(255, 0xD9, 0x77, 0x06)),
        ("rose", "樱粉", Color.FromArgb(255, 0xDB, 0x27, 0x77)),
    ];

    private QuizDatabase? _database;
    private IReadOnlyList<RecordedSession> _sessions = [];
    private int _index;
    private AppSettings _settings = new();

    /// <summary>位置防抖落库（见 <see cref="OnAppWindowChanged"/>）。</summary>
    private DispatcherTimer? _positionSave;

    /// <summary>`ApplyGeometry` 正在自己挪窗口：那时的位置变化不算「用户拖过」。</summary>
    private bool _applyingGeometry;

    /// <summary>第一次几何应用完成之前不落库（否则会把构造期的默认位置写进设置）。</summary>
    private bool _geometryApplied;

    // 拖动（只认顶部第一栏）
    private bool _dragging;
    private PointInt32 _dragPointerStart;
    private double _dragOriginX;
    private double _dragOriginY;

    private bool _dark;
    private Color _seed = Color.FromArgb(255, 0x64, 0x74, 0x8B);

    internal FloatWindow()
    {
        InitializeComponent();

        // **圆角**：本窗口是「无边框无标题栏」（`SetBorderAndTitleBar(false, false)`），
        // Windows 不会替它画圆角 —— 与 1.1.0 那个「应用图标要自己带圆角」是同一类坑。
        // 三条路里选了第三条（都能查证）：
        //   ① `DwmSetWindowAttribute(DWMWA_WINDOW_CORNER_PREFERENCE)`：走的是 DWM 画的**系统边框**，
        //      本窗口没有系统边框，设了不起作用；
        //   ② `SystemBackdrop = new TransparentBackdrop()`：WinAppSDK **1.7 里根本没有这个类型**
        //      （实测：`Microsoft.UI.Xaml.winmd` 里只有 MicaBackdrop / DesktopAcrylicBackdrop），
        //      写了直接编译不过；
        //   ③ **窗口区域**（`CreateRoundRectRgn` + `SetWindowRgn`）：Win32 层的裁剪，任何窗口样式都生效，
        //      圆角之外的像素**不属于窗口**（透出桌面、点也点不到）。见 `ApplyRoundedRegion`。
        Title = "QuizSyncFloat";

        // 无边框无标题栏、不进 Alt+Tab/任务栏、不可缩放（规格：不支持自由拉伸，只有三种外观）。
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        AppWindow.IsShownInSwitchers = false;
        // 顶部第一栏 = 唯一拖动区（整窗可拖会和内容滚动/点选打架，1.1.0 的实测结论）。
        TopBar.PointerPressed += OnTopBarPointerPressed;
        TopBar.PointerMoved += OnTopBarPointerMoved;
        TopBar.PointerReleased += OnTopBarPointerReleased;
        TopBar.PointerCanceled += OnTopBarPointerReleased;
        TopBar.PointerCaptureLost += OnTopBarPointerReleased;

        // 顶栏按钮那一排的宽度/位置变了（换外观、字号、极简模式），拖动区要跟着重算。
        TopActions.SizeChanged += (_, _) => ApplyDragRegion();
        // 元素真正进可视树之后再登记一次 —— 在这之前 `TransformToVisual` 会抛 E_UNEXPECTED。
        TopActions.Loaded += (_, _) => ApplyDragRegion();

        // **拖动的主路径**：把顶栏登记成系统的「标题栏区域」（`NonClientRegionKind.Caption`），
        // 由 Windows 自己执行拖动（真正的模态拖动循环：跟手、能跨屏、松手落位）。
        // 为什么不靠 XAML 的 PointerMoved（那条路也留着，见 ApplyDragRegion 的注释）：
        // 它要求指针事件真的送到本窗口，而本窗口是 `WS_EX_NOACTIVATE` 的浮层 ——
        // 这条链上任何一环断掉都表现为用户那句「拖拽不动」，且从界面上完全看不出来。
        // 探针回读 `WM_NCHITTEST`（tools\probe_float_window.ps1）：顶栏内必须返回
        // `HTCAPTION(2)`、内容区返回 `HTCLIENT(1)` —— 这就是「系统会替你拖」的判据。
        TryEnableNativeCaption();

        // ⚠️ `IsShownInSwitchers = false` **在构造期设无效**：实测（tools/probe_float_window.ps1）
        // 窗口显示后 `WS_EX_TOOLWINDOW` 仍然是 0，也就是照样出现在 Alt+Tab 与任务栏里。
        // 窗口真的显示出来之后再补一次，并直接写扩展样式 —— 与悬浮球（分层窗口）的做法对齐。
        // 这里同时补 `WS_EX_NOACTIVATE`：规格 §6.1 要求悬浮窗**不抢焦点**
        // （用户在别的程序里打字时，点悬浮窗不该把焦点抢走）。
        Activated += (_, _) => EnsureOverlayStyles();

        // **位置落库**：拖动现在主要由 Windows 自己完成（登记了系统标题栏拖动区），
        // 那种拖动**不会**经过我们的 `PointerReleased`（第 168 轮才发现的原缺口：
        // 拖完松手位置不落库，重启又跳回旧位置）。改成听窗口自己的位置变化：
        // 每动一下就重置一个 400 ms 的防抖计时器，停稳后写一次 —— 两条拖动路径都覆盖。
        AppWindow.Changed += OnAppWindowChanged;

        // 主界面改了显示设置（字号等）时跟着刷新。
        App.DisplaySettingsChanged += OnDisplaySettingsChanged;
        App.Staging.Changed += OnStagingChanged;
        Closed += (_, _) =>
        {
            App.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            App.Staging.Changed -= OnStagingChanged;
            AppWindow.Changed -= OnAppWindowChanged;
            _positionSave?.Stop();
            _database?.Dispose();
            _database = null;
        };
    }

    /// <summary>窗口位置/尺寸变了。位置变化（不是我们自己 ApplyGeometry 引起的）就防抖落库。</summary>
    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidPositionChange || _applyingGeometry || !_geometryApplied)
        {
            return;
        }

        _positionSave ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _positionSave.Tick -= OnPositionSaveTick;
        _positionSave.Tick += OnPositionSaveTick;
        _positionSave.Stop();
        _positionSave.Start();
    }

    private void OnPositionSaveTick(object? sender, object e)
    {
        _positionSave?.Stop();

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var factor = hwnd == nint.Zero ? 1.0 : GetDpiForWindow(hwnd) / 96.0;
        if (factor <= 0)
        {
            factor = 1.0;
        }

        var x = AppWindow.Position.X / factor;
        var y = AppWindow.Position.Y / factor;
        if (Math.Abs(x - _settings.FloatWindowX) < 0.5 && Math.Abs(y - _settings.FloatWindowY) < 0.5)
        {
            return; // 没真的动过就别写文件（设置页每改一项都会重建窗口）。
        }

        _settings.FloatWindowX = x;
        _settings.FloatWindowY = y;
        _settings.Save();
        CrashLog.Write($"悬浮窗：位置已记住（逻辑 {x:F0},{y:F0}）");
    }

    // ===================================================================
    // 对外：应用设置 / 刷新 / 位置
    // ===================================================================

    /// <summary>按当前设置重建：尺寸、透明度、置顶、配色、内容、按钮。设置页每改一项都调它。</summary>
    internal void ApplySettings()
    {
        _settings = AppSettings.Load();
        _dark = ResolveDark();
        var palette = Palettes.FirstOrDefault(p => p.Id == _settings.FloatWindowPalette);
        _seed = palette.Id is null ? Palettes[0].Seed : palette.Seed;

        ApplyChrome();
        Reload();
    }

    /// <summary>重读数据并重画（**不碰窗口几何** —— 免得用户正在看的时候窗口跳一下）。</summary>
    /// <param name="resetToLatest">
    /// 识别完成后刷新时传 true：**回到最新一条**（规格 §6.5「识别结束后回到最新」）。
    /// 用户刚识别完想看的就是这次结果，停在之前翻到的位置上会让人以为没更新。
    /// 改设置触发的刷新传 false，免得把用户正在看的那一条顶掉。
    /// </param>
    internal void Reload(bool resetToLatest = false)
    {
        _database ??= QuizDatabase.OpenOrCreate(AppDataDirectory.DatabasePath());

        // 与主界面同一批 store：当前合集还在就用它，否则用全部记录。
        var active = _settings.CurrentCollectionId;
        var hasActive = active is { Length: > 0 }
            && CollectionStore.List(_database).Any(c => c.CollectionId == active);
        _sessions = hasActive
            ? CollectionStore.SessionsOf(_database, active!, 50)
            : SessionRecorder.RecentSessions(_database, 50);

        if (resetToLatest)
        {
            _index = 0;
        }
        else if (_index >= _sessions.Count)
        {
            _index = Math.Max(0, _sessions.Count - 1);
        }

        Render();
    }

    // ===================================================================
    // 窗口外观
    // ===================================================================

    /// <summary>外观 = 尺寸（外观 × 显示比例）+ 置顶 + 配色。</summary>
    private void ApplyChrome()
    {
        // 每次应用设置都把浮层的窗口样式补齐一次（不抢焦点 + 不进 Alt+Tab + 无边框）。
        // 放在这里而不是构造期：构造期窗口还没显示，实测那时设的样式会被后面覆盖掉。
        EnsureOverlayStyles();

        // ⚠️ 第 170 轮：**透明度整项删除**（用户原话「悬浮窗删掉透明度调整功能」）。
        // 原来这里写 `Root.Opacity = Math.Clamp(_settings.FloatWindowOpacity, 0.2, 1.0)`；
        // 窗口现在始终 100% 不透明 —— 半透明的浮层压在白底题目上本来就更难读，
        // 而用户要的是「别让它半透明」而不是「再给一个调不准的旋钮」。
        // 底色画在圆角那一层（`Chrome`）上，不是窗口根节点 —— 根节点必须保持透明，
        // 否则圆角外会是一块方角底色（用户报过「没圆角」）。
        Chrome.Background = new SolidColorBrush(Blend(_seed, _dark ? "#121510" : "#FFFFFF", _dark ? 0.10 : 0.07));
        // 一圈很淡的描边：透出桌面时，纯色块与桌面之间没有边界会显得"糊"。
        Chrome.BorderBrush = new SolidColorBrush(Blend(_seed, _dark ? "#3F3F46" : "#D8DEE9", 0.5));

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = _settings.FloatWindowTopmost;
        }

        ApplyGeometry();
    }

    private static (double Ratio, double BaseWidth) AspectOf(string id) =>
        Aspects.FirstOrDefault(a => a.Id == id) is { BaseWidth: > 0 } aspect
            ? (aspect.Ratio, aspect.BaseWidth)
            : (Aspects[0].Ratio, Aspects[0].BaseWidth);

    /// <summary>
    /// 尺寸与位置。**逻辑像素 → 物理像素要乘 DPI**（`AppWindow` 只认物理像素），
    /// 这条踩过：不换算时 200% 缩放的机器上窗口会小一半。
    /// </summary>
    private void ApplyGeometry()
    {
        var (ratio, baseWidth) = AspectOf(_settings.FloatWindowAspect);
        // 区间是 `AppSettings.MinScale`/`MaxScale`（用户 2026-09-30：「只能在 1.0-2.0 之间」）——
        // 不要再写一份字面量：设置页的滑块与这里必须永远一致。
        var scale = Math.Clamp(_settings.FloatWindowScale, AppSettings.MinScale, AppSettings.MaxScale);
        var width = baseWidth * scale;
        var height = width / ratio;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var dpi = GetDpiForWindow(hwnd);
        var factor = dpi <= 0 ? 1.0 : dpi / 96.0;

        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;

        // 位置：摆过就用记下来的；没摆过 → 屏幕右侧、距右边缘 24（规格 §3.8 的「归位」口径）。
        double logicalX = _settings.FloatWindowX;
        double logicalY = _settings.FloatWindowY;
        if (logicalX < 0 || logicalY < 0)
        {
            logicalX = (area.Width / factor) - width - 24;
            // 规格 §6.1 的 `kFloatWindowDefaultMarginY = 120`：默认位置是「右上角往下 120」，
            // 不是垂直居中（居中是上一版的写法，1.1.0 不是这样）。
            // 仍然夹进工作区，免得窗口在小屏或放大比例下有一半露在屏幕外。
            logicalY = Math.Min(120, Math.Max(0, (area.Height / factor) - height));
        }

        var x = area.X + (int)Math.Round(logicalX * factor);
        var y = area.Y + (int)Math.Round(logicalY * factor);

        _applyingGeometry = true;
        try
        {
            AppWindow.MoveAndResize(new RectInt32(
                x,
                y,
                (int)Math.Round(width * factor),
                (int)Math.Round(height * factor)));
        }
        finally
        {
            _applyingGeometry = false;
            _geometryApplied = true;
        }

        // 尺寸一变，圆角区域要跟着重算（区域是**物理像素**坐标，且只作用于窗口自身坐标系）。
        ApplyRoundedRegion();
        // 顶栏的拖动区同理（顶栏高度是实测的，尺寸/字号变了要重登记）。
        ApplyDragRegion();
    }

    /// <summary>圆角半径（逻辑像素）。与 `FloatWindow.xaml` 里 `Chrome` 的 CornerRadius 保持一致。</summary>
    private const int CornerRadiusLogical = 12;

    /// <summary>顶栏高度（逻辑像素）的兜底值：上 8 + 一行字（CaptionSmall）约 20 + 下 8。
    /// 布局跑过之后用实测的 `TopBar.ActualHeight`。</summary>
    private const double TopBarHeightLogical = 36;

    private InputNonClientPointerSource? _captionSource;

    /// <summary>拖动区登记时量不到按钮位置 → 延迟重试（见 `ApplyDragRegion`）。</summary>
    private DispatcherTimer? _dragRegionRetry;
    private int _dragRegionRetries;

    /// <summary>上一次登记成功的拖动区宽度（0 = 还没登记过）；只在变化时写日志。</summary>
    private int _dragRegionWidth = -1;

    private void OnDragRegionRetryTick(object? sender, object e)
    {
        _dragRegionRetry?.Stop();
        ApplyDragRegion();
    }

    /// <summary>拿「非客户区输入源」：它能声明窗口的哪一块**由系统当成标题栏**。</summary>
    private void TryEnableNativeCaption()
    {
        try
        {
            _captionSource = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
        }
        catch (Exception error)
        {
            // 拿不到不当致命：XAML 那条 PointerMoved 的路还在，只是没有系统拖动那么跟手。
            CrashLog.Write("悬浮窗：拿不到非客户区输入源，拖动退到 XAML 指针事件那条路", error);
        }
    }

    /// <summary>
    /// 把顶部第一栏里**按钮左边那一段**登记为系统标题栏区域 ⇒ 按住它拖动由 **Windows 自己**完成。
    ///
    /// 为什么不用 XAML 的 `PointerMoved` 当主路径（那条也留着，作为拿不到输入源时的退路）：
    /// 它要求指针事件真的到达本窗口。本窗口是 `WS_EX_NOACTIVATE` 的浮层，指针链上
    /// 任何一环不成立（窗口不激活就没有隐式捕获、`CapturePointer` 返回 false……）
    /// 都表现为用户那句「拖拽不动」，而且界面上完全看不出来。系统拖动只依赖
    /// `WM_NCHITTEST` 的返回值，判据能直接回读（`tools\probe_float_window.ps1`）。
    ///
    /// ⚠️ **不能把整条顶栏都登记**：顶栏右边是一排图标按钮（下一条 / 上一条 / 重新识别 /
    /// 默认模式 / 锁定位置）。登记进去之后点它们会变成「拖窗口」，按钮全废
    /// —— 探针里专门有一条断言：按钮那一带必须仍是 `HTCLIENT`。
    /// </summary>
    private void ApplyDragRegion()
    {
        if (_captionSource is null)
        {
            return;
        }

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (hwnd == nint.Zero)
        {
            return;
        }

        var factor = GetDpiForWindow(hwnd) / 96.0;
        var logical = TopBar.ActualHeight > 0 ? TopBar.ActualHeight : TopBarHeightLogical;
        var size = AppWindow.Size;
        var height = Math.Clamp((int)Math.Round(logical * factor), 1, size.Height);

        // 按钮那一排的左边缘（相对窗口内容左上角，逻辑像素）。
        // ⚠️ 布局还没跑（或元素不在可视树里）时 `TransformToVisual` 会抛
        // `COMException 0x8000FFFF`（实测：应用一启动就调必然撞上，用户那份配置下两次都抛）——
        // 这时**不能就此罢手**：拖动区没登记 = 用户又回到「拖不动」。改成延迟重试
        // （400 ms 一次，最多 5 次；`Loaded` / `SizeChanged` 也会再触发）。
        var actionLeft = 0.0;
        try
        {
            actionLeft = TopActions
                .TransformToVisual(null)
                .TransformPoint(new Windows.Foundation.Point(0, 0)).X;
        }
        catch (Exception error)
        {
            if (_dragRegionRetries < 5)
            {
                _dragRegionRetries++;
                _dragRegionRetry ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                _dragRegionRetry.Tick -= OnDragRegionRetryTick;
                _dragRegionRetry.Tick += OnDragRegionRetryTick;
                _dragRegionRetry.Start();
                return;
            }

            CrashLog.Write("悬浮窗：量不到顶栏按钮的位置，拖动区不登记（拖动退到 XAML 指针事件）", error);
            return;
        }

        var width = (int)Math.Round(Math.Max(0, actionLeft - 6) * factor);
        if (width < 1)
        {
            // 窗口窄到按钮已经占满顶栏：那就没有可拖的地方（宁可不能拖，也不能让按钮点不动）。
            try
            {
                _captionSource.SetRegionRects(NonClientRegionKind.Caption, []);
            }
            catch (Exception error)
            {
                CrashLog.Write("悬浮窗：清空拖动区失败", error);
            }

            return;
        }

        try
        {
            _captionSource.SetRegionRects(
                NonClientRegionKind.Caption,
                [new RectInt32(0, 0, width, height)]);

            // 只在**宽度真的变了**时写一行日志：这是「拖动区到底有没有登记上」的唯一现场
            // （界面上看不出来；用户报「拖不动」时先看这一行有没有、宽多少）。
            if (width != _dragRegionWidth)
            {
                _dragRegionWidth = width;
                CrashLog.Write($"悬浮窗：标题栏拖动区已登记（宽 {width} px / 窗口 {size.Width}×{size.Height}，顶栏高 {height} px）");
            }
        }
        catch (Exception error)
        {
            CrashLog.Write("悬浮窗：登记标题栏拖动区失败（拖动退到 XAML 指针事件）", error);
        }
    }

    /// <summary>
    /// 把窗口裁成圆角矩形（用户 2026-09-29 报「没圆角」）。
    ///
    /// `CreateRoundRectRgn` 的后两个参数是**圆角椭圆的长短轴**，所以要给 2×半径；右/下各 +1
    /// 是因为裁剪区域是**左闭右开**的，不加的话最右一列与最下一行会被切掉。
    ///
    /// ⚠️ `SetWindowRgn` 成功之后这块 HRGN **归系统所有**：不能再 `DeleteObject`
    /// （删掉窗口区域就没了），失败时反而必须自己删，否则漏 GDI 对象。
    /// </summary>
    private void ApplyRoundedRegion()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (hwnd == nint.Zero)
        {
            return;
        }

        var factor = GetDpiForWindow(hwnd) / 96.0;
        var radius = Math.Max(1, (int)Math.Round(CornerRadiusLogical * factor));
        var size = AppWindow.Size;

        var region = CreateRoundRectRgn(0, 0, size.Width + 1, size.Height + 1, radius * 2, radius * 2);
        if (region == nint.Zero)
        {
            return;
        }

        if (SetWindowRgn(hwnd, region, true) == 0)
        {
            DeleteObject(region);
        }
    }

    private bool ResolveDark() => _settings.FloatWindowTheme switch
    {
        "dark" => true,
        "light" => false,
        _ => App.IsDarkThemeActive,
    };

    // ===================================================================
    // 内容
    // ===================================================================

    private void Render()
    {
        ContentHost.Children.Clear();
        TopActions.Children.Clear();
        BottomBar.Children.Clear();

        var fg = Pick(_dark ? "#EDEDED" : "#1B1B1B");
        var muted = Pick(_dark ? "#A9B0AA" : "#5C6660");
        var panel = Blend(_seed, _dark ? "#121510" : "#FFFFFF", _dark ? 0.16 : 0.05);
        var stroke = Blend(_seed, _dark ? "#121510" : "#FFFFFF", _dark ? 0.28 : 0.16);

        TopBar.Background = new SolidColorBrush(panel);
        TopBar.BorderBrush = new SolidColorBrush(stroke);
        TopBar.BorderThickness = new Thickness(0, 0, 0, 1);
        SessionLabel.Foreground = new SolidColorBrush(fg);
        SessionLabel.FontSize = QsFontSizes.CaptionSmall;
        SessionLabel.FontFamily = UiFont.Demibold;

        ProgressHint.Background = new SolidColorBrush(panel);
        ProgressHint.BorderBrush = new SolidColorBrush(stroke);
        ProgressHint.BorderThickness = new Thickness(1);
        ProgressText.Foreground = new SolidColorBrush(muted);
        ProgressText.FontSize = QsFontSizes.CaptionSmall;

        if (_sessions.Count == 0)
        {
            SessionLabel.Text = "还没有识别记录";
            ContentHost.Children.Add(new TextBlock
            {
                Text = "识别一张之后，结果会出现在这里。",
                Foreground = new SolidColorBrush(muted),
                FontSize = QsFontSizes.Body,
                TextWrapping = TextWrapping.Wrap,
            });
        }
        else
        {
            var session = _sessions[_index];
            // 序号按**时间顺序**编（规格 §6.2 的 `humanIndex = total - index`）。
            // `_sessions` 是「新的在前」，所以最新一次的序号是 total；原先写成 `_index + 1`
            // 会让最新一次显示成「第 1 次」，翻页时序号还越翻越小。
            // 时间用完整的 `YYYY-MM-DD HH:mm`（`FormatClock` 只有 `MM-dd HH:mm`）。
            SessionLabel.Text =
                $"第 {_sessions.Count - _index}/{_sessions.Count} 次识别 · {UiText.FormatDateMinute(session.CreatedAt)}";

            if (_settings.FloatWindowMinimal)
            {
                RenderMinimal(session);
            }
            else
            {
                RenderDetailed(session);
            }
        }

        BuildTopActions(new SolidColorBrush(fg));
        BuildBottomBar();
        UpdateProgressHint();

        // 按钮是这一帧刚建出来的：拖动区要按**现在**的按钮位置重登记一次
        // （构造期那次量到的是 0，等于没登记）。
        ApplyDragRegion();
    }

    /// <summary>默认模式：一题一段**纯文本**（可拖选复制）。
    /// 选项只留命中的那些；没有答案时保留全部选项 —— 这是 1.1.0 的「极简模式」契约
    /// （附录 A 第 5 条：极简只留命中选项，不渲染解析与材料）。</summary>
    private void RenderMinimal(RecordedSession session)
    {
        var questions = QuestionsOf(session);
        if (questions.Count == 0)
        {
            ContentHost.Children.Add(new TextBlock
            {
                Text = session.Status == "failed"
                    ? UiText.ErrorReason(session.ErrorCode, session.ErrorMessage)
                    : "这次识别没有题目。",
                Foreground = new SolidColorBrush(Pick(_dark ? "#F87171" : "#DC2626")),
                FontSize = QsFontSizes.Body,
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        var fg = new SolidColorBrush(Pick(_dark ? "#EDEDED" : "#1B1B1B"));
        var fontScale = Math.Clamp(_settings.FloatWindowFontScale, 0.6, 2.0);

        foreach (var question in questions)
        {
            var block = new StackPanel { Spacing = 4 };
            var hit = Highlight.Compute(question);
            var accent = new SolidColorBrush(hit.NeedsReview
                ? Pick(_dark ? "#FBBF24" : "#B45309")
                : Pick(_dark ? "#4ADE80" : "#15803D"));

            block.Children.Add(new TextBlock
            {
                Text = question.Stem,
                Foreground = fg,
                FontSize = App.QuestionFontSize * fontScale,
                LineHeight = App.QuestionFontSize * fontScale * 1.6,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            });

            foreach (var option in MinimalOptions(question, hit))
            {
                block.Children.Add(new TextBlock
                {
                    Text = $"{option.Label}. {option.Text}",
                    Foreground = new SolidColorBrush(Pick(_dark ? "#D6DAD7" : "#3C443F")),
                    FontSize = App.QuestionFontSize * fontScale * 0.94,
                    LineHeight = App.QuestionFontSize * fontScale * 0.94 * 1.5,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                });
            }

            if (!string.IsNullOrWhiteSpace(question.AnswerText))
            {
                block.Children.Add(new TextBlock
                {
                    Text = $"答案：{question.AnswerText}",
                    Foreground = accent,
                    FontFamily = UiFont.Demibold,
                    FontSize = App.QuestionFontSize * fontScale,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                });
            }
            else
            {
                block.Children.Add(new TextBlock
                {
                    Text = "未识别出答案",
                    Foreground = new SolidColorBrush(Pick(_dark ? "#A9B0AA" : "#5C6660")),
                    FontSize = App.QuestionFontSize * fontScale * 0.9,
                    TextWrapping = TextWrapping.Wrap,
                });
            }

            ContentHost.Children.Add(block);
        }
    }

    /// <summary>极简模式的选项规则：有命中就只给命中的；没有命中（没答案）就给全部 —— 否则用户什么都看不到。</summary>
    private static IEnumerable<Option> MinimalOptions(Question question, HighlightResult hit)
    {
        if (question.Options.Count == 0)
        {
            return [];
        }

        var matched = question.Options.Where(o => hit.OptionLabels.Contains(o.Label)).ToList();
        return matched.Count > 0 ? matched : question.Options;
    }

    /// <summary>详细解析模式：与主界面**同一套**题目卡片（`QuestionCardView`）。</summary>
    private void RenderDetailed(RecordedSession session)
    {
        var questions = QuestionsOf(session);
        var fontScale = Math.Clamp(_settings.FloatWindowFontScale, 0.6, 2.0);

        if (questions.Count == 0)
        {
            ContentHost.Children.Add(new TextBlock
            {
                Text = session.Status == "failed"
                    ? UiText.ErrorReason(session.ErrorCode, session.ErrorMessage)
                    : "这次识别没有题目。",
                FontSize = QsFontSizes.Body,
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        foreach (var question in questions)
        {
            var card = new QuestionCardView();
            card.Render(
                question,
                fontSize: App.QuestionFontSize * fontScale,
                fontWeight: App.QuestionFontWeight);
            ContentHost.Children.Add(card);
        }
    }

    private IReadOnlyList<Question> QuestionsOf(RecordedSession session) =>
        _database is null ? [] : new AnalysisCache(_database).QuestionsOfSession(session.SessionId);

    // ===================================================================
    // 顶部第一栏的图标按钮（视觉顺序：下一条 · 上一条 · 重新识别 · 默认模式 · 锁定位置）
    //
    // ⚠️ 规格自身对这五个按钮的**排列方向**给了两种读法且互相矛盾：§6.2 的绘图代码是
    // 「从右到左 1=next…5=toggle-lock」（即最右是「下一条」），而 §3.8 的页尾说明按左到右
    // 写成「下一条 · 上一条 · …」。这里按 §3.8 的读法保持现状（「下一条」在最左）——
    // 它是用户看得见的说明书，翻过来只是左右镜像、没有功能收益。
    // ===================================================================

    private void BuildTopActions(Brush foreground)
    {
        // 方向：列表**新的在前**（`_index` 越小越新），所以
        // 「下一条」= 更晚的一条 = `_index--`，「上一条」= 更早的一条 = `_index++`。
        // 上一版两个方向是反的（按「下一条」却在往更早翻），与规格 §6.2 的 next/prev 相反。
        TopActions.Children.Add(IconButton("\uE76C", "下一条", foreground, _index > 0, () =>
        {
            _index--;
            Render();
        }));

        TopActions.Children.Add(IconButton("\uE76B", "上一条", foreground, _index + 1 < _sessions.Count, () =>
        {
            _index++;
            Render();
        }));

        TopActions.Children.Add(IconButton("\uE72C", "重新识别", foreground, _sessions.Count > 0, () =>
        {
            if (_sessions.Count > 0)
            {
                App.RequestReanalyzeFor(_sessions[_index].SessionId);
            }
        }));

        TopActions.Children.Add(IconButton(
            "\uE736",
            _settings.FloatWindowMinimal ? "默认模式（当前：极简）" : "默认模式（当前：详细解析）",
            foreground,
            enabled: true,
            () =>
            {
                _settings.FloatWindowMinimal = !_settings.FloatWindowMinimal;
                _settings.Save();
                Render();
            }));

        TopActions.Children.Add(IconButton(
            _settings.FloatWindowLocked ? "\uE72E" : "\uE785",
            _settings.FloatWindowLocked ? "锁定位置（已锁定：拖不动）" : "锁定位置（未锁定：按住顶部第一栏即可拖动）",
            foreground,
            enabled: true,
            () =>
            {
                _settings.FloatWindowLocked = !_settings.FloatWindowLocked;
                _settings.Save();
                Render();
            }));
    }

    /// <summary>
    /// 顶栏图标按钮。第 170 轮把命中区从 30×30 提到 <see cref="IconButtonSize"/>×同值
    /// （用户「点击不灵敏」）：Glyph 字号不变，只是**可点范围变大**，
    /// 靠近按钮边缘的点击不再落到旁边。
    /// </summary>
    private const int IconButtonSize = 34;

    private static Button IconButton(string glyph, string tooltip, Brush foreground, bool enabled, Action onClick)
    {
        var button = new Button
        {
            Width = IconButtonSize,
            Height = IconButtonSize,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            IsEnabled = enabled,
            Content = new FontIcon
            {
                Glyph = glyph,
                FontSize = QsFontSizes.Subtitle,
                Foreground = foreground,
            },
        };
        ToolTipService.SetToolTip(button, tooltip);
        button.Click += (_, _) => onClick();
        return button;
    }

    // ===================================================================
    // 底部按钮（规格：识别一张 / 多页识别 [多页时换成三个] [默认模式多一个复制]）
    // ===================================================================

    private void BuildBottomBar()
    {
        var multipage = App.Staging.Count > 0;
        var hasSession = _sessions.Count > 0;

        if (multipage)
        {
            BottomBar.Children.Add(TextButton("结束并上传", "\uE898", primary: true, enabled: true,
                () => App.RequestMultipage()));
            BottomBar.Children.Add(TextButton("继续添加页", "\uE710", primary: false,
                enabled: App.Staging.Count < MultipageStaging.HardMaxPages,
                () => App.RequestMultipage()));
            BottomBar.Children.Add(TextButton("取消多页", "\uE711", primary: false, enabled: true,
                () => App.ClearStaging()));
            return;
        }

        BottomBar.Children.Add(TextButton("识别一张", "\uE722", primary: true, enabled: true,
            () => App.RequestCapture()));
        BottomBar.Children.Add(TextButton("多页识别", "\uE81E", primary: false, enabled: true,
            () => App.RequestMultipage()));

        if (_settings.FloatWindowMinimal)
        {
            BottomBar.Children.Add(TextButton("复制识别内容", "\uE8C8", primary: false, enabled: hasSession, () =>
            {
                if (_sessions.Count > 0)
                {
                    App.RequestCopyFor(_sessions[_index].SessionId);
                }
            }));
        }
    }

    private static Button TextButton(string text, string glyph, bool primary, bool enabled, Action onClick)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(new FontIcon { Glyph = glyph, FontSize = QsFontSizes.Body });
        content.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = QsFontSizes.Caption,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var button = new Button
        {
            Content = content,
            Padding = new Thickness(10, 6, 10, 6),
            IsEnabled = enabled,
        };
        if (!primary)
        {
            button.Background = new SolidColorBrush(Colors.Transparent);
            button.BorderThickness = new Thickness(1);
        }

        button.Click += (_, _) => onClick();
        return button;
    }

    // ===================================================================
    // 拖动（只认顶部第一栏；锁定后不响应）
    // ===================================================================

    private void OnTopBarPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_settings.FloatWindowLocked)
        {
            return;
        }

        var point = e.GetCurrentPoint(null);
        _dragPointerStart = new PointInt32((int)point.Position.X, (int)point.Position.Y);
        _dragOriginX = _settings.FloatWindowX;
        _dragOriginY = _settings.FloatWindowY;
        if (_dragOriginX < 0 || _dragOriginY < 0)
        {
            // 第一次拖动：把当前实际位置当成起点（否则会从 (-1,-1) 跳一下）。
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var factor = GetDpiForWindow(hwnd) / 96.0;
            _dragOriginX = AppWindow.Position.X / factor;
            _dragOriginY = AppWindow.Position.Y / factor;
        }

        var captured = TopBar.CapturePointer(e.Pointer);
        CrashLog.Write($"悬浮窗拖动探针：按下 ({point.Position.X:F0},{point.Position.Y:F0}) capturePointer={captured}");

        // ⚠️ **`_dragging` 不能等于 `CapturePointer` 的返回值**（用户 2026-09-29 报「拖拽不动」）。
        // 本窗口是 `WS_EX_NOACTIVATE` 的浮层：点它**不激活**窗口，而 XAML 的指针捕获要求
        // 窗口是前台窗口 —— 捕获会失败并返回 false，于是 `_dragging` 恒为 false、
        // `PointerMoved` 第一句就 return：按着顶栏拖，窗口一动不动。
        // 修法两层：① 无条件进入拖动状态；② 用 Win32 的 `SetCapture` 兜住「指针移出窗口」
        // 那一段（XAML 捕获失败时，鼠标消息会在指针离开本窗口后送给别的窗口）。
        _dragging = true;
        SetCapture(WinRT.Interop.WindowNative.GetWindowHandle(this));
        e.Handled = true;
    }

    private void OnTopBarPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        var point = e.GetCurrentPoint(null);
        CrashLog.Write($"悬浮窗拖动探针：移动 ({point.Position.X:F0},{point.Position.Y:F0})");
        var dx = point.Position.X - _dragPointerStart.X;
        var dy = point.Position.Y - _dragPointerStart.Y;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var factor = GetDpiForWindow(hwnd) / 96.0;

        AppWindow.Move(new PointInt32(
            (int)Math.Round((_dragOriginX + dx) * factor),
            (int)Math.Round((_dragOriginY + dy) * factor)));
    }

    private void OnTopBarPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        CrashLog.Write("悬浮窗拖动探针：松手");
        TopBar.ReleasePointerCapture(e.Pointer);
        // Win32 那层捕获也要显式放掉（上面 SetCapture 的对应动作）。
        ReleaseCapture();

        // 松手才落库：拖动过程中每一帧都写文件既没必要，也会让 setting.json 反复被重写。
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var factor = GetDpiForWindow(hwnd) / 96.0;
        _settings.FloatWindowX = AppWindow.Position.X / factor;
        _settings.FloatWindowY = AppWindow.Position.Y / factor;
        _settings.Save();
    }

    // ===================================================================
    // 识别进行中的提示（贴内容区下部）
    // ===================================================================

    private void OnDisplaySettingsChanged() => Render();

    private void OnStagingChanged() => Render();

    internal void UpdateProgressHint()
    {
        var analyzing = App.IsAnalyzing;
        ProgressHint.Visibility = analyzing ? Visibility.Visible : Visibility.Collapsed;
        HintRing.IsActive = analyzing;
        ProgressText.Text = analyzing ? "电脑正在识别中…" : string.Empty;
    }

    // ===================================================================
    // 颜色小工具
    // ===================================================================

    private static Color Pick(string hex)
    {
        var value = hex.TrimStart('#');
        return Color.FromArgb(
            255,
            Convert.ToByte(value[..2], 16),
            Convert.ToByte(value.Substring(2, 2), 16),
            Convert.ToByte(value.Substring(4, 2), 16));
    }

    /// <summary>`Color.alphaBlend(seed @ alpha, base)`（Flutter 侧同一套算法，两端观感才对得上）。</summary>
    private static Color Blend(Color seed, string baseHex, double alpha)
    {
        var basis = Pick(baseHex);
        byte Mix(byte a, byte b) => (byte)Math.Round((a * alpha) + (b * (1 - alpha)));
        return Color.FromArgb(255, Mix(seed.R, basis.R), Mix(seed.G, basis.G), Mix(seed.B, basis.B));
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hWnd);

    /// <summary>Win32 鼠标捕获：指针移出窗口后仍然把鼠标消息送给我们（拖动必需）。</summary>
    [DllImport("user32.dll")]
    private static extern nint SetCapture(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    /// <summary>窗口裁剪区域（圆角）。坐标是**窗口自身的物理像素**。</summary>
    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(nint hWnd, nint hRgn, bool redraw);

    [DllImport("gdi32.dll")]
    private static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint hObject);

    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080L;

    /// <summary>点了也不激活（规格 §6.1）。悬浮窗是「看一眼」的浮层，抢焦点会打断用户手上正在做的事。</summary>
    private const long WsExNoActivate = 0x08000000L;

    /// <summary>
    /// 补齐浮层的窗口样式：**不进 Alt+Tab / 任务栏**（`WS_EX_TOOLWINDOW`）+ **不抢焦点**
    /// （`WS_EX_NOACTIVATE`）+ **没有非客户区边框**（见 <see cref="StripNonClientFrame"/>）。
    ///
    /// 为什么要在窗口显示之后再写一次：构造期设 `IsShownInSwitchers = false` 是无效的
    /// （`tools/probe_float_window.ps1` 实测：窗口显示后 `WS_EX_TOOLWINDOW` 仍为 0），
    /// 所以构造之后每次应用设置都补一遍，`Activated` 时再补一遍。
    /// </summary>
    private void EnsureOverlayStyles()
    {
        AppWindow.IsShownInSwitchers = false;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var exStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        var wanted = exStyle | WsExToolWindow | WsExNoActivate;
        if (wanted != exStyle)
        {
            SetWindowLongPtr(hwnd, GwlExStyle, (nint)wanted);
        }

        StripNonClientFrame();
    }

    // 窗口样式位（`GWL_STYLE`）。
    private const int GwlStyle = -16;
    private const long WsPopup = unchecked((long)0x80000000);
    private const long WsCaption = 0x00C00000L;
    private const long WsBorder = 0x00800000L;
    private const long WsDlgFrame = 0x00400000L;
    private const long WsThickFrame = 0x00040000L;
    private const long WsSysMenu = 0x00080000L;

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpFrameChanged = 0x0020;

    /// <summary>
    /// 去掉窗口的**非客户区边框**（用户 2026-09-30 报「悬浮窗外围渲染有黑线框框」「点击不灵敏」）。
    ///
    /// 实测（`tools/hit_map.ps1`，修复前）：`presenter.SetBorderAndTitleBar(false, false)` 之后
    /// 窗口仍带着 `WS_DLGFRAME`（style `0x14480000`），于是**客户区不是整个窗口**：
    ///
    /// ```text
    /// window=684x1520  client=678x1514  inset=(3,3,3,3)
    /// WM_NCHITTEST：四条边的最外 3 像素一律 HTBORDER(18)
    /// ```
    ///
    /// 两个后果，正好是用户报的那两条：
    /// ① 那 3 像素是**系统画的边框**（左/上是亮边 227、右/下是暗边 105——用户看到的一圈深色线），
    ///    XAML 的底色与圆角都画不到那里；
    /// ② 它**不吃鼠标**（`HTBORDER` = 系统什么都不做），窗口四周因此有一圈点不动的死区。
    ///
    /// 修法是把这个边框整个去掉、改成 `WS_POPUP`，再 `SWP_FRAMECHANGED` 让系统重算客户区。
    /// 去掉了 `WS_SYSMENU`，所以 Alt+Space 的系统菜单也没了（浮层本来就不该有）。
    /// **`WS_EX_NOACTIVATE` 不受影响**（那是扩展样式，另在 <see cref="EnsureOverlayStyles"/> 里维护）。
    /// </summary>
    private void StripNonClientFrame()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (hwnd == nint.Zero)
        {
            return;
        }

        var style = GetWindowLongPtr(hwnd, GwlStyle).ToInt64();
        var wanted = (style & ~(WsCaption | WsBorder | WsDlgFrame | WsThickFrame | WsSysMenu)) | WsPopup;
        if (wanted == style)
        {
            return;
        }

        SetWindowLongPtr(hwnd, GwlStyle, (nint)wanted);
        SetWindowPos(
            hwnd, nint.Zero, 0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoZOrder | SwpFrameChanged);

        // 只在真的改过时写一行：这是「一圈黑线到底还在不在」的唯一现场。
        CrashLog.Write($"悬浮窗：已去掉非客户区边框（style 0x{style:X8} → 0x{wanted:X8}）");
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hWnd, int index, nint value);
}
