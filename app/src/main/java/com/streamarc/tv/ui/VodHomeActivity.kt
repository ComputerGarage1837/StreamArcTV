package com.streamarc.tv.ui

import android.content.Context
import android.content.Intent
import android.content.res.Configuration
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import android.widget.LinearLayout
import android.widget.Toast
import androidx.appcompat.app.AlertDialog
import androidx.appcompat.app.AppCompatActivity
import androidx.lifecycle.lifecycleScope
import androidx.recyclerview.widget.LinearLayoutManager
import androidx.recyclerview.widget.RecyclerView
import com.bumptech.glide.Glide
import com.streamarc.tv.R
import com.streamarc.tv.data.Account
import com.streamarc.tv.data.CatalogCache
import com.streamarc.tv.data.ContentKind
import com.streamarc.tv.data.Episode
import com.streamarc.tv.data.Prefs
import com.streamarc.tv.data.Service
import com.streamarc.tv.data.SeriesCache
import com.streamarc.tv.data.Stream
import com.streamarc.tv.data.WatchProgress
import com.streamarc.tv.data.XtreamApi
import com.streamarc.tv.databinding.ActivityVodHomeBinding
import com.streamarc.tv.databinding.ItemVodCardBinding
import com.streamarc.tv.databinding.ItemVodHeroBinding
import com.streamarc.tv.databinding.ItemVodRowBinding
import com.streamarc.tv.transfer.TransferStore
import com.streamarc.tv.transfer.TransferType
import com.streamarc.tv.update.UpdateChecker
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.launch

/**
 * Video on Demand home: a featured hero, Continue watching, Next episodes, My List, what's new,
 * and genre rows, in the style of a streaming service. Categories and search stay one tap away
 * (the Categories / Search entries open the full grid browser).
 */
class VodHomeActivity : AppCompatActivity() {

    private lateinit var b: ActivityVodHomeBinding
    private lateinit var prefs: Prefs
    private lateinit var service: Service
    private lateinit var account: Account

    private var movies: List<Stream> = emptyList()
    private var series: List<Stream> = emptyList()
    private var nextEpisodes: List<Card> = emptyList()
    private var featured: List<Stream> = emptyList()
    private var heroIndex = 0
    private var loaded = false

    private val rowsAdapter = RowsAdapter()
    private val handler = Handler(Looper.getMainLooper())
    private val heroTicker = object : Runnable {
        override fun run() {
            if (featured.size > 1) {
                heroIndex = (heroIndex + 1) % featured.size
                rowsAdapter.notifyItemChanged(0, "hero")
            }
            handler.postDelayed(this, HERO_INTERVAL_MS)
        }
    }

    // ---- Model ------------------------------------------------------------------

    class Card(
        val title: String,
        val subtitle: String?,
        val image: String?,
        val progress: Float?,
        val badge: String? = null,
        val onClick: () -> Unit,
        val onLongClick: (() -> Unit)? = null,
    )

    sealed class Row {
        object Hero : Row()
        class Cards(val title: String, val cards: List<Card>, val seeAll: (() -> Unit)?) : Row()
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        b = ActivityVodHomeBinding.inflate(layoutInflater)
        setContentView(b.root)
        Edge.pad(b.root)
        prefs = Prefs(this)
        service = Service.valueOf(intent.getStringExtra(EXTRA_SERVICE) ?: Service.VOD.name)
        val acct = prefs.account(service)
        if (acct == null) { startActivity(LoginActivity.intent(this, service)); finish(); return }
        account = acct

        b.header.txtBrand.text = getString(R.string.app_name)
        b.header.btnSettings.setOnClickListener { startActivity(Intent(this, SettingsActivity::class.java)) }
        b.header.btnUpdate.setOnClickListener { UpdateChecker.check(this, manual = true) }

        b.navHome.isSelected = true
        b.navHome.setOnClickListener { b.listRows.smoothScrollToPosition(0) }
        b.navSearch.setOnClickListener { startActivity(BrowseActivity.intent(this, service, ContentKind.MOVIE, search = true)) }
        b.navCategories.setOnClickListener { startActivity(BrowseActivity.intent(this, service, ContentKind.MOVIE)) }
        b.navDownloads.setOnClickListener { startActivity(TransfersActivity.intent(this, TransferType.DOWNLOAD)) }
        b.navProfile.setOnClickListener { startActivity(ProfileActivity.intent(this, service)) }

        b.listRows.layoutManager = LinearLayoutManager(this)
        b.listRows.adapter = rowsAdapter
        b.listRows.setHasFixedSize(false)
        applyNavLayout()
        render()
        load()
    }

    override fun onConfigurationChanged(newConfig: Configuration) {
        super.onConfigurationChanged(newConfig)
        applyNavLayout()
        rowsAdapter.notifyDataSetChanged()
    }

    override fun onResume() {
        super.onResume()
        if (!prefs.isSignedIn(service)) { finish(); return }
        if (loaded) { render(); loadNextEpisodes() }
        handler.removeCallbacks(heroTicker)
        handler.postDelayed(heroTicker, HERO_INTERVAL_MS)
    }

    override fun onPause() {
        super.onPause()
        handler.removeCallbacks(heroTicker)
    }

    /** Side rail in landscape, a row of tabs under the header when the phone is upright. */
    private fun applyNavLayout() {
        val land = resources.configuration.orientation == Configuration.ORIENTATION_LANDSCAPE
        val d = resources.displayMetrics.density
        b.body.orientation = if (land) LinearLayout.HORIZONTAL else LinearLayout.VERTICAL
        b.nav.orientation = if (land) LinearLayout.VERTICAL else LinearLayout.HORIZONTAL
        b.nav.layoutParams = LinearLayout.LayoutParams(
            if (land) (96 * d).toInt() else ViewGroup.LayoutParams.MATCH_PARENT,
            if (land) ViewGroup.LayoutParams.MATCH_PARENT else ViewGroup.LayoutParams.WRAP_CONTENT
        ).apply { topMargin = (6 * d).toInt() }
        // The content box takes the leftover space along the body's axis: width beside the
        // rail, height under the tab row.
        b.content.layoutParams = LinearLayout.LayoutParams(
            if (land) 0 else ViewGroup.LayoutParams.MATCH_PARENT,
            if (land) ViewGroup.LayoutParams.MATCH_PARENT else 0,
            1f
        )
        for (i in 0 until b.nav.childCount) {
            val item = b.nav.getChildAt(i)
            item.layoutParams = LinearLayout.LayoutParams(
                if (land) ViewGroup.LayoutParams.MATCH_PARENT else 0,
                ViewGroup.LayoutParams.WRAP_CONTENT,
                if (land) 0f else 1f
            ).apply { bottomMargin = (4 * d).toInt() }
        }
    }

    // ---- Loading ----------------------------------------------------------------

    private fun load() {
        b.progress.visibility = if (movies.isEmpty()) View.VISIBLE else View.GONE
        lifecycleScope.launch {
            try {
                movies = CatalogCache.get(this@VodHomeActivity, service, account, ContentKind.MOVIE)
                series = CatalogCache.get(this@VodHomeActivity, service, account, ContentKind.SERIES)
                loaded = true
                b.progress.visibility = View.GONE
                featured = (CatalogCache.recentlyAdded(movies, 40).filter { !it.image.isNullOrBlank() }.take(4) +
                    CatalogCache.recentlyAdded(series, 40).filter { !it.image.isNullOrBlank() }.take(2))
                render()
                loadNextEpisodes()
            } catch (e: Exception) {
                b.progress.visibility = View.GONE
                if (movies.isEmpty()) {
                    b.txtError.text = e.message ?: getString(R.string.load_failed)
                    b.txtError.visibility = View.VISIBLE
                }
            }
        }
    }

    /** Series you've started: fetch their episode lists (cached) and pick the next unwatched one. */
    private fun loadNextEpisodes() {
        val started = WatchProgress.startedSeries()
        if (started.isEmpty()) { nextEpisodes = emptyList(); return }
        lifecycleScope.launch {
            val cards = started.map { entry ->
                async(Dispatchers.IO) {
                    val sid = entry.seriesId ?: return@async null
                    val eps = try { SeriesCache.episodes(service, account, sid) } catch (_: Exception) { return@async null }
                    val sorted = SeriesCache.ordered(eps)
                    val idx = sorted.indexOfFirst { it.season == entry.season && it.number == entry.episode }
                    // Still mid-way through this one: it belongs in Continue watching instead.
                    if (idx >= 0 && !entry.watched) return@async null
                    val next = sorted.drop(idx + 1).firstOrNull { !WatchProgress.isWatched(WatchProgress.episodeKey(it.id)) }
                        ?: return@async null
                    val seriesTitle = entry.title ?: ""
                    Card(
                        title = seriesTitle,
                        subtitle = getString(R.string.season_episode_fmt, next.season, next.number) + "  ·  " + next.title,
                        image = entry.image,
                        progress = null,
                        badge = getString(R.string.next_episode),
                        onClick = { playEpisode(sid, seriesTitle, entry.image, next) },
                        onLongClick = { showSeriesMenu(sid, seriesTitle, entry.image) },
                    )
                }
            }.awaitAll().filterNotNull()
            nextEpisodes = cards
            render()
        }
    }

    // ---- Rows -------------------------------------------------------------------

    private fun render() {
        val rows = ArrayList<Row>()
        if (featured.isNotEmpty()) rows.add(Row.Hero)

        val cont = WatchProgress.continueWatching().map { (key, e) -> continueCard(key, e) }
        if (cont.isNotEmpty()) rows.add(Row.Cards(getString(R.string.continue_watching), cont, null))
        if (nextEpisodes.isNotEmpty()) rows.add(Row.Cards(getString(R.string.next_episodes), nextEpisodes, null))

        val movieById = movies.associateBy { it.id }
        val seriesById = series.associateBy { it.id }
        val myList = prefs.favorites(service, ContentKind.MOVIE).mapNotNull { movieById[it] }.map { movieCard(it) } +
            prefs.favorites(service, ContentKind.SERIES).mapNotNull { seriesById[it] }.map { seriesCard(it) }
        if (myList.isNotEmpty()) rows.add(Row.Cards(getString(R.string.my_list), myList, null))

        if (movies.isNotEmpty()) rows.add(Row.Cards(getString(R.string.new_movies), CatalogCache.recentlyAdded(movies, 30).map { movieCard(it) }) {
            startActivity(BrowseActivity.intent(this, service, ContentKind.MOVIE, BrowseActivity.RECENT_ID))
        })
        if (series.isNotEmpty()) rows.add(Row.Cards(getString(R.string.new_series), CatalogCache.recentlyAdded(series, 30).map { seriesCard(it) }) {
            startActivity(BrowseActivity.intent(this, service, ContentKind.SERIES, BrowseActivity.RECENT_ID))
        })

        for (g in topGenres(movies, 8)) {
            val items = CatalogCache.recentlyAdded(movies.filter { m -> m.genres.any { it.equals(g, true) } }, 30)
            rows.add(Row.Cards(getString(R.string.genre_movies_fmt, g), items.map { movieCard(it) }) {
                startActivity(BrowseActivity.intent(this, service, ContentKind.MOVIE, BrowseActivity.GENRE_PREFIX + g))
            })
        }
        for (g in topGenres(series, 5)) {
            val items = CatalogCache.recentlyAdded(series.filter { s -> s.genres.any { it.equals(g, true) } }, 30)
            rows.add(Row.Cards(getString(R.string.genre_series_fmt, g), items.map { seriesCard(it) }) {
                startActivity(BrowseActivity.intent(this, service, ContentKind.SERIES, BrowseActivity.GENRE_PREFIX + g))
            })
        }
        rowsAdapter.submit(rows)
    }

    private fun topGenres(items: List<Stream>, n: Int): List<String> {
        val counts = HashMap<String, Int>()
        for (s in items) for (g in s.genres) counts[g] = (counts[g] ?: 0) + 1
        return counts.entries.filter { it.value >= 5 }.sortedByDescending { it.value }.take(n).map { it.key }
    }

    private fun movieCard(s: Stream): Card {
        val key = s.streamId?.let { WatchProgress.movieKey(service, it) }
        val frac = key?.let { WatchProgress.fraction(it) }
        return Card(
            title = s.name ?: "", subtitle = s.genres.firstOrNull(), image = s.image,
            progress = frac?.takeIf { it < 1f }, badge = if (frac != null && frac >= 1f) "✓" else null,
            onClick = { playMovie(s) },
            onLongClick = { showMovieMenu(s) },
        )
    }

    private fun seriesCard(s: Stream): Card {
        val id = s.id ?: ""
        return Card(
            title = s.name ?: "", subtitle = s.genres.firstOrNull(), image = s.image, progress = null,
            onClick = { if (id.isNotEmpty()) openSeries(id, s.name ?: "", s.image, s.plot) },
            onLongClick = { if (id.isNotEmpty()) showSeriesMenu(id, s.name ?: "", s.image, s.plot) },
        )
    }

    private fun continueCard(key: String, e: WatchProgress.Entry): Card {
        val left = getString(R.string.left_fmt, duration(e.remainingMs))
        val sub = when (e.kind) {
            WatchProgress.KIND_EPISODE -> getString(R.string.season_episode_fmt, e.season, e.episode) + "  ·  " + left
            else -> left
        }
        return Card(
            title = e.title ?: "", subtitle = sub, image = e.image,
            progress = (e.positionMs.toFloat() / e.durationMs).coerceIn(0f, 1f),
            onClick = { resume(key, e) },
            onLongClick = { showContinueMenu(key, e) },
        )
    }

    // ---- Actions ----------------------------------------------------------------

    private fun playMovie(s: Stream) {
        val id = s.streamId ?: return
        val url = try { XtreamApi.streamUrl(service, account, s, prefs.liveFormat) } catch (e: Exception) {
            Toast.makeText(this, e.message, Toast.LENGTH_LONG).show(); return
        }
        val key = WatchProgress.movieKey(service, id)
        WatchProgress.describe(key, WatchProgress.KIND_MOVIE, s.name ?: "", s.image, s.containerExtension, id)
        startActivity(PlayerActivity.intent(this, url, s.name ?: "", false, key))
    }

    private fun playEpisode(seriesId: String, seriesTitle: String, image: String?, ep: Episode) {
        val url = try { XtreamApi.episodeUrl(service, account, ep) } catch (e: Exception) {
            Toast.makeText(this, e.message, Toast.LENGTH_LONG).show(); return
        }
        val key = WatchProgress.episodeKey(ep.id)
        WatchProgress.describe(key, WatchProgress.KIND_EPISODE, seriesTitle, image, ep.containerExtension, ep.id,
            subtitle = ep.title, seriesId = seriesId, season = ep.season, episode = ep.number)
        startActivity(PlayerActivity.intent(this, url, "$seriesTitle · ${com.streamarc.tv.data.Format.se(ep.season, ep.number)} ${ep.title}", false, key))
    }

    private fun openSeries(id: String, title: String, image: String?, plot: String?) {
        startActivity(SeriesActivity.intent(this, service, id, title, image, plot))
    }

    private fun resume(key: String, e: WatchProgress.Entry) {
        val id = e.itemId ?: return
        when (e.kind) {
            WatchProgress.KIND_MOVIE -> {
                val url = try { XtreamApi.movieUrl(service, account, id, e.ext) } catch (ex: Exception) {
                    Toast.makeText(this, ex.message, Toast.LENGTH_LONG).show(); return
                }
                startActivity(PlayerActivity.intent(this, url, e.title ?: "", false, key))
            }
            WatchProgress.KIND_EPISODE -> {
                val ep = Episode(id, e.subtitle ?: "", e.season, e.episode, e.ext ?: "mp4", null, null)
                val url = try { XtreamApi.episodeUrl(service, account, ep) } catch (ex: Exception) {
                    Toast.makeText(this, ex.message, Toast.LENGTH_LONG).show(); return
                }
                startActivity(PlayerActivity.intent(this, url, "${e.title} · ${com.streamarc.tv.data.Format.se(e.season, e.episode)} ${e.subtitle ?: ""}", false, key))
            }
            WatchProgress.KIND_DOWNLOAD -> {
                val job = TransferStore.get(this).get(id)
                val uri = job?.fileUri ?: run { Toast.makeText(this, R.string.download_missing, Toast.LENGTH_SHORT).show(); return }
                startActivity(PlayerActivity.intent(this, uri, e.title ?: "", false, key))
            }
        }
    }

    private fun showContinueMenu(key: String, e: WatchProgress.Entry) {
        val opts = arrayListOf(getString(R.string.play), getString(R.string.mark_watched), getString(R.string.remove_from_continue))
        if (e.kind == WatchProgress.KIND_EPISODE && e.seriesId != null) opts.add(getString(R.string.open_series))
        AlertDialog.Builder(this).setTitle(e.title ?: "")
            .setItems(opts.toTypedArray()) { _, which ->
                when (which) {
                    0 -> resume(key, e)
                    1 -> { WatchProgress.setWatched(key, true); render(); loadNextEpisodes() }
                    2 -> { WatchProgress.remove(key); render() }
                    3 -> openSeries(e.seriesId!!, e.title ?: "", e.image, null)
                }
            }
            .setNegativeButton(android.R.string.cancel, null).show()
    }

    private fun showMovieMenu(s: Stream) {
        val id = s.id ?: return
        val key = s.streamId?.let { WatchProgress.movieKey(service, it) }
        val fav = prefs.isFavorite(service, ContentKind.MOVIE, id)
        val opts = arrayListOf(
            getString(R.string.play),
            getString(if (fav) R.string.remove_from_favorites else R.string.add_to_favorites),
            getString(R.string.download),
        )
        if (key != null) opts.add(getString(if (WatchProgress.isWatched(key)) R.string.mark_unwatched else R.string.mark_watched))
        AlertDialog.Builder(this).setTitle(s.name ?: "")
            .setItems(opts.toTypedArray()) { _, which ->
                when (which) {
                    0 -> playMovie(s)
                    1 -> { prefs.toggleFavorite(service, ContentKind.MOVIE, id); render() }
                    2 -> {
                        val url = try { XtreamApi.streamUrl(service, account, s, prefs.liveFormat) } catch (_: Exception) { return@setItems }
                        TransferDialogs.download(this, picker, listOf(DownloadItem(s.name ?: "Movie", getString(R.string.movies), url, s.containerExtension ?: "mp4",
                            fileBase = com.streamarc.tv.transfer.Folders.movieFileName(s.name ?: "Movie"))))
                    }
                    3 -> { WatchProgress.setWatched(key!!, !WatchProgress.isWatched(key)); render() }
                }
            }
            .setNegativeButton(android.R.string.cancel, null).show()
    }

    private fun showSeriesMenu(id: String, title: String, image: String?, plot: String? = null) {
        val fav = prefs.isFavorite(service, ContentKind.SERIES, id)
        val opts = arrayOf(getString(R.string.open), getString(if (fav) R.string.remove_from_favorites else R.string.add_to_favorites))
        AlertDialog.Builder(this).setTitle(title)
            .setItems(opts) { _, which ->
                when (which) {
                    0 -> openSeries(id, title, image, plot)
                    1 -> { prefs.toggleFavorite(service, ContentKind.SERIES, id); render() }
                }
            }
            .setNegativeButton(android.R.string.cancel, null).show()
    }

    private val picker = FolderPicker(this)

    private fun duration(ms: Long): String {
        val m = (ms / 60_000).toInt()
        return if (m >= 60) "${m / 60}h ${m % 60}m" else "${m.coerceAtLeast(1)}m"
    }

    // ---- Adapters ---------------------------------------------------------------

    private inner class RowsAdapter : RecyclerView.Adapter<RecyclerView.ViewHolder>() {
        private var rows: List<Row> = emptyList()
        private val pool = RecyclerView.RecycledViewPool()

        fun submit(list: List<Row>) { rows = list; notifyDataSetChanged() }
        override fun getItemCount() = rows.size
        override fun getItemViewType(position: Int) = if (rows[position] is Row.Hero) 0 else 1

        inner class HeroVH(val vb: ItemVodHeroBinding) : RecyclerView.ViewHolder(vb.root)
        inner class RowVH(val vb: ItemVodRowBinding) : RecyclerView.ViewHolder(vb.root) {
            val cards = CardsAdapter()
            init {
                vb.listCards.layoutManager = LinearLayoutManager(vb.root.context, LinearLayoutManager.HORIZONTAL, false)
                vb.listCards.adapter = cards
                vb.listCards.setRecycledViewPool(pool)
            }
        }

        override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): RecyclerView.ViewHolder {
            val inf = LayoutInflater.from(parent.context)
            return if (viewType == 0) HeroVH(ItemVodHeroBinding.inflate(inf, parent, false))
            else RowVH(ItemVodRowBinding.inflate(inf, parent, false))
        }

        override fun onBindViewHolder(holder: RecyclerView.ViewHolder, position: Int) {
            when (val row = rows[position]) {
                is Row.Hero -> bindHero((holder as HeroVH).vb)
                is Row.Cards -> {
                    val vb = (holder as RowVH).vb
                    vb.txtRowTitle.text = row.title
                    vb.btnSeeAll.visibility = if (row.seeAll != null) View.VISIBLE else View.GONE
                    vb.btnSeeAll.setOnClickListener { row.seeAll?.invoke() }
                    holder.cards.submit(row.cards)
                }
            }
        }
    }

    private fun bindHero(vb: ItemVodHeroBinding) {
        val s = featured.getOrNull(heroIndex) ?: return
        // Never let the banner take more than ~40% of the rows area: the first row of posters
        // must be visible underneath without scrolling.
        val cap = (b.listRows.height * 0.40f).toInt()
        val wanted = resources.getDimensionPixelSize(R.dimen.vod_hero_height)
        if (cap > 0) vb.root.layoutParams = vb.root.layoutParams.apply { height = minOf(wanted, cap) }
        val isSeries = s.seriesId != null && s.streamId == null || series.any { it === s }
        val kind = if (isSeries) ContentKind.SERIES else ContentKind.MOVIE
        Glide.with(vb.imgBackdrop).load(s.image).centerCrop().into(vb.imgBackdrop)
        if (vb.imgPoster.layoutParams.width > 0) Glide.with(vb.imgPoster).load(s.image).fitCenter().into(vb.imgPoster)
        vb.txtHeroTitle.text = s.name ?: ""
        val meta = listOfNotNull(
            getString(if (isSeries) R.string.series else R.string.movie),
            s.genres.take(2).joinToString(", ").takeIf { it.isNotBlank() },
            s.rating?.trim()?.takeIf { it.isNotBlank() && it != "0" }?.let { "★ $it" },
        )
        vb.txtHeroMeta.text = meta.joinToString("   ·   ")
        vb.txtHeroPlot.text = s.plot?.trim().orEmpty()
        vb.txtHeroPlot.visibility = if (vb.txtHeroPlot.text.isBlank()) View.GONE else View.VISIBLE
        vb.btnHeroPlay.text = getString(if (isSeries) R.string.open_hero else R.string.play_hero)
        vb.btnHeroPlay.setOnClickListener { if (isSeries) openSeries(s.id ?: return@setOnClickListener, s.name ?: "", s.image, s.plot) else playMovie(s) }
        val fav = s.id?.let { prefs.isFavorite(service, kind, it) } == true
        vb.btnHeroList.text = getString(if (fav) R.string.in_my_list else R.string.add_my_list)
        vb.btnHeroList.setOnClickListener {
            val id = s.id ?: return@setOnClickListener
            prefs.toggleFavorite(service, kind, id)
            rowsAdapter.notifyItemChanged(0, "hero")
            render()
        }
        vb.dots.removeAllViews()
        val d = resources.displayMetrics.density
        for (i in featured.indices) {
            val dot = View(this)
            val size = (if (i == heroIndex) 18 else 7) * d
            dot.layoutParams = LinearLayout.LayoutParams(size.toInt(), (7 * d).toInt()).apply { marginEnd = (6 * d).toInt() }
            dot.background = androidx.core.content.ContextCompat.getDrawable(this, R.drawable.bg_dot)
            dot.alpha = if (i == heroIndex) 1f else 0.4f
            vb.dots.addView(dot)
        }
    }

    private inner class CardsAdapter : RecyclerView.Adapter<CardsAdapter.VH>() {
        private var items: List<Card> = emptyList()
        inner class VH(val vb: ItemVodCardBinding) : RecyclerView.ViewHolder(vb.root)
        fun submit(list: List<Card>) { items = list; notifyDataSetChanged() }
        override fun getItemCount() = items.size
        override fun onCreateViewHolder(parent: ViewGroup, viewType: Int) =
            VH(ItemVodCardBinding.inflate(LayoutInflater.from(parent.context), parent, false))

        override fun onBindViewHolder(holder: VH, position: Int) {
            val c = items[position]
            val vb = holder.vb
            vb.txtTitle.textSize = 12f
            vb.txtSub.textSize = 10.5f
            vb.txtTitle.text = c.title
            vb.txtSub.text = c.subtitle ?: ""
            vb.txtSub.visibility = if (c.subtitle.isNullOrBlank()) View.INVISIBLE else View.VISIBLE
            Glide.with(vb.imgPoster).load(c.image).placeholder(R.drawable.ic_placeholder).error(R.drawable.ic_placeholder).centerCrop().into(vb.imgPoster)
            vb.progressWatch.visibility = if (c.progress != null) View.VISIBLE else View.GONE
            if (c.progress != null) vb.progressWatch.progress = (c.progress * 1000).toInt()
            vb.txtBadge.visibility = if (c.badge != null) View.VISIBLE else View.GONE
            vb.txtBadge.text = c.badge ?: ""
            vb.txtBadge.isSelected = c.badge != "✓"
            vb.root.setOnClickListener { c.onClick() }
            vb.root.setOnLongClickListener { c.onLongClick?.invoke(); c.onLongClick != null }
        }
    }

    companion object {
        private const val EXTRA_SERVICE = "service"
        private const val HERO_INTERVAL_MS = 8_000L
        fun intent(ctx: Context, service: Service): Intent =
            Intent(ctx, VodHomeActivity::class.java).putExtra(EXTRA_SERVICE, service.name)
    }
}
