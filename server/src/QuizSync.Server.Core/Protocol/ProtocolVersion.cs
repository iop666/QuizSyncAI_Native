namespace QuizSync.Server.Core.Protocol;

/// <summary>协议版本常量。**唯一来源**是这里（不许在别处写死数字）。</summary>
public static class ProtocolVersion
{
    /// <summary>v2 客户端用的协议主版本（路径 `/api/v2/*`、协商头 `X-QS-Protocol`）。</summary>
    public const int V2 = 2;

    /// <summary>v1 冻结的协议版本（`/info` 的 `protocol_version` 与 WS `hello` 里都写 1）。</summary>
    public const int V1 = 1;

    /// <summary>
    /// v1 兼容层在**版本协商**里呈现的主版本。
    ///
    /// 为什么不是 <see cref="V2"/>：v1 的协商比的是**应用版本的主版本**，而 v2 主机的
    /// 应用版本是 2.x —— 拿它去比，任何 1.x 老客户端都会收到 426，兼容层等于不存在。
    /// 所以 v1 路径上固定以主版本 1 参与协商（一致性向量 `auth.ndjson` 22/23 与
    /// `errors.ndjson` 2–4 钉的就是这个行为）。
    /// </summary>
    public const int V1CompatMajor = 1;
}
