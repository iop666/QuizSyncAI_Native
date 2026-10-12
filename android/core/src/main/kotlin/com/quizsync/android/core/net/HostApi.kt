package com.quizsync.android.core.net

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.MultipartBody
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import java.io.IOException
import java.util.concurrent.TimeUnit

/** Host 返回的一次响应（状态码 + 解析后的 JSON 文本）。 */
data class HostResponse(
    val status: Int,
    val body: JsonElement?,
    val rawText: String,
) {
    fun jsonObject(): JsonObject = body?.jsonObject ?: JsonObject(emptyMap())

    fun string(field: String): String? = jsonObject()[field]?.jsonPrimitive?.content

    fun int(field: String): Int? = string(field)?.toIntOrNull()
}

/**
 * 客户端 → Host 的 HTTP 接口（`spec/04-http-api.md` 的 v1 兼容子集）。
 *
 * 只做 I/O：拼请求、解析 JSON、把 HTTP 失败变成异常。**业务判断不在这里**。
 * 鉴权：配对前不带 token，配对后所有请求都带 `Authorization: Bearer <token>`。
 */
class HostApi(
    baseUrl: String,
    private val client: OkHttpClient = defaultClient(),
    private val appVersion: String = "2.0.0",
) {
    private val base = baseUrl.trimEnd('/')
    private var token: String? = null

    companion object {
        private val JSON_MEDIA = "application/json; charset=utf-8".toMediaType()

        fun defaultClient(): OkHttpClient = OkHttpClient.Builder()
            // 与 v1 客户端同口径：连接 5s / 读 30s（上传另算）。
            .connectTimeout(5, TimeUnit.SECONDS)
            .readTimeout(30, TimeUnit.SECONDS)
            .writeTimeout(120, TimeUnit.SECONDS)
            .build()
    }

    fun setToken(value: String) {
        token = value
    }

    fun hasToken(): Boolean = token != null

    /** `GET /api/v1/info`（免鉴权，用来探活与看协议版本）。 */
    fun info(): HostResponse = get("/api/v1/info", auth = false)

    /** `POST /api/v1/pair`。成功时**自动记住 token**（409 重复配对也算成功，照样发新 token）。 */
    fun pair(code: String, deviceId: String, deviceName: String, platform: String = "android"): HostResponse {
        val body = JsonObject(
            mapOf(
                "code" to Json.parseToJsonElement("\"$code\""),
                "device_id" to Json.parseToJsonElement("\"$deviceId\""),
                "device_name" to Json.parseToJsonElement("\"$deviceName\""),
                "platform" to Json.parseToJsonElement("\"$platform\""),
                "app_version" to Json.parseToJsonElement("\"$appVersion\""),
            ),
        )
        val response = post("/api/v1/pair", body, auth = false)
        if (response.status == 200 || response.status == 409) {
            response.string("token")?.let { token = it }
        }
        return response
    }

    /** `POST /api/v1/images`（multipart，字段名固定 `file`，服务端按内容寻址去重）。 */
    fun uploadImage(bytes: ByteArray, filename: String = "page.jpg"): HostResponse {
        val part = MultipartBody.Builder()
            .setType(MultipartBody.FORM)
            .addFormDataPart("file", filename, bytes.toRequestBody("image/jpeg".toMediaType()))
            .build()
        val builder = Request.Builder().url("$base/api/v1/images").post(part)
        token?.let { builder.header("Authorization", "Bearer $it") }
        return execute(builder.build())
    }

    /** `POST /api/v1/tasks`（恒 202；`status` 是 queued / done）。单页是 [createTask] 的 [List] 版的特例。 */
    fun createTask(taskId: String, imageHash: String, sourceDevice: String): HostResponse =
        createTask(taskId, listOf(imageHash), sourceDevice)

    /**
     * `POST /api/v1/tasks`，**多页**（协议 §3.3：`image_hashes` 是页序，1..6 张）。
     *
     * 字段按页数发：单页只发 `image_hash`（**与真机上验过的那条完全一样**，不多带字段），
     * 多页才补上 `image_hashes`（服务端 `image_hashes` 优先、否则退回 `image_hash`，
     * 见 `quizsync_server.dart:657-667`）。Flutter 客户端是「两个都发」，那对单页是多余的 ——
     * 被验证过的那条路径不该为了一个用不上的字段换形状。
     */
    fun createTask(taskId: String, imageHashes: List<String>, sourceDevice: String): HostResponse {
        val hashes = imageHashes.filter { it.isNotEmpty() }
        val fields = mutableMapOf(
            "task_id" to Json.parseToJsonElement("\"$taskId\""),
            "image_hash" to Json.parseToJsonElement("\"${hashes.firstOrNull().orEmpty()}\""),
            "source_device" to Json.parseToJsonElement("\"$sourceDevice\""),
        )
        if (hashes.size > 1) {
            fields["image_hashes"] = JsonArray(hashes.map { JsonPrimitive(it) })
        }
        return post("/api/v1/tasks", JsonObject(fields))
    }

    /** `GET /api/v1/tasks/<id>`（done 时带 `session`，任务视图本身没有题目数）。 */
    fun task(taskId: String): HostResponse = get("/api/v1/tasks/$taskId")

    /**
     * `GET /api/v1/tasks/active`（`spec/04-http-api.md` §1.4.1）。
     *
     * 只读快照，用来回答「主机现在在识别什么」：主机**本机截屏不建 tasks 行**，
     * 客户端拿不到 taskId，只能靠这个端点。手机前台每秒调一次（本项目的轮询兜底）。
     * 响应恒有 `status`(`idle`｜`queued`｜`analyzing`｜`done`｜`failed`) / `task_id` /
     * `session_id` / `image_count` / `updated_at` / `message` / `active_collection_id` /
     * `active_collection_name` / `collections` / `ops_lamport`；`session` **只在 done 时**出现
     * （= 会话全字段 + `image_hashes` + `image_count` + `questions`）。
     */
    fun activeTask(): HostResponse = get("/api/v1/tasks/active")

    /** `POST /api/v1/collections/<id>/select`（切换主机当前合集，§1.3.11）。 */
    fun selectCollection(collectionId: String): HostResponse =
        postRaw("/api/v1/collections/$collectionId/select", "", auth = true)

    /** `POST /api/v1/sessions/<sessionId>/reanalyze`（202 + 新会话 id，§1.3.9）。 */
    fun reanalyze(sessionId: String): HostResponse =
        postRaw("/api/v1/sessions/$sessionId/reanalyze", "", auth = true)

    /** `GET /api/v1/collections`。 */
    fun collections(): HostResponse = get("/api/v1/collections")

    /** `GET /api/v1/devices`（已配对设备列表；用来知道「该从哪些来源设备拉 op」）。 */
    fun devices(): HostResponse = get("/api/v1/devices")

    /** `POST /api/v1/sync/ops`（推 op；`applied` / `rejected` 两个计数由服务端给）。 */
    fun pushOps(opsJson: String): HostResponse =
        postRaw("/api/v1/sync/ops", opsJson)

    /** `GET /api/v1/sync/ops?from_device=&since_lamport=`。 */
    fun pullOps(fromDevice: String, sinceLamport: Long): HostResponse =
        get("/api/v1/sync/ops?from_device=$fromDevice&since_lamport=$sinceLamport")

    /** 轮询任务直到 `done`/`failed`；超时抛异常（回放与真机都靠它）。 */
    fun awaitTask(taskId: String, timeoutMs: Long = 30_000, intervalMs: Long = 100): HostResponse {
        val deadline = System.currentTimeMillis() + timeoutMs
        var last: HostResponse? = null
        while (System.currentTimeMillis() < deadline) {
            val response = task(taskId)
            last = response
            val status = response.string("status")
            if (status == "done" || status == "failed") return response
            Thread.sleep(intervalMs)
        }
        throw IOException("任务 $taskId 在 ${timeoutMs}ms 内没到终态，最后状态：${last?.rawText}")
    }

    private fun get(path: String, auth: Boolean = true): HostResponse {
        val builder = Request.Builder().url("$base$path").get()
        if (auth) {
            token?.let { builder.header("Authorization", "Bearer $it") }
        }
        return execute(builder.build())
    }

    private fun post(path: String, body: JsonObject, auth: Boolean = true): HostResponse =
        postRaw(path, body.toString(), auth)

    private fun postRaw(path: String, body: String, auth: Boolean = true): HostResponse {
        val builder = Request.Builder()
            .url("$base$path")
            .post(body.toRequestBody(JSON_MEDIA))
        if (auth) {
            token?.let { builder.header("Authorization", "Bearer $it") }
        }
        return execute(builder.build())
    }

    private fun execute(request: Request): HostResponse =
        client.newCall(request).execute().use { response ->
            // okhttp 4.x 的 `body` 可空（5.x 不可空）：这里用安全调用 + 兜底空串，
            // 两个大版本都能编（4.x 是本工程用的那一档，见 core/build.gradle.kts 的注释）。
            val text = response.body?.string().orEmpty()
            val parsed = runCatching { Json.parseToJsonElement(text) }.getOrNull()
            HostResponse(status = response.code, body = parsed, rawText = text)
        }
}
