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
import com.streamarc.tv.databinding.ItemStreamBinding
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch

/**
 * Category + content browser. For Live TV this is the TV guide: each channel
 * row shows what's on now (with progress) and next, and the panel above the
 * list details the focused channel. Holding OK on any item opens a context
 * menu with Play and Add/Remove favorites.
 */
class BrowseActivity : AppCompatActivity() {

    private lateinit var b: ActivityBrowseBinding
    private lateinit var service: Service
    private lateinit var prefs: Prefs
    private lateinit var account: Account

    /** What is being browsed: LIVE for the guide, MOVIE or SERIES for the VOD tabs. */
    private var kind: ContentKind = ContentKind.LIVE
    private val isLive: Boolean get() = kind == ContentKind.LIVE

    private val categoryAdapter = CategoryAdapter { cat -> selectCategory(cat) }
    private val streamAdapter = StreamAdapter({ s -> play(s) }, { s -> showItemMenu(s) })

    private var allStreams: List<Stream> = emptyList()
    private val streamCache = HashMap<String?, List<Stream>>()
    private var loadJob: Job? = null
    private var selectedCategoryId: String? = null
    private var favoritesMode = false

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

        b.listCategories.layoutManager = LinearLayoutManager(this)
        b.listCategories.adapter = categoryAdapter

        if (service.kind != ContentKind.LIVE) {
            b.tabs.visibility = View.VISIBLE
            b.btnTabMovies.setOnClickListener { switchKind(ContentKind.MOVIE) }
            b.btnTabSeries.setOnClickListener { switchKind(ContentKind.SERIES) }
            renderTabs()
        }

        if (isLive) {
            b.panelEpg.visibility = View.VISIBLE
            b.txtPanelChannel.text = getString(R.string.loading_guide)
            b.txtPanelNow.text = ""
            b.txtPanelDesc.text = getString(R.string.hold_ok_hint)
            b.txtPanelUpcoming.text = ""
            b.listStreams.visibility = View.GONE
            b.epgGrid.visibility = View.VISIBLE
            b.epgGrid.guideLookup = { ch -> EpgCache.guideFor(service, ch.epgChannelId) }
            lifecycleScope.launch {
                val ok = EpgCache.loadGuide(service, account)
                if (ok) b.epgGrid.guideLoaded()
            }
            b.epgGrid.listener = object : EpgGridView.Listener {
                override fun onFocusChanged(channel: Stream, programme: EpgProgramme?) {
                    showChannelDetails(channel, channel.streamId?.let { EpgCache.peek(service, it) }, programme)
                }
                override fun onChannelClick(channel: Stream) = play(channel)
                override fun onChannelLongClick(channel: Stream) = showItemMenu(channel)
                override fun onNeedEpg(channel: Stream) {
                    val id = channel.streamId ?: return
                    lifecycleScope.launch {
                        // Prefer the whole-guide download; fall back to the per-channel call
                        // for channels the guide doesn't cover.
                        EpgCache.loadGuide(service, account)
                        val fromGuide = EpgCache.guideFor(service, channel.epgChannelId)
                        val list = fromGuide ?: EpgCache.get(service, account, id)
                        b.epgGrid.setEpg(id, list)
                    }
                }
            }
        } else {
            b.panelEpg.visibility = View.GONE
            streamAdapter.grid = true
            b.listStreams.layoutManager = GridLayoutManager(this, resources.getInteger(R.integer.poster_columns))
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
                val cats = XtreamApi.categories(service, account, kind)
                val all = listOf(
                    Category(FAV_ID, "★ " + getString(R.string.favorites)),
                    Category(null, getString(R.string.all_categories))
                ) + cats
                categoryAdapter.submit(all)
                // Open on Favorites when the user has some, otherwise on All.
                val start = if (prefs.favorites(service, kind).isNotEmpty()) all[0] else all[1]
                selectCategory(start)
            } catch (e: Exception) {
                showError(e.message ?: getString(R.string.load_failed))
            }
        }
    }

    private fun selectCategory(cat: Category) {
        favoritesMode = cat.id == FAV_ID
        selectedCategoryId = if (favoritesMode) null else cat.id
        categoryAdapter.selectedId = cat.id
        b.txtCategory.text = cat.name
        loadJob?.cancel()
        setLoading(true)
        loadJob = lifecycleScope.launch {
            try {
                val key = selectedCategoryId
                allStreams = streamCache[key] ?: XtreamApi.streams(service, account, key, kind).also { streamCache[key] = it }
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
        if (q.isNotEmpty()) list = list.filter { it.name?.contains(q, ignoreCase = true) == true }

        if (isLive) {
            b.epgGrid.favorites = favs
            b.epgGrid.setChannels(list)
            if (list.isNotEmpty() && currentFocus == null) b.epgGrid.requestFocus()
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
        AlertDialog.Builder(this)
            .setTitle(stream.name ?: "")
            .setItems(arrayOf(first, favLabel)) { _, which ->
                when (which) {
                    0 -> play(stream)
                    1 -> {
                        val nowFav = prefs.toggleFavorite(service, kind, id)
                        val msg = if (nowFav) R.string.added_to_favorites_fmt else R.string.removed_from_favorites_fmt
                        Toast.makeText(this, getString(msg, stream.name ?: ""), Toast.LENGTH_SHORT).show()
                        applyFilter()
                    }
                }
            }
            .setNegativeButton(android.R.string.cancel, null)
            .show()
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
        var selectedId: String? = null
            set(value) { field = value; notifyDataSetChanged() }

        class VH(val vb: ItemCategoryBinding) : RecyclerView.ViewHolder(vb.root)

        fun submit(list: List<Category>) { items = list; notifyDataSetChanged() }

        override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): VH =
            VH(ItemCategoryBinding.inflate(LayoutInflater.from(parent.context), parent, false))

        override fun getItemCount() = items.size

        override fun onBindViewHolder(holder: VH, position: Int) {
            val c = items[position]
            holder.vb.txtName.text = c.name ?: "—"
            holder.vb.root.isSelected = c.id == selectedId
            holder.vb.root.setOnClickListener { onClick(c) }
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

        override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): VH {
            val vb = ItemStreamBinding.inflate(LayoutInflater.from(parent.context), parent, false)
            if (grid) {
                vb.root.orientation = android.widget.LinearLayout.VERTICAL
                vb.imgIcon.layoutParams = vb.imgIcon.layoutParams.apply {
                    width = ViewGroup.LayoutParams.MATCH_PARENT
                    height = parent.resources.getDimensionPixelSize(R.dimen.poster_height)
                }
                vb.txtName.maxLines = 2
                vb.txtName.gravity = android.view.Gravity.CENTER_HORIZONTAL
            }
            return VH(vb)
        }

        override fun getItemCount() = items.size

        override fun onBindViewHolder(holder: VH, position: Int) {
            val s = items[position]
            holder.vb.txtName.text = (if (favs.contains(s.id)) "★ " else "") + (s.name ?: "—")
            Glide.with(holder.vb.imgIcon)
                .load(s.image)
                .placeholder(R.drawable.ic_placeholder)
                .error(R.drawable.ic_placeholder)
                .centerCrop()
                .into(holder.vb.imgIcon)
            holder.vb.root.setOnClickListener { onClick(s) }
            holder.vb.root.setOnLongClickListener { onLongClick(s); true }
        }
    }

    companion object {
        private const val EXTRA_SERVICE = "service"
        private const val FAV_ID = "__favorites__"
        fun intent(ctx: Context, service: Service): Intent =
            Intent(ctx, BrowseActivity::class.java).putExtra(EXTRA_SERVICE, service.name)
    }
}
