using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using QuizSync.Server.Core.Storage;

namespace QuizSync.Server.Core.Tasks;

/// <summary>
/// 一条识别出来的题目（写进 `questions` 表）。
///
/// **字段与协议 `questions` 表逐列对齐**（除 `question_id` / `session_id` / `created_at` /
/// `updated_at` / `updated_by` / `lamport` / `field_clocks_json` / `deleted_at` 这些由服务端生成的列）。
/// 手机端有三个功能直接吃这几列：标绿靠 `answer.choice ∩ options[].label`
/// （`options_json` + `choice_json`）、材料折叠靠 `material`、标黄靠 `need_review` / `confidence`。
/// **少写任何一列，界面上都只表现成「这版功能没做」，且全程没有任何报错** ——
/// 这正是这一版要钉死的东西（见 `InsertQuestion`）。
/// </summary>
public sealed record AnalyzedQuestion(
    int Ordinal,
    string Stem,
    string Type,
    string? AnswerText,
    string Analysis,
    double Confidence,
    string? QuestionNo = null,
    string Material = "",
    string? OptionsJson = null,
    string? ChoiceJson = null,
    int NeedReview = 0,
    int AnswerInImage = 0,
    int Incomplete = 0,
    int AnswerGuessed = 0,
    string? WarningsJson = null);

/// <summary>
/// 识别结果。**失败也走这个类型**（不抛异常）：任务失败要把原因落进 `tasks.error_code`
/// **与** `sessions.error_code` 两处，而异常链会把「Provider 明确告诉我们的原因」
/// （`ai_auth` / `ai_timeout` 之类）压成 `internal`，手机端就没有可分支的码了。
/// </summary>
public sealed record AnalyzeOutcome(
    IReadOnlyList<AnalyzedQuestion> Questions,
    bool FromCache = false,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    string? AiProvider = null,
    string? AiModel = null,
    long? LatencyMs = null);

/// <summary>
/// 一次识别请求。
///
/// **为什么不是「只给图片字节」**：派发给 Provider 的 `recognition_job` 帧要求带
/// `task_id` / `session_id` / `collection_id` / `image_hashes`（Provider 靠它们把结果关联回会话，
/// 并原样带回来），这些只有 <see cref="TaskService"/> 知道 —— 识别器自己拿不到。
/// </summary>
public sealed record AnalyzeRequest(
    string TaskId,
    string SessionId,
    string? CollectionId,
    IReadOnlyList<string> ImageHashes,
    IReadOnlyList<byte[]> Pages);

/// <summary>
/// 识别能力的抽象。**Server 自己不做 AI**（2.0 架构里 AI 归 Windows 客户端的 Provider 角色），
/// 但 v1 兼容层必须有「提交任务 → 出结果」这条链路，否则老客户端的搜题流程断掉。
///
/// 生产实现是 <see cref="ProviderAnalyzer"/>（派发给 Provider）；测试与一致性向量回放用
/// <see cref="FixtureAnalyzer"/>（回放要的是确定性，不是真 AI）。
/// </summary>
public interface IAnalyzer
{
    Task<AnalyzeOutcome> AnalyzeAsync(AnalyzeRequest request, CancellationToken ct);
}

/// <summary>
/// 固定答案的识别器：**只给测试与一致性向量回放用**。
/// 默认返回 1 道单选，与向量 `tasks.ndjson` 第 9 步期望的 `question_count: 1` 对齐。
///
/// ⚠️ 它**永远成功**，所以生产路径绝不允许落到这里：真人拿到的会是「第 1 题（fixture）」这种胡话，
/// 而且没有任何报错（这个缺陷真实存在过：<see cref="QuizSyncHost.Analyzer"/> 的默认值就是它，
/// 而当时的 `StartAsync` 没有注入点）。CLI 里它只能靠 `--analyzer fixture` 显式开启。
/// </summary>
public sealed class FixtureAnalyzer(int questionCount = 1) : IAnalyzer
{
    private readonly int _questionCount = questionCount;

    public Task<AnalyzeOutcome> AnalyzeAsync(AnalyzeRequest request, CancellationToken ct)
    {
        var questions = new List<AnalyzedQuestion>();
        for (var i = 0; i < _questionCount; i++)
        {
            questions.Add(new AnalyzedQuestion(
                Ordinal: i,
                Stem: $"第 {i + 1} 题（fixture）",
                // 协议里 type 的取值是 single|multi|judge|blank|subjective（**不是** single_choice）。
                // 这是假数据，但假数据用错字面量会把下一个人带进沟里。
                Type: "single",
                AnswerText: "A",
                Analysis: "fixture 解析",
                Confidence: 0.98));
        }

        return Task.FromResult(new AnalyzeOutcome(questions));
    }
}

/// <summary>`sessions` / `questions` 两张表的写入（识别结果落库）。</summary>
public sealed class SessionRepository(QuizSyncDatabase db)
{
    private readonly QuizSyncDatabase _db = db;

    /// <summary>建会话行（任务提交时就建，`question_count` 先给 0）。</summary>
    public void InsertSession(
        string sessionId, string imageHash, string sourceDevice, string collectionId, long nowMs)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sessions (session_id, image_hash, source_device, status, question_count, cached,
                                  collection_id, created_at, updated_at, updated_by, lamport)
            VALUES ($id, $hash, $src, 'analyzing', 0, 0, $collection, $at, $at, $src, 0)
            """;
        cmd.Parameters.AddWithValue("$id", sessionId);
        cmd.Parameters.AddWithValue("$hash", imageHash);
        cmd.Parameters.AddWithValue("$src", sourceDevice);
        cmd.Parameters.AddWithValue("$collection", collectionId);
        cmd.Parameters.AddWithValue("$at", nowMs);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 会话完成。`ai_provider` / `ai_model` / `latency_ms` 一并落库 —— 手机端历史里
    /// 「看不到模型」就是这几列没写造成的（列在库里、插入点丢掉，界面上无从分辨）。
    /// fixture 路径这三个参数是 null（假识别器没有模型可言），与以前的行为一致。
    /// </summary>
    public void SetSessionDone(
        string sessionId, int questionCount, bool cached, long nowMs, string updatedBy,
        string? aiProvider = null, string? aiModel = null, long? latencyMs = null)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            UPDATE sessions SET status = 'done', question_count = $count, cached = $cached,
                                updated_at = $at, updated_by = $by,
                                ai_provider = $provider, ai_model = $model, latency_ms = $latency
            WHERE session_id = $id
            """;
        cmd.Parameters.AddWithValue("$count", questionCount);
        cmd.Parameters.AddWithValue("$cached", cached ? 1 : 0);
        cmd.Parameters.AddWithValue("$at", nowMs);
        cmd.Parameters.AddWithValue("$by", updatedBy);
        cmd.Parameters.AddWithValue("$provider", (object?)aiProvider ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$model", (object?)aiModel ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$latency", (object?)latencyMs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", sessionId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 会话失败。**`error_code` 必须一起写**：只写 `error_message` 时，手机存下来的那条记录
    /// 就是一个「没有 error_code 的失败」，端侧按码给文案的分支全部落空，只能显示通用提示。
    /// </summary>
    public void SetSessionFailed(string sessionId, string message, string? errorCode, long nowMs)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            UPDATE sessions SET status = 'failed', error_message = $msg, error_code = $code, updated_at = $at
            WHERE session_id = $id
            """;
        cmd.Parameters.AddWithValue("$msg", message);
        cmd.Parameters.AddWithValue("$code", (object?)errorCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$at", nowMs);
        cmd.Parameters.AddWithValue("$id", sessionId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>题目的序号从 ordinal 开始顺延（同一个会话里不重号）。</summary>
    public int NextQuestionOrdinal(string sessionId)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(ordinal), -1) + 1 FROM questions WHERE session_id = $id";
        cmd.Parameters.AddWithValue("$id", sessionId);
        return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 写一道题。**逐列显式写**（不靠列默认值）：`options_json` / `choice_json` / `material` /
    /// `need_review` / `answer_in_image` / `incomplete` / `answer_guessed` / `warnings_json`
    /// 以前根本没出现在 INSERT 里，于是「识别成功、库里也有列、但手机上标绿/折叠/标黄全都不动」——
    /// 排查时看起来像「这版功能没做」，实际是投影里丢掉了几列。
    /// NULL 可空的列（`question_no` / `options_json` / `choice_json` / `answer_text` / `warnings_json`）
    /// 显式写 NULL，NOT NULL 的列写真实值（空字符串就是空字符串）。
    /// </summary>
    public void InsertQuestion(string sessionId, string questionId, AnalyzedQuestion question, long nowMs, string updatedBy)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO questions (question_id, session_id, ordinal, question_no, stem, material, type,
                                   options_json, choice_json, answer_text, analysis, confidence,
                                   need_review, answer_in_image, incomplete, answer_guessed, warnings_json,
                                   created_at, updated_at, updated_by, lamport)
            VALUES ($qid, $sid, $ordinal, $no, $stem, $material, $type,
                    $options, $choice, $answer, $analysis, $confidence,
                    $needReview, $answerInImage, $incomplete, $answerGuessed, $warnings,
                    $at, $at, $by, 0)
            """;
        cmd.Parameters.AddWithValue("$qid", questionId);
        cmd.Parameters.AddWithValue("$sid", sessionId);
        cmd.Parameters.AddWithValue("$ordinal", question.Ordinal);
        cmd.Parameters.AddWithValue("$no", (object?)question.QuestionNo ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$stem", question.Stem);
        cmd.Parameters.AddWithValue("$material", question.Material);
        cmd.Parameters.AddWithValue("$type", question.Type);
        cmd.Parameters.AddWithValue("$options", (object?)question.OptionsJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$choice", (object?)question.ChoiceJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$answer", (object?)question.AnswerText ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$analysis", question.Analysis);
        cmd.Parameters.AddWithValue("$confidence", question.Confidence);
        cmd.Parameters.AddWithValue("$needReview", question.NeedReview);
        cmd.Parameters.AddWithValue("$answerInImage", question.AnswerInImage);
        cmd.Parameters.AddWithValue("$incomplete", question.Incomplete);
        cmd.Parameters.AddWithValue("$answerGuessed", question.AnswerGuessed);
        cmd.Parameters.AddWithValue("$warnings", (object?)question.WarningsJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$at", nowMs);
        cmd.Parameters.AddWithValue("$by", updatedBy);
        cmd.ExecuteNonQuery();
    }

    public void InsertSessionImage(string sessionImageId, string sessionId, int ordinal, string imageHash, long nowMs, string updatedBy)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO session_images (session_image_id, session_id, ordinal, image_hash, created_at, lamport, updated_at, updated_by)
            VALUES ($id, $sid, $ordinal, $hash, $at, 0, $at, $by)
            """;
        cmd.Parameters.AddWithValue("$id", sessionImageId);
        cmd.Parameters.AddWithValue("$sid", sessionId);
        cmd.Parameters.AddWithValue("$ordinal", ordinal);
        cmd.Parameters.AddWithValue("$hash", imageHash);
        cmd.Parameters.AddWithValue("$at", nowMs);
        cmd.Parameters.AddWithValue("$by", updatedBy);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 会话行的**完整载荷**：`sessions` 的全部列 + `questions`（按 ordinal）+ `image_hashes` /
    /// `image_count`。
    ///
    /// `GET /tasks/&lt;id&gt;` 的 `session`、WS `task_result.session`、`/tasks/active` 的 `session`
    /// 共用这一份 —— 手机端的题目列表就是从这里来的，所以题目必须**整行**给（见
    /// <see cref="InsertQuestion"/> 的说明：少一列界面上就少一个功能，而且不报错）。
    /// </summary>
    public JsonObject? SessionJson(string sessionId)
    {
        JsonObject? obj;
        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = "SELECT * FROM sessions WHERE session_id = $id";
            cmd.Parameters.AddWithValue("$id", sessionId);
            using var reader = cmd.ExecuteReader();
            obj = reader.Read() ? Row(reader) : null;
        }

        if (obj is null)
        {
            return null;
        }

        var questions = new JsonArray();
        foreach (var question in Children("questions", sessionId))
        {
            questions.Add(question);
        }

        obj["questions"] = questions;

        var hashes = PageHashes(sessionId);
        obj["image_hashes"] = new JsonArray([.. hashes.Select(h => (JsonNode)JsonValue.Create(h)!)]);
        obj["image_count"] = hashes.Count;
        return obj;
    }

    /// <summary>会话下的活行（软删除的不给），按 ordinal 升序。</summary>
    private List<JsonObject> Children(string table, string sessionId)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = $"SELECT * FROM {table} WHERE session_id = $id AND deleted_at IS NULL ORDER BY ordinal";
        cmd.Parameters.AddWithValue("$id", sessionId);
        using var reader = cmd.ExecuteReader();
        var rows = new List<JsonObject>();
        while (reader.Read())
        {
            rows.Add(Row(reader));
        }

        return rows;
    }

    /// <summary>整行转 JSON（SQLite 的 INTEGER 统一成 long，与 `SessionJson` 的老口径一致）。</summary>
    private static JsonObject Row(SqliteDataReader reader)
    {
        var obj = new JsonObject();
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var name = reader.GetName(i);
            if (reader.IsDBNull(i))
            {
                obj[name] = null;
                continue;
            }

            var value = reader.GetValue(i);
            obj[name] = value switch
            {
                long l => JsonValue.Create(l),
                int n => JsonValue.Create((long)n),
                double d => JsonValue.Create(d),
                _ => JsonValue.Create(value.ToString()),
            };
        }

        return obj;
    }

    /// <summary>
    /// 会话所属的合集号。单独取一列而不是拖着整行 —— 识别派发的 `recognition_job` 帧要带它，
    /// 而那条路上不需要题目与页序。
    /// </summary>
    public string? CollectionId(string sessionId)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT collection_id FROM sessions WHERE session_id = $id";
        cmd.Parameters.AddWithValue("$id", sessionId);
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? null : (string)value;
    }

    /// <summary>会话的页序 hash（按 ordinal）。</summary>
    public List<string> PageHashes(string sessionId)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT image_hash FROM session_images WHERE session_id = $id AND deleted_at IS NULL ORDER BY ordinal";
        cmd.Parameters.AddWithValue("$id", sessionId);
        using var reader = cmd.ExecuteReader();
        var hashes = new List<string>();
        while (reader.Read())
        {
            hashes.Add(reader.GetString(0));
        }

        return hashes;
    }

    /// <summary>找「同图 + 同页序 + 已完成」的会话（结果复用；页序必须逐位相同）。</summary>
    public string? FindReusableSession(string imageHash, IReadOnlyList<string> hashes, int window = 500)
    {
        var candidates = new List<string>();
        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = """
                SELECT session_id FROM sessions
                WHERE image_hash = $hash AND status = 'done' AND deleted_at IS NULL
                ORDER BY created_at DESC LIMIT $limit
                """;
            cmd.Parameters.AddWithValue("$hash", imageHash);
            cmd.Parameters.AddWithValue("$limit", window);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                candidates.Add(reader.GetString(0));
            }
        }

        foreach (var candidate in candidates)
        {
            var pages = PageHashes(candidate);
            if (pages.Count == hashes.Count && pages.SequenceEqual(hashes, StringComparer.Ordinal))
            {
                return candidate;
            }
        }

        return null;
    }
}
