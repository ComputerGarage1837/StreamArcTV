package com.streamarc.tv.data

import com.google.gson.JsonObject
import com.google.gson.JsonParser
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import okhttp3.HttpUrl.Companion.toHttpUrl
import okhttp3.Request
import java.util.concurrent.ConcurrentHashMap

/**
 * The Movie Database: actor photos and "similar titles" for a movie or show. The provider only
 * sends actor names, so the title is looked up on TMDB and the photos come from there. Every
 * failure just means "nothing to show".
 */
object Tmdb {
    private const val KEY = "ecd110afb0b691daebc36389f6c8a0cf"
    private const val API = "https://api.themoviedb.org/3/"
    private const val IMG = "https://image.tmdb.org/t/p/w185"

    class Cast(val name: String, val character: String?, val photo: String?)
    class Info(val cast: List<Cast>, val similarTitles: List<String>)

    private val cache = ConcurrentHashMap<String, Info>()
    private val none = Info(emptyList(), emptyList())

    /** Drops the clutter providers put in names: "EN - Title (2019) 4K [Multi-Sub]". */
    fun cleanTitle(raw: String): String {
        var t = raw.replace(Regex("[\\[(].*?[\\])]"), " ")
        t = t.replace(Regex("^\\s*[A-Za-z]{2,3}\\s*[-:|]\\s+"), "")
        t = t.replace(Regex("\\b(4K|UHD|FHD|HD|SD|HDR|1080p|720p|2160p|MULTI|SUBS?)\\b", RegexOption.IGNORE_CASE), " ")
        return t.replace(Regex("\\s+"), " ").trim(' ', '-', '|', ':')
    }

    fun yearOf(raw: String, year: String?): String? =
        year?.take(4)?.takeIf { it.length == 4 && it.all(Char::isDigit) }
            ?: Regex("\\((\\d{4})\\)").find(raw)?.groupValues?.get(1)

    private fun get(url: okhttp3.HttpUrl): JsonObject? {
        val req = Request.Builder().url(url).header("Accept", "application/json").build()
        XtreamApi.client.newCall(req).execute().use { r ->
            if (!r.isSuccessful) return null
            return runCatching { JsonParser.parseString(r.body?.string() ?: "").asJsonObject }.getOrNull()
        }
    }

    private fun JsonObject.str(n: String): String? = get(n)?.takeIf { !it.isJsonNull }?.let { runCatching { it.asString }.getOrNull() }?.takeIf { it.isNotBlank() }

    suspend fun lookup(rawTitle: String, year: String?, series: Boolean): Info = withContext(Dispatchers.IO) {
        val title = cleanTitle(rawTitle)
        if (title.length < 2) return@withContext none
        val y = yearOf(rawTitle, year)
        val ck = "${if (series) "tv" else "movie"}|${title.lowercase()}|${y ?: ""}"
        cache[ck]?.let { return@withContext it }
        val info = try { fetch(title, y, series) } catch (_: Exception) { null }
        // Failures are not cached, so a later visit tries again.
        if (info == null) return@withContext none
        cache[ck] = info
        info
    }

    private fun fetch(title: String, year: String?, series: Boolean): Info? {
        val kind = if (series) "tv" else "movie"
        val search = API.toHttpUrl().newBuilder().addPathSegments("search/$kind")
            .addQueryParameter("api_key", KEY).addQueryParameter("query", title)
            .apply { if (year != null) addQueryParameter(if (series) "first_air_date_year" else "year", year) }.build()
        var results = get(search)?.getAsJsonArray("results")
        if ((results == null || results.size() == 0) && year != null) {
            results = get(search.newBuilder().removeAllQueryParameters(if (series) "first_air_date_year" else "year").build())?.getAsJsonArray("results")
        }
        val id = results?.firstOrNull()?.asJsonObject?.str("id") ?: return null
        val details = get(API.toHttpUrl().newBuilder().addPathSegments("$kind/$id")
            .addQueryParameter("api_key", KEY).addQueryParameter("append_to_response", "credits,recommendations,similar").build()) ?: return null
        val cast = details.getAsJsonObject("credits")?.getAsJsonArray("cast")?.mapNotNull { e ->
            val o = e.asJsonObject
            val n = o.str("name") ?: return@mapNotNull null
            Cast(n, o.str("character"), o.str("profile_path")?.let { IMG + it })
        }?.take(20) ?: emptyList()
        val titles = ArrayList<String>()
        for (section in listOf("recommendations", "similar")) {
            details.getAsJsonObject(section)?.getAsJsonArray("results")?.forEach { e ->
                val o = e.asJsonObject
                (o.str(if (series) "name" else "title"))?.let { titles.add(it) }
                (o.str(if (series) "original_name" else "original_title"))?.let { titles.add(it) }
            }
        }
        // Popular titles that share this one's genres (kids + animation, crime + drama, ...): catches the obvious
        // neighbours that TMDB's own short "similar" list leaves out.
        val genreIds = details.getAsJsonArray("genres")?.mapNotNull { it.asJsonObject.str("id") } ?: emptyList()
        if (genreIds.isNotEmpty()) {
            fun discover(ids: List<String>): List<String> {
                val out = ArrayList<String>()
                for (page in 1..3) {
                    val u = API.toHttpUrl().newBuilder().addPathSegments("discover/$kind")
                        .addQueryParameter("api_key", KEY).addQueryParameter("with_genres", ids.joinToString(","))
                        .addQueryParameter("sort_by", "popularity.desc").addQueryParameter("vote_count.gte", "30")
                        .addQueryParameter("include_adult", "false").addQueryParameter("page", page.toString()).build()
                    val r = get(u)?.getAsJsonArray("results") ?: break
                    if (r.size() == 0) break
                    r.forEach { e ->
                        val o = e.asJsonObject
                        o.str(if (series) "name" else "title")?.let { out.add(it) }
                        o.str(if (series) "original_name" else "original_title")?.let { out.add(it) }
                    }
                }
                return out
            }
            var found = discover(genreIds.take(3))
            if (found.size < 30 && genreIds.size > 2) found = discover(genreIds.take(2))
            titles.addAll(found)
        }
        return Info(cast, titles)
    }

    private fun norm(s: String) = cleanTitle(s).lowercase().replace(Regex("[^a-z0-9]+"), " ").trim()

    private val STOP = setOf("the", "a", "an", "and", "of", "in", "on", "for", "to", "part", "season", "movie", "show")

    /**
     * Titles from [catalogue] that TMDB lists as similar, so everything shown can be played here, followed by
     * other titles of the same franchise (same opening words). TMDB's own genre classification supplies the
     * popular neighbours; the provider's loose genre text is never used to pad the list.
     */
    fun similarIn(info: Info, catalogue: List<Stream>, selfId: String?, selfTitle: String, max: Int = 30): List<Stream> {
        val rank = HashMap<String, Int>()
        info.similarTitles.map { norm(it) }.filter { it.isNotEmpty() }.forEachIndexed { i, t -> rank.putIfAbsent(t, i) }
        val others = catalogue.filter { it.id != selfId && !it.name.isNullOrBlank() }
        val matched = others.mapNotNull { s -> rank[norm(s.name!!)]?.let { s to it } }
            .sortedBy { it.second }.map { it.first }.distinctBy { it.id }
        val words = norm(selfTitle).split(' ').filter { it.isNotEmpty() && it !in STOP }
        val lead = words.take(2).joinToString(" ")
        val franchise = if (words.isEmpty() || (words.size == 1 && words[0].length < 5)) emptyList() else {
            others.filter { s -> val n = norm(s.name!!).split(' ').filter { it !in STOP }.joinToString(" "); n.startsWith(lead) }
        }
        return (matched + franchise).distinctBy { it.id }.take(max)
    }
}
