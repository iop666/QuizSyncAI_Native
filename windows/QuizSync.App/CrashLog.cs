using System;
using System.IO;

namespace QuizSync.App;

/// <summary>
/// 未处理异常的唯一落点。
///
/// **为什么必须有它**：WinUI 里 XAML 层的异常以 `0xc000027b`（STOWED_EXCEPTION）崩溃收场，
/// Windows 事件日志只给「故障模块 Microsoft.UI.Xaml.dll + 偏移量」，
/// **读不出是哪一行、也读不出异常类型** —— 排查只能靠猜（这一程已经为它猜错过一次）。
/// 写一行文本到 `%LOCALAPPDATA%\QuizSyncAI\app\logs\crash.log` 是最便宜的取证手段。
/// </summary>
public static class CrashLog
{
    public static string Path => System.IO.Path.Combine(
        QuizSync.Data.AppDataDirectory.Ensure(), "logs", "crash.log");

    public static void Write(string what, Exception error)
    {
        Append($"{what}{Environment.NewLine}{error}");
    }

    /// <summary>
    /// 记一行**没有异常**的事实（环境分支这类）。与 <see cref="Write(string, Exception)"/> 同一个文件。
    ///
    /// 为什么需要它：有几个行为是**按机器/系统分叉**的 —— 标题栏走自定义还是系统、
    /// 背板走 Mica 还是亚克力还是纯色。这些分支在界面上往往看不出差别
    /// （背板被不透明画布盖着），出问题时第一句要问的就是「你这台走的是哪条」。
    /// 与其让人去推断，不如启动时写一行。
    /// </summary>
    public static void Write(string what) => Append(what);

    private static void Append(string body)
    {
        try
        {
            var path = Path;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.AppendAllText(
                path,
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {body}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // 记录失败也不能让「记录」本身再抛一次（那会把原来的异常顶掉）。
        }
    }
}
