using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using QuizSync.Provider;

namespace QuizSync.App;

/// <summary>
/// 托盘的接线：把 <see cref="TrayIcon"/> 的命令接到真实动作上。
///
/// 菜单结构与文案逐条照 1.1.0（ui-spec-desktop §1.4 / <c>desktop/lib/main.dart:677-714</c>）。
/// **这一版每一项都真的接线了**（多页、清空暂存、合集弹窗、剪贴板、悬浮窗、退出）——
/// 原来这句写的是「有几项还没有对应功能」，那是早期状态，已经过期。
/// `default:` 那一支仍然保留：将来加菜单项时**必须**由这里显式接上，
/// 落进去就等于「点下去没反应」，所以它宁可说一句「还没接上」。
/// </summary>
internal sealed class TrayController : IDisposable
{
    private readonly TrayIcon _tray;

    internal TrayController(string iconPath)
    {
        _tray = new TrayIcon(iconPath, AppWindowScope.Title);
        _tray.CommandInvoked += OnCommand;
        // 左键单击 = 显示主窗口（1.1.0 的行为）。
        _tray.LeftClicked += () => ShowMainWindow();
    }

    internal void Show() => _tray.Show();

    /// <summary>
    /// 刷新菜单上的动态文案与勾选状态。**每次状态变化后都要调**：
    /// 托盘提示里带生效热键与已暂存页数，勾选项要反映悬浮球当前是开还是关
    /// （1.1.0 的文案就是 `有暂存：结束多页并识别 (...)` / `隐藏悬浮球` 这种随状态变的形式）。
    /// </summary>
    internal void Refresh(string? hotkey, int stagedPages, int stagedLimit, bool ballEnabled)
    {
        var capture = string.IsNullOrWhiteSpace(hotkey) ? "无可用热键" : hotkey!;

        _tray.SetLabel(TrayCommand.Capture,
            stagedPages > 0 ? $"结束多页并识别 ({capture})" : $"截取屏幕并识别 ({capture})");
        _tray.SetLabel(TrayCommand.MultiPage,
            string.IsNullOrWhiteSpace(hotkey) ? "多页识别（热键被占用）" : $"多页识别 ({capture})");
        _tray.SetLabel(TrayCommand.ClearStaging, $"清空多页暂存区（{stagedPages} 页）");
        _tray.SetLabel(TrayCommand.ToggleBall, ballEnabled ? "隐藏悬浮球" : "显示悬浮球");

        // 勾选状态：勾 = 「当前是开着的」。
        _tray.SetChecked(TrayCommand.ToggleBall, ballEnabled);

        _tray.UpdateTooltip(stagedPages > 0
            ? $"{AppWindowScope.Title} — {capture} 截屏搜题 · 已暂存 {stagedPages} 页"
            : $"{AppWindowScope.Title} — {capture} 截屏搜题");
    }

    private void OnCommand(TrayCommand command)
    {
        switch (command)
        {
            case TrayCommand.Capture:
                App.RequestCapture();
                break;

            case TrayCommand.ToggleBall:
                App.ToggleFloatingBall();
                break;

            case TrayCommand.ShowWindow:
                ShowMainWindow();
                break;

            case TrayCommand.Settings:
                ShowMainWindow();
                App.NavigateTo(typeof(Pages.SettingsPage));
                break;

            case TrayCommand.Exit:
                Application.Current.Exit();
                break;

            // 多页两项从这一轮起是真的（与 F9 / 悬浮球长按 / 暂存条同一条入口）。
            case TrayCommand.MultiPage:
                App.RequestMultipage();
                break;

            case TrayCommand.ClearStaging:
                App.ClearStaging();
                break;

            // 「切换任务合集」也从这一轮起是真的：叫出窗口 + 开合集弹窗（与顶栏药丸同一条入口）。
            case TrayCommand.SwitchCollection:
                App.RequestSwitchCollection();
                break;

            // 「从剪贴板读取」也从这一轮起是真的：读剪贴板里的图 → 走同一条分析链。
            case TrayCommand.Clipboard:
                App.RequestClipboard();
                break;

            // 「显示/隐藏悬浮窗」：与设置页那个开关**同一条路径**（`App.ToggleFloatWindow`），
            // 两边各写一份必然出现「托盘说开着、设置页说关着」。
            case TrayCommand.ToggleFloatWindow:
                App.ToggleFloatWindow();
                break;

            default:
                App.Note($"托盘项「{command}」还没接上");
                break;
        }
    }

    private static string Label(TrayCommand command) => command switch
    {
        TrayCommand.MultiPage => "多页识别",
        TrayCommand.ClearStaging => "清空多页暂存区",
        TrayCommand.Clipboard => "从剪贴板读取",
        TrayCommand.ToggleFloatWindow => "显示/隐藏悬浮窗",
        TrayCommand.SwitchCollection => "切换任务合集",
        _ => command.ToString(),
    };

    private static void ShowMainWindow()
    {
        if (App.MainWindowInstance is not { } window)
        {
            return;
        }

        window.Activate();
    }

    public void Dispose()
    {
        _tray.CommandInvoked -= OnCommand;
        _tray.Dispose();
    }
}
