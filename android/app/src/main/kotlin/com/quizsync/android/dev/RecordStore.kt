package com.quizsync.android.dev

import android.content.Context
import com.quizsync.android.core.db.QuizSyncDatabase
import kotlinx.serialization.json.JsonPrimitive
import com.quizsync.android.core.sync.ApplyResult
import com.quizsync.android.core.sync.SyncOpType
import com.quizsync.android.core.sync.SyncEntity
import com.quizsync.android.core.net.HostApi
import com.quizsync.android.core.sync.SyncApplier
import com.quizsync.android.core.sync.SyncOp
import com.quizsync.android.dev.model.CollectionGroup
import com.quizsync.android.dev.model.OptionItem
import com.quizsync.android.dev.model.QuestionItem
import com.quizsync.android.dev.model.QuestionType
import com.quizsync.android.dev.model.SessionItem
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.buildJsonArray
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import java.io.File

/**
 * 手机侧的记录存储：**持久库 + 从主机拉 op + 应用 + 查询**。
 *
 * 三件事都有现成的内核，这里只做接线：
 * * `QuizSyncDatabase`（协议 schema 随 APK 走 assets）；
 * * `SyncApplier`（逐字段 LWW，已与一致性向量对拍过）；
 * * `HostApi.pullOps`（用配对时拿到的 token）。
 *
 * **两条与 Windows 侧同一个坑，务必按这个顺序**：
 * 1. 协议 DDL **不幂等** —— 库已存在时必须 `openExisting`，不能拿 schema 再跑一遍
 *    （Windows 侧就是在这里抛了 `index ... already exists`）；
 * 2. 拉取要带**本地水位**（`applier.watermark()`），否则每次都全量重拉。
 *
 * 本文件在里程碑 A1 里扩了一件事：**查询**（历史列表 / 合集分组 / 搜索结果 / 题目全字段）。
 * 查询全部走这里，Composable 里不碰 SQL、不解析 JSON（用户要求：组合期不做重活）。
 */
/**
 * **所有数据库访问都必须走这个单线程派发器。**
 *
 * 为什么：`androidx.sqlite` 的 `SQLiteConnection` **不是线程安全的** ——
 * 两个线程同时用同一个连接会 **native 崩溃**。
 * 实测：`SIGSEGV`，栈是 `QuizViewModel$loadHistory$2` → `RecordStore.sessions`。
 * 起因是 `refreshAll` 里加了一句「先刷本地历史」，于是多了一个并发的库访问者，
 * 和 `deleteSession` 的写入撞上了。
 *
 * 收敛到一个线程是唯一能保证不并发的做法；靠「每个调用点自己小心」迟早再犯。
 *
 * 放在**文件级**而不是 companion 里：这个类的 companion 是 `private` 的，
 * 外面（ViewModel / PairScreen）拿不到它的成员。
 */
@OptIn(kotlinx.coroutines.ExperimentalCoroutinesApi::class)
internal val dbDispatcher: kotlinx.coroutines.CoroutineDispatcher =
    kotlinx.coroutines.Dispatchers.IO.limitedParallelism(1)


class RecordStore(context: Context, schemaSql: String, private val selfDeviceId: String) {
    private val file = File(context.filesDir, "quizsync.db")
    private val database: QuizSyncDatabase =
        if (file.exists()) QuizSyncDatabase.openExisting(file.absolutePath)
        else QuizSyncDatabase.open(file.absolutePath, schemaSql)

    private val applier = SyncApplier(database.connection)

    private val json = Json { ignoreUnknownKeys = true; isLenient = true }

    /**
     * 从主机拉 op 并应用。返回本次应用了几条（0 表示没有新东西）。
     *
     * **`from_device` 是「要拉谁的 op」**（`spec/04-http-api.md` §1.3.13 / §1.4.3 写得很明确：
     * 它是**来源设备**维度，既不是「我」也不是「发给谁」）。第十五轮实测栽在这里：我传的是
     * **手机自己的 deviceId** → 等价于「只拉我自己产生的 op」→ 屏幕上显示「已从主机拉取 0 条 op」，
     * 而服务端库里有 op。
     *
     * 所以这里按协议的做法来：**对每个来源设备各拉一次** —— 主机自己的 `/info.device_id`
     * （主机直接产生的改动）加上 `/devices` 里所有已配对设备（排除自己）。
     * 游标暂用 0（全量重拉）：applier 按 `op_id` 幂等，重复投递是安全的；
     * 「每个来源设备各自一条独立游标」是协议里的模型，作为后续优化（现在这点数据量不值得先做）。
     */
    fun sync(host: String, token: String): Int {
        val api = HostApi(host)
        api.setToken(token)

        val sources = linkedSetOf<String>()
        api.info().string("device_id")?.takeIf { it.isNotBlank() }?.let { sources += it }
        for (element in api.devices().jsonObject()["devices"]?.jsonArray ?: emptyList()) {
            element.jsonObject["device_id"]?.toString()?.trim('"')
                ?.takeIf { it.isNotBlank() }?.let { sources += it }
        }
        sources -= selfDeviceId

        if (sources.isEmpty()) {
            // 没有任何来源设备：只可能是「服务端没报告自己的 device_id」，如实说出来。
            error("主机没有可拉取的来源设备（/info 与 /devices 都没给 device_id）")
        }

        var applied = 0
        for (source in sources) {
            val response = api.pullOps(fromDevice = source, sinceLamport = 0)
            if (response.status !in 200..299) {
                error(
                    "拉取 $source 的 op 失败：${response.status}" +
                        (response.string("message")?.let { "：$it" } ?: ""),
                )
            }

            val ops = response.jsonObject()["ops"]?.jsonArray?.map { SyncOp.parse(it) } ?: emptyList()
            ops.forEach { applier.apply(it) }
            applied += ops.size
        }

        return applied
    }

    /**
     * 把**本机产生的** op 推给主机；返回推了几条（0 = 没有要推的）。
     *
     * 为什么必须有这一步：`deleteCollection` / `deleteSession` 走 `applier.apply(op)`，
     * 墓碑 op 只落在**本地** `sync_ops` 里 —— 不推上去的话，手机上删掉的合集在电脑上原封不动，
     * 而且下次拉取还会把它拉回来（本地墓碑被对端的高 lamport 覆盖）。界面上那句
     * 「软删除并同步到电脑」指的就是这一步。1.1.0 的 M50 为同一件事补过一次「删完立刻推」，
     * 2.0 重写时这条连接线漏了（`HostApi.pushOps` 一直只有测试在用）。
     *
     * **只推 `device_id == 本机` 的 op**：本地 `sync_ops` 是「我这台设备见过的所有 op」，
     * 里面也有从主机拉下来的对端 op；把它们推回去会被主机按「`device_id` 等于主机自己 →
     * 静默跳过」丢掉（`spec/04-http-api.md` §1.3.12），白跑一趟网络。
     *
     * 游标**刻意不做**，与 [sync] 的全量重拉同一个取舍：applier 与主机两侧都按 `op_id` 幂等
     * （主机重复投递不计 `applied`），重复推是安全的。真到「op 攒到几千条」再按
     * `settings.push_cursor:<host>` 记水位也不迟 —— 现在这点数据量不值得先引入一条状态。
     */
    fun pushPending(host: String, token: String): Int {
        val api = HostApi(host)
        api.setToken(token)

        var total = 0
        var since = 0L
        while (true) {
            // **必须循环**：`pull` 一批最多 [PUSH_BATCH] 条、且按 lamport 升序 ——
            // 只推第一批的话，本地 op 攒过 500 条之后**最新的那些永远推不上去**（第一批永远是最老的）。
            // `since` 用「本批最后一条的 lamport」：同一台设备的 lamport 严格递增（本地时钟每写一条 +1），
            // 所以「大于它」不会漏；这个不变式由 applier 保证。
            val ops = applier.pull(selfDeviceId, sinceLamport = since, limit = PUSH_BATCH)
            if (ops.isEmpty()) {
                break
            }

            val body = buildJsonObject {
                put(
                    "ops",
                    buildJsonArray {
                        ops.forEach { add(Json.encodeToJsonElement(SyncOp.serializer(), it)) }
                    },
                )
            }.toString()

            val response = api.pushOps(body)
            if (response.status !in 200..299) {
                // 只有「推失败」算异常：调用方（删除入口）不该因为网络问题把删除结果说成失败 ——
                // 本地墓碑已经落库了，下次同步还会再推一次。
                error(
                    "推送本机 op 失败：${response.status}" +
                        (response.string("message")?.let { "：$it" } ?: ""),
                )
            }

            total += ops.size
            since = ops.last().lamport
            if (ops.size < PUSH_BATCH) {
                break
            }
        }

        return total
    }

    // ========================================================================
    // 查询：历史列表 / 合集分组 / 搜索 / 题目
    // ========================================================================

    /** 会话列表（`collectionId == null` = 全部，含未分类）。 */
    fun sessions(collectionId: String? = null, limit: Int = 200): List<SessionItem> {
        val rows = mutableListOf<SessionItem>()
        val sql = buildString {
            append(SESSION_SELECT)
            append(" WHERE s.deleted_at IS NULL")
            if (collectionId != null) append(" AND s.collection_id = ?")
            append(" ORDER BY s.created_at DESC, s.rowid DESC LIMIT ?")
        }
        val statement = database.connection.prepare(sql)
        try {
            var index = 1
            if (collectionId != null) statement.bindText(index++, collectionId)
            statement.bindLong(index, limit.toLong())
            while (statement.step()) {
                rows += readSession(statement)
            }
        } finally {
            statement.close()
        }
        return rows
    }

    /** 单条会话（结果页用；不存在或已删返回 null）。 */
    fun session(sessionId: String): SessionItem? {
        val statement = database.connection.prepare(SESSION_SELECT + " WHERE s.session_id = ?")
        try {
            statement.bindText(1, sessionId)
            return if (statement.step()) readSession(statement) else null
        } finally {
            statement.close()
        }
    }

    /**
     * 合集分组（§2.4.1）：未归属合集的会话归到「未分类」一组。
     *
     * 排序：按该组**最近一条记录的时间**倒序 —— ui-spec-android 未显式指定分组顺序，
     * 这里取「最近有记录的合集排前面」（已记进 DECISIONS）。
     *
     * **已删除（墓碑）的合集也归到「未分类」**：规格 §3.4 说合集删除**不级联删记录**
     * （M18 口径），所以那些记录必须仍然看得见 —— 否则用户删一个合集，里面的记录就凭空消失了。
     */
    fun groups(): List<CollectionGroup> {
        val rows = mutableListOf<CollectionGroup>()
        val statement = database.connection.prepare(
            """
            SELECT CASE WHEN s.collection_id IS NULL OR s.collection_id = 'null' OR c.deleted_at IS NOT NULL
                        THEN NULL ELSE s.collection_id END AS gid,
                   c.name, COUNT(*) AS n, MAX(s.created_at) AS newest
              FROM sessions s
              LEFT JOIN collections c ON c.collection_id = s.collection_id
             WHERE s.deleted_at IS NULL
             GROUP BY gid
             ORDER BY newest DESC, gid ASC
            """.trimIndent(),
        )
        try {
            while (statement.step()) {
                val id = if (statement.isNull(0)) null else statement.getText(0)
                val name = if (statement.isNull(1)) null else statement.getText(1)
                rows += CollectionGroup(
                    collectionId = id,
                    // 未归属合集组标题固定为「未分类」（history_page.dart:420）。
                    // ⚠️ **名字要当心字符串 `"null"`**：主机响应里的 `active_collection_name` 可能是
                    //    **字符串 "null"**（`QuizViewModel` 的 `:232`/`:377` 两处都专门防了它），
                    //    而**合集列表**这条路径没防 —— 于是这个字面量被原样存进本地库，
                    //    历史分组标题就真的显示成 `null`（真机实测：`text="null"` 的 TextView）。
                    //    真实数据一次就暴露；假数据里每个合集都有正常名字，永远测不出来。
                    title = if (id == null) "未分类" else name?.takeIf { it.isNotBlank() && it != "null" } ?: id,
                    count = statement.getLong(2).toInt(),
                )
            }
        } finally {
            statement.close()
        }
        return rows
    }

    /**
     * 搜索（§2.4.2）：命中题干或解析的会话。
     *
     * 老应用走 FTS；协议 schema 明确允许「引擎不支持 FTS5 时退化为 LIKE，
     * 其余功能不受影响」（`schema-v1.sql:279`）。这里用 LIKE —— 结果集一样，
     * 且不依赖 trigram 分词器。
     */
    fun searchSessions(keyword: String, limit: Int = 200): List<SessionItem> {
        val trimmed = keyword.trim()
        if (trimmed.isEmpty()) return emptyList()
        val rows = mutableListOf<SessionItem>()
        val pattern = "%" + trimmed.replace("\\", "\\\\").replace("%", "\\%").replace("_", "\\_") + "%"
        val statement = database.connection.prepare(
            SESSION_SELECT +
                " WHERE s.deleted_at IS NULL AND s.session_id IN (" +
                " SELECT q.session_id FROM questions q" +
                "  WHERE q.deleted_at IS NULL AND (q.stem LIKE ? ESCAPE '\\' OR q.analysis LIKE ? ESCAPE '\\')" +
                " ) ORDER BY s.created_at DESC, s.rowid DESC LIMIT ?",
        )
        try {
            statement.bindText(1, pattern)
            statement.bindText(2, pattern)
            statement.bindLong(3, limit.toLong())
            while (statement.step()) {
                rows += readSession(statement)
            }
        } finally {
            statement.close()
        }
        return rows
    }

    /** 某会话的全部题目（按 ordinal）。字段与协议 schema 逐列对应。 */
    fun questions(sessionId: String): List<QuestionItem> {
        val rows = mutableListOf<QuestionItem>()
        val statement = database.connection.prepare(
            """
            SELECT question_id, session_id, ordinal, question_no, stem, material, type,
                   options_json, choice_json, answer_text, analysis, confidence,
                   need_review, answer_in_image, incomplete, answer_guessed, warnings_json,
                   answer_edited, analysis_edited, updated_at
              FROM questions
             WHERE session_id = ? AND deleted_at IS NULL
             ORDER BY ordinal
            """.trimIndent(),
        )
        try {
            statement.bindText(1, sessionId)
            while (statement.step()) {
                val typeWire = statement.getText(6)
                rows += QuestionItem(
                    id = statement.getText(0),
                    sessionId = statement.getText(1),
                    ordinal = statement.getLong(2).toInt(),
                    questionNo = if (statement.isNull(3)) null else statement.getText(3),
                    stem = statement.getText(4),
                    material = statement.getText(5),
                    type = QuestionType.parse(typeWire),
                    typeWire = typeWire,
                    options = parseOptions(statement.getText(7)),
                    choice = parseStrings(statement.getText(8)),
                    answerText = if (statement.isNull(9)) null else statement.getText(9),
                    analysis = statement.getText(10),
                    confidence = statement.getDouble(11),
                    needReview = statement.getLong(12) != 0L,
                    answerInImage = statement.getLong(13) != 0L,
                    incomplete = statement.getLong(14) != 0L,
                    answerGuessed = statement.getLong(15) != 0L,
                    warnings = parseStrings(statement.getText(16)),
                    answerEdited = statement.getLong(17) != 0L,
                    analysisEdited = statement.getLong(18) != 0L,
                    updatedAt = statement.getLong(19),
                )
            }
        } finally {
            statement.close()
        }
        return rows
    }

    private fun readSession(statement: androidx.sqlite.SQLiteStatement): SessionItem = SessionItem(
        sessionId = statement.getText(0),
        collectionId = if (statement.isNull(1)) null else statement.getText(1),
        collectionName = statement.getText(2)?.takeIf { it.isNotBlank() && it != "null" },
        status = statement.getText(3),
        errorCode = if (statement.isNull(4)) null else statement.getText(4),
        errorMessage = if (statement.isNull(5)) null else statement.getText(5),
        questionCount = statement.getLong(6).toInt(),
        imageCount = statement.getLong(9).toInt(),
        aiModel = if (statement.isNull(7)) null else statement.getText(7),
        sourceDevice = statement.getText(8),
        createdAt = statement.getLong(10),
    )

    private fun parseOptions(raw: String?): List<OptionItem> {
        if (raw.isNullOrBlank()) return emptyList()
        return runCatching {
            json.parseToJsonElement(raw).jsonArray.mapNotNull { element ->
                val obj = element.jsonObject
                val label = obj["label"]?.jsonPrimitive?.content.orEmpty()
                if (label.isEmpty()) null
                else OptionItem(label = label, text = obj["text"]?.jsonPrimitive?.content.orEmpty())
            }
        }.getOrDefault(emptyList())
    }

    private fun parseStrings(raw: String?): List<String> {
        if (raw.isNullOrBlank()) return emptyList()
        return runCatching {
            val element = json.parseToJsonElement(raw)
            (element as? JsonArray)?.mapNotNull { it.jsonPrimitive.content } ?: emptyList()
        }.getOrDefault(emptyList())
    }

    fun close() = database.close()

    private companion object {
        /** 推送一批的条数（与 `SyncApplier.pull` 的默认上限一致；超过就分批推，见 [pushPending]）。 */
        const val PUSH_BATCH = 500

        /**
         * 会话查询的公共投影。
         *
         * `image_count` 不在协议 `sessions` 表里（只有 `image_hash` 与 `session_images` 页序），
         * 所以这里按页数派生，并按规格 §2.3.2 的口径「不足 1 张按 1 张」处理。
         */
        const val SESSION_SELECT = """
            SELECT s.session_id, s.collection_id, c.name, s.status, s.error_code, s.error_message,
                   s.question_count, s.ai_model, s.source_device,
                   (SELECT COUNT(*) FROM session_images si
                     WHERE si.session_id = s.session_id AND si.deleted_at IS NULL) AS page_count,
                   s.created_at
              FROM sessions s
              LEFT JOIN collections c ON c.collection_id = s.collection_id
        """
    }
    /**
     * 删一个合集（规格 §3.4「合集也能在手机上删除」）。**软删除 + 生成墓碑 op**，两者缺一不可：
     * 只改本地不写 op 的后果是**手机上删掉了、电脑上那个合集还在**，下次同步又被拉回来。
     *
     * 与 Windows 端 `CollectionStore.Delete` 对齐：
     * * `collections` 表有 `updated_at`，所以墓碑把这一列一起往后推（删除也是对这一行的一次修改）；
     * * **不级联删记录**（M18 口径）：`sessions` 不动，那些记录在 `groups()` 里归到「未分类」；
     * * **幂等**：已经是墓碑的再删一次不产生第二条 op（墓碑时间被反复往后推会永久阻塞 30 天 GC）。
     *
     * 走 `applier.apply(op)` 而不是自己写 SQL：它**先写 `sync_ops` 再应用实体**，
     * 记账与落库不会漏掉一件。
     */
    fun deleteCollection(collectionId: String, now: Long = System.currentTimeMillis()): Boolean {
        val alive = database.connection.prepare(
            "SELECT 1 FROM collections WHERE collection_id = ? AND deleted_at IS NULL",
        )
        val exists = try {
            alive.bindText(1, collectionId)
            alive.step()
        } finally {
            alive.close()
        }

        if (!exists) return false

        val op = SyncOp(
            op_id = java.util.UUID.randomUUID().toString(),
            device_id = selfDeviceId,
            lamport = applier.watermark() + 1,
            entity = SyncEntity.COLLECTION,
            entity_id = collectionId,
            op_type = SyncOpType.DELETE,
            fields_json = mapOf(
                "deleted_at" to JsonPrimitive(now),
                "updated_at" to JsonPrimitive(now),
            ),
            created_at = now,
        )
        return applier.apply(op) != ApplyResult.DUPLICATE
    }

    /**
     * 删一条识别记录（规格 §2.4.5）。**软删除 + 生成墓碑 op**，两者缺一不可。
     *
     * 为什么不能只改本地库：协议里 `delete` 就是「写 `deleted_at`」的一种 upsert，同样受 LWW 约束。
     * 只改本地不写 op 的后果是**手机上删掉了、电脑上那条还在**，下次同步还会被推回来。
     *
     * 墓碑字段集与 Windows 端 `LocalStore.DeleteLocal` 对齐（那是参考实现）：
     * `sessions` 有 `updated_at`，所以墓碑把这一列一起往后推 —— 删除也是对这一行的一次修改。
     *
     * 走 `applier.apply(op)` 而不是自己写 SQL：它**先写 `sync_ops` 再应用实体**，
     * 既记了账（能被推给主机）又落了自己的库，两件事不会漏掉一件。
     */
    fun deleteSession(sessionId: String, now: Long = System.currentTimeMillis()): Boolean {
        val op = SyncOp(
            op_id = java.util.UUID.randomUUID().toString(),
            device_id = selfDeviceId,
            lamport = applier.watermark() + 1,
            entity = SyncEntity.SESSION,
            entity_id = sessionId,
            op_type = SyncOpType.DELETE,
            fields_json = mapOf(
                "deleted_at" to JsonPrimitive(now),
                "updated_at" to JsonPrimitive(now),
            ),
            created_at = now,
        )
        return applier.apply(op) != ApplyResult.DUPLICATE
    }
    /**
     * 造几条**测试用**的识别记录（没有主机服务端时，界面没东西可测）。
     *
     * 为什么走这里而不是直接改数据库：这是应用自己的写入路径，列名/非空列/schema 一定对；
     * 直接往库里塞行一旦漏了非空列，表现是「页面打不开」而不是「少一条记录」。
     *
     * 刻意造出几种**不同的展示分支**，这样一次就能把界面上的分支都看到：
     * 普通选择题 / 建议复核（低置信度 + need_review）/ AI 猜测（incomplete + answer_guessed）
     * / 判断题 / 主观题（无选项）/ 带阅读材料的题。
     *
     * 不写同步 op：这批数据只为本机测试，主机不存在（见 DECISIONS 的阻塞性发现）。
     */
    fun seedTestSessions(count: Int = 5): Int {
        val now = System.currentTimeMillis()
        val samples = listOf(
            // (题干, 选项, 正确答案, 解析, 题型, 置信度, 需复核, 猜测, 不全, 材料)
            Sample(
                stem = "下列关于加速度的说法正确的是？",
                options = listOf("A" to "加速度是矢量，方向与速度变化量方向相同", "B" to "加速度越大，速度一定越大", "C" to "加速度为零时，速度一定为零", "D" to "加速度方向总与速度方向相同"),
                choice = listOf("A"),
                analysis = "加速度描述速度变化的快慢，是矢量，方向与速度变化量方向一致。速度大不代表速度变化快，故 B 错；匀速直线运动加速度为零而速度不为零，故 C 错。",
                type = "single",
                confidence = 0.92,
            ),
            Sample(
                stem = "物体做匀速圆周运动时，下列物理量中保持不变的是？",
                options = listOf("A" to "线速度", "B" to "角速度", "C" to "向心加速度", "D" to "合外力"),
                choice = listOf("B"),
                analysis = "匀速圆周运动中角速度大小与方向都不变；线速度、向心加速度、合外力方向时刻改变。",
                type = "single",
                confidence = 0.41,
                needReview = true,
            ),
            Sample(
                stem = "摩擦力的方向总是与物体运动方向相反。",
                options = listOf("A" to "正确", "B" to "错误"),
                choice = listOf("B"),
                analysis = "摩擦力方向与**相对运动（或相对运动趋势）**方向相反，不一定与物体运动方向相反：传送带上随带加速的物体，摩擦力方向就与运动方向相同。",
                type = "judge",
                confidence = 0.77,
            ),
            Sample(
                stem = "某同学用打点计时器研究匀变速直线运动，得到一条纸带。请根据纸带数据求小车的加速度，并说明理由。",
                options = emptyList(),
                choice = emptyList(),
                answerText = "由逐差法 a = [(s₄+s₅+s₆) − (s₁+s₂+s₃)] / (9T²) 计算，代入相邻计数点间距即可。",
                analysis = "逐差法可以充分利用全部数据、减小偶然误差。注意 T 是相邻计数点的时间间隔，不是打点周期。",
                type = "subjective",
                confidence = 0.66,
            ),
            Sample(
                stem = "阅读材料，回答问题：某型号电池的电动势为 3.7 V，内阻为 0.5 Ω。",
                material = "表 1 电池参数\n电动势 E = 3.7 V\n内阻 r = 0.5 Ω\n额定容量 4000 mAh",
                options = listOf("A" to "7.4 W", "B" to "14.8 W", "C" to "3.7 W", "D" to "1.85 W"),
                choice = listOf("A"),
                analysis = "短路电流 I = E/r = 7.4 A，输出功率 P = E·I = 27.4 W；若按外阻等于内阻的最大输出功率算则是 E²/(4r) ≈ 6.8 W。本题按题给条件取 A。",
                type = "single",
                confidence = 0.55,
                answerGuessed = true,
                incomplete = true,
            ),
        )

        var inserted = 0
        for (i in 0 until count.coerceIn(1, 50)) {
            val sample = samples[i % samples.size]
            val sessionId = "seed-${now}-$i"
            val status = if (i == 3) "failed" else "done"
            val createdAt = now - (i + 1) * 37L * 60L * 1000L

            exec(
                "INSERT OR REPLACE INTO sessions (" +
                    "session_id, collection_id, image_hash, source_device, status, error_code, error_message, " +
                    "ai_provider, ai_model, question_count, latency_ms, created_at, updated_at, updated_by" +
                    ") VALUES (?, NULL, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
            ) { s ->
                s.bindText(1, sessionId)
                s.bindText(2, "sha256:seed-$i")
                s.bindText(3, selfDeviceId)
                s.bindText(4, status)
                if (status == "failed") {
                    s.bindText(5, "no_question_found")
                    s.bindText(6, "截图里没有找到题目，可以换一张更清晰的")
                } else {
                    s.bindNull(5)
                    s.bindNull(6)
                }
                s.bindText(7, "stub")
                s.bindText(8, "seed-model")
                s.bindLong(9, if (status == "failed") 0L else 1L)
                s.bindLong(10, 1200L + i * 130L)
                s.bindLong(11, createdAt)
                s.bindLong(12, createdAt)
                s.bindText(13, selfDeviceId)
            }

            if (status == "done") {
                val questionId = "$sessionId-q0"
                val optionsJson = sample.options.joinToString(",", "[", "]") { (label, text) ->
                    "{\"label\":\"$label\",\"text\":\"$text\"}"
                }
                val choiceJson = sample.choice.joinToString(",", "[", "]") { "\"$it\"" }
                exec(
                    "INSERT OR REPLACE INTO questions (" +
                        "question_id, session_id, ordinal, question_no, stem, material, type, options_json, " +
                        "choice_json, answer_text, analysis, confidence, need_review, answer_in_image, " +
                        "incomplete, answer_guessed, warnings_json, created_at, updated_at, updated_by" +
                        ") VALUES (?, ?, 0, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 0, ?, ?, '[]', ?, ?, ?)",
                ) { s ->
                    s.bindText(1, questionId)
                    s.bindText(2, sessionId)
                    s.bindText(3, (i + 1).toString())
                    s.bindText(4, sample.stem)
                    s.bindText(5, sample.material)
                    s.bindText(6, sample.type)
                    s.bindText(7, optionsJson)
                    s.bindText(8, choiceJson)
                    if (sample.answerText != null) s.bindText(9, sample.answerText) else s.bindNull(9)
                    s.bindText(10, sample.analysis)
                    s.bindDouble(11, sample.confidence)
                    s.bindLong(12, if (sample.needReview) 1L else 0L)
                    s.bindLong(13, if (sample.incomplete) 1L else 0L)
                    s.bindLong(14, if (sample.answerGuessed) 1L else 0L)
                    s.bindLong(15, createdAt)
                    s.bindLong(16, createdAt)
                    s.bindText(17, selfDeviceId)
                }
            }

            inserted++
        }

        return inserted
    }

    private data class Sample(
        val stem: String,
        val options: List<Pair<String, String>>,
        val choice: List<String>,
        val analysis: String,
        val type: String,
        val confidence: Double,
        val material: String = "",
        val answerText: String? = null,
        val needReview: Boolean = false,
        val answerGuessed: Boolean = false,
        val incomplete: Boolean = false,
    )

    // ========================================================================
    // 落地识别结果（协议 §3.4 / §4.2：客户端从**任务载荷**写本地库）
    // ========================================================================

    /**
     * 把主机返回的一次识别结果（`session`，含 `questions` / `image_hashes`）写进本地库。
     * 返回真正写进去的 op 条数（0 = 这一次什么都没写，或者这份载荷已经落过了）。
     *
     * **为什么必须有这一步**（本轮真机验收抓到的断链，症状是「识别完成」点进去却空白）：
     * 主机为识别结果写的 `sessions`/`questions` **不生成同步 op** —— 它只中转别人推来的 op。
     * 于是手机靠 [sync] 永远拉不到自己那次识别的结果：当前任务页写着「识别完成，可以离线查看」，
     * 点进去是「本次识别已不存在」。协议本来就把这件事交给客户端：
     * `task_result.session`（§4.2）与 `/tasks/active` 的 `session`（§3.4.1）都要求
     * 客户端**复用同一套落地逻辑**（1.1.0 的 `pollTaskUntilDone` 就是直接写本地库的）。
     *
     * 走 `applier.apply(op)` 而不是自己写 SQL，三个理由：
     * ① 列白名单（`PRAGMA table_info`）、逐字段 LWW、`sync_ops` 记账全是现成且与一致性向量对拍过的；
     * ② op 的**作者写成主机**（`device_id = hostDeviceId`）—— 这些字段本来就是主机写的，
     *    手机只是镜像，不该把这些字段记成「手机自己改的」（否则以后真出现冲突会判错方向）；
     * ③ 幂等：`op_id` 里带**载荷指纹**，同一份载荷重复投递会被 applier 按 `op_id` 判重。
     *
     * **只写非空字段**：载荷里为 `null` 的列一律跳过（缺失列走 schema 默认值），
     * 免得把「对端这次没给」当成「对端要把它清空」。
     */
    fun applyTaskResult(session: JsonObject, hostDeviceId: String): Int {
        val sessionId = session["session_id"]?.jsonPrimitive?.contentOrNull ?: return 0
        var applied = 0

        // 会话行：`session` 的键就是 sessions 表的列名，额外三样是载荷（题目、页序、页数）。
        applied += applyMirror(
            entity = SyncEntity.SESSION,
            entityId = sessionId,
            payload = session,
            hostDeviceId = hostDeviceId,
            skip = setOf("questions", "image_hashes", "image_count"),
        )

        for (element in session["questions"]?.jsonArray ?: emptyList()) {
            val question = element.jsonObject
            val questionId = question["question_id"]?.jsonPrimitive?.contentOrNull ?: continue
            applied += applyMirror(SyncEntity.QUESTION, questionId, question, hostDeviceId, emptySet())
        }

        val hashes = session["image_hashes"]?.jsonArray?.mapNotNull { it.jsonPrimitive.contentOrNull }
            ?: emptyList()
        hashes.forEachIndexed { index, hash ->
            // 页序：实体 id 用**确定性**拼法，同一会话重复落地不会插出第二份页序。
            val stamp = session["updated_at"]?.jsonPrimitive?.contentOrNull?.toLongOrNull()
                ?: session["created_at"]?.jsonPrimitive?.contentOrNull?.toLongOrNull()
                ?: System.currentTimeMillis()
            val page = JsonObject(
                mapOf(
                    "session_id" to JsonPrimitive(sessionId),
                    "ordinal" to JsonPrimitive(index),
                    "image_hash" to JsonPrimitive(hash),
                    "created_at" to JsonPrimitive(stamp),
                    "updated_at" to JsonPrimitive(stamp),
                    "updated_by" to JsonPrimitive(hostDeviceId),
                ),
            )
            applied += applyMirror(
                entity = SyncEntity.SESSION_IMAGE,
                entityId = "si-$sessionId-$index",
                payload = page,
                hostDeviceId = hostDeviceId,
                skip = emptySet(),
            )
        }

        return applied
    }

    /**
     * 把一条实体行**镜像**成本地 op 并应用。
     *
     * 跳过三类列：`lamport` / `field_clocks_json`（同步时钟，由 applier 维护）、
     * `deleted_at`（墓碑由删除那条路走；识别结果的载荷里它恒为 null）。
     */
    private fun applyMirror(
        entity: String,
        entityId: String,
        payload: JsonObject,
        hostDeviceId: String,
        skip: Set<String>,
    ): Int {
        val fields = linkedMapOf<String, JsonElement>()
        for ((key, value) in payload) {
            if (key in skip || key == "lamport" || key == "field_clocks_json" || key == "deleted_at") continue
            if (value is JsonNull) continue
            if (value !is JsonPrimitive) continue
            fields[key] = value
        }
        if (fields.isEmpty()) {
            return 0
        }

        val now = System.currentTimeMillis()
        val lamport = applier.watermark() + 1
        val op = SyncOp(
            // 指纹进 op_id：同一份载荷重复投递 = 同一条 op → applier 判重，不会重复写。
            op_id = "mirror-$entity-$entityId-${fields.hashCode()}",
            device_id = hostDeviceId,
            lamport = lamport,
            entity = entity,
            entity_id = entityId,
            op_type = SyncOpType.UPSERT,
            fields_json = fields,
            created_at = now,
        )
        return if (applier.apply(op) == ApplyResult.DUPLICATE) 0 else 1
    }

    private fun exec(sql: String, bind: (androidx.sqlite.SQLiteStatement) -> Unit) {
        database.connection.prepare(sql).use { statement ->
            bind(statement)
            statement.step()
        }
    }
}
