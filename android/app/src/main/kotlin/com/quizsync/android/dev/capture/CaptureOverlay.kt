package com.quizsync.android.dev.capture

import android.graphics.Rect

/**
 * 截屏时要临时藏起来的**自家浮层**（当前只有悬浮球，规格 §3.1）。
 *
 * 为什么要有这个接口：截图拍的是整屏，球一定会被拍进去（用户看到的是一张带绿点的题图，
 * AI 也会把球当成题干的一部分）。规格 §3.1 的办法是「截屏前先隐藏球、等约 250ms 再取帧、
 * 截完恢复」。取帧那一侧（[CaptureService]）不该知道球是怎么实现的，所以这里只留三件事：
 * 球在哪、藏起来、恢复。
 *
 * **三个方法都必须在主线程调用**（`WindowManager.addView/removeView` 的要求），
 * 由 `QuizViewModel` 在 `Dispatchers.Main` 上调用。
 */
interface CaptureOverlay {
    /** 球**现在**占的屏幕像素矩形；没在显示 = null。用于「藏失败时涂白」的兜底。 */
    fun visibleRect(): Rect?

    /** 藏起来。返回 true = 窗口真的从 WindowManager 移除了（这一帧里不会有球）。 */
    fun hideForCapture(): Boolean

    /** 截完恢复。恢复要**原地**（球被用户拖到哪就回哪），不是回到默认位置。 */
    fun restoreAfterCapture()
}
