package com.quizsync.android.dev

import kotlin.test.Test
import kotlin.test.assertEquals

/**
 * 采样率的纯逻辑回归（不需要设备：`sampleSizeFor` 不碰任何 Android API）。
 *
 * 为什么要钉它：采样过头会把题目拍糊（识别的输入糊了，AI 再强也读不出来），
 * 采样不足则解码时白搬几十 MB 像素。规则是「采样后长边仍然 ≥ maxEdge」的最大 2 的幂。
 */
class ImageCompressTest {
    @Test
    fun sampling_keeps_the_long_edge_above_the_limit() {
        // 4000×3000 压到长边 1600：采样 2 → 2000×1500（≥1600，交给后面那次缩放）。
        assertEquals(2, ImageCompress.sampleSizeFor(4000, 3000, 1600))

        // 8000×6000：采样 4 → 2000×1500。
        assertEquals(4, ImageCompress.sampleSizeFor(8000, 6000, 1600))

        // 1600×1200 本来就在上限内：不采样（返回 1 表示按原尺寸解码）。
        assertEquals(1, ImageCompress.sampleSizeFor(1600, 1200, 1600))

        // 竖图同样按**长边**判断，不能只看宽。
        assertEquals(2, ImageCompress.sampleSizeFor(3000, 4000, 1600))

        // 3200×2400：采样 2 → 1600×1200，正好等于上限，仍然算合格的一档。
        assertEquals(2, ImageCompress.sampleSizeFor(3200, 2400, 1600))
    }

    /**
     * 缩放目标尺寸：**截屏那条路唯一的尺寸保证**（相册那条靠采样 + 缩放两道，
     * 截屏这条路只过这一道 —— 服务已把 VirtualDisplay 按 1600 建好，这里是第二道保险）。
     */
    @Test
    fun scaling_keeps_the_long_edge_within_the_limit() {
        // 平板竖屏 1800×2880（验收机就是这一类）：长边正好压到 1600，短边等比 1000。
        assertEquals(1000 to 1600, ImageCompress.scaledSize(1800, 2880, 1600))

        // 已经在限制内：**不能**放大、也不能改尺寸（原样返回）。
        assertEquals(1600 to 1200, ImageCompress.scaledSize(1600, 1200, 1600))

        // 横屏同理，按长边（宽）压。
        assertEquals(1600 to 900, ImageCompress.scaledSize(3200, 1800, 1600))

        // 极端窄条也要保证两个方向都 ≥1（0 像素的位图会让 compress 直接失败）。
        assertEquals(1 to 1600, ImageCompress.scaledSize(1, 4000, 1600))
    }
}
