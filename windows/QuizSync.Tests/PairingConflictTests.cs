using QuizSync.ServerBridge;
using Xunit;

namespace QuizSync.Tests;

/// <summary>
/// 「同一个 `device_id` 重复配对」这条协议规则的回归（第 167 轮）。
///
/// 为什么单独一条：协议规定 **409 = 重复配对算成功**（服务端照样发新 token，旧的作废），
/// 而两个调用方（`QuizSync.Provider` 的 `HostSession`、桌面端的 `SyncUploader`）都自己写成
/// `JsonNode.Parse(error.Message)`。`HostException.Message` 是 `"{code} ({status}): {body}"`
/// 拼出来**给人看的**字符串，解析必然抛：
/// `'(' is invalid after a single JSON value. Expected end of data. LineNumber: 0 | BytePositionInLine: 4`。
///
/// 真实后果（第 167 轮实测，与"主机内嵌"无关 —— 独立进程的主机一样复现）：
/// 机器上 `provider.token` 一旦过期（主机换过库 / 设备被吊销过），识别服务一启动就走到 409 这条路，
/// 于是**立刻退出**并打印一个莫名其妙的 JSON 错误。修法是把这条协议规则收进
/// `HostClient.PairAsync`（`PairOrConflictAsync`），调用方不再自己解析。
///
/// 宿主直接内嵌在测试进程里（随机端口）：这样这条用例**不需要**任何外部依赖就能起一台真主机。
/// </summary>
[Collection("host-launcher")]
public sealed class PairingConflictTests
{
    [Fact]
    public async Task Re_pairing_the_same_device_is_treated_as_success()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), $"qs-pair-409-{Guid.NewGuid():N}");
        var host = await EmbeddedHost.StartAsync(dataDirectory, port: 0);

        try
        {
            var control = new HostControl(
                host.BaseUrl, HostControl.ReadOrCreateControlToken(dataDirectory));
            var code = (await control.PairCodeAsync())!["code"]!.GetValue<string>();

            var client = new HostClient(host.BaseUrl);
            var first = await client.PairAsync(code, "windows-provider", "测试 Provider");
            var firstToken = first!["token"]!.GetValue<string>();
            Assert.NotEmpty(firstToken);

            // 第二次同 device_id：主机会回 409（这就是以前炸掉的那条路）。
            // 断言里必须**证明走的是 409**，否则这条用例可能只是走了一遍正常的 200 路径、
            // 什么都没验到 —— `already_paired` 是冲突响应体独有的字段（实测：
            // `{"token":"…","server_device_id":"…","already_paired":true}`）。
            var second = await client.PairAsync(code, "windows-provider", "测试 Provider");

            Assert.NotNull(second);
            Assert.True(
                second!["already_paired"]?.GetValue<bool>() ?? false,
                "第二次配对必须走 409 那条路（响应体带 already_paired=true），否则这条用例什么都没验到");
            Assert.NotEmpty(second["token"]!.GetValue<string>());
        }
        finally
        {
            await host.DisposeAsync();

            try
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
            catch (IOException)
            {
                // 临时目录清不掉不该让测试变红
            }
        }
    }
}
