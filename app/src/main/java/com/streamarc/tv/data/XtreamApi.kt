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

    class ApiException(message: String) : Exception(message)

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
                throw ApiException("Unexpected response from server")
            }
            val info = resp?.userInfo ?: throw ApiException("Unexpected response from server")
            if (!info.isAuthenticated) {
                throw ApiException(info.message?.takeIf { it.isNotBlank() } ?: "Invalid username or password")
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
    suspend fun fullGuide(service: Service, account: Account, fromEpoch: Long, toEpoch: Long): Map<String, List<EpgProgramme>> =
        withContext(Dispatchers.IO) {
            val base = serverUrl(service)
            val url = base.newBuilder()
                .addPathSegment("xmltv.php")
                .addQueryParameter("username", account.username)
                .addQueryParameter("password", account.password)
                .build()
            val req = Request.Builder().url(url).header("User-Agent", USER_AGENT).build()
            val slowClient = client.newBuilder()
                .readTimeout(180, TimeUnit.SECONDS)
                .callTimeout(300, TimeUnit.SECONDS)
                .build()
            try {
                slowClient.newCall(req).execute().use { resp ->
                    if (!resp.isSuccessful) throw ApiException("Guide download failed (HTTP ${resp.code})")
                    val body = resp.body ?: throw ApiException("Empty guide")
                    body.byteStream().use { input -> XmltvParser.parse(input, fromEpoch, toEpoch) }
                }
            } catch (e: IOException) {
                throw ApiException("Can't download guide: ${e.message ?: "network error"}")
            }
        }

    /** Now/next programmes for one live channel (`get_short_epg`). */
    suspend fun shortEpg(service: Service, account: Account, streamId: String, limit: Int = 30): List<EpgProgramme> =
        withContext(Dispatchers.IO) {
            val body = get(
                apiUrl(
                    service, account.username, account.password, "get_short_epg",
                    mapOf("stream_id" to streamId, "limit" to limit.toString())
                )
            )
            EpgParser.parse(body)
        }

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
            // Some panels answer with an object (e.g. an error) instead of a list.
            return emptyList()
        }
        val type = TypeToken.getParameterized(List::class.java, T::class.java).type
        return try {
            gson.fromJson<List<T>>(trimmed, type) ?: emptyList()
        } catch (e: JsonSyntaxException) {
            throw ApiException("Unexpected response from server")
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
        try {
            client.newCall(req).execute().use { resp ->
                val text = resp.body?.string() ?: ""
                if (!resp.isSuccessful) {
                    if (resp.code == 401 || resp.code == 403) {
                        throw ApiException("Invalid username or password")
                    }
                    throw ApiException("Server error (HTTP ${resp.code})")
                }
                if (text.isBlank()) throw ApiException("Empty response from server")
                return text
            }
        } catch (e: IOException) {
            throw ApiException("Can't reach server: ${e.message ?: "network error"}")
        }
    }
}
