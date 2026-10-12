using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace QuizSync.App;

/// <summary>
/// 从系统剪贴板取一张图，并转成 JPEG 字节 + 协议里的 `sha256:…` 摘要。
///
/// 走 WinRT 的 <see cref="DataPackageView"/>（`GetBitmapAsync`）而不是 Win32 的 `CF_DIB`：
/// 1.1.0 在 Win32 那条路上踩过「剪贴板里的 DIB 大多是 `BI_BITFIELDS`，只认 `BI_RGB` 会
/// 每一步都成功、最后静默返回 null」（见 `AGENTS.md` M14）。WinRT 这层由系统做格式归一，
/// 不需要自己解析 DIB 头与通道掩码。
///
/// 返回 null 表示「剪贴板里没有能用的图」—— 调用方**必须据此刻意提示**，不要静默什么都不做。
/// </summary>
internal static class ClipboardImage
{
    /// <summary>上一次「看到了但没认出来」时剪贴板里实际有哪些格式（排查用）。</summary>
    internal static IReadOnlyList<string> LastRejectedFormats { get; private set; } = [];
    internal static async Task<(byte[] Jpeg, string Hash)?> TryReadAsync()
    {
        var content = await GetContentWithRetryAsync();
        if (!content.Contains(StandardDataFormats.Bitmap))
        {
            // 把**实际有哪些格式**带回去：只说「没有图片」时，用户（和排查的人）无法区分
            // 「剪贴板真的空的」和「有图但格式不是我们认的那种」——后者要靠这个列表才能定位。
            LastRejectedFormats = [.. content.AvailableFormats];
            return null;
        }

        LastRejectedFormats = [];
        var reference = await content.GetBitmapAsync();
        using var source = await reference.OpenReadAsync();

        var decoder = await BitmapDecoder.CreateAsync(source);
        var bitmap = await decoder.GetSoftwareBitmapAsync();

        // 统一编成 JPEG：下游（协议上传、AI 请求、落盘）认的都是 JPEG 字节。
        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();
        output.Seek(0);

        var bytes = new byte[output.Size];
        using (var reader = new DataReader(output.GetInputStreamAt(0)))
        {
            await reader.LoadAsync((uint)output.Size);
            reader.ReadBytes(bytes);
        }

        return (bytes, $"sha256:{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))}");
    }

    /// <summary>
    /// 读剪贴板内容，**占着就退避重试几次**。
    ///
    /// 为什么必须重试（第 170 轮，做端到端探针时抓到）：别的进程锁着剪贴板时
    /// `Clipboard.GetContent()` 会抛 `CLIPBRD_E_CANT_OPEN`（`0x800401D0`，`Message` 还是**空串**），
    /// 而「任何程序复制的那一瞬间」都锁着它 —— 这是常态不是故障。
    /// 监听回调正好落进那个窗口里时，原来只写一行日志就放弃，于是**那张图永久丢掉**
    /// （下一次剪贴板变化才会再来），用户看到的就是「复制了图但没反应」。
    /// 实测现场：`[02:01:10] 剪贴板监听跳过一次：`（消息为空）。
    ///
    /// 重试总时长约 40+80+120+160+200 = 600 ms，够跨过任何一次正常的复制事务；
    /// 仍然失败才把异常抛给调用方（`App.OnClipboardUpdate` 会记日志、不弹提示）。
    /// </summary>
    private static async Task<DataPackageView> GetContentWithRetryAsync()
    {
        const int attempts = 6;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return Clipboard.GetContent();
            }
            catch (Exception error) when (
                attempt < attempts
                && error is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
            {
                await Task.Delay(40 * attempt).ConfigureAwait(true);
            }
        }
    }
}
