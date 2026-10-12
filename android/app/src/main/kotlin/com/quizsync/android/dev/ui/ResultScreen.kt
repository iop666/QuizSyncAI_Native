// 结果页（规格 ui-spec-android §3）—— 出处 `app/lib/ui/result_page.dart` + `ui-pkg/src/question_card.dart`。
//
// 逐条对照的范围：
//  * §3.1 页面骨架（AppBar 两行 / 三个 action / 会话切换条 / body 的四种状态 / 免责声明）；
//  * §3.2 顶部状态药丸行（五项，顺序固定）；
//  * §3.3 无题目的两种空状态（失败 / 非失败）；
//  * §3.4 题目卡片（容器 / 不全横幅 / 题号行 8 元素 / 材料折叠 / 选项区 / 答案区 / 无答案提示 /
//    解析 / 告警条 / 已人工修改药丸）；
//  * §3.5 标绿标黄的**判定**不在这里 —— 已在 `model/QuizModels.kt` 的 `computeHighlight` 算好，
//    本文件只负责按 `question.highlight` 上色；
//  * §4.3 结果页浮层（`NextRoundBanner`，opacity 0.85，画在最后 = 压在最上层）。
//
// **字号**：题干 / 选项 / 答案用设置里的 `fontSize`（12–32，默认 16），其余全部是它的**倍数**
// （解析 ×0.92、告警 ×0.82、材料标题 ×0.8、选项圈内字母 ×0.78…，见 §6.4 的字号表）——
// 结果页有 A− / A＋ 调字号，写死 px 会让调字号只对一半内容生效。
//
// **已知差距（如实标注，不假装做到了）**：§3.6 的 `MathText`（LaTeX / 化学式下标 / 表格）
// 在安卓端**没有实现**，这里用普通 `Text` 原样显示文本。
package com.quizsync.android.dev.ui

import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.drawBehind
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Size
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Autorenew
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.filled.ChevronLeft
import androidx.compose.material.icons.filled.ChevronRight
import androidx.compose.material.icons.filled.Edit
import androidx.compose.material.icons.filled.ErrorOutline
import androidx.compose.material.icons.automirrored.filled.HelpOutline
import androidx.compose.material.icons.filled.Image
import androidx.compose.material.icons.filled.Info
import androidx.compose.material.icons.filled.Lightbulb
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.ReportGmailerrorred
import androidx.compose.material.icons.outlined.ExpandLess
import androidx.compose.material.icons.outlined.ExpandMore
import androidx.compose.material.icons.outlined.Inbox
import androidx.compose.material.icons.automirrored.outlined.MenuBook
import androidx.compose.material.icons.outlined.SearchOff
import androidx.compose.material.icons.outlined.TextDecrease
import androidx.compose.material.icons.outlined.TextIncrease
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.material3.LocalTextStyle
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Text
import androidx.compose.ui.input.nestedscroll.nestedScroll
import androidx.compose.material3.LargeTopAppBar
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalWindowInfo
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.quizsync.android.dev.QsTokens
import com.quizsync.android.dev.model.QuestionItem
import com.quizsync.android.dev.model.SessionItem
import com.quizsync.android.dev.model.SessionStatus
import com.quizsync.android.dev.model.formatCardStamp
import com.quizsync.android.dev.state.ActiveTaskState
import com.quizsync.android.dev.state.ResultState
import com.quizsync.android.dev.ui.components.MathText
import com.quizsync.android.dev.ui.components.Disclaimer
import com.quizsync.android.dev.ui.designsystem.QsCard
import com.quizsync.android.dev.ui.designsystem.QsOptionBadge
import com.quizsync.android.dev.ui.designsystem.QsSecondaryButton
import com.quizsync.android.dev.ui.designsystem.QsStateView
import com.quizsync.android.dev.ui.designsystem.QsTag
import com.quizsync.android.dev.ui.designsystem.QsTextButton
import com.quizsync.android.dev.ui.designsystem.QsTonalButton
import com.quizsync.android.dev.ui.components.MessageEffect
import com.quizsync.android.dev.ui.components.NextRoundBanner
import com.quizsync.android.dev.ui.components.QuizSnackbarHost
import com.quizsync.android.dev.ui.components.StatusStyle
import com.quizsync.android.dev.ui.components.statusStyleOf
import com.quizsync.android.dev.ui.haptics.hapticsClickable
import com.quizsync.android.dev.ui.theme.QuizPalette
import com.quizsync.android.dev.ui.theme.quizPalette
import androidx.compose.ui.text.font.FontFamily

/** §4.3：结果页浮层的透明度（`kResultBannerOpacity`，result_page.dart:19）。 */
private const val kResultBannerOpacity = 0.85f

/** 字号范围（§2.5 / §3.1：12–32，默认 16）。 */
private const val kMinFontSize = 12
private const val kMaxFontSize = 32

/** 宽屏门槛：窗口宽度 ≥ 这个值才在结果页标题栏上给「缩小 / 放大字号」两个按钮。 */
const val kFontActionsMinWidthDp = 600f

/**
 * 结果页标题栏要不要放「字号加减」——**纯函数**，单测直接钉（`ResultScreenPolicyTest`）。
 *
 * | 窗口宽度 | 典型设备 | 结果 |
 * |---|---|---|
 * | < 600dp | 手机竖屏（本机 480dp） | **不显示**（用户口径：「默认手机显示状态下删除」） |
 * | ≥ 600dp | 平板竖屏 720dp / 平板横屏 / 手机横屏 | 显示（用户口径：「平板等横屏保留」） |
 */
fun qsShowsFontActions(widthDp: Float): Boolean = widthDp >= kFontActionsMinWidthDp

/**
 * 结果页（§3.1）。**压在主壳之上的路由**，返回走 `onBack`（= `viewModel.popRoute()`）。
 *
 * `task` 只用来画 §4.3 的浮层（电脑正在识别中 / 识别完成），不参与题目渲染。
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ResultScreen(
    state: ResultState,
    dark: Boolean,
    fontSize: Int,
    task: ActiveTaskState,
    message: String?,
    messageId: Long,
    onMessageShown: () -> Unit,
    onBack: () -> Unit,
    onStep: (Int) -> Unit,
    onReanalyze: () -> Unit,
    onReload: () -> Unit,
    onBumpFontSize: (Int) -> Unit,
) {
    val session = state.session

    // 提示条按 §1.4 浮动样式；「重新生成」的结果就弹在这里（主壳的 Snackbar 盖不到压栈页）。
    val snackbarHostState = remember { SnackbarHostState() }
    MessageEffect(snackbarHostState, message, messageId) { onMessageShown() }

    // **识别结果标题定死在上面**（用户口径 2026-09-27：「把识别结果标题定死在上面，不要现在这样滑动的」）。
    // 原来这里是 P4 做的"可折叠大标题"（`LargeTopAppBar` + `exitUntilCollapsedScrollBehavior()`：
    // 往下滚时大标题收成一行、把纵向空间还给题目）。用户不要这个 —— 标题必须一直在，
    // 所以换成固定高度的 `TopAppBar`，滚动不再与标题联动（`nestedScroll` 那根线也随之去掉）。

    // ── 「上一次 / 下一次识别」**不做任何动画** ──────────────────────────────
    // 用户口径 2026-09-28（第二轮）：「删除除了进入页面和返回页面外的所有动画」。
    // 这里曾经是一套 shared-axis X（旧内容退 1/4 屏淡出、新内容从另一侧进 1/4 屏淡入），
    // 为了它还要按会话缓存最近两屏的内容（两层同时画 ⇒ 退出那一层得画得出"上一条"）。
    // 现在两点一起删掉：**直接换内容**，屏幕上不会同时存在两屏。
    //
    // 仍然保留的一件与动画无关的事：`stepResult` 是**原子切换**（先读库、再换 id），
    // 所以换 id 的那一帧内容就已经就绪，不会先闪一个转圈再换成题目。

    // 右下角/右上角那一排动作放不放得下「字号加减」（用户 2026-09-28 第四条：
    // 「默认手机显示状态下删除结果页中字号加减的选项（平板等横屏保留）」）。
    //
    // 判据用**窗口宽度 ≥ 600dp**（而不是 orientation）：
    //  * 手机竖屏（本机 1440px / 3.0 = 480dp）→ 不显示 ✓「默认手机显示状态」；
    //  * 平板竖屏（本机 1800px / 2.5 = 720dp）、平板横屏、手机横屏 → 显示 ✓「平板等横屏保留」。
    // 用窗口宽度而不是屏幕宽度：分屏/小窗下按真实可用宽度决定，不会出现"半屏也挤五个图标"。
    // ⚠️ 调字号在宽屏仍可用，只是入口从结果页顶部挪到了 settings 一类的窄屏路径 ——
    // 窄屏用户改字号走「设置 → 外观 → 题目字号」的那条滑杆（那里同时给了预览）。
    val wide = with(LocalDensity.current) {
        LocalWindowInfo.current.containerSize.width.toDp().value
    }
    val showFontActions = qsShowsFontActions(wide)

    Scaffold(
        snackbarHost = { QuizSnackbarHost(snackbarHostState) },
        topBar = {
            // ⚠️ 这里原来是一整个 `Column`（`TopAppBar` + 它下面那条**会话切换条**）——
            // 因为 Compose 的 `TopAppBar` **没有** `bottom` 参数，而状态栏 inset 只由它自己吃，
            // 挂在它后面的那条会落进状态栏里（实测 y 18..78，与系统时钟重叠）。
            // 用户口径 2026-09-28：「把上一次/下一次的按钮放到标题栏右上角、重新生成的前面」——
            // 两个按钮搬进 `actions` 之后，那条切换条没有内容了（它中间那个「N / M」与标题第二行
            // 「第 N / M 次识别 · 时间」重复），于是整条删掉、`Column` 也跟着去掉。
            TopAppBar(
                title = {
                    // 两行（§3.1）：『识别结果』；有会话时补一行『第 N / M 次识别 · 时间』。
                    // 字号从字面量（17 / 11.5sp）收敛到字号阶的角色 —— 那两个值是 1.1.0 的
                    // Flutter 像素对齐产物，原生排版里没有意义。
                    Column {
                        Text(
                            text = "识别结果",
                            style = MaterialTheme.typography.titleLarge,
                            fontWeight = FontWeight.Bold,
                        )
                        val position = state.positionLabel
                        if (position != null && session != null) {
                            Text(
                                text = position,
                                style = MaterialTheme.typography.labelSmall,
                                fontWeight = FontWeight.Medium,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                        }
                    }
                },
                navigationIcon = {
                    IconButton(onClick = onBack) {
                        Icon(Icons.Default.ChevronLeft, contentDescription = "返回")
                    }
                },
                actions = {
                    // 右上角动作顺序（用户 2026-09-28 第三条口径）：
                    //   [缩小字号] [放大字号] → [重新生成] → [上一次识别] [下一次识别]
                    // 即：字号加减在**最左**（"重新生成前面"），重新生成在**上一条/下一条前面**。
                    // 调字号是"临时看一眼"，所以挨着标题、离返回最近；换会话是"翻页"，留在最右。
                    //
                    // 字号加减只在宽屏出现（下一条注释）：手机竖屏放不下五个动作。
                    if (showFontActions) {
                        IconButton(onClick = { onBumpFontSize(-1) }, enabled = fontSize > kMinFontSize) {
                            Icon(Icons.Outlined.TextDecrease, contentDescription = "缩小字号")
                        }
                        IconButton(onClick = { onBumpFontSize(1) }, enabled = fontSize < kMaxFontSize) {
                            Icon(Icons.Outlined.TextIncrease, contentDescription = "放大字号")
                        }
                    }
                    if (state.reanalyzing) {
                        // 重新生成中：转圈替代按钮（§3.1）。
                        Box(
                            modifier = Modifier.padding(horizontal = QsTokens.spaceMd).size(QsTokens.touchTarget),
                            contentAlignment = Alignment.Center,
                        ) {
                            CircularProgressIndicator(
                                modifier = Modifier.size(QsTokens.iconSm),
                                strokeWidth = QsTokens.progressStroke,
                            )
                        }
                    } else {
                        IconButton(onClick = onReanalyze, enabled = !state.missing) {
                            Icon(Icons.Default.Autorenew, contentDescription = "重新生成")
                        }
                    }
                    // 「上一次 / 下一次识别」——原来是顶栏下面那条切换条里的两个**文字**按钮，
                    // 现在按用户口径收进标题栏右上角。收成图标按钮是为了和右边几个同一套尺寸
                    // 与触控目标；无障碍名称仍是完整的「上一次识别 / 下一次识别」。
                    // 顺序与切换条一致：左 = 更早的一条，右 = 更新的一条。
                    IconButton(onClick = { onStep(1) }, enabled = state.hasPrev) {
                        Icon(Icons.Default.ChevronLeft, contentDescription = "上一次识别")
                    }
                    IconButton(onClick = { onStep(-1) }, enabled = state.hasNext) {
                        Icon(Icons.Default.ChevronRight, contentDescription = "下一次识别")
                    }
                    Spacer(Modifier.width(QsTokens.spaceXs))
                },
            )
        },
        bottomBar = {
            // 常驻免责声明（§1.4 / §3.1）：11sp、次要色。
            Disclaimer(modifier = Modifier.padding(QsTokens.spaceSm))
        },
    ) { padding ->
        Box(modifier = Modifier.fillMaxSize().padding(padding)) {
            // 直接画当前会话的内容（没有转场，见上面那一段注释）。
            when {
                state.loading -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                    CircularProgressIndicator()
                }

                state.missing -> QsStateView(
                    icon = Icons.Outlined.SearchOff,
                    title = "本次识别已不存在",
                    message = "可能已被清理或删除。返回列表挑选其它记录即可。",
                    actions = { QsTonalButton(onClick = onBack) { Text("返回列表") } },
                )

                state.questions.isEmpty() -> EmptyResult(
                    session = session,
                    style = session?.let { statusStyleOf(it.status, dark) },
                    onReload = onReload,
                    onReanalyze = onReanalyze,
                )

                else -> LazyColumn(
                    modifier = Modifier.fillMaxSize(),
                    contentPadding = PaddingValues(top = QsTokens.spaceSm, bottom = QsTokens.spaceLg),
                ) {
                    item(key = "result-pills") {
                        PillRow(session = session, questions = state.questions)
                    }
                    itemsIndexed(state.questions, key = { _, q -> q.id }) { _, question ->
                        QuestionCard(question = question, fontSize = fontSize)
                    }
                }
            }

            // ⚠️ 浮层必须是**最后一个孩子**才压在题目之上（Stack 里后画的在上；§4.3 / M18 的返工结论）。
            hostBanner(task = task, currentSessionId = state.sessionId)?.let { banner ->
                Box(modifier = Modifier.padding(start = QsTokens.spaceMd, top = QsTokens.spaceSm, end = QsTokens.spaceMd)) {
                    NextRoundBanner(
                        text = banner.text,
                        spinning = banner.spinning,
                        opacity = kResultBannerOpacity,
                    )
                }
            }
        }
    }
}

// ============================================================================
// §3.2 顶部药丸行
// ============================================================================

@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun PillRow(session: SessionItem?, questions: List<QuestionItem>) {
    val variant = MaterialTheme.colorScheme.onSurfaceVariant
    FlowRow(
        modifier = Modifier
            .fillMaxWidth()
            .padding(start = QsTokens.spaceMd, top = QsTokens.spaceXxs, end = QsTokens.spaceMd, bottom = QsTokens.spaceSm),
        horizontalArrangement = Arrangement.spacedBy(QsTokens.spaceSm),
        verticalArrangement = Arrangement.spacedBy(QsTokens.spaceSm),
    ) {
        // 顺序固定：① 会话状态（filled）② 共 N 题 ③ N 张图片 ④ 合集记录 ⑤ 模型名。
        session?.let {
            val style = statusStyleOf(it.status, quizPalette.dark)
            QsTag(text = style.label, color = style.color, filled = true)
        }
        QsTag(text = "共 ${questions.size} 题", color = variant, filled = false)
        QsTag(
            text = "${session?.imageCount ?: 0} 张图片",
            color = variant,
            filled = false,
            icon = Icons.Default.Image,
        )
        if (session?.collectionId != null) {
            QsTag(text = "合集记录", color = variant, filled = false)
        }
        val model = session?.aiModel?.trim()
        if (!model.isNullOrEmpty()) {
            QsTag(text = model, color = variant, filled = false)
        }
    }
}

// ============================================================================
// §3.3 无题目的两种空状态
// ============================================================================

@Composable
private fun EmptyResult(
    session: SessionItem?,
    style: StatusStyle?,
    onReload: () -> Unit,
    onReanalyze: () -> Unit,
) {
    val failed = session?.status == SessionStatus.Failed.wire
    // `failed` 为真时 session 必然非空，但 Kotlin 不知道 —— 先取出来，避免 `!!`。
    val failedSession = session?.takeIf { failed }
    QsStateView(
        icon = if (failed) Icons.Default.ErrorOutline else Icons.Outlined.Inbox,
        title = if (failed) "分析失败" else "本次识别没有题目",
        message = if (failed) {
            failedSession?.errorMessage ?: failedSession?.errorLabel ?: "原因未知，可在电脑端重试"
        } else {
            "截图里可能没有可识别的题目，换个更清晰的截图再试。"
        },
        actions = {
            Row(
                horizontalArrangement = Arrangement.spacedBy(QsTokens.spaceSm),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                QsSecondaryButton(onClick = onReload) {
                    Icon(Icons.Default.Refresh, contentDescription = null, modifier = Modifier.size(QsTokens.iconSm))
                    Spacer(Modifier.width(QsTokens.spaceSm))
                    Text("重新加载")
                }
                QsTonalButton(onClick = onReanalyze) {
                    Icon(Icons.Default.Autorenew, contentDescription = null, modifier = Modifier.size(QsTokens.iconSm))
                    Spacer(Modifier.width(QsTokens.spaceSm))
                    Text("重新生成")
                }
                style?.let { QsTag(text = it.label, color = it.color, filled = true) }
            }
        },
    )
}

// ============================================================================
// §3.4 题目卡片
// ============================================================================

@Composable
private fun QuestionCard(question: QuestionItem, fontSize: Int) {
    val palette = quizPalette
    val mark = if (question.highlight.needsReview) palette.uncertainText else palette.highlightText
    val markBackground =
        if (question.highlight.needsReview) palette.uncertainBackground else palette.highlightBackground

    QsCard(
        modifier = Modifier
            .fillMaxWidth()
            .padding(horizontal = QsTokens.spaceMd, vertical = QsTokens.spaceSm),
        // 内边距仍由下面那层 `Column` 自己管（原样搬运，避免 QsCard 的默认内距叠上来）。
        contentPadding = PaddingValues(0.dp),
        // 主题默认 1px 描边；题目不全时整卡换 2px 黄框（§3.4.1）。
        border = if (question.incomplete) {
            BorderStroke(QsTokens.strokeEmphasis, palette.incomplete)
        } else {
            BorderStroke(QsTokens.hairline, palette.cardStroke)
        },
    ) {
        Column(
            modifier = Modifier.fillMaxWidth().padding(QsTokens.spaceLg),
            verticalArrangement = Arrangement.Top,
        ) {
            if (question.incomplete) {
                IncompleteBanner(question = question, fontSize = fontSize, palette = palette)
                Spacer(Modifier.height(QsTokens.spaceSm))
            }

            QuestionHeader(question = question, palette = palette)
            Spacer(Modifier.height(QsTokens.spaceMd))

            if (question.hasMaterial) {
                MaterialPanel(question = question, fontSize = fontSize, palette = palette)
                Spacer(Modifier.height(QsTokens.spaceMd))
            }

            // 题干：字号跟设置走；**字重加粗**（用户 2026-09-28：「结果页题目要加粗」）。
            // 这一行原来写 `FontWeight.Medium` —— 比主题 `bodyLarge` 的 SemiBold **还轻**，
            // 于是屏幕上"解析比题目粗"（解析继承主题的 SemiBold），与用户要的正好相反。
            // 每处都显式给字重，不再靠"继承主题"这条隐式通道（它正是当初反掉的原因）。
            MathText(
                text = question.stem,
                fontSize = fontSize,
                lineHeight = (fontSize * 1.4f).sp,
                fontWeight = FontWeight.Bold,
                color = palette.questionText,
            )

            if (question.options.isNotEmpty()) {
                Spacer(Modifier.height(QsTokens.spaceMd))
                OptionArea(
                    question = question,
                    fontSize = fontSize,
                    palette = palette,
                    mark = mark,
                    markBackground = markBackground,
                )
            }

            if (question.highlight.highlightAnswerText) {
                Spacer(Modifier.height(QsTokens.spaceMd))
                AnswerBlock(
                    question = question,
                    fontSize = fontSize,
                    palette = palette,
                    mark = mark,
                    markBackground = markBackground,
                )
            }

            if (question.highlight.noAnswer) {
                Spacer(Modifier.height(QsTokens.spaceMd))
                NoAnswerHint(fontSize = fontSize, palette = palette)
            }

            if (question.analysis.isNotBlank()) {
                Spacer(Modifier.height(QsTokens.spaceMd))
                AnalysisBlock(question = question, fontSize = fontSize, palette = palette)
            }

            if (question.warnings.isNotEmpty()) {
                Spacer(Modifier.height(QsTokens.spaceSm))
                Warnings(question = question, fontSize = fontSize, palette = palette)
            }

            if (question.answerEdited || question.analysisEdited) {
                Spacer(Modifier.height(QsTokens.spaceSm))
                QsTag(
                    text = "已人工修改",
                    color = MaterialTheme.colorScheme.primary,
                    filled = false,
                    icon = Icons.Default.Edit,
                )
            }
        }
    }
}

/** §3.4.4「题目不全」横幅：整宽、左 4px 黄条、底色黄、文案二选一。 */
@Composable
private fun IncompleteBanner(question: QuestionItem, fontSize: Int, palette: QuizPalette) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .background(palette.incompleteBackground, RoundedCornerShape(QsTokens.radiusControl))
            // 不画左侧竖条（用户 2026-09-27 明确要求原生端不要竖条）。
            .border(QsTokens.hairline, palette.incomplete.copy(alpha = 0.45f), RoundedCornerShape(QsTokens.radiusControl))
            .padding(horizontal = 10.dp, vertical = 7.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Icon(
            Icons.Default.ReportGmailerrorred,
            contentDescription = null,
            tint = palette.incomplete,
            modifier = Modifier.size(QsTokens.iconBase),
        )
        Spacer(Modifier.width(QsTokens.spaceSm))
        Text(
            text = if (question.answerGuessed) {
                "题目不全（选项被截断或缺失），下面的答案是 AI 按题意推断的"
            } else {
                "题目不全（题干或选项被截断）"
            },
            fontSize = (fontSize * 0.82f).sp,
            lineHeight = (fontSize * 0.82f * 1.4f).sp,
            fontWeight = FontWeight.SemiBold,
            color = palette.incomplete,
        )
    }
}

/** §3.4.3 题号行：8 个元素，顺序与出现条件见规格表。 */
@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun QuestionHeader(question: QuestionItem, palette: QuizPalette) {
    val primary = MaterialTheme.colorScheme.primary
    val variant = MaterialTheme.colorScheme.onSurfaceVariant
    FlowRow(
        modifier = Modifier.fillMaxWidth(),
        horizontalArrangement = Arrangement.spacedBy(QsTokens.spaceSm),
        // 换行后的行间距（§3.4.3 的 `runSpacing: AppSpacing.xs`）。Compose 的 FlowRow
        // 用 `itemVerticalAlignment` 表达「每项在行内居中」，没有 `verticalAlignment`。
        verticalArrangement = Arrangement.spacedBy(QsTokens.spaceXs),
        itemVerticalAlignment = Alignment.CenterVertically,
    ) {
        // ① 排序序号（**不带点号**）
        Text(
            text = "${question.ordinal + 1}",
            fontSize = 12.5.sp,
            lineHeight = 15.sp,
            fontWeight = FontWeight.Bold,
            color = primary,
        )
        // ② 题号徽标（主色底 + 白字）
        Text(
            text = question.questionNoLabel ?: "第 ${question.ordinal + 1} 题",
            fontSize = 11.5.sp,
            lineHeight = 13.8.sp,
            fontWeight = FontWeight.Bold,
            color = Color.White,
            modifier = Modifier
                .background(primary, RoundedCornerShape(QsTokens.radiusChip))
                .padding(horizontal = 9.dp, vertical = 3.dp),
        )
        // ③ 题型药丸（未知题型原样显示）
        QsTag(text = question.type.label, color = variant, filled = false)
        // ④ AI 猜测答案
        if (question.answerGuessed) {
            Badge(
                text = "AI 猜测答案",
                foreground = palette.incomplete,
                background = palette.incompleteBackground,
                border = palette.incomplete,
                weight = FontWeight.Bold,
            )
        }
        // ⑤ AI 不确定，建议复核
        if (question.highlight.needsReview) {
            Badge(
                text = "AI 不确定，建议复核",
                foreground = palette.reviewBadge,
                background = palette.reviewBadgeBackground,
                border = palette.reviewBadge,
                weight = FontWeight.SemiBold,
            )
        }
        // ⑥ 图中已印答案
        if (question.answerInImage) {
            QsTag(text = "图中已印答案", color = variant, filled = false, icon = Icons.Default.Image)
        }
        // ⑦ 把握率：只在 `0 < confidence < 1` 时出现（安卓端不传 alwaysShowConfidence）
        if (question.confidence > 0 && question.confidence < 1) {
            Text(
                text = "把握 ${Math.round(question.confidence * 100)}%",
                fontSize = 11.sp,
                color = variant,
            )
        }
        // ⑧ 时间戳
        Text(text = formatCardStamp(question.updatedAt), fontSize = 11.sp, color = variant)
    }
}

/** 猜测 / 复核徽标（§3.4.3 第 4、5 项）：11.5、圆角 999、1px 描边。 */
@Composable
private fun Badge(
    text: String,
    foreground: Color,
    background: Color,
    border: Color,
    weight: FontWeight,
) {
    Text(
        text = text,
        fontSize = 11.5.sp,
        lineHeight = 13.8.sp,
        fontWeight = weight,
        color = foreground,
        modifier = Modifier
            .background(background, RoundedCornerShape(QsTokens.radiusChip))
            .border(QsTokens.hairline, border, RoundedCornerShape(QsTokens.radiusChip))
            .padding(horizontal = 8.dp, vertical = 3.dp),
    )
}

/** §3.4.10 阅读材料折叠面板：**默认折叠**，放在题干之前。 */
@Composable
private fun MaterialPanel(question: QuestionItem, fontSize: Int, palette: QuizPalette) {
    var open by remember(question.id) { mutableStateOf(false) }
    val lineCount = remember(question.material) {
        question.material.split('\n').count { it.trim().isNotEmpty() }
    }

    Column(
        modifier = Modifier
            .fillMaxWidth()
            .background(palette.materialBackground, RoundedCornerShape(QsTokens.radiusControl))
            .border(QsTokens.hairline, palette.materialStroke, RoundedCornerShape(QsTokens.radiusControl)),
    ) {
        Row(
            modifier = Modifier
                .fillMaxWidth()
                // 可点元素一律带触感（既定风格）。
                .hapticsClickable { open = !open }
                .padding(horizontal = 12.dp, vertical = 10.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Icon(
                Icons.AutoMirrored.Outlined.MenuBook,
                contentDescription = null,
                tint = palette.reviewBadge,
                modifier = Modifier.size(QsTokens.iconBase),
            )
            Spacer(Modifier.width(6.dp))
            Text(
                text = if (open) "阅读材料（点击收起）" else "阅读材料（$lineCount 行，点击展开）",
                fontSize = (fontSize * 0.8f).sp,
                lineHeight = (fontSize * 0.8f * 1.3f).sp,
                fontWeight = FontWeight.Bold,
                color = MaterialTheme.colorScheme.onSurface,
                modifier = Modifier.weight(1f),
            )
            Icon(
                if (open) Icons.Outlined.ExpandLess else Icons.Outlined.ExpandMore,
                contentDescription = null,
                tint = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.size(QsTokens.iconSm),
            )
        }
        // 展开/收起**不做动画**（用户口径 2026-09-28：「删除除了进入页面和返回页面外的所有动画」）。
        // 这里以前是 `AnimatedVisibility(expandVertically + fadeIn / shrinkVertically + fadeOut)`。
        if (open) {
            MathText(
                text = question.material,
                fontSize = (fontSize * 0.92f).toInt(),
                lineHeight = (fontSize * 0.92f * 1.65f).sp,
                color = MaterialTheme.colorScheme.onSurface,
                modifier = Modifier.padding(start = QsTokens.spaceMd, end = QsTokens.spaceMd, bottom = QsTokens.spaceMd),
            )
        }
    }
}

// ============================================================================
// §3.4.5 选项区
// ============================================================================

@Composable
private fun OptionArea(
    question: QuestionItem,
    fontSize: Int,
    palette: QuizPalette,
    mark: Color,
    markBackground: Color,
) {
    val hits = question.highlight.optionLabels
    if (question.highlight.isJudge) {
        // 判断题：固定「对 / 错」两个按钮，**不画选项行**。
        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.spacedBy(QsTokens.spaceMd),
        ) {
            listOf("对", "错").forEach { label ->
                JudgeButton(
                    label = label,
                    hit = label in hits,
                    fontSize = fontSize,
                    mark = mark,
                    markBackground = markBackground,
                    palette = palette,
                )
            }
        }
        return
    }
    Column(modifier = Modifier.fillMaxWidth()) {
        question.options.forEach { option ->
            val hit = option.label in hits
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(bottom = 6.dp)
                    // 命中态：只做「浅绿底 + 文字变色 + 行尾 ✓」，**不画左侧竖条**
                    //（用户 2026-09-27 明确要求原生端不要竖条；我先前按 SPEC 文字加的竖条已移除）。
                    .clip(RoundedCornerShape(QsTokens.radiusControl))
                    .background(
                        color = if (hit) markBackground else Color.Transparent,
                        shape = RoundedCornerShape(QsTokens.radiusControl),
                    )
                    // 规格 §3.4.5 / SPEC.md:462-463：命中项是「文字变色 + 浅底 + **左侧 4px 竖条** + 行尾 ✓」，
                    // **不是整圈粗边**。这里曾把 4dp 当成整圈边框来画 —— 视觉上明显过粗、不如 1.1.0
                    // （用户 2026-09-27 反馈「安卓端题目选项边框太粗」）。现在：整圈只用 `hairline`（1dp），
                    // 命中才在**左侧**画 4dp 竖条（`strokeMarker`，与令牌语义一致）。
                    .border(
                        width = QsTokens.hairline,
                        color = if (hit) mark.copy(alpha = 0.45f) else palette.optionBar,
                        shape = RoundedCornerShape(QsTokens.radiusControl),
                    )
                    .padding(horizontal = 12.dp, vertical = 10.dp),
                verticalAlignment = Alignment.Top,
            ) {
                // 圈内字母：**圆圈与字母都在同一个"行高盒子"里居中**（用户 2026-09-28：
                // 「圆圈过高，要把圆圈居中，然后字母在圆圈里居中」）。
                // 几何与共用组件都在 `QsOptionBadge`；设置页的「题目字号预览」用的是同一个组件，
                // 所以这里看到的圈与预览里看到的圈永远一致。
                QsOptionBadge(
                    label = option.label,
                    fontSize = fontSize,
                    textColor = if (hit) mark else palette.questionText,
                    circleColor = if (hit) mark.copy(alpha = 0.16f) else palette.fill,
                )
                Spacer(Modifier.width(10.dp))
                // 1.1.0 口径的**例外①**：**选项内容默认不加粗**；用户 2026-09-27 追加：
                // **被选中的答案内容要加粗**。
                //
                // ⚠️ 这里原来只换 `fontFamily`（指望"换个字族就等于不加粗"），**字重没管** ——
                // `MathText` 快路径 `fontWeight = null` ⇒ 字重仍从 `LocalTextStyle`（主题的 `bodyLarge`
                // = SemiBold）继承，于是"不加粗的选项"其实是半粗的。现在**字重也显式给**：
                // 未命中 Normal、命中 Bold —— 与题干 Bold、解析 Normal 一起构成看得清的三级层次。
                MathText(
                    text = option.text,
                    fontSize = fontSize,
                    lineHeight = (fontSize * 1.5f).sp,
                    fontWeight = if (hit) FontWeight.Bold else FontWeight.Normal,
                    color = if (hit) mark else palette.questionText,
                    modifier = Modifier.weight(1f),
                )
                if (hit) {
                    Spacer(Modifier.width(QsTokens.spaceSm))
                    Icon(
                        Icons.Default.Check,
                        contentDescription = null,
                        tint = mark,
                        // 规格写的是 `size: fontSize`（在 Flutter 里字号与尺寸是同一个数字）；
                        // Compose 里尺寸要 `dp`、字号要 `sp`，所以这里用 `fontSize.dp`。
                        // **必须显式居中对齐**：这一行的 `verticalAlignment` 是 `Top`（为了多行题干时
                        // 圈号与首行对齐），不写 align 的话 ✓ 会贴着顶边 —— 用户 2026-09-27 反馈
                        // 「后面的对号也没对齐」。
                        modifier = Modifier.align(Alignment.CenterVertically).size(fontSize.dp),
                    )
                }
            }
        }
    }
}

/** 判断题按钮（§3.4.5）：命中 → 标色浅底 + 2px 标色边 + ✓；未命中 → 1px 灰边。 */
@Composable
private fun JudgeButton(
    label: String,
    hit: Boolean,
    fontSize: Int,
    mark: Color,
    markBackground: Color,
    palette: QuizPalette,
) {
    Row(
        modifier = Modifier
            .background(
                color = if (hit) markBackground else Color.Transparent,
                shape = RoundedCornerShape(QsTokens.radiusControl),
            )
            .border(
                width = if (hit) QsTokens.strokeEmphasis else QsTokens.hairline,
                color = if (hit) mark else palette.judgeBorder,
                shape = RoundedCornerShape(QsTokens.radiusControl),
            )
            .padding(horizontal = 26.dp, vertical = 10.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(
            text = label,
            fontSize = fontSize.sp,
            fontWeight = if (hit) FontWeight.Bold else null,
            color = if (hit) mark else palette.questionText,
        )
        if (hit) {
            Spacer(Modifier.width(6.dp))
            Icon(Icons.Default.Check, contentDescription = null, tint = mark, modifier = Modifier.size(QsTokens.iconSm))
        }
    }
}

// ============================================================================
// §3.4.6 答案区块 / §3.4.7 未识别出答案 / §3.4.8 解析 / §3.4.9 告警
// ============================================================================

@Composable
private fun AnswerBlock(
    question: QuestionItem,
    fontSize: Int,
    palette: QuizPalette,
    mark: Color,
    markBackground: Color,
) {
    Row(verticalAlignment = Alignment.CenterVertically) {
        Text(
            text = "答案",
            fontSize = (fontSize * 0.78f).sp,
            fontWeight = FontWeight.Bold,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
        if (question.answerGuessed) {
            Spacer(Modifier.width(6.dp))
            Text(
                text = "（AI 猜测）",
                fontSize = (fontSize * 0.78f).sp,
                fontWeight = FontWeight.Bold,
                color = palette.incomplete,
            )
        }
    }
    Spacer(Modifier.height(5.dp))
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .background(markBackground, RoundedCornerShape(QsTokens.radiusControl))
            // 标绿/标黄：文字变色 + 背景色 + 行尾 ✓；**不画左侧竖条**
            //（用户 2026-09-27 明确要求原生端不要竖条）。
            .border(QsTokens.hairline, mark.copy(alpha = 0.45f), RoundedCornerShape(QsTokens.radiusControl))
            .padding(12.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Column(modifier = Modifier.weight(1f)) {
            val text = question.answerText.orEmpty()
            if (!question.hasStructuredAnswer) {
                MathText(
                    text = text,
                    fontSize = fontSize,
                    lineHeight = (fontSize * 1.5f).sp,
                    fontWeight = FontWeight.SemiBold,
                    color = mark,
                )
            } else {
                // 结构化（多行）：行首无编号时补一个 5×5 的圆点（§3.4.6）。
                text.split('\n').filter { it.isNotBlank() }.forEachIndexed { index, line ->
                    Row(
                        modifier = Modifier.padding(top = if (index == 0) 0.dp else 6.dp),
                        verticalAlignment = Alignment.Top,
                    ) {
                        val trimmed = line.trim()
                        if (!hasNumbering(trimmed)) {
                            Box(
                                modifier = Modifier
                                    .padding(top = 6.dp)
                                    .size(QsTokens.statusDotSm)
                                    .background(mark, CircleShape),
                            )
                            Spacer(Modifier.width(QsTokens.spaceSm))
                        }
                        Text(
                            text = trimmed,
                            fontSize = fontSize.sp,
                            lineHeight = (fontSize * 1.5f).sp,
                            fontWeight = FontWeight.SemiBold,
                            color = mark,
                            modifier = Modifier.weight(1f),
                        )
                    }
                }
            }
        }
        Spacer(Modifier.width(QsTokens.spaceSm))
        Icon(
            imageVector = if (question.highlight.needsReview) Icons.AutoMirrored.Filled.HelpOutline else Icons.Default.CheckCircle,
            contentDescription = null,
            tint = mark,
            // 同上：规格的 `size: fontSize` 在 Compose 里是 `fontSize.dp`。
            modifier = Modifier.size(fontSize.dp),
        )
    }
}

/**
 * 行首编号判定（§3.4.6 的 `_numbering`，question_card.dart:541-542）：
 * `①-⑳` / `1.` / `1、` / `1)` / `（1）` / `一、` / `-` / `*` / `•` / `·`。
 */
private val kNumbering = Regex("^([①-⑳]|\\d+[.、)]|（\\d+）|[一二三四五六七八九十]+、|[-*•·])")

private fun hasNumbering(line: String): Boolean = kNumbering.containsMatchIn(line)

@Composable
private fun NoAnswerHint(fontSize: Int, palette: QuizPalette) {
    // 浅色：orange[50] 底 + orange[800] 前景；深色：orange[900]@0.22 底 + orange[200]（§3.4.7）。
    val foreground = if (palette.dark) Color(0xFFFFCC80) else Color(0xFFEF6C00)
    val background = if (palette.dark) Color(0x38E65100) else Color(0xFFFFF3E0)
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .background(background, RoundedCornerShape(QsTokens.radiusControl))
            .border(QsTokens.hairline, foreground.copy(alpha = 0.4f), RoundedCornerShape(QsTokens.radiusControl))
            .padding(12.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Icon(Icons.AutoMirrored.Filled.HelpOutline, contentDescription = null, tint = foreground, modifier = Modifier.size(QsTokens.iconBase))
        Spacer(Modifier.width(QsTokens.spaceSm))
        Text(text = "未识别出答案", fontSize = (fontSize * 0.9f).sp, color = foreground)
    }
}

@Composable
private fun AnalysisBlock(question: QuestionItem, fontSize: Int, palette: QuizPalette) {
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .background(palette.analysisBackground, RoundedCornerShape(QsTokens.radiusControl))
            .border(QsTokens.hairline, palette.analysisStroke, RoundedCornerShape(QsTokens.radiusControl))
            .padding(start = 12.dp, top = 10.dp, end = 12.dp, bottom = 12.dp),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Icon(
                Icons.Default.Lightbulb,
                contentDescription = null,
                tint = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.size(QsTokens.iconBase),
            )
            Spacer(Modifier.width(5.dp))
            Text(
                text = "解析",
                fontSize = (fontSize * 0.78f).sp,
                fontWeight = FontWeight.Bold,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
        Spacer(Modifier.height(6.dp))
        // 1.1.0 口径的**例外②**：**解析正文不加粗**（上面的「解析」小标题仍加粗 ✓）。
        // ⚠️ 光换 `fontFamily` **不够**：`MathText` 的快路径 `fontWeight = null` ⇒ 字重仍从
        // `LocalTextStyle` 继承，而主题把 `bodyLarge` 设成了 SemiBold —— 解析正文一直是半粗的
        //（用户 2026-09-28 当场指出「解析要正常粗细」）。所以这里**字重也一并还原成 Normal**。
        CompositionLocalProvider(
            LocalTextStyle provides LocalTextStyle.current.copy(
                fontFamily = FontFamily.Default,
                fontWeight = FontWeight.Normal,
            ),
        ) {
            MathText(
                text = question.analysis,
                fontSize = (fontSize * 0.92f).toInt(),
                lineHeight = (fontSize * 0.92f * 1.6f).sp,
                fontWeight = FontWeight.Normal,
                color = palette.questionText,
            )
        }
    }
}

@Composable
private fun Warnings(question: QuestionItem, fontSize: Int, palette: QuizPalette) {
    Column(modifier = Modifier.fillMaxWidth()) {
        question.warnings.forEach { warning ->
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(top = 6.dp)
                    .background(
                        palette.reviewBadgeBackground.copy(alpha = if (palette.dark) 0.16f else 0.55f),
                        RoundedCornerShape(QsTokens.radiusControl),
                    )
                    .border(
                QsTokens.hairline,
                        palette.reviewBadge.copy(alpha = 0.35f),
                        RoundedCornerShape(QsTokens.radiusControl),
                    )
                    .padding(horizontal = 10.dp, vertical = 8.dp),
                verticalAlignment = Alignment.Top,
            ) {
                Icon(
                    Icons.Default.Info,
                    contentDescription = null,
                    tint = palette.reviewBadge,
                    modifier = Modifier.size(QsTokens.iconBase).padding(top = 2.dp),
                )
                Spacer(Modifier.width(6.dp))
                Text(
                    text = warning,
                    fontSize = (fontSize * 0.82f).sp,
                    lineHeight = (fontSize * 0.82f * 1.5f).sp,
                    color = palette.warnText,
                    modifier = Modifier.weight(1f),
                )
            }
        }
    }
}

// ============================================================================
// §4.3 结果页浮层
// ============================================================================

/** 浮层内容：文案 + 是否转圈。 */
private data class HostBanner(val text: String, val spinning: Boolean)

/**
 * 结果页浮层的出现条件（providers.dart:362-379 / §4.3）：
 * * `showsHostProgress`：`task.running && task.hostInitiated`，且 `task.sessionId` 为空或 ≠ 当前 sessionId；
 * * `showsHostDone`：`task.finished && task.autoOpenResult && task.hostInitiated`，且 `task.sessionId` 非空且 ≠ 当前 sessionId。
 */
private fun hostBanner(task: ActiveTaskState, currentSessionId: String?): HostBanner? {
    if (!task.hostInitiated) return null
    val sameSession = !task.sessionId.isNullOrEmpty() && task.sessionId == currentSessionId
    if (task.running && !sameSession) {
        return HostBanner("电脑正在识别中…（${task.pages} 张图片）", spinning = true)
    }
    if (task.finished && task.autoOpenResult && !task.sessionId.isNullOrEmpty() && !sameSession) {
        return HostBanner("识别完成，正在打开新结果…", spinning = false)
    }
    return null
}
