package com.quizsync.android.dev.ball

import android.annotation.SuppressLint
import android.content.Context
import android.graphics.Color
import android.graphics.PixelFormat
import android.graphics.Rect
import android.graphics.drawable.GradientDrawable
import android.provider.Settings
import android.view.Gravity
import android.view.MotionEvent
import android.view.View
import android.view.WindowManager
import android.widget.FrameLayout
import android.widget.TextView
import com.quizsync.android.dev.capture.CaptureOverlay
import kotlin.math.abs
import kotlin.math.roundToInt

/** 悬浮球的三态（规格 ui-spec-android §7.1）。 */
enum class BallState(val argb: Long) {
    /** 待识别 —— 绿 `0xFF16A34A`（默认态）。 */
    Idle(0xFF16A34A),

    /** 识别中 —— 琥珀。 */
    Busy(0xFFF59E0B),

    /** 多页收集中 —— 琥珀 + 页数角标。 */
    MultiPage(0xFFF59E0B),
}

/**
 * 悬浮球（`WindowManager` overlay，规格 §7.1）。
 *
 * 契约逐条照规格：
 * * **默认贴右边缘、垂直 40% 屏高、48dp**；
 * * **单击 = 单图识别**（多页收集中 = 提交已截页）；**长按 ≥500ms = 进入 / 累加多页**；
 * * **拖动阈值 8dp**，松手吸附最近边缘（垂直位置保留）；
 * * 多页收集时在**右上角显示页数角标**。
 *
 * 三个设计要点：
 * 1. **手势在松手时结算**，不依赖任何计时器 —— 1.1.0 用 `WM_TIMER` 判长按踩过坑
 *    （`WM_TIMER` 是队列最低优先级，松手消息会先到，长按被判成单击）；
 * 2. 拖动**优先于**长按：位移超过阈值就不再算长按；
 * 3. 球是 `TYPE_APPLICATION_OVERLAY` + `FLAG_NOT_FOCUSABLE`：不抢焦点、不影响底下的应用。
 *
 * **不做的事**：真正截屏需要 MediaProjection 授权，那是另一条权限流程。
 * 这一版只把球做出来并把三个手势回调出去，回调里目前是「如实告知还没接上」。
 */
class FloatingBallController(
    private val context: Context,
    private val onTap: () -> Unit,
    private val onLongPress: () -> Unit,
) : CaptureOverlay {
    private val windowManager =
        context.getSystemService(Context.WINDOW_SERVICE) as WindowManager

    private var container: FrameLayout? = null
    private var circle: View? = null
    private var badge: TextView? = null
    private var params: WindowManager.LayoutParams? = null

    private var sizePx = 0
    private var state = BallState.Idle
    private var pages = 0

    /** 用户拖过之后的窗口位置（`null` = 还没拖过，用默认的「贴右 · 40% 屏高」）。 */
    private var savedX: Int? = null
    private var savedY: Int? = null

    /** 最近一次 `show()` 给的外观参数 —— 截完要按同一套外观原地恢复。 */
    private var lastSizeDp = 48
    private var lastOpacity = 1f

    /** 正在「为截屏而隐藏」：这期间 `show()` 只记外观，不把球放回屏幕上。 */
    private var captureHidden = false

    /** 这次按下有没有变成拖动（拖动优先于长按）。 */
    private var dragging = false
    private var downX = 0f
    private var downY = 0f
    private var downAt = 0L
    private var startX = 0
    private var startY = 0

    val isShowing: Boolean get() = container != null

    /** 有没有悬浮窗权限。没有的话什么都显示不出来，调用方要引导用户去开。 */
    fun canDrawOverlays(): Boolean = Settings.canDrawOverlays(context)

    /** 显示悬浮球（已在显示则只更新外观）。 */
    @SuppressLint("RtlHardcoded")
    fun show(sizeDp: Int, opacity: Float, state: BallState, pages: Int) {
        // 外观参数先记下来：截屏期间的 show() 只更新它，恢复时按最新的一套画。
        lastSizeDp = sizeDp
        lastOpacity = opacity
        this.state = state
        this.pages = pages

        // 正在为截屏而隐藏：这时候把球放回屏幕上，它就会被拍进图里。
        // 记下外观就返回，等 `restoreAfterCapture()` 再显示。
        if (captureHidden) {
            return
        }

        if (!canDrawOverlays()) {
            return
        }

        val density = context.resources.displayMetrics.density
        val newSize = (sizeDp * density).roundToInt().coerceAtLeast(16)

        if (container == null) {
            val root = FrameLayout(context)
            val dot = BallView(context)
            val label = TextView(context).apply {
                setTextColor(Color.WHITE)
                textSize = 10f
                gravity = Gravity.CENTER
            }

            root.addView(dot, FrameLayout.LayoutParams(newSize, newSize))
            root.addView(
                label,
                FrameLayout.LayoutParams(
                    FrameLayout.LayoutParams.WRAP_CONTENT,
                    FrameLayout.LayoutParams.WRAP_CONTENT,
                    Gravity.TOP or Gravity.END,
                ),
            )

            val layout = WindowManager.LayoutParams(
                WindowManager.LayoutParams.WRAP_CONTENT,
                WindowManager.LayoutParams.WRAP_CONTENT,
                WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY,
                WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE or
                    WindowManager.LayoutParams.FLAG_LAYOUT_NO_LIMITS,
                PixelFormat.TRANSLUCENT,
            ).apply {
                gravity = Gravity.TOP or Gravity.START
                val screenHeight = context.resources.displayMetrics.heightPixels
                val screenWidth = context.resources.displayMetrics.widthPixels
                // 默认：贴右边缘、垂直 40% 屏高（§7.1）；用户拖过就**回他拖到的位置**。
                x = savedX ?: (screenWidth - newSize)
                y = savedY ?: ((screenHeight * 0.4f).roundToInt() - newSize / 2)
            }

            root.setOnTouchListener { _, event -> onTouch(event) }

            container = root
            circle = dot
            badge = label
            params = layout
            sizePx = newSize
            windowManager.addView(root, layout)
        }

        if (sizePx != newSize) {
            sizePx = newSize
            circle?.layoutParams = FrameLayout.LayoutParams(newSize, newSize)
        }

        (circle as? BallView)?.update(state, opacity, pages)

        // 多页收集时才显示角标（§7.1）。
    }

    /** 换状态 / 页数（不重建窗口）。 */
    fun update(state: BallState, pages: Int) {
        this.state = state
        this.pages = pages
        (circle as? BallView)?.update(state, 1f, pages)
    }

    /** 球面：状态主色 + 整窗不透明度（规格 §7.1 的三态配色）。 */
    private fun dotDrawable(state: BallState, opacity: Float): GradientDrawable {
        val argb = state.argb.toInt()
        val alpha = (opacity.coerceIn(0.2f, 1f) * 255).roundToInt()
        return GradientDrawable().apply {
            shape = GradientDrawable.OVAL
            setColor(Color.argb(alpha, Color.red(argb), Color.green(argb), Color.blue(argb)))
        }
    }

    fun hide() {
        // 非截屏原因的隐藏（关掉开关 / 识别模块关闭）：取消「待恢复」语义 ——
        // 否则一次截屏途中的关开关会被 `restoreAfterCapture()` 又把球放回来。
        captureHidden = false
        val root = container ?: return
        runCatching { windowManager.removeView(root) }
        container = null
        circle = null
        badge = null
        params = null
    }

    // ========================================================================
    // 截屏配合（CaptureOverlay，规格 §3.1「截屏前必须先隐藏悬浮球自身」）
    // ========================================================================

    /** 球现在占的屏幕像素矩形；没显示 = null。 */
    override fun visibleRect(): Rect? {
        val root = container ?: return null
        // ⚠️ **不能用 `layout.x/y` 当屏幕坐标**：overlay 窗口的 x/y 相对的是「窗口父框架」，
        // 真机实测比屏幕坐标**低一个状态栏高度**（验收机：layout.y=1080 时球实际在 1152）。
        // 拿它当屏幕矩形去涂白，白块会落在球的**上面**一条，球还留在图里。
        // 截出来的帧覆盖整块屏幕，所以这里必须给真实屏幕坐标 —— 用 `getLocationOnScreen`。
        val location = IntArray(2)
        root.getLocationOnScreen(location)
        // 用实测宽高（角标会让容器比圆点宽一点），没量到就退回圆点边长。
        val width = if (root.width > 0) root.width else sizePx
        val height = if (root.height > 0) root.height else sizePx
        return Rect(location[0], location[1], location[0] + width, location[1] + height)
    }

    /**
     * 藏起来准备截屏。位置先记下来 —— 恢复必须**原地**，不能回默认位置
     * （球被拖到左下角，截一次图又跳回右侧 40% 屏高，用户会以为球自己跑了）。
     */
    override fun hideForCapture(): Boolean {
        val root = container ?: return false
        params?.let {
            savedX = it.x
            savedY = it.y
        }
        val removed = runCatching { windowManager.removeView(root) }.isSuccess
        container = null
        circle = null
        badge = null
        params = null
        captureHidden = removed
        return removed
    }

    /** 截完恢复（按最近一次 `show()` 的外观、在原位置重建窗口）。 */
    override fun restoreAfterCapture() {
        if (!captureHidden) {
            return
        }
        captureHidden = false
        show(lastSizeDp, lastOpacity, state, pages)
    }

    /**
     * 手势结算（松手时做，不用任何计时器）：
     * 拖动 > 长按 > 单击，优先级从高到低。
     */
    private fun onTouch(event: MotionEvent): Boolean {
        val layout = params ?: return false
        val threshold = 8 * context.resources.displayMetrics.density

        when (event.action) {
            MotionEvent.ACTION_DOWN -> {
                dragging = false
                downX = event.rawX
                downY = event.rawY
                downAt = System.currentTimeMillis()
                startX = layout.x
                startY = layout.y
                return true
            }

            MotionEvent.ACTION_MOVE -> {
                val dx = event.rawX - downX
                val dy = event.rawY - downY
                if (!dragging && (abs(dx) > threshold || abs(dy) > threshold)) {
                    dragging = true
                }

                if (dragging) {
                    layout.x = startX + dx.roundToInt()
                    layout.y = startY + dy.roundToInt()
                    container?.let { runCatching { windowManager.updateViewLayout(it, layout) } }
                }

                return true
            }

            MotionEvent.ACTION_UP -> {
                if (dragging) {
                    // 松手吸附最近的左 / 右边缘，垂直位置保留（§7.1）。
                    val screenWidth = context.resources.displayMetrics.widthPixels
                    layout.x = if (layout.x + sizePx / 2 < screenWidth / 2) 0 else screenWidth - sizePx
                    container?.let { runCatching { windowManager.updateViewLayout(it, layout) } }
                    // 记住用户放下的位置：之后「为截屏藏起来 → 恢复」要回到这里。
                    savedX = layout.x
                    savedY = layout.y
                } else if (System.currentTimeMillis() - downAt >= 500) {
                    onLongPress()
                } else {
                    onTap()
                }

                dragging = false
                return true
            }
        }

        return false
    }
}
