using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace QuizSync.App.Theme;

/// <summary>
/// 字重 → 字体资源。**必须这样映射**：WinUI 的一个 <c>FontFamily</c> 只指向一个字体文件，
/// 写 <c>FontWeight="SemiBold"</c> 并不会自动切到另一个字体文件，只会让引擎
/// 在同一个文件里找（找不到就伪加粗）。所以每个字重都显式选文件。
///
/// 档位对应（1.1.0 的「题目字重」可选 300/400/500/600/700）：
/// 300/400 → Regular（没带 Light，300 也用 Regular，见 docs/DECISIONS.md）、
/// 500 → Medium、600 → Demibold、700+ → Bold。
/// </summary>
internal static class UiFont
{
    public static FontFamily For(int weight) => weight switch
    {
        >= 700 => Resource("QsFontBold"),
        >= 600 => Resource("QsFontDemibold"),
        >= 500 => Resource("QsFontMedium"),
        _ => Resource("QsFontRegular"),
    };

    /// <summary>正文默认字重（400）。</summary>
    public static FontFamily Regular => Resource("QsFontRegular");

    /// <summary>w600 场景（标题、次要标题）。</summary>
    public static FontFamily Demibold => Resource("QsFontDemibold");

    /// <summary>w700 场景（大标题、序号、徽标）。</summary>
    public static FontFamily Bold => Resource("QsFontBold");

    private static FontFamily Resource(string key) =>
        // 走 ShellTheme：字体族资源不在 ThemeDictionaries 里，而在 ShellStyles.xaml 的根上，
        // 而 ShellStyles.xaml 是被 App.xaml **合并**进来的 —— 直接 Application.Current.Resources[key]
        // 在合并字典上不可靠（ThemeDictionaries 那条路已经实测踩过）。
        ShellTheme.Resource(key) as FontFamily
        ?? new FontFamily("Segoe UI");
}
