using System.Text.Json.Nodes;
using QuizSync.Data;
using QuizSync.ServerBridge;

namespace QuizSync.Provider;

/// <summary>
/// 本机 Provider 与主机的会话：**探活 → 拿设备令牌（没有就自己配对）→ 提供已鉴权的客户端**。
///
/// 为什么要「自己配对」而不是走什么特权通道：Provider 就是一个**普通的已配对设备**
/// （协议 `spec/03-auth-pairing.md` 里没有第二种身份）。它跑在主机同一台机器上，
/// 所以能读主机数据目录里的 `control.token` 走本机控制面拿配对码 ——
/// 这条路径界面上的「连接设备」早就在用了（`SyncUploader`），这里是同一套语义。
///
/// **device_id 刻意不等于 `SyncUploader.DeviceId`（`windows-desktop`）**：
/// WS 中枢按 `device_id` **踢旧连接**（协议 `spec/05-websocket.md` §4）。若两者同名，
/// 「界面在推 op」与「Provider 在收派发」会互相把对方的连接踢掉，表现为随机断线重连。
/// 所以 Provider 用自己的 id。
/// </summary>
public sealed class HostSession : IDisposable
{
    /// <summary>Provider 的设备标识。见类注释：**不能**与 `windows-desktop` 取重。</summary>
    public const string ProviderDeviceId = "windows-provider";

    /// <summary>配对时报给主机的设备名（主机端「连接设备」列表里显示的就是它）。</summary>
    public const string ProviderDeviceName = "本机识别服务（Provider）";

    private const string TokenFile = "provider.token";

    private readonly HttpClient _http;

    private HostSession(string baseUrl, string token, HostClient client, HttpClient http)
    {
        BaseUrl = baseUrl;
        Token = token;
        Client = client;
        _http = http;
    }

    public string BaseUrl { get; }

    /// <summary>设备令牌明文（只在内存里；落盘的是 `dpapi:` 密文）。</summary>
    public string Token { get; }

    /// <summary>WS 地址（同一个端口，路径固定 `/ws`）。</summary>
    public Uri WebSocketUri => new($"{BaseUrl.Replace("http://", "ws://", StringComparison.Ordinal)}/ws");

    /// <summary>已鉴权的 HTTP 客户端（取图、拉合集等）。</summary>
    public HostClient Client { get; }

    /// <summary>
    /// 找主机并拿到可用令牌。找不到主机 → null（**不抛**：调用方要能区分「主机没开」
    /// 与「出错了」，前者是正常的待机状态）。
    /// </summary>
    public static async Task<HostSession?> ConnectAsync(CancellationToken cancellationToken = default)
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            var session = await ConnectCoreAsync(http, cancellationToken).ConfigureAwait(false);
            if (session is null)
            {
                http.Dispose();
            }

            return session;
        }
        catch
        {
            http.Dispose();
            throw;
        }
    }

    private static async Task<HostSession?> ConnectCoreAsync(HttpClient http, CancellationToken cancellationToken)
    {
        var probe = await new HostDiscovery(http).ProbeAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (probe is null)
        {
            return null;
        }

        var token = await EnsureTokenAsync(http, probe.BaseUrl, cancellationToken).ConfigureAwait(false);
        var client = new HostClient(probe.BaseUrl, http);
        client.UseToken(token);
        return new HostSession(probe.BaseUrl, token, client, http);
    }

    /// <summary>
    /// 取设备令牌：先用存下来的；主机不认（401，例如主机换过库/被吊销过）就**重新配对一次**。
    /// 返回的是已经验证可用的令牌。
    /// </summary>
    private static async Task<string> EnsureTokenAsync(HttpClient http, string baseUrl, CancellationToken cancellationToken)
    {
        var tokenPath = Path.Combine(AppDataDirectory.Ensure(), TokenFile);
        var stored = File.Exists(tokenPath) ? SecretStore.Unprotect(File.ReadAllText(tokenPath).Trim()) : string.Empty;

        if (!string.IsNullOrEmpty(stored) && await TokenWorksAsync(baseUrl, stored, http, cancellationToken).ConfigureAwait(false))
        {
            return stored;
        }

        var issued = await PairAsync(http, baseUrl, cancellationToken).ConfigureAwait(false);
        // **加密落盘**（与 AI Key 同一套 `SecretStore`）。
        File.WriteAllText(tokenPath, SecretStore.Protect(issued));
        return issued;
    }

    /// <summary>
    /// 令牌还能不能用：打一个**需要鉴权**的端点，只看是不是 401。
    ///
    /// ⚠️ 不能用 `/api/v1/info` —— 那个端点在服务端是**免鉴权豁免列表**里的
    /// （客户端靠它探测「主机在不在」），不带 token 也回 200。用它做判据的话，
    /// 任何一个陈旧令牌都会「验证通过」，然后 WS 握手 401、循环重连。
    /// </summary>
    private static async Task<bool> TokenWorksAsync(
        string baseUrl, string token, HttpClient http, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v1/collections");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        try
        {
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return response.StatusCode != System.Net.HttpStatusCode.Unauthorized;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    private static async Task<string> PairAsync(HttpClient http, string baseUrl, CancellationToken cancellationToken)
    {
        var directory = HostDataDirectory.Find()
            ?? throw new InvalidOperationException(HostDataDirectory.ExplainMissing());
        var controlToken = HostControl.FindControlToken(directory)
            ?? throw new InvalidOperationException($"读不到 {Path.Combine(directory, "control.token")}：主机没在跑？");

        var code = (await new HostControl(baseUrl, controlToken, http).PairCodeAsync(cancellationToken).ConfigureAwait(false))
            ?["code"]?.ToString();
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new InvalidOperationException("主机没有返回配对码");
        }

        var client = new HostClient(baseUrl, http);
        // 409（同 device_id 重复配对）在协议里算成功，并且由 `HostClient.PairAsync` 内部处理掉了 ——
        // 调用方不必知道它。**原来这里写的是 `JsonNode.Parse(error.Message)`**（第 167 轮实测必炸）：
        // `Message` 是 `"{code} ({status}): {body}"` 拼出来给人看的字符串，解析会先读到状态码那个
        // 数字、再撞上 ` (`，抛 `'(' is invalid after a single JSON value`。真实机器上
        // `provider.token` 一过期就会走到这条路，表现是「识别服务刚起来就退出，报个 JSON 错误」。
        var issued = await client.PairAsync(
                code!, ProviderDeviceId, ProviderDeviceName, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var token = issued?["token"]?.ToString();
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("主机没有返回访问令牌");
        }

        return token;
    }

    public void Dispose() => _http.Dispose();
}
