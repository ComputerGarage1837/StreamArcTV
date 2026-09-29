package com.streamarc.tv.data

import android.util.Base64
import com.google.gson.JsonElement
import com.google.gson.JsonObject
import com.google.gson.JsonParser
import kotlinx.coroutines.sync.Semaphore
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
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

/** Streaming parser for XMLTV (`xmltv.php`). */
object XmltvParser {
    private val fmt = SimpleDateFormat("yyyyMMddHHmmss Z", Locale.US)
    private val fmtNoZone = SimpleDateFormat("yyyyMMddHHmmss", Locale.US).apply { timeZone = TimeZone.getTimeZone("UTC") }

    /** Result of a parse: programmes by channel id, plus display-name → id for loose matching. */
    class Guide(val byId: Map<String, List<EpgProgramme>>, val idByName: Map<String, String>)

    fun parse(input: java.io.InputStream, from: Long, to: Long): Guide {
        val out = HashMap<String, ArrayList<EpgProgramme>>()
        val names = HashMap<String, String>()
        var channelId: String? = null
        val parser = android.util.Xml.newPullParser()
        parser.setFeature(org.xmlpull.v1.XmlPullParser.FEATURE_PROCESS_NAMESPACES, false)
        parser.setInput(input, null)
        var event = parser.eventType
        var channel: String? = null
        var start = 0L
        var end = 0L
        var title = ""
        var desc = ""
        var inProgramme = false
        var text: String? = null
        while (event != org.xmlpull.v1.XmlPullParser.END_DOCUMENT) {
            when (event) {
                org.xmlpull.v1.XmlPullParser.START_TAG -> when (parser.name) {
                    "channel" -> channelId = parser.getAttributeValue(null, "id")
                    "display-name" -> if (channelId != null) text = ""
                    "programme" -> {
                        inProgramme = true
                        channel = parser.getAttributeValue(null, "channel")
                        start = time(parser.getAttributeValue(null, "start"))
                        end = time(parser.getAttributeValue(null, "stop"))
                        title = ""; desc = ""
                    }
                    "title", "desc" -> if (inProgramme) text = ""
                }
                org.xmlpull.v1.XmlPullParser.TEXT -> if (text != null) text += parser.text
                org.xmlpull.v1.XmlPullParser.END_TAG -> when (parser.name) {
                    "display-name" -> {
                        val id = channelId
                        if (id != null && text != null) {
                            val key = normalize(text!!)
                            if (key.isNotEmpty() && !names.containsKey(key)) names[key] = id
                            text = null
                        }
                    }
                    "channel" -> channelId = null
                    "title" -> if (inProgramme && text != null) { if (title.isBlank()) title = text.trim(); text = null }
                    "desc" -> if (inProgramme && text != null) { if (desc.isBlank()) desc = text.trim(); text = null }
                    "programme" -> {
                        inProgramme = false
                        val ch = channel
                        if (ch != null && end > from && start < to && end > start) {
                            out.getOrPut(ch) { ArrayList() }.add(EpgProgramme(title.ifBlank { "Untitled programme" }, desc, start, end))
                        }
                    }
                }
            }
            event = parser.next()
        }
        for (list in out.values) list.sortBy { it.start }
        return Guide(out, names)
    }

    /** Loose key for name matching: lowercase, no punctuation/spaces, no HD/FHD/4K suffixes. */
    fun normalize(name: String): String =
        name.lowercase().replace(Regex("\\b(uhd|fhd|hd|sd|4k)\\b"), "").replace(Regex("[^a-z0-9]"), "")

    private fun time(value: String?): Long {
        if (value.isNullOrBlank()) return 0L
        val v = value.trim()
        return try {
            synchronized(fmt) {
                if (v.length > 14) fmt.parse(v)?.time?.div(1000) ?: 0L else fmtNoZone.parse(v)?.time?.div(1000) ?: 0L
            }
        } catch (_: Exception) {
            0L
        }
    }
}

/**
 * Small in-memory cache of per-channel EPG so guide rows can be filled lazily
 * without hammering the panel. Entries expire after a few minutes.
 */
object EpgCache {
    private const val TTL_MS = 6 * 60 * 60 * 1000L   // per-channel data is refreshed after this
    private val cache = HashMap<String, Pair<Long, List<EpgProgramme>>>()
    private val gate = Semaphore(6)

    // ---- Whole-guide handling ------------------------------------------
    //
    // Other IPTV apps "lose" their guide because they refresh in the foreground, keep it
    // only in memory, and accept whatever the panel returns, including the empty or
    // half-built file a panel serves while it regenerates its EPG. Here the last good
    // guide is kept on disk, refreshed in the background, and only replaced by a new
    // download that is at least as complete.

    private const val REFRESH_MS = 3 * 60 * 60 * 1000L          // refresh quietly after this age
    private const val RETRY_MS = 5 * 60 * 1000L                 // back-off after a failed download
    private val guides = HashMap<Service, Pair<Long, XmltvParser.Guide>>()
    private val guideFailedAt = HashMap<Service, Long>()
    private val guideMutex = kotlinx.coroutines.sync.Mutex()
    private val refreshing = HashSet<Service>()
    @Volatile private var appContext: android.content.Context? = null

    /** Give the cache a context once (application) so guides can be kept on disk. */
    fun init(context: android.content.Context) { appContext = context.applicationContext }

    /** True while a guide (fresh or older) is in memory. */
    fun guideLoaded(service: Service): Boolean = synchronized(guides) { guides[service] } != null

    /** Age of the in-memory guide in ms, or -1. */
    fun guideAge(service: Service): Long = synchronized(guides) { guides[service] }?.let { System.currentTimeMillis() - it.first } ?: -1L

    /**
     * Programmes for a channel from the full guide: by its XMLTV id first, then by a loose
     * match on the channel name. Null when the guide has nothing for it.
     */
    fun guideFor(service: Service, epgChannelId: String?, channelName: String? = null): List<EpgProgramme>? {
        val g = synchronized(guides) { guides[service] }?.second ?: return null
        val id = epgChannelId?.trim()?.takeIf { it.isNotEmpty() }
        if (id != null) {
            g.byId[id]?.let { return it }
            g.byId.entries.firstOrNull { it.key.equals(id, ignoreCase = true) }?.value?.let { return it }
        }
        val name = channelName?.let { XmltvParser.normalize(it) }?.takeIf { it.isNotEmpty() } ?: return null
        val byName = g.idByName[name] ?: return null
        return g.byId[byName]
    }

    /** How many channels the loaded guide covers (0 when not loaded). */
    fun guideChannelCount(service: Service): Int = synchronized(guides) { guides[service] }?.second?.byId?.size ?: 0

    /**
     * Makes a guide available: from memory, else from the disk copy (instantly), else by
     * downloading. A guide older than [REFRESH_MS] is refreshed in the background while the
     * old one stays in use. Returns true when a guide is available afterwards. Never throws.
     */
    suspend fun loadGuide(service: Service, account: Account, onProgress: ((Long, Long) -> Unit)? = null, force: Boolean = false): Boolean {
        if (force) {
            // Manual refresh: fetch now, regardless of age. The download still only replaces the
            // current guide when it is at least as complete, so a bad fetch can't lose data.
            synchronized(guides) { guideFailedAt.remove(service) }
            clear()
            appContext?.let { Prefs(it).setShortEpgOffUntil(service, 0L) }
            return guideMutex.withLock { download(service, account, onProgress) }
        }
        synchronized(guides) { guides[service] }?.let { (at, _) ->
            if (System.currentTimeMillis() - at > REFRESH_MS) refreshInBackground(service, account)
            return true
        }
        return guideMutex.withLock {
            synchronized(guides) { guides[service] }?.let { return@withLock true }
            val ctx = appContext
            val disk = if (ctx != null) withContext(Dispatchers.IO) { readDisk(ctx, service) } else null
            if (disk != null) {
                synchronized(guides) { guides[service] = disk }
                val age = System.currentTimeMillis() - disk.first
                com.streamarc.tv.util.AppLog.i("Epg", "${service.name} guide from disk: ${disk.second.byId.size} channels, " +
                    "${disk.second.byId.values.sumOf { it.size }} programmes, ${age / 60_000} min old")
                if (age > REFRESH_MS) refreshInBackground(service, account)
                return@withLock true
            }
            val failed = synchronized(guides) { guideFailedAt[service] }
            if (failed != null && System.currentTimeMillis() - failed < RETRY_MS) {
                com.streamarc.tv.util.AppLog.w("Epg", "${service.name} guide: not retrying yet (last attempt failed ${(System.currentTimeMillis() - failed) / 1000}s ago)")
                return@withLock false
            }
            download(service, account, onProgress)
        }
    }

    /** Downloads and installs a guide when it passes the completeness check against the current one. */
    private suspend fun download(service: Service, account: Account, onProgress: ((Long, Long) -> Unit)?): Boolean {
        val now = System.currentTimeMillis() / 1000
        val from = (now / 1800) * 1800 - 2 * 3600
        val to = from + 50 * 3600      // two days so a disk copy still covers the grid later
        val started = System.currentTimeMillis()
        com.streamarc.tv.util.AppLog.i("Epg", "${service.name} guide: downloading whole listing (xmltv)")
        return try {
            val guide = XtreamApi.fullGuide(service, account, from, to, onProgress)
            val current = synchronized(guides) { guides[service] }?.second
            val programmes = guide.byId.values.sumOf { it.size }
            com.streamarc.tv.util.AppLog.i("Epg", "${service.name} guide downloaded: ${guide.byId.size} channels, $programmes programmes " +
                "in ${(System.currentTimeMillis() - started) / 1000}s")
            if (guide.byId.isEmpty()) throw IllegalStateException("Empty guide")
            if (current != null && !atLeastAsComplete(guide, current)) {
                // The panel is probably regenerating its EPG; keep the good copy and try later.
                com.streamarc.tv.util.AppLog.w("Epg", "${service.name} guide: new listing is less complete than the kept one " +
                    "(${current.byId.size} channels); keeping the old copy")
                synchronized(guides) { guideFailedAt[service] = System.currentTimeMillis() }
                return current.byId.isNotEmpty()
            }
            val at = System.currentTimeMillis()
            synchronized(guides) { guides[service] = at to guide; guideFailedAt.remove(service) }
            appContext?.let { ctx -> withContext(Dispatchers.IO) { writeDisk(ctx, service, at, guide) } }
            true
        } catch (e: XtreamApi.GuideTooLarge) {
            // Caller decided the file is too big; don't retry the full download for a while.
            com.streamarc.tv.util.AppLog.w("Epg", "${service.name} guide: too large (${e.bytes / (1024 * 1024)} MB); using per-channel lookups")
            synchronized(guides) { guideFailedAt[service] = System.currentTimeMillis() + 6 * 60 * 60 * 1000L }
            guideLoaded(service)
        } catch (e: Exception) {
            com.streamarc.tv.util.AppLog.e("Epg", "${service.name} guide download failed after ${(System.currentTimeMillis() - started) / 1000}s", e)
            synchronized(guides) { guideFailedAt[service] = System.currentTimeMillis() }
            guideLoaded(service)
        }
    }

    private fun atLeastAsComplete(fresh: XmltvParser.Guide, old: XmltvParser.Guide): Boolean {
        val oldChannels = old.byId.size
        val oldProgrammes = old.byId.values.sumOf { it.size }
        val newChannels = fresh.byId.size
        val newProgrammes = fresh.byId.values.sumOf { it.size }
        return newChannels >= oldChannels * 0.7 && newProgrammes >= oldProgrammes * 0.5
    }

    private val bgScope = kotlinx.coroutines.CoroutineScope(kotlinx.coroutines.SupervisorJob() + Dispatchers.IO)

    private fun refreshInBackground(service: Service, account: Account) {
        synchronized(refreshing) { if (!refreshing.add(service)) return }
        val failed = synchronized(guides) { guideFailedAt[service] }
        if (failed != null && System.currentTimeMillis() - failed < RETRY_MS) { synchronized(refreshing) { refreshing.remove(service) }; return }
        bgScope.launch {
            try { guideMutex.withLock { download(service, account, null) } } finally { synchronized(refreshing) { refreshing.remove(service) } }
        }
    }

    // ---- Disk copy -----------------------------------------------------

    private class DiskGuide(val savedAt: Long, val byId: Map<String, List<EpgProgramme>>, val idByName: Map<String, String>)

    private fun guideFile(ctx: android.content.Context, service: Service) = java.io.File(ctx.cacheDir, "guide_${service.name}.json")

    private fun readDisk(ctx: android.content.Context, service: Service): Pair<Long, XmltvParser.Guide>? {
        val f = guideFile(ctx, service)
        if (!f.exists()) return null
        return try {
            f.bufferedReader().use { r ->
                val d = com.google.gson.Gson().fromJson(r, DiskGuide::class.java) ?: return null
                if (d.byId.isEmpty()) return null
                // Drop a copy whose programmes have all ended; it can't fill the grid anyway.
                val nowSec = System.currentTimeMillis() / 1000
                if (d.byId.values.none { list -> list.any { it.end > nowSec } }) { f.delete(); return null }
                d.savedAt to XmltvParser.Guide(d.byId, d.idByName)
            }
        } catch (_: Exception) {
            f.delete(); null
        }
    }

    private fun writeDisk(ctx: android.content.Context, service: Service, savedAt: Long, guide: XmltvParser.Guide) {
        try {
            val f = guideFile(ctx, service)
            val tmp = java.io.File(f.path + ".tmp")
            tmp.bufferedWriter().use { w -> com.google.gson.Gson().toJson(DiskGuide(savedAt, guide.byId, guide.idByName), w) }
            tmp.renameTo(f)
        } catch (_: Exception) {
        }
    }

    // ---- Per-channel short EPG (fallback for channels the guide doesn't cover) ----

    private fun key(service: Service, streamId: String) = "${service.name}/$streamId"

    /** Cached programmes if fresh, else null (no network). */
    fun peek(service: Service, streamId: String): List<EpgProgramme>? {
        val e = synchronized(cache) { cache[key(service, streamId)] } ?: return null
        return if (System.currentTimeMillis() - e.first < TTL_MS) e.second else null
    }

    // Some panels have no per-channel listings at all and answer every request with an empty
    // list. Asking for each of thousands of channels anyway is pure load on the panel (and can
    // get a customer rate-limited), so after a run of empty answers the lookups are paused.
    private const val EMPTY_STREAK_LIMIT = 40
    private const val SHORT_EPG_PAUSE_MS = 6 * 60 * 60 * 1000L
    private val emptyStreak = HashMap<Service, Int>()
    private val requestCount = HashMap<Service, Int>()

    /** True while per-channel lookups are paused for this service (the panel had nothing for any channel). */
    fun shortEpgPaused(service: Service): Boolean {
        val ctx = appContext ?: return false
        return Prefs(ctx).shortEpgOffUntil(service) > System.currentTimeMillis()
    }

    /** Programmes for the channel, fetching (at most 6 at a time) when not cached. */
    suspend fun get(service: Service, account: Account, streamId: String): List<EpgProgramme> {
        peek(service, streamId)?.let { return it }
        if (shortEpgPaused(service)) return emptyList()
        return gate.withPermit {
            peek(service, streamId)?.let { return@withPermit it }
            if (shortEpgPaused(service)) return@withPermit emptyList()
            val list = try {
                XtreamApi.shortEpg(service, account, streamId)
            } catch (_: Exception) {
                emptyList()
            }
            synchronized(emptyStreak) {
                val n = (requestCount[service] ?: 0) + 1
                requestCount[service] = n
                if (n % 500 == 0) com.streamarc.tv.util.AppLog.w("Epg", "${service.name}: $n per-channel programme requests this session")
                if (list.isEmpty()) {
                    val streak = (emptyStreak[service] ?: 0) + 1
                    emptyStreak[service] = streak
                    if (streak >= EMPTY_STREAK_LIMIT) {
                        appContext?.let { Prefs(it).setShortEpgOffUntil(service, System.currentTimeMillis() + SHORT_EPG_PAUSE_MS) }
                        emptyStreak[service] = 0
                        com.streamarc.tv.util.AppLog.w("Epg", "${service.name}: the panel returned no programme data for $EMPTY_STREAK_LIMIT channels in a row; " +
                            "per-channel lookups paused for ${SHORT_EPG_PAUSE_MS / 3_600_000} h")
                    }
                } else {
                    emptyStreak[service] = 0
                }
            }
            // A panel hiccup that returns nothing must not wipe programmes we already had.
            val previous = synchronized(cache) { cache[key(service, streamId)] }?.second
            val kept = if (list.isEmpty() && !previous.isNullOrEmpty()) previous else list
            synchronized(cache) { cache[key(service, streamId)] = System.currentTimeMillis() to kept }
            kept
        }
    }

    fun clear() = synchronized(cache) { cache.clear() }
}
