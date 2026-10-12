using QuizSync.Server.Core.Images;
using Xunit;

namespace QuizSync.Server.Tests;

/// <summary>
/// 图片宽高的解析（`spec/07-images.md` §1.8 第 3 条：上传时**真算**尺寸，不再恒 `null`）。
///
/// fixture 是 **ffmpeg 真实编码出来的文件头**（不是手搓的字节）：
/// * JPEG 截到 `SOF0` 之后 3 字节（336 字节），真实尺寸 1600×900；
/// * PNG 取前 64 字节（含 `IHDR`），真实尺寸 640×480。
///
/// 为什么要这么较真：手搓的文件头往往「刚好符合我写的解析器」，而真实编码器会插入
/// `APP0`/`APP13`/`DQT`/`DHT` 这些段 —— 解析器挪错一个字节，手搓 fixture 照样绿。
/// 另一条同样重要：**解不出来必须返回 null**，不许猜一个数字（那会被客户端当契约用）。
/// </summary>
public sealed class ImageDimensionsTests
{
    // ffmpeg: testsrc=size=1600x900 → test.jpg，截到 SOF0 + 3 字节
    private const string Jpeg1600x900Header =
        "/9j/4AAQSkZJRgABAgAAAQABAAD//gAPTGF2YzYzLjYuMTAxAP/bAEMACA4OEA4QExMTExMTFhUWFxcXFhYWFhcXFxkZGR0dHRkZGRcXGRkcHB0dICEgHh4dHiEhIyMjKiooKDExMjw8SP/EANkAAQABBQEBAAAAAAAAAAAAAAAHAwYCAQgFBAEBAAIDAQEBAAAAAAAAAAAAAAYDAgcBBQgEEAEAAQEDAwwOCAQDBQkBAQEAAQIDEQQhMRIWExWjYVFk4wVBklKRFNPiY9KiU4QHRcPRIqEGYoEy4Rdl5KRxQjPwI7HCNUM0JLLBgrODdLRzRJM2EQEAAQICAwoMAwYFAgUCBwEAAQIDBBEhFhUSBTGjkuNkUtOR0kHiU2NRE2EUogYipOFiMqHRcUKBI7EzZfDBcoJDk7Ik5DWztHSDRTQXc//AABEIA4QGQAMB";

    // ffmpeg: testsrc=size=640x480 → test.png，前 64 字节
    private const string Png640x480Header =
        "iVBORw0KGgoAAAANSUhEUgAAAoAAAAHgCAIAAAC6s0uzAAAACXBIWXMAAAABAAAAAQBPJcTWAAAN/klEQVR4nA==";

    private static byte[] RealJpeg() => Convert.FromBase64String(Jpeg1600x900Header);

    private static byte[] RealPng() => Convert.FromBase64String(Png640x480Header);

    [Fact]
    public void 真实JPEG文件头解出宽高()
    {
        Assert.Equal((1600, 900), ImageDimensions.Probe(RealJpeg()));
    }

    [Fact]
    public void 真实PNG文件头解出宽高()
    {
        Assert.Equal((640, 480), ImageDimensions.Probe(RealPng()));
    }

    [Fact]
    public void 认不出来就返回null而不是猜()
    {
        // 只有 SOI/EOI 的假 JPEG（`ImageTests.FakeJpeg` 就是这个形状）：没有 SOF，解不出来。
        Assert.Null(ImageDimensions.Probe([0xFF, 0xD8, 0xFF, 0xD9, 0, 0, 0, 0, 0, 0, 0, 0]));
        // 随机字节 / 空 / 太短
        Assert.Null(ImageDimensions.Probe([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12]));
        Assert.Null(ImageDimensions.Probe([]));
        Assert.Null(ImageDimensions.Probe([0xFF]));
    }

    [Fact]
    public void 截断的文件头不会越界也不会给出数字()
    {
        var jpeg = RealJpeg();
        // 把 SOF 段切掉一半：解析器必须停下来返回 null，而不是读到缓冲区外面。
        for (var cut = 2; cut < jpeg.Length; cut += 37)
        {
            var probed = ImageDimensions.Probe(jpeg.AsSpan(0, cut));
            if (probed is { } size)
            {
                // 万一在某个截断点上已经读全了 SOF，那也必须是真实尺寸，不能是垃圾值。
                Assert.Equal((1600, 900), size);
            }
        }

        var png = RealPng();
        Assert.Null(ImageDimensions.Probe(png.AsSpan(0, 16)));
        Assert.Null(ImageDimensions.Probe(png.AsSpan(0, 8)));
    }
}
