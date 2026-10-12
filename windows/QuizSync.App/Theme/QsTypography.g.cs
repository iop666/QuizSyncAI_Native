// 本文件由 tools/gen_design_tokens.py 从 design/tokens.json 生成，请勿手改。
// 校验：跑一次 tools/gen_design_tokens.py，加参数 check。
//
// 字号阶的 C# 取值。XAML 里用 `QsFontSizeXxx` 资源或 `QsXxxTextStyle` 样式；
// 代码里新建 TextBlock/Button 时用这里的常量 —— **不要再写 11.5 / 12.5 / 13.5 这类字面量**，
// 那几个是 1.1.0 Flutter 源码里的像素对齐值，原生排版里没有意义（原生字号是整数阶梯）。

namespace QuizSync.App.Theme;

/// <summary>跨端字号阶的 Windows 侧取值。</summary>
internal static class QsFontSizes
{
    /// <summary>专供密集计数/角标（用户要求比 caption 更小一档）；正文不许用（10px / w400）</summary>
    public const double Micro = 10;

    /// <summary>密集元信息：侧栏计数、时间戳后缀（11px / w400）</summary>
    public const double CaptionSmall = 11;

    /// <summary>辅助说明、时间戳、标签文字（12px / w400）</summary>
    public const double Caption = 12;

    /// <summary>正文默认（14px / w400）</summary>
    public const double Body = 14;

    /// <summary>列表项主标题、需要强调的正文（14px / w600）</summary>
    public const double BodyStrong = 14;

    /// <summary>卡片标题、设置分组标题（16px / w600）</summary>
    public const double Subtitle = 16;

    /// <summary>页面标题（页头 / TopAppBar）（20px / w600）</summary>
    public const double Title = 20;

    /// <summary>结果页大标题、空状态标题（28px / w600）</summary>
    public const double TitleLarge = 28;
}
