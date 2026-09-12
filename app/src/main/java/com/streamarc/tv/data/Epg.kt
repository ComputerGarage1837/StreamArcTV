package com.streamarc.tv.data

import android.util.Base64
import com.google.gson.JsonElement
import com.google.gson.JsonObject
import com.google.gson.JsonParser
import kotlinx.coroutines.sync.Semaphore
import kotlinx.coroutines.sync.withPermit
import java.text.SimpleDateFormat
import java.util.Locale
import java.util.TimeZone

/** One programme in the electronic programme guide. Times are epoch seconds. */
data class EpgProgramme(
    val title: String,
    val description: String,
    val start: Long,
    val end: Long
) {
    fun isOnNow(now: Long = System.currentTimeMillis() / 1000): Boolean = now in start until end

    /** 0..1000 progress through the programme, or 0 if it hasn't started. */
    fun progress(now: Long = System.currentTimeMillis() / 1000): Int {
        if (end <= start || now < start) return 0
        if (now >= end) return 1000
        return ((now - start) * 1000 / (end - start)).toInt()
    }
}

/** Parses the `get_short_epg` response of an Xtream Codes panel. */
object EpgParser {
    private val sqlFmt = SimpleDateFormat("yyyy-MM-dd HH:mm:ss", Locale.US)

    fun parse(body: String): List<EpgProgramme> {
        val root: JsonElement = try {
            JsonParser.parseString(body)
        } catch (_: Exception) {
            return emptyList()
        }
        val listings = when {
            root.isJsonObject && root.asJsonObject.has("epg_listings") -> root.asJsonObject.getAsJsonArray("epg_listings")
            root.isJsonArray -> root.asJsonArray
            else -> return emptyList()
        } ?: return emptyList()

        val out = ArrayList<EpgProgramme>()
        for (el in listings) {
            if (!el.isJsonObject) continue
            val o = el.asJsonObject
            val start = o.str("start_timestamp")?.toLongOrNull() ?: parseSql(o.str("start")) ?: continue
            val end = o.str("stop_timestamp")?.toLongOrNull() ?: parseSql(o.str("end")) ?: parseSql(o.str("stop")) ?: continue
            val title = decode(o.str("title")).ifBlank { "Untitled programme" }
            val desc = decode(o.str("description"))
            out.add(EpgProgramme(title, desc, start, end))
        }
        return out.sortedBy { it.start }
    }

    private fun JsonObject.str(name: String): String? =
        get(name)?.takeIf { !it.isJsonNull }?.let { runCatching { it.asString }.getOrNull() }

    private fun decode(value: String?): String {
        if (value.isNullOrBlank()) return ""
        return try {
            String(Base64.decode(value, Base64.DEFAULT), Charsets.UTF_8).trim()
        } catch (_: Exception) {
            value.trim()
        }
    }

    private fun parseSql(value: String?): Long? {
        if (value.isNullOrBlank()) return null
        return try {
            synchronized(sqlFmt) {
                sqlFmt.timeZone = TimeZone.getDefault()
                sqlFmt.parse(value)?.time?.div(1000)
            }
        } catch (_: Exception) {
            null
        }
    }
}

/**
 * Small in-memory cache of per-channel EPG so guide rows can be filled lazily
 * without hammering the panel. Entries expire after a few minutes.
 */
object EpgCache {
    private const val TTL_MS = 5 * 60 * 1000L
    private val cache = HashMap<String, Pair<Long, List<EpgProgramme>>>()
    private val gate = Semaphore(4)

    private fun key(service: Service, streamId: String) = "${service.name}/$streamId"

    /** Cached programmes if fresh, else null (no network). */
    fun peek(service: Service, streamId: String): List<EpgProgramme>? {
        val e = synchronized(cache) { cache[key(service, streamId)] } ?: return null
        return if (System.currentTimeMillis() - e.first < TTL_MS) e.second else null
    }

    /** Programmes for the channel, fetching (at most 4 at a time) when not cached. */
    suspend fun get(service: Service, account: Account, streamId: String): List<EpgProgramme> {
        peek(service, streamId)?.let { return it }
        return gate.withPermit {
            peek(service, streamId)?.let { return@withPermit it }
            val list = try {
                XtreamApi.shortEpg(service, account, streamId)
            } catch (_: Exception) {
                emptyList()
            }
            synchronized(cache) { cache[key(service, streamId)] = System.currentTimeMillis() to list }
            list
        }
    }

    fun clear() = synchronized(cache) { cache.clear() }
}
