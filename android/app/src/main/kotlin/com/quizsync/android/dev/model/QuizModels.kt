// 领域模型 + 规格里的**纯判定/格式化逻辑**（不依赖 Compose，可直接跑 JVM 单测）。
//
// 取值口径全部来自老应用共享包：
//  * `computeHighlight` ← `core-pkg/ai/highlight.dart:35-60`（ui-spec-android §3.5 逐字）；
//  * `matchedOptionLabels` ← `core-pkg/model/answer_value.dart:22-26`；
//  * 题型中文名表 ← `ui-pkg/src/theme_colors.dart:54-60`；
//  * `displayTitle` / `questionNoLabel` ← `core-pkg/model/question.dart:107-119`；
//  * 会话摘要文案 ← `app/lib/ui/session_tile.dart:117-144`。
package com.quizsync.android.dev.model

import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/** 题目选项：`label` 如 `A` / `对`，`text` 为选项内容。 */
data class OptionItem(val label: String, val text: String)

/** 题型。取值与 `ai-contract.md` 第 3 节 enum 逐字一致。 */
enum class QuestionType(val wire: String, val label: String) {
    Single("single", "单选题"),
    Multi("multi", "多选题"),
    Judge("judge", "判断题"),
    Blank("blank", "填空题"),
    Subjective("subjective", "主观题");

    /** 该题型必须有选项（单选 / 多选 / 判断）。 */
    val requiresOptions: Boolean
        get() = this == Single || this == Multi || this == Judge

    companion object {
        fun parse(raw: String?): QuestionType =
            entries.firstOrNull { it.wire == raw } ?: Subjective

        fun isKnown(raw: String?): Boolean = entries.any { it.wire == raw }

        /** 题型药丸文案：认识的用中文名，不认识的原样显示（§3.4.3 第 3 项）。 */
        fun labelOf(raw: String?): String =
            entries.firstOrNull { it.wire == raw }?.label ?: (raw ?: "")
    }
}

/**
 * 一道题（UI 只读这一份）。
 *
 * 派生量（`displayTitle` / `hasMaterial` / …）在**构造时算一次**，不在组合期算
 * （用户要求：组合期不做重活）。
 */
data class QuestionItem(
    val id: String,
    val sessionId: String,
    val ordinal: Int,
    val questionNo: String?,
    val stem: String,
    val material: String,
    val type: QuestionType,
    val typeWire: String,
    val options: List<OptionItem>,
    /** `answer.choice`，已剔除不在 options 里的非法 label。 */
    val choice: List<String>,
    val answerText: String?,
    val analysis: String,
    val confidence: Double,
    val needReview: Boolean,
    val answerInImage: Boolean,
    val incomplete: Boolean,
    val answerGuessed: Boolean,
    val warnings: List<String>,
    val answerEdited: Boolean,
    val analysisEdited: Boolean,
    val updatedAt: Long,
) {
    /** 识别到的题号文本：`第 12 题`；无题号则 null（question.dart:108-112）。 */
    val questionNoLabel: String? = questionNo?.trim()?.takeIf { it.isNotEmpty() }?.let { "第 $it 题" }

    /** 卡片标题：`3. 第 12 题`；无题号时为 `3.`（question.dart:116-119）。 */
    val displayTitle: String =
        if (questionNoLabel == null) "${ordinal + 1}." else "${ordinal + 1}. $questionNoLabel"

    /** 会话「识别完成」卡片里的答案摘要（current_task_page.dart:442-474）。 */
    val answerBrief: String =
        if (choice.isNotEmpty()) choice.joinToString("") else (answerText ?: "—")

    val hasMaterial: Boolean = material.trim().isNotEmpty()

    /** 主观/填空答案是否是结构化多行（question.dart:128-132）。 */
    val hasStructuredAnswer: Boolean = run {
        val text = answerText ?: ""
        text.contains('\n') && text.split('\n').count { it.trim().isNotEmpty() } > 1
    }

    val highlight: Highlight = computeHighlight(this)
}

/** 标绿结论（`HighlightResult`）。 */
data class Highlight(
    /** 需要标绿的选项 label 集合。 */
    val optionLabels: Set<String> = emptySet(),
    /** 填空 / 主观：`answer.text` 在「答案」区块用彩色文字 + 浅底显示。 */
    val highlightAnswerText: Boolean = false,
    /** 无答案可标：显示「未识别出答案」并标记需复核。 */
    val noAnswer: Boolean = false,
    /** 黄色徽标「AI 不确定，建议复核」（confidence < 0.6 或 need_review）。 */
    val needsReview: Boolean = false,
    /** 题型为判断（UI 用「对 / 错」两个按钮呈现）。 */
    val isJudge: Boolean = false,
)

/**
 * `ai-contract.md` 第 5 节伪码的逐字实现（`highlight.dart:35-60`）。
 *
 * 注意第 ① 支里「命中为空」要把 `needsReview` **强制**改成 true，
 * 第 ③ 支同理 —— 这是规格里写死的，别「顺手优化」掉。
 */
fun computeHighlight(q: QuestionItem): Highlight {
    val needsReview = q.confidence < 0.6 || q.needReview
    val isJudge = q.type == QuestionType.Judge

    if (q.choice.isNotEmpty() && q.options.isNotEmpty()) {
        val labels = q.options.mapTo(HashSet()) { it.label }
        val hits = q.choice.filter { it in labels }.toSet()
        if (hits.isNotEmpty()) {
            return Highlight(optionLabels = hits, needsReview = needsReview, isJudge = isJudge)
        }
        return Highlight(noAnswer = true, needsReview = true, isJudge = isJudge)
    }
    if (!q.answerText.isNullOrEmpty()) {
        return Highlight(highlightAnswerText = true, needsReview = needsReview, isJudge = isJudge)
    }
    return Highlight(noAnswer = true, needsReview = true, isJudge = isJudge)
}

/** 会话状态（`status` 列的取值）。 */
enum class SessionStatus(val wire: String, val label: String) {
    Queued("queued", "排队中"),
    Analyzing("analyzing", "识别中"),
    Done("done", "已完成"),
    Failed("failed", "失败"),
    Cancelled("cancelled", "已取消");

    companion object {
        fun parse(raw: String?): SessionStatus? = entries.firstOrNull { it.wire == raw }

        /** 未知状态原样显示（widgets.dart:206）。 */
        fun labelOf(raw: String?): String = parse(raw)?.label ?: (raw ?: "")
    }
}

/** 历史列表 / 合集详情里的一条会话。 */
data class SessionItem(
    val sessionId: String,
    val collectionId: String?,
    val collectionName: String?,
    val status: String,
    val errorCode: String?,
    val errorMessage: String?,
    val questionCount: Int,
    val imageCount: Int,
    val aiModel: String?,
    val sourceDevice: String,
    val createdAt: Long,
) {
    /** 失败码 → 中文（session_tile.dart:129-138）。 */
    val errorLabel: String? = errorCode?.let { errorLabelOf(it) }

    val headline: String = headlineOf(this)
    val timeLabel: String = formatMinute(createdAt)
    val statusLabel: String = SessionStatus.labelOf(status)

    /** 条目副标题：`{time} · {status}{来自电脑}`（session_tile.dart:100-104）。 */
    val subtitle: String =
        buildString {
            append(timeLabel).append(" · ").append(statusLabel)
            if (sourceDevice != "android-local") append(" · 来自电脑")
        }

    /** 失败条目的标题用状态色（session_tile.dart:95-98）。 */
    val headlineUsesStatusColor: Boolean = status == SessionStatus.Failed.wire
}

/** 历史摘要文案（session_tile.dart:117-127，逐字）。 */
fun headlineOf(session: SessionItem): String {
    if (session.status == SessionStatus.Failed.wire) {
        return session.errorMessage ?: session.errorLabel ?: "分析失败"
    }
    return when (session.status) {
        SessionStatus.Analyzing.wire -> "正在识别…"
        SessionStatus.Queued.wire -> "等待电脑上线后自动分析"
        SessionStatus.Cancelled.wire -> "已取消"
        else -> if (session.questionCount == 0) "未识别到题目" else "识别出 ${session.questionCount} 道题"
    }
}

/** 失败码 → 中文（session_tile.dart:129-138，逐字）。 */
fun errorLabelOf(code: String): String = when (code) {
    "ai_timeout" -> "AI 超时，可重试"
    "ai_auth" -> "主机 API Key 无效"
    "ai_rate_limited" -> "触发限流，稍后重试"
    "ai_bad_response" -> "AI 返回无法解析"
    "ai_quota_exceeded" -> "主机今日调用已达上限"
    "no_question_found" -> "未识别到题目"
    "no_active_collection" -> "主机未选择任务合集"
    else -> "分析失败（$code）"
}

/** 时间标签格式：`MM-dd HH:mm`（session_tile.dart:140-144）。 */
fun formatMinute(epochMs: Long): String =
    SimpleDateFormat("MM-dd HH:mm", Locale.getDefault()).format(Date(epochMs))

/** 题目卡片里的时间戳：`{MM}-{dd} {HH}:{mm}`（question_card.dart:262）。 */
fun formatCardStamp(epochMs: Long): String =
    SimpleDateFormat("MM-dd HH:mm", Locale.getDefault()).format(Date(epochMs))

/** 历史分组（合集）：`collectionId == null` = 未分类。 */
data class CollectionGroup(
    /** `null` = 未分类（老数据没有合集）。 */
    val collectionId: String?,
    val title: String,
    val count: Int,
) {
    val isUnclassified: Boolean get() = collectionId == null

    /** 多选删除里用的稳定 key（history_page.dart:412 的 `group-…`）。 */
    val key: String get() = "group-${collectionId ?: "unclassified"}"
}

/**
 * 历史页多选模式的**纯逻辑**（可 JVM 单测）：长按进多选后，点一下是「选中 / 取消选中」。
 *
 * 为什么单独抽出来测：这里有三条容易写错的规则 ——
 * ① **「未分类」不能删**（它不是真实合集，删了没有对象）→ 不进选择集，当前状态原样返回；
 * ② 取消最后一个选中项时**退出多选模式**（返回 `null`）—— 否则会停在「在多选、但一个都没选」
 *    那种界面上（删除按钮灰着），用户会以为界面坏了；
 * ③ `null` 与**空集**是两种状态：`null` = 不在多选，空集 = 在多选但没选。
 */
fun qsToggleCollectionSelection(current: Set<String>?, group: CollectionGroup): Set<String>? {
    val id = group.collectionId ?: return current
    if (group.isUnclassified) return current

    val selected = (current ?: emptySet()).toMutableSet()
    if (!selected.add(id)) selected.remove(id)
    return selected.ifEmpty { null }
}

/** 多选模式下的计数文案（判据与界面文案同源，避免两处各写一遍）。 */
fun qsSelectionLabel(count: Int): String = "已选 $count 个合集"
