package com.streamarc.tv.data

/** Episode lists per series, kept for half an hour so the VOD home and the player don't refetch them. */
object SeriesCache {
    private const val TTL_MS = 30 * 60 * 1000L
    private val cache = HashMap<String, Pair<Long, List<Episode>>>()

    suspend fun episodes(service: Service, account: Account, seriesId: String): List<Episode> {
        val now = System.currentTimeMillis()
        synchronized(cache) { cache[seriesId]?.let { if (now - it.first < TTL_MS) return it.second } }
        val eps = XtreamApi.seriesInfo(service, account, seriesId)
        synchronized(cache) { cache[seriesId] = now to eps }
        return eps
    }

    /** Episodes in viewing order: by season, then episode number. */
    fun ordered(eps: List<Episode>): List<Episode> = eps.sortedWith(compareBy({ it.season }, { it.number }))
}
