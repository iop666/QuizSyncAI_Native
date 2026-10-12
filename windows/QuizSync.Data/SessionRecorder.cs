using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using QuizSync.Core;

namespace QuizSync.Data;

/// <summary>
/// 要落库的一道题。字段与协议 `questions` 表**逐列对齐**（列名一律从 `schema/schema-v1.sql` 读）。
///
/// **为什么每一列都必须在这里出现**（本轮实测的教训）：这一层原来只投影 9 个字段，
/// `material` / `options_json` / `incomplete` / `answer_guessed` / `answer_in_image` /
/// `warnings_json` / `analysis_edited` / `answer_edited` 既没写进库、也没读回来 ——
/// **列在库里、投影里丢掉**，界面上只表现成「这一版还没做材料折叠 / 标黄 / 标绿」，
/// 全程没有任何报错。所以「写」与「读」两侧的列清单必须一起改，并由一条
/// 「字段填满 → 逐字段断言」的回归钉住。
///
/// 选项保留 `label` 与 `text` 两项，用 Core 的 <see cref="Option"/>：标绿的判定是
/// 「`answer.choice` ∩ `options[].label`」（`ai-contract.md` 第 5 节），只剩一串字符串
/// 就算不出该标哪一项；`options_json` 的形状由 Core / `ai-contract.md` 定死，
/// 本层不再抄一份解析（QuizSync.Data 本来就引用 QuizSync.Core）。
/// </summary>
public sealed record RecordedQuestion(
    int Ordinal,
    string Stem,
    string Type,
    string? QuestionNo = null,
    string? AnswerText = null,
    string Analysis = "",
    IReadOnlyList<string>? Choice = null,
    double Confidence = 0.5,
    bool NeedReview = false,
    string Material = "",
    IReadOnlyList<Option>? Options = null,
    bool Incomplete = false,
    bool AnswerGuessed = false,
    bool AnswerInImage = false,
    IReadOnlyList<string>? Warnings = null,
    bool AnalysisEdited = false,
    bool AnswerEdited = false);

/// <summary>
/// 库里的一行会话（历史列表用）。字段同样与 `sessions` 表对齐：
/// `collection_id`（未分类 = NULL）、`error_code` / `error_message`（识别失败的那条记录
/// 也要能如实显示错误码与文案，而不是显示成一条「没有题目的成功记录」）。
/// </summary>
public sealed record RecordedSession(
    string SessionId,
    string ImageHash,
    string SourceDevice,
    string Status,
    int QuestionCount,
    long CreatedAt,
    string? AiProvider,
    string? AiModel,
    int LatencyMs,
    bool Cached,
    string? CollectionId = null,
    string? ErrorCode = null,
    string? ErrorMessage = null);

/// <summary>
/// 把一次识别结果落成 `sessions` + `questions` 两个实体，**并生成同步 op**（Android 端可拉取）。
///
/// 列名与 NOT NULL 约束全部来自协议 schema（`schema/schema-v1.sql`）与一致性向量
/// （`conformance/vectors/sync.ndjson` 的 `op-s1-create` / `op-q1-create`）—— **不是手抄的**：
/// * `sessions` 必须给：image_hash / source_device / status / created_at / updated_at / updated_by
///   （其余如 question_count / cached 有默认值，但一起写更省一次改动）；
/// * `questions` 必须给：session_id / ordinal / stem / type / created_at / updated_at / updated_by；
///   `choice_json` 与 `need_review` 带 NOT NULL 默认值，但选择题的答案就在 choice_json 里，
///   不写就等于历史里看不到答案。
///
/// 走 `LocalStore.WriteLocal` 而不是直接 INSERT：**要生成 op**，否则这些记录永远同步不到手机。
/// 注意 `WriteLocal` 的列白名单（`LocalStore.Entities`）对未知列名**当场抛异常** ——
/// 这是刻意的：列清单在仓库里只该有「白名单一份」，漏了就炸，不要静默丢字段。
/// </summary>
public static class SessionRecorder
{
    /// <summary>
    /// 落一次识别；返回 session_id。
    /// `collectionId` 非空 = 这条记录归入该合集（1.1.0 的「任务合集」），
    /// `errorCode` / `errorMessage` 供识别失败但仍要留档的路径使用。
    /// </summary>
    public static string Record(
        LocalStore store,
        string imageHash,
        string sourceDevice,
        IReadOnlyList<RecordedQuestion> questions,
        long now,
        string? aiProvider = null,
        string? aiModel = null,
        int latencyMs = 0,
        bool cached = false,
        string? collectionId = null,
        string? errorCode = null,
        string? errorMessage = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(questions);

        var sessionId = $"s-{Guid.NewGuid():N}";
        store.WriteLocal(SyncEntities.Session, sessionId, new Dictionary<string, object?>
        {
            ["image_hash"] = imageHash,
            ["source_device"] = sourceDevice,
            ["status"] = "done",
            ["collection_id"] = collectionId,
            ["error_code"] = errorCode,
            ["error_message"] = errorMessage,
            ["question_count"] = (long)questions.Count,
            ["ai_provider"] = aiProvider,
            ["ai_model"] = aiModel,
            ["latency_ms"] = (long)latencyMs,
            ["cached"] = cached ? 1L : 0L,
            ["created_at"] = now,
            ["updated_at"] = now,
            ["updated_by"] = sourceDevice,
        });

        for (var index = 0; index < questions.Count; index++)
        {
            var question = questions[index];
            store.WriteLocal(SyncEntities.Question, $"q-{Guid.NewGuid():N}", new Dictionary<string, object?>
            {
                ["session_id"] = sessionId,
                ["ordinal"] = (long)(question.Ordinal == 0 ? index : question.Ordinal),
                ["question_no"] = question.QuestionNo,
                ["stem"] = question.Stem,
                ["material"] = question.Material,
                ["type"] = question.Type,
                ["options_json"] = EncodeOptions(question.Options),
                ["choice_json"] = JsonSerializer.Serialize(question.Choice ?? []),
                ["answer_text"] = question.AnswerText,
                ["analysis"] = question.Analysis,
                ["confidence"] = question.Confidence,
                ["need_review"] = question.NeedReview ? 1L : 0L,
                ["incomplete"] = question.Incomplete ? 1L : 0L,
                ["answer_guessed"] = question.AnswerGuessed ? 1L : 0L,
                ["answer_in_image"] = question.AnswerInImage ? 1L : 0L,
                ["warnings_json"] = JsonSerializer.Serialize(question.Warnings ?? []),
                ["analysis_edited"] = question.AnalysisEdited ? 1L : 0L,
                ["answer_edited"] = question.AnswerEdited ? 1L : 0L,
                ["created_at"] = now,
                ["updated_at"] = now,
                ["updated_by"] = sourceDevice,
            });
        }

        return sessionId;
    }

    /// <summary>最近的会话（新的在前）。软删除的不算。</summary>
    public static IReadOnlyList<RecordedSession> RecentSessions(QuizDatabase database, int limit = 50) =>
        QuerySessions(database, limit, collectionId: null);

    /// <summary>
    /// 某个会话下的题目（按 ordinal）。
    /// **这里就是「题库 → 界面」的唯一投影**：少 SELECT 一列，界面上就少一样东西，
    /// 而且不会有任何报错（本轮修的就是这个）。读的时候按**列名**取值
    /// （`reader.GetOrdinal`）—— 万一 SELECT 清单与取值清单脱节，会当场抛，
    /// 而不是悄悄返回默认值。
    /// </summary>
    public static IReadOnlyList<RecordedQuestion> QuestionsOf(QuizDatabase database, string sessionId)
    {
        ArgumentNullException.ThrowIfNull(database);
        using var command = database.Connection.CreateCommand();
        command.CommandText =
            """
            SELECT ordinal, stem, type, question_no, answer_text, analysis, choice_json, confidence, need_review,
                   material, options_json, incomplete, answer_guessed, answer_in_image, warnings_json,
                   analysis_edited, answer_edited
              FROM questions
             WHERE session_id = $session AND deleted_at IS NULL
             ORDER BY ordinal
            """;
        command.Parameters.AddWithValue("$session", sessionId);

        var questions = new List<RecordedQuestion>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            questions.Add(ReadQuestion(reader));
        }

        return questions;
    }

    /// <summary>
    /// 会话投影的唯一实现（`RecentSessions` 与 `CollectionStore.SessionsOf` 都走这里）——
    /// **列清单只能有一份**：这个实体的列名在本仓库里出现在 `LocalStore.Entities`、
    /// 本文件的 `Record`、以及这条 SELECT 三处，任何一处漏列都必须由测试当场发现。
    /// `collectionId` 为 null = 不过滤；非空 = 只取该合集的会话。
    /// **不 JOIN `collections`**：合集被软删只是它自己从合集列表里消失，
    /// 记录仍然留在「全部记录」里（1.1.0 的语义是删合集不删记录）。
    /// </summary>
    internal static IReadOnlyList<RecordedSession> QuerySessions(QuizDatabase database, int limit, string? collectionId)
    {
        ArgumentNullException.ThrowIfNull(database);
        using var command = database.Connection.CreateCommand();
        command.CommandText =
            """
            SELECT session_id, image_hash, source_device, status, question_count,
                   created_at, ai_provider, ai_model, latency_ms, cached,
                   collection_id, error_code, error_message
              FROM sessions
             WHERE deleted_at IS NULL
               AND ($collection IS NULL OR collection_id = $collection)
             ORDER BY created_at DESC, rowid DESC
             LIMIT $limit
            """;
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$collection", (object?)collectionId ?? DBNull.Value);

        var sessions = new List<RecordedSession>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            string? Text(string column)
            {
                var index = reader.GetOrdinal(column);
                return reader.IsDBNull(index) ? null : reader.GetString(index);
            }

            long Number(string column)
            {
                var index = reader.GetOrdinal(column);
                return reader.IsDBNull(index)
                    ? 0L
                    : Convert.ToInt64(reader.GetValue(index), CultureInfo.InvariantCulture);
            }

            sessions.Add(new RecordedSession(
                SessionId: reader.GetString(reader.GetOrdinal("session_id")),
                ImageHash: reader.GetString(reader.GetOrdinal("image_hash")),
                SourceDevice: reader.GetString(reader.GetOrdinal("source_device")),
                Status: reader.GetString(reader.GetOrdinal("status")),
                QuestionCount: (int)Number("question_count"),
                CreatedAt: Number("created_at"),
                AiProvider: Text("ai_provider"),
                AiModel: Text("ai_model"),
                LatencyMs: (int)Number("latency_ms"),
                Cached: Number("cached") != 0,
                CollectionId: Text("collection_id"),
                ErrorCode: Text("error_code"),
                ErrorMessage: Text("error_message")));
        }

        return sessions;
    }

    private static RecordedQuestion ReadQuestion(SqliteDataReader reader)
    {
        string? Text(string column)
        {
            var index = reader.GetOrdinal(column);
            return reader.IsDBNull(index) ? null : reader.GetString(index);
        }

        long Number(string column)
        {
            var index = reader.GetOrdinal(column);
            return reader.IsDBNull(index)
                ? 0L
                : Convert.ToInt64(reader.GetValue(index), CultureInfo.InvariantCulture);
        }

        bool Flag(string column) => Number(column) != 0;

        return new RecordedQuestion(
            Ordinal: (int)Number("ordinal"),
            Stem: Text("stem") ?? string.Empty,
            Type: Text("type") ?? string.Empty,
            QuestionNo: Text("question_no"),
            AnswerText: Text("answer_text"),
            Analysis: Text("analysis") ?? string.Empty,
            Choice: ParseStrings(Text("choice_json")),
            Confidence: reader.IsDBNull(reader.GetOrdinal("confidence"))
                ? 0.5
                : Convert.ToDouble(reader.GetValue(reader.GetOrdinal("confidence")), CultureInfo.InvariantCulture),
            NeedReview: Flag("need_review"),
            Material: Text("material") ?? string.Empty,
            Options: ParseOptions(Text("options_json")),
            Incomplete: Flag("incomplete"),
            AnswerGuessed: Flag("answer_guessed"),
            AnswerInImage: Flag("answer_in_image"),
            Warnings: ParseStrings(Text("warnings_json")),
            AnalysisEdited: Flag("analysis_edited"),
            AnswerEdited: Flag("answer_edited"));
    }

    /// <summary>`options_json` 的写入形状：`[{"label":"A","text":"甲"}]`（`ai-contract.md` 第 3 节）。</summary>
    private static string EncodeOptions(IReadOnlyList<Option>? options) =>
        JsonSerializer.Serialize((options ?? []).Select(option => new { label = option.Label, text = option.Text }));

    private static List<Option> ParseOptions(string? json)
    {
        var options = new List<Option>();
        if (ParseArray(json) is not { } array)
        {
            return options;
        }

        foreach (var item in array)
        {
            if (item is JsonObject option)
            {
                options.Add(Option.FromJson(option));
            }
        }

        return options;
    }

    private static List<string> ParseStrings(string? json) =>
        ParseArray(json) is { } array ? [.. array.Select(item => item?.ToString() ?? string.Empty)] : [];

    private static JsonArray? ParseArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            // 脏数据（比如别的端写坏的）不该让整个历史页打不开。
            return JsonNode.Parse(json) as JsonArray;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
