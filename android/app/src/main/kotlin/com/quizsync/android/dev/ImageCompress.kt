package com.quizsync.android.dev

import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.net.Uri
import java.io.ByteArrayOutputStream

/**
 * 相册选图 → 交给主机的 JPEG 字节。
 *
 * 这三个数都是**既定值**，不是随手定的：
 *  * 长边 ≤1600、质量 80 —— 1.1.0 的 `core-pkg/util/image_proc.dart` 就是这个口径
 *    （Windows 端截图也走同一条）；
 *  * 单文件 ≤2MB —— 协议 `POST /api/v1/images` 的硬上限（超了回 413 `payload_too_large`）。
 *
 * **为什么压缩是前置条件而不是「优化」**：手机随手拍的照片普遍 4000×3000 / 5–8MB，
 * 不压必然被主机 413 挡回来，而用户看到的只是「上传失败」，真因是尺寸。
 *
 * 两条实现纪律：
 *  * 采样用 2 的幂次（`inSampleSize`），并且只采样到「再小一档就低于 maxEdge」为止 ——
 *    采样过头会糊，采样不足则后面那次缩放要搬更多像素；
 *  * 读流两次（先量尺寸再解码），**不把原图整个读进内存**：8MB 的图解码成位图是 ~100MB。
 */
object ImageCompress {
    /** 长边上限（1.1.0 与 Windows 端同值）。 */
    const val MAX_EDGE = 1600

    /** JPEG 质量（1.1.0 同值）。 */
    const val QUALITY = 80

    /** 协议 §3.2 的单文件上限：2MB。 */
    const val MAX_BYTES = 2 * 1024 * 1024

    /** 解码并压成 JPEG。读不出 / 解码失败返回 null（调用方如实提示，不静默）。 */
    fun jpegFromUri(
        context: Context,
        uri: Uri,
        maxEdge: Int = MAX_EDGE,
        quality: Int = QUALITY,
    ): ByteArray? {
        val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        context.contentResolver.openInputStream(uri)?.use { BitmapFactory.decodeStream(it, null, bounds) }
        if (bounds.outWidth <= 0 || bounds.outHeight <= 0) {
            return null
        }

        val options = BitmapFactory.Options().apply {
            inSampleSize = sampleSizeFor(bounds.outWidth, bounds.outHeight, maxEdge)
        }
        val decoded = context.contentResolver.openInputStream(uri)?.use {
            BitmapFactory.decodeStream(it, null, options)
        } ?: return null

        return try {
            val scaled = scaleToMaxEdge(decoded, maxEdge)
            ByteArrayOutputStream().use { out ->
                scaled.compress(Bitmap.CompressFormat.JPEG, quality, out)
                if (scaled !== decoded) {
                    scaled.recycle()
                }

                out.toByteArray()
            }
        } finally {
            decoded.recycle()
        }
    }

    /**
     * 截屏那条路：位图**已经在手**（`ImageReader` 取的一帧），不再走解码，直接压成 JPEG。
     *
     * 与 [jpegFromUri] 共用 [scaleToMaxEdge]：两条路的「长边 ≤1600、质量 80」必须是同一个口径，
     * 各写一份早晚会漂（而口径一漂，主机那边的 413 就回来了）。
     *
     * **不回收入参**（[bitmap] 归调用方），自己造的缩放副本自己回收。
     * 压不出字节（被回收 / 尺寸非法）返回 null，由调用方如实提示。
     */
    fun jpegFromBitmap(
        bitmap: Bitmap,
        maxEdge: Int = MAX_EDGE,
        quality: Int = QUALITY,
    ): ByteArray? {
        if (bitmap.isRecycled || bitmap.width <= 0 || bitmap.height <= 0) {
            return null
        }
        val scaled = scaleToMaxEdge(bitmap, maxEdge)
        return try {
            ByteArrayOutputStream().use { out ->
                if (!scaled.compress(Bitmap.CompressFormat.JPEG, quality, out)) null else out.toByteArray()
            }
        } finally {
            if (scaled !== bitmap) {
                scaled.recycle()
            }
        }
    }

    /**
     * 2 的幂次采样率：取「采样后长边仍然 ≥ maxEdge」的最大一档。
     * 例：4000×3000、maxEdge=1600 → 2（2000×1500 再交给缩放）。
     */
    internal fun sampleSizeFor(width: Int, height: Int, maxEdge: Int): Int {
        var sample = 1
        while (maxOf(width, height) / (sample * 2) >= maxEdge) {
            sample *= 2
        }

        return sample
    }

    /**
     * 缩放后的目标尺寸（长边 ≤ [maxEdge]，等比、两个方向都不小于 1）。
     *
     * 抽成纯函数是为了**能单测**：这条「长边 ≤1600」的约定是协议级的
     * （超过 1600 的图会让上传变慢、还更容易撞 2MB 上限），两条压缩路径都靠它。
     */
    internal fun scaledSize(width: Int, height: Int, maxEdge: Int): Pair<Int, Int> {
        val longest = maxOf(width, height)
        if (longest <= maxEdge) {
            return width to height
        }
        val ratio = maxEdge.toFloat() / longest
        return (width * ratio).toInt().coerceAtLeast(1) to (height * ratio).toInt().coerceAtLeast(1)
    }

    private fun scaleToMaxEdge(bitmap: Bitmap, maxEdge: Int): Bitmap {
        val (width, height) = scaledSize(bitmap.width, bitmap.height, maxEdge)
        if (width == bitmap.width && height == bitmap.height) {
            return bitmap
        }
        return Bitmap.createScaledBitmap(bitmap, width, height, true)
    }
}
