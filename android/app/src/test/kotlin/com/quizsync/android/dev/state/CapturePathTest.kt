package com.quizsync.android.dev.state

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Test

/**
 * 采集路径的判据（规格 §3.2）：**无障碍是备选，不可用时回落主路径**。
 *
 * 为什么这条值得测：两条路径在界面上**看不出区别**（都能截到图），
 * 判据错了的唯一现象是「用户选了备选路径、却每次都弹屏幕录制授权框」——
 * 只有日志与这条判据能分辨。三种回落原因（版本低 / 服务没开 / 可用）也必须各说各的话。
 */
class CapturePathTest {

    @Test
    fun `主路径永远是主路径`() {
        assertEquals(CapturePath.Projection, qsCapturePath(CaptureMode.Main, accessibilityReady = true))
        assertEquals(CapturePath.Projection, qsCapturePath(CaptureMode.Main, accessibilityReady = false))
    }

    @Test
    fun `选了备选且服务可用才走无障碍`() {
        assertEquals(CapturePath.Accessibility, qsCapturePath(CaptureMode.Accessibility, accessibilityReady = true))
    }

    @Test
    fun `选了备选但服务没开则回落主路径`() {
        assertEquals(CapturePath.Projection, qsCapturePath(CaptureMode.Accessibility, accessibilityReady = false))
    }

    @Test
    fun `主路径不需要任何说明`() {
        assertNull(qsCapturePathNote(CaptureMode.Main, accessibilityConnected = false, supported = true))
        assertNull(qsCapturePathNote(CaptureMode.Main, accessibilityConnected = false, supported = false))
    }

    @Test
    fun `备选可用时不打扰用户`() {
        assertNull(qsCapturePathNote(CaptureMode.Accessibility, accessibilityConnected = true, supported = true))
    }

    @Test
    fun `系统版本太低与服务没开要说不同的话`() {
        val tooOld = qsCapturePathNote(CaptureMode.Accessibility, accessibilityConnected = false, supported = false)
        val notEnabled = qsCapturePathNote(CaptureMode.Accessibility, accessibilityConnected = false, supported = true)

        assertNotNull(tooOld)
        assertNotNull(notEnabled)
        // 版本低是**改不了**的（不是用户没开），文案必须区分开，否则用户会一直在设置里找开关。
        assertEquals(true, tooOld!!.contains("Android 11"))
        assertEquals(false, notEnabled!!.contains("Android 11"))
        assertEquals(true, notEnabled.contains("无障碍"))
    }
}
