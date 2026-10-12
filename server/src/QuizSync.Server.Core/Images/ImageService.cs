using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using QuizSync.Server.Core.Protocol;
using QuizSync.Server.Core.Storage;

namespace QuizSync.Server.Core.Images;

/// <summary>上传结果（成功 / 各类拒绝）。</summary>
public sealed record UploadOutcome(
    int Status, JsonObject Body, string? Hash = null, byte[]? Bytes = null);

/// <summary>
/// 图片上传与下载。行为逐条对 <c>spec/07-images.md</c>：
///
/// - **内容寻址**：`image_hash` = sha256(收到的字节)，同字节必得同 hash，
///   秒传回显 `existed: true`（不重复写文件，也不刷新 `created_at`）；
/// - **两阶段体积闸门**：先按 `Content-Length` 预检（上限 + 64 KiB），
///   再**边收边计数**（超限立刻 413，**不等整包收完**，内存有界）；
/// - **限流在体积判定之前**（被拒的上传照样占额度）；
/// - **下载只认 64 位小写十六进制 hash**（拼文件名的东西必须挡住路径穿越）。
/// </summary>
public sealed class ImageService(ImageRepository images, IImageBlobStore blobs, IClock clock, ImageOptions options)
{
    private readonly ImageRepository _images = images;
    private readonly IImageBlobStore _blobs = blobs;
    private readonly IClock _clock = clock;
    private readonly ImageOptions _options = options;
    private readonly SlidingWindowRateLimiter _uploads = new(options.UploadsPerMinute, 60_000);

    public SlidingWindowRateLimiter UploadLimiter => _uploads;

    public UploadOutcome Download(string hash)
    {
        if (!Tokens.IsImageHash(hash))
        {
            // 与「不存在」同码：不泄漏文件系统细节（也不给路径穿越任何机会）。
            return new UploadOutcome(404, ApiError.Body(ApiError.NotFound, "图片不存在"));
        }

        var bytes = _blobs.Read(hash);
        return bytes is null
            ? new UploadOutcome(404, ApiError.Body(ApiError.NotFound, "图片不存在"))
            : new UploadOutcome(200, new JsonObject(), hash, bytes);
    }

    /// <summary>
    /// 处理 `POST /api/v1/images`（multipart/form-data，文件字段名固定 `file`）。
    /// </summary>
    public async Task<UploadOutcome> UploadAsync(HttpContext context, string uploaderDeviceId, CancellationToken ct)
    {
        // 1) 限流（先于体积判定）。
        if (!_uploads.TryAcquire(uploaderDeviceId, _clock.NowMs, out var retryAfter))
        {
            await DrainAsync(context, ct).ConfigureAwait(false);
            return new UploadOutcome(429, ApiError.Body(ApiError.RateLimited, "上传过于频繁", retryAfter));
        }

        // 2) Content-Length 预检：声明值就超限时不必读 body。
        var declared = context.Request.ContentLength;
        if (declared is not null && declared > _options.MaxBytes + 64 * 1024)
        {
            await DrainAsync(context, ct).ConfigureAwait(false);
            return new UploadOutcome(413, ApiError.Body(ApiError.PayloadTooLarge, $"单文件需 ≤ {_options.MaxBytes / 1024 / 1024}MB"));
        }

        if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var mediaType) ||
            !mediaType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            return new UploadOutcome(400, ApiError.Body(ApiError.InvalidRequest, "需要 multipart/form-data"));
        }

        var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;
        if (string.IsNullOrEmpty(boundary))
        {
            return new UploadOutcome(400, ApiError.Body(ApiError.InvalidRequest, "需要 multipart/form-data"));
        }

        var reader = new MultipartReader(boundary, context.Request.Body);
        MultipartSection? section;
        while ((section = await reader.ReadNextSectionAsync(ct).ConfigureAwait(false)) is not null)
        {
            if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition) ||
                !disposition.DispositionType.Equals("form-data", StringComparison.OrdinalIgnoreCase) ||
                disposition.Name != "file")
            {
                continue;
            }

            // 3) 边收边计数：超过上限立刻停手（不把整包读进内存）。
            using var buffer = new MemoryStream();
            var chunk = new byte[64 * 1024];
            int read;
            while ((read = await section.Body.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > _options.MaxBytes)
                {
                    await DrainAsync(context, ct).ConfigureAwait(false);
                    return new UploadOutcome(413, ApiError.Body(ApiError.PayloadTooLarge, $"单文件需 ≤ {_options.MaxBytes / 1024 / 1024}MB"));
                }
            }

            var bytes = buffer.ToArray();
            if (bytes.Length == 0)
            {
                return new UploadOutcome(400, ApiError.Body(ApiError.InvalidRequest, "文件内容为空"));
            }

            var hash = Tokens.Sha256Hex(bytes);
            var existed = _images.Find(hash);

            // 真算尺寸（`spec/07-images.md` §1.8 的第 3 条 v2 目标）：只读文件头，不解码像素。
            // 解不出来时**沿用既有值**（同字节重传不会把历史尺寸抹掉 —— 协议 §1.7 专门点过这条风险）。
            var dimensions = ImageDimensions.Probe(bytes);
            var width = dimensions?.Width ?? existed?.Width;
            var height = dimensions?.Height ?? existed?.Height;

            _blobs.Write(hash, bytes);
            _images.Upsert(new ImageRecord(
                Hash: hash,
                Size: bytes.Length,
                Mime: "image/jpeg",
                Width: width,
                Height: height,
                LocalPath: _blobs.PathFor(hash) ?? existed?.LocalPath,
                CreatedAt: existed?.CreatedAt ?? _clock.NowMs,
                UploadedBy: uploaderDeviceId));

            return new UploadOutcome(200, new JsonObject
            {
                ["image_hash"] = hash,
                ["size"] = bytes.Length,
                ["mime"] = "image/jpeg",
                ["width"] = width,
                ["height"] = height,
                ["existed"] = existed is not null,
            }, hash, bytes);
        }

        return new UploadOutcome(400, ApiError.Body(ApiError.InvalidRequest, "缺少 file 字段"));
    }

    /// <summary>
    /// 回 4xx 之前把**已经在路上的**请求体吞掉（最多 <see cref="ImageOptions.DrainGraceMs"/>）。
    ///
    /// 不这么做的话：服务端在客户端还在发的时候关连接，Windows 会发 RST，
    /// 客户端拿到的不是干净的 413/429 而是「连接被重置」。**但也不能读完再回** ——
    /// 一致性向量 `images.ndjson` 11 的前提是「超限立刻回、不等整包收完」。
    /// </summary>
    private async Task DrainAsync(HttpContext context, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(_options.DrainGraceMs));
            var buffer = new byte[64 * 1024];
            var total = 0L;
            while (await context.Request.Body.ReadAsync(buffer, timeout.Token).ConfigureAwait(false) > 0)
            {
                total += buffer.Length;
                if (total > _options.MaxBytes + 8 * 1024 * 1024)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            // 客户端已经断了 / 到点了：反正要回错误，这里不用管。
        }
    }
}

/// <summary>图片字节的存放（内容寻址；生产是磁盘，测试可以是内存）。</summary>
public interface IImageBlobStore
{
    void Write(string hash, byte[] bytes);

    byte[]? Read(string hash);

    /// <summary>落盘的本地绝对路径（内存实现返回 null）。本地专属列，不进同步。</summary>
    string? PathFor(string hash);
}

/// <summary>磁盘实现：`&lt;dir&gt;/&lt;hash&gt;.jpg`。</summary>
public sealed class DirectoryImageBlobStore(string directory) : IImageBlobStore
{
    private readonly string _directory = directory;

    public string? PathFor(string hash) => Path.Combine(_directory, hash + ".jpg");

    public void Write(string hash, byte[] bytes)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(PathFor(hash)!, bytes);
    }

    public byte[]? Read(string hash)
    {
        var path = PathFor(hash)!;
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }
}

/// <summary>内存实现（单测用）。</summary>
public sealed class MemoryImageBlobStore : IImageBlobStore
{
    private readonly Dictionary<string, byte[]> _map = [];

    public string? PathFor(string hash) => null;

    public void Write(string hash, byte[] bytes) => _map[hash] = bytes;

    public byte[]? Read(string hash) => _map.TryGetValue(hash, out var bytes) ? bytes : null;
}

/// <summary>图片相关的数值口径（协议冻结值）。</summary>
public sealed record ImageOptions
{
    public long MaxBytes { get; init; } = 2 * 1024 * 1024;

    public int UploadsPerMinute { get; init; } = 30;

    public int DrainGraceMs { get; init; } = 300;
}
