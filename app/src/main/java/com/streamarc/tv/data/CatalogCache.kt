package com.streamarc.tv.data

import android.content.Context
import com.google.gson.Gson
import com.google.gson.reflect.TypeToken
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import java.io.File

/**
 * Whole-catalogue cache for movies and series. One network request per content
 * kind; the result is kept in memory and on disk, so the next launch shows the
 * catalogue instantly while a fresh copy is fetched in the background when the
 * saved one is older than 30 minutes.
 */
object CatalogCache {
    private const val TTL_MS = 30 * 60 * 1000L
    private val lists = HashMap<String, Pair<Long, List<Stream>>>()
    private val mutex = Mutex()
    private val gson = Gson()
    private val listType = TypeToken.getParameterized(List::class.java, Stream::class.java).type
    private val refreshScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val refreshing = HashSet<String>()

    private fun key(service: Service, kind: ContentKind) = "${service.name}_${kind.name}"
    private fun file(context: Context, k: String) = File(context.cacheDir, "catalog_$k.json")

    fun peek(service: Service, kind: ContentKind): List<Stream>? =
        synchronized(lists) { lists[key(service, kind)] }?.second

    suspend fun get(context: Context, service: Service, account: Account, kind: ContentKind): List<Stream> {
        val k = key(service, kind)
        val mem = synchronized(lists) { lists[k] }
        if (mem != null) {
            if (System.currentTimeMillis() - mem.first > TTL_MS) refreshInBackground(context, service, account, kind)
            return mem.second
        }
        return mutex.withLock {
            synchronized(lists) { lists[k] }?.let { return@withLock it.second }
            // Disk copy first: instant, then refreshed behind the scenes if stale.
            val disk = withContext(Dispatchers.IO) { readDisk(context, k) }
            if (disk != null) {
                synchronized(lists) { lists[k] = disk }
                if (System.currentTimeMillis() - disk.first > TTL_MS) refreshInBackground(context, service, account, kind)
                return@withLock disk.second
            }
            val all = XtreamApi.streams(service, account, null, kind)
            val now = System.currentTimeMillis()
            synchronized(lists) { lists[k] = now to all }
            withContext(Dispatchers.IO) { writeDisk(context, k, now, all) }
            all
        }
    }

    private fun refreshInBackground(context: Context, service: Service, account: Account, kind: ContentKind) {
        val k = key(service, kind)
        synchronized(refreshing) { if (!refreshing.add(k)) return }
        refreshScope.launch {
            try {
                val all = XtreamApi.streams(service, account, null, kind)
                val now = System.currentTimeMillis()
                synchronized(lists) { lists[k] = now to all }
                writeDisk(context.applicationContext, k, now, all)
            } catch (_: Exception) {
            } finally {
                synchronized(refreshing) { refreshing.remove(k) }
            }
        }
    }

    private class DiskEntry(val savedAt: Long, val items: List<Stream>)

    private fun readDisk(context: Context, k: String): Pair<Long, List<Stream>>? {
        val f = file(context, k)
        if (!f.exists()) return null
        return try {
            f.bufferedReader().use { r ->
                val e = gson.fromJson(r, DiskEntry::class.java) ?: return null
                e.savedAt to e.items
            }
        } catch (_: Exception) {
            f.delete(); null
        }
    }

    private fun writeDisk(context: Context, k: String, savedAt: Long, items: List<Stream>) {
        try {
            val f = file(context, k)
            val tmp = File(f.path + ".tmp")
            tmp.bufferedWriter().use { w -> gson.toJson(DiskEntry(savedAt, items), w) }
            tmp.renameTo(f)
        } catch (_: Exception) {
        }
    }

    fun clear(context: Context) {
        synchronized(lists) { lists.clear() }
        context.cacheDir.listFiles()?.filter { it.name.startsWith("catalog_") }?.forEach { it.delete() }
    }

    /** Newest first by the panel's "added" timestamp; items without one go last. */
    fun recentlyAdded(all: List<Stream>, limit: Int = 300): List<Stream> =
        all.sortedByDescending { it.added?.trim()?.toLongOrNull() ?: 0L }.take(limit)
}
