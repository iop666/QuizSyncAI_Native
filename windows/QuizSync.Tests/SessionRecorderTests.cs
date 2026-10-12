using System.Text.Json.Nodes;
using QuizSync.Core;
using QuizSync.Data;
using Xunit;

namespace QuizSync.Tests;

/// <summary>
/// 识别结果落库（第九轮补）：会话与题目要**一次写全 NOT NULL 列**、要生成同步 op、
/// 要能被历史列表读回来。列名全部来自协议 schema 与一致性向量，不是手抄的。
/// </summary>
public sealed class SessionRecorderTests
{
    private static (LocalStore Store, QuizDatabase Database) NewStore(string deviceId = "windows-local")
    {
        var database = QuizDatabase.CreateFromProtocolSchema(":memory:");
        return (new LocalStore(database, deviceId), database);
    }

    private static IReadOnlyList<RecordedQuestion> Sample() =>
    [
        new RecordedQuestion(0, "第一题", "single", QuestionNo: "1", Choice: ["B"], Analysis: "解析一", Confidence: 0.9),
        new RecordedQuestion(1, "第二题", "subjective", AnswerText: "手写答案"),
    ];

    [Fact]
    public void Record_writes_the_session_and_every_question()
    {
        var (store, database) = NewStore();

        var sessionId = SessionRecorder.Record(
            store, "sha256:abc", "windows-local", Sample(), now: 1000,
            aiProvider: "openai-compatible", aiModel: "m", latencyMs: 123);

        var sessions = SessionRecorder.RecentSessions(database);
        var session = Assert.Single(sessions);
        Assert.Equal(sessionId, session.SessionId);
        Assert.Equal("sha256:abc", session.ImageHash);
        Assert.Equal(2, session.QuestionCount);
        Assert.Equal(123, session.LatencyMs);

        var questions = SessionRecorder.QuestionsOf(database, sessionId);
        Assert.Equal(2, questions.Count);
        Assert.Equal("第一题", questions[0].Stem);
        Assert.Equal(["B"], questions[0].Choice);       // 选择题的答案在 choice_json 里
        Assert.Equal("手写答案", questions[1].AnswerText);
        Assert.Equal(0, questions[0].Ordinal);
        Assert.Equal(1, questions[1].Ordinal);
    }

    [Fact]
    public void Record_generates_sync_ops_so_the_phone_can_pull_them()
    {
        var (store, _) = NewStore();

        SessionRecorder.Record(store, "sha256:abc", "windows-local", Sample(), now: 1000);

        var ops = store.LocalOpsSince(0);
        Assert.Equal(3, ops.Count);                     // 1 个 session + 2 个 question
        Assert.Contains(ops, op => op.Entity == SyncEntities.Session);
        Assert.Equal(2, ops.Count(op => op.Entity == SyncEntities.Question));
    }

    [Fact]
    public void Questions_come_back_in_ordinal_order()
    {
        var (store, database) = NewStore();
        var scrambled = new List<RecordedQuestion>
        {
            new(2, "第三题", "blank"),
            new(0, "第一题", "blank"),
            new(1, "第二题", "blank"),
        };

        var sessionId = SessionRecorder.Record(store, "sha256:x", "windows-local", scrambled, now: 1);

        Assert.Equal(["第一题", "第二题", "第三题"],
            SessionRecorder.QuestionsOf(database, sessionId).Select(q => q.Stem));
    }

    [Fact]
    public void Soft_deleted_sessions_are_not_listed()
    {
        var (store, database) = NewStore();
        var sessionId = SessionRecorder.Record(store, "sha256:a", "windows-local", Sample(), now: 1000);
        Assert.Single(SessionRecorder.RecentSessions(database));      // 删之前看得到

        store.DeleteLocal(SyncEntities.Session, sessionId, deletedAt: 2000);

        Assert.Empty(SessionRecorder.RecentSessions(database));       // 软删之后不在列表里
    }

    [Fact]
    public void Broken_choice_json_does_not_break_the_history_page()
    {
        var (store, database) = NewStore();
        var sessionId = SessionRecorder.Record(store, "sha256:a", "windows-local", Sample(), now: 1000);

        // 脏数据（比如别的端写坏的）不该让整页打不开。
        using (var command = database.Connection.CreateCommand())
        {
            command.CommandText = "UPDATE questions SET choice_json = '不是 JSON' WHERE session_id = $s";
            command.Parameters.AddWithValue("$s", sessionId);
            command.ExecuteNonQuery();
        }

        var questions = SessionRecorder.QuestionsOf(database, sessionId);
        Assert.Equal(2, questions.Count);
        Assert.Empty(questions[0].Choice!);
    }

    /// <summary>
    /// **投影丢列的回归**（本轮加）：一道「什么字段都填满」的题写进库再读出来，逐字段断言相等。
    ///
    /// 钉的就是本仓库反复踩的那一类缺陷：列在 schema 里、库里也存得下，但 DTO 上没有、
    /// SELECT 里也没读 —— 界面上只表现成「这一版还没做材料折叠 / 标黄 / 标绿」，
    /// **全程没有任何报错**。只有这条用例会红。
    /// </summary>
    [Fact]
    public void Every_question_column_survives_the_write_read_round_trip()
    {
        var (store, database) = NewStore();

        var written = new RecordedQuestion(
            Ordinal: 3,
            Stem: "题干（含 $x^2$）",
            Type: "multi",
            QuestionNo: "7",
            AnswerText: "AB",
            Analysis: "解析全文",
            Choice: ["A", "B"],
            Confidence: 0.42,
            NeedReview: true,
            Material: "材料：某段阅读材料",
            Options: [new Option("A", "甲"), new Option("B", "乙"), new Option("C", "丙")],
            Incomplete: true,
            AnswerGuessed: true,
            AnswerInImage: true,
            Warnings: ["选项不全（仅 3 项）", "答案为 AI 按题意推断，仅供参考"],
            AnalysisEdited: true,
            AnswerEdited: true);

        var sessionId = SessionRecorder.Record(store, "sha256:full", "windows-local", [written], now: 1234);

        var read = Assert.Single(SessionRecorder.QuestionsOf(database, sessionId));
        Assert.Equal(written.Ordinal, read.Ordinal);
        Assert.Equal(written.Stem, read.Stem);
        Assert.Equal(written.Type, read.Type);
        Assert.Equal(written.QuestionNo, read.QuestionNo);
        Assert.Equal(written.AnswerText, read.AnswerText);
        Assert.Equal(written.Analysis, read.Analysis);
        Assert.Equal<string>(written.Choice!, read.Choice!);
        Assert.Equal(written.Confidence, read.Confidence);
        Assert.Equal(written.NeedReview, read.NeedReview);
        Assert.Equal(written.Material, read.Material);
        // 选项必须**带 label**：标绿判定是 answer.choice ∩ options[].label，
        // 只剩一串字符串就算不出该标哪一项。
        Assert.Equal<Option>(written.Options!, read.Options!);
        Assert.Equal(written.Incomplete, read.Incomplete);
        Assert.Equal(written.AnswerGuessed, read.AnswerGuessed);
        Assert.Equal(written.AnswerInImage, read.AnswerInImage);
        Assert.Equal<string>(written.Warnings!, read.Warnings!);
        Assert.Equal(written.AnalysisEdited, read.AnalysisEdited);
        Assert.Equal(written.AnswerEdited, read.AnswerEdited);
    }

    /// <summary>会话侧新增的三列（合集 / 错误码 / 错误文案）同样要能写进去、读回来。</summary>
    [Fact]
    public void Session_collection_and_error_columns_survive_the_round_trip()
    {
        var (store, database) = NewStore();

        var sessionId = SessionRecorder.Record(
            store, "sha256:err", "windows-local", [], now: 555,
            collectionId: "c-abc", errorCode: "no_question_found", errorMessage: "没识别到题目");

        var session = Assert.Single(SessionRecorder.RecentSessions(database));
        Assert.Equal(sessionId, session.SessionId);
        Assert.Equal("c-abc", session.CollectionId);
        Assert.Equal("no_question_found", session.ErrorCode);
        Assert.Equal("没识别到题目", session.ErrorMessage);
        Assert.Equal(0, session.QuestionCount);

        // 不传就是 NULL（未分类 / 没有错误），不要写成空串假装有值。
        var plain = SessionRecorder.Record(store, "sha256:plain", "windows-local", [], now: 556);
        var plainSession = SessionRecorder.RecentSessions(database).Single(s => s.SessionId == plain);
        Assert.Null(plainSession.CollectionId);
        Assert.Null(plainSession.ErrorCode);
        Assert.Null(plainSession.ErrorMessage);
    }

    /// <summary>
    /// 新增的列也必须进**同步 op**：写入走的是 `LocalStore.WriteLocal`，
    /// 落库与生成 op 是同一步；这一条钉住「库里有、手机端正拉不到」这种分叉。
    /// </summary>
    [Fact]
    public void The_new_question_columns_travel_in_the_sync_op()
    {
        var (store, _) = NewStore();
        SessionRecorder.Record(
            store, "sha256:op", "windows-local",
            [
                new RecordedQuestion(
                    1, "题干", "single",
                    Material: "材料", Options: [new Option("A", "甲")],
                    Incomplete: true, AnswerGuessed: true, AnswerInImage: true, Warnings: ["w"],
                    AnalysisEdited: true, AnswerEdited: true),
            ],
            now: 1000);

        var op = store.LocalOpsSince(0).Single(o => o.Entity == SyncEntities.Question);
        foreach (var column in new[]
                 {
                     "material", "options_json", "incomplete", "answer_guessed",
                     "answer_in_image", "warnings_json", "analysis_edited", "answer_edited",
                 })
        {
            Assert.True(op.Fields.ContainsKey(column), $"同步 op 里少了 {column}（手机端会读不到这一列）");
        }

        Assert.Equal("材料", op.Fields["material"]?.ToString());
        Assert.Equal(1L, op.Fields["incomplete"]);
        Assert.Equal(1L, op.Fields["answer_guessed"]);

        // options_json 的线上形状是 [{"label":..,"text":..}]（`ai-contract.md` 第 3 节）。
        var options = JsonNode.Parse((string)op.Fields["options_json"]!)!.AsArray();
        Assert.Equal("A", options[0]!["label"]!.ToString());
        Assert.Equal("甲", options[0]!["text"]!.ToString());
    }
}
