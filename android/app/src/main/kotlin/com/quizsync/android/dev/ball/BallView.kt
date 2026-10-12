package com.quizsync.android.dev.ball

import android.content.Context
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.RectF
import android.view.View
import kotlin.math.min

/**
 * 悬浮球的**画法**（规格 ui-spec-android §7.1，1.1.0 的 `FloatingBallManager` 是程序画的）。
 *
 * 三层，缺一层用户就看出来了：
 * * 球体：半径 `s/2 - 4dp` 的实心圆，品牌绿 `#16A34A`（多页/识别中换琥珀 `#F59E0B`），alpha = 不透明度；
 * * 描边：白色 alpha 120、宽 2dp 的白环；
 * * **图标：白色放大镜（圆 + 斜柄），描边 2.5dp** —— 上一版我只画了个纯色圆，
 *   用户当场指出「1.1.0 的悬浮窗图标没了」；放大镜是这个球唯一的语义（搜题）。
 * * 多页角标：右上角半径 7.5dp 的琥珀圆 + 白字 10dp 粗体（页数，0 页时是 `•`）。
 *
 * 为什么不用 PNG 素材：1.1.0 的安卓端就是矢量画的（没有随包球图，那是**桌面端**的
 * `floating_ball_*.png`），矢量画随尺寸缩放都清晰、也不用为三种状态各带一张图。
 */
class BallView(context: Context) : View(context) {

    private val density = resources.displayMetrics.density
    private val fill = Paint(Paint.ANTI_ALIAS_FLAG).apply { style = Paint.Style.FILL }
    private val stroke = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.STROKE
        strokeCap = Paint.Cap.ROUND
    }
    private val textPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = Color.WHITE
        textAlign = Paint.Align.CENTER
        typeface = android.graphics.Typeface.DEFAULT_BOLD
    }

    private var state = BallState.Idle
    private var opacity = 0.85f
    private var pages = 0

    fun update(state: BallState, opacity: Float, pages: Int) {
        this.state = state
        this.opacity = opacity
        this.pages = pages
        invalidate()
    }

    override fun onDraw(canvas: Canvas) {
        val s = min(width, height).toFloat()
        if (s <= 0f) return

        val alpha = (opacity.coerceIn(0.2f, 1f) * 255f).toInt()
        val radius = s / 2f - 4f * density
        val centerX = width / 2f
        val centerY = height / 2f

        val argb = state.argb.toInt()
        fill.color = Color.argb(alpha, Color.red(argb), Color.green(argb), Color.blue(argb))
        canvas.drawCircle(centerX, centerY, radius, fill)

        // 白环：跟着不透明度一起淡（否则 30% 透明度的球会顶着一个实心白圈）
        stroke.color = Color.argb((120 * (alpha / 255f)).toInt(), 255, 255, 255)
        stroke.strokeWidth = 2f * density
        canvas.drawCircle(centerX, centerY, radius, stroke)

        drawMagnifier(canvas, centerX, centerY, radius, alpha)

        if (pages >= 0 && state != BallState.Idle || pages > 0) {
            drawBadge(canvas, s, alpha)
        }
    }

    /** 白色放大镜：镜圈 + 斜柄（比例照规格：图标整体约占球的 46%）。 */
    private fun drawMagnifier(canvas: Canvas, centerX: Float, centerY: Float, radius: Float, alpha: Int) {
        val iconRadius = radius * 0.30f
        val lensCenterX = centerX - radius * 0.12f
        val lensCenterY = centerY - radius * 0.12f

        stroke.color = Color.argb(alpha, 255, 255, 255)
        stroke.strokeWidth = 2.5f * density
        canvas.drawCircle(lensCenterX, lensCenterY, iconRadius, stroke)

        // 柄：从镜圈右下沿 45° 往外
        val startX = lensCenterX + iconRadius * 0.7071f
        val startY = lensCenterY + iconRadius * 0.7071f
        canvas.drawLine(startX, startY, startX + iconRadius * 0.85f, startY + iconRadius * 0.85f, stroke)
    }

    /** 多页角标：右上角琥珀圆 + 白字（0 页时画 `•`，与 1.1.0 一致）。 */
    private fun drawBadge(canvas: Canvas, size: Float, alpha: Int) {
        val badgeRadius = 7.5f * density
        val badgeCenterX = size - 7f * density
        val badgeCenterY = 7f * density

        fill.color = Color.argb(alpha, 245, 158, 11)
        canvas.drawCircle(badgeCenterX, badgeCenterY, badgeRadius, fill)

        textPaint.textSize = 10f * density
        textPaint.alpha = alpha
        val label = if (pages > 0) pages.toString() else "•"
        val metrics = textPaint.fontMetrics
        val baseline = badgeCenterY - (metrics.ascent + metrics.descent) / 2f
        canvas.drawText(label, badgeCenterX, baseline, textPaint)
    }
}
