using System.Diagnostics;

namespace QuizSync.Server.Core.Net;

/// <summary>防火墙检查结果：`null` = 查不出来（不是 Windows / netsh 不在 / 命令失败）。</summary>
public sealed record FirewallStatus(
    /// <summary>有没有一条**入站放行**的规则在管这个端口或这个程序。</summary>
    bool? Allowed,
    /// <summary>判据里到底看到了什么（给用户和日志看，别让他猜）。</summary>
    string Detail,
    /// <summary>缺规则时该复制的那条命令（普通 PowerShell 窗口跑不了，要管理员窗口）。</summary>
    string? FixCommand);

/// <summary>
/// Windows 防火墙入站规则的**只读检查**（+ 一条给用户复制的命令）。
///
/// 为什么要它：手机连不上电脑时，**最常见的原因就是防火墙拦了入站**，而现象是
/// 「配对页一直转圈 / 连接超时」—— 用户完全看不出是防火墙。`setup` 向导里说一句
/// 「端口有没有被放行」能省掉一整轮排查。
///
/// **为什么不自动加规则**：`netsh advfirewall ... add rule` 必须管理员权限，
/// 而本项目的一条硬规矩是「不要求管理员权限」（`AGENTS.md` §0.6 第 6 条）。
/// 所以默认只**查**并打印命令；用户显式给 `--firewall` 时才尝试，失败也如实说原因。
///
/// 判据为什么用 **netsh 的退出码 + 路径文本**，而不是输出里的字段名：
/// netsh 的输出是**本地化**的（中文 Windows 上是「规则名称」「程序」），
/// 按字段名解析会在中文系统上直接失效。退出码与「我们自己的 exe 路径」都不受语言影响。
/// </summary>
public static class FirewallCheck
{
    /// <summary>我们自己创建的规则名（固定前缀 + 端口，便于用户一眼认出）。</summary>
    public static string RuleName(int port) => $"QuizSync AI Host {port}";

    /// <summary>要复制到**管理员** PowerShell / cmd 里跑的那条命令。</summary>
    public static string AddCommand(int port) =>
        $"netsh advfirewall firewall add rule name=\"{RuleName(port)}\" dir=in action=allow protocol=TCP localport={port}";

    /// <summary>移除规则（不留痕时用）。</summary>
    public static string RemoveCommand(int port) =>
        $"netsh advfirewall firewall delete rule name=\"{RuleName(port)}\"";

    /// <summary>```netsh show rule``` 的退出码为 0 且输出里有这条规则名 → 规则在。</summary>
    public static bool HasRule(string output, int exitCode, string ruleName) =>
        exitCode == 0 && output.Contains(ruleName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 输出里提到了这个程序路径 → 有一条规则是**按程序**放行的（Windows 弹「允许访问」时
    /// 生成的就是这种：`Program: ...\QuizSync.Server.Cli.exe`）。
    /// 只看我们自己的路径文本，因此不受输出语言影响。
    /// </summary>
    public static bool MentionsProgram(string output, string programPath) =>
        !string.IsNullOrWhiteSpace(programPath)
        && output.Contains(programPath, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 查一次：先看我们自己那条端口规则，再看有没有按程序放行的规则。
    /// `programPath` 传**实际会跑的那个 exe**（没有就用 dll 的目录）。
    /// </summary>
    public static FirewallStatus Inspect(int port, string? programPath = null)
    {
        var ruleName = RuleName(port);
        var byRule = Run($"advfirewall firewall show rule name=\"{ruleName}\"");
        if (HasRule(byRule.Output, byRule.ExitCode, ruleName))
        {
            return new FirewallStatus(true, $"已有入站规则「{ruleName}」（TCP {port}）", null);
        }

        // Windows 的「允许应用通过防火墙」弹窗是按**程序**建规则的，用户很可能已经点过「允许」。
        // 那种情况下不该再让他加一条按端口的规则。
        if (!string.IsNullOrWhiteSpace(programPath))
        {
            var byProgram = Run("advfirewall firewall show rule name=all verbose");
            if (MentionsProgram(byProgram.Output, programPath))
            {
                return new FirewallStatus(
                    true,
                    $"没有按端口的规则，但防火墙里有按程序放行的规则（{programPath}）—— 通常已经够用",
                    null);
            }

            if (byProgram.ExitCode != 0)
            {
                return new FirewallStatus(
                    null,
                    $"查不出来：netsh 退出码 {byProgram.ExitCode}（不是 Windows，或防火墙服务没起来）",
                    AddCommand(port));
            }
        }

        return new FirewallStatus(
            false,
            $"没有找到放行 TCP {port} 的入站规则；手机连不上时**先查这里**",
            AddCommand(port));
    }

    /// <summary>
    /// 尝试加规则（只在用户显式要求时调用）。**没提权就会失败** —— 如实返回原因，不假装成功。
    /// </summary>
    public static (bool Ok, string Message) TryAdd(int port)
    {
        var result = Run($"advfirewall firewall add rule name=\"{RuleName(port)}\" dir=in action=allow protocol=TCP localport={port}");
        if (result.ExitCode == 0)
        {
            return (true, $"已加入站规则「{RuleName(port)}」（TCP {port}）");
        }

        var hint = result.Output.Contains("elevat", StringComparison.OrdinalIgnoreCase)
                   || result.Output.Contains("administrator", StringComparison.OrdinalIgnoreCase)
                   || result.Output.Contains("提升", StringComparison.Ordinal)
                   || result.Output.Contains("管理员", StringComparison.Ordinal)
            ? "需要**管理员**权限"
            : $"netsh 退出码 {result.ExitCode}";

        return (false, $"{hint}，没有加成。请用管理员窗口执行：{AddCommand(port)}");
    }

    private static (int ExitCode, string Output) Run(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("netsh", arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null)
            {
                return (-1, "netsh 起不来");
            }

            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit(15000);
            return (process.HasExited ? process.ExitCode : -1, output);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return (-1, $"netsh 调用失败：{error.Message}");
        }
    }
}
