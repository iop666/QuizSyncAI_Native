package com.quizsync.android.dev.capture

import android.accessibilityservice.AccessibilityService
import android.graphics.Bitmap
import android.os.Build
import android.util.Log
import android.view.Display
import android.view.accessibility.AccessibilityEvent
import androidx.annotation.RequiresApi
import com.quizsync.android.dev.ImageCompress
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withContext
import kotlin.coroutines.resume

/**
 * **备选截屏路径**：无障碍服务（规格 §3.2 的 `AccessibilityService.takeScreenshot()`，Android 11+）。
 *
 * 为什么要有它：主路径（MediaProjection）每次开机会弹一次系统授权框，部分定制系统还会在
 * 息屏 / 切后台时把会话掐掉。无障碍这条路**只需要用户在系统设置里开一次**，之后截屏不再弹框。
 *
 * 三条硬约束（都写在代码里，别当成可以商量的）：
 * 1. **不读屏幕内容**：`onAccessibilityEvent` 是空的，`accessibilityEventTypes` 也只要
 *    `typeWindowStateChanged`（见 `res/xml/accessibility_capture.xml`）—— 只用 `takeScreenshot`
 *    这个系统能力。这是对用户的承诺，也是这条路径能过审的前提。
 * 2. **API < 30 没有这个能力**：`takeScreenshot` 是 R 起才有的 API，调用点会先判断版本并回落主路径。
 * 3. **不可用时回落，且如实说出来**：`isConnected()` 为假时由调用方（`QuizViewModel`）
 *    回落到主路径并出声，绝不静默换路 —— 用户选了备选路径却悄悄走主路径，会以为设置没生效。
 *
 * 服务没连上时 [capture] 返回 null（而不是抛异常）。
 */
class AccessibilityCaptureService : AccessibilityService() {

    companion object {
        private const val TAG = "QuizSyncA11y"

        @Volatile
        private var instance: AccessibilityCaptureService? = null

        /** 系统设置里这个服务现在是不是开着的。 */
        fun isConnected(): Boolean = instance != null

        /** 当前系统版本有没有 `takeScreenshot`（Android 11+）。 */
        fun isSupported(): Boolean = Build.VERSION.SDK_INT >= Build.VERSION_CODES.R

        /**
         * 取**一帧**并压成 JPEG（1600 / q80，与主路径同一套 [ImageCompress]）。
         *
         * 返回 null = 没取到（服务没开 / 系统给了失败码 / 压缩失败）——
         * 三种原因在 logcat 里分得清，界面上由调用方按 [isConnected] 与 [isSupported] 分别解释。
         */
        suspend fun capture(): ByteArray? {
            if (!isSupported()) {
                Log.w(TAG, "系统版本 < Android 11，没有 takeScreenshot")
                return null
            }

            val service = instance
            if (service == null) {
                Log.w(TAG, "无障碍服务没连上，取帧返回 null（调用方回落主路径）")
                return null
            }

            return service.takeScreenshotOnce()
        }
    }

    override fun onServiceConnected() {
        super.onServiceConnected()
        instance = this
        Log.i(TAG, "无障碍服务已连接（备选截屏路径可用）")
    }

    override fun onDestroy() {
        if (instance === this) {
            instance = null
            Log.i(TAG, "无障碍服务已断开（备选截屏路径不可用）")
        }

        super.onDestroy()
    }

    override fun onAccessibilityEvent(event: AccessibilityEvent?) {
        // **刻意什么都不做**：本服务不读屏幕内容，只用 takeScreenshot 这个能力。
    }

    override fun onInterrupt() = Unit

    @RequiresApi(Build.VERSION_CODES.R)
    private suspend fun takeScreenshotOnce(): ByteArray? = suspendCancellableCoroutine { continuation ->
        val callback = object : TakeScreenshotCallback {
            override fun onSuccess(screenshot: ScreenshotResult) {
                val jpeg = try {
                    // `wrapHardwareBuffer` 得到的是**硬件位图**，不能直接压缩 → 先拷进 ARGB_8888。
                    val hardware = Bitmap.wrapHardwareBuffer(screenshot.hardwareBuffer, screenshot.colorSpace)
                    val software = hardware?.copy(Bitmap.Config.ARGB_8888, false)
                    hardware?.recycle()
                    software?.let { ImageCompress.jpegFromBitmap(it) }
                } catch (error: Throwable) {
                    Log.w(TAG, "取到的帧转换失败：${error.message}")
                    null
                } finally {
                    screenshot.hardwareBuffer.close()
                }

                if (continuation.isActive) {
                    Log.i(TAG, if (jpeg == null) "取帧失败：转换/压缩没出字节" else "取帧成功：${jpeg.size} 字节")
                    continuation.resume(jpeg)
                }
            }

            override fun onFailure(errorCode: Int) {
                if (continuation.isActive) {
                    Log.w(TAG, "takeScreenshot 失败：errorCode=$errorCode")
                    continuation.resume(null)
                }
            }
        }

        try {
            takeScreenshot(Display.DEFAULT_DISPLAY, mainExecutor, callback)
        } catch (error: Throwable) {
            // 极少数机器上会直接抛（系统版本 / 服务状态不一致）—— 如实记下来，返回 null 交给回落。
            Log.w(TAG, "takeScreenshot 调用抛异常：${error.message}")
            if (continuation.isActive) {
                continuation.resume(null)
            }
        }
    }
}
