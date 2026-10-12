using System.Diagnostics;
using System.Text.Json.Nodes;

namespace QuizSync.ServerBridge;

/// <summary>拉起识别服务（Provider）的结局。**每一种都要能对用户说清楚**，不许"点了没反应"。</summary>
public enum ProviderLaunchStatus
{
    /// <summary>主机说它已经在线了（`/api/v2/info` 的 `provider_online`）—— 不需要拉起。</summary>
    AlreadyOnline,

    /// <summary>这次是我们拉起来的，并且主机已经认到它。</summary>
    Started,

    /// <summary>找不到 Provider 程序（没构建过 / 路径不对）。**不猜、不静默**，把找过的路径一并报出来。</summary>
    NotFound,

    /// <summary>起来了但没在超时内上线。</summary>
    Timeout,

    /// <summary>启动进程本身就失败了（权限、路径被占…），或者刚起来就退出（例如没配 AI，退出码 2）。</summary>
    Failed,
}

/// <param name="ElapsedMs">从发起拉起到主机认到它的毫秒数。</param>
/// <param name="Detail">给人看的一句话（失败时是原因 + 试过的路径）。</param>
/// <param name="ProcessId">这次拉起来的进程号（<see cref="ProviderLaunchStatus.Started"/> 时非空）。</param>
public sealed record ProviderLaunchOutcome(
    ProviderLaunchStatus Status,
    int ElapsedMs,
    string Detail,
    int? ProcessId = null);

/// <summary>
/// 「主机在跑、但没人干识别」时把 **Provider** 拉起来（`QuizSync.Provider.Cli provider`）。
///
/// **为什么需要它**：主机按架构不持有 API Key（D1），识别是 Provider 的活。少一个 Provider 进程，
/// 手机截屏的链路就停在「等待电脑识别」，而用户看不到任何提示 —— 这类"能点但没结果"最难自查。
///
/// **怎么判断"在不在线"**：问主机，不猜。`/api/v2/info` 的 `provider_online` 是主机自己的判断
/// （v1 的 `/info` 是冻结契约，不能加字段；这是 `ServerInfo` 里写明的）。
///
/// **与 `setup --autostart` 的分工**：那条是"登录时自动起"；这条是"没装启动项、或者进程挂了"时的兜底。
/// </summary>
public static class ProviderLauncher
{
    /// <summary>
    /// 本应用**自己**拉起来的那个 Provider 进程号（重启时只杀它）。
    ///
    /// 为什么要记：`setup --autostart` 或用户手开的 Provider **不能被我们杀掉** ——
    /// 在别人的进程上动手比「重启没生效」更坏（与「关掉连接设备不去杀主机」同一条纪律）。
    /// </summary>
    private static int? _startedProcessId;

    /// <summary>本应用拉起来的 Provider 进程号（null = 没起过，或已经被关掉了）。</summary>
    public static int? StartedProcessId => _startedProcessId;

    /// <summary>
    /// 关掉**本应用自己拉起来的**那个识别服务（返回是否真的关掉了一个）。
    ///
    /// 「重启连接服务」需要它：不先关掉旧的，`EnsureOnlineAsync` 会看到 `provider_online = true`
    /// 直接返回「本来就在线」，重启就成了空操作。
    /// </summary>
    public static bool TryStopStarted(int waitMs = 5000)
    {
        if (_startedProcessId is not { } id)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(id);
            if (process.HasExited)
            {
                _startedProcessId = null;
                return false;
            }

            process.Kill(entireProcessTree: true);
            process.WaitForExit(waitMs);
            _startedProcessId = null;
            return true;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            // 进程已经没了 / 拿不到句柄：当成"没有可关的"，不是错误。
            _startedProcessId = null;
            return false;
        }
    }

    /// <summary>Provider 程序的位置。`QS_PROVIDER_DLL` 优先（脚本与测试用），否则按仓库相对布局找。</summary>
    public static string? ResolveProviderDll()
    {
        var configured = Environment.GetEnvironmentVariable("QS_PROVIDER_DLL");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        // 从应用所在目录逐级往上找，两种构建产物都试：publish（Release 发布）优先，其次 Debug 直出。
        // ⚠️ TFM 不写死：CLI 工程是 `net10.0-windows`，硬编码 `net10.0` 在干净构建下会找不到
        //（这条是 `ProviderCliTests.CliDll` 的既有教训，按 `net10.0*` 匹配目录即可）。
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 8 && directory is not null; depth++)
        {
            var release = Path.Combine(
                directory.FullName, "QuizSync.Provider.Cli", "bin", "Release", "publish", "QuizSync.Provider.Cli.dll");
            if (File.Exists(release))
            {
                return release;
            }

            var debugRoot = Path.Combine(directory.FullName, "QuizSync.Provider.Cli", "bin", "Debug");
            if (Directory.Exists(debugRoot))
            {
                // ⚠️ 目录里可能同时留着旧 TFM 的残留（本机实测：`net10.0` 与 `net10.0-windows` 并存）。
                // 按文件系统顺序取第一个会挑到残留 —— 那样跑起来**退出码 1**（缺依赖），
                // 报出来只有"刚起来就退出了"，很容易被误判成环境问题。
                // 所以：**先按当前 TFM 精确匹配，再按产物新旧兜底**。
                var candidates = Directory.EnumerateDirectories(debugRoot, "net10.0*")
                    .Select(dir => Path.Combine(dir, "QuizSync.Provider.Cli.dll"))
                    .Where(File.Exists)
                    .OrderByDescending(path =>
                        Path.GetFileName(Path.GetDirectoryName(path)) == "net10.0-windows")
                    .ThenByDescending(File.GetLastWriteTimeUtc)
                    .ToList();
                if (candidates.Count > 0)
                {
                    return candidates[0];
                }
            }

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>找过哪些地方（失败时原样报给用户，省得他从零开始猜）。</summary>
    public static string SearchedHint() =>
        "环境变量 QS_PROVIDER_DLL；以及从应用目录逐级向上找 "
        + "`QuizSync.Provider.Cli/bin/{Release/publish,Debug/net10.0*}/QuizSync.Provider.Cli.dll`";

    /// <summary>
    /// 主机自己说的：识别服务在不在线。
    /// 返回 <c>null</c> = **问不出来**（主机不可达 / 这个主机没有 v2 的 info）——
    /// 与"在线"和"不在线"都是两件事，调用方必须分开处理，不能把 null 当 false。
    /// </summary>
    public static async Task<bool?> IsOnlineAsync(string baseUrl, CancellationToken cancellationToken = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var text = await http.GetStringAsync(
                $"{baseUrl.TrimEnd('/')}/api/v2/info", cancellationToken).ConfigureAwait(false);
            return JsonNode.Parse(text)?["provider_online"]?.GetValue<bool>();
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// 确保识别服务在线：问主机 → 不在就拉一个独立进程 → 等主机认到它。
    ///
    /// 注意**不在这里查 AI 配置**：那属于调用方的判断（界面要给出的提示是"去 API 配置里填 Key"，
    /// 而不是"进程起不来"）。没配 Key 的 Provider 会以退出码 2 立刻退出，这条会如实报成
    /// <see cref="ProviderLaunchStatus.Failed"/> 并把退出码带上。
    /// </summary>
    /// <param name="baseUrl">主机的地址（探活与"认到了没有"都用它）。</param>
    /// <param name="serverDataDirectory">
    /// **主机**的数据目录。给了就通过环境变量 `QS_SERVER_DATA` 传给子进程 ——
    /// 为什么不是命令行参数：`QuizSync.Provider.Cli` 的 `provider` 子命令**不读 `--data`**
    /// （那是 `doctor` / `analyze` 的参数），它靠 `HostDataDirectory.Find()` 找主机数据目录，
    /// 而那条路认的就是 `QS_SERVER_DATA`。不传时子进程用默认位置（`%LOCALAPPDATA%\QuizSyncAI\server`）。
    /// </param>
    public static async Task<ProviderLaunchOutcome> EnsureOnlineAsync(
        string baseUrl,
        string? serverDataDirectory = null,
        int timeoutMs = 25000,
        CancellationToken cancellationToken = default)
    {
        // ① 已经在线：直接用。**绝不重复起第二个** —— 两个 Provider 会互相顶掉（协议里是单 Provider）。
        if (await IsOnlineAsync(baseUrl, cancellationToken).ConfigureAwait(false) is true)
        {
            return new ProviderLaunchOutcome(ProviderLaunchStatus.AlreadyOnline, 0, "识别服务本来就在线");
        }

        // ② 找程序
        var dll = ResolveProviderDll();
        if (dll is null)
        {
            return new ProviderLaunchOutcome(
                ProviderLaunchStatus.NotFound, 0, "找不到识别服务程序：" + SearchedHint());
        }

        // ③ 起进程。**`QS_DOTNET` 优先走 `dotnet <dll>`**，只有在没给 `QS_DOTNET` 时才考虑 apphost
        //（publish 出来的 `.exe`）—— 实测（本轮）：本机的 .NET 10 是私有安装（`D:\Windows\Apps\DSH_Tools\dotnet`），
        // 没有注册到系统，直接跑 `QuizSync.Provider.Cli.exe` 会以
        // 「You must install .NET to run this application」失败（退出码 0x80008083），
        // 而 `dotnet <dll>` 一切正常。先挑 apphost 会让"点了没反应"变成一条查不出来的路。
        var apphost = Path.ChangeExtension(dll, ".exe");
        var dotnet = Environment.GetEnvironmentVariable("QS_DOTNET");
        var useApphost = string.IsNullOrWhiteSpace(dotnet) && File.Exists(apphost);
        var fileName = useApphost ? apphost : (string.IsNullOrWhiteSpace(dotnet) ? "dotnet" : dotnet);
        var arguments = useApphost ? "provider" : $"\"{dll}\" provider";

        var stopwatch = Stopwatch.StartNew();
        Process process;
        try
        {
            var startInfo = new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(dll)!,
            };

            if (!string.IsNullOrWhiteSpace(serverDataDirectory))
            {
                startInfo.Environment["QS_SERVER_DATA"] = serverDataDirectory;
            }

            process = Process.Start(startInfo)!;
            // 记下来：这样「重启连接服务」才知道该关谁（只关自己起的那个）。
            _startedProcessId = process.Id;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new ProviderLaunchOutcome(
                ProviderLaunchStatus.Failed, (int)stopwatch.ElapsedMilliseconds,
                $"启动识别服务失败：{error.Message}");
        }

        // ④ 等主机认到它（判据只有一条：主机说的 provider_online）
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            if (process.HasExited)
            {
                // 最常见的两种：没配 AI（2）、主机不认令牌（4）—— 退出码原样带上，别让用户猜。
                return new ProviderLaunchOutcome(
                    ProviderLaunchStatus.Failed, (int)stopwatch.ElapsedMilliseconds,
                    $"识别服务刚起来就退出了（退出码 {process.ExitCode}）", process.Id);
            }

            if (await IsOnlineAsync(baseUrl, cancellationToken).ConfigureAwait(false) is true)
            {
                return new ProviderLaunchOutcome(
                    ProviderLaunchStatus.Started, (int)stopwatch.ElapsedMilliseconds,
                    "识别服务已启动并在线", process.Id);
            }

            await Task.Delay(400, cancellationToken).ConfigureAwait(false);
        }

        return new ProviderLaunchOutcome(
            ProviderLaunchStatus.Timeout, (int)stopwatch.ElapsedMilliseconds,
            $"识别服务起来了但主机 {timeoutMs} ms 内没认到它（进程 {process.Id} 仍在跑，可以稍后重试）",
            process.Id);
    }
}
