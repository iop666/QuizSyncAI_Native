package com.quizsync.android.dev.capture

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ApplicationInfo
import android.content.pm.ServiceInfo
import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.PixelFormat
import android.graphics.Rect
import android.hardware.display.DisplayManager
import android.hardware.display.VirtualDisplay
import android.media.Image
import android.media.ImageReader
import android.media.projection.MediaProjection
import android.media.projection.MediaProjectionManager
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.util.DisplayMetrics
import android.util.Log
import android.view.WindowManager
import androidx.core.app.NotificationManagerCompat
import com.quizsync.android.dev.ImageCompress
import java.io.File

/**
 * 截屏采集前台服务（规格 §3.1「悬浮球单击」→ §3.2「截屏」这一段）。
 *
 * 一句话：`MediaProjection` → **常驻** `VirtualDisplay` → `ImageReader`，按需取**一帧** → JPEG。
 *
 * 四条照抄 1.1.0 结论、但按 2.0 风格重写的纪律：
 *  1. **一个授权 = 一个会话，`createVirtualDisplay` 只调一次**（§3.2 的常量约束）。
 *     每次截屏都重建会反复弹系统提示、屏幕还会闪 —— 所以本服务是**长驻**的，
 *     取帧只是从常驻的 `ImageReader` 里拿最新一帧；
 *  2. **`registerCallback` 必须先于 `createVirtualDisplay`**：Android 14+ 起后者会检查回调
 *     注册状态，顺序反了直接 `IllegalStateException`；
 *  3. **`foregroundServiceType="mediaProjection"`** + 常驻通知：这是 Android 14 的硬性要求，
 *     也是会话能活下来的前提。**没有通知权限（API 33+ 的 POST_NOTIFICATIONS）也不能崩** ——
 *     通知画不出来而已，服务照跑；
 *  4. VirtualDisplay 按**长边 ≤1600** 建（1.1.0 同口径）：省内存也省一次缩放，
 *     最后再过一道 [ImageCompress.jpegFromBitmap]（1600 / q80）保证不变量。
 *
 * **裁掉悬浮球自己**，两种手段，按能不能藏成功二选一（两种同时做会**擦掉题图**）：
 *  * 主路径（规格 §3.1「截屏前必须先隐藏悬浮球自身」）：调用方先 `CaptureOverlay.hideForCapture()`
 *    并等 250ms → 这一帧里根本没有球，不需要涂白；
 *  * 兜底：万一窗口没被移除（藏失败），调用方把球**当时**的屏幕矩形传进来（`whiten`），
 *    这里按同一个缩放比换算后涂成白色 —— 宁可少一块内容，也不让 AI 看见球。
 */
class CaptureService : Service() {

    companion object {
        private const val TAG = "CaptureService"

        private const val CHANNEL_ID = "quizsync_capture"
        private const val NOTIFICATION_ID = 1001

        private const val ACTION_START = "com.quizsync.android.dev.capture.START"
        private const val ACTION_STOP = "com.quizsync.android.dev.capture.STOP"
        private const val EXTRA_RESULT_CODE = "result_code"
        private const val EXTRA_RESULT_DATA = "result_data"

        /** 取帧的等待预算：藏球之后系统要重新合成一帧，通常几十毫秒，给足 600ms。 */
        private const val FRAME_WAIT_MS = 600L
        private const val FRAME_POLL_MS = 40L

        @Volatile
        private var instance: CaptureService? = null

        /** 会话失效过（锁屏 / 系统回收 / 用户停止录制 / 权限撤销）。下次点击时引导重新授权。 */
        @Volatile
        var sessionLost: Boolean = false
            private set

        /** 现在有没有可用的常驻会话（= 点球就能取帧，不用再弹系统框）。 */
        fun isReady(): Boolean = instance?.reader != null && !sessionLost

        /** 用 `createScreenCaptureIntent()` 的授权结果启动服务（`startForegroundService`）。 */
        fun start(context: Context, resultCode: Int, data: Intent) {
            val intent = Intent(context, CaptureService::class.java).apply {
                action = ACTION_START
                putExtra(EXTRA_RESULT_CODE, resultCode)
                putExtra(EXTRA_RESULT_DATA, data)
            }
            context.startForegroundService(intent)
        }

        /**
         * 停服务并释放会话（规格 §3.1：**关掉悬浮球或识别模块 = 截屏会话一并结束**，
         * 系统那条「正在共享屏幕」的提示随之消失）。
         *
         * 用 `stopService` 而不是 `startService(ACTION_STOP)`：从后台起服务在 API 26+ 会抛
         * `IllegalStateException`（用户切走设置页、Activity 销毁时就踩到）。
         */
        fun stop(context: Context) {
            context.stopService(Intent(context, CaptureService::class.java))
        }

        /**
         * 取**一帧**并压成 JPEG（1600 / q80）。返回 null = 真的取不到画面（没有会话），
         * 由调用方如实提示，不静默。
         *
         * @param ballRect   悬浮球**当时的屏幕像素矩形**（见 [CaptureOverlay.visibleRect]）。
         * @param ballHidden 调用方有没有把球藏成功（藏了才可能拿到「没有球」的新帧）。
         *
         * 涂白规则（只在必要时涂，别把题图擦了）：
         *  * 拿到**新帧** + 藏球成功 → 不需要涂（球根本不在帧里）；
         *  * 拿到**新帧** + 藏球失败 → 球还在屏上，按矩形涂白；
         *  * 只拿到**旧帧**（屏幕静止时系统不给新帧，真机实测会发生）→ 那一帧可能拍到了球，也涂白。
         */
        fun capture(ballRect: Rect? = null, ballHidden: Boolean = false): ByteArray? =
            instance?.captureFrame(ballRect, ballHidden)
    }

    private var projection: MediaProjection? = null
    private var virtualDisplay: VirtualDisplay? = null
    private var reader: ImageReader? = null
    private var scale = 1f

    /** 取帧可能在 IO 线程，而会话在 Main 上建：所有会碰 reader/display 的操作都过这把锁。 */
    private val lock = Any()

    private val projectionCallback = object : MediaProjection.Callback() {
        override fun onStop() {
            // 系统停掉共享（锁屏、状态栏上的「停止共享」、权限撤销）—— 如实记下来，
            // 清掉会话，并且**把前台服务一起收掉**：留着一个没有投影的 mediaProjection
            // 前台服务在 Android 14+ 上属于非法状态，用户还会一直看到共享提示。
            Log.w(TAG, "MediaProjection onStop：会话失效（下次点击要重新授权）")
            sessionLost = true
            releaseCapture()
            stopSelf()
        }
    }

    override fun onBind(intent: Intent?) = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_STOP -> {
                stopSelf()
                return START_NOT_STICKY
            }

            ACTION_START -> {
                startAsForeground()
                val code = intent.getIntExtra(EXTRA_RESULT_CODE, 0)
                val data = intent.projectionData()
                if (code != 0 && data != null) {
                    setupProjection(code, data)
                } else {
                    Log.w(TAG, "没有授权结果（resultCode=$code data=${data != null}）：不建会话")
                    stopSelf()
                    return START_NOT_STICKY
                }
            }
        }
        instance = this
        // 不用 START_STICKY：系统重启服务时 intent 为 null，两条分支都不走，
        // 前台与投影都不会就位 —— 那种「活着但没用」的服务不如让用户重新点一次球。
        return START_NOT_STICKY
    }

    // ========================================================================
    // 前台 + 通知
    // ========================================================================

    private fun startAsForeground() {
        val manager = getSystemService(NOTIFICATION_SERVICE) as NotificationManager
        if (manager.getNotificationChannel(CHANNEL_ID) == null) {
            manager.createNotificationChannel(
                NotificationChannel(CHANNEL_ID, "截屏服务", NotificationManager.IMPORTANCE_LOW).apply {
                    description = "保持截屏会话常驻（系统授权只弹一次）"
                    setShowBadge(false)
                },
            )
        }

        val launch = PendingIntent.getActivity(
            this,
            0,
            packageManager.getLaunchIntentForPackage(packageName),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )
        val notification: Notification = Notification.Builder(this, CHANNEL_ID)
            .setContentTitle("截屏服务运行中")
            .setContentText("点悬浮球即可截屏搜题")
            .setSmallIcon(android.R.drawable.ic_menu_camera)
            .setContentIntent(launch)
            .setOngoing(true)
            .build()

        // 通知权限被拒（API 33+ 常见）：**照常进前台，不崩**。只是通知画不出来，
        // 会话本身不受影响（前台服务的判定与「通知可见」是两件事）。
        val notificationsAllowed = runCatching {
            NotificationManagerCompat.from(this).areNotificationsEnabled()
        }.getOrDefault(true)
        if (!notificationsAllowed) {
            Log.w(TAG, "没有通知权限：截屏服务照常运行，只是常驻通知不显示")
        }

        try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                startForeground(NOTIFICATION_ID, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION)
            } else {
                startForeground(NOTIFICATION_ID, notification)
            }
        } catch (error: Exception) {
            // Android 12+ 后台起前台服务会抛 ForegroundServiceStartNotAllowedException。
            // 正常路径是从 Activity 授权后启动（前台），真抛了也要留下痕迹而不是静默死。
            Log.e(TAG, "startForeground 失败：${error.message}", error)
        }
    }

    // ========================================================================
    // 会话（建一次、用到底）
    // ========================================================================

    private data class ScreenSpec(val width: Int, val height: Int, val dpi: Int, val scale: Float)

    /** 屏幕物理尺寸，长边超过 1600 时整体等比缩到 1600（1.1.0 同口径）。 */
    private fun screenSpec(): ScreenSpec {
        val manager = getSystemService(WINDOW_SERVICE) as WindowManager
        val metrics = DisplayMetrics()
        @Suppress("DEPRECATION")
        manager.defaultDisplay.getRealMetrics(metrics)
        val longEdge = maxOf(metrics.widthPixels, metrics.heightPixels)
        val ratio = if (longEdge > ImageCompress.MAX_EDGE) ImageCompress.MAX_EDGE.toFloat() / longEdge else 1f
        return ScreenSpec(
            width = (metrics.widthPixels * ratio).toInt().coerceAtLeast(1),
            height = (metrics.heightPixels * ratio).toInt().coerceAtLeast(1),
            dpi = (metrics.densityDpi * ratio).toInt().coerceAtLeast(1),
            scale = ratio,
        )
    }

    private fun setupProjection(resultCode: Int, data: Intent) {
        releaseCapture()
        val manager = getSystemService(MEDIA_PROJECTION_SERVICE) as MediaProjectionManager
        val created = runCatching { manager.getMediaProjection(resultCode, data) }
            .onFailure { Log.e(TAG, "getMediaProjection 失败（授权可能已被用过）：${it.message}", it) }
            .getOrNull()
        if (created == null) {
            stopSelf()
            return
        }

        // ⚠️ 顺序：回调**必须先注册**（Android 14+ 的硬性检查），注册晚了 createVirtualDisplay 直接抛。
        created.registerCallback(projectionCallback, Handler(Looper.getMainLooper()))

        val spec = screenSpec()
        val imageReader = ImageReader.newInstance(spec.width, spec.height, PixelFormat.RGBA_8888, 3)
        val display = runCatching {
            created.createVirtualDisplay(
                "quizsync-capture",
                spec.width,
                spec.height,
                spec.dpi,
                DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR,
                imageReader.surface,
                null,
                null,
            )
        }.onFailure { Log.e(TAG, "createVirtualDisplay 失败：${it.message}", it) }.getOrNull()

        if (display == null) {
            runCatching { imageReader.close() }
            runCatching { created.stop() }
            stopSelf()
            return
        }

        projection = created
        virtualDisplay = display
        reader = imageReader
        scale = spec.scale
        sessionLost = false
        Log.i(TAG, "截屏会话就绪：${spec.width}×${spec.height}（dpi=${spec.dpi}，缩放=${spec.scale}）")
    }

    private fun releaseCapture() {
        runCatching { virtualDisplay?.release() }
        runCatching { reader?.close() }
        runCatching { projection?.stop() }
        virtualDisplay = null
        reader = null
        projection = null
    }

    override fun onDestroy() {
        releaseCapture()
        if (instance === this) {
            instance = null
        }
        super.onDestroy()
    }

    // ========================================================================
    // 取帧
    // ========================================================================

    private fun captureFrame(ballRect: Rect?, ballHidden: Boolean): ByteArray? = synchronized(lock) {
        val active = reader ?: run {
            Log.w(TAG, "取帧失败：还没有会话（要先授权）")
            return null
        }
        var fresh: Image? = null
        var stale: Image? = null
        try {
            // 先把队列里已有的帧收掉，只留**最新的一张**当兜底：屏幕静止时（比如相册里一张
            // 不动的图）系统可能一直不给新帧，而那一张旧帧其实就是「当前这一屏」——
            // 只是它可能拍到了还没藏好的球，所以用的时候要涂白。
            stale = drain(active)
            fresh = awaitFreshFrame(active)
            val frame = fresh ?: stale
            if (frame == null) {
                Log.w(TAG, "取帧失败：队列里一张都没有")
                return null
            }
            val freshUsed = fresh != null
            val whiten = if (freshUsed) (if (ballHidden) null else ballRect) else ballRect
            if (!freshUsed) {
                Log.w(TAG, "没有新帧：退回藏球前那一帧（${frame.width}×${frame.height}），按球矩形涂白兜底")
            }

            val padded = frame.toBitmap()
            // 涂白必须在裁剪之前：带 padding 的位图是**可变**的（`Canvas` 不认可变位图），
            // 而 padding 在行尾，坐标系与屏幕一致，矩形不用换算。
            if (whiten != null) {
                paintWhite(padded, whiten)
            }
            // 裁掉行尾 padding。**这里刻意不 `recycle()`**：子位图与父位图共享像素分配，
            // 显式回收父位图会把子位图的像素一起放掉（一段一帧 6-8MB，交给 GC 收更安全）。
            val cropped = if (padded.width == frame.width && padded.height == frame.height) {
                padded
            } else {
                Bitmap.createBitmap(padded, 0, 0, frame.width, frame.height)
            }

            val jpeg = ImageCompress.jpegFromBitmap(cropped)
            if (jpeg == null) {
                Log.e(TAG, "取帧失败：位图压不出 JPEG")
                return null
            }
            Log.i(
                TAG,
                "取帧成功：${frame.width}×${frame.height} → JPEG ${jpeg.size} 字节" +
                    "（新帧=$freshUsed 藏球=$ballHidden 涂白=${if (whiten == null) "无" else whiten}）",
            )
            dumpForAcceptance(jpeg)
            return jpeg
        } catch (error: Exception) {
            Log.e(TAG, "取帧异常：${error.message}", error)
            return null
        } finally {
            runCatching { fresh?.close() }
            runCatching { stale?.close() }
        }
    }

    /**
     * 取一帧**藏球之后**的新帧；拿不到就返回 null（由调用方退回旧帧兜底）。
     *
     * 为什么要挑新帧：屏幕内容静止时 `ImageReader` 不会自己产生新帧，
     * 直接取会拿到**藏球之前**那一帧 —— 球就又被拍进去了。
     */
    private fun awaitFreshFrame(active: ImageReader): Image? {
        val deadline = System.currentTimeMillis() + FRAME_WAIT_MS
        while (System.currentTimeMillis() < deadline) {
            active.acquireLatestImage()?.let { return it }
            Thread.sleep(FRAME_POLL_MS)
        }
        return null
    }

    /** 收掉队列里已有的帧，返回**最新的一张**（其余关掉）；一张都没有返回 null。 */
    private fun drain(active: ImageReader): Image? {
        var keep: Image? = null
        var total = 0
        while (true) {
            val next = active.acquireLatestImage() ?: break
            keep?.close()
            keep = next
            total++
            if (total > 8) break
        }
        if (total > 1) {
            Log.d(TAG, "队列里有 $total 张旧帧，只留最新的一张兜底")
        }
        return keep
    }

    /** 把球那一块涂成白色（屏幕坐标 → 帧坐标）。 */
    private fun paintWhite(frame: Bitmap, rect: Rect) {
        val left = (rect.left * scale).toInt().coerceIn(0, frame.width)
        val top = (rect.top * scale).toInt().coerceIn(0, frame.height)
        val right = (rect.right * scale).toInt().coerceIn(0, frame.width)
        val bottom = (rect.bottom * scale).toInt().coerceIn(0, frame.height)
        if (right <= left || bottom <= top) {
            return
        }
        Canvas(frame).drawRect(
            left.toFloat(),
            top.toFloat(),
            right.toFloat(),
            bottom.toFloat(),
            Paint().apply {
                color = Color.WHITE
                style = Paint.Style.FILL
            },
        )
        Log.w(TAG, "悬浮球没藏成功：把 $rect 涂白后交图（帧坐标 $left,$top-$right,$bottom）")
    }

    /**
     * 验收取证用：debug（debuggable）包把最近一帧留在 `filesDir/capture-last.jpg`，
     * 好 `adb pull` 下来量字节数 / 宽高 / 暗像素。**release 包一个字节都不写盘**
     * （截的是用户整屏，落盘就是隐私问题）。
     */
    private fun dumpForAcceptance(jpeg: ByteArray) {
        val debuggable = (applicationInfo.flags and ApplicationInfo.FLAG_DEBUGGABLE) != 0
        if (!debuggable) {
            return
        }
        runCatching {
            val file = File(filesDir, "capture-last.jpg")
            file.writeBytes(jpeg)
            Log.i(TAG, "最近一帧留在 ${file.absolutePath}（${file.length()} 字节）")
        }.onFailure { Log.w(TAG, "写取证文件失败：${it.message}") }
    }

    private fun Intent.projectionData(): Intent? =
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            getParcelableExtra(EXTRA_RESULT_DATA, Intent::class.java)
        } else {
            @Suppress("DEPRECATION")
            getParcelableExtra(EXTRA_RESULT_DATA)
        }
}

/**
 * `Image` → `Bitmap`。`ImageReader` 的行距带 padding（`rowStride > pixelStride * width`），
 * 直接 `copyPixelsFromBuffer` 会把行尾的填充像素也算进宽度，图会**斜**。
 */
private fun Image.toBitmap(): Bitmap {
    val plane = planes[0]
    val pixelStride = plane.pixelStride
    val rowStride = plane.rowStride
    val rowPadding = rowStride - pixelStride * width
    val padded = Bitmap.createBitmap(
        width + rowPadding / pixelStride,
        height,
        Bitmap.Config.ARGB_8888,
    )
    padded.copyPixelsFromBuffer(plane.buffer)
    return padded
}
