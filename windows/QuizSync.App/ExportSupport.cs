using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using QuizSync.Core;
using QuizSync.Data;

namespace QuizSync.App;

/// <summary>
/// 导出：单次识别 / 合集 / 全部历史（Markdown 或 JSON）/ 日志。
///
/// **为什么合成一个文件**：这四个入口原来分散在三处（顶栏两个、设置页两个还是「还没接上」占位），
/// 而它们要用的东西是同一批 —— 另存为对话框、单篇 Markdown 组装、库读取。
/// 这个项目里「同一件事两份实现」已经分叉过好几次（导出那次就是），所以只留一份。
///
/// 纯组装（JSON 结构、日志脱敏）在 `QuizSync.Data.HistoryExport`，**那里有测试**；
/// 这里只负责文件对话框与库读取这些**没法单测**的部分。
/// </summary>
internal static class ExportSupport
{
    /// <summary>另存为对话框。返回 null = 用户取消（与「导出失败」区分开，`SPEC.md` §5 明确要求）。</summary>
    private static async Task<Windows.Storage.StorageFile?> PickSaveFileAsync(
        string suggestedName, params (string Label, string Extension)[] types)
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
            SuggestedFileName = suggestedName,
        };

        foreach (var (label, extension) in types)
        {
            picker.FileTypeChoices.Add(label, [extension]);
        }

        // 非打包（unpackaged）应用必须先给 picker 一个窗口句柄，否则直接抛。
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindowInstance);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        return await picker.PickSaveFileAsync();
    }

    /// <summary>把合集名 / 会话 id 变成合法文件名（Windows 不允许 `\ / : * ? " &lt; &gt; |`）。</summary>
    internal static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string([.. name.Select(c => invalid.Contains(c) ? '_' : c)]).Trim();
        return string.IsNullOrEmpty(cleaned) ? "quizsync-export" : cleaned;
    }

    /// <summary>
    /// 单次识别的 Markdown（1.1.0 的「导出本次」）。
    /// 组装本体在 `HistoryExport.SessionMarkdown`（**那里有测试**），这里只补上本机设备 id。
    /// </summary>
    internal static string ComposeSessionMarkdown(RecordedSession session, IReadOnlyList<QuizSync.Core.Question> questions) =>
        HistoryExport.SessionMarkdown(session, questions, SyncUploader.DeviceId);

    /// <summary>
    /// 「导出本次」：当前会话 → Markdown。
    /// 没有题目时**明确说出来**，不静默返回。
    /// </summary>
    internal static async Task<string> ExportSessionAsync(QuizDatabase database, RecordedSession session)
    {
        var questions = new AnalysisCache(database).QuestionsOfSession(session.SessionId);
        if (questions.Count == 0)
        {
            return "这次识别没有题目可导出";
        }

        var file = await PickSaveFileAsync($"session-{session.SessionId}", ("Markdown", ".md"));
        if (file is null)
        {
            return "已取消导出";
        }

        await Windows.Storage.FileIO.WriteTextAsync(file, ComposeSessionMarkdown(session, questions));
        return $"已导出：{file.Path}";
    }

    /// <summary>
    /// 「导出合集」（顶栏与设置页**同一个入口**）：把该合集下所有识别记录导成一个 Markdown，
    /// 按识别顺序（时间升序）排列。空合集明确说出来。
    /// </summary>
    internal static async Task<string> ExportCollectionAsync(QuizDatabase database, string collectionId)
    {
        var collection = CollectionStore.List(database).FirstOrDefault(c => c.CollectionId == collectionId);
        if (collection is null)
        {
            return "还没有选合集，先点顶栏的合集名字选一个";
        }

        var sessions = CollectionStore.SessionsOf(database, collection.CollectionId, 1000)
            .OrderBy(s => s.CreatedAt)
            .ToArray();
        if (sessions.Length == 0)
        {
            return $"合集「{collection.Name}」里还没有识别记录";
        }

        var file = await PickSaveFileAsync(SafeFileName(collection.Name), ("Markdown", ".md"));
        if (file is null)
        {
            return "已取消导出";
        }

        var cache = new AnalysisCache(database);
        var items = sessions
            .Select(session => new HistoryExport.Item(
                session,
                cache.QuestionsOfSession(session.SessionId),
                collection.Name))
            .Where(item => item.Questions.Count > 0)
            .ToArray();

        await Windows.Storage.FileIO.WriteTextAsync(
            file,
            HistoryExport.CollectionMarkdown(collection.Name, items, SyncUploader.DeviceId));
        return $"已导出合集「{collection.Name}」（{sessions.Length} 条）：{file.Path}";
    }

    /// <summary>
    /// 「导出全部历史」：**所有**合集的记录 + 未分类的那些，按识别顺序；Markdown 或 JSON
    /// （`SPEC.md` §2.3 给了两种格式，所以文件类型让用户选）。
    /// </summary>
    internal static async Task<string> ExportAllHistoryAsync(QuizDatabase database, bool asJson)
    {
        var items = ReadAll(database);
        if (items.Count == 0)
        {
            return "还没有识别记录，没有可导出的内容";
        }

        var file = await PickSaveFileAsync(
            $"quizsync-history-{DateTime.Now:yyyyMMdd}",
            asJson ? ("JSON", ".json") : ("Markdown", ".md"));
        if (file is null)
        {
            return "已取消导出";
        }

        var text = asJson
            ? HistoryExport.AllHistoryJson(items, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            : HistoryExport.AllHistoryMarkdown(items, SyncUploader.DeviceId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        await Windows.Storage.FileIO.WriteTextAsync(file, text);
        return $"已导出全部历史（{items.Count} 条）：{file.Path}";
    }

    /// <summary>
    /// 读出**全部**历史（含未分类），按识别顺序升序。
    /// 走 `RecentSessions` 而不是按合集遍历：未分类的记录（`collection_id IS NULL`）也要能导出来。
    /// </summary>
    private static IReadOnlyList<HistoryExport.Item> ReadAll(QuizDatabase database)
    {
        var collections = CollectionStore.List(database)
            .ToDictionary(c => c.CollectionId, c => c.Name, StringComparer.Ordinal);
        var cache = new AnalysisCache(database);

        return
        [
            .. SessionRecorder.RecentSessions(database, limit: 100_000)
                .OrderBy(s => s.CreatedAt)
                .Select(session => new HistoryExport.Item(
                    session,
                    cache.QuestionsOfSession(session.SessionId),
                    session.CollectionId is { } id && collections.TryGetValue(id, out var name) ? name : null)),
        ];
    }

    /// <summary>
    /// 「导出日志」：把应用数据目录下 `logs\` 里的**全部**文件拼成一个 txt。
    /// **必须过脱敏**（`HistoryExport.RedactSecrets`），否则等于把 Key 与令牌发出去。
    /// 没有日志时如实说「还没有日志」，不生成空文件。
    /// </summary>
    internal static async Task<string> ExportLogsAsync()
    {
        var directory = Path.Combine(AppDataDirectory.Ensure(), "logs");
        var files = Directory.Exists(directory)
            ? Directory.GetFiles(directory).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray()
            : [];

        if (files.Length == 0)
        {
            return $"还没有日志可导出（看过 {directory}）";
        }

        var builder = new StringBuilder();
        builder.AppendLine("# QuizSync AI 日志");
        builder.Append("导出时间：").AppendLine(HistoryExport.FormatTime(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        builder.Append("数据目录：").AppendLine(AppDataDirectory.Ensure());
        builder.Append("日志目录：").AppendLine(directory);
        builder.AppendLine();
        builder.AppendLine("> 已自动脱敏：`api_key` / `token` / `authorization` / `secret` 一类键后面的值、");
        builder.AppendLine("> `sk-` 开头的 Key、以及独立的 64 位十六进制串（设备令牌）都已替换成「(已脱敏)」。");

        foreach (var path in files)
        {
            builder.AppendLine();
            builder.Append("## ").Append(Path.GetFileName(path));
            try
            {
                var info = new FileInfo(path);
                builder.Append('（').Append(info.Length).AppendLine(" 字节）");
                builder.AppendLine();
                builder.AppendLine("```text");
                builder.AppendLine(HistoryExport.RedactSecrets(await File.ReadAllTextAsync(path)));
                builder.AppendLine("```");
            }
            catch (IOException error)
            {
                // 单个文件读不动（被占用等）不影响其余文件 —— 但要说出来。
                builder.Append("（读不出来：").Append(error.Message).AppendLine("）");
            }
        }

        var target = await PickSaveFileAsync(
            $"quizsync-logs-{DateTime.Now:yyyyMMdd-HHmm}",
            ("文本", ".txt"));
        if (target is null)
        {
            return "已取消导出";
        }

        await Windows.Storage.FileIO.WriteTextAsync(target, builder.ToString());
        return $"已导出 {files.Length} 个日志文件（已脱敏）：{target.Path}";
    }
}
