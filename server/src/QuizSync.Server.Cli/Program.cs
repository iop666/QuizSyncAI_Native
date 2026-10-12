using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using QuizSync.Server.Cli;
using QuizSync.Server.Core;
using QuizSync.Server.Core.Net;
using QuizSync.Server.Core.Storage;
using QuizSync.Server.Core.Tasks;
using QuizSync.Server.Core.Ws;

// CLI 只做三件事：解析参数、调用 Core 或打**本机控制面**、把结果打到终端。
// **任何协议逻辑都不许写在这里**（见 src/README.md 的分层纪律）。

var parsed = CliArgs.Parse(args);
// `help` 是**文档里写的命令**，但它此前没有 case —— 掉进 `switch` 的 `default:`（与 `run` 共用分支）
// 于是「想看帮助」变成了**真把主机起起来**：占住 8765（局域网可连）、动真实数据目录，
// 而且因为 `touchesData` 把 help 划成"不碰数据"，控制令牌是空的 → 本地控制面直接 501
// （`pair` / `devices` 打过去全是"这个实例没有开放本机控制面"）。
// 现场：`SetupWizardTests.Help_mentions_the_firewall_switch` 里 `RunCli("help")` 的子进程
// 一直不退出，而 `RunCli` 用的是 `StandardOutput.ReadToEnd()` —— **永远等不到 EOF，整轮测试挂死**。
if (parsed.ShowHelp || parsed.Command == "help")
{
    Console.WriteLine(CliArgs.HelpText);
    return 0;
}

// **共享数据目录**（第二十三轮）：应用与服务端是两个 exe，各自用
// `AppContext.BaseDirectory/userdata` 的话不在同一个地方 —— 应用读不到这里的 `control.token`，
// 于是「显示配对码 / 连接设备 / 推送」对真实用户是**断的**（此前的验证全靠 `QS_SERVER_DATA`
// 把它们接上，那是开发时的私有手段）。约定 `%LOCALAPPDATA%\QuizSyncAI\server`，
// 应用侧对应 `QuizSync.App\HostDataDirectory.cs` 里的 `SharedLayout`。
var dataDir = parsed.DataDirectory ?? Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuizSyncAI", "server");

var options = new ServerOptions
{
    PreferredPort = parsed.Port ?? ServerOptions.DefaultPort,
    BindAddress = parsed.Bind ?? "0.0.0.0",
    // 必须是**解析后**的路径：原来传 parsed.DataDirectory，而它没写 --data 时是 null，
    // QuizSyncHost 对 null 的处理是 databasePath = ":memory:" —— 于是 run 起来时
    // control.token 落在 <baseDir>/userdata，数据库却在内存里：配对成功却一行不落库、
    // devices 命令永远查不到设备、服务端一重启配对全丢（第七轮实测）。
    DataDirectory = dataDir,
};

// 中文输出必须显式设成 UTF-8：默认跟随控制台代码页，CI runner 上会变成 ??????
// （实测：本地绿、CI 红的 5 条用例全是这个原因 —— 断言里的中文子串在子进程输出里不存在了）。
try
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
}
catch (IOException)
{
    // 没有控制台（被重定向到管道）时可能抛，忽略即可。
}


// 免数据目录的命令：`version` / `help` 不该有副作用；
// `setup` 不带 `--apply` 时是 **dry-run** —— 只打印计划，绝不能把目录建出来
// （这条是 `SetupWizardTests.Dry_run_prints_the_plan_and_changes_nothing` 抓出来的：
//  原来无条件建目录，等于「想看一眼却把环境改了」）。
var touchesData = parsed.Command is not ("version" or "help")
    && !(parsed.Command == "setup" && !parsed.Apply)
    && !parsed.ShowHelp;
var controlToken = touchesData ? ReadOrCreateControlToken(dataDir) : string.Empty;

switch (parsed.Command)
{
    case "version":
        Console.WriteLine($"QuizSync Server {ServerOptions.ProtocolVersion}.0.0" +
                          $"（协议 v{ServerOptions.ProtocolVersion}，含 /api/v1 兼容层）");
        return 0;

    case "pair":
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            client.DefaultRequestHeaders.Add("X-QS-Control", controlToken);
            try
            {
                var json = await client.GetFromJsonAsync<JsonElement>(
                    $"http://127.0.0.1:{options.PreferredPort}/api/v1/pair/code");
                if (parsed.Json)
                {
                    Console.WriteLine(json.GetRawText());
                }
                else
                {
                    var expires = DateTimeOffset
                        .FromUnixTimeMilliseconds(json.GetProperty("expires_at").GetInt64())
                        .ToLocalTime();
                    Console.WriteLine($"配对码：{json.GetProperty("code").GetString()}");
                    Console.WriteLine($"有效期至：{expires:yyyy-MM-dd HH:mm:ss}");
                    Console.WriteLine($"配对链接：{json.GetProperty("pair_uri").GetString()}");
                }

                return 0;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                Console.Error.WriteLine($"读不到配对码：服务没在 127.0.0.1:{options.PreferredPort} 上跑（{ex.Message}）");
                return 3;
            }
        }

    case "devices":
        {
            using var database = QuizSyncDatabase.Open(Path.Combine(dataDir, "quizsync.db"));
            var devices = new DeviceRepository(database);
            if (parsed.Sub == "revoke")
            {
                if (parsed.Arg is null)
                {
                    Console.Error.WriteLine("用法：devices revoke <device_id>");
                    return 1;
                }

                devices.Revoke(parsed.Arg, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                Console.WriteLine($"已吊销：{parsed.Arg}");
                return 0;
            }

            var rows = devices.List();
            if (parsed.Json)
            {
                var array = new JsonArray();
                foreach (var d in rows)
                {
                    array.Add(new JsonObject
                    {
                        ["device_id"] = d.DeviceId,
                        ["name"] = d.Name,
                        ["platform"] = d.Platform,
                        ["paired_at"] = d.PairedAt,
                        ["revoked"] = d.IsRevoked,
                    });
                }

                Console.WriteLine(array.ToJsonString());
                return 0;
            }

            if (rows.Count == 0)
            {
                Console.WriteLine("（还没有已配对设备）");
                return 0;
            }

            foreach (var d in rows)
            {
                Console.WriteLine($"{d.DeviceId,-24} {d.Name,-16} {d.Platform,-8} {(d.IsRevoked ? "已吊销" : "有效")}");
            }

            return 0;
        }

    case "config":
        {
            using var database = QuizSyncDatabase.Open(Path.Combine(dataDir, "quizsync.db"));
            var store = new DeviceRepository(database);
            if (parsed.Sub == "set" && parsed.Arg is not null && parsed.Value is not null)
            {
                store.SetSetting(parsed.Arg, parsed.Value);
                Console.WriteLine($"{parsed.Arg} = {parsed.Value}");
                return 0;
            }

            if (parsed.Sub == "get" && parsed.Arg is not null)
            {
                Console.WriteLine(store.GetSetting(parsed.Arg) ?? "（未设置）");
                return 0;
            }

            if (parsed.Json)
            {
                Console.WriteLine(new JsonObject
                {
                    ["data_dir"] = dataDir,
                    ["port"] = options.PreferredPort,
                    ["port_range"] = options.PortRange,
                    ["active_collection_id"] = store.GetSetting("active_collection_id"),
                }.ToJsonString());
                return 0;
            }

            Console.WriteLine($"data_dir = {dataDir}");
            Console.WriteLine($"active_collection_id = {store.GetSetting("active_collection_id") ?? "（未设置）"}");
            Console.WriteLine($"pairing.port = {options.PreferredPort}（占用时向上探测到 {options.PreferredPort + options.PortRange - 1}）");
            return 0;
        }

    case "setup":
        {
            // 向导：**默认只打印计划（dry-run）**，`--apply` 才落盘。
            // 一台真机上第一次跑，最怕的就是「想看一眼却把环境改了」。
            var port = options.PreferredPort;
            var bind = options.BindAddress;
            var databasePath = Path.Combine(dataDir, "quizsync.db");

            var steps = new List<(string Action, string Detail)>
            {
                ("确保数据目录存在", dataDir),
                ("写入端口与绑定地址", $"pairing.port = {port}；bind = {bind}"),
                ("初始化库（表结构与协议 schema 一致）", databasePath),
            };

            // 随登录启动：只有显式给了开关才进计划（缺省**不动用户的环境**）。
            if (parsed.Autostart == true)
            {
                steps.Add(("安装随登录启动", StartupEntry.Describe(ServerCommand(), ServerEntry(), port, dataDir)));
            }
            else if (parsed.Autostart == false)
            {
                steps.Add(("移除随登录启动", StartupEntry.LauncherPath));
            }

            // 防火墙：**手机连不上时最常见的原因**，而现象只是「一直转圈」。
            // 只读查一次并把结论写进计划；真要加规则得用户显式给 `--firewall`（要管理员权限）。
            var firewall = FirewallCheck.Inspect(port, ServerEntry());
            steps.Add(("检查 Windows 防火墙入站", firewall.Detail));
            if (firewall.FixCommand is null)
            {
                // 已经有规则（或查不出来但也不缺）——不用再提。
            }
            else if (!parsed.Firewall)
            {
                steps.Add(("放行入站端口（要管理员窗口，本向导不自动做）", firewall.FixCommand));
            }
            else if (!parsed.Apply)
            {
                // dry-run **绝不改系统**：加防火墙规则也是改系统，所以只提示怎么加。
                steps.Add(("加入站规则（--firewall 要配 --apply 才会真的加）", firewall.FixCommand));
            }
            else
            {
                var (added, message) = FirewallCheck.TryAdd(port);
                steps.Add((added ? "加入站规则（--firewall）" : "加入站规则失败（--firewall）", message));
            }

            if (parsed.Json)
            {
                var stepsJson = new System.Text.Json.Nodes.JsonArray();
                foreach (var (action, detail) in steps)
                {
                    stepsJson.Add(new JsonObject { ["action"] = action, ["detail"] = detail });
                }

                Console.WriteLine(new JsonObject
                {
                    ["applied"] = parsed.Apply,
                    ["data_dir"] = dataDir,
                    ["port"] = port,
                    ["bind"] = bind,
                    ["steps"] = stepsJson,
                }.ToJsonString());
            }
            else
            {
                Console.WriteLine(parsed.Apply ? "将执行以下步骤：" : "计划（dry-run，加 --apply 才会真的做）：");
                foreach (var (action, detail) in steps)
                {
                    Console.WriteLine($"  · {action}：{detail}");
                }

                if (!parsed.Apply)
                {
                    Console.WriteLine("落盘后请执行：QuizSync.Server.Cli pair   # 生成配对码，供手机扫码");
                }
            }

            if (!parsed.Apply)
            {
                return 0;
            }

            Directory.CreateDirectory(dataDir);
            using (var database = QuizSyncDatabase.Open(databasePath))
            {
                var store = new DeviceRepository(database);
                store.SetSetting("pairing.port", port.ToString(System.Globalization.CultureInfo.InvariantCulture));
                store.SetSetting("pairing.bind", bind);
            }

            // 启动项：写文件可能失败（启动文件夹被重定向、被安全软件锁住…）。
            // **不许把异常直接甩给用户**：实测第一次尝试抛过一次 `WriteAllText` 异常、
            // 之后两次都成功（不可复现）—— 无法复现的错误更要给出人话，否则只剩一段栈。
            try
            {
                if (parsed.Autostart == true)
                {
                    var written = StartupEntry.Install(ServerCommand(), ServerEntry(), port, dataDir);
                    Console.WriteLine($"已安装随登录启动：{written}");
                }
                else if (parsed.Autostart == false)
                {
                    var removed = StartupEntry.Remove();
                    Console.WriteLine(removed
                        ? $"已移除随登录启动：{StartupEntry.LauncherPath}"
                        : "随登录启动本来就没装，无需移除");
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"启动项没做成（{error.GetType().Name}）：{error.Message}");
                Console.Error.WriteLine("其余步骤已完成；可以稍后重试，或手动在启动文件夹里增删。");
            }

            if (!parsed.Json)
            {
                Console.WriteLine($"已就绪：{dataDir}");
                Console.WriteLine("下一步：run 启动服务端；pair 生成配对码。");
            }

            return 0;
        }

    case "doctor":
        {
            var checks = new List<(string Name, bool Ok, string Detail)>
        {
            ("数据目录", true, dataDir),
            ("控制令牌", controlToken.Length == 64, controlToken.Length == 64 ? "已生成（仅本机可用）" : "形态不对"),
        };

            try
            {
                using var database = QuizSyncDatabase.Open(Path.Combine(dataDir, "quizsync.db"));
                var devices = new DeviceRepository(database);
                checks.Add(("本地库", true, $"schema v{V1Schema.SchemaVersion}，设备 {devices.List().Count} 台"));
            }
            catch (Exception ex)
            {
                checks.Add(("本地库", false, ex.Message));
            }

            try
            {
                await using var host = await QuizSyncHost.StartAsync(options);
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                var health = await client.GetFromJsonAsync<JsonElement>($"{host.BaseUrl}/health");
                checks.Add(("监听", true, $"{host.BaseUrl}（/health: {health.GetProperty("status").GetString()}）"));
                checks.Add(("配对码", host.Pairing.Code.Length == 6, host.Pairing.Code));
            }
            catch (Exception ex)
            {
                checks.Add(("监听", false, ex.Message));
            }

            // 「Provider 是否在线」只能问**正在运行的那个实例**：自检自己起的这个 Host 上
            // 必然没有 Provider（它的 /ws 才刚开），问它等于什么都没查。
            // 用 /api/v2/info 的 provider_online —— 那是 v2 的加法字段；v1 的 /info 是冻结契约，不能加。
            // 这一项**永远是 OK**：Provider 随时会连上/断开，doctor 不该因为「现在没人在线」就报失败，
            // 但必须把「没人在线 = 提交任务会立刻 provider_offline」讲清楚。
            if (options.PreferredPort == 0)
            {
                // `--port 0` 是随机端口，没有固定地址可问（自检自己那个实例上必然没有 Provider）。
                checks.Add(("Provider", true, "本次自检用的是随机端口（--port 0），没有可问的实例"));
            }
            else
            {
                try
                {
                    using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                    var live = await probe.GetFromJsonAsync<JsonElement>(
                        $"http://127.0.0.1:{options.PreferredPort}/api/v2/info");
                    var online = live.GetProperty("provider_online").GetBoolean();
                    checks.Add(("Provider", true, online
                        ? $"在线（{live.GetProperty("provider_model").GetString() ?? "未自述模型"}）"
                        : "不在线（提交识别任务会立刻以 provider_offline 失败，请打开电脑端并确认已连接）"));
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
                {
                    checks.Add(("Provider", true,
                        $"没有正在运行的实例可问（{options.PreferredPort} 端口上没回应）—— 先 run 再 doctor 才看得到"));
                }
            }

            var failed = checks.Count(c => !c.Ok);
            if (parsed.Json)
            {
                var array = new JsonArray();
                foreach (var (name, ok, detail) in checks)
                {
                    array.Add(new JsonObject { ["check"] = name, ["ok"] = ok, ["detail"] = detail });
                }

                Console.WriteLine(array.ToJsonString());
            }
            else
            {
                foreach (var (name, ok, detail) in checks)
                {
                    Console.WriteLine($"{(ok ? "OK  " : "FAIL")} {name,-8} {detail}");
                }
            }

            return failed == 0 ? 0 : 2;
        }

    case "run":
        {
            if (parsed.Analyzer is not ("fixture" or "provider"))
            {
                Console.Error.WriteLine($"--analyzer 只认 fixture 或 provider，收到：{parsed.Analyzer}");
                return 1;
            }

            // 识别器的选择**必须显式**：
            // - `provider`（默认）：派发给电脑端的 Provider。没有人配了 AI 或没在线时，任务**立刻失败**
            //   （provider_offline），而不是编一个答案出来；
            // - `fixture`：固定答案的假识别器，**只给联调与演示用**。它永远成功、永远只有 1 道题，
            //   所以绝不能被当成默认 —— 真人拿到的会是「第 1 题（fixture）」这种胡话，且没有任何报错。
            Func<WebSocketHub, IAnalyzer> analyzerFactory = parsed.Analyzer == "fixture"
                ? _ => new FixtureAnalyzer()
                : hub => new ProviderAnalyzer(hub.Providers, SystemClock.Instance, parsed.AnalyzerTimeoutMs);

            try
            {
                await using var host = await QuizSyncHost.StartAsync(
                    options with { ControlToken = controlToken }, null, analyzerFactory);
                Console.WriteLine($"QuizSync Server 已启动：{host.BaseUrl}");
                Console.WriteLine($"协议 v{ServerOptions.ProtocolVersion}（同时在 /api/v1/* 上提供兼容层）");
                Console.WriteLine(parsed.Analyzer == "fixture"
                    ? "识别：fixture（假识别器，固定回 1 道题 —— 仅供联调，别用来验真人流程）"
                    : $"识别：派发给电脑端 Provider（等回包上限 {parsed.AnalyzerTimeoutMs} ms；"
                      + "没有 Provider 在线时任务会立刻以 provider_offline 失败）");
                Console.WriteLine($"配对码：{host.Pairing.Code}（有效期 5 分钟，`pair` 命令可随时再读）");
                Console.WriteLine("按 Ctrl+C 退出。");
                var stop = new TaskCompletionSource();
                Console.CancelKeyPress += (_, e) =>
                {
                    e.Cancel = true;
                    stop.TrySetResult();
                };
                AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.TrySetResult();
                await stop.Task;
                return 0;
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine($"启动失败：{ex.Message}");
                return 2;
            }
        }

    default:
        // 认不出的命令**绝不能**悄悄退回 `run`：主机一跑就占住端口（默认 0.0.0.0:8765，局域网可连）、
        // 打开/创建真实数据目录，而用户以为自己只是敲错了一个字。`help` 就是这么掉进来的（见文件开头）。
        Console.Error.WriteLine($"未知命令：{parsed.Command}（用 `--help` 看用法）");
        return 2;
}

/// <summary>
/// 当前进程是用哪个宿主跑起来的（`dotnet QuizSync.Server.Cli.dll …` 时就是 dotnet.exe）。
/// 写启动项必须用它 —— 直接写 exe 路径会在「用 `dotnet xxx.dll` 启动」的部署里指错东西。
/// </summary>
/// <remarks>
/// ⚠️ 这两个是**顶层语句里的局部函数**，不是 `CliArgs` 的方法：`Program.cs` 用的是顶层语句，
/// `CliArgs` 是文件末尾的类型声明 —— 写进 record 里上面的 `setup` 分支就看不到它们（实测 CS0103）。
/// </remarks>
static string ServerCommand() => Environment.ProcessPath ?? "dotnet";

/// <summary>本 CLI 自己的程序集路径（启动项要执行的就是它）。</summary>
static string ServerEntry() =>
    System.Reflection.Assembly.GetEntryAssembly()?.Location
    ?? Environment.GetCommandLineArgs()[0];

/// <summary>
/// 本机控制令牌：第一次跑时生成并写进数据目录（CLI 与正在跑的实例靠它对话）。
/// 控制面只服务回环地址，所以这个令牌不必联网传输。
/// </summary>
static string ReadOrCreateControlToken(string dataDir)
{
    Directory.CreateDirectory(dataDir);
    var path = Path.Combine(dataDir, "control.token");
    if (File.Exists(path))
    {
        var existing = File.ReadAllText(path).Trim();
        if (existing.Length == 64)
        {
            return existing;
        }
    }

    var token = QuizSync.Server.Core.Tokens.NewToken();
    File.WriteAllText(path, token);
    return token;
}

internal sealed record CliArgs(
    string Command,
    string? Sub,
    string? Arg,
    string? Value,
    int? Port,
    string? Bind,
    string? DataDirectory,
    string Analyzer,
    int AnalyzerTimeoutMs,
    bool Doctor,
    bool Json,
    bool Apply,
    /// <summary>随登录启动：`true` 装、`false` 卸、`null` 不动（setup 缺省）。</summary>
    bool? Autostart,
    /// <summary>`setup --firewall`：真的尝试加防火墙入站规则（**需要管理员权限**，失败会如实报）。</summary>
    bool Firewall,
    bool ShowHelp)
{
    /// <summary>`--analyzer` 的默认值：**派发给 Provider**（生产语义）。</summary>
    public const string DefaultAnalyzer = "provider";

    /// <summary>`--analyzer-timeout` 的默认值 = 协议冻结的 `deadline_ms`。</summary>
    public const int DefaultAnalyzerTimeoutMs = 90_000;

    public const string HelpText = """
        QuizSync Server —— 局域网搜题的 Host（协议 v2，含 v1 兼容层）

        用法：quizsync-server [命令] [选项]

        命令：
          run                 前台运行（默认）
          setup [--apply]     首次配置向导（默认 dry-run，只打印计划）
          doctor              自检：数据目录 / 本地库 / 监听 / 配对码 / Provider 是否在线
          pair                读正在运行的实例的配对码（含配对链接与有效期）
          devices [list]      列出已配对设备
          devices revoke <id> 吊销设备
          config [list]       打印数据目录与端口等配置
          config get <key>    读一个设置项
          config set <k> <v>  写一个设置项
          version             打印版本
          help                显示本帮助

        选项：
          --port <n>      监听端口（默认 8765，被占用时向上探测到 8770）
          --bind <addr>   绑定地址（默认 0.0.0.0，即局域网可连）
          --data <dir>    数据目录（库 / 图片 / 日志 / control.token）
          --analyzer <fixture|provider>
                          识别器（默认 provider）。provider = 把作业派发给电脑端在线的 Provider；
                          **fixture 是固定答案的假识别器，只给联调/演示用** —— 它永远成功、
                          永远只回 1 道题，用真人的图去跑只会拿到「第 1 题（fixture）」这种胡话。
                          所以它只能显式开，绝不作为默认值。
          --analyzer-timeout <ms>
                          等 Provider 回包的上限（默认 90000，即协议 deadline_ms）。
                          超时后任务落 ai_timeout；没有 Provider 在线时**立刻**失败 provider_offline。
          --json          机器可读输出（脚本用）
          --apply         setup 真的落盘（不加则只打印计划）
          --autostart     随用户登录隐藏启动主机（写启动文件夹；不需要管理员）
          --no-autostart  移除上面那个启动项
          --firewall      setup 时**尝试**加入站规则（要管理员权限；不加则只检查并打印命令）
          --doctor        等价于 doctor 命令
          --version       等价于 version 命令
          -h, --help      显示本帮助
        """;

    public static CliArgs Parse(string[] args)
    {
        var positional = new List<string>();
        int? port = null;
        string? bind = null;
        string? data = null;
        var analyzer = DefaultAnalyzer;
        var analyzerTimeoutMs = DefaultAnalyzerTimeoutMs;
        var doctor = false;
        var apply = false;
        var json = false;
        var help = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--port" when i + 1 < args.Length:
                    port = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--bind" when i + 1 < args.Length:
                    bind = args[++i];
                    break;
                case "--data" when i + 1 < args.Length:
                    data = args[++i];
                    break;
                case "--analyzer" when i + 1 < args.Length:
                    analyzer = args[++i];
                    break;
                case "--analyzer-timeout" when i + 1 < args.Length:
                    analyzerTimeoutMs = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--json":
                    json = true;
                    break;
                case "--doctor":
                    doctor = true;
                    break;
                case "--version":
                    positional.Add("version");
                    break;
                case "-h" or "--help":
                    help = true;
                    break;
                default:
                    positional.Add(args[i]);
                    break;
            }
        }

        var command = positional.Count > 0 ? positional[0] : "run";
        if (args.Contains("--apply"))
        {
            apply = true;
        }

        // 随登录启动：`--autostart` 装、`--no-autostart` 卸、都不给就**不碰**（缺省不动用户环境）。
        bool? autostart = null;
        if (args.Contains("--autostart"))
        {
            autostart = true;
        }
        else if (args.Contains("--no-autostart"))
        {
            autostart = false;
        }

        // 防火墙规则：缺省**只检查、只打印命令**（加规则要管理员权限，绝不悄悄提权）。
        var firewall = args.Contains("--firewall");

        if (doctor)
        {
            command = "doctor";
        }

        return new CliArgs(
            Command: command,
            Sub: positional.Count > 1 ? positional[1] : null,
            Arg: positional.Count > 2 ? positional[2] : null,
            Value: positional.Count > 3 ? positional[3] : null,
            Port: port,
            Bind: bind,
            DataDirectory: data,
            Analyzer: analyzer,
            AnalyzerTimeoutMs: analyzerTimeoutMs,
            Doctor: doctor,
            Apply: apply,
            Autostart: autostart,
            Firewall: firewall,
            Json: json,
            ShowHelp: help);
    }
}
