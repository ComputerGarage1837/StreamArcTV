package com.streamarc.tv.data

import android.content.Context
import android.content.SharedPreferences
import com.google.gson.Gson
import com.google.gson.reflect.TypeToken

/**
 * Where you left off in movies, episodes and downloaded files, plus a watched flag.
 * Keyed by a stable id per item so the same title resumes from any screen.
 */
object WatchProgress {
    class Entry(
        var positionMs: Long = 0,
        var durationMs: Long = 0,
        var watched: Boolean = false,
        var updatedAt: Long = 0,
    ) {
        // What the item is, so Continue Watching / Next Episodes can show and play it without a catalogue lookup.
        var kind: String? = null       // "movie", "ep" or "dl"
        var title: String? = null      // movie title, series title, or download title
        var subtitle: String? = null   // episode title
        var image: String? = null
        var ext: String? = null
        var itemId: String? = null     // stream id, episode id or download job id
        var seriesId: String? = null
        var season: Int = 0
        var episode: Int = 0

        val remainingMs: Long get() = (durationMs - positionMs).coerceAtLeast(0L)
    }

    const val KIND_MOVIE = "movie"
    const val KIND_EPISODE = "ep"
    const val KIND_DOWNLOAD = "dl"

    private const val MAX_ENTRIES = 2000
    /** Within this of the end (or past 93%) counts as watched. */
    private const val END_SLACK_MS = 90_000L
    private const val RESUME_MIN_MS = 10_000L

    private lateinit var sp: SharedPreferences
    private val gson = Gson()
    private val type = TypeToken.getParameterized(HashMap::class.java, String::class.java, Entry::class.java).type
    @Volatile private var cache: HashMap<String, Entry>? = null

    fun init(context: Context) {
        sp = context.applicationContext.getSharedPreferences("streamarc_watch", Context.MODE_PRIVATE)
    }

    fun movieKey(service: Service, id: String) = "movie:${service.name}:$id"
    fun episodeKey(id: String) = "ep:$id"
    fun downloadKey(jobId: String) = "dl:$jobId"

    fun get(key: String): Entry? = synchronized(this) { map()[key] }

    /** Position to offer on resume, or 0 when there is nothing worth resuming. */
    fun resumePosition(key: String): Long {
        val e = get(key) ?: return 0L
        return if (!e.watched && e.positionMs >= RESUME_MIN_MS) e.positionMs else 0L
    }

    /** 0..1 progress for a partly watched item, 1 when watched, null when never played. */
    fun fraction(key: String): Float? {
        val e = get(key) ?: return null
        if (e.watched) return 1f
        if (e.durationMs <= 0 || e.positionMs < RESUME_MIN_MS) return null
        return (e.positionMs.toFloat() / e.durationMs).coerceIn(0f, 1f)
    }

    fun isWatched(key: String): Boolean = get(key)?.watched == true

    /** Records what an item is (called when it is opened) without touching its position. */
    fun describe(
        key: String, kind: String, title: String, image: String?, ext: String?, itemId: String,
        subtitle: String? = null, seriesId: String? = null, season: Int = 0, episode: Int = 0,
    ) = synchronized(this) {
        val m = map()
        val e = m[key] ?: Entry()
        e.kind = kind; e.title = title; e.subtitle = subtitle; e.image = image; e.ext = ext; e.itemId = itemId
        e.seriesId = seriesId; e.season = season; e.episode = episode
        if (e.updatedAt == 0L) e.updatedAt = System.currentTimeMillis()
        m[key] = e
        persist(m)
    }

    fun remove(key: String) = synchronized(this) { val m = map(); if (m.remove(key) != null) persist(m) }

    /** Partly watched items, most recent first. */
    fun continueWatching(limit: Int = 20): List<Pair<String, Entry>> = synchronized(this) {
        map().entries
            .filter { it.value.kind != null && !it.value.watched && it.value.positionMs >= RESUME_MIN_MS && it.value.durationMs > 0 }
            .sortedByDescending { it.value.updatedAt }
            .take(limit)
            .map { it.key to it.value }
    }

    /** Series that have been started: the most recently touched episode entry per series, newest first. */
    fun startedSeries(limit: Int = 12): List<Entry> = synchronized(this) {
        map().values
            .filter { it.kind == KIND_EPISODE && it.seriesId != null && (it.watched || it.positionMs >= RESUME_MIN_MS) }
            .groupBy { it.seriesId!! }
            .map { (_, list) -> list.maxByOrNull { it.updatedAt }!! }
            .sortedByDescending { it.updatedAt }
            .take(limit)
    }

    /** Records the current position; near the end it flips to watched and clears the position. */
    fun save(key: String, positionMs: Long, durationMs: Long) = synchronized(this) {
        val m = map()
        val e = m[key] ?: Entry()
        val nearEnd = durationMs > 0 && (positionMs >= durationMs * 0.93 || durationMs - positionMs <= END_SLACK_MS)
        if (nearEnd) { e.watched = true; e.positionMs = 0 } else { e.watched = false; e.positionMs = positionMs }
        e.durationMs = durationMs
        e.updatedAt = System.currentTimeMillis()
        m[key] = e
        persist(m)
    }

    fun setWatched(key: String, watched: Boolean) = synchronized(this) {
        val m = map()
        val e = m[key] ?: Entry()
        e.watched = watched
        e.positionMs = 0
        e.updatedAt = System.currentTimeMillis()
        m[key] = e
        persist(m)
    }

    fun ended(key: String) = setWatched(key, true)

    /** Drops the in-memory copy after a backup import so the next read comes from disk. */
    fun reload() = synchronized(this) { cache = null }

    private fun map(): HashMap<String, Entry> = cache ?: run {
        val json = sp.getString("entries", null)
        val m: HashMap<String, Entry> = try {
            if (json == null) HashMap() else gson.fromJson(json, type) ?: HashMap()
        } catch (_: Exception) { HashMap() }
        cache = m
        m
    }

    private fun persist(m: HashMap<String, Entry>) {
        if (m.size > MAX_ENTRIES) {
            val drop = m.entries.sortedBy { it.value.updatedAt }.take(m.size - MAX_ENTRIES).map { it.key }
            for (k in drop) m.remove(k)
        }
        sp.edit().putString("entries", gson.toJson(m, type)).apply()
    }
}
