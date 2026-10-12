using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace QuizSync.App.Theme;

/// <summary>
/// 主窗口的系统背板（Mica / 亚克力）。**2.0 起用原生材质**，原来是纯色画布。
///
/// 三点依据（都来自 1.7.250606001 包内自带的官方 XML 文档，不是网上二手说法）：
/// * <c>MicaController.IsSupported()</c> 的摘要原文是
///   "Determines whether the mica material is supported on the **current operating system**"
///   —— 所以这是**系统**判据、**与打包方式无关**。我们要兜底的真正原因是
///   <c>TargetPlatformMinVersion=10.0.19041.0</c>（Win10 上 Mica 不支持），不是「未打包」。
/// * <c>Window.SystemBackdrop</c>：backdrop 画在窗口内容**之下**。
/// * <c>MicaBackdrop.Kind</c> 取 <c>MicaKind.BaseAlt</c>（比 Base 更暗一档，适合工具类应用）。
///
/// **不支持的机器上不能留上一次的值**：显式置 null，让画布退回 <c>DesignTokens.xaml</c> 的
/// <c>QsCanvasBrush</c>（浅色 #F3F5F4）。否则主题切换/重建窗口时会残留旧材质。
/// </summary>
internal static class QsBackdrop
{
    /// <summary>给窗口挂背板。返回实际用上的材质名，供调用方写日志。</summary>
    public static string Apply(Window window, bool preferMica = true)
    {
        if (preferMica && MicaController.IsSupported())
        {
            window.SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
            return "Mica(BaseAlt)";
        }

        if (DesktopAcrylicController.IsSupported())
        {
            window.SystemBackdrop = new DesktopAcrylicBackdrop();
            return "DesktopAcrylic";
        }

        window.SystemBackdrop = null;
        return "None";
    }
}
