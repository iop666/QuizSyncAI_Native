namespace QuizSync.Server.Core.Images;

/// <summary>
/// 从图片字节里解出真实宽高（**不解码像素，只读文件头**）。
///
/// 为什么要有它：协议 `spec/07-images.md` §1.1 的现状是「`width`/`height` 从不计算，首次上传恒为
/// `null`」，而 §1.8 把「真算」列为 v2 目标 —— 客户端拿不到尺寸就没法算缩略图比例、
/// 也没法在结果页按原图比例占位（会先闪一下再跳）。这里补的就是那一条。
///
/// 只认两种容器，够用且**不引入图像库**（本机没有可用的解码器依赖，也不该为一个字段引进来）：
/// * **JPEG**：扫段找 `SOFn`（`FFC0`–`FFCF`，排除 `C4`/`C8`/`CC` 这三个非 SOF 的码），
///   后面 3 字节是 `精度 / 高 / 宽`（大端 16 位）；
/// * **PNG**：`IHDR` 的宽高（偏移 16..24，大端 32 位）。
///
/// 认不出来就返回 null（**不猜**）：上传响应里 `width`/`height` 宁可如实为 `null`，
/// 也不要写一个编出来的数字 —— 那个数字会被客户端当契约用。
/// </summary>
public static class ImageDimensions
{
    /// <summary>解出的宽高；解不出来返回 null。</summary>
    public static (int Width, int Height)? Probe(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4)
        {
            return null;
        }

        // PNG：89 50 4E 47 0D 0A 1A 0A，随后 IHDR 的长度(4)+类型(4)，数据从 16 开始。
        if (bytes.Length >= 24 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            var width = ReadBigEndianInt32(bytes[16..20]);
            var height = ReadBigEndianInt32(bytes[20..24]);
            return width > 0 && height > 0 ? (width, height) : null;
        }

        // JPEG：FF D8 开头。
        if (bytes[0] != 0xFF || bytes[1] != 0xD8)
        {
            return null;
        }

        var offset = 2;
        while (offset + 9 < bytes.Length)
        {
            // 段标记必须是 FF xx；遇到填充的 FF 就往后挪一格（合法且常见）。
            if (bytes[offset] != 0xFF)
            {
                offset++;
                continue;
            }

            var marker = bytes[offset + 1];
            if (marker == 0xFF)
            {
                offset++;
                continue;
            }

            // 无长度字段的标记：SOI/EOI/RSTn/TEM。
            if (marker is 0xD8 or 0xD9 or 0x01 || (marker >= 0xD0 && marker <= 0xD7))
            {
                offset += 2;
                continue;
            }

            var length = (bytes[offset + 2] << 8) | bytes[offset + 3];
            if (length < 2)
            {
                return null;
            }

            // SOF0..SOF15，去掉 C4(DHT)/C8(JPG)/CC(DAC) —— 这三个不是帧头。
            var isFrameHeader = marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
            if (isFrameHeader)
            {
                if (offset + 9 >= bytes.Length)
                {
                    return null;
                }

                var height = (bytes[offset + 5] << 8) | bytes[offset + 6];
                var width = (bytes[offset + 7] << 8) | bytes[offset + 8];
                return width > 0 && height > 0 ? (width, height) : null;
            }

            // 扫到压缩数据就停：后面不会再出现帧头。
            if (marker == 0xDA)
            {
                return null;
            }

            offset += 2 + length;
        }

        return null;
    }

    private static int ReadBigEndianInt32(ReadOnlySpan<byte> four) =>
        (four[0] << 24) | (four[1] << 16) | (four[2] << 8) | four[3];
}
