using QuizSync.Core;
using Xunit;

namespace QuizSync.Tests;

/// <summary>
/// 配对二维码：**深链逐字照 `protocol.md` §2.1**，并且编码出来的矩阵真的能解回原文。
///
/// 这两条分开测是有原因的：
///  * 深链格式错了 → 手机上的 `PairUri.parse` 认不出（用户只看到「配对失败」）；
///  * 矩阵画错了（掩码/纠错/静区）→ 手机**扫不出来**，而电脑上一切正常。
/// 只有「解码回原文」这一条能同时压住第二种。
/// </summary>
public sealed class PairQrTests
{
    [Fact]
    public void Payload_matches_the_protocol_example()
    {
        var payload = PairQr.BuildPayload("192.168.1.23", 8765, "482913", "5f1d0f9e-0000-4000-8000-000000000001");

        Assert.Equal(
            "quizsync://pair?host=192.168.1.23&port=8765&code=482913&sid=5f1d0f9e-0000-4000-8000-000000000001&v=1",
            payload);
    }

    [Fact]
    public void Payload_omits_sid_when_the_server_has_no_device_id_yet()
    {
        var payload = PairQr.BuildPayload("10.0.0.5", 8766, "000123", serverDeviceId: null);

        Assert.Equal("quizsync://pair?host=10.0.0.5&port=8766&code=000123&v=1", payload);
    }

    [Fact]
    public void Payload_rejects_a_blank_host_or_code()
    {
        Assert.Throws<ArgumentException>(() => PairQr.BuildPayload("  ", 8765, "482913", null));
        Assert.Throws<ArgumentException>(() => PairQr.BuildPayload("192.168.1.23", 8765, string.Empty, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => PairQr.BuildPayload("192.168.1.23", 0, "482913", null));
    }

    /// <summary>
    /// **往返**：编码 → 解码 = 原文。这是「手机扫得出来」在无设备条件下能做到的最强证明
    /// （ZXing 的解码器就是手机端扫码库同源的那一套算法）。
    /// </summary>
    [Theory]
    [InlineData("192.168.101.23", 8765, "482913", "5f1d0f9e-0000-4000-8000-000000000001")]
    [InlineData("10.0.0.5", 8770, "000001", null)]
    [InlineData("192.168.1.100", 8765, "999999", "0b0b0b0b-1111-4222-8333-444444444444")]
    public void Matrix_round_trips_through_the_decoder(string host, int port, string code, string? sid)
    {
        var payload = PairQr.BuildPayload(host, port, code, sid);

        var matrix = PairQr.Encode(payload);

        Assert.Equal(payload, PairQr.Decode(matrix));
        // 六七十字节的链接在 M 级纠错下版本 4–5（33×33 / 37×37）：模块够大，手机好扫。
        Assert.InRange(matrix.Size, 21, 45);
    }

    /// <summary>同一份内容编两次必须一模一样（渲染层靠它做"内容没变就不重画"）。</summary>
    [Fact]
    public void Encoding_is_deterministic()
    {
        var payload = PairQr.BuildPayload("192.168.101.23", 8765, "482913", "abc");

        var first = PairQr.Encode(payload);
        var second = PairQr.Encode(payload);

        Assert.Equal(first.Size, second.Size);
        for (var y = 0; y < first.Size; y++)
        {
            for (var x = 0; x < first.Size; x++)
            {
                Assert.Equal(first[x, y], second[x, y]);
            }
        }
    }

    /// <summary>三个定位角必须是「实心 7×7 + 一圈白 + 1 像素黑框」—— 扫不出来的第一嫌疑就是这里。</summary>
    [Fact]
    public void Finder_patterns_are_intact()
    {
        var matrix = PairQr.Encode(PairQr.BuildPayload("192.168.101.23", 8765, "482913", "abc"));

        foreach (var (originX, originY) in new[] { (0, 0), (matrix.Size - 7, 0), (0, matrix.Size - 7) })
        {
            for (var y = 0; y < 7; y++)
            {
                for (var x = 0; x < 7; x++)
                {
                    var border = x == 0 || y == 0 || x == 6 || y == 6;
                    var core = x is >= 2 and <= 4 && y is >= 2 and <= 4;
                    var expected = border || core;
                    Assert.Equal(expected, matrix[originX + x, originY + y]);
                }
            }
        }
    }
}
