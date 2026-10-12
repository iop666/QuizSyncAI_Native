using System.Text.Json.Nodes;
using QuizSync.Core;
using QuizSync.Data;
using QuizSync.Provider;
using QuizSync.ServerBridge;

namespace QuizSync.Provider.Cli;

/// <summary>
/// Provider 的命令行入口（计划 §7.2/§7.3 的 `--headless`）。
///
/// 现在落地的是**自检**这一条：它把「能不能找到主机、控制面是否可用、本地库与截图是否正常」
/// 一次问清楚并按 JSON 输出 —— 无界面跑得起来，也是后续「无界面识别」那条链路的骨架。
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args.Contains("help") || args.Contains("--help"))
        {
            PrintHelp();
            return args.Length == 0 ? 1 : 0;
        }

        var json = args.Contains("--json");
        var dataDir = ValueOf(args, "--data") ?? DefaultDataDirectory();

        try
        {
            return args[0] switch
            {
                "doctor" => await DoctorAsync(dataDir, json).ConfigureAwait(false),
                "analyze" => await AnalyzeAsync(dataDir, json, args).ConfigureAwait(false),
                "sessions" => Sessions(json, args),
                "qrz" => DecodeQr(args),
                "provider" => await ProviderAsync().ConfigureAwait(false),
                "version" => PrintVersion(json),
                _ => Unknown(args[0]),
            };
        }
        catch (Exception error)
        {
            // 不吞异常：无界面模式下日志就是唯一的现场。
            Console.Error.WriteLine($"失败：{error.Message}");
            return 2;
        }
    }

    /// <summary>
    /// `analyze`：**截屏 → AI → 解析 → 打印**，无界面跑通「Windows 单机闭环」的那条链
    /// （计划 Phase 5 的 `--headless`）。原先这条链在 C# 上接不起来 —— 缺「截屏 → JPEG」，
    /// 本轮补上 `ScreenJpeg`。
    ///
    /// 配置走参数或环境变量，**都不给就用本地默认值**：
    /// `--ai-url`/`QS_AI_BASE_URL`、`--ai-key`/`QS_AI_API_KEY`、`--ai-model`/`QS_AI_MODEL`、
    /// `--ai-provider`/`QS_AI_PROVIDER`。`--no-hide` 关掉「拍之前先藏自己的窗口」。
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task<int> AnalyzeAsync(string dataDir, bool json, string[] args)
    {
        var config = new AiConfig
        {
            ProviderId = ValueOf(args, "--ai-provider") ?? Environment.GetEnvironmentVariable("QS_AI_PROVIDER") ?? "openai-compatible",
            BaseUrl = ValueOf(args, "--ai-url") ?? Environment.GetEnvironmentVariable("QS_AI_BASE_URL") ?? string.Empty,
            ApiKey = ValueOf(args, "--ai-key") ?? Environment.GetEnvironmentVariable("QS_AI_API_KEY") ?? string.Empty,
            Model = ValueOf(args, "--ai-model") ?? Environment.GetEnvironmentVariable("QS_AI_MODEL") ?? string.Empty,
        };

        var hide = !args.Contains("--no-hide");
        var shot = ScreenJpeg.Capture(hide ? AppWindowScope.Title : null);
        if (shot is null)
        {
            Console.Error.WriteLine("截屏失败：拿不到屏幕尺寸。");
            return 2;
        }

        Directory.CreateDirectory(dataDir);
        using var database = QuizDatabase.OpenOrCreate(Path.Combine(dataDir, "quizsync.db"));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds + 30) };
        var engine = new AnalysisEngine(
            new HttpAiProvider(config.ProviderId, http),
            new AnalysisCache(database),
            new QuotaGuard(database),
            deviceId: "windows-local");

        var outcome = await engine.AnalyzeImageAsync(shot.Jpeg, shot.ImageHash, config).ConfigureAwait(false);

        var report = new JsonObject
        {
            ["image"] = new JsonObject
            {
                ["sha256"] = shot.ImageHash,
                ["width"] = shot.Width,
                ["height"] = shot.Height,
                ["source_width"] = shot.SourceWidth,
                ["source_height"] = shot.SourceHeight,
                ["bytes"] = shot.Jpeg.Length,
            },
            ["provider"] = config.ProviderId,
            ["model"] = config.Model,
            ["from_cache"] = outcome.FromCache,
            ["parse_failed"] = outcome.ParseFailed,
            ["latency_ms"] = outcome.LatencyMs,
            ["provider_calls"] = outcome.ProviderCalls,
            ["error_code"] = outcome.ErrorCode,
            ["error_message"] = outcome.ErrorMessage,
            ["question_count"] = outcome.Questions.Count,
            ["questions"] = new JsonArray([.. outcome.Questions.Select(q => (JsonNode)new JsonObject
            {
                ["question_no"] = q.QuestionNo,
                ["type"] = q.Type.ToString(),
                ["stem"] = q.Stem,
                ["answer"] = q.AnswerText,
                ["choice"] = new JsonArray([.. q.Choice.Select(c => (JsonNode)c)]),
                ["need_review"] = q.NeedReview,
                ["confidence"] = q.Confidence,
            })]),
        };

        Console.WriteLine(report.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = !json }));
        return outcome.ErrorCode is null ? 0 : 2;
    }

    /// <summary>
    /// `provider`：**无界面地当主机的识别服务**（协议里的 Provider 角色）。
    ///
    /// 主机（`QuizSyncServer`）按架构不持有 API Key —— Key 是 DPAPI 密文、只有本机这个
    /// Windows 用户能解开。所以「手机传图 → 主机派发 → AI 识别 → 结果回传」这条链必须有一个
    /// **跑在这台机器上的进程**，这条命令就是它。
    ///
    /// 退出码：0 = 正常退出（Ctrl+C）；2 = 没有可用的 AI 配置；3 = 找不到主机；4 = 主机不认令牌。
    /// </summary>
    private static async Task<int> ProviderAsync()
    {
        var config = AiSettings.Load();
        if (!AiSettings.IsUsable(config))
        {
            Console.Error.WriteLine(AiSettings.ExplainMissing());
            return 2;
        }

        var session = await HostSession.ConnectAsync().ConfigureAwait(false);
        if (session is null)
        {
            Console.Error.WriteLine(
                $"没发现主机（{HostDiscovery.BasePort}–{HostDiscovery.BasePort + HostDiscovery.PortSpan - 1} 都没应答）。"
                + "先启动主机：quizsync-server run");
            return 3;
        }

        using (session)
        using (var stop = new CancellationTokenSource())
        {
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                stop.Cancel();
            };

            Console.WriteLine($"Provider 已启动：主机 {session.BaseUrl}，模型 {config.Model}（Ctrl+C 退出）");
            using var service = new ProviderService(
                session, config, line => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {line}"));
            var normal = await service.RunAsync(stop.Token).ConfigureAwait(false);
            return normal ? 0 : 4;
        }
    }

    /// <summary>
    /// `qrz &lt;图片&gt;`：把图里的二维码解回文本（退出码 0 = 解出来了）。
    ///
    /// 为什么留在 CLI 里而不是只写在测试里：**「屏幕上那张二维码到底是不是对的」只有从截图里
    /// 解一次才算证过**。`tools\verify_pair_qr.ps1` 就是拿它把「设置 → 连接设备」页上真实画出来的
    /// 二维码解回深链、再与主机控制面给的配对码逐字比 —— 否则我们只能证明「编码函数没错」，
    /// 证不了「画在界面上的是它」。
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static int DecodeQr(string[] args)
    {
        var path = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        if (path is null || !File.Exists(path))
        {
            Console.Error.WriteLine("用法：qrz <图片路径>");
            return 1;
        }

        using var bitmap = new System.Drawing.Bitmap(path);
        // LockBits + 手工读像素：整屏截图（3200×2000）用 GetPixel 要十来秒。
        var data = bitmap.LockBits(
            new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height),
            System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        string? payload;
        try
        {
            var stride = data.Stride;
            var bytes = new byte[stride * bitmap.Height];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            var bpp = Math.Abs(stride) / bitmap.Width;

            payload = PairQr.DecodeLuminance(bitmap.Width, bitmap.Height, (x, y) =>
            {
                var offset = (y * stride) + (x * bpp);
                var b = bytes[offset];
                var g = bytes[offset + 1];
                var r = bytes[offset + 2];
                return ((r * 299) + (g * 587) + (b * 114)) / 1000;
            });
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        if (payload is null)
        {
            Console.Error.WriteLine("没解出二维码（图里没有，或画糊了）");
            return 2;
        }

        Console.WriteLine(payload);
        return 0;
    }

    private static async Task<int> DoctorAsync(string dataDir, bool json)    {
        var report = new JsonObject
        {
            ["data_dir"] = dataDir,
            ["data_dir_exists"] = Directory.Exists(dataDir),
        };

        // 1) 主机：探活 + 控制面。
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var probe = await new HostDiscovery(http).ProbeAsync().ConfigureAwait(false);
        report["host"] = probe is null
            ? null
            : new JsonObject
            {
                ["base_url"] = probe.BaseUrl,
                ["port"] = probe.Port,
                ["protocol_version"] = probe.ProtocolVersion,
                ["server_device_id"] = probe.ServerDeviceId,
            };

        var controlToken = HostControl.FindControlToken(dataDir);
        report["control_token_present"] = controlToken is not null;
        if (probe is not null && controlToken is not null)
        {
            try
            {
                var code = await new HostControl(probe.BaseUrl, controlToken, http).PairCodeAsync().ConfigureAwait(false);
                report["pairing_code"] = code?["code"]?.ToString();
            }
            catch (HostException error)
            {
                report["pairing_code_error"] = error.Code;
            }
        }

        // 2) 本地库：能开就报一下表数量（顺便证明 schema 在）。
        report["local_db"] = TryLocalDb(dataDir, out var dbNote) ? "ok" : "missing";
        if (dbNote is not null)
        {
            report["local_db_note"] = dbNote;
        }

        // 3) 截屏：真截一小块，证明 GDI 通路可用。
        var shot = ScreenCapture.CaptureRegion(0, 0, 8, 8);
        report["capture"] = shot is null ? "failed" : $"{shot.Width}x{shot.Height}";

        // 4) AI 配置：只报「有没有 key」，**绝不打印 key 本身**。
        var configPath = Path.Combine(dataDir, "ai-config.json");
        report["ai_config_present"] = File.Exists(configPath);
        report["prompt_version"] = Prompt.ComputePromptVersion();

        if (json)
        {
            Console.WriteLine(report.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            Console.WriteLine("QuizSync Provider 自检");
            Console.WriteLine($"  数据目录   {dataDir}（{(Directory.Exists(dataDir) ? "存在" : "不存在")}）");
            Console.WriteLine($"  主机       {(probe is null ? "未发现" : $"{probe.BaseUrl}（协议 {probe.ProtocolVersion}）")}");
            Console.WriteLine($"  控制令牌   {(controlToken is null ? "无" : "已找到")}");
            Console.WriteLine($"  本地库     {report["local_db"]}");
            Console.WriteLine($"  截屏       {report["capture"]}");
            Console.WriteLine($"  提示词版本 {report["prompt_version"]}");
        }

        // 一个都没齐才算失败：自检的价值在于「一眼看出缺哪一块」。
        return probe is not null || report["local_db"]?.ToString() == "ok" || shot is not null ? 0 : 1;
    }

    /// <summary>
    /// `sessions`：**只读**列出本地库里的识别记录，重点回答一句
    /// 「这条记录到底挂没挂上当前任务合集」。
    ///
    /// 为什么值得单开一条命令（第 170 轮）：用户报「api 识别后当前合集看不到」，而界面在这个
    /// 故障下**什么都不显示**（左侧列表空、悬浮窗写「还没有识别记录」）——
    /// 真凶是三个 `SessionRecorder.Record(...)` 调用点都没传 `collectionId`，
    /// 记录全成了「未归类」，而界面只查当前合集。没有这条命令，就只能靠读日志猜。
    ///
    /// 库的位置与界面**同一套口径**：`QS_APP_DATA`（脚本/探针用）→ 否则共享布局
    /// `<root>\app\quizsync.db`；显式 `--data <目录>` 时按那个目录找（与 `analyze` 一致）。
    /// </summary>
    private static int Sessions(bool json, string[] args)
    {
        var explicitDir = ValueOf(args, "--data");
        var path = explicitDir is { Length: > 0 }
            ? Path.Combine(explicitDir, "quizsync.db")
            : AppDataDirectory.DatabasePath();
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"找不到本地库：{path}");
            return 2;
        }

        using var database = QuizDatabase.OpenExisting(path);
        var collections = CollectionStore.List(database)
            .ToDictionary(c => c.CollectionId, c => c.Name, StringComparer.Ordinal);
        var sessions = SessionRecorder.RecentSessions(database, 500);

        var byCollection = new Dictionary<string, int>(StringComparer.Ordinal);
        var rows = new JsonArray();
        foreach (var session in sessions)
        {
            var key = session.CollectionId is { Length: > 0 } ? session.CollectionId : string.Empty;
            byCollection[key] = byCollection.TryGetValue(key, out var count) ? count + 1 : 1;
            rows.Add(new JsonObject
            {
                ["session_id"] = session.SessionId,
                ["collection_id"] = session.CollectionId,
                ["collection_name"] = session.CollectionId is { Length: > 0 } id
                    && collections.TryGetValue(id, out var name) ? name : null,
                ["question_count"] = session.QuestionCount,
                ["status"] = session.Status,
                ["error_code"] = session.ErrorCode,
                ["created_at"] = session.CreatedAt,
            });
        }

        var groups = new JsonArray();
        foreach (var collection in CollectionStore.List(database))
        {
            byCollection.TryGetValue(collection.CollectionId, out var count);
            groups.Add(new JsonObject
            {
                ["collection_id"] = collection.CollectionId,
                ["name"] = collection.Name,
                ["count"] = count,
            });
        }

        // 「未归类」**不是合集**（契约：它由「删除合集」产生，记录回到「全部记录」），
        // 但列表里要看得见它，否则「19 条记录一条都没挂合集」这件事在输出里就消失了。
        if (byCollection.TryGetValue(string.Empty, out var loose) && loose > 0)
        {
            groups.Add(new JsonObject
            {
                ["collection_id"] = null,
                ["name"] = "未归类",
                ["count"] = loose,
            });
        }

        var report = new JsonObject
        {
            ["database"] = path,
            ["total"] = sessions.Count,
            ["unassigned"] = byCollection.TryGetValue(string.Empty, out var looseCount) ? looseCount : 0,
            ["collections"] = groups,
            ["sessions"] = rows,
        };

        if (json)
        {
            Console.WriteLine(report.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        Console.WriteLine($"本地库 {path}");
        Console.WriteLine($"识别记录 {sessions.Count} 条（未归类 {report["unassigned"]} 条）");
        foreach (var node in groups)
        {
            Console.WriteLine($"  {(node as JsonObject)?["name"]}：{(node as JsonObject)?["count"]} 条");
        }

        foreach (var session in sessions.Take(20))
        {
            var name = session.CollectionId is { Length: > 0 } id && collections.TryGetValue(id, out var n)
                ? n : "未归类";
            Console.WriteLine(
                $"  {session.SessionId}  合集={name}  题数={session.QuestionCount}  "
                + $"状态={session.Status}  时间={HistoryStamp(session.CreatedAt)}");
        }

        return 0;
    }

    /// <summary>毫秒时间戳 → 本地时间字符串（只给 `sessions` 的表格输出用）。</summary>
    private static string HistoryStamp(long unixMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss",
            System.Globalization.CultureInfo.InvariantCulture);

    private static bool TryLocalDb(string dataDir, out string? note)    {
        note = null;
        var path = Path.Combine(dataDir, "quizsync.db");
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var database = QuizDatabase.OpenExisting(path);
            using var command = database.Connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table'";
            var tables = Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            note = $"{tables} 张表";
            return true;
        }
        catch (Microsoft.Data.Sqlite.SqliteException error)
        {
            note = error.Message;
            return false;
        }
    }

    private static int PrintVersion(bool json)
    {
        Console.WriteLine(json
            ? new JsonObject { ["product"] = "QuizSync AI", ["component"] = "provider", ["protocol"] = "2.0.0" }.ToJsonString()
            : "QuizSync AI Provider（协议 2.0.0）");
        return 0;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"未知命令：{command}（用 --help 看用法）");
        return 1;
    }

    private static string? ValueOf(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static string DefaultDataDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuizSyncAI");

    private static void PrintHelp() => Console.WriteLine("""
        QuizSync AI Provider（无界面）

          provider                          当主机的识别服务：连主机、接派发、调 AI、回传结果
          doctor [--data <目录>] [--json]   自检：主机探活、控制面、本地库、截屏、提示词版本
          analyze [--json] [--no-hide]      单机闭环：截屏 → AI → 打印题目（本机识别用）
          sessions [--data <目录>] [--json]  只读列出本地识别记录：合集归属 / 题数 / 时间
          qrz <图片路径>                    把图里的二维码解回文本（配对二维码取证用）
          version [--json]                 打印组件与协议版本
          help                             本帮助

        退出码：0 = 正常；1 = 用法错误；2 = 运行期失败（stderr 有原因）
                provider：2 = 没配 AI；3 = 找不到主机；4 = 主机不认令牌（要重新配对）
                qrz：2 = 图里没解出二维码
        """);
}
