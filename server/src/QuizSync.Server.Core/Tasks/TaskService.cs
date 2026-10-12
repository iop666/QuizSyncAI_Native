using System.Text.Json.Nodes;
using QuizSync.Server.Core.Images;
using QuizSync.Server.Core.Protocol;
using QuizSync.Server.Core.Storage;

namespace QuizSync.Server.Core.Tasks;

/// <summary>建任务的结果（成功走 202，失败带错误码）。</summary>
public sealed record SubmitOutcome(int Status, JsonObject Body);

/// <summary>
/// `GET /api/v1/tasks/active` 的内存快照（协议 §3.4.1）。**不含**合集与 ops 水位 ——
/// 那两项是宿主的领域（`Collections` / `Sync`），由宿主拼上去。
/// </summary>
public sealed record ActiveTaskSnapshot(
    string Status,
    string? TaskId,
    string? SessionId,
    int ImageCount,
    long UpdatedAt,
    string? Message,
    JsonObject? Session);

/// <summary>
/// 任务流水线（v1 语义，逐条对 <c>spec/08-tasks.md</c> 与 `tasks.ndjson`）：
///
/// - **校验顺序**：字段缺失 → 页数上限 6 → 页图必须先上传 → 合集（无/不存在 → 409）
///   → 队列深度 20（429 `queue_full`，`retry_after_seconds` 固定 10）；
/// - **`task_id` 幂等**：同一 id 再提一次直接回既有状态（不重复识别）；
/// - **结果复用**：同 `image_hash` **且页序逐位相同**、且该会话已完成、且没带 `force_reanalyze`
///   → 不复用则新建；复用时不重复调 AI；
/// - **恒返回 202**，`cached = cached || status == 'done'`；
/// - 队列**串行执行**（并发恒 1），失败落 `error_code`（`tasks` **与** `sessions` 两处都落）；
/// - 维护「当前任务」内存快照（安卓端 1 秒轮询的兜底通道，见 <see cref="ActiveTask"/>）。
/// </summary>
public sealed class TaskService(
    TaskRepository tasks,
    SessionRepository sessions,
    ImageRepository images,
    IImageBlobStore blobs,
    CollectionRepository collections,
    IAnalyzer analyzer,
    IClock clock,
    TaskOptions options)
{
    private readonly TaskRepository _tasks = tasks;
    private readonly SessionRepository _sessions = sessions;
    private readonly ImageRepository _images = images;
    private readonly IImageBlobStore _blobs = blobs;
    private readonly CollectionRepository _collections = collections;
    private readonly IAnalyzer _analyzer = analyzer;
    private readonly IClock _clock = clock;
    private readonly TaskOptions _options = options;
    private int _running;

    private readonly Lock _activeGate = new();

    /// <summary>
    /// 当前（或最后一次）任务的快照。**内存态**：进程重启即回到 idle，不补发历史结果
    /// ——与协议 §3.4.1「该状态是内存态」一致。
    /// </summary>
    private ActiveTaskSnapshot _active = new("idle", null, null, 0, clock.NowMs, null, null);

    /// <summary>
    /// 任务状态变化回调：（任务号，状态，会话号，页数）。宿主把它接到 WS 广播上
    /// —— 这是「手机提交后能实时看到进度」的唯一出口。
    /// </summary>
    public Action<string, string, string?, int>? OnTaskUpdate { get; set; }

    /// <summary>
    /// 当前任务快照（协议 §3.4.1）。没有进行中的任务时是 `idle`；`done` / `failed` 会一直保留到
    /// 下一次提交 —— 手机第一次探测就拿到终态时只当基线，之后才决定要不要进结果页，所以终态不能立刻清掉。
    ///
    /// `Session` 是**新建的对象**（不是共享引用）：System.Text.Json 的节点只能有一个父节点，
    /// 同一份节点挂进第二个响应体会在序列化时抛异常（表现为空体 500）。
    /// </summary>
    public ActiveTaskSnapshot ActiveTask()
    {
        lock (_activeGate)
        {
            return _active with { Session = _active.Session?.DeepClone() as JsonObject };
        }
    }

    private void SetActive(string status, string? taskId, string? sessionId, int imageCount, string? message, JsonObject? session = null)
    {
        lock (_activeGate)
        {
            _active = new ActiveTaskSnapshot(status, taskId, sessionId, imageCount, _clock.NowMs, message, session);
        }
    }

    public SubmitOutcome Submit(JsonObject body, string callerDeviceId, string serverDeviceId) =>
        SubmitCore(body, callerDeviceId, serverDeviceId, checkQueueDepth: true);

    /// <summary>
    /// 建任务的公共路径。<paramref name="checkQueueDepth"/> 只在 **HTTP 创建端点**上为 true：
    /// 队列深度闸门是给「客户端连点」准备的；重新生成走执行器，不受它限制
    /// （v1 行为，`tasks.ndjson` 14–16 就是这个组合）。
    /// </summary>
    private SubmitOutcome SubmitCore(
        JsonObject body, string callerDeviceId, string serverDeviceId, bool checkQueueDepth)
    {
        var taskId = body["task_id"]?.ToString();
        var imageHash = body["image_hash"]?.ToString();
        if (string.IsNullOrEmpty(taskId) || string.IsNullOrEmpty(imageHash))
        {
            return Failure(400, ApiError.InvalidRequest, "task_id / image_hash 缺失");
        }

        // 多页：`image_hashes` 优先，否则退回单页。
        var hashes = new List<string>();
        if (body["image_hashes"] is JsonArray pageArray && pageArray.Count > 0)
        {
            foreach (var page in pageArray)
            {
                var hash = page?.ToString();
                if (!string.IsNullOrEmpty(hash))
                {
                    hashes.Add(hash);
                }
            }
        }

        if (hashes.Count == 0)
        {
            hashes.Add(imageHash);
        }

        if (hashes.Count > _options.MaxPages)
        {
            return Failure(400, ApiError.TooManyPages, $"最多 {_options.MaxPages} 页");
        }

        foreach (var hash in hashes)
        {
            if (_images.Find(hash) is null)
            {
                return Failure(400, ApiError.InvalidRequest, "image_hash 未上传");
            }
        }

        var requested = body["collection_id"]?.ToString();
        var collectionId = string.IsNullOrEmpty(requested) ? _collections.ActiveId() : requested;
        if (string.IsNullOrEmpty(collectionId))
        {
            return Failure(409, ApiError.NoActiveCollection, "请在电脑端先新建或选择一个任务合集");
        }

        if (!_collections.Exists(collectionId))
        {
            return Failure(409, ApiError.NoActiveCollection, "所选合集不存在，请重新选择");
        }

        if (checkQueueDepth)
        {
            var queued = _tasks.QueuedCount();
            if (queued >= _options.MaxQueueDepth)
            {
                return Failure(429, ApiError.QueueFull, $"主机任务队列已满（{queued}），请稍后重试", 10);
            }
        }

        // 幂等：同一 task_id 直接回既有状态（排队中就再踢一次队列）。
        var existing = _tasks.Find(taskId);
        if (existing is not null)
        {
            if (existing.Status is TaskStatus.Queued or TaskStatus.Analyzing)
            {
                Kick();
            }

            return Success(existing.Status, existing.SessionId);
        }

        var sourceDevice = body["source_device"]?.ToString() ?? callerDeviceId;
        var force = body["force_reanalyze"]?.GetValue<bool>() == true;

        if (!force)
        {
            var reusable = _sessions.FindReusableSession(imageHash, hashes);
            if (reusable is not null)
            {
                _tasks.Insert(new TaskRecord(
                    TaskId: taskId, ImageHash: imageHash, SourceDevice: sourceDevice,
                    Status: TaskStatus.Done, Attempts: 0, ErrorCode: null, SessionId: reusable,
                    CreatedAt: _clock.NowMs, StartedAt: null, FinishedAt: _clock.NowMs, PayloadJson: null));
                return Success(TaskStatus.Done, reusable);
            }
        }

        var sessionId = Guid.NewGuid().ToString();
        _sessions.InsertSession(sessionId, imageHash, sourceDevice, collectionId, _clock.NowMs);
        for (var i = 0; i < hashes.Count; i++)
        {
            _sessions.InsertSessionImage(Guid.NewGuid().ToString(), sessionId, i, hashes[i], _clock.NowMs, sourceDevice);
        }

        var payload = new JsonObject
        {
            ["image_hashes"] = new JsonArray([.. hashes.Select(h => (JsonNode)JsonValue.Create(h)!)]),
            ["collection_id"] = collectionId,
            ["server_device_id"] = serverDeviceId,
        };

        _tasks.Insert(new TaskRecord(
            TaskId: taskId, ImageHash: imageHash, SourceDevice: sourceDevice,
            Status: TaskStatus.Queued, Attempts: 0, ErrorCode: null, SessionId: sessionId,
            CreatedAt: _clock.NowMs, StartedAt: null, FinishedAt: null, PayloadJson: payload.ToJsonString()));

        OnTaskUpdate?.Invoke(taskId, TaskStatus.Queued, sessionId, hashes.Count);
        SetActive(TaskStatus.Queued, taskId, sessionId, hashes.Count, null);
        Kick();
        // 响应报的是**提交那一刻**的状态：新任务是 queued / 0 题，
        // 不能再去读会话 —— fixture 识别快到可能已经跑完了（实测竞态）。
        return Success(TaskStatus.Queued, sessionId, questionCount: 0);
    }

    public JsonObject? TaskView(string taskId)
    {
        var task = _tasks.Find(taskId);
        if (task is null)
        {
            return null;
        }

        var session = task.SessionId is null ? null : _sessions.SessionJson(task.SessionId);
        return new JsonObject
        {
            ["task_id"] = task.TaskId,
            ["status"] = task.Status,
            ["session_id"] = task.SessionId,
            ["error_code"] = task.ErrorCode,
            // 现状：错误文案取自**会话行**（会话不存在时恒为 null）。
            ["error_message"] = session?["error_message"]?.ToString(),
            ["session"] = session,
        };
    }

    public bool Retry(string taskId)
    {
        var task = _tasks.Find(taskId);
        if (task is null)
        {
            return false;
        }

        if (_tasks.Retry(taskId))
        {
            var pages = task.SessionId is null ? 0 : _sessions.PageHashes(task.SessionId).Count;
            SetActive(TaskStatus.Queued, taskId, task.SessionId, pages, null);
            Kick();
        }

        return true;
    }

    /// <summary>重新生成：会话不存在 → null（404）；没有可重跑的图 → false（409）。</summary>
    public SubmitOutcome? Reanalyze(string sessionId, string callerDeviceId)
    {
        var session = _sessions.SessionJson(sessionId);
        if (session is null)
        {
            return null;
        }

        var hashes = _sessions.PageHashes(sessionId);
        if (hashes.Count == 0)
        {
            var fallback = session["image_hash"]?.ToString();
            if (!string.IsNullOrEmpty(fallback))
            {
                hashes.Add(fallback);
            }
        }

        if (hashes.Count == 0 || hashes.Exists(h => _images.Find(h) is null))
        {
            return Failure(409, ApiError.InvalidRequest, "原图已不在电脑上，无法重新识别");
        }

        var body = new JsonObject
        {
            ["task_id"] = Guid.NewGuid().ToString(),
            ["image_hash"] = hashes[0],
            ["image_hashes"] = new JsonArray([.. hashes.Select(h => (JsonNode)JsonValue.Create(h)!)]),
            ["source_device"] = callerDeviceId,
            ["collection_id"] = session["collection_id"]?.ToString(),
            ["force_reanalyze"] = true,
        };

        var outcome = SubmitCore(body, callerDeviceId, callerDeviceId, checkQueueDepth: false);
        if (outcome.Status != 202)
        {
            return outcome;
        }

        // 重新生成的响应体是「202 + task_id + 新 session_id + question_count: null + cached: false」。
        // 注意：不能把 outcome.Body 里的节点**直接引用**到新对象上 —— System.Text.Json
        // 的节点只能有一个父节点，那样会在序列化前抛异常（表现为空体 500）。
        return new SubmitOutcome(202, new JsonObject
        {
            ["status"] = outcome.Body["status"]?.ToString(),
            ["task_id"] = body["task_id"]?.ToString(),
            ["session_id"] = outcome.Body["session_id"]?.ToString(),
            ["question_count"] = null,
            ["cached"] = false,
        });
    }

    private SubmitOutcome Success(string status, string? sessionId, int? questionCount = null)
    {
        if (questionCount is null && sessionId is not null)
        {
            var session = _sessions.SessionJson(sessionId);
            if (session is not null)
            {
                // 注意：`SessionJson` 里的整数是 long（SQLite INTEGER），
                // 用 GetValue<int>() 会抛（类型不完全匹配）→ 空体 500。
                questionCount = (int)(session["question_count"]?.GetValue<long>() ?? 0L);
            }
        }

        return new SubmitOutcome(202, new JsonObject
        {
            ["status"] = status,
            ["session_id"] = sessionId,
            ["question_count"] = questionCount,
            // 复用/已完成一律报 cached=true（客户端据此区分「新分析」与「命中缓存」）。
            ["cached"] = status == TaskStatus.Done,
        });
    }

    private static SubmitOutcome Failure(int status, string code, string message, int? retryAfter = null) =>
        new(status, ApiError.Body(code, message, retryAfter));

    /// <summary>踢一次队列（不等待）。串行执行用一个整数标志保证：队列并发恒 1。</summary>
    private void Kick() => _ = RunAsync();

    private async Task RunAsync()
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return; // 已有消费者在跑
        }

        try
        {
            while (true)
            {
                var task = _tasks.Dequeue();
                if (task is null)
                {
                    return;
                }

                await ExecuteAsync(task).ConfigureAwait(false);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private async Task ExecuteAsync(TaskRecord task)
    {
        var now = _clock.NowMs;
        _tasks.SetStatus(task.TaskId, TaskStatus.Analyzing, startedAt: now);
        if (task.SessionId is not null)
        {
            OnTaskUpdate?.Invoke(task.TaskId, TaskStatus.Analyzing, task.SessionId, 0);
        }

        var sessionId = task.SessionId;
        if (sessionId is null)
        {
            _tasks.SetStatus(task.TaskId, TaskStatus.Failed, finishedAt: now, errorCode: ApiError.Internal);
            SetActive(TaskStatus.Failed, task.TaskId, null, 0, "任务没有关联会话");
            OnTaskUpdate?.Invoke(task.TaskId, TaskStatus.Failed, null, 0);
            return;
        }

        var hashes = _sessions.PageHashes(sessionId);
        if (hashes.Count == 0)
        {
            hashes.Add(task.ImageHash);
        }

        var pages = new List<byte[]>();
        foreach (var hash in hashes)
        {
            var bytes = _blobs.Read(hash);
            if (bytes is null)
            {
                Fail(task, sessionId, hashes.Count, ApiError.Internal, "图片文件缺失");
                return;
            }

            pages.Add(bytes);
        }

        // 快照在**真正开始识别之前**置成 analyzing + 真实页数：手机轮询看到的就是
        // 「N 张图片识别中…」。等结果这段时间可能长达 90 秒，这里不能还写着上一单的状态。
        SetActive(TaskStatus.Analyzing, task.TaskId, sessionId, hashes.Count, null);

        var request = new AnalyzeRequest(
            TaskId: task.TaskId,
            SessionId: sessionId,
            CollectionId: _sessions.CollectionId(sessionId),
            ImageHashes: hashes,
            Pages: pages);

        try
        {
            var outcome = await _analyzer.AnalyzeAsync(request, CancellationToken.None).ConfigureAwait(false);

            // 失败优先看 error_code：识别器把「为什么失败」放在这里（Provider 的 ai_auth / ai_timeout、
            // 派发失败 provider_offline）。异常链会把它们压成 internal，所以失败不走异常。
            if (outcome.ErrorCode is not null)
            {
                Fail(task, sessionId, hashes.Count, outcome.ErrorCode, outcome.ErrorMessage ?? "识别失败");
                return;
            }

            if (outcome.Questions.Count == 0)
            {
                Fail(task, sessionId, hashes.Count, "no_question_found", "没有识别到题目");
                return;
            }

            var baseOrdinal = _sessions.NextQuestionOrdinal(sessionId);
            foreach (var question in outcome.Questions)
            {
                _sessions.InsertQuestion(
                    sessionId,
                    Guid.NewGuid().ToString(),
                    question with { Ordinal = baseOrdinal + question.Ordinal },
                    _clock.NowMs,
                    task.SourceDevice);
            }

            // ai_provider / ai_model / latency_ms 一并落库：手机端历史里的「模型」就是这几列。
            _sessions.SetSessionDone(
                sessionId, outcome.Questions.Count, outcome.FromCache, _clock.NowMs, task.SourceDevice,
                outcome.AiProvider, outcome.AiModel, outcome.LatencyMs);
            _tasks.SetStatus(task.TaskId, TaskStatus.Done, finishedAt: _clock.NowMs);
            SetActive(TaskStatus.Done, task.TaskId, sessionId, hashes.Count, null, _sessions.SessionJson(sessionId));
            OnTaskUpdate?.Invoke(task.TaskId, TaskStatus.Done, sessionId, hashes.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Fail(task, sessionId, hashes.Count, ApiError.Internal, ex.Message);
        }
    }

    /// <summary>
    /// 失败落库：**`tasks.error_code` 与 `sessions.error_code` 都要写**。
    /// 只写 `sessions.error_message` 时，手机存下来的是一条「没有 error_code 的失败」，
    /// 端侧按码给文案的分支全部落空（用户只会看到通用提示，也不知道该去开电脑端还是换 key）。
    /// </summary>
    private void Fail(TaskRecord task, string sessionId, int imageCount, string code, string message)
    {
        _sessions.SetSessionFailed(sessionId, message, code, _clock.NowMs);
        _tasks.SetStatus(task.TaskId, TaskStatus.Failed, finishedAt: _clock.NowMs, errorCode: code);
        SetActive(TaskStatus.Failed, task.TaskId, sessionId, imageCount, message);
        OnTaskUpdate?.Invoke(task.TaskId, TaskStatus.Failed, sessionId, imageCount);
    }
}

/// <summary>任务相关的数值口径（协议冻结值）。</summary>
public sealed record TaskOptions
{
    public int MaxPages { get; init; } = 6;

    public int MaxQueueDepth { get; init; } = 20;
}
