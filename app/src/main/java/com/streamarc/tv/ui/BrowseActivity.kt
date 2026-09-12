package com.streamarc.tv.ui

import android.content.Context
import android.content.Intent
import android.os.Bundle
import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import android.widget.Toast
import androidx.appcompat.app.AlertDialog
import androidx.appcompat.app.AppCompatActivity
import androidx.core.widget.doAfterTextChanged
import androidx.lifecycle.lifecycleScope
import androidx.recyclerview.widget.GridLayoutManager
import androidx.recyclerview.widget.LinearLayoutManager
import androidx.recyclerview.widget.RecyclerView
import com.bumptech.glide.Glide
import com.streamarc.tv.R
import com.streamarc.tv.data.Account
import com.streamarc.tv.data.CatalogCache
import com.streamarc.tv.data.Category
import com.streamarc.tv.data.ContentKind
import com.streamarc.tv.data.EpgCache
import com.streamarc.tv.data.EpgProgramme
import com.streamarc.tv.data.Format
import com.streamarc.tv.data.Prefs
import com.streamarc.tv.data.Service
import com.streamarc.tv.data.Stream
import com.streamarc.tv.data.XtreamApi
import com.streamarc.tv.databinding.ActivityBrowseBinding
import com.streamarc.tv.databinding.ItemCategoryBinding
import com.streamarc.tv.databinding.ItemCategoryChipBinding
import com.streamarc.tv.databinding.ItemStreamBinding
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch

/**
 * Category + content browser. For Live TV this is the TV guide: each channel
 * row shows what's on now (with progress) and next, and the panel above the
 * list details the focused channel. Holding OK on any item opens a context
 * menu with Play and Add/Remove favorites.
 */
@androidx.annotation.OptIn(androidx.media3.common.util.UnstableApi::class)
class BrowseActivity : AppCompatActivity() {

    private lateinit var b: ActivityBrowseBinding
    private lateinit var service: Service
    private lateinit var prefs: Prefs
    private lateinit var account: Account

    /** What is being browsed: LIVE for the guide, MOVIE or SERIES for the VOD tabs. */
    private var kind: ContentKind = ContentKind.LIVE
    private val isLive: Boolean get() = kind == ContentKind.LIVE

    private val picker = FolderPicker(this)
    private val categoryAdapter = CategoryAdapter { cat -> selectCategory(cat) }
    private val streamAdapter = StreamAdapter({ s -> play(s) }, { s -> showItemMenu(s) })

    private var allStreams: List<Stream> = emptyList()
    private val streamCache = HashMap<String?, List<Stream>>()
    private var loadJob: Job? = null
    private var selectedCategoryId: String? = null
    private var favoritesMode = false
    private var guideReady = false
    private var prefetchJob: Job? = null

    /** Small live preview in the guide panel. */
    private var preview: androidx.media3.exoplayer.ExoPlayer? = null
    private var previewStream: Stream? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        b = ActivityBrowseBinding.inflate(layoutInflater)
        setContentView(b.root)
        Edge.pad(b.root)
        prefs = Prefs(this)
        service = Service.valueOf(intent.getStringExtra(EXTRA_SERVICE) ?: Service.LIVE.name)

        val acct = prefs.account(service)
        if (acct == null) {
            startActivity(LoginActivity.intent(this, service))
            finish()
            return
        }
        account = acct
        kind = service.kind

        b.txtTitle.text = if (isLive) getString(R.string.tv_guide) else service.title
        b.btnBack.setOnClickListener { finish() }
        b.btnProfile.setOnClickListener { startActivity(ProfileActivity.intent(this, service)) }

        applyCategoryLayout()

        if (service.kind != ContentKind.LIVE) {
            b.btnTabMovies.setOnClickListener { switchKind(ContentKind.MOVIE) }
            b.btnTabSeries.setOnClickListener { switchKind(ContentKind.SERIES) }
            b.btnTabDownloads.setOnClickListener { startActivity(TransfersActivity.intent(this, com.streamarc.tv.transfer.TransferType.DOWNLOAD)) }
            renderTabs()
        }

        if (isLive) {
            b.panelEpg.visibility = View.VISIBLE
            b.txtPanelChannel.text = getString(R.string.tv_guide)
            b.txtPanelNow.text = ""
            b.txtPanelDesc.text = getString(R.string.hold_ok_hint)
            b.txtPanelUpcoming.text = ""
            b.listStreams.visibility = View.GONE
            b.epgGrid.visibility = View.VISIBLE
            b.epgGrid.guideLookup = { ch -> EpgCache.guideFor(service, ch.epgChannelId, ch.name) }
            showGuideProgress(getString(R.string.guide_downloading), null)
            lifecycleScope.launch {
                val ok = EpgCache.loadGuide(service, account)
                if (ok) b.epgGrid.guideLoaded()
                guideReady = true
                prefetchEpg()
            }
            b.epgGrid.listener = object : EpgGridView.Listener {
                override fun onFocusChanged(channel: Stream, programme: EpgProgramme?) {
                    showChannelDetails(channel, channel.streamId?.let { EpgCache.peek(service, it) }, programme)
                }
                override fun onChannelClick(channel: Stream) {
                    if (previewStream?.streamId != null && previewStream?.streamId == channel.streamId) play(channel)
                    else startPreview(channel)
                }
                override fun onChannelLongClick(channel: Stream) = showItemMenu(channel)
                override fun onNeedEpg(channel: Stream) {
                    val id = channel.streamId ?: return
                    lifecycleScope.launch {
                        // Prefer the whole-guide download; fall back to the per-channel call
                        // for channels the guide doesn't cover.
                        EpgCache.loadGuide(service, account)
                        val fromGuide = EpgCache.guideFor(service, channel.epgChannelId, channel.name)
                        val list = fromGuide ?: EpgCache.get(service, account, id)
                        b.epgGrid.setEpg(id, list)
                    }
                }
            }
        } else {
            b.panelEpg.visibility = View.GONE
            streamAdapter.grid = true
            streamAdapter.columns = resources.getInteger(R.integer.poster_columns)
            b.listStreams.layoutManager = GridLayoutManager(this, streamAdapter.columns)
            b.listStreams.adapter = streamAdapter
        }

        b.inputSearch.doAfterTextChanged { applyFilter() }

        loadCategories()
    }

    override fun onResume() {
        super.onResume()
        // If the user signed out from the profile screen, leave.
        if (!prefs.isSignedIn(service)) finish()
    }

    override fun onStart() {
        super.onStart()
        if (isLive) previewStream?.let { startPreview(it) }
    }

    override fun onStop() {
        super.onStop()
        releasePreview()
    }

    private fun startPreview(stream: Stream) {
        val url = try { XtreamApi.streamUrl(service, account, stream, prefs.liveFormat) } catch (_: Exception) { return }
        previewStream = stream
        val player = preview ?: buildPreviewPlayer().also { preview = it }
        b.previewPlayer.player = player
        b.previewPlayer.visibility = View.VISIBLE
        b.imgPanelLogo.visibility = View.GONE
        b.txtPreviewHint.visibility = View.VISIBLE
        player.setMediaItem(androidx.media3.common.MediaItem.fromUri(url))
        player.playWhenReady = true
        player.prepare()
    }

    private fun buildPreviewPlayer(): androidx.media3.exoplayer.ExoPlayer {
        val http = androidx.media3.datasource.DefaultHttpDataSource.Factory()
            .setUserAgent(XtreamApi.USER_AGENT)
            .setAllowCrossProtocolRedirects(true)
            .setConnectTimeoutMs(20_000)
            .setReadTimeoutMs(30_000)
        return androidx.media3.exoplayer.ExoPlayer.Builder(this)
            .setMediaSourceFactory(
                androidx.media3.exoplayer.source.DefaultMediaSourceFactory(this)
                    .setDataSourceFactory(androidx.media3.datasource.DefaultDataSource.Factory(this, http))
            )
            .build()
            .also { p ->
                p.addListener(object : androidx.media3.common.Player.Listener {
                    override fun onPlayerError(error: androidx.media3.common.PlaybackException) {
                        Toast.makeText(this@BrowseActivity, "Preview failed: ${error.errorCodeName}", Toast.LENGTH_SHORT).show()
                    }
                })
            }
    }

    private fun releasePreview() {
        b.previewPlayer.player = null
        preview?.release()
        preview = null
        b.previewPlayer.visibility = View.GONE
        b.txtPreviewHint.visibility = View.GONE
        b.imgPanelLogo.visibility = View.VISIBLE
    }

    /** Portrait phones get a horizontal chip row so the content gets the full width. */
    private fun compactCategories(): Boolean =
        resources.configuration.orientation == android.content.res.Configuration.ORIENTATION_PORTRAIT &&
            resources.configuration.smallestScreenWidthDp < 600

    private fun applyCategoryLayout() {
        val compact = compactCategories()
        categoryAdapter.chips = compact
        placeTabs(compact)
        b.listCategories.adapter = null
        b.listCategoriesTop.adapter = null
        if (compact) {
            b.listCategories.visibility = View.GONE
            b.listCategoriesTop.visibility = View.VISIBLE
            b.listCategoriesTop.layoutManager = LinearLayoutManager(this, LinearLayoutManager.HORIZONTAL, false)
            b.listCategoriesTop.adapter = categoryAdapter
        } else {
            b.listCategoriesTop.visibility = View.GONE
            b.listCategories.visibility = View.VISIBLE
            b.listCategories.layoutManager = LinearLayoutManager(this)
            b.listCategories.adapter = categoryAdapter
        }
    }

    /** Tabs live in the top bar on wide screens and on their own full-width row on portrait phones. */
    private fun placeTabs(compact: Boolean) {
        val showTabs = service.kind != ContentKind.LIVE
        val buttons = listOf(b.btnTabMovies, b.btnTabSeries, b.btnTabDownloads)
        val target: ViewGroup = if (compact) b.tabsRow else b.tabs
        for (btn in buttons) {
            (btn.parent as? ViewGroup)?.removeView(btn)
            val lp = if (compact) {
                android.widget.LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f)
            } else {
                android.widget.LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT)
            }
            if (btn !== b.btnTabMovies) lp.marginStart = (6 * resources.displayMetrics.density).toInt()
            btn.maxLines = 1
            target.addView(btn, lp)
        }
        b.tabs.visibility = if (showTabs && !compact) View.VISIBLE else View.GONE
        b.tabsRow.visibility = if (showTabs && compact) View.VISIBLE else View.GONE
        // The chip row already shows the category; the top-bar label is dropped on phones.
        b.txtCategory.visibility = if (compact) View.GONE else View.VISIBLE
    }

    /** Rotation is handled in place (see the manifest) so the category and guide position survive. */
    override fun onConfigurationChanged(newConfig: android.content.res.Configuration) {
        super.onConfigurationChanged(newConfig)
        applyCategoryLayout()
        if (!isLive) {
            streamAdapter.columns = resources.getInteger(R.integer.poster_columns)
            (b.listStreams.layoutManager as? GridLayoutManager)?.spanCount = streamAdapter.columns
            b.listStreams.post { streamAdapter.notifyDataSetChanged() }
        }
    }

    private fun renderTabs() {
        b.btnTabMovies.isSelected = kind == ContentKind.MOVIE
        b.btnTabSeries.isSelected = kind == ContentKind.SERIES
    }

    private fun switchKind(newKind: ContentKind) {
        if (newKind == kind) return
        kind = newKind
        renderTabs()
        loadJob?.cancel()
        streamCache.clear()
        allStreams = emptyList()
        b.inputSearch.setText("")
        streamAdapter.submit(emptyList(), emptySet())
        loadCategories()
    }

    // ---- Loading -------------------------------------------------------

    private fun loadCategories() {
        setLoading(true)
        lifecycleScope.launch {
            try {
                val fetched = XtreamApi.categories(service, account, kind)
                var cats = if (isLive) fetched.filter { it.id !in prefs.hiddenLiveCategories } else fetched
                if (!isLive) {
                    // Some panels file items under category ids the category list never mentions.
                    // Add those so their content is reachable.
                    val catalogue = CatalogCache.get(service, account, kind)
                    val known = cats.mapNotNull { it.id }.toSet()
                    val extra = catalogue.flatMap { it.allCategoryIds }.filter { it !in known }.distinct()
                    if (extra.isNotEmpty()) cats = cats + extra.map { Category(it, getString(R.string.category_num_fmt, it)) }
                }
                val all = if (isLive) {
                    listOf(
                        Category(FAV_ID, "★ " + getString(R.string.favorites)),
                        Category(null, getString(R.string.all_categories))
                    ) + cats
                } else {
                    listOf(
                        Category(FAV_ID, "★ " + getString(R.string.favorites)),
                        Category(RECENT_ID, getString(R.string.recently_added)),
                        Category(null, getString(R.string.all_categories))
                    ) + cats
                }
                categoryAdapter.submit(all)
                if (isLive) {
                    selectCategory(defaultLiveCategory(all))
                } else {
                    // Movies and series open on Recently added.
                    selectCategory(all[1])
                }
            } catch (e: Exception) {
                showError(e.message ?: getString(R.string.load_failed))
            }
        }
    }

    /** The category the guide opens on: the user's setting, else "General …" if the provider has one, else All. */
    private fun defaultLiveCategory(all: List<Category>): Category {
        val wanted = prefs.defaultLiveCategory
        when (wanted) {
            Prefs.CATEGORY_FAVORITES -> return all[0]
            Prefs.CATEGORY_ALL -> return all[1]
            null -> {}
            else -> all.firstOrNull { it.id == wanted }?.let { return it }
        }
        val provider = all.filter { it.id != null && it.id != FAV_ID }
        return provider.firstOrNull { it.name?.trim().equals("General Streams", ignoreCase = true) }
            ?: provider.firstOrNull { it.name?.contains("general streams", ignoreCase = true) == true }
            ?: provider.firstOrNull { it.name?.contains("general", ignoreCase = true) == true }
            ?: all[1]
    }

    private fun selectCategory(cat: Category) {
        favoritesMode = cat.id == FAV_ID
        val recentMode = cat.id == RECENT_ID
        selectedCategoryId = if (favoritesMode || recentMode) null else cat.id
        categoryAdapter.selectedId = cat.id
        b.txtCategory.text = cat.name
        loadJob?.cancel()
        setLoading(true)
        loadJob = lifecycleScope.launch {
            try {
                val key = selectedCategoryId
                allStreams = if (isLive) {
                    streamCache[key] ?: XtreamApi.streams(service, account, key, kind).also { streamCache[key] = it }
                } else {
                    // Movies/series: the whole catalogue is fetched once and filtered here.
                    val all = CatalogCache.get(service, account, kind)
                    when {
                        recentMode -> CatalogCache.recentlyAdded(all)
                        key == null -> all
                        else -> all.filter { key in it.allCategoryIds }
                    }
                }
                applyFilter()
                setLoading(false)
            } catch (e: Exception) {
                showError(e.message ?: getString(R.string.load_failed))
            }
        }
    }

    private fun applyFilter() {
        val q = b.inputSearch.text?.toString()?.trim().orEmpty()
        val favs = prefs.favorites(service, kind)
        var list = if (favoritesMode) allStreams.filter { favs.contains(it.id) } else allStreams
        if (isLive) {
            val hidden = prefs.hiddenLiveCategories
            if (hidden.isNotEmpty()) list = list.filter { it.categoryId !in hidden }
        }
        if (q.isNotEmpty()) list = list.filter { it.name?.contains(q, ignoreCase = true) == true }

        if (isLive) {
            b.epgGrid.favorites = favs
            b.epgGrid.setChannels(list)
            if (list.isNotEmpty() && currentFocus == null) b.epgGrid.requestFocus()
            if (guideReady) prefetchEpg()
        } else {
            streamAdapter.submit(list, favs)
        }

        b.txtEmpty.text = if (favoritesMode && favs.isEmpty()) getString(R.string.no_favorites_yet) else getString(R.string.nothing_here)
        b.txtEmpty.visibility = if (list.isEmpty()) View.VISIBLE else View.GONE
    }

    // ---- Actions -------------------------------------------------------

    private fun play(stream: Stream) {
        if (kind == ContentKind.SERIES) {
            val id = stream.seriesId ?: stream.streamId ?: return
            startActivity(SeriesActivity.intent(this, service, id, stream.name ?: "", stream.image, stream.plot))
            return
        }
        val url = try {
            XtreamApi.streamUrl(service, account, stream, prefs.liveFormat)
        } catch (e: Exception) {
            showError(e.message ?: getString(R.string.load_failed)); return
        }
        startActivity(PlayerActivity.intent(this, url, stream.name ?: "", isLive))
    }

    /** Context menu opened by holding OK / long-pressing an item. */
    private fun showItemMenu(stream: Stream) {
        val id = stream.id ?: return
        val isFav = prefs.isFavorite(service, kind, id)
        val favLabel = getString(if (isFav) R.string.remove_from_favorites else R.string.add_to_favorites)
        val first = getString(if (kind == ContentKind.SERIES) R.string.open else R.string.play)
        val third = when (kind) {
            ContentKind.LIVE -> getString(R.string.record_menu)
            ContentKind.MOVIE -> getString(R.string.download)
            ContentKind.SERIES -> getString(R.string.download_series)
        }
        AlertDialog.Builder(this)
            .setTitle(stream.name ?: "")
            .setItems(arrayOf(first, favLabel, third)) { _, which ->
                when (which) {
                    0 -> play(stream)
                    1 -> {
                        val nowFav = prefs.toggleFavorite(service, kind, id)
                        val msg = if (nowFav) R.string.added_to_favorites_fmt else R.string.removed_from_favorites_fmt
                        Toast.makeText(this, getString(msg, stream.name ?: ""), Toast.LENGTH_SHORT).show()
                        applyFilter()
                    }
                    2 -> when (kind) {
                        ContentKind.LIVE -> recordChannel(stream)
                        ContentKind.MOVIE -> downloadMovie(stream)
                        ContentKind.SERIES -> downloadSeries(stream)
                    }
                }
            }
            .setNegativeButton(android.R.string.cancel, null)
            .show()
    }

    private fun downloadMovie(stream: Stream) {
        val url = try { XtreamApi.streamUrl(service, account, stream, prefs.liveFormat) } catch (e: Exception) {
            Toast.makeText(this, e.message, Toast.LENGTH_LONG).show(); return
        }
        val ext = stream.containerExtension?.takeIf { it.isNotBlank() } ?: "mp4"
        TransferDialogs.download(this, picker, listOf(DownloadItem(stream.name ?: "Movie", getString(R.string.movies), url, ext)))
    }

    private fun downloadSeries(stream: Stream) {
        val seriesId = stream.seriesId ?: stream.streamId ?: return
        val name = stream.name ?: "Series"
        lifecycleScope.launch {
            val episodes = try { XtreamApi.seriesInfo(service, account, seriesId) } catch (e: Exception) {
                Toast.makeText(this@BrowseActivity, e.message ?: getString(R.string.load_failed), Toast.LENGTH_LONG).show(); return@launch
            }
            if (episodes.isEmpty()) { Toast.makeText(this@BrowseActivity, R.string.no_episodes, Toast.LENGTH_SHORT).show(); return@launch }
            AlertDialog.Builder(this@BrowseActivity)
                .setTitle(name)
                .setMessage(getString(R.string.download_series_confirm_fmt, episodes.size))
                .setPositiveButton(R.string.download_all) { _, _ ->
                    val items = episodes.map { ep ->
                        DownloadItem("$name S${ep.season}E${ep.number} ${ep.title}", name, XtreamApi.episodeUrl(service, account, ep), ep.containerExtension)
                    }
                    TransferDialogs.download(this@BrowseActivity, picker, items)
                }
                .setNegativeButton(android.R.string.cancel, null)
                .show()
        }
    }

    private fun recordChannel(stream: Stream) {
        // Recordings always use the MPEG-TS stream so the file plays back directly.
        val url = try { XtreamApi.streamUrl(service, account, stream, "ts") } catch (e: Exception) {
            Toast.makeText(this, e.message, Toast.LENGTH_LONG).show(); return
        }
        TransferDialogs.record(this, picker, stream.name ?: "Channel", url)
    }

    /**
     * Fill the guide for every channel in the current list ahead of scrolling: from the
     * downloaded listing when it covers the channel, otherwise one short-EPG call per channel.
     */
    private fun prefetchEpg() {
        prefetchJob?.cancel()
        val channels = allStreams
        prefetchJob = lifecycleScope.launch {
            val total = channels.size
            var done = 0
            var lastShown = -1L
            for (ch in channels) {
                done++
                val id = ch.streamId ?: continue
                if (EpgCache.peek(service, id) == null) {
                    val fromGuide = EpgCache.guideFor(service, ch.epgChannelId, ch.name)
                    val list = fromGuide ?: EpgCache.get(service, account, id)
                    b.epgGrid.setEpg(id, list)
                }
                val now = System.currentTimeMillis()
                if (now - lastShown > 150 || done == total) {
                    lastShown = now
                    showGuideProgress(getString(R.string.guide_filling_fmt, done, total), if (total > 0) done * 1000 / total else 0)
                }
            }
            hideGuideProgress()
        }
    }

    /** Thin bar under the guide panel: indeterminate while the listing downloads, then per-channel progress. */
    private fun showGuideProgress(text: String, progress: Int?) {
        if (!isLive) return
        b.guideLoading.visibility = View.VISIBLE
        b.txtGuideLoading.text = text
        if (progress == null) {
            b.progressGuide.isIndeterminate = true
        } else {
            b.progressGuide.isIndeterminate = false
            b.progressGuide.progress = progress.coerceIn(0, 1000)
        }
    }

    private fun hideGuideProgress() {
        b.guideLoading.visibility = View.GONE
    }

    private fun showChannelDetails(stream: Stream, programmes: List<EpgProgramme>?, focused: EpgProgramme? = null) {
        if (!isLive) return
        b.txtPanelChannel.text = stream.name ?: ""
        Glide.with(b.imgPanelLogo)
            .load(stream.icon?.takeIf { it.isNotBlank() })
            .placeholder(R.drawable.ic_placeholder)
            .error(R.drawable.ic_placeholder)
            .fitCenter()
            .into(b.imgPanelLogo)
        val now = System.currentTimeMillis() / 1000
        val list = programmes.orEmpty()
        val current = focused ?: list.firstOrNull { it.isOnNow(now) } ?: list.firstOrNull { it.end > now }
        if (current == null) {
            b.txtPanelNow.text = if (programmes == null) getString(R.string.loading_guide) else getString(R.string.no_programme_info)
            b.txtPanelDesc.text = getString(R.string.hold_ok_hint)
            b.txtPanelUpcoming.text = ""
            return
        }
        b.txtPanelNow.text = "${Format.timeRange(current.start, current.end)}   ${current.title}"
        b.txtPanelDesc.text = current.description.ifBlank { getString(R.string.hold_ok_hint) }
        b.txtPanelUpcoming.text = list.filter { it.start >= current.end }.take(2)
            .joinToString("   ·   ") { "${Format.time(it.start)}  ${it.title}" }
            .let { if (it.isBlank()) "" else getString(R.string.next_fmt, it) }
    }

    private fun setLoading(loading: Boolean) {
        b.progress.visibility = if (loading) View.VISIBLE else View.GONE
        b.txtError.visibility = View.GONE
        if (loading) b.txtEmpty.visibility = View.GONE
    }

    private fun showError(msg: String) {
        b.progress.visibility = View.GONE
        b.txtError.text = msg
        b.txtError.visibility = View.VISIBLE
    }

    // ---- Adapters ------------------------------------------------------

    private class CategoryAdapter(val onClick: (Category) -> Unit) :
        RecyclerView.Adapter<CategoryAdapter.VH>() {

        private var items: List<Category> = emptyList()
        var chips: Boolean = false
        var selectedId: String? = null
            set(value) { field = value; notifyDataSetChanged() }

        class VH(val root: View, val txtName: android.widget.TextView) : RecyclerView.ViewHolder(root)

        fun submit(list: List<Category>) { items = list; notifyDataSetChanged() }

        override fun getItemViewType(position: Int) = if (chips) 1 else 0

        override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): VH =
            if (viewType == 1) {
                val vb = ItemCategoryChipBinding.inflate(LayoutInflater.from(parent.context), parent, false)
                VH(vb.root, vb.txtName)
            } else {
                val vb = ItemCategoryBinding.inflate(LayoutInflater.from(parent.context), parent, false)
                VH(vb.root, vb.txtName)
            }

        override fun getItemCount() = items.size

        override fun onBindViewHolder(holder: VH, position: Int) {
            val c = items[position]
            holder.txtName.text = c.name ?: "—"
            holder.root.isSelected = c.id == selectedId
            holder.root.setOnClickListener { onClick(c) }
        }
    }

    /** Movie posters (grid) and plain lists. */
    private class StreamAdapter(val onClick: (Stream) -> Unit, val onLongClick: (Stream) -> Unit) :
        RecyclerView.Adapter<StreamAdapter.VH>() {

        private var items: List<Stream> = emptyList()
        private var favs: Set<String> = emptySet()
        var grid: Boolean = false

        class VH(val vb: ItemStreamBinding) : RecyclerView.ViewHolder(vb.root)

        fun submit(list: List<Stream>, favorites: Set<String>) { items = list; favs = favorites; notifyDataSetChanged() }

        private var recycler: RecyclerView? = null
        var columns: Int = 3

        override fun onAttachedToRecyclerView(recyclerView: RecyclerView) { recycler = recyclerView }
        override fun onDetachedFromRecyclerView(recyclerView: RecyclerView) { recycler = null }

        override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): VH {
            val vb = ItemStreamBinding.inflate(LayoutInflater.from(parent.context), parent, false)
            if (grid) {
                vb.root.orientation = android.widget.LinearLayout.VERTICAL
                vb.imgIcon.scaleType = android.widget.ImageView.ScaleType.FIT_CENTER
                vb.txtName.maxLines = 2
                vb.txtName.gravity = android.view.Gravity.CENTER_HORIZONTAL
            }
            return VH(vb)
        }

        /** Poster box at 2:3 from the column width so the whole poster shows without stretching. */
        private fun sizePoster(holder: VH) {
            if (!grid) return
            val rv = recycler ?: return
            val d = rv.resources.displayMetrics.density
            val usable = rv.width - rv.paddingLeft - rv.paddingRight
            if (usable <= 0) return
            val colW = usable / columns - (8 * d).toInt() * 2   // item margins + padding
            val h = (colW * 3) / 2
            val lp = holder.vb.imgIcon.layoutParams
            if (lp.width != ViewGroup.LayoutParams.MATCH_PARENT || lp.height != h) {
                lp.width = ViewGroup.LayoutParams.MATCH_PARENT
                lp.height = h
                holder.vb.imgIcon.layoutParams = lp
            }
        }

        override fun getItemCount() = items.size

        override fun onBindViewHolder(holder: VH, position: Int) {
            val s = items[position]
            sizePoster(holder)
            holder.vb.txtName.text = (if (favs.contains(s.id)) "★ " else "") + (s.name ?: "—")
            Glide.with(holder.vb.imgIcon)
                .load(s.image)
                .placeholder(R.drawable.ic_placeholder)
                .error(R.drawable.ic_placeholder)
                .fitCenter()
                .into(holder.vb.imgIcon)
            holder.vb.root.setOnClickListener { onClick(s) }
            holder.vb.root.setOnLongClickListener { onLongClick(s); true }
        }
    }

    companion object {
        private const val EXTRA_SERVICE = "service"
        private const val FAV_ID = Prefs.CATEGORY_FAVORITES
        private const val RECENT_ID = "__recent__"
        fun intent(ctx: Context, service: Service): Intent =
            Intent(ctx, BrowseActivity::class.java).putExtra(EXTRA_SERVICE, service.name)
    }
}
