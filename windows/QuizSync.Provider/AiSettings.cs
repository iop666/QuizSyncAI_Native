using System.Text.Json;
using System.Text.Json.Nodes;
using QuizSync.Core;
using QuizSync.Data;

namespace QuizSync.Provider;

/// <summary>
/// AI 配置的来源：**先看配置在文件里的值，再看环境变量**。
///
/// 环境变量那条（`QS_AI_*`）保留着，因为它与 `QuizSync.Provider.Cli` 同口径 ——
/// 命令行验证过的那套配置可以直接喂给界面，调试时不用两处各配一份。
///
/// **位置说明**（本轮从 `QuizSync.App` 搬到这里）：AI Key 只有 Windows 端持有，
/// 而「谁调 AI」在 2.0 里有两个入口 —— 带界面的应用（本机截屏识别）与无界面的
/// Provider（`provider` 命令，替主机分析手机传上来的图）。两个入口读的必须是**同一份**
/// `ai.json`，否则会出现「界面里配好了、手机上识别却说没配 AI」这种最难查的不一致。
///
/// **密钥落盘说明**：`api_key` 存在 `<共享根>\app\ai.json` 里，是 `dpapi:` 密文
/// （见 <see cref="SecretStore"/>）；老配置里的明文仍能读。
/// </summary>
public static class AiSettings
{
    public const string BaseUrlVariable = "QS_AI_BASE_URL";
    public const string ApiKeyVariable = "QS_AI_API_KEY";
    public const string ModelVariable = "QS_AI_MODEL";
    public const string ProviderVariable = "QS_AI_PROVIDER";

    /// <summary>
    /// OpenAI 兼容这一路的默认模型与地址。
    ///
    /// 出处：1.1.0 设置页的默认值就是 DeepSeek（`api_settings_page.dart:118-120`「默认已按 DeepSeek
    /// 填好模型名与地址（deepseek-flash）」、`AiUiSettings.defaultModel/defaultBaseUrl`）。
    /// 注意这与 `AiProviders.OpenAiDefaultBaseUrl`（`https://api.openai.com/v1`）**不是**同一个东西：
    /// 后者是「provider 的通用兜底」，这里是**本项目的默认值**（用户没填过就按 DeepSeek 走）。
    /// 只对 openai-compatible 生效 —— 选了 anthropic / gemini 时地址留空，交回 Core 的 provider 兜底。
    /// </summary>
    public const string DefaultModel = "deepseek-flash";

    public const string DefaultBaseUrl = "https://api.deepseek.com";

    /// <summary>请求超时默认 90 秒、每日调用上限默认 200 次（1.1.0 的下拉默认值）。</summary>
    public const int DefaultTimeoutSeconds = 90;

    public const int DefaultDailyLimit = 200;

    private const string FileName = "ai.json";

    private static string FilePath => Path.Combine(AppDataDirectory.Ensure(), FileName);

    /// <summary>设置文件里的值（没配置过就是空）。</summary>
    public static AiConfig? LoadSaved()
    {
        var json = ReadFile();
        if (json is null)
        {
            return null;
        }

        return new AiConfig
        {
            ProviderId = json["provider_id"]?.ToString() is { Length: > 0 } provider ? provider : "openai-compatible",
            BaseUrl = json["base_url"]?.ToString() ?? string.Empty,
            // 落盘的是密文（`dpapi:` 前缀），这里解回明文；老配置里的明文原样通过。
            ApiKey = SecretStore.Unprotect(json["api_key"]?.ToString() ?? string.Empty),
            Model = json["model"]?.ToString() ?? string.Empty,
            // 第七轮补上：`timeout_seconds` 以前**只写不读**，所以「请求超时」这个下拉
            // 改了等于没改（`Load()` 每次都吐默认 90）。落库键位早就有，是读的那一半漏了。
            TimeoutSeconds = json["timeout_seconds"]?.GetValue<int>() is { } timeout && timeout > 0
                ? timeout
                : DefaultTimeoutSeconds,
            MaxRetries = json["max_retries"]?.GetValue<int>() is { } retries && retries >= 0 ? retries : 2,
        };
    }

    /// <summary>每日调用上限（0 = 不设上限）。与 AI 配置同一个文件，但**不进 `AiConfig`**（那是 Core 的模型）。</summary>
    public static int LoadDailyLimit() =>
        ReadFile()?["daily_limit"]?.GetValue<int>() is { } limit && limit >= 0 ? limit : DefaultDailyLimit;

    /// <summary>只改每日调用上限，**不动文件里的其他键**（尤其是加密后的 api_key）。</summary>
    public static void SaveDailyLimit(int limit)
    {
        var json = ReadFile() ?? new JsonObject();
        json["daily_limit"] = Math.Max(0, limit);
        WriteFile(json);
    }

    /// <summary>存到设置文件（界面「保存」按钮走这里）。</summary>
    public static void Save(AiConfig config)
    {
        // 先读旧的：api_key 的密文与 daily_limit 必须保住 ——
        // 界面上「改模型名」这类动作不该顺手把 Key 清掉（那会变成「改一个字就得多贴一次 Key」）。
        var json = ReadFile() ?? new JsonObject();
        json["provider_id"] = config.ProviderId;
        json["base_url"] = config.BaseUrl;
        // **加密落盘**（DPAPI, CurrentUser）：挡住「文件被拷到别的机器/账户」这类风险。
        // 注意它挡不住同一用户下运行的程序 —— 别把它当保险箱。
        json["api_key"] = SecretStore.Protect(config.ApiKey);
        json["model"] = config.Model;
        json["timeout_seconds"] = config.TimeoutSeconds;
        json["max_retries"] = config.MaxRetries;
        WriteFile(json);
    }

    /// <summary>
    /// 实际使用的配置：**文件优先，其次环境变量**（文件是用户在界面上显式保存的，更权威）。
    /// </summary>
    public static AiConfig Load()
    {
        var saved = LoadSaved();
        var environment = new AiConfig
        {
            ProviderId = Environment.GetEnvironmentVariable(ProviderVariable) ?? "openai-compatible",
            BaseUrl = Environment.GetEnvironmentVariable(BaseUrlVariable) ?? string.Empty,
            ApiKey = Environment.GetEnvironmentVariable(ApiKeyVariable) ?? string.Empty,
            Model = Environment.GetEnvironmentVariable(ModelVariable) ?? string.Empty,
        };

        var providerId = Pick(saved?.ProviderId ?? string.Empty, environment.ProviderId);
        var model = Pick(saved?.Model ?? string.Empty, environment.Model);
        var baseUrl = Pick(saved?.BaseUrl ?? string.Empty, environment.BaseUrl);

        // OpenAI 兼容这一路：用户没填过模型/地址就按 DeepSeek 的默认值走
        // （1.1.0 的默认值，见 DefaultModel 的说明），这样「只粘一个 Key 就能用」。
        if (providerId == AiProviders.OpenAiCompatible)
        {
            model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model;
            baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl;
        }

        return new AiConfig
        {
            ProviderId = providerId,
            BaseUrl = baseUrl,
            ApiKey = Pick(saved?.ApiKey ?? string.Empty, environment.ApiKey),
            Model = model,
            TimeoutSeconds = saved?.TimeoutSeconds ?? environment.TimeoutSeconds,
            MaxRetries = saved?.MaxRetries ?? environment.MaxRetries,
        };
    }

    private static JsonObject? ReadFile()
    {
        if (!File.Exists(FilePath))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(File.ReadAllText(FilePath)) as JsonObject;
        }
        catch (JsonException)
        {
            // 配置文件坏了不该让整个应用起不来：当作没配置，界面上会提示。
            return null;
        }
    }

    private static void WriteFile(JsonObject json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string Pick(string preferred, string fallback) =>
        string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;

    /// <summary>能不能真的调用：Key 与 model 缺一不可（BaseUrl 空则用 provider 默认）。</summary>
    public static bool IsUsable(AiConfig config) =>
        !string.IsNullOrWhiteSpace(config.ApiKey) && !string.IsNullOrWhiteSpace(config.Model);

    /// <summary>没配置时给用户看的话（指向设置页，而不是只丢一句环境变量）。</summary>
    public static string ExplainMissing() =>
        "还没配置 AI：请到「设置」里填 API Key 与模型（也可以设环境变量 "
        + $"{ApiKeyVariable} / {ModelVariable}），保存后再回来。";
}
