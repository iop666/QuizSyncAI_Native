using ZXing;
using ZXing.Common;
using ZXing.QrCode;

namespace QuizSync.Core;

/// <summary>
/// 配对二维码（`protocol.md` §2.1 的深链 + `SPEC.md` §2.3 的「连接设备」页）。
///
/// **这一段是纯计算**（深链拼装 + QR 矩阵），不碰界面也不碰网络 ——
/// 于是「二维码里放的到底是什么」可以单测，而不是只能靠手机扫一下试试。
///
/// 为什么这事重要：二维码是**配对唯一不需要令牌**的入口，内容错一个字段
/// （host 写成 `127.0.0.1`、漏掉 `sid`、code 过期没刷新）手机那头只会说「配对失败」，
/// 从电脑上完全看不出问题在哪。`docs/protocol.md` 第 31–37 行就是判据。
/// </summary>
public static class PairQr
{
    /// <summary>协议版本（`protocol.md` §2.1 的 `v=1`）。</summary>
    public const int ProtocolVersion = 1;

    /// <summary>
    /// 深链全文：`quizsync://pair?host=…&port=…&code=…&sid=…&v=1`。
    ///
    /// ⚠️ `host` 必须是**手机能访问到的那个地址**（局域网 IP），
    /// 不能是主机自己控制面里写的 `127.0.0.1`（那是给本机客户端用的）。
    /// 第 168 轮实测：服务端 `GET /api/v1/pair/code` 返回的 `pair_uri` 里就是 `127.0.0.1`，
    /// 直接拿它做二维码，手机扫到只会去连自己。
    /// </summary>
    public static string BuildPayload(string host, int port, string code, string? serverDeviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(port);

        var payload = $"quizsync://pair?host={Uri.EscapeDataString(host)}&port={port}"
            + $"&code={Uri.EscapeDataString(code)}";
        if (!string.IsNullOrWhiteSpace(serverDeviceId))
        {
            payload += $"&sid={Uri.EscapeDataString(serverDeviceId)}";
        }

        return payload + $"&v={ProtocolVersion}";
    }

    /// <summary>QR 矩阵（`true` = 黑模块）。行优先：`this[y, x]`。</summary>
    public sealed class Matrix(int size, bool[,] modules)
    {
        public int Size { get; } = size;

        public bool this[int x, int y] => modules[y, x];
    }

    /// <summary>
    /// 编码成矩阵。纠错级别 M（约 15% 冗余）：配对链接只有六七十字节，
    /// M 级下版本 4–5 就够，模块大、手机好扫；H 级会把矩阵撑大、反而更糊。
    /// </summary>
    public static Matrix Encode(string payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);

        var writer = new QRCodeWriter();
        var hints = new Dictionary<EncodeHintType, object>
        {
            [EncodeHintType.CHARACTER_SET] = "UTF-8",
            [EncodeHintType.ERROR_CORRECTION] = ZXing.QrCode.Internal.ErrorCorrectionLevel.M,
            // 静区（quiet zone）由渲染层统一留白，这里只要模块。
            [EncodeHintType.MARGIN] = 0,
        };

        var bits = writer.encode(payload, BarcodeFormat.QR_CODE, 0, 0, hints)
            ?? throw new InvalidOperationException("二维码编码失败");

        var size = bits.Width;
        var modules = new bool[size, size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                modules[y, x] = bits[x, y];
            }
        }

        return new Matrix(size, modules);
    }

    /// <summary>
    /// 把矩阵解回文本。**只给测试与取证用**：`tools\verify_pair_qr.ps1` 靠它把
    /// 「屏幕上那张二维码」解回深链，证明画出来的确实是屏幕上该有的东西。
    /// </summary>
    public static string? Decode(Matrix matrix)
    {
        ArgumentNullException.ThrowIfNull(matrix);

        return DecodeLuminance(matrix.Size, matrix.Size, (x, y) => matrix[x, y] ? 0 : 255);
    }

    /// <summary>
    /// 从截图里的**黑白图**解出二维码（取证用）。
    /// [luminance] 是 `(x, y) → 0..255` 的取样函数，[width]/[height] 是图幅。
    /// </summary>
    public static string? DecodeLuminance(int width, int height, Func<int, int, int> luminance)
    {
        ArgumentNullException.ThrowIfNull(luminance);

        var source = new LuminanceFromFunction(width, height, luminance);
        // 二维码区域理论上已经是纯黑白，用 HybridBinarizer（ZXing 的默认二值化）即可。
        var bitmap = new BinaryBitmap(new HybridBinarizer(source));
        return new QRCodeReader().decode(bitmap)?.Text;
    }

    /// <summary>`LuminanceSource` 的最小实现：直接问调用方要某个像素的灰度。</summary>
    private sealed class LuminanceFromFunction : LuminanceSource
    {
        private readonly Func<int, int, int> _luminance;
        private readonly int _width;
        private readonly byte[] _row;

        public LuminanceFromFunction(int width, int height, Func<int, int, int> luminance)
            : base(width, height)
        {
            _luminance = luminance;
            _width = width;
            _row = new byte[width];
        }

        public override byte[] getRow(int y, byte[] row)
        {
            var target = row is { Length: > 0 } && row.Length >= _width ? row : new byte[_width];
            for (var x = 0; x < _width; x++)
            {
                target[x] = (byte)Math.Clamp(_luminance(x, y), 0, 255);
            }

            return target;
        }

        public override byte[] Matrix
        {
            get
            {
                var all = new byte[_width * Height];
                for (var y = 0; y < Height; y++)
                {
                    getRow(y, _row);
                    Array.Copy(_row, 0, all, y * _width, _width);
                }

                return all;
            }
        }
    }
}
