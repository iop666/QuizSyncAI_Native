namespace QuizSync.Core;

/// <summary>会话状态的一整套外观（颜色 + 图标字形 + 中文名）。</summary>
/// <param name="Argb">颜色，0xAARRGGBB。</param>
/// <param name="Glyph">Segoe Fluent Icons 字形。</param>
/// <param name="Label">中文名。</param>
public sealed record StatusStyle(uint Argb, string Glyph, string Label);

/// <summary>
/// 界面上的**纯文案与纯映射**。放在 Core 里是为了能被单测钉住 ——
/// 这些都是「1.1.0 里写死、复刻时必须逐字一致」的字符串，
/// 散在 XAML 里就没法验证，也会在几个地方各抄一份。
///
/// 每一条的出处都写在注释里（路径相对冻结源码根
/// <c>D:\DeepseekHarness\QuizSync_AI-issue\soundcode\</c>）。
/// </summary>
public static class UiText
{
    // ===================================================================
    // 会话状态。出处 packages/quizsync_ui/lib/src/widgets.dart 的 `statusStyle`
    // 与 `HighlightColors.text`（done 用标绿色）。
    // 图标字形取自 Segoe Fluent Icons，与 Material 图标一一对应：
    //   schedule→E823  autorenew→E72C  check_circle→E930
    //   error_outline→E783  cancel_outlined→E711  help_outline→E897
    // ===================================================================
    public static StatusStyle Status(string? status, bool dark) => status switch
    {
        "queued" => new StatusStyle(dark ? 0xFF9AA4AE : 0xFF64748B, "\uE823", "排队中"),
        "analyzing" => new StatusStyle(dark ? 0xFF60A5FA : 0xFF2563EB, "\uE72C", "识别中"),
        "done" => new StatusStyle(dark ? 0xFF4ADE80 : 0xFF16A34A, "\uE930", "已完成"),
        "failed" => new StatusStyle(dark ? 0xFFF87171 : 0xFFDC2626, "\uE783", "失败"),
        "cancelled" => new StatusStyle(dark ? 0xFF9AA4AE : 0xFF64748B, "\uE711", "已取消"),
        _ => new StatusStyle(dark ? 0xFF9AA4AE : 0xFF64748B, "\uE897", status ?? string.Empty),
    };

    /// <summary>题型中文。出处 <c>ui_pkg/src/theme_colors.dart:54-60</c>。</summary>
    public static string QuestionTypeLabel(string? wire) => wire switch
    {
        "single" => "单选题",
        "multi" => "多选题",
        "judge" => "判断题",
        "blank" => "填空题",
        "subjective" => "主观题",
        _ => wire ?? string.Empty,
    };

    /// <summary>
    /// 时长文案。出处 <c>ui_pkg/src/widgets.dart</c> 的 <c>formatSeconds</c>：
    /// &lt; 60 → <c>N 秒</c>；否则 <c>M 分</c> 或 <c>M 分 S 秒</c>。
    /// </summary>
    public static string FormatSeconds(int seconds)
    {
        if (seconds < 60)
        {
            return $"{seconds} 秒";
        }

        var minutes = seconds / 60;
        var rest = seconds % 60;
        return rest == 0 ? $"{minutes} 分" : $"{minutes} 分 {rest} 秒";
    }

    /// <summary>
    /// 错误码的**短**中文名（会话列表项用）。出处 <c>desktop/lib/ui/home_page.dart:921-929</c>。
    /// 认不出的码**原样显示**，不要兜成「分析失败」——那会把真实错误码藏掉。
    /// </summary>
    public static string ErrorCodeLabel(string? code) => code switch
    {
        "ai_timeout" => "AI 超时",
        "ai_auth" => "API Key 无效",
        "ai_rate_limited" => "触发限流",
        "ai_bad_response" => "AI 返回无法解析",
        "ai_quota_exceeded" => "今日调用已达上限",
        "no_question_found" => "未识别到题目",
        null or "" => "分析失败",
        _ => code,
    };

    /// <summary>
    /// 失败面板里的**长**原因。出处 <c>desktop/lib/ui/home_page.dart:1488-1496</c>。
    /// 认不出的码用 <paramref name="fallback"/>（一般是服务端给的 error_message）。
    /// </summary>
    public static string ErrorReason(string? code, string? fallback) => code switch
    {
        "ai_timeout" => "AI 请求超时：模型可能太慢，可在设置里调大超时或换更快的模型",
        "ai_auth" => "API Key 无效或无权限：检查设置里的 Key 与模型名是否配套",
        "ai_rate_limited" => "触发限流：稍等重试，或在设置里调低每日上限",
        "ai_bad_response" => "AI 返回无法解析：可重试，或换一个更强的模型",
        "ai_output_truncated" => "AI 的思考占满了输出预算、回答被截断：直接再识别一次即可（预算已调大，" 
            + "偶尔仍会撞上：题目解析特别长时把「识别设置」里的超时或模型换成更强的那个）",
        "ai_quota_exceeded" => "今日调用已达上限：可在设置里调整上限",
        "no_question_found" => "未识别到题目：可框选题目区域后重试",
        "internal" => "内部错误或无法连接 AI 服务",
        _ => string.IsNullOrWhiteSpace(fallback) ? "分析失败" : fallback!,
    };

    /// <summary>
    /// 只有这些错误码才给「框选题目区域后重试」按钮
    /// （出处 <c>home_page.dart:1504-1507</c>：<c>no_question_found</c> / <c>ai_bad_response</c> /
    /// <c>ai_timeout</c> / 无错误码）。
    /// </summary>
    public static bool CanCropRetry(string? code) =>
        code is null or "no_question_found" or "ai_bad_response" or "ai_timeout";

    /// <summary>
    /// 会话列表项的首行标题。出处 <c>desktop/lib/ui/home_page.dart:911-919</c>。
    /// </summary>
    public static string SessionHeadline(string? status, string? errorCode, string? errorMessage, int questionCount)
    {
        if (status == "failed")
        {
            var reason = string.IsNullOrWhiteSpace(errorMessage) ? ErrorCodeLabel(errorCode) : errorMessage!;
            return $"识别失败：{reason}";
        }

        return status switch
        {
            "analyzing" => "正在识别…",
            "queued" => "排队中…",
            "cancelled" => "已取消",
            _ => questionCount > 0 ? $"识别出 {questionCount} 道题" : "未识别到题目",
        };
    }

    /// <summary>来源端的中文名：本机 / 手机（出处 <c>home_page.dart:152</c> 的 `${本机|手机}`）。</summary>
    public static string SourceLabel(string? sourceDevice, string selfDeviceId) =>
        string.Equals(sourceDevice, selfDeviceId, StringComparison.Ordinal) ? "本机" : "手机";

    /// <summary>
    /// 时间戳 → <c>MM-dd HH:mm</c>（出处 <c>desktop/lib/ui/home_page.dart:931-936</c>）。
    /// <paramref name="offset"/> 不给就用机器本地时区。
    /// </summary>
    public static string FormatClock(long unixMs, TimeSpan? offset = null)
    {
        var moment = DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
        if (offset is { } value)
        {
            moment = moment.ToOffset(value);
        }
        else
        {
            moment = moment.ToLocalTime();
        }

        return moment.ToString("MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>会话头上的完整时间 <c>YYYY-MM-DD HH:mm</c>（出处 <c>home_page.dart:244</c>）。</summary>
    public static string FormatDateMinute(long unixMs, TimeSpan? offset = null)
    {
        var moment = DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
        moment = offset is { } value ? moment.ToOffset(value) : moment.ToLocalTime();
        return moment.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>阅读材料面板的标题。出处 <c>ui_pkg/src/question_card.dart:71-78,613-698</c>。</summary>
    public static string MaterialTitle(bool open, int lineCount) =>
        open ? "阅读材料（点击收起）" : $"阅读材料（{lineCount} 行，点击展开）";

    /// <summary>材料里的有效行数（非空行）。</summary>
    public static int MaterialLineCount(string? material) =>
        string.IsNullOrEmpty(material)
            ? 0
            : material.Replace("\r\n", "\n", StringComparison.Ordinal)
                      .Split('\n')
                      .Count(line => !string.IsNullOrWhiteSpace(line));

    /// <summary>题目不全横幅的文案。出处 <c>question_card.dart:136-166</c>。</summary>
    public static string IncompleteBanner(bool answerGuessed) => answerGuessed
        ? "题目不全（选项被截断或缺失），下面的答案是 AI 按题意推断的"
        : "题目不全（题干或选项被截断）";

    /// <summary>
    /// 多页暂存条的提示语（三选一）。出处 <c>desktop/lib/ui/home_page.dart:99-102</c>。
    /// </summary>
    public static string StagingHint(int staged, int limit) => staged switch
    {
        0 => $"按「多页模式」热键开始攒页；抓满 {limit} 张会自动上传识别",
        _ when staged >= limit => $"已抓满 {limit} 张，正在自动上传识别；也可以按「截屏识别」立刻上传",
        _ => $"继续按「多页模式」加页；抓满 {limit} 张自动上传，或按「截屏识别」结束多页立刻上传",
    };

    /// <summary>免责声明（全应用只此一句，须常驻可见其一）。出处 <c>home_page.dart:1300-1302</c>。</summary>
    public const string Disclaimer = "答案由 AI 生成，仅供参考";

    /// <summary>把握率文案。出处 <c>question_card.dart:258-262</c>：<c>把握 N%</c>。</summary>
    public static string ConfidenceLabel(double confidence)
    {
        // 注意别写成 $"{value:0%}" —— .NET 自定义格式串里的 % 会**再乘 100**，会得到 8700%。
        var percent = (int)Math.Round(confidence * 100, MidpointRounding.AwayFromZero);
        return $"把握 {percent}%";
    }

    /// <summary>
    /// 把一段文本截到 <paramref name="max"/> 个字符，超出补省略号（搜索结果的一行摘要用）。
    ///
    /// 先按**字符**截而不是先按显示宽度：这里只用于列表摘要，单行省略交给控件。
    /// 换行会被压成空格 —— 题干里的换行在摘要里换行会把行高撑乱。
    /// </summary>
    public static string Ellipsis(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var flattened = string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (max <= 1 || flattened.Length <= max)
        {
            return flattened;
        }

        return string.Concat(flattened.AsSpan(0, max - 1), "…");
    }
}
