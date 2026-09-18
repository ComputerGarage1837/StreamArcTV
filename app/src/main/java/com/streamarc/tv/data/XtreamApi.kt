package com.streamarc.tv.data

import com.google.gson.Gson
import com.google.gson.JsonSyntaxException
import com.google.gson.reflect.TypeToken
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import okhttp3.HttpUrl
import okhttp3.HttpUrl.Companion.toHttpUrlOrNull
import okhttp3.OkHttpClient
import okhttp3.Request
import java.io.IOException
import java.util.concurrent.TimeUnit

/**
 * Minimal Xtream Codes API client.
 */
object XtreamApi {

    open class ApiException(message: String) : Exception(message)
    /** The request never got a proper answer (DNS, connect, timeout, TLS…); the cause says which. */
    class NetworkException(message: String, cause: Throwable) : ApiException(message) { init { initCause(cause) } }
    /** The server answered with an HTTP error status. */
    class HttpException(val code: Int) : ApiException("Server error (HTTP $code)")
    /** The server answered, but not with the JSON the API promises (a block page, say). */
    class BadResponseException : ApiException("Unexpected response from server")
    /** Signed in fine as far as the network goes, but the account itself is the problem. */
    class AccountException(message: String) : ApiException(message)

    const val USER_AGENT = "StreamArcTV"

    val client: OkHttpClient = OkHttpClient.Builder()
        .connectTimeout(20, TimeUnit.SECONDS)
        .readTimeout(40, TimeUnit.SECONDS)
        .followRedirects(true)
        .followSslRedirects(true)
        .build()

    private val gson = Gson()

    // ---- Public API ----------------------------------------------------

    suspend fun login(service: Service, username: String, password: String): UserInfo =
        withContext(Dispatchers.IO) {
            val body = get(apiUrl(service, username, password))
            val resp = try {
                gson.fromJson(body, LoginResponse::class.java)
            } catch (e: JsonSyntaxException) {
                throw BadResponseException()
            }
            val info = resp?.userInfo ?: throw BadResponseException()
            com.streamarc.tv.util.AppLog.i("Api", "login ${service.name}: auth=${info.auth} status=${info.status} exp=${info.expDate} " +
                "connections=${info.activeCons}/${info.maxConnections} trial=${info.isTrial} created=${info.createdAt} message=${info.message} " +
                "server=${resp.serverInfo?.url}:${resp.serverInfo?.port} (${resp.serverInfo?.serverProtocol})")
            val status = info.status?.trim()?.lowercase().orEmpty()
            val exp = info.expDateEpochSeconds
            if (!info.isAuthenticated) {
                throw AccountException(when {
                    status.contains("expire") || (exp != null && exp * 1000 < System.currentTimeMillis()) ->
                        "This account has expired" + (exp?.let { " (on ${Format.date(it)})" } ?: "") + ". Contact your provider to renew it."
                    status.contains("ban") || status.contains("disab") || status.contains("block") ->
                        "This account has been disabled by the provider. Contact them to sort it out."
                    !info.message.isNullOrBlank() -> "The provider says: ${info.message}"
                    else -> "Wrong username or password. Check both carefully (they are case-sensitive)."
                })
            }
            if (status.contains("expire") || (exp != null && exp * 1000 < System.currentTimeMillis())) {
                throw AccountException("This account expired" + (exp?.let { " on ${Format.date(it)}" } ?: "") + ". Contact your provider to renew it.")
            }
            if (status.contains("ban") || status.contains("disab") || status.contains("block")) {
                throw AccountException("This account has been disabled by the provider. Contact them to sort it out.")
            }
            info
        }

    suspend fun categories(service: Service, account: Account, kind: ContentKind = service.kind): List<Category> =
        withContext(Dispatchers.IO) {
            val action = when (kind) {
                ContentKind.LIVE -> "get_live_categories"
                ContentKind.MOVIE -> "get_vod_categories"
                ContentKind.SERIES -> "get_series_categories"
            }
            val body = get(apiUrl(service, account.username, account.password, action))
            parseList<Category>(body)
        }

    suspend fun streams(service: Service, account: Account, categoryId: String?, kind: ContentKind = service.kind): List<Stream> =
        withContext(Dispatchers.IO) {
            val action = when (kind) {
                ContentKind.LIVE -> "get_live_streams"
                ContentKind.MOVIE -> "get_vod_streams"
                ContentKind.SERIES -> "get_series"
            }
            val extra = if (categoryId != null) mapOf("category_id" to categoryId) else emptyMap()
            val body = get(apiUrl(service, account.username, account.password, action, extra))
            parseList<Stream>(body)
        }

    /** Episodes of a series grouped by season (`get_series_info`). */
    suspend fun seriesInfo(service: Service, account: Account, seriesId: String): List<Episode> =
        withContext(Dispatchers.IO) {
            val body = get(apiUrl(service, account.username, account.password, "get_series_info", mapOf("series_id" to seriesId)))
            val root = try { com.google.gson.JsonParser.parseString(body) } catch (_: Exception) { throw ApiException("Unexpected response from server") }
            if (!root.isJsonObject) return@withContext emptyList()
            val episodes = root.asJsonObject.get("episodes")?.takeIf { !it.isJsonNull } ?: return@withContext emptyList()
            val out = ArrayList<Episode>()
            fun add(seasonKey: String, arr: com.google.gson.JsonElement) {
                if (!arr.isJsonArray) return
                for (e in arr.asJsonArray) {
                    if (!e.isJsonObject) continue
                    val o = e.asJsonObject
                    fun str(n: String) = o.get(n)?.takeIf { !it.isJsonNull }?.let { runCatching { it.asString }.getOrNull() }
                    val id = str("id") ?: continue
                    val info = o.get("info")?.takeIf { it.isJsonObject }?.asJsonObject
                    fun istr(n: String) = info?.get(n)?.takeIf { !it.isJsonNull }?.let { runCatching { it.asString }.getOrNull() }
                    out.add(
                        Episode(
                            id = id,
                            title = str("title")?.takeIf { it.isNotBlank() } ?: "Episode ${str("episode_num") ?: ""}".trim(),
                            season = str("season")?.toIntOrNull() ?: seasonKey.toIntOrNull() ?: 0,
                            number = str("episode_num")?.toIntOrNull() ?: 0,
                            containerExtension = str("container_extension")?.takeIf { it.isNotBlank() } ?: "mp4",
                            plot = istr("plot"),
                            duration = istr("duration")
                        )
                    )
                }
            }
            when {
                episodes.isJsonObject -> episodes.asJsonObject.entrySet().forEach { (k, v) -> add(k, v) }
                episodes.isJsonArray -> episodes.asJsonArray.forEachIndexed { i, v -> add((i + 1).toString(), v) }
            }
            out.sortedWith(compareBy({ it.season }, { it.number }))
        }

    fun episodeUrl(service: Service, account: Account, episode: Episode): String {
        val base = serverUrl(service)
        return base.newBuilder()
            .addPathSegment("series")
            .addPathSegment(account.username)
            .addPathSegment(account.password)
            .addPathSegment("${episode.id}.${episode.containerExtension}")
            .build().toString()
    }

    /**
     * The whole programme guide in one request (`xmltv.php`), parsed as a stream so
     * large files stay cheap. Returns programmes keyed by XMLTV channel id, limited
     * to [fromEpoch, toEpoch) so a week-long file doesn't fill memory.
     */
    /** Thrown from the progress callback to stop a guide download (e.g. it is too large). */
    class GuideTooLarge(val bytes: Long) : Exception("Guide too large")

    suspend fun fullGuide(
        service: Service, account: Account, fromEpoch: Long, toEpoch: Long,
        onProgress: ((bytes: Long, total: Long) -> Unit)? = null
    ): XmltvParser.Guide =
        withContext(Dispatchers.IO) {
            val base = serverUrl(service)
            val url = base.newBuilder()
                .addPathSegment("xmltv.php")
                .addQueryParameter("username", account.username)
                .addQueryParameter("password", account.password)
                .build()
            // Ask for gzip ourselves so the byte count below is what really crosses the wire.
            val req = Request.Builder().url(url)
                .header("User-Agent", USER_AGENT)
                .header("Accept-Encoding", "gzip")
                .build()
            val slowClient = client.newBuilder()
                .readTimeout(180, TimeUnit.SECONDS)
                .callTimeout(0, TimeUnit.MILLISECONDS)
                .build()
            try {
                slowClient.newCall(req).execute().use { resp ->
                    if (!resp.isSuccessful) throw ApiException("Guide download failed (HTTP ${resp.code})")
                    val body = resp.body ?: throw ApiException("Empty guide")
                    val total = body.contentLength()
                    val counting = object : java.io.FilterInputStream(body.byteStream()) {
                        var count = 0L
                        var lastReport = 0L
                        override fun read(): Int = super.read().also { if (it >= 0) tick(1) }
                        override fun read(b: ByteArray, off: Int, len: Int): Int = super.read(b, off, len).also { if (it > 0) tick(it) }
                        private fun tick(n: Int) {
                            count += n
                            if (count - lastReport >= 128 * 1024) { lastReport = count; onProgress?.invoke(count, total) }
                        }
                        override fun close() {
                            onProgress?.invoke(count, if (total > 0) total else count)   // final, exact
                            super.close()
                        }
                    }
                    val gzip = resp.header("Content-Encoding")?.contains("gzip", ignoreCase = true) == true
                    val input: java.io.InputStream = if (gzip) java.util.zip.GZIPInputStream(counting, 64 * 1024) else counting
                    input.use { XmltvParser.parse(it, fromEpoch, toEpoch) }
                }
            } catch (e: GuideTooLarge) {
                throw e
            } catch (e: IOException) {
                throw ApiException("Can't download guide: ${e.message ?: "network error"}")
            }
        }

    /** Now/next programmes for one live channel (`get_short_epg`). */
    suspend fun shortEpg(service: Service, account: Account, streamId: String, limit: Int = 48): List<EpgProgramme> =
        withContext(Dispatchers.IO) {
            val body = get(
                apiUrl(
                    service, account.username, account.password, "get_short_epg",
                    mapOf("stream_id" to streamId, "limit" to limit.toString())
                )
            )
            EpgParser.parse(body)
        }

    /** Direct movie address from an id and container extension (for items remembered outside the catalogue). */
    fun movieUrl(service: Service, account: Account, streamId: String, ext: String?): String =
        serverUrl(service).newBuilder()
            .addPathSegment("movie")
            .addPathSegment(account.username)
            .addPathSegment(account.password)
            .addPathSegment("$streamId.${ext?.takeIf { it.isNotBlank() } ?: "mp4"}")
            .build().toString()

    fun streamUrl(service: Service, account: Account, stream: Stream, liveFormat: String): String {
        val base = serverUrl(service)
        val id = stream.streamId ?: throw ApiException("Stream has no id")
        val b = base.newBuilder()
        when (if (service.kind == ContentKind.LIVE) ContentKind.LIVE else ContentKind.MOVIE) {
            ContentKind.LIVE -> {
                b.addPathSegment("live")
                    .addPathSegment(account.username)
                    .addPathSegment(account.password)
                    .addPathSegment("$id.$liveFormat")
            }
            else -> {
                val ext = stream.containerExtension?.takeIf { it.isNotBlank() } ?: "mp4"
                b.addPathSegment("movie")
                    .addPathSegment(account.username)
                    .addPathSegment(account.password)
                    .addPathSegment("$id.$ext")
            }
        }
        return b.build().toString()
    }

    // ---- Internals -----------------------------------------------------

    private fun serverUrl(service: Service): HttpUrl {
        if (!service.isConfigured) {
            throw ApiException("${service.title} isn't configured in this build")
        }
        return service.baseUrl.toHttpUrlOrNull()
            ?: throw ApiException("Bad server URL: ${service.baseUrl}")
    }

    private inline fun <reified T> parseList(body: String): List<T> {
        val trimmed = body.trim()
        if (!trimmed.startsWith("[")) {
            // A web page means a block / maintenance page; an object is a panel's own error message.
            if (trimmed.startsWith("<")) throw BadResponseException()
            return emptyList()
        }
        val type = TypeToken.getParameterized(List::class.java, T::class.java).type
        return try {
            gson.fromJson<List<T>>(trimmed, type) ?: emptyList()
        } catch (e: JsonSyntaxException) {
            throw BadResponseException()
        }
    }

    private fun apiUrl(
        service: Service,
        username: String,
        password: String,
        action: String? = null,
        extra: Map<String, String> = emptyMap()
    ): HttpUrl {
        val base = serverUrl(service)
        val b = base.newBuilder()
            .addPathSegment("player_api.php")
            .addQueryParameter("username", username)
            .addQueryParameter("password", password)
        if (action != null) b.addQueryParameter("action", action)
        extra.forEach { (k, v) -> b.addQueryParameter(k, v) }
        return b.build()
    }

    private fun get(url: HttpUrl): String {
        val req = Request.Builder()
            .url(url)
            .header("User-Agent", USER_AGENT)
            .header("Accept", "application/json, */*")
            .build()
        val safe = com.streamarc.tv.util.AppLog.safeUrl(url.toString())
        val started = System.currentTimeMillis()
        try {
            client.newCall(req).execute().use { resp ->
                val text = resp.body?.string() ?: ""
                val ct = resp.header("Content-Type").orEmpty()
                val ms = System.currentTimeMillis() - started
                if (!resp.isSuccessful) {
                    com.streamarc.tv.util.AppLog.e("Api", "GET $safe -> HTTP ${resp.code} ($ct, ${text.length} chars, ${ms}ms) body: ${text.take(300).replace('\n', ' ')}")
                    throw HttpException(resp.code)
                }
                if (text.isBlank()) {
                    com.streamarc.tv.util.AppLog.e("Api", "GET $safe -> HTTP ${resp.code} but an empty body (${ms}ms)")
                    throw BadResponseException()
                }
                if (ct.contains("text/html", ignoreCase = true) && text.trimStart().startsWith("<")) {
                    com.streamarc.tv.util.AppLog.e("Api", "GET $safe -> HTTP ${resp.code} returned a web page instead of data ($ct, ${ms}ms): ${text.take(300).replace('\n', ' ')}")
                    throw BadResponseException()
                }
                com.streamarc.tv.util.AppLog.i("Api", "GET $safe -> HTTP ${resp.code} ($ct, ${text.length} chars, ${ms}ms)")
                return text
            }
        } catch (e: IOException) {
            com.streamarc.tv.util.AppLog.e("Api", "GET $safe failed after ${System.currentTimeMillis() - started}ms: ${e.javaClass.simpleName}: ${e.message}" +
                (e.cause?.let { " <- ${it.javaClass.simpleName}: ${it.message}" } ?: ""))
            throw NetworkException("Can't reach server: ${e.message ?: "network error"}", e)
        }
    }
}
