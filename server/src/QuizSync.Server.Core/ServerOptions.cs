namespace QuizSync.Server.Core;

/// <summary>
/// Host 的运行参数。默认值与 <c>spec/02-transport.md</c> 的数值表一一对应：
/// 端口 8765、探测范围 6（8765–8770）、绑定 <c>0.0.0.0</c>、明文 HTTP。
/// </summary>
public sealed record ServerOptions
{
    /// <summary>默认端口（协议固定值，不要改成别的）。</summary>
    public const int DefaultPort = 8765;

    /// <summary>端口被占用时向上探测的个数：8765–8770。</summary>
    public const int DefaultPortRange = 6;

    /// <summary>本实现自述的协议主版本。</summary>
    public const int ProtocolVersion = 2;

    /// <summary>v1 兼容层自述的协议版本（老客户端只认 1）。</summary>
    public const int V1ProtocolVersion = 1;

    public string DeviceId { get; init; } = "windows-local";

    public string ServerName { get; init; } = Environment.MachineName;

    public string Platform { get; init; } = "windows";

    public string AppVersion { get; init; } = "2.0.0";

    /// <summary>0 = 由系统分配空闲端口（测试用）。</summary>
    public int PreferredPort { get; init; } = DefaultPort;

    public int PortRange { get; init; } = DefaultPortRange;

    public string BindAddress { get; init; } = "0.0.0.0";

    /// <summary>数据目录（库 / 图片 / 日志）。null = 未决定，由调用方给。</summary>
    public string? DataDirectory { get; init; }

    /// <summary>是否在 <c>/info</c> 里自述已配置 AI（钥匙串由 CLI 提供）。</summary>
    public bool AiConfigured { get; init; }

    /// <summary>本机控制面令牌：留空表示不开放本机命令行端点。</summary>
    public string? ControlToken { get; init; }
}
