using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace QuizSync.Provider;

/// <summary>
/// 系统托盘图标 + 右键菜单。
///
/// **原生实现，不引第三方包**：本仓库的桌面浮层（悬浮球）本来就是手写的 Win32
/// <c>Shell_NotifyIcon</c> 级别的活，托盘这点量不值得再拉一个 NuGet 依赖进来
/// （改依赖要动 csproj，而且要重新评估许可证与维护状态）。
///
/// 两条必须处理的 Win32 现实（不处理就是「用着用着菜单不弹 / 图标没了」）：
/// 1. **弹菜单前要把自己的隐藏窗口设成前台，弹完补一条 <c>WM_NULL</c>** ——
///    否则菜单不会因为点到别处而消失（这是 <c>TrackPopupMenu</c> 的著名怪癖）。
/// 2. **资源管理器重启会清空托盘区**，必须监听 <c>RegisterWindowMessage("TaskbarCreated")</c>
///    并重新 <c>Shell_NotifyIcon(NIM_ADD)</c>，否则用户重启一次 explorer 图标就永远不见了。
///
/// 勾选项：Win32 这边勾选状态是 <c>MF_CHECKED</c> 标志位，必须在**每次弹菜单前**按当前状态重建，
/// 不能只在创建时设一次（1.1.0 那边栽过「用 MenuItem.checked 在 Windows 上没有勾」的坑，
/// 见 AGENTS.md 的 M32 记录 —— 插件只在 `type == "checkbox"` 时才加 <c>MF_CHECKED</c>）。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TrayIcon : IDisposable
{
    private const int WmApp = 0x8000;
    private const int WmCommand = 0x0111;
    private const int WmDestroy = 0x0002;
    private const int WmNull = 0x0000;
    private const int WmRButtonUp = 0x0205;
    private const int WmLButtonUp = 0x0202;
    private const int CallbackMessage = WmApp + 1;

    private const int MfString = 0x0000;
    private const int MfSeparator = 0x0800;
    private const int MfChecked = 0x0008;
    private const int TpmRightButton = 0x0002;
    private const int TpmReturnCmd = 0x0100;

    private const int NimAdd = 0x00000000;
    private const int NimModify = 0x00000001;
    private const int NimDelete = 0x00000002;

    private const int NifMessage = 0x00000001;
    private const int NifIcon = 0x00000002;
    private const int NifTip = 0x00000004;

    private const int ImageIcon = 1;
    private const int LrLoadFromFile = 0x00000010;

    /// <summary>菜单布局见 <see cref="TrayMenu.Layout"/>（纯模型、可单测；这里只负责画出来）。</summary>
    private readonly string _iconPath;
    private readonly Dictionary<TrayCommand, string> _labels = [];
    private readonly HashSet<TrayCommand> _checked = [];
    private readonly WndProcDelegate _wndProc;

    private IntPtr _hwnd;
    private IntPtr _icon;
    private string _tooltip;
    private uint _taskbarCreatedMessage;
    private bool _disposed;

    public TrayIcon(string iconPath, string tooltip)
    {
        _iconPath = iconPath;
        _tooltip = tooltip;
        _wndProc = WindowProc;

        // 默认文案与 §1.4 一致；调用方可以用 SetLabel 换成带热键的动态文案。
        foreach (var command in Enum.GetValues<TrayCommand>())
        {
            _labels[command] = TrayMenu.DefaultLabel(command);
        }
    }

    /// <summary>用户点了某一项。**回调来自窗口过程（UI 线程的消息泵）**，可以直接动界面。</summary>
    public event Action<TrayCommand>? CommandInvoked;

    /// <summary>左键单击托盘图标（1.1.0 的行为是「显示主窗口」）。</summary>
    public event Action? LeftClicked;

    /// <summary>改某一项的文案（例如带上当前生效的热键）。</summary>
    public void SetLabel(TrayCommand command, string label) => _labels[command] = label;

    /// <summary>改勾选项的状态。**下次弹菜单时生效**（菜单每次都重建）。</summary>
    public void SetChecked(TrayCommand command, bool isChecked)
    {
        if (isChecked)
        {
            _checked.Add(command);
        }
        else
        {
            _checked.Remove(command);
        }
    }

    /// <summary>改托盘提示文字（1.1.0 的提示里会带上生效热键与已暂存页数）。</summary>
    public void UpdateTooltip(string tooltip)
    {
        _tooltip = tooltip;
        if (_hwnd != IntPtr.Zero)
        {
            Notify(NimModify);
        }
    }

    /// <summary>建隐藏窗口并把图标挂进托盘区。失败抛异常（由调用方写进窗口标题，不静默）。</summary>
    public void Show()
    {
        var module = GetModuleHandle(null);
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

        var wndClass = new WndClassEx
        {
            cbSize = Marshal.SizeOf<WndClassEx>(),
            lpfnWndProc = _wndProc,
            hInstance = module,
            lpszClassName = ClassName,
        };

        // 类名可能已经注册过（重建托盘时），失败不当作致命错误。
        _ = RegisterClassEx(ref wndClass);

        // 隐藏窗口：托盘回调需要一个能收消息的窗口，但它永远不显示。
        _hwnd = CreateWindowEx(0, ClassName, "AI 双端搜题 · 托盘", 0, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, module, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException($"托盘窗口创建失败（{Marshal.GetLastWin32Error()}）");
        }

        // 按**系统小图标尺寸**取图，而不是默认尺寸：托盘图标该用 SM_CXSMICON，
        // 传 0,0 拿到的可能是不合适的档位（本机的 .ico 里带了 16/20/24/32/40/48/64/128/256 九档）。
        var small = GetSystemMetrics(49 /* SM_CXSMICON */);
        _icon = LoadImage(IntPtr.Zero, _iconPath, ImageIcon, small, small, LrLoadFromFile);
        if (_icon == IntPtr.Zero)
        {
            // 图标文件缺失不该让托盘整个不可用：退回系统默认图标，用户至少还能右键。
            _icon = LoadIcon(IntPtr.Zero, new IntPtr(32512) /* IDI_APPLICATION */);
        }

        Notify(NimAdd);
    }

    private void Notify(int message)
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        var data = new NotifyIconData
        {
            cbSize = Marshal.SizeOf<NotifyIconData>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NifMessage | NifIcon | NifTip,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon,
            szTip = _tooltip.Length > 127 ? _tooltip[..127] : _tooltip,
            szInfo = string.Empty,
            szInfoTitle = string.Empty,
        };

        _ = Shell_NotifyIcon(message, ref data);
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            for (var i = 0; i < TrayMenu.Layout.Count; i++)
            {
                var command = TrayMenu.Layout[i];
                var flags = command is null ? MfSeparator : MfString;
                if (command is { } checkedCommand && _checked.Contains(checkedCommand))
                {
                    flags |= MfChecked;
                }

                var label = command is { } textCommand
                    ? _labels.GetValueOrDefault(textCommand, textCommand.ToString())
                    : null;

                // 命令 id 用「下标 + 1」，这样同一个枚举值在菜单里的位置变了也不会串。
                _ = AppendMenu(menu, (uint)flags, (nuint)(i + 1), label);
            }

            GetCursorPos(out var cursor);

            // 这两行是 TrackPopupMenu 的必修课：不设前台，菜单点到别处不会消失；
            // 弹完补一条 WM_NULL，否则下一次右键要点两下才出菜单。
            _ = SetForegroundWindow(_hwnd);
            var chosen = TrackPopupMenuEx(menu, TpmRightButton | TpmReturnCmd, cursor.X, cursor.Y, _hwnd, IntPtr.Zero);
            _ = PostMessage(_hwnd, WmNull, IntPtr.Zero, IntPtr.Zero);

            // 用 TpmReturnCmd 让菜单**直接返回**选中的 id（而不是给窗口发 WM_COMMAND）。
            // 两条路都接上：返回值走这里，外部（测试 / 无障碍工具 / 快捷键）发的 WM_COMMAND 走 WindowProc。
            Dispatch(chosen);
        }
        finally
        {
            _ = DestroyMenu(menu);
        }
    }

    /// <summary>
    /// 派发一个菜单项 id。映射本身在 <see cref="TrayMenu.CommandForMenuId"/>（纯函数，有单测），
    /// 这里只负责把结果转成事件。
    /// </summary>
    private void Dispatch(int menuId)
    {
        if (TrayMenu.CommandForMenuId(menuId) is { } command)
        {
            CommandInvoked?.Invoke(command);
        }
    }

    private IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        // 资源管理器重启 → 托盘区被清空 → 必须重新挂图标（否则图标永远消失）。
        if (_taskbarCreatedMessage != 0 && message == _taskbarCreatedMessage)
        {
            Notify(NimAdd);
            return IntPtr.Zero;
        }

        switch (message)
        {
            case CallbackMessage:
                var mouse = (int)(lParam.ToInt64() & 0xFFFF);
                if (mouse == WmLButtonUp)
                {
                    LeftClicked?.Invoke();
                }
                else if (mouse == WmRButtonUp)
                {
                    ShowMenu();
                }

                return IntPtr.Zero;

            // 菜单本身走 TrackPopupMenuEx 的返回值，但**也要接 WM_COMMAND**：
            // 测试、无障碍工具、以及将来可能加的快捷键都会从这条路进来。
            case WmCommand:
                Dispatch((int)(wParam.ToInt64() & 0xFFFF));
                return IntPtr.Zero;

            case WmDestroy:
                return IntPtr.Zero;
        }

        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_hwnd != IntPtr.Zero)
        {
            Notify(NimDelete);
            _ = DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        if (_icon != IntPtr.Zero)
        {
            _ = DestroyIcon(_icon);
            _icon = IntPtr.Zero;
        }

        GC.SuppressFinalize(this);
    }

    private const string ClassName = "QuizSyncTrayWindow";

    // ===================================================================
    // Win32
    // ===================================================================

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public uint dwState;
        public uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public uint uVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public int cbSize;
        public uint style;
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointStruct
    {
        public int X;
        public int Y;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WndClassEx wndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadImage(IntPtr instance, string name, int type, int cx, int cy, int load);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, nuint id, string? item);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr menu, int flags, int x, int y, IntPtr hwnd, IntPtr parameters);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out PointStruct point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);
}
