using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace QuizSync.App.Theme;

/// <summary>
/// 在**代码里**取主题色 / 主题画笔。
///
/// 为什么需要它：题目卡片有大量「按条件才出现」的块（标绿的选项行、标黄的警示块、
/// 材料面板……），这些只能在代码里建，没法全部写在 XAML 里。
///
/// ⚠️ **这里踩过一个真 bug，写下来免得再犯**：
/// <c>Application.Current.Resources["QsHitGreenBrush"]</c> 这种直接按 key 取**取不到**
/// ThemeDictionaries 里的资源；而更隐蔽的是
/// <c>Application.Current.Resources.ThemeDictionaries</c> ——
/// **它只有「根字典自己」的条目，不包含 MergedDictionaries 里的那些**。
/// 我们的颜色全在 <c>Theme/ShellStyles.xaml</c>（被 App.xaml 合并进来的）里，
/// 于是这个属性是**空的**，查不到就退成 Transparent —— 界面表现是
/// 「药丸没有底色、缩略图没有底、橙色提示块看不见、代码里设的文字色全透明」，
/// 而 XAML 里的 <c>{ThemeResource}</c> 一切正常（框架自己会跨合并字典找），
/// 所以光看控件树完全看不出来。**必须自己递归合并字典去找。**
/// </summary>
internal static class ShellTheme
{
    /// <summary>取当前主题的名字（ThemeDictionaries 的键）。</summary>
    public static string CurrentKey(FrameworkElement? element = null)
    {
        var theme = element?.ActualTheme ?? Application.Current.RequestedTheme switch
        {
            ApplicationTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Light,
        };

        return theme == ElementTheme.Dark ? "Dark" : "Light";
    }

    /// <summary>按当前主题取资源；找不到就依次退到 Dark / Light / HighContrast，再不行返回 null。</summary>
    public static object? Resource(string key, FrameworkElement? element = null)
    {
        foreach (var name in new[] { CurrentKey(element), "Dark", "Light", "HighContrast" })
        {
            if (FindInTheme(Application.Current.Resources, name, key) is { } themed)
            {
                return themed;
            }
        }

        // 再退一层：字体族这类资源不在 ThemeDictionaries 里，而是直接摆在根上。
        return FindPlain(Application.Current.Resources, key);
    }

    /// <summary>取画笔。同一个 key 在 XAML 里定义的是 <c>SolidColorBrush</c>。</summary>
    public static Brush Brush(string key, FrameworkElement? element = null) =>
        Resource(key, element) as Brush ?? new SolidColorBrush(Microsoft.UI.Colors.Transparent);

    /// <summary>取颜色。传画笔的 key 也能用（会读它的 <c>Color</c>）。</summary>
    public static Color Color(string key, FrameworkElement? element = null)
    {
        var value = Resource(key, element);
        return value switch
        {
            Color color => color,
            SolidColorBrush brush => brush.Color,
            _ => Microsoft.UI.Colors.Transparent,
        };
    }

    /// <summary>把某个主题色按 alpha 调成另一个不透明度（1.1.0 里遍地 `color@12%` 这种写法）。</summary>
    public static Brush Alpha(string key, byte alpha, FrameworkElement? element = null)
    {
        var color = Color(key, element);
        return new SolidColorBrush(Windows.UI.Color.FromArgb(alpha, color.R, color.G, color.B));
    }

    private static object? FindInTheme(ResourceDictionary dictionary, string theme, string key)
    {
        if (dictionary.ThemeDictionaries.TryGetValue(theme, out var raw)
            && raw is ResourceDictionary themed
            && themed.TryGetValue(key, out var value)
            && value is not null)
        {
            return value;
        }

        foreach (var merged in dictionary.MergedDictionaries)
        {
            if (FindInTheme(merged, theme, key) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static object? FindPlain(ResourceDictionary dictionary, string key)
    {
        if (dictionary.TryGetValue(key, out var value) && value is not null)
        {
            return value;
        }

        foreach (var merged in dictionary.MergedDictionaries)
        {
            if (FindPlain(merged, key) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
