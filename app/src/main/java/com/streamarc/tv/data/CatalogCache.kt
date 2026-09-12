package com.streamarc.tv.data

import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock

/**
 * Whole-catalogue cache for movies and series: one request per content kind,
 * kept for 30 minutes, so categories filter instantly instead of loading one
 * by one.
 */
object CatalogCache {
    private const val TTL_MS = 30 * 60 * 1000L
    private val lists = HashMap<String, Pair<Long, List<Stream>>>()
    private val mutex = Mutex()

    private fun key(service: Service, kind: ContentKind) = "${service.name}/${kind.name}"

    fun peek(service: Service, kind: ContentKind): List<Stream>? {
        val e = synchronized(lists) { lists[key(service, kind)] } ?: return null
        return if (System.currentTimeMillis() - e.first < TTL_MS) e.second else null
    }

    suspend fun get(service: Service, account: Account, kind: ContentKind): List<Stream> {
        peek(service, kind)?.let { return it }
        return mutex.withLock {
            peek(service, kind)?.let { return@withLock it }
            val all = XtreamApi.streams(service, account, null, kind)
            synchronized(lists) { lists[key(service, kind)] = System.currentTimeMillis() to all }
            all
        }
    }

    fun clear() = synchronized(lists) { lists.clear() }

    /** Newest first by the panel's "added" timestamp; items without one go last. */
    fun recentlyAdded(all: List<Stream>, limit: Int = 300): List<Stream> =
        all.sortedByDescending { it.added?.trim()?.toLongOrNull() ?: 0L }.take(limit)
}
