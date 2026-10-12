using System.Diagnostics;
using System.Text.Json.Nodes;
using Xunit;

namespace QuizSync.Server.Tests;

/// <summary>
/// `setup` 向导：**默认 dry-run（只打印计划）**，`--apply` 才落盘。
///
/// 一台真机上第一次跑，最怕的就是「想看一眼却把环境改了」—— 所以默认不写，
/// 且落盘动作要幂等（跑两次不该坏）。CLI 一律当**独立进程**跑（参数解析与工作目录
/// 是最容易出问题的地方，只调函数测不出来）。
/// </summary>
public sealed class SetupWizardTests
{
    /// <summary>
    /// CLI 产物就在测试输出目录里（测试项目直接引用了 CLI 工程，构建顺序有保证）。
    /// 之前按路径往上找 in/{Release,Debug} —— 那条路找到的是**陈旧二进制**：
    /// 改了 CLI 却没重建它，用例就会拿旧行为判失败（实测踩过）。
    /// </summary>
    private static string CliDll()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "QuizSync.Server.Cli.dll");
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException($"测试输出目录里没有 CLI 产物：{path}");
    }

    /// <summary>
    /// 优先 `QS_DOTNET`（本机 SDK 装在用户目录、没进 PATH 时用），否则退化到 PATH 里的 `dotnet`
    /// —— CI 上 `setup-dotnet` 已经把 dotnet 放进 PATH 了，那里没有 QS_DOTNET。
    /// （最初写成「必须有 QS_DOTNET」，于是本地绿、CI 红：5 条用例全在这个变量上崩。）
    /// </summary>
    private static string Dotnet()
    {
        var configured = Environment.GetEnvironmentVariable("QS_DOTNET");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        try
        {
            using var probe = Process.Start(new ProcessStartInfo("dotnet")
            {
                ArgumentList = { "--version" },
                RedirectStandardOutput = true,
            })!;
            probe.WaitForExit(5000);
            return "dotnet";
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException(
                "PATH 里找不到 dotnet，也没设置 QS_DOTNET —— 两条路至少给一条。", error);
        }
    }

    private static (string Output, int ExitCode) RunCli(params string[] args)
    {
        var info = new ProcessStartInfo(Dotnet())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // 明确按 UTF-8 读：CLI 的中文输出走 UTF-8，别让测试按控制台代码页解。
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        info.ArgumentList.Add(CliDll());
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(30_000);
        return (output, process.ExitCode);
    }

    [Fact]
    public void Dry_run_prints_the_plan_and_changes_nothing()
    {
        var dataDir = Path.Combine(Directory.CreateTempSubdirectory("qs-setup-dry-").FullName, "新目录");

        var (output, code) = RunCli("setup", "--data", dataDir);

        Assert.Equal(0, code);
        Assert.Contains("dry-run", output, StringComparison.Ordinal);
        Assert.Contains("配对码", output, StringComparison.Ordinal);
        // 关键：dry-run **不能**建目录、不能建库。
        Assert.False(Directory.Exists(dataDir), "dry-run 竟然创建了数据目录");
    }

    [Fact]
    public void Setup_checks_the_firewall_and_never_touches_it_in_dry_run()
    {
        // ⚠️ 必须是**还不存在**的子目录：`CreateTempSubdirectory` 自己会把那个目录建出来，
        // 直接拿它当 dataDir 的话，下面那句"dry-run 没建目录"就成了**断言测试自己刚建的目录不存在**
        // —— 无论 CLI 怎么做都是红的（第十六轮我加这条用例时就抄漏了 `Path.Combine(…, "新目录")`，
        // 与上面 `Dry_run_prints_the_plan_and_changes_nothing` 的写法对齐才是本意）。
        var dataDir = Path.Combine(Directory.CreateTempSubdirectory("qs-setup-fw-").FullName, "新目录");

        // dry-run + --firewall：**只提示、绝不动系统**（加规则也是改系统）。
        var (output, code) = RunCli("setup", "--data", dataDir, "--port", "8898", "--firewall");

        Assert.Equal(0, code);
        Assert.Contains("检查 Windows 防火墙入站", output, StringComparison.Ordinal);
        Assert.Contains("--apply", output, StringComparison.Ordinal); // 提示要配 --apply 才会真的加
        Assert.False(Directory.Exists(dataDir), "dry-run 竟然创建了数据目录");
    }

    [Fact]
    public void Help_mentions_the_firewall_switch()
    {
        var (output, code) = RunCli("help");

        Assert.Equal(0, code);
        Assert.Contains("--firewall", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// 认不出的命令必须**报错退出**，绝不能悄悄退回 `run` 把主机起起来。
    ///
    /// 现场（2026-09-28）：`help` 没有 case，掉进了与 `run` 共用的 `default:` —— 于是
    /// `Help_mentions_the_firewall_switch` 的子进程在 8765 上真起了一台主机（绑 `0.0.0.0`、
    /// 动真实数据目录），而 [RunCli] 用 `StandardOutput.ReadToEnd()` 等 EOF → **整轮测试挂死**，
    /// 表现是"某个 testhost 进程几十分钟不结束"，而不是一条红的用例。
    ///
    /// 这条用例只能证明"报错退出"这一半：真起了主机的话它会**挂住**而不是断言失败
    /// （`ReadToEnd` 等不到 EOF）—— 那本身就是这个 bug 的症状，没有更好的写法。
    /// </summary>
    [Fact]
    public void Unknown_command_reports_an_error_instead_of_starting_a_host()
    {
        var (output, code) = RunCli("definitely-not-a-command");

        Assert.NotEqual(0, code);
        Assert.Contains("未知命令", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_creates_the_data_directory_and_writes_settings()
    {
        var dataDir = Directory.CreateTempSubdirectory("qs-setup-apply-").FullName;

        var (output, code) = RunCli("setup", "--apply", "--data", dataDir, "--port", "8899");

        Assert.Equal(0, code);
        Assert.Contains("已就绪", output, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(dataDir, "quizsync.db")), "库没建出来");

        // 端口确实写进了设置（用 config get 读回来，走的是另一条命令路径）。
        var (portOut, portCode) = RunCli("config", "get", "pairing.port", "--data", dataDir);
        Assert.Equal(0, portCode);
        Assert.Contains("8899", portOut, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_is_idempotent()
    {
        var dataDir = Directory.CreateTempSubdirectory("qs-setup-twice-").FullName;

        var first = RunCli("setup", "--apply", "--data", dataDir, "--port", "8901");
        var second = RunCli("setup", "--apply", "--data", dataDir, "--port", "8901");

        Assert.Equal(0, first.ExitCode);
        Assert.Equal(0, second.ExitCode);
        Assert.Contains("已就绪", second.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Json_output_is_machine_readable()
    {
        var dataDir = Directory.CreateTempSubdirectory("qs-setup-json-").FullName;

        var (output, code) = RunCli("setup", "--data", dataDir, "--json", "--port", "8902");

        Assert.Equal(0, code);
        var json = JsonNode.Parse(output.Substring(output.IndexOf('{', StringComparison.Ordinal)))!;
        Assert.False(json["applied"]!.GetValue<bool>());
        Assert.Equal(8902, json["port"]!.GetValue<int>());
        Assert.True(json["steps"]!.AsArray().Count >= 3);
    }

    [Fact]
    public void Help_lists_the_setup_command()
    {
        var (output, code) = RunCli("--help");
        Assert.Equal(0, code);
        Assert.Contains("setup", output, StringComparison.Ordinal);
        Assert.Contains("--apply", output, StringComparison.Ordinal);
    }
}
