@file:Suppress("DEPRECATION")

package com.quizsync.android.dev.ui.haptics

import android.annotation.SuppressLint
import android.content.Context
import android.os.Build
import android.util.Log
import android.view.HapticFeedbackConstants
import android.view.View
import android.view.accessibility.AccessibilityManager
import androidx.compose.foundation.Indication
import androidx.compose.foundation.LocalIndication
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.runtime.Composable
import androidx.compose.runtime.derivedStateOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Modifier
import androidx.compose.ui.composed
import androidx.compose.ui.hapticfeedback.HapticFeedback
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.semantics.Role
import androidx.core.content.getSystemService

/**
 * 触感反馈（学习自 ImageToolbox 的 `core/ui/.../widget/enhanced/EnhancedHapticFeedback.kt`，
 * Apache-2.0，作者 T8RIN）。这是那个 App「手感」的主要来源之一，光有动画是不够的。
 *
 * 三条关键做法，**照抄而不是自己发明**：
 *
 * 1. **走 `View.performHapticFeedback` + `FLAG_IGNORE_GLOBAL_SETTING`**。
 *    只调 `HapticFeedbackType` 的默认实现会尊重系统的「触摸震动」开关，
 *    关掉之后整台 App 一点震动都没有；带上这个 flag 才有稳定的响应。
 * 2. **尊重无障碍**：读屏（`isTouchExplorationEnabled`）打开时不震 ——
 *    否则视障用户每次探索都会挨一下震动。
 * 3. **把震动包进点击本身**（`hapticsClickable` / `hapticsCombinedClickable`），
 *    而不是让每个调用点自己记得调。ImageToolbox 里 **`onClick` 发的是 `longPress()`**
 *    （对应 `HapticFeedbackConstants.LONG_PRESS`，一下「笃」的感觉），
 *    长按同样发它，双击发两下 —— 这是他们的既定取舍，照做。
 */
private fun View.vibrate() = reallyPerformHapticFeedback(HapticFeedbackConstants.CONTEXT_CLICK)

private fun View.vibrateStrong() = reallyPerformHapticFeedback(HapticFeedbackConstants.LONG_PRESS)

private fun View.reallyPerformHapticFeedback(feedbackConstant: Int) {
    runCatching {
        if (context.isTouchExplorationEnabled()) {
            Log.v(HapticsTag, "skip haptic $feedbackConstant: touch exploration on")
            return
        }

        isHapticFeedbackEnabled = true
        // `performHapticFeedback` 的返回值 = 平台是否受理了这次触感请求。
        // **必须记下来**：这是「震动到底发出去没有」在设备上唯一取得到的证据
        // （`dumpsys vibrator_manager` 在这台机器上只给设置、不给历史，实测过）。
        // ImageToolbox 在这一处也记日志（他们的 `makeLog`），我第一版把它简化成静默了。
        val handled = performHapticFeedback(
            feedbackConstant,
            HapticFeedbackConstants.FLAG_IGNORE_GLOBAL_SETTING,
        )
        Log.d(HapticsTag, "haptic $feedbackConstant handled=$handled")
    }.onFailure {
        // 震动失败不该影响功能，但也不能一声不响 —— 响了才知道是哪一步没通。
        Log.w(HapticsTag, "haptic $feedbackConstant failed", it)
    }
}

private const val HapticsTag = "QuizSyncHaptics"

private fun Context.isTouchExplorationEnabled(): Boolean =
    getSystemService<AccessibilityManager>()?.isTouchExplorationEnabled == true

/** 强度：0 = 关，1 = 轻（CONTEXT_CLICK），2 = 重（LONG_PRESS）。 */
@SuppressLint("InlinedApi")
internal data class EnhancedHapticFeedback(
    val strength: Int,
    val view: View,
) : HapticFeedback {

    override fun performHapticFeedback(hapticFeedbackType: HapticFeedbackType) {
        when (hapticFeedbackType) {
            HapticFeedbackType.LongPress -> when (strength) {
                1 -> view.vibrate()
                2 -> view.vibrateStrong()
            }

            HapticFeedbackType.TextHandleMove ->
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O_MR1) {
                    view.reallyPerformHapticFeedback(HapticFeedbackConstants.TEXT_HANDLE_MOVE)
                } else {
                    view.reallyPerformHapticFeedback(HapticFeedbackConstants.CLOCK_TICK)
                }

            HapticFeedbackType.Confirm -> view.reallyPerformHapticFeedback(HapticFeedbackConstants.CONFIRM)
            HapticFeedbackType.ContextClick -> view.reallyPerformHapticFeedback(HapticFeedbackConstants.CONTEXT_CLICK)
            HapticFeedbackType.GestureEnd -> view.reallyPerformHapticFeedback(HapticFeedbackConstants.GESTURE_END)
            HapticFeedbackType.GestureThresholdActivate ->
                view.reallyPerformHapticFeedback(HapticFeedbackConstants.GESTURE_THRESHOLD_ACTIVATE)

            HapticFeedbackType.Reject -> view.reallyPerformHapticFeedback(HapticFeedbackConstants.REJECT)
            HapticFeedbackType.SegmentFrequentTick ->
                view.reallyPerformHapticFeedback(HapticFeedbackConstants.SEGMENT_FREQUENT_TICK)

            HapticFeedbackType.SegmentTick -> view.reallyPerformHapticFeedback(HapticFeedbackConstants.SEGMENT_TICK)
            HapticFeedbackType.ToggleOff -> view.reallyPerformHapticFeedback(HapticFeedbackConstants.TOGGLE_OFF)
            HapticFeedbackType.ToggleOn -> view.reallyPerformHapticFeedback(HapticFeedbackConstants.TOGGLE_ON)
            HapticFeedbackType.VirtualKey -> view.reallyPerformHapticFeedback(HapticFeedbackConstants.VIRTUAL_KEY)
        }
    }
}

internal data object EmptyHaptics : HapticFeedback {
    override fun performHapticFeedback(hapticFeedbackType: HapticFeedbackType) = Unit
}

/** 轻 tick（拖动、切换之类）。 */
fun HapticFeedback.press() = performHapticFeedback(HapticFeedbackType.TextHandleMove)

/** 一下「笃」（点击、长按）。 */
fun HapticFeedback.longPress() = performHapticFeedback(HapticFeedbackType.LongPress)

fun HapticFeedback.toggle(on: Boolean) =
    performHapticFeedback(if (on) HapticFeedbackType.ToggleOn else HapticFeedbackType.ToggleOff)

@Composable
fun rememberEnhancedHapticFeedback(strength: Int = 1): HapticFeedback {
    val view = LocalView.current
    val haptics by remember(strength) {
        derivedStateOf {
            if (strength == 0) EmptyHaptics else EnhancedHapticFeedback(strength = strength, view = view)
        }
    }

    return haptics
}

/**
 * 会震动的点击。用它**替换** `Modifier.clickable` —— 别在 onClick 里自己调震动，
 * 那样漏一处就没手感，而且顺序（震动 vs 动作）各处不一致。
 */
fun Modifier.hapticsClickable(
    // 允许外部传入：**按下的状态要能被别人读**（比如让卡片的圆角跟着按下变）——
    // 学自 ImageToolbox 的 `shapeByInteraction(shape, pressedShape, interactionSource)`。
    interactionSource: MutableInteractionSource? = null,
    enabled: Boolean = true,
    onClickLabel: String? = null,
    role: Role? = null,
    enableHaptics: Boolean = true,
    onClick: () -> Unit,
): Modifier = composed {
    // 自带增强触感，不读 LocalHapticFeedback —— 少一处装配就少一处漏接。
    val haptics = rememberEnhancedHapticFeedback()

    clickable(
        interactionSource = interactionSource,
        indication = LocalIndication.current,
        enabled = enabled,
        onClickLabel = onClickLabel,
        role = role,
        onClick = {
            if (enableHaptics) haptics.longPress()
            onClick()
        },
    )
}

/** 会震动的单击 + 长按（长按同时发一下，与 ImageToolbox 一致）。 */
fun Modifier.hapticsCombinedClickable(
    interactionSource: MutableInteractionSource? = null,
    enabled: Boolean = true,
    onClickLabel: String? = null,
    role: Role? = null,
    onLongClickLabel: String? = null,
    onLongClick: (() -> Unit)? = null,
    onClick: () -> Unit,
): Modifier = composed {
    val haptics = rememberEnhancedHapticFeedback()

    combinedClickable(
        interactionSource = interactionSource,
        indication = LocalIndication.current,
        enabled = enabled,
        onClickLabel = onClickLabel,
        role = role,
        onLongClickLabel = onLongClickLabel,
        onLongClick = if (onLongClick != null) {
            {
                haptics.longPress()
                onLongClick()
            }
        } else {
            null
        },
        hapticFeedbackEnabled = false,
        onClick = {
            haptics.longPress()
            onClick()
        },
    )
}
