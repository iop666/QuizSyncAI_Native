using System.Net;
using System.Text;
using QuizSync.Server.Core;

namespace QuizSync.Server.Tests;

/// <summary>
/// **机械化的负向鉴权**：枚举真实路由表，逐条**匿名**去打；任何 2xx 都算泄漏。
///
/// 由来：v2 路由加进来时，鉴权中间件只判 `/api/v1/` 前缀 → `/api/v2/*` 整段裸奔
/// （局域网内未配对设备能直接拉同步 op）。人眼盯不住新路由，所以让测试按路由表逐条打。
/// </summary>
public sealed class RouteAuthCoverageTests
{
    /// <summary>设计上免鉴权的路由（改这里等于改协议，必须写进 DECISIONS）。</summary>
    private static readonly string[] Exempt =
    [
        "/health",
        "/api/v1/info",
        "/api/v2/info",
        "/api/v1/pair",
        "/api/v1/pair/code",
        "/api/v1/pair/code/refresh",
    ];

    private static bool IsExempt(string route) =>
        Exempt.Any(e => route.Equals(e, StringComparison.Ordinal)
            || route.StartsWith(e + "/", StringComparison.Ordinal));

    private static async Task<(QuizSyncHost Host, HttpClient Client)> StartAsync()
    {
        var host = await QuizSyncHost.StartAsync(new ServerOptions
        {
            PreferredPort = 0,
            ServerName = "AUTH-COVERAGE",
            AppVersion = "2.0.0",
            // 控制面令牌：免鉴权路由要靠它把关，这里给一个合法值。
            ControlToken = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
        });
        return (host, new HttpClient { BaseAddress = new Uri(host.BaseUrl) });
    }

    [Fact]
    public async Task Every_api_route_rejects_anonymous_access()
    {
        var (host, client) = await StartAsync();
        await using (host)
        using (client)
        {
            var routes = host.ApiRoutePatterns;
            Assert.NotEmpty(routes);

            var leaks = new List<string>();
            foreach (var route in routes)
            {
                if (IsExempt(route))
                {
                    continue;
                }

                // 路由模板里的 {id} 换成实际值；方法不确定就 GET 与 POST 都试。
                var path = route.Replace("{id}", "x", StringComparison.Ordinal)
                    .Replace("{taskId}", "x", StringComparison.Ordinal);

                foreach (var method in new[] { HttpMethod.Get, HttpMethod.Post })
                {
                    using var request = new HttpRequestMessage(method, path);
                    if (method == HttpMethod.Post)
                    {
                        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                    }

                    using var response = await client.SendAsync(request);
                    if ((int)response.StatusCode is >= 200 and < 300)
                    {
                        leaks.Add($"{method} {route} → {(int)response.StatusCode}");
                    }
                }
            }

            Assert.True(leaks.Count == 0,
                "有路由允许匿名访问（应返回 401）：\n" + string.Join("\n", leaks) + "\n\n路由表：\n" + string.Join("\n", routes));
        }
    }

    [Fact]
    public async Task Exempt_routes_are_still_reachable_anonymously()
    {
        var (host, client) = await StartAsync();
        await using (host)
        using (client)
        {
            // 免鉴权清单不能变成「全都 401」——那样客户端连版本协商都做不了。
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/info")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v2/info")).StatusCode);
            // 控制面：免设备 Bearer，但仍要控制令牌（无令牌必须是 403/401，不能 200）。
            using var bare = new HttpClient { BaseAddress = new Uri(host.BaseUrl) };
            var code = await bare.GetAsync("/api/v1/pair/code");
            Assert.NotEqual(HttpStatusCode.OK, code.StatusCode);
        }
    }
}
