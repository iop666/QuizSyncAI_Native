using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace QuizSync.App.Theme;

/// <summary>
/// 自定义标题栏下「标题栏与内容之间那条 1–2px 缝」的补偿。
///
/// **出处**：WinUI-Gallery 的 <c>MainWindow.xaml.cs</c> L48-L57 + L98-L110，
/// 它注释里指向官方 issue microsoft-ui-xaml#9934。**实测读了那条 issue 的原文**
/// （标题是 "Window close button does not fill with main window"）：说的是
/// <c>ExtendsContentIntoTitleBar = true</c> 之后**标题栏与内容之间会露出窗口底色**。
/// Gallery 的修法就是按窗口状态给它下面那个元素一个负的上边距 —— 最大化 <c>-1</c>、还原 <c>-2</c>。
///
/// **为什么不能照抄到我们的 <c>NavigationView</c> 上**（结构差异，不是偷懒）：
/// Gallery 的 <c>NavigationView</c> **就是应用外壳**（<c>TitleBar</c> 在 Row 0、它在 Row 1，
/// 内容 Frame 是它的 Content），所以补在它身上等于补在「标题栏正下方」；
/// 我们的标题栏下面是一整个 <c>Frame</c>，而 <c>NavigationView</c> 在**设置页里面** ——
/// 补在它身上会把设置页顶进标题栏底下（页面其余部分不动，反而错位）。
/// 所以这里的契约是「补**标题栏正下方**那个元素」，由调用方传进来。
///
/// 哪天我们把 <c>NavigationView</c> 提成应用外壳，把传进来的元素换成它即可。
/// </summary>
internal sealed class TitleBarTrim : IDisposable
{
    private readonly AppWindow _appWindow;
    private readonly OverlappedPresenter _presenter;
    private readonly FrameworkElement _target;
    private OverlappedPresenterState _last;

    private TitleBarTrim(AppWindow appWindow, OverlappedPresenter presenter, FrameworkElement target)
    {
        _appWindow = appWindow;
        _presenter = presenter;
        _target = target;
        _last = presenter.State;
    }

    /// <summary>
    /// 给 <paramref name="belowTitleBar"/> 挂上补偿。窗口的 Presenter 不是
    /// <see cref="OverlappedPresenter"/>（理论上不会）时返回 <c>null</c>，调用方不必特判。
    /// </summary>
    public static TitleBarTrim? Attach(Window window, FrameworkElement belowTitleBar)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(belowTitleBar);

        if (window.AppWindow.Presenter is not OverlappedPresenter presenter)
        {
            return null;
        }

        var trim = new TitleBarTrim(window.AppWindow, presenter, belowTitleBar);
        trim.Apply(force: true);
        window.AppWindow.Changed += trim.OnChanged;
        return trim;
    }

    /// <summary>窗口状态没变就什么都不做（<c>AppWindow.Changed</c> 还会为位置/大小触发）。</summary>
    private void OnChanged(AppWindow sender, AppWindowChangedEventArgs args) => Apply();

    private void Apply(bool force = false)
    {
        if (!force && _presenter.State == _last)
        {
            return;
        }

        _target.Margin = _presenter.State == OverlappedPresenterState.Maximized
            ? new Thickness(0, -1, 0, 0)
            : new Thickness(0, -2, 0, 0);
        _last = _presenter.State;
    }

    /// <summary>页面/窗口销毁时必须调 —— 否则 <c>AppWindow.Changed</c> 会一直握着这个对象。</summary>
    public void Dispose() => _appWindow.Changed -= OnChanged;
}
