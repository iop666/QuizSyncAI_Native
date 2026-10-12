using QuizSync.Server.Core.Net;
using Xunit;

namespace QuizSync.Server.Tests;

/// <summary>
/// 防火墙入站检查（`setup` 向导里的那一步）。
///
/// 判据**不能依赖 netsh 输出的字段名**：那是本地化的，中文 Windows 上是「规则名称」「程序」，
/// 按字段名解析会在中文系统上直接失效。这里钉住的是「只用退出码 + 我们自己的路径文本」这条口径，
/// 以及三条容易做错的边界：
/// ① 已经有按**程序**放行的规则（Windows 弹窗生成的）时，不该再让用户加一条按端口的规则；
/// ② netsh 跑不起来（不是 Windows / 服务没起）时如实说「查不出来」，**不是**「没有规则」；
/// ③ `--firewall` 没提权失败时，要把该复制的那条命令原样给出来。
/// </summary>
public sealed class FirewallCheckTests
{
    [Fact]
    public void 按端口规则在不在只看退出码与规则名()
    {
        // 英文输出
        Assert.True(FirewallCheck.HasRule(
            "Rule Name:                            QuizSync AI Host 8765\r\nEnabled: Yes", 0, "QuizSync AI Host 8765"));
        // 中文输出（字段名完全不同，但规则名是我们自己的字面量）
        Assert.True(FirewallCheck.HasRule(
            "规则名称:                             QuizSync AI Host 8765\r\n已启用: 是", 0, "QuizSync AI Host 8765"));
        // 没有规则：netsh 退出码 1（实测），输出是本地化的「没有与指定标准相符的规则」
        Assert.False(FirewallCheck.HasRule("No rules match the specified criteria.", 1, "QuizSync AI Host 8765"));
        Assert.False(FirewallCheck.HasRule("没有与指定标准相符的规则。", 1, "QuizSync AI Host 8765"));
        // 退出码 0 但名字对不上（比如 show rule name=all）也不算
        Assert.False(FirewallCheck.HasRule("Rule Name: aDrive", 0, "QuizSync AI Host 8765"));
    }

    [Fact]
    public void 按程序放行的规则靠路径文本识别()
    {
        const string output = """
            Rule Name:                            QuizSync.Server.Cli
            ----------------------------------------------
            Enabled:                              Yes
            Direction:                            In
            Program:                              D:\ZCode\QuizSyncServer\src\QuizSync.Server.Cli\bin\Release\publish\QuizSync.Server.Cli.exe
            Action:                               Allow
            """;

        Assert.True(FirewallCheck.MentionsProgram(output, @"D:\ZCode\QuizSyncServer\src\QuizSync.Server.Cli\bin\Release\publish\QuizSync.Server.Cli.exe"));
        // 大小写不敏感（Windows 路径本身就不区分）
        Assert.True(FirewallCheck.MentionsProgram(output, @"d:\zcode\quizsyncserver\src\quizsync.server.cli\bin\release\publish\quizsync.server.cli.exe"));
        // 另一条路径（Debug 目录）不算命中 —— 这正是「跑的是哪个程序」必须传实际路径的原因
        Assert.False(FirewallCheck.MentionsProgram(output, @"D:\ZCode\QuizSyncServer\src\QuizSync.Server.Cli\bin\Debug\net10.0\QuizSync.Server.Cli.dll"));
        // 空路径不算命中（别把「没传」当成「有规则」）
        Assert.False(FirewallCheck.MentionsProgram(output, ""));
    }

    [Fact]
    public void 修复命令是可直接复制的netsh命令()
    {
        var command = FirewallCheck.AddCommand(8765);

        Assert.Contains("netsh advfirewall firewall add rule", command, StringComparison.Ordinal);
        Assert.Contains(FirewallCheck.RuleName(8765), command, StringComparison.Ordinal);
        Assert.Contains("localport=8765", command, StringComparison.Ordinal);
        Assert.Contains("dir=in", command, StringComparison.Ordinal);
        Assert.Contains("action=allow", command, StringComparison.Ordinal);
        // 规则名要能一眼认出是本项目的
        Assert.StartsWith("QuizSync AI Host", FirewallCheck.RuleName(8765), StringComparison.Ordinal);
    }

    [Fact]
    public void 本机可以查一次且结论有出处()
    {
        // 真跑一次 netsh（本机是 Windows）：允许「查不出来」，但必须给出理由文本，
        // **不许**在没有证据时返回「已放行」。
        var status = FirewallCheck.Inspect(8765, programPath: null);

        Assert.False(string.IsNullOrWhiteSpace(status.Detail));
        if (status.Allowed is null)
        {
            Assert.Contains("查不出来", status.Detail, StringComparison.Ordinal);
        }
        else if (status.Allowed is false)
        {
            // 缺规则时必须把命令给出来，否则用户不知道下一步做什么
            Assert.NotNull(status.FixCommand);
        }
    }
}
