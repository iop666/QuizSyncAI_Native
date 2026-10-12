package com.quizsync.android.dev

import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.compose.ui.test.onAllNodesWithText
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.test.ext.junit.runners.AndroidJUnit4
import org.junit.Assert.assertTrue
import org.junit.Assume.assumeTrue
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith

/**
 * 真机仪器测试（`androidTest`）—— **这个目录以前根本不存在**（Phase 3 的缺口）。
 *
 * 与 JVM 单测的分工，别搞混：
 *  * `test/`（JVM）覆盖**纯逻辑**：解析、状态机、向量对拍 —— 跑得快、能在 CI 上跑；
 *  * `androidTest/`（本目录）覆盖**"真的装到机器上、真的画出来"**：Compose 树能不能组起来、
 *    点击能不能换页。这类事 JVM 测不了（要真 Activity、真渲染）。
 *
 * 与 `tools/verify_android_ui.ps1` 的关系：那个脚本走 UIA（`uiautomator dump` + 点坐标），
 * 好处是**不依赖被测代码的任何接口**，坏处是脆（要 sleep、坐标会漂、已经因为竞态红过好几次）。
 * 这里走 Compose 语义树，稳得多，但要求测试与代码同包同进程。**两者都留**：
 * 脚本验"装出来的那个包"，仪器测试验"这一份代码"。
 *
 * ⚠️ 落点取决于设备上的数据（有合集 → 主壳；没有 → 首次使用门页）。
 * 这不是"测试写得不确定"，而是**应用本来就有两个合法落点**；
 * 所以主壳相关的那两条用 `assumeTrue` 标成 skipped 而不是假装通过。
 */
@RunWith(AndroidJUnit4::class)
class AppSmokeInstrumentedTest {

    @get:Rule
    val rule = createAndroidComposeRule<MainActivity>()

    private val appName = "AI 双端搜题"

    /** 当前落在主壳上吗（而不是首次使用门页）。 */
    private fun onMainShell(): Boolean =
        rule.onAllNodesWithText("当前任务").fetchSemanticsNodes().isNotEmpty()

    @Test
    fun `启动后落在两个合法落点之一`() {
        val shell = onMainShell()
        val gate = rule.onAllNodesWithText("新建任务合集").fetchSemanticsNodes().isNotEmpty() ||
            rule.onAllNodesWithText("任务合集").fetchSemanticsNodes().isNotEmpty()

        assertTrue(
            "启动后既不是主壳也不是首次使用门页 —— 界面没画出来（主壳=$shell 门页=$gate）",
            shell || gate,
        )
    }

    @Test
    fun `主壳三个标签都在`() {
        assumeTrue("设备上没有合集 → 应用落在首次使用门页，这条不适用", onMainShell())

        rule.onNodeWithText(appName).assertExists()          // 顶栏应用名
        rule.onNodeWithText("当前任务").assertExists()
        rule.onNodeWithText("历史记录").assertExists()
        rule.onNodeWithText("设置").assertExists()
    }

    @Test
    fun `点历史记录能换页并看到搜索框`() {
        assumeTrue("设备上没有合集 → 应用落在首次使用门页，这条不适用", onMainShell())

        rule.onNodeWithText("历史记录").performClick()
        rule.waitForIdle()

        val hasSearch = rule.onAllNodesWithText("搜索题干与解析").fetchSemanticsNodes().isNotEmpty()
        val hasEmpty = rule.onAllNodesWithText("还没有识别记录").fetchSemanticsNodes().isNotEmpty()
        assertTrue("历史页既没有搜索框也没有空态（换页没生效？）", hasSearch || hasEmpty)
    }

    @Test
    fun `点设置能换页并看到分类`() {
        assumeTrue("设备上没有合集 → 应用落在首次使用门页，这条不适用", onMainShell())

        rule.onNodeWithText("设置").performClick()
        rule.waitForIdle()

        // 设置页的标题与至少一个分类（分类表见 ui-spec-android §2.5）
        val hasTheme = rule.onAllNodesWithText("主题").fetchSemanticsNodes().isNotEmpty()
        val hasRecognition = rule.onAllNodesWithText("识别模块").fetchSemanticsNodes().isNotEmpty()
        assertTrue("设置页没看到「主题」或「识别模块」—— 换页没生效？", hasTheme || hasRecognition)
    }
}
