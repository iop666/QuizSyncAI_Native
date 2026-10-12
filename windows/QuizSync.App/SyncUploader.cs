using System.IO;
using System.Text.Json.Nodes;
using QuizSync.Data;
using QuizSync.ServerBridge;
using QuizSync.Provider;

namespace QuizSync.App;

/// <summary>
/// 本机（桌面端）与主机的**已鉴权连接**，以及基于它的三件事：推 op、列设备、吊销设备。
///
/// **为什么需要它**（第十一轮发现）：电脑识别完只写自己的本地库、从不 push，
/// 所以手机端永远拉不到东西 —— 我验 Android 拉取时只能用脚本给服务端「播种」。
/// 补上这一段，双端才算真的闭环。
///
/// 自己与主机配对拿设备令牌（用控制面读配对码 → `POST /api/v1/pair`），
/// 令牌与推送水位存在 `<app>/userdata/` 下。
/// **注意**：令牌是密钥，这里先落普通文件（与控制令牌同一处理），
/// 迁到 DPAPI 单独一轮做（已记在 DECISIONS）。
/// </summary>
public static class SyncUploader
{
    /// <summary>
    /// 本应用的设备标识。**不能等于主机自己的 device_id**（主机是 `windows-local`）。
    ///
    /// 第十二轮实测撞到：原来这里写的就是 `windows-local`，于是服务端认为这些 op
    /// 「声称由主机自己产生」，按防冒充规则**静默跳过**（`applied`/`rejected` 都不计）——
    /// 界面显示「已推送 6 条改动（主机接受 0、拒绝 0）」，服务端一条都没落。
    /// 规则本身写得很清楚（`SyncService.cs` 第 17 行），是我把 id 取重了。
    /// </summary>
    public const string DeviceId = "windows-desktop";
    private const string TokenFile = "device.token";
    private const string WatermarkFile = "sync.watermark";

    /// <summary>
    /// 拿一个**已鉴权**的主机客户端：本地有令牌就用它；没有就用控制面读配对码、自己配对一次。
    ///
    /// 单独抽出来是因为设置页的「已配对设备」也要用同一条连接 —— 再写一份必然分叉
    /// （这个项目里「两份实现」已经被证伪过很多次）。
    /// </summary>
    public static async Task<HostClient> ConnectAsync(string baseUrl, string controlToken)
    {
        var directory = AppDataDirectory.Ensure();
        var client = new HostClient(baseUrl);

        var tokenPath = Path.Combine(directory, TokenFile);
        var token = File.Exists(tokenPath) ? SecretStore.Unprotect(File.ReadAllText(tokenPath).Trim()) : null;
        if (string.IsNullOrEmpty(token))
        {
            // 自己跟主机配对：配对码本来就是我们（控制面）读出来显示给用户的。
            var control = new HostControl(baseUrl, controlToken);
            var pairCode = await control.PairCodeAsync().ConfigureAwait(false);
            var code = pairCode?["code"]?.ToString();
            if (string.IsNullOrWhiteSpace(code))
            {
                throw new InvalidOperationException("读不到配对码，无法取得设备令牌");
            }

            // **409 = 重复配对，在协议里算成功**（服务端照样发新 token，见 spec/04-http-api.md；
            // Android 侧一直按这个语义处理）。这条规则现在由 `HostClient.PairAsync` 内部处理，
            // 这里不必再抓 —— 原来这里写的是 `JsonNode.Parse(error.Message)`，而 `Message` 是
            // `"{code} ({status}): {body}"` 拼出来给人看的字符串，解析必然抛
            // `'(' is invalid after a single JSON value`（第 167 轮实测；同一处 `QuizSync.Provider`
            // 的 `HostSession` 也踩了，两处一起改）。
            var issued = await client.PairAsync(code!, DeviceId, Environment.MachineName).ConfigureAwait(false);
            var issuedToken = issued?["token"]?.ToString();

            token = issuedToken;
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException("主机没有返回访问令牌");
            }

            // **加密落盘**（与 AI Key 同一套 `SecretStore`）：设备令牌原来也是明文。
            File.WriteAllText(tokenPath, SecretStore.Protect(token));
        }

        client.UseToken(token);
        return client;
    }

    /// <summary>
    /// 把本地存的设备令牌**丢掉**（下次 <see cref="ConnectAsync"/> 会拿控制面读到的配对码重新配对一次）。
    ///
    /// 只在「主机明确说这个令牌不认」时调（见 <see cref="WithReauthAsync"/>）。
    /// </summary>
    public static void ForgetToken()
    {
        var path = Path.Combine(AppDataDirectory.Ensure(), TokenFile);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// 带**自愈**的已鉴权调用：主机回 401 就丢掉旧令牌、重新配对一次、**把这次调用重试一次**。
    ///
    /// 为什么必须有它（第 170 轮，用户报「安卓也不同步」）：桌面端的设备令牌是一份
    /// **可以被动失效**的凭据 —— 服务端 `PairingService` 每次配对都会发新令牌并覆盖
    /// `devices.token_hash`（`paired_at` 保留首次配对时间，所以从库上看不出来换过），
    /// 于是**任何另一个用同一个 `device_id` 的应用实例配一次对，用户这台机器的令牌就废了**。
    /// 实测到的正是这一条：本机 `device.token` 的 SHA-256 是 `6d595174…`，
    /// 而主机 `devices` 表里 `windows-desktop` 存的是 `0473bf03…` ——
    /// 2026-09-30 01:39/01:40 两次识别 22 道题全部推失败（`unauthorized (401): token 无效`），
    /// 水位停在 38，手机端两天都看不到东西；而日志里只有一行、界面上一个字都没有。
    ///
    /// **为什么是自愈而不是「让用户重新扫码」**：这个应用本来就持有控制面令牌
    /// （配对码就是它读出来给用户看的），重新配对是它自己能做的事；
    /// 把「令牌被别人换掉了」这种内部状态丢给用户，只能得到一句「手机还是不同步」。
    /// </summary>
    private static async Task<(T Value, bool Repaired)> WithReauthAsync<T>(
        string baseUrl, string controlToken, Func<HostClient, Task<T>> action)
    {
        try
        {
            return (await action(await ConnectAsync(baseUrl, controlToken).ConfigureAwait(false))
                .ConfigureAwait(false), false);
        }
        catch (HostException error) when (error.StatusCode == 401)
        {
            ForgetToken();
            var retried = await action(await ConnectAsync(baseUrl, controlToken).ConfigureAwait(false))
                .ConfigureAwait(false);
            return (retried, true);
        }
    }

    /// <summary>
    /// 已配对设备列表（界面「连接设备 → 已配对设备」）。
    /// 返回 (device_id, name, platform, paired_at, last_seen_at, revoked) 六元的数组，
    /// **不含 token_hash**（服务端本来就不回传，见 `spec/01-conventions.md`）。
    /// </summary>
    public static async Task<IReadOnlyList<PairedDevice>> ListDevicesAsync(string baseUrl, string controlToken)
    {
        var payload = (await WithReauthAsync(baseUrl, controlToken, c => c.DevicesAsync())
            .ConfigureAwait(false)).Value;
        var list = new List<PairedDevice>();
        if (payload?["devices"] is not JsonArray array)
        {
            return list;
        }

        foreach (var node in array)
        {
            if (node is not JsonObject device)
            {
                continue;
            }

            var id = device["device_id"]?.ToString();
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            list.Add(new PairedDevice(
                id!,
                device["name"]?.ToString() ?? "(未命名)",
                device["platform"]?.ToString() ?? "?",
                device["paired_at"]?.GetValue<long>() ?? 0,
                device["last_seen_at"]?.GetValue<long>() ?? 0,
                device["revoked_at"] is not null && device["revoked_at"]!.GetValueKind() != System.Text.Json.JsonValueKind.Null));
        }

        return list;
    }

    /// <summary>
    /// 吊销一台设备（软删：服务端置 `revoked_at`，它的令牌立刻 401）。
    /// 返回给用户看的一句话；失败**如实抛出**，由界面显示原因，不静默。
    /// </summary>
    public static async Task<string> RevokeDeviceAsync(string baseUrl, string controlToken, PairedDevice device)
    {
        await WithReauthAsync(baseUrl, controlToken, c => c.RevokeDeviceAsync(device.DeviceId))
            .ConfigureAwait(false);
        return $"已吊销「{device.Name}」的访问令牌，它需要重新扫码配对";
    }

    /// <summary>推送尚未上传的 op；返回一句给用户看的结果。</summary>
    public static async Task<string> PushPendingAsync(QuizDatabase database, string baseUrl, string controlToken)
    {
        var since = ReadWatermark();
        var store = new LocalStore(database, DeviceId);
        var ops = store.LocalOpsSince(since);
        if (ops.Count == 0)
        {
            return "没有待推送的改动";
        }

        var body = new JsonObject { ["ops"] = new JsonArray([.. ops.Select(op => (JsonNode)op.ToJson())]) };
        var (result, repaired) = await WithReauthAsync(
                baseUrl, controlToken, c => c.PushOpsAsync(body.ToJsonString()))
            .ConfigureAwait(false);
        var applied = result?["applied"]?.GetValue<int>() ?? 0;
        var rejected = result?["rejected"]?.GetValue<int>() ?? 0;

        // 只有主机**收下了**才推进水位：被拒的 op 下次还会重推（否则就永久丢了）。
        if (rejected == 0)
        {
            WriteWatermark(ops.Max(op => op.Lamport));
        }

        return $"已推送 {ops.Count} 条改动（主机接受 {applied}、拒绝 {rejected}）"
             + (repaired ? "；设备令牌已失效，已自动重新配对" : string.Empty);
    }

    /// <summary>
    /// 还有多少条 op 没推上去（按水位文件算）。**0 = 本机与主机一致**。
    ///
    /// 加它的原因（第 168 轮，用户报「安卓端也不同步」）：推送原来只挂在
    /// 「本机识别成功之后」这一个时机上 —— 删记录、改答案、建合集产生的 op 要等到
    /// **下一次识别成功**才顺路推上去。而识别一直在失败，于是 9/27 那次真实识别
    /// 产生的 3 条 op 在本地躺了两天，手机端当然看不到（水位文件停在 35、库里最大 38）。
    /// 有了这个计数，App 里那个定时补推才知道「有没有事要做」。
    /// </summary>
    public static int PendingCount(QuizDatabase database) =>
        new LocalStore(database, DeviceId).LocalOpsSince(ReadWatermark()).Count;

    /// <summary>水位文件里的 lamport（没有 / 读坏了都当 0，等于全量重推一次）。</summary>
    public static long ReadWatermark()
    {
        var path = Path.Combine(AppDataDirectory.Ensure(), WatermarkFile);
        return File.Exists(path) && long.TryParse(File.ReadAllText(path).Trim(), out var parsed) ? parsed : 0L;
    }

    private static void WriteWatermark(long lamport) => File.WriteAllText(
        Path.Combine(AppDataDirectory.Ensure(), WatermarkFile),
        lamport.ToString(System.Globalization.CultureInfo.InvariantCulture));
}

/// <summary>一台已配对设备（`GET /api/v1/devices` 的一行）。</summary>
public sealed record PairedDevice(
    string DeviceId, string Name, string Platform, long PairedAt, long LastSeenAt, bool Revoked);
