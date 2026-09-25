package com.streamarc.tv.update

import android.content.Context
import com.google.gson.JsonObject
import com.google.gson.JsonParser
import com.streamarc.tv.BuildConfig
import com.streamarc.tv.data.Prefs
import com.streamarc.tv.data.XtreamApi
import com.streamarc.tv.util.AppLog
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import okhttp3.Request
import java.text.SimpleDateFormat
import java.util.Locale
import java.util.TimeZone
import java.util.concurrent.TimeUnit

/**
 * Service notice shown on the home screen, read from `release/announcement.json`
 * on the repository's main branch (see ANNOUNCEMENT_FORMAT.md). It can be posted,
 * changed and removed without an app update; the banner hides when there is nothing
 * to show. The last notice seen is cached so it still shows while offline.
 */
object Announcement {

    enum class Level { INFO, WARNING, OUTAGE }

    data class Notice(
        val title: String,
        val message: String,
        val level: Level,
        val link: String?,
        /** Epoch millis after which the notice hides itself, or null for "until removed". */
        val untilMs: Long?
    ) {
        fun isActive(now: Long = System.currentTimeMillis()): Boolean =
            message.isNotBlank() && (untilMs == null || now < untilMs)
    }

    private const val TAG = "Notice"
    private const val SUPPORTED_SCHEMA = 1
    private const val PLATFORM = "android"

    private val repo: String get() = BuildConfig.GITHUB_REPO

    private val apiUrl: String
        get() = "https://api.github.com/repos/$repo/contents/release/announcement.json?ref=main"

    private val rawUrl: String
        get() = "https://raw.githubusercontent.com/$repo/main/release/announcement.json"

    // Short timeouts: the home screen must never wait on GitHub.
    private val client by lazy {
        XtreamApi.client.newBuilder()
            .connectTimeout(8, TimeUnit.SECONDS)
            .readTimeout(8, TimeUnit.SECONDS)
            .callTimeout(12, TimeUnit.SECONDS)
            .build()
    }

    /** The notice last fetched (possibly inactive), for an instant first paint. */
    fun cached(context: Context): Notice? =
        Prefs(context).announcementJson?.let { parse(it) }

    /**
     * Fetches the current notice. Returns null when the file is missing, unreadable
     * or unreachable (the caller keeps whatever it already shows in that case).
     */
    suspend fun fetch(context: Context): Notice? = withContext(Dispatchers.IO) {
        val json = try {
            get(apiUrl, "application/vnd.github.raw")
        } catch (e: Exception) {
            AppLog.w(TAG, "Notice via API failed (${e.javaClass.simpleName}: ${e.message}); trying raw")
            null
        } ?: try {
            get(rawUrl, "application/json")
        } catch (e: Exception) {
            AppLog.w(TAG, "Notice via raw failed (${e.javaClass.simpleName}: ${e.message})")
            return@withContext null
        } ?: run {
            // No file in the repository at all: nothing to show.
            Prefs(context).announcementJson = null
            return@withContext Notice("", "", Level.INFO, null, null)
        }
        val notice = parse(json) ?: return@withContext null
        Prefs(context).announcementJson = json
        notice
    }

    /** GET a text document; returns null on 404. */
    private fun get(url: String, accept: String): String? {
        val req = Request.Builder()
            .url(url)
            .header("Accept", accept)
            .header("User-Agent", XtreamApi.USER_AGENT)
            .header("Cache-Control", "no-cache")
            .build()
        client.newCall(req).execute().use { resp ->
            if (resp.code == 404) return null
            if (!resp.isSuccessful) throw IllegalStateException("GitHub returned HTTP ${resp.code}")
            return resp.body?.string() ?: ""
        }
    }

    /** Parses the JSON into a Notice; an inactive or hidden notice has an empty message. */
    fun parse(json: String): Notice? {
        val o: JsonObject = try {
            JsonParser.parseString(json).asJsonObject
        } catch (e: Exception) {
            AppLog.w(TAG, "Notice file is not valid JSON: ${e.message}")
            return null
        }
        val schema = o.get("schemaVersion")?.takeIf { it.isJsonPrimitive }?.asInt ?: 0
        if (schema != SUPPORTED_SCHEMA) {
            AppLog.w(TAG, "Notice file has unsupported schema $schema")
            return null
        }
        val hidden = Notice("", "", Level.INFO, null, null)
        val active = o.get("active")?.takeIf { it.isJsonPrimitive }?.asBoolean ?: false
        if (!active) return hidden
        val platforms = o.get("platforms")?.takeIf { it.isJsonArray }?.asJsonArray
            ?.mapNotNull { it.takeIf { p -> p.isJsonPrimitive }?.asString?.lowercase() }
        if (!platforms.isNullOrEmpty() && PLATFORM !in platforms) return hidden

        val message = str(o, "message").replace("\\n", "\n").trim()
        if (message.isEmpty()) return hidden
        val title = str(o, "title").trim()
        val level = when (str(o, "level").trim().lowercase()) {
            "warning", "warn" -> Level.WARNING
            "outage", "error", "danger", "critical" -> Level.OUTAGE
            else -> Level.INFO
        }
        val link = str(o, "link").trim().takeIf { it.startsWith("http://") || it.startsWith("https://") }
        val until = parseTime(str(o, "until").trim())
        return Notice(title, message, level, link, until)
    }

    private fun str(o: JsonObject, key: String): String =
        o.get(key)?.takeIf { it.isJsonPrimitive }?.asString ?: ""

    /** Accepts ISO-8601 with an offset or "Z"; a date-only value ends that day (UTC). */
    private fun parseTime(raw: String): Long? {
        if (raw.isEmpty()) return null
        // Normalise "…Z" and "…-04:00" to the "-0400" form SimpleDateFormat's Z understands on every Android version.
        var s = raw.trim().replace(' ', 'T')
        if (s.endsWith("Z") || s.endsWith("z")) s = s.dropLast(1) + "+0000"
        s = s.replace(Regex("([+-]\\d\\d):(\\d\\d)$"), "$1$2")
        val patterns = listOf(
            "yyyy-MM-dd'T'HH:mm:ssZ",
            "yyyy-MM-dd'T'HH:mmZ",
            "yyyy-MM-dd'T'HH:mm:ss",
            "yyyy-MM-dd'T'HH:mm",
            "yyyy-MM-dd"
        )
        for (p in patterns) {
            try {
                val f = SimpleDateFormat(p, Locale.US).apply {
                    isLenient = false
                    if (!p.endsWith("Z")) timeZone = TimeZone.getTimeZone("UTC")
                }
                val t = f.parse(s)?.time ?: continue
                return if (p == "yyyy-MM-dd") t + 24L * 3600_000 else t
            } catch (_: Exception) {
            }
        }
        AppLog.w(TAG, "Notice 'until' not understood: $s")
        return null
    }
}
