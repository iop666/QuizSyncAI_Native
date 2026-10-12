using QuizSync.Provider;
using Xunit;

namespace QuizSync.Tests;

/// <summary>
/// 托盘菜单的**映射与文案**。这两块是托盘中唯一能被纯逻辑钉住的部分，
/// 而它们恰好也是最容易错的部分：
/// * id → 命令的映射是「布局下标 + 1」的手写规则，差一位的表现是「点了 A 执行了 B」，
///   在界面上极难发现（托盘菜单又不方便自动化点击）；
/// * 文案随状态变（热键被占用、有没有暂存），错了用户会看到自相矛盾的两处提示。
/// </summary>
public sealed class TrayIconTests
{
    /// <summary>
    /// 菜单布局（ui-spec-desktop §1.4 的 13 项表，null = 分隔线）。
    /// 这里**故意把期望值抄一份**：这个用例的作用就是「布局被改动时有人被叫醒」——
    /// 如果只是从实现里读出来再比一遍，那它永远是绿的，等于没测。
    /// </summary>
    private static readonly TrayCommand?[] ExpectedLayout =
    [
        TrayCommand.Capture,
        TrayCommand.MultiPage,
        TrayCommand.ClearStaging,
        TrayCommand.Clipboard,
        null,                            // 分隔线
        TrayCommand.ToggleBall,
        TrayCommand.ToggleFloatWindow,
        null,                            // 分隔线
        TrayCommand.SwitchCollection,
        TrayCommand.ShowWindow,
        TrayCommand.Settings,
        null,                            // 分隔线
        TrayCommand.Exit,
    ];

    [Fact]
    public void 菜单项数与顺序与_1_1_0_的_13_项表一致()
    {
        for (var i = 0; i < ExpectedLayout.Length; i++)
        {
            var menuId = i + 1;
            Assert.Equal(ExpectedLayout[i], TrayMenu.CommandForMenuId(menuId));
        }
    }

    [Fact]
    public void 分隔线不产生命令()
    {
        // 下标 4 / 7 / 11（即 id 5 / 8 / 12）是分隔线。
        Assert.Null(TrayMenu.CommandForMenuId(5));
        Assert.Null(TrayMenu.CommandForMenuId(8));
        Assert.Null(TrayMenu.CommandForMenuId(12));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(14)]
    [InlineData(999)]
    public void 越界的菜单_id_不会误触发命令(int menuId) =>
        Assert.Null(TrayMenu.CommandForMenuId(menuId));

    [Fact]
    public void 第一项是截屏识别最后一项是退出()
    {
        // 顺序在界面上是硬要求（§1.4：第 1 项截屏、第 13 项退出），单独钉一条更醒目的。
        Assert.Equal(TrayCommand.Capture, TrayMenu.CommandForMenuId(1));
        Assert.Equal(TrayCommand.Exit, TrayMenu.CommandForMenuId(13));
    }
}
