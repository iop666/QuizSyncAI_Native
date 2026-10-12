using System;
using System.IO;
using Microsoft.UI.Xaml;
using QuizSync.Provider;

namespace QuizSync.App;

/// <summary>Phase 6 外壳的入口。</summary>
public partial class App : Application, IDisposable
{
    /// <summary>全局热键的槽位号（每个槽位固定一个 id，见 `HotkeyService` 的说明）。</summary>
    private const int CaptureSlot = 1;

    /// <summary>多页模式的槽位号（F9）。两个用途各自一个槽位，与 1.1.0 一致。</summary>
    private const int MultipageSlot = 2;

    /// <summary>F9：多页模式。与 1.1.0 一致的键位（`AGENTS.md` M46：F8 截屏识别 / F9 多页）。</summary>
    private const uint VkF9 = 0x78;

    /// <summary>F8：截屏识别。与 1.1.0 一致的键位（`AGENTS.md` M46：F8 截屏识别 / F9 多页）。</summary>
    private const uint VkF8 = 0x77;

    private Window? _window;
    private HotkeyService? _hotkeys;
    private FloatingBall? _ball;
    private Pages.FloatWindow? _floatWindow;
    private TrayController? _tray;

    /// <summary>定时补推待上传改动的脉动（见 <see cref="StartSyncPump"/>）。</summary>
    private static DispatcherTimer? _syncPump;

    /// <summary>剪贴板监听（「识别设置 → 剪贴板监听」）。null = 没接上。</summary>
    private ClipboardWatchHook? _clipboardWatch;
    /// <summary>上一次已经送去识别的图片摘要（同一张图不反复触发）。</summary>
    private string? _lastClipboardHash;

    /// <summary>正在读剪贴板 / 派发分析：剪贴板更新可能连发几次，重入会重复建任务。</summary>
    private bool _clipboardBusy;

    /// <summary>主窗口实例。托盘回调、设置页等处需要它调 <c>Activate()</c>。</summary>
    internal static Window? MainWindowInstance => ((App)Current)._window;

    /// <summary>
    /// 当前**生效**的截屏热键文案（取不到就是 null）。顶栏的热键药丸、空态提示、
    /// 托盘提示都读它 —— 1.1.0 里这三处也都读同一个「生效热键」，
    /// 各写一份必然出现「一处显示 F8、另一处显示无可用热键」。
    /// </summary>
    internal static string? CaptureHotkeyLabel { get; private set; }

    /// <summary>题库正文字号（「显示设置」里可调，1.1.0 默认 16、范围 12–32）。</summary>
    internal static double QuestionFontSize { get; private set; } = 16;

    /// <summary>题库正文字重（档位 300–700，实际默认 500 —— 见 <see cref="AppSettings"/> 的说明）。</summary>
    internal static int QuestionFontWeight { get; private set; } = 500;

    /// <summary>默认的截屏热键（F8，1.1.0 的默认键）。</summary>
    internal static HotkeyChord DefaultCaptureChord { get; } = new("F8", VkF8, HotkeyModifiers.None);

    /// <summary>默认的多页模式热键（F9）。</summary>
    internal static HotkeyChord DefaultMultipageChord { get; } = new("F9", VkF9, HotkeyModifiers.None);

    /// <summary>生效的多页热键文案（暂存条与托盘都读它，不各写一份）。</summary>
    internal static string? MultipageHotkeyLabel { get; private set; }

    internal static bool MultipageHotkeyOk { get; private set; }

    internal static string MultipageHotkeyStatus { get; private set; } = "未注册";

    /// <summary>多页暂存区（F9 加页 / F8 结束上传 / 悬浮球长按）。</summary>
    internal static MultipageStaging Staging { get; } = new();

    /// <summary>
    /// 当前是不是深色（**实际生效**的那一套）。
    /// 「跟随软件设置」= `system` 时由操作系统的应用主题决定，不能用设置字符串直接判 ——
    /// 悬浮窗的「跟随软件设置」就靠它，否则系统深色 + 应用跟随会画成浅色。
    /// </summary>
    internal static bool IsDarkThemeActive => AppSettings.Load().Theme switch
    {
        "dark" => true,
        "light" => false,
        _ => new Windows.UI.ViewManagement.UISettings()
            .GetColorValue(Windows.UI.ViewManagement.UIColorType.Background).R < 128,
    };

    /// <summary>
    /// 主界面是不是正在识别。悬浮窗的「识别进行中」提示读它 ——
    /// 那件事只发生在主界面（它持 <c>_busy</c> 与整条识别流程），悬浮窗只是显示方。
    /// </summary>
    internal static bool IsAnalyzing { get; private set; }

    /// <summary>识别开始 / 结束时由主界面调（<see cref="IsAnalyzing"/> 的唯一写入口）。</summary>
    internal static void SetAnalyzing(bool analyzing)
    {
        if (IsAnalyzing == analyzing)
        {
            return;
        }

        IsAnalyzing = analyzing;
        ((App)Current)._floatWindow?.UpdateProgressHint();
    }

    /// <summary>「重新识别 / 复制识别内容」两条请求：悬浮窗发，主界面执行。</summary>
    internal static event Action<string>? ReanalyzeRequested;

    internal static event Action<string>? CopyRequested;

    internal static void RequestReanalyzeFor(string sessionId) => ReanalyzeRequested?.Invoke(sessionId);

    internal static void RequestCopyFor(string sessionId) => CopyRequested?.Invoke(sessionId);

    /// <summary>
    /// 截屏热键**真实的**注册结果（热键设置页读它）。这里不允许「假装成功」：
    /// 失败时 <see cref="CaptureHotkeyOk"/> 为 false、<see cref="CaptureHotkeyStatus"/> 里带原因。
    /// </summary>
    internal static bool CaptureHotkeyOk { get; private set; }

    internal static string CaptureHotkeyStatus { get; private set; } = "未注册";

    /// <summary>
    /// 「显示设置」改完了（字号 / 字重 / 主题）。主界面订阅它**立刻重画题目卡片** ——
    /// 1.1.0 的用户反馈里「调了没反应」是最早被报上来的那类问题，这里不留「重启后生效」。
    /// </summary>
    internal static event Action? DisplaySettingsChanged;

    /// <summary>当前 App 实例（设置页要它应用字号 / 主题 / 悬浮球 / 热键）。</summary>
    internal static App? Instance => Current as App;

    public App()
    {
        InitializeComponent();

        // 全局兜底：任何漏到 XAML 层的异常都先落一行日志再崩 —— 否则事件日志里只有
        // 「Microsoft.UI.Xaml.dll + 偏移量」，等于没有现场（见 CrashLog 的注释）。
        UnhandledException += (_, args) =>
        {
            CrashLog.Write("未处理异常", args.Exception);
        };
        var ui = AppSettings.Load();
        QuestionFontSize = ui.FontSize;
        QuestionFontWeight = ui.FontWeight;
        ApplyTheme(ui.Theme);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 上一次运行里「恢复备份」因为库被占用而暂存过 → **在打开数据库之前**换入。
        // 顺序不能改：一旦有页面先打开库，这个换入就只能等下一次启动了。
        try
        {
            if (Data.BackupArchive.HasPendingRestore(Data.AppDataDirectory.DatabasePath()))
            {
                Data.BackupArchive.ApplyPendingRestore(Data.AppDataDirectory.DatabasePath());
            }
        }
        catch (IOException)
        {
            // 换不掉就留着，下次启动再试（**绝不删用户数据**）。
        }

        var window = new MainWindow();
        _window = window;
        // 窗口关掉就把热键注销掉：`RegisterHotKey` 是**系统级**的，不显式注销会留在系统里。
        window.Closed += (_, _) => Dispose();
        window.Activate();

        RegisterGlobalHotkey();
        ShowFloatingBall();
        // 悬浮窗：**开机时按设置决定是否显示**（默认关闭 —— 全新安装不会自己冒出来）。
        ApplyFloatWindowSettings();
        ShowTray();
        AttachClipboardWatch(window);
        // 脉动在**这里**建（OnLaunched = UI 线程）：`DispatcherTimer` 是 XAML 对象，
        // 在别的线程上 new 会以 `RPC_E_WRONG_THREAD`（0x8001010E）把进程打挂 ——
        // 第 168 轮实测：放在 `EnsureConnectionIfEnabled` 的 `ConfigureAwait(false)` 之后，
        // 启动 1 秒后 APPCRASH（CoreMessagingXP.dll / 0xc000027b）。
        StartSyncPump();
        EnsureConnectionIfEnabled();
    }

    /// <summary>
    /// 「开启连接设备」开着时，**启动就把主机拉起来**（SPEC §2.2：「打开时才启动服务」）。
    ///
    /// 第 167 轮起这一步默认**在本应用进程内**起宿主（`HostLauncher` 的默认口径），
    /// 所以用户只需要这一个应用：装一次、更新一次，不会出现「应用升了、主机还是旧的」这种版本错位。
    /// 若本机已经在跑独立的主机进程（`setup --autostart` 或用户自己开的），探活会命中它，这里不会再起第二个。
    ///
    /// 静默：不抢焦点、不改标题 —— 启动路径上弹一句提示是打扰；结果写进 `logs\crash.log`。
    /// 用户想看到明确反馈时，设置页那个总开关与「重启连接服务」都逐种情况报结果。
    /// 关着时**什么都不做**（默认就是关着：全新安装不会自己监听端口）。
    ///
    /// **第 169 轮起它把识别服务也一起管**（用户要求「识别服务要在应用开启后自动开启」）：
    /// 主机起来之后紧接着起 Provider —— 少了它，手机截屏只会停在「等待电脑识别」，
    /// 而用户看不到任何提示。没配 AI 时不起（那种进程会立刻以退出码 2 退出），只写一行日志说明。
    /// </summary>
    private static async void EnsureConnectionIfEnabled()
    {
        if (!AppSettings.Load().ConnectEnabled)
        {
            return;
        }

        try
        {
            var outcome = await QuizSync.ServerBridge.HostLauncher.EnsureRunningAsync(
                serverDataDirectory: QuizSync.Data.HostDataDirectory.ForStarting());
            CrashLog.Write(outcome.Status switch
            {
                QuizSync.ServerBridge.HostLaunchStatus.Embedded =>
                    $"启动时主机：内嵌在本应用进程内 {outcome.BaseUrl}（{outcome.ElapsedMs} ms）",
                _ => $"启动时主机：{outcome.Status} {outcome.BaseUrl ?? outcome.Detail}",
            });

            if (outcome.BaseUrl is { Length: > 0 } baseUrl)
            {
                CrashLog.Write("启动时识别服务：" + await StartProviderAsync(baseUrl).ConfigureAwait(true));
            }
        }
        catch (Exception error)
        {
            CrashLog.Write("启动时拉起主机失败", error);
        }

        // 主机起来之后立刻补推一次：本机攒着的改动不该等到「下一次识别成功」才走。
        // ⚠️ `ConfigureAwait(true)`（不是 false）：这条链后面还有 XAML 的活，
        // 回到 UI 线程再继续（`ConfigureAwait(false)` 会把续体丢到线程池上）。
        await FlushPendingOpsAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// 起识别服务（Provider），返回一句给人看的结果。**没配 AI 时不起** ——
    /// 那种 Provider 会以退出码 2 立刻退出（协议里就是定的），起了只会留下一条看不懂的失败。
    /// </summary>
    private static async Task<string> StartProviderAsync(string baseUrl)
    {
        if (!QuizSync.Provider.AiSettings.IsUsable(QuizSync.Provider.AiSettings.Load()))
        {
            return "未启动（还没配置 AI：设置 → API 配置）";
        }

        var outcome = await QuizSync.ServerBridge.ProviderLauncher.EnsureOnlineAsync(
            baseUrl, serverDataDirectory: QuizSync.Data.HostDataDirectory.ForStarting()).ConfigureAwait(true);

        return outcome.Status switch
        {
            QuizSync.ServerBridge.ProviderLaunchStatus.AlreadyOnline => "本来就在线",
            QuizSync.ServerBridge.ProviderLaunchStatus.Started =>
                $"已启动并在线（{outcome.ElapsedMs} ms，进程 {outcome.ProcessId}）",
            _ => outcome.Detail,
        };
    }

    /// <summary>
    /// 「开启连接设备」与「重启连接服务」共用的一条路径：**主机 + 识别服务一起管**。
    ///
    /// 两条纪律：
    /// * `restart: true` 时**只关自己起的那些**（内嵌主机 + 本应用拉起的 Provider）——
    ///   独立主机进程与别人起的 Provider 一律不动，如实说明；
    /// * 返回一句人话，调用方负责显示（设置页 `App.Note`，启动路径只写日志）。
    /// </summary>
    internal static async Task<string> StartConnectionAsync(bool restart)
    {
        var notes = new List<string>();

        if (restart)
        {
            if (QuizSync.ServerBridge.ProviderLauncher.TryStopStarted())
            {
                notes.Add("已停掉本应用起的识别服务");
            }

            // 只有**内嵌**的那一份归我们管：独立主机进程（`setup --autostart` / 用户手开）不动。
            if (QuizSync.ServerBridge.HostLauncher.Embedded is not null)
            {
                await QuizSync.ServerBridge.HostLauncher.ReleaseEmbeddedAsync().ConfigureAwait(true);
                notes.Add("已停掉内嵌主机");
            }
            else
            {
                notes.Add("主机不是内嵌的（独立进程），不动它");
            }
        }

        var host = await QuizSync.ServerBridge.HostLauncher.EnsureRunningAsync(
            serverDataDirectory: QuizSync.Data.HostDataDirectory.ForStarting()).ConfigureAwait(true);
        if (host.BaseUrl is null)
        {
            return string.Join("；", notes.Append($"主机没起来：{host.Detail}"));
        }

        notes.Add(host.Status switch
        {
            QuizSync.ServerBridge.HostLaunchStatus.Embedded => $"主机已就绪（内嵌 {host.BaseUrl}）",
            QuizSync.ServerBridge.HostLaunchStatus.AlreadyRunning => $"主机本来就在运行（{host.BaseUrl}）",
            _ => $"主机已就绪（{host.BaseUrl}）",
        });

        notes.Add("识别服务" + await StartProviderAsync(host.BaseUrl).ConfigureAwait(true));
        return string.Join("；", notes);
    }

    /// <summary>
    /// 定时补推：每 30 秒看一眼「还有没有没推上去的 op」，有就推。
    ///
    /// 为什么必须有它（第 168 轮，用户报「安卓端也不同步」）：推送原来只有**一个**时机 ——
    /// 本机识别成功之后顺路推（`HomePage` 的 `TryPushAsync`）。于是「删了记录 / 改了答案 /
    /// 建了合集」这些 op 要等到下一次识别成功才可能上去；而识别一旦一直失败，
    /// 它们就永远躺在本地（实测：水位文件停在 lamport 35、库里已经有 38，中间那 3 条
    /// 包含 9/27 13:33 那次真实识别，手机端两天都没看到）。
    ///
    /// 三条纪律：
    /// * **不打扰**：只写 `logs\crash.log`（`App.Note` 会把主窗口拉到前台，后台任务不许这么干）；
    /// * **同一句话只说一次**：状态没变就不重复刷日志（照 CLI 的「按内容指纹去重」）；
    /// * **主机没起来就不当错误**：那种情况下一个字都不写（用户没开「连接设备」是本意，不是故障）。
    /// </summary>
    private static void StartSyncPump()
    {
        _syncPump ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _syncPump.Tick -= OnSyncPumpTick;
        _syncPump.Tick += OnSyncPumpTick;
        _syncPump.Start();
    }

    private static void OnSyncPumpTick(object? sender, object e)
    {
        _ = FlushPendingOpsAsync();
    }

    /// <summary>补推一次；返回推了几条（0 = 没有待推的或主机不在）。</summary>
    private static async Task<int> FlushPendingOpsAsync()
    {
        try
        {
            // 每次都用**新的**连接对象：这里是长跑进程里的定时任务，复用实例容易叠上过期的 DNS/连接。
            var probe = await new QuizSync.ServerBridge.HostDiscovery().ProbeAsync().ConfigureAwait(false);
            if (probe is null)
            {
                return 0; // 主机没起来：不是错误，什么都不说。
            }

            var directory = QuizSync.Data.HostDataDirectory.Find();
            var controlToken = directory is null
                ? null
                : QuizSync.ServerBridge.HostControl.FindControlToken(directory);
            if (controlToken is null)
            {
                return 0;
            }

            using var database = QuizSync.Data.QuizDatabase.OpenOrCreate(
                QuizSync.Data.AppDataDirectory.DatabasePath());
            var pending = SyncUploader.PendingCount(database);
            if (pending == 0)
            {
                return 0;
            }

            var notice = await SyncUploader.PushPendingAsync(database, probe.BaseUrl, controlToken)
                .ConfigureAwait(false);
            ReportSyncState($"补推待上传改动：{pending} 条 → {notice}");
            return pending;
        }
        catch (Exception error)
        {
            ReportSyncState($"补推待上传改动失败：{error.Message}");
            return 0;
        }
    }

    /// <summary>同一句话只说一次（内容没变就不写日志）。</summary>
    private static void ReportSyncState(string line)
    {
        if (string.Equals(line, _lastSyncState, StringComparison.Ordinal))
        {
            return;
        }

        _lastSyncState = line;
        CrashLog.Write(line);
    }

    private static string? _lastSyncState;

    /// <summary>
    /// 上一次补推的结果原文（null = 还没推过）。给设置页「当前连接状态」用（第 170 轮）：
    /// 补推的失败原来只进 `logs\crash.log`，界面上一个字都没有，用户只能看到
    /// 「识别完了但手机上没有」。
    /// </summary>
    internal static string? LastSyncState => _lastSyncState;

    /// <summary>
    /// 接上剪贴板监听（设置里的「剪贴板监听」，1.1.0 默认开）。
    ///
    /// 语义照 1.1.0：**只认新出现的图片**，而且是**静默**的 —— 复制一张图就出结果，
    /// 但**不把主窗口抢到前台**（`App.Note` 会 `Activate()`，所以这条链上一次都不能用它；
    /// 1.1.0 的 `capture_coordinator` 对剪贴板路径同样注明「静默：不打扰用户」）。
    ///
    /// 监听器**常驻注册**、开关在事件里判 —— 不拿「注销/注册」表达设置状态：
    /// 那种写法有一个前提（每次改设置都能走到），1.1.0 的热键就是这么锁死的（`AGENTS.md` M30/M31）。
    /// </summary>
    private void AttachClipboardWatch(MainWindow window)
    {
        try
        {
            _clipboardWatch = new ClipboardWatchHook(
                WinRT.Interop.WindowNative.GetWindowHandle(window),
                OnClipboardUpdate);
            // 这两条按机器分叉、界面上看不出来，所以写一行日志（与外壳分支同一条规矩）。
            CrashLog.Write($"剪贴板监听：{(_clipboardWatch.Listening ? "已注册" : "注册失败（AddClipboardFormatListener 返回 false）")}");
        }
        catch (Exception error)
        {
            CrashLog.Write("剪贴板监听没接上", error);
        }
    }

    /// <summary>
    /// 剪贴板更新（`WM_CLIPBOARDUPDATE`）。本函数在**窗口过程里**被调用 —— 也就是 UI 线程。
    /// 文本复制（本程序自己「复制本题答案」也会）走到这里时读不出图，直接静默返回。
    /// </summary>
    private async void OnClipboardUpdate()
    {
        if (_clipboardBusy || _clipboardWatch is null || !AppSettings.Load().ClipboardWatch)
        {
            return;
        }

        _clipboardBusy = true;
        try
        {
            var image = await ClipboardImage.TryReadAsync();
            if (image is not { } shot)
            {
                return;
            }

            if (!ClipboardWatchPolicy.ShouldAnalyze(enabled: true, shot.Hash, _lastClipboardHash))
            {
                return;
            }

            _lastClipboardHash = shot.Hash;
            CrashLog.Write($"剪贴板监听：识别新图片 {shot.Hash[..Math.Min(19, shot.Hash.Length)]}…（{shot.Jpeg.Length} 字节）");
            HandOffClipboardImage(shot.Jpeg, shot.Hash);
        }
        catch (Exception error)
        {
            // 剪贴板被别的进程占着（CLIPBRD_E_CANT_OPEN）是**常态**，不是故障：记一行就好，不弹提示。
            CrashLog.Write($"剪贴板监听跳过一次：{error.Message}");
        }
        finally
        {
            _clipboardBusy = false;
        }
    }

    /// <summary>
    /// 注册全局热键。内核（`HotkeyService`）早就写好了 —— 它按 M45f 的结论实现：
    /// **后台线程一次 `await` 都不做**（同步 `PeekMessageW` + `Sleep`），并且每 2 秒自检补回注册。
    /// 原来那个「长按/线程漂移导致热键悄悄失效」的坑都在内核里处理掉了，这里只负责接线。
    ///
    /// 注册失败**不静默**：把原因写到窗口标题后缀上（这一版还没有日志面板，标题是最容易看见的地方）。
    /// </summary>
    private void RegisterGlobalHotkey()
    {
        try
        {
            _hotkeys = new HotkeyService();
            _hotkeys.Triggered += slot =>
            {
                // 热键回调来自后台线程，必须切回 UI 线程再动 XAML。
                // 两个槽位各自走一个入口，而「悬浮球 / 托盘 / 热键」三者的同名动作走**同一个**入口 ——
                // 一分叉就必然有一条是错的（1.1.0 的历史里就是这么错过的）。
                var action = slot switch
                {
                    CaptureSlot => (Action)TriggerCapture,
                    MultipageSlot => TriggerMultipage,
                    _ => null,
                };

                if (action is not null)
                {
                    _window?.DispatcherQueue.TryEnqueue(() => action());
                }
            };

            // 启动时用**磁盘上的**组合键：用户自定义过就用他的，没自定义过就是默认 F8 / F9。
            ApplyCaptureHotkey(AppSettings.Load().CaptureHotkey);
            ApplyMultipageHotkey(AppSettings.Load().MultipageHotkey);
        }
        catch (Exception error)
        {
            CaptureHotkeyOk = false;
            CaptureHotkeyLabel = null;
            CaptureHotkeyStatus = $"热键服务没起来：{error.Message}";
            Note($"热键注册失败：{error.Message}");
            RefreshTray();
        }
    }

    /// <summary>
    /// 重新注册截屏热键（设置页的「自定义」与「恢复默认」都走这里）。传 null = 回到默认键 F8。
    ///
    /// 必须先 `Unregister` 再 `Register`：`RegisterHotKey` 是**线程级**资源，
    /// 同一个槽位换组合键时不先注销，新键会注册不上（老键还留在系统里，表现为「改了但按新键没反应」）。
    /// 返回值是给界面显示的**真实**状态文案。
    /// </summary>
    internal static string ApplyCaptureHotkey(HotkeyChord? customChord)
    {
        if (Instance is not { } app)
        {
            return "应用没起来，热键没有改";
        }

        var chord = customChord ?? DefaultCaptureChord;
        if (app._hotkeys is null)
        {
            CaptureHotkeyOk = false;
            CaptureHotkeyLabel = null;
            CaptureHotkeyStatus = "热键服务没起来（见窗口标题）";
            return CaptureHotkeyStatus;
        }

        app._hotkeys.Unregister(CaptureSlot);
        var result = app._hotkeys.Register(CaptureSlot, chord);
        switch (result)
        {
            case HotkeyRegisterResult.Ok:
                CaptureHotkeyOk = true;
                CaptureHotkeyLabel = chord.Label;
                CaptureHotkeyStatus = $"已注册：{chord.Label}";
                SetWindowTitle(AppWindowScope.Title);

                break;
            case HotkeyRegisterResult.Rejected:
                CaptureHotkeyOk = false;
                CaptureHotkeyLabel = null;
                CaptureHotkeyStatus = app._hotkeys.LastRejectReason ?? "这个组合键不允许做全局热键";
                break;
            default:
                CaptureHotkeyOk = false;
                CaptureHotkeyLabel = null;
                CaptureHotkeyStatus = result == HotkeyRegisterResult.AlreadyRegistered
                    ? $"{chord.Label} 已被别的程序占用，换一个组合键"
                    : $"{chord.Label} 注册失败（Win32 错误），换一个组合键试试";
                break;
        }

        if (!CaptureHotkeyOk)
        {
            Note($"热键 {chord.Label} 未生效：{CaptureHotkeyStatus}");
        }

        // 托盘菜单上的「截取屏幕并识别 (F8)」读的就是 CaptureHotkeyLabel，换键后要一起刷新。
        app.RefreshTray();
        return CaptureHotkeyStatus;
    }

    /// <summary>
    /// 把「显示设置」里的字号 / 字重 / 主题**真的用上**：更新静态值 + 换界面主题 +
    /// 通知主界面重画题目卡片。改完立刻生效，不用重启。
    /// </summary>
    internal static void ApplyUiSettings()
    {
        var ui = AppSettings.Load();
        QuestionFontSize = ui.FontSize;
        QuestionFontWeight = ui.FontWeight;
        ApplyTheme(ui.Theme);
        DisplaySettingsChanged?.Invoke();
    }

    /// <summary>
    /// 主题三态：`system` / `light` / `dark`（1.1.0「显示设置 → 主题」）。
    /// WinUI 里对应 `ElementTheme.Default / Light / Dark`；设在 Frame 上整棵树跟着走
    /// （`ThemeResource` 会在主题变化时重算），不需要逐个控件刷颜色。
    ///
    /// 设成 static 是为了让设置页改完主题能立刻调它（选中即生效，1.1.0 的行为）。
    /// </summary>
    internal static void ApplyTheme(string theme)
    {
        if (((App)Current)._window?.Content is not FrameworkElement root)
        {
            return;
        }

        root.RequestedTheme = theme switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
    }

    /// <summary>
    /// 悬浮球（原生分层窗口）。点击它就等于按一次 F8 —— **两条路走同一个入口**，
    /// 所以「点了球」与「按了热键」的行为不会分叉。
    ///
    /// 失败**不静默**：球建不起来时把原因写进窗口标题（这一版还没有日志面板）。
    /// </summary>
    private void ShowFloatingBall() => ApplyBallSettings();

    /// <summary>
    /// 按当前设置重建悬浮球。**设置页改完就调它** —— 这样「改了设置立刻生效」，
    /// 不用重启应用（1.1.0 的用户反馈里，「调了没反应」是最早被报上来的那类问题）。
    /// </summary>
    internal void ApplyBallSettings()
    {
        // 先把旧的收掉：尺寸/透明度变了必须重建（分层窗口的位图是按尺寸建的）。
        _ball?.Dispose();
        _ball = null;

        var settings = BallSettings.Load();
        if (!settings.Enabled)
        {
            return;
        }

        try
        {
            _ball = new FloatingBall(
                _window!.DispatcherQueue,
                TriggerCapture,
                settings.Diameter,
                settings.Opacity,
                settings.Stroke ? settings.StrokeWidth : 0,
                settings.StrokeOpacity,
                settings.X,
                settings.Y,
                (x, y) =>
                {
                    // 只更新位置，别把界面上的其他改动覆盖掉（设置页可能刚改过大小）。
                    var current = BallSettings.Load();
                    current.X = x;
                    current.Y = y;
                    current.Save();
                },
                // 悬浮球左键长按 ≥500ms / 右键 = 加一页（与 F9 完全等价）。
                // 之前这里是空的 —— 手势认出来了却什么都不做，正是「静默空操作」。
                onAppendPage: TriggerMultipage);
            _ball.Show();
        }
        catch (Exception error)
        {
            _ball?.Dispose();
            _ball = null;
            SetWindowTitle($"{AppWindowScope.Title}（悬浮球未显示：{error.Message}）");
        }
    }

    /// <summary>
    /// 按当前设置重建悬浮窗（开关、外观、比例、配色、明暗、极简、透明度全在这里生效）。
    /// **设置页改完就调它** —— 与悬浮球同一条纪律：「改了设置立刻生效」，不用重启。
    ///
    /// 注意**不是每次重建窗口**：窗口已经开着时只调 <see cref="Pages.FloatWindow.ApplySettings"/>，
    /// 否则用户每拖一次滑块，窗口都会消失再出现（1.1.0 的观感问题就是重建造成的）。
    /// </summary>
    internal void ApplyFloatWindowSettings()
    {
        var settings = AppSettings.Load();

        if (!settings.FloatWindowEnabled)
        {
            _floatWindow?.Close();
            _floatWindow = null;
            return;
        }

        if (_floatWindow is null)
        {
            try
            {
                _floatWindow = new Pages.FloatWindow();
            }
            catch (Exception error)
            {
                _floatWindow = null;
                SetWindowTitle($"{AppWindowScope.Title}（悬浮窗未显示：{error.Message}）");
                return;
            }
        }

        _floatWindow.ApplySettings();

        // 规格 §6.1：悬浮窗**不抢焦点**。`Activate()` 会把它抢到前台 —— 而设置页每改一项都会
        // 走到这里，用户正在别的程序里打字时就会被顶掉（`EnsureOverlayStyles` 只能挡住「点击激活」，
        // 挡不住程序自己调 `Activate()`）。改成 `Show(activateWindow: false)`：只显示/更新，不动前台窗口。
        _floatWindow.AppWindow.Show(activateWindow: false);
    }

    /// <summary>「显示/隐藏悬浮窗」：托盘菜单与设置页的开关都走它（一条路径，行为不分叉）。</summary>
    internal static void ToggleFloatWindow()
    {
        var current = AppSettings.Load();
        current.FloatWindowEnabled = !current.FloatWindowEnabled;
        current.Save();
        var app = (App)Current;
        app.ApplyFloatWindowSettings();
        app.RefreshTray();
        Note(current.FloatWindowEnabled ? "已显示悬浮窗" : "已隐藏悬浮窗");
    }

    /// <summary>
    /// 识别结束后刷新悬浮窗内容（结果已经落库，重读一次即可）。
    /// **`resetToLatest: true`**：这是「识别完成」那条路 —— 规格 §6.5 要求识别结束后回到最新一条，
    /// 用户刚识别完想看的就是这次结果。改设置触发的刷新不走这里。
    /// </summary>
    internal static void RefreshFloatWindow() => ((App)Current)._floatWindow?.Reload(resetToLatest: true);

    /// <summary>
    /// 重建当前设置页。**外观 / 配色 / 明暗这三组改完必须重建整页** ——
    /// 它们的选项本身就是界面（选中态、「使用中」、副标题里的当前尺寸），
    /// 只改设置不重画的话，用户看到的是「点了没反应」。
    /// </summary>
    internal void ReloadSettingsPage()
    {
        if (_window is MainWindow main && main.ShellFrame.Content is Pages.SettingsPage page)
        {
            page.Refresh();
        }
    }

    /// <summary>
    /// 热键当前是否被界面**刻意吞掉**（设置页挂着的时候，尤其是正在录入新热键时）。
    ///
    /// 语义照 1.1.0 的结论（`AGENTS.md` M30/M31）：**热键始终注册着**，在**触发那一刻**
    /// 问一次「现在要不要吞掉」。用「进页面就注销、离开再注册」表达 UI 状态会锁死 ——
    /// 窗口收进托盘/最小化时页面不 dispose，标志一直是 true，热键就永久失效（用户看不出原因）。
    /// </summary>
    internal static bool HotkeySuppressed { get; set; }

    /// <summary>
    /// 热键、悬浮球、托盘菜单共用的触发路径 —— **三条路走同一个入口**，
    /// 所以「按 F8」「点球」「点托盘菜单」的行为不会分叉（1.1.0 的教训：一分叉就有一条是错的）。
    ///
    /// F8 的语义按 1.1.0：**不在多页模式时＝截一张屏立刻识别；正在多页模式里时＝结束多页，
    /// 把已抓的图一起上传识别**（ui-spec-desktop §3.9 的分组说明）。
    ///
    /// 目标页面是 <see cref="Pages.HomePage"/>。
    /// </summary>
    private void TriggerCapture()
    {
        if (HotkeySuppressed)
        {
            Note("热键被设置页吞掉（避免设置热键时误触发截屏）—— 离开设置页立刻恢复");
            return;
        }

        // 正在攒页 → F8 是「结束多页并上传」，不是再截一张。
        if (Staging.Count > 0)
        {
            FinishMultipage();
            return;
        }

        if (MainPage() is { } current)
        {
            _ = current.RunCaptureAsync();
        }
    }

    /// <summary>
    /// F9：多页模式。抓一页进暂存区；抓满上限就**自动上传识别**，不用再按键
    /// （ui-spec-desktop §1.2.2 与 §3.9）。
    ///
    /// 悬浮球的左键长按 / 右键、托盘的「多页识别」都走这里。
    /// </summary>
    private void TriggerMultipage()
    {
        if (HotkeySuppressed)
        {
            Note("热键被设置页吞掉（避免设置热键时误触发截屏）—— 离开设置页立刻恢复");
            return;
        }

        var shot = ScreenJpeg.Capture(AppWindowScope.Title);
        if (shot is null)
        {
            Note("多页：截屏失败（拿不到屏幕尺寸）");
            return;
        }

        // 第 1 页进入多页模式时先把主界面切到主界面页，用户能立刻看到暂存条。
        if (MainPage() is null)
        {
            NavigateTo(typeof(Pages.HomePage));
        }

        SetBallState(BallState.MultiPage);

        if (Staging.Append(shot))
        {
            // 抓满上限 → 自动上传。
            FinishMultipage();
            return;
        }

        RefreshTray();
    }

    /// <summary>结束多页：把暂存页一次上传识别（F8 在多页模式里的语义）。</summary>
    private void FinishMultipage()
    {
        var pages = Staging.TakeAll();
        Staging.Clear();
        SetBallState(BallState.Idle);
        RefreshTray();

        if (pages.Count == 0)
        {
            Note("多页：暂存区是空的，没有可上传的页");
            return;
        }

        if (MainPage() is { } home)
        {
            _ = home.RunMultipageAsync(pages);
        }
    }

    /// <summary>
    /// 当前主界面页（不在主界面就返回 null，调用方据此先导航过去）。
    /// 走 <see cref="MainWindow.ShellFrame"/> —— 窗口内容外面包了标题栏 Grid，
    /// `Content is Frame` 已经从「能过」变成「永远 null」，而那是**静默**失效。
    /// </summary>
    private Pages.HomePage? MainPage() =>
        _window is MainWindow main && main.ShellFrame.Content is Pages.HomePage home ? home : null;

    /// <summary>
    /// 注册 / 换多页热键（槽位 2，默认 F9）。与截屏热键同一套处理：
    /// 换键前先注销，失败不假装成功。
    /// </summary>
    internal static string ApplyMultipageHotkey(HotkeyChord? customChord)
    {
        if (Instance is not { } app)
        {
            return "应用没起来，热键没有改";
        }

        var chord = customChord ?? DefaultMultipageChord;
        if (app._hotkeys is null)
        {
            MultipageHotkeyOk = false;
            MultipageHotkeyLabel = null;
            MultipageHotkeyStatus = "热键服务没起来（见窗口标题）";
            return MultipageHotkeyStatus;
        }

        app._hotkeys.Unregister(MultipageSlot);
        var result = app._hotkeys.Register(MultipageSlot, chord);
        switch (result)
        {
            case HotkeyRegisterResult.Ok:
                MultipageHotkeyOk = true;
                MultipageHotkeyLabel = chord.Label;
                MultipageHotkeyStatus = $"已注册：{chord.Label}";
                break;
            case HotkeyRegisterResult.Rejected:
                MultipageHotkeyOk = false;
                MultipageHotkeyLabel = null;
                MultipageHotkeyStatus = app._hotkeys.LastRejectReason ?? "这个组合键不允许做全局热键";
                break;
            default:
                MultipageHotkeyOk = false;
                MultipageHotkeyLabel = null;
                MultipageHotkeyStatus = result == HotkeyRegisterResult.AlreadyRegistered
                    ? $"{chord.Label} 已被别的程序占用，换一个组合键"
                    : $"{chord.Label} 注册失败（Win32 错误），换一个组合键试试";
                break;
        }

        app.RefreshTray();
        return MultipageHotkeyStatus;
    }

    /// <summary>托盘的「截取屏幕并识别」。托盘回调在 UI 线程的消息泵上，不用切线程。</summary>
    internal static void RequestCapture() => ((App)Current).TriggerCapture();

    /// <summary>托盘的「多页识别」——与 F9、悬浮球长按完全等价。</summary>
    internal static void RequestMultipage() => ((App)Current).TriggerMultipage();

    /// <summary>
    /// 托盘的「从剪贴板读取」：读剪贴板里的图 → 当成一页走同一条分析链。
    ///
    /// 剪贴板里没有图时**明确提示**（这是最容易「点了没反应」的入口）。
    /// 与 F8 / 悬浮球一样把主界面叫出来，用户才看得到结果。
    /// </summary>
    internal static async void RequestClipboard()
    {
        var app = (App)Current;
        MainWindowInstance?.Activate();

        (byte[] Jpeg, string Hash)? image;
        try
        {
            image = await ClipboardImage.TryReadAsync();
        }
        catch (Exception error)
        {
            Note($"读剪贴板失败：{error.Message}");
            return;
        }

        if (image is not { } shot)
        {
            var formats = ClipboardImage.LastRejectedFormats;
            Note(formats.Count == 0
                ? "剪贴板里没有图片（先截图或复制一张图再试）"
                : $"剪贴板里的格式认不出来：{string.Join(" / ", formats)}");
            return;
        }

        HandOffClipboardImage(shot.Jpeg, shot.Hash);
    }

    /// <summary>
    /// 把「剪贴板里读到的一张图」交给主界面，走与截屏完全相同的那条分析链。
    ///
    /// 两条入口共用它：托盘的「从剪贴板读取」（**先把窗口叫出来**，因为它是一次明确的用户动作）
    /// 与剪贴板监听（**不碰窗口状态**）。分开写两份必然会漂移 —— 1.1.0 的历史里
    /// 「同一件事两个入口」正是漏改的重灾区。
    /// </summary>
    private static void HandOffClipboardImage(byte[] jpeg, string hash)
    {
        var app = (App)Current;
        if (app.MainPage() is not { } home)
        {
            NavigateTo(typeof(Pages.HomePage));
            app._window?.DispatcherQueue.TryEnqueue(() =>
            {
                if (((App)Current).MainPage() is { } page)
                {
                    _ = page.RunClipboardAsync(jpeg, hash);
                }
            });
            return;
        }

        _ = home.RunClipboardAsync(jpeg, hash);
    }

    /// <summary>托盘的「清空多页暂存区」——丢弃暂存页，不上传。</summary>
    internal static void ClearStaging()
    {
        var app = (App)Current;
        var count = Staging.Count;
        Staging.Clear();
        SetBallState(BallState.Idle);
        app.RefreshTray();
        Note(count == 0 ? "暂存区本来是空的" : $"已清空多页暂存区（{count} 页）");
    }

    /// <summary>
    /// 托盘的「切换任务合集」：把窗口叫出来 → 开那个合集弹窗（与顶栏药丸同一条入口）。
    ///
    /// ⚠️ 这里踩过一次：第一版无论在哪都先 `Navigate(typeof(HomePage))` 再派发去开弹窗，
    /// 而新页面的 `Loaded` 还没跑、`_database` 还是 null → 弹窗里那句 `if (_database is null) return;`
    /// 直接把它吃掉了，**表现为点了没反应**（正是这一路一直在清掉的静默空操作）。
    ///
    /// 所以：**已经在主界面就直接开**（正常情况就是这条），只有确实不在主界面才导航，
    /// 并且把开弹窗排到导航之后的下一轮派发。
    /// </summary>
    internal static void RequestSwitchCollection()
    {
        var app = (App)Current;
        MainWindowInstance?.Activate();

        if (app.MainPage() is { } current)
        {
            _ = current.ShowCollectionPickerAsync();
            return;
        }

        NavigateTo(typeof(Pages.HomePage));
        app._window?.DispatcherQueue.TryEnqueue(() =>
        {
            if (app.MainPage() is { } home)
            {
                _ = home.ShowCollectionPickerAsync();
            }
            else
            {
                Note("切不到主界面，「切换任务合集」没打开");
            }
        });
    }

    /// <summary>托盘的「显示 / 隐藏悬浮球」：翻开关 → 存盘 → 重建球 → 刷新托盘勾选状态。</summary>
    internal static void ToggleFloatingBall()
    {
        var app = (App)Current;
        var settings = BallSettings.Load();
        settings.Enabled = !settings.Enabled;
        settings.Save();
        app.ApplyBallSettings();
        app.RefreshTray();
    }

    /// <summary>切到某个页面（托盘的「设置」用）。窗口先显示出来，否则用户看不到切页结果。</summary>
    internal static void NavigateTo(Type page)
    {
        if (((App)Current)._window is MainWindow main)
        {
            // 托盘触发的切页同样是压栈方向（DrillIn），与界面里的入口保持一致。
            Theme.QsAnimations.NavigateForward(main.ShellFrame, page);
        }
    }

    /// <summary>
    /// 把一句话写到窗口标题后缀上。这一版还没有日志面板与通知中心，
    /// 标题是用户最容易看见的地方 —— **「还没接上」这类信息绝不能静默吞掉**。
    ///
    /// 走 <see cref="SetWindowTitle"/> 而不是直接写 <c>window.Title</c>：
    /// 自定义标题栏之后屏幕上那一行是 <c>TitleBar.Title</c>，只改 Window.Title 等于把提示藏了。
    /// </summary>
    internal static void Note(string text)
    {
        SetWindowTitle($"{AppWindowScope.Title}（{text}）");
        ((App)Current)._window?.Activate();
    }

    /// <summary>
    /// 设置窗口标题的**唯一入口**（同时更新自定义标题栏那一行）。
    /// 原来有 4 处各写一遍 <c>window.Title = …</c>，加自定义标题栏时必须一起改，
    /// 漏一处就是「某类提示用户永远看不见」——所以收成一个入口。
    /// </summary>
    internal static void SetWindowTitle(string text)
    {
        switch (((App)Current)._window)
        {
            case MainWindow main:
                main.ShowTitle(text);
                break;
            case { } window:
                window.Title = text;
                break;
            default:
                break;
        }
    }

    /// <summary>建托盘图标。失败**不静默**：原因写进窗口标题（与热键、悬浮球同一套处理）。</summary>
    private void ShowTray()
    {
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "tray.ico");
            _tray = new TrayController(iconPath);
            _tray.Show();
            RefreshTray();
        }
        catch (Exception error)
        {
            _tray?.Dispose();
            _tray = null;
            SetWindowTitle($"{AppWindowScope.Title}（托盘图标未显示：{error.Message}）");
        }
    }

    /// <summary>刷新托盘菜单的动态文案与勾选状态（热键、暂存页数、悬浮球开关）。</summary>
    private void RefreshTray() =>
        _tray?.Refresh(
            CaptureHotkeyLabel,
            stagedPages: Staging.Count,
            stagedLimit: Staging.Limit,
            BallSettings.Load().Enabled);

    /// <summary>
    /// 换悬浮球的状态（待识别 / 识别中 / 多页模式）。1.1.0 三态各有素材图与主色
    /// （ui-spec-desktop §6.6），识别期间球要变黄、多页期间变绿 —— 这是给用户的唯一进度反馈，
    /// 不能一直蓝着。球没开就什么都不做。
    /// </summary>
    internal static void SetBallState(BallState state) =>
        ((App)Current)._ball?.SetState(state);

    /// <summary>
    /// 改题目正文字号 / 字重。**存下来之外还要让主界面重画** ——
    /// 1.1.0 里拖完滑块主界面的题目卡片当场就变；只存不刷新等于「调了没反应」。
    /// </summary>
    internal static void ApplyQuestionStyle(double fontSize, double fontWeight)
    {
        QuestionFontSize = fontSize;
        QuestionFontWeight = (int)Math.Round(fontWeight);

        if (((App)Current)._window is MainWindow main
            && main.ShellFrame.Content is Pages.HomePage home)
        {
            home.RefreshQuestionStyle();
        }
    }

    /// <summary>
    /// 注销全局热键、销毁悬浮球与托盘图标、停掉内嵌主机
    /// （前三者都是系统级的，退出时必须显式清理）。
    /// </summary>
    public void Dispose()
    {
        _hotkeys?.Dispose();
        _hotkeys = null;
        _ball?.Dispose();
        _ball = null;
        // 托盘图标不显式 NIM_DELETE 的话会**留在托盘区**，直到用户把鼠标划过去才消失。
        _tray?.Dispose();
        _tray = null;
        // 剪贴板监听也是系统级的：不移除的话窗口销毁后消息还在发（子类化过程已被回收）。
        _clipboardWatch?.Dispose();
        _clipboardWatch = null;
        // 内嵌主机（第 167 轮）：不 dispose 的话 SQLite 连接与监听端口要等进程结束才放开，
        // 而「退出应用 → 立刻再启动一次」是很常见的动作（端口没放干净会让第二次起来慢一截）。
        // **限时等待**：退出路径上不能因为收尾卡住而让窗口关不掉。
        try
        {
            QuizSync.ServerBridge.HostLauncher.ReleaseEmbeddedAsync()
                .AsTask().Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception error)
        {
            CrashLog.Write("停止内嵌主机失败", error);
        }

        GC.SuppressFinalize(this);
    }
}
