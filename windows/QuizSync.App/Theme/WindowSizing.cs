using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace QuizSync.App.Theme;

/// <summary>
/// 主窗口的尺寸策略。1.1.0 是 `desktop/lib/main.dart:198,205`：
/// 默认 <c>Size(1180, 760)</c>、最小 <c>Size(860, 520)</c>，
/// 并且**尺寸是逻辑像素**（window_manager 会乘 devicePixelRatio）。
///
/// WinUI 3 这边：
/// * <see cref="Microsoft.UI.Windowing.AppWindow.Resize"/> 收的是**物理**像素，所以按窗口 DPI 换算一次；
/// * 「最小尺寸」WinUI **没有**对应 API，只能拦 <c>WM_GETMINMAXINFO</c>。
///   这里用 comctl32 的 <c>SetWindowSubclass</c>（而不是自己换 WNDPROC）——
///   换 WNDPROC 要把原过程存下来再手工转发，漏一条消息就是一类诡异 bug；
///   <c>DefSubclassProc</c> 会替我们把没处理的消息传下去。
/// </summary>
internal static class WindowSizing
{
    private const int WmGetMinMaxInfo = 0x0024;

    public static void Apply(Window window, int width, int height, int minWidth, int minHeight)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var scale = GetDpiForWindow(hwnd) / 96.0;

        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(
            Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd));

        appWindow.Resize(new SizeInt32(
            (int)Math.Round(width * scale),
            (int)Math.Round(height * scale)));

        _minWidth = (int)Math.Round(minWidth * scale);
        _minHeight = (int)Math.Round(minHeight * scale);
        _subclass = SubclassProc;
        SetWindowSubclass(hwnd, _subclass, 1, 0);
    }

    private static int _minWidth;
    private static int _minHeight;
    private static SubclassProcDelegate? _subclass;

    private static nint SubclassProc(nint hWnd, uint message, nint wParam, nint lParam, nuint id, nint data)
    {
        if (message == WmGetMinMaxInfo && lParam != 0)
        {
            var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            info.MinTrackSize.X = _minWidth;
            info.MinTrackSize.Y = _minHeight;
            Marshal.StructureToPtr(info, lParam, fDeleteOld: false);
        }

        return DefSubclassProc(hWnd, message, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point Reserved;
        public Point MaxSize;
        public Point MaxPosition;
        public Point MinTrackSize;
        public Point MaxTrackSize;
    }

    private delegate nint SubclassProcDelegate(nint hWnd, uint message, nint wParam, nint lParam, nuint id, nint data);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(nint hWnd, SubclassProcDelegate callback, nuint id, nint data);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hWnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hWnd);
}
