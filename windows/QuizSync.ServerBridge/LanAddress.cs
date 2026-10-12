using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace QuizSync.ServerBridge;

/// <summary>
/// 找「手机能访问到的那一个」本机 IPv4 地址 —— 配对二维码里的 `host` 必须是它。
///
/// 为什么单独抽出来（第 168 轮）：服务端控制面 `GET /api/v1/pair/code` 返回的 `pair_uri`
/// 里 host 写死 `127.0.0.1`（那是给本机客户端用的）。把它直接画成二维码，手机扫到只会去连自己，
/// 而电脑这头看不出任何异常 —— 用户只会看到「扫码后配对失败」。
///
/// 选择规则（按优先级）：
///   ① 有默认网关的那个接口（真正通外网/局域网的那张网卡）；
///   ② 私有网段：192.168.x → 10.x → 172.16–31.x（家用路由器几乎都是 192.168）；
///   ③ 其它非回环、非链路本地地址（169.254.x 是拿不到 DHCP 时的自分配地址，必须排除）。
/// 一张网卡都没有时返回 null —— 界面照实说「找不到局域网地址」，不编一个出来。
/// </summary>
public static class LanAddress
{
    /// <summary>候选地址（给纯函数挑选用）。</summary>
    public readonly record struct Candidate(string Address, string Interface, bool HasGateway);

    /// <summary>纯函数：从候选里挑一个（没有合适的就 null）。可单测。</summary>
    public static Candidate? Pick(IEnumerable<Candidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var usable = candidates
            .Where(c => IPAddress.TryParse(c.Address, out var ip) && IsUsable(ip))
            .ToList();
        if (usable.Count == 0)
        {
            return null;
        }

        return usable
            .OrderByDescending(c => c.HasGateway)
            .ThenByDescending(c => Rank(IPAddress.Parse(c.Address)))
            .ThenBy(c => c.Interface, StringComparer.Ordinal)
            .First();
    }

    /// <summary>真的去问系统。</summary>
    public static string? Find()
    {
        try
        {
            var candidates = new List<Candidate>();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                var properties = nic.GetIPProperties();
                var hasGateway = properties.GatewayAddresses
                    .Any(g => g.Address is not null && !g.Address.Equals(IPAddress.Any));

                foreach (var address in properties.UnicastAddresses)
                {
                    if (address.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        candidates.Add(new Candidate(address.Address.ToString(), nic.Name, hasGateway));
                    }
                }
            }

            return Pick(candidates)?.Address;
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    private static bool IsUsable(IPAddress ip) =>
        !IPAddress.IsLoopback(ip) && !ip.ToString().StartsWith("169.254.", StringComparison.Ordinal);

    /// <summary>0 = 最像"家用局域网地址"。数字越大越靠后。</summary>
    private static int Rank(IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();
        if (bytes[0] == 192 && bytes[1] == 168)
        {
            return 0;
        }

        if (bytes[0] == 10)
        {
            return 1;
        }

        if (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
        {
            return 2;
        }

        return 3;
    }
}
