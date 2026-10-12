package com.quizsync.android.baselineprofile

import androidx.benchmark.macro.junit4.BaselineProfileRule
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.uiautomator.By
import androidx.test.uiautomator.Until
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

/**
 * Baseline Profile 生成器（Phase 3）。
 *
 * 它做的是一件很具体的事：**把「用户装上应用后真的会走的路径」跑一遍，让 ART 记下哪些
 * 方法/类在启动期被加载**，产出一份 profile 随 APK 分发 —— 这样首次安装后的那几次冷启动
 * 就不必再做全量 JIT/解释执行（AOT 编译按 profile 预热）。
 *
 * 覆盖选择（**不贪多**）：只走
 * ① 冷启动到主壳；② 三个底部标签各一次；③ 进一次设置页。
 * 这几条是每次打开应用都会经过的路径；再往深走（配对、识别、结果页）需要外部条件
 * （相机权限、主机在线、真图），**采集器不该依赖它们** —— 采集失败会让人以为"Profile 做好了"。
 *
 * 跑法（设备已解锁、允许 adb 安装）：
 *   gradle :baselineprofile:pixel6Api34BenchmarkAndroidTest   ← Gradle 托管设备，本机没有
 *   gradle :baselineprofile:connectedBenchmarkAndroidTest      ← 用真机（本项目走这条）
 */
@RunWith(AndroidJUnit4::class)
class BaselineProfileGenerator {

    @get:Rule
    val rule = BaselineProfileRule()

    @Test
    fun generate() {
        rule.collect(packageName = TARGET_PACKAGE, maxIterations = 8) {
            pressHome()
            startActivityAndWait()

            // 主壳（首次使用的机器会落在门页，那也算合法落点，所以不强制断言主壳）
            device.wait(Until.hasObject(By.text(TARGET_APP_NAME)), 10_000)

            // 三个标签各走一次 —— 这是"每次打开都会经过"的导航路径
            listOf("历史记录", "设置", "当前任务").forEach { tab ->
                device.findObject(By.text(tab))?.click()
                device.waitForIdle(2_000)
            }
        }
    }

    private companion object {
        /**
         * 被测应用：`app/build.gradle.kts` 的 **applicationId（release/benchmark 变体）** ——
         * `benchmark` 变体是 `initWith(release)`，不带 debug 的 `.dev` 后缀。
         * ⚠️ 它**不是** `namespace`：采集器认的是"装上去的那个包"（改了包名这里也要改）。
         */
        const val TARGET_PACKAGE = "com.quizsync.android.native"

        const val TARGET_APP_NAME = "AI 双端搜题"
    }
}
