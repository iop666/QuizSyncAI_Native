using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace QuizSync.App;

/// <summary>
/// 悬浮球的三态。出处 ui-spec-desktop §6.6（<c>desktop/lib/services/floating_ball.dart:19-22</c>）。
/// </summary>
internal enum BallState
{
    /// <summary>待识别 —— 蓝 <c>0xFF1D86FC</c>。</summary>
    Idle,

    /// <summary>识别中 —— 黄 <c>0xFFFBD705</c>。</summary>
    Detecting,

    /// <summary>多页模式 —— 绿 <c>0xFF1EB945</c>。</summary>
    MultiPage,
}

internal static class BallVisuals
{
    /// <summary>状态对应的球素材文件名（与 1.1.0 的 <c>assets/floating_ball_*.png</c> 逐字对应）。</summary>
    public static string AssetName(this BallState state) => state switch
    {
        BallState.Detecting => "ball_detecting.png",
        BallState.MultiPage => "ball_multipage.png",
        _ => "ball_idle.png",
    };

    /// <summary>状态主色（0xAARRGGBB）。描边色由它按通道 ×0.72 算出来。</summary>
    public static uint Argb(this BallState state) => state switch
    {
        BallState.Detecting => 0xFFFBD705,
        BallState.MultiPage => 0xFF1EB945,
        _ => 0xFF1D86FC,
    };

    /// <summary>设置页里显示的中文名（§6.6 的表）。</summary>
    public static string Label(this BallState state) => state switch
    {
        BallState.Detecting => "识别中",
        BallState.MultiPage => "多页模式",
        _ => "待识别",
    };
}

/// <summary>
/// 球素材的解码与缩放缓存。
///
/// **为什么要缓存**（AGENTS.md 的 M14 实测结论）：球图是固定素材，早期实现每次切状态都
/// 重新 <c>decodePng</c> + <c>copyResize</c> + 逐像素预乘，进程内探针反复切 300 次后
/// RSS 从 163.5 MB 涨到 216.6 MB 且仍在涨（用户观感「打开悬浮球内存飙升」）。
/// 按「状态 + 尺寸」缓存**预乘后的 BGRA** 之后，内存停住、GDI/USER 句柄也稳定。
///
/// 用 GDI+ 而不是 WIC：本仓库的 <c>QuizSync.Provider</c> 已经把
/// <c>System.Drawing.Common</c> 引进来了（<c>ScreenJpeg</c> 就用它），
/// WIC 要写 150 行 COM 互操作，不值得。
/// </summary>
internal static class BallArtwork
{
    private static readonly Dictionary<(BallState State, int Size), byte[]> Cache = [];
    private static readonly object Gate = new();

    /// <summary>取某个状态的球素材，缩放到 <paramref name="size"/>，返回**预乘 alpha 的 BGRA**。</summary>
    [SupportedOSPlatform("windows")]
    public static byte[] Get(BallState state, int size)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue((state, size), out var cached))
            {
                return cached;
            }

            var decoded = Decode(state, size);
            Cache[(state, size)] = decoded;
            return decoded;
        }
    }

    /// <summary>素材文件的绝对路径。资源随包放在 <c>Assets\</c> 下（csproj 的 Content）。</summary>
    public static string PathOf(BallState state) =>
        Path.Combine(AppContext.BaseDirectory, "Assets", state.AssetName());

    [SupportedOSPlatform("windows")]
    private static byte[] Decode(BallState state, int size)
    {
        var path = PathOf(state);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"找不到悬浮球素材：{path}", path);
        }

        using var source = new Bitmap(path);
        using var scaled = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(scaled))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.DrawImage(source, new Rectangle(0, 0, size, size));
        }

        var locked = scaled.LockBits(
            new Rectangle(0, 0, size, size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var pixels = new byte[size * size * 4];
            Marshal.Copy(locked.Scan0, pixels, 0, pixels.Length);

            // GDI+ 的 32bppArgb 是**直通 alpha**，而分层窗口要的是**预乘** alpha —— 这里转一次。
            // 不转的话半透明的边缘会发黑（`ULW_ALPHA` 的语义决定了这一点）。
            for (var i = 0; i < pixels.Length; i += 4)
            {
                var alpha = pixels[i + 3];
                pixels[i + 0] = (byte)(pixels[i + 0] * alpha / 255);
                pixels[i + 1] = (byte)(pixels[i + 1] * alpha / 255);
                pixels[i + 2] = (byte)(pixels[i + 2] * alpha / 255);
            }

            return pixels;
        }
        finally
        {
            scaled.UnlockBits(locked);
        }
    }
}
