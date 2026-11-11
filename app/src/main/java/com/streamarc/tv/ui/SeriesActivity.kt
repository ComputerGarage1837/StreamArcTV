package com.streamarc.tv.ui

import android.content.Context
import android.content.Intent
import android.os.Bundle
import android.view.LayoutInflater
import android.view.View
import android.widget.Toast
import android.view.ViewGroup
import androidx.appcompat.app.AppCompatActivity
import androidx.lifecycle.lifecycleScope
import androidx.recyclerview.widget.LinearLayoutManager
import androidx.recyclerview.widget.RecyclerView
import com.bumptech.glide.Glide
import com.streamarc.tv.R
import com.streamarc.tv.data.Episode
import com.streamarc.tv.data.Prefs
import com.streamarc.tv.data.Service
import com.streamarc.tv.data.WatchProgress
import com.streamarc.tv.data.XtreamApi
import com.streamarc.tv.databinding.ActivitySeriesBinding
import com.streamarc.tv.databinding.ItemEpisodeBinding
import kotlinx.coroutines.launch

/** Seasons and episodes of one series; tapping an episode plays it. */
class SeriesActivity : AppCompatActivity() {

    private lateinit var b: ActivitySeriesBinding
    private lateinit var service: Service
    private val picker = FolderPicker(this)
    private var episodes: List<Episode> = emptyList()
    private val adapter = EpisodeAdapter({ ep -> play(ep) }, { ep -> askDownload(ep) }, { season -> downloadSeason(season) })

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        b = ActivitySeriesBinding.inflate(layoutInflater)
        setContentView(b.root)
        Edge.pad(b.root)
        service = Service.valueOf(intent.getStringExtra(EXTRA_SERVICE) ?: Service.VOD.name)
        val seriesId = intent.getStringExtra(EXTRA_ID) ?: run { finish(); return }
        val account = Prefs(this).account(service) ?: run { finish(); return }

        b.txtTitle.text = intent.getStringExtra(EXTRA_TITLE) ?: ""
        b.txtPlot.text = intent.getStringExtra(EXTRA_PLOT) ?: ""
        Glide.with(b.imgCover)
            .load(intent.getStringExtra(EXTRA_COVER)?.takeIf { it.isNotBlank() })
            .placeholder(R.drawable.ic_placeholder)
            .error(R.drawable.ic_placeholder)
            .centerCrop()
            .into(b.imgCover)
        b.btnBack.setOnClickListener { finish() }
        b.btnDownloadAll.setOnClickListener { downloadAll() }

        b.listEpisodes.layoutManager = LinearLayoutManager(this)
        b.listEpisodes.adapter = adapter
        val extras = ExtrasView(this)
        adapter.footer = extras

        b.progress.visibility = View.VISIBLE
        lifecycleScope.launch {
            try {
                val data = XtreamApi.seriesFull(service, account, seriesId)
                val episodes = data.episodes
                bindDetails(data.details)
                b.progress.visibility = View.GONE
                if (episodes.isEmpty()) {
                    b.txtError.text = getString(R.string.no_episodes)
                    b.txtError.visibility = View.VISIBLE
                } else {
                    this@SeriesActivity.episodes = episodes
                    adapter.submit(episodes)
                    loadExtras(extras, account, data.details?.genre, seriesId)
                    b.btnDownloadAll.visibility = View.VISIBLE
                    b.listEpisodes.post { b.listEpisodes.getChildAt(0)?.findViewById<View>(R.id.row)?.requestFocus() }
                }
            } catch (e: Exception) {
                b.progress.visibility = View.GONE
                b.txtError.text = com.streamarc.tv.data.Diagnose.explain(this@SeriesActivity, e)
                b.txtError.visibility = View.VISIBLE
            }
        }
    }

    /** Actor photos and similar shows (from TMDB), matched to what this provider actually has. */
    private fun loadExtras(extras: ExtrasView, account: com.streamarc.tv.data.Account, genre: String?, seriesId: String) {
        lifecycleScope.launch {
            val title = b.txtTitle.text.toString()
            val info = com.streamarc.tv.data.Tmdb.lookup(title, null, series = true)
            extras.showCast(info.cast)
            val catalogue = try { com.streamarc.tv.data.CatalogCache.getStored(this@SeriesActivity, service, com.streamarc.tv.data.ContentKind.SERIES) } catch (_: Exception) { emptyList() }
            val similar = com.streamarc.tv.data.Tmdb.similarIn(info, catalogue, seriesId, title)
            extras.showSimilar(similar) { s ->
                s.id?.let { startActivity(intent(this@SeriesActivity, service, it, s.name.orEmpty(), s.image, s.plot)) }
            }
            adapter.notifyDataSetChanged()
        }
    }

    /** Rating, genre, cast and trailer from the provider, when it has them. */
    private fun bindDetails(d: com.streamarc.tv.data.Details?) {
        d ?: return
        val facts = listOfNotNull(
            d.year,
            d.rating?.toDoubleOrNull()?.takeIf { it > 0 }?.let { "★ " + String.format("%.1f", it) },
            d.genre
        )
        if (facts.isNotEmpty()) { b.txtFacts.text = facts.joinToString("  ·  "); b.txtFacts.visibility = View.VISIBLE }
        if (b.txtPlot.text.isNullOrBlank() && !d.plot.isNullOrBlank()) b.txtPlot.text = d.plot
        d.cast?.let { b.txtCast.text = getString(R.string.cast_fmt, it); b.txtCast.visibility = View.VISIBLE }
        d.trailerUrl?.let { url -> b.btnTrailer.visibility = View.VISIBLE; b.btnTrailer.setOnClickListener { openTrailer(this, url) } }
    }

    private fun item(ep: Episode): DownloadItem? {
        val account = Prefs(this).account(service) ?: return null
        val url = try { XtreamApi.episodeUrl(service, account, ep) } catch (_: Exception) { return null }
        val show = b.txtTitle.text.toString()
        return DownloadItem("$show ${com.streamarc.tv.data.Format.se(ep.season, ep.number)} ${ep.title}", show, url, ep.containerExtension,
            fileBase = com.streamarc.tv.transfer.Folders.episodeFileName(show, ep.season, ep.number, ep.title))
    }

    override fun onResume() {
        super.onResume()
        adapter.notifyDataSetChanged()
    }

    private fun askDownload(ep: Episode) {
        FocusDialog(this)
            .setTitle(ep.title)
            .setItems(arrayOf(
                getString(R.string.play), getString(R.string.download), getString(R.string.download_season_fmt, ep.season),
                getString(if (WatchProgress.isWatched(WatchProgress.episodeKey(ep.id))) R.string.mark_unwatched else R.string.mark_watched)
            )) { _, which ->
                when (which) {
                    0 -> play(ep)
                    1 -> item(ep)?.let { TransferDialogs.download(this, picker, listOf(it)) }
                    2 -> downloadSeason(ep.season)
                    3 -> {
                        val k = WatchProgress.episodeKey(ep.id)
                        WatchProgress.setWatched(k, !WatchProgress.isWatched(k))
                        adapter.notifyDataSetChanged()
                    }
                }
            }
            .setNegativeButton(android.R.string.cancel, null)
            .show()
    }

    private fun downloadSeason(season: Int) {
        val eps = episodes.filter { it.season == season }
        if (eps.isEmpty()) return
        FocusDialog(this)
            .setTitle(getString(R.string.download_season_fmt, season))
            .setMessage(getString(R.string.download_season_confirm_fmt, eps.size, season))
            .setPositiveButton(R.string.download_here) { _, _ -> TransferDialogs.download(this, picker, eps.mapNotNull { item(it) }) }
            .setNegativeButton(android.R.string.cancel, null)
            .show()
    }

    /** Episodes not yet watched and not already downloaded (or downloading), in viewing order. */
    private fun unwatchedNotDownloaded(): List<Episode> {
        val jobs = com.streamarc.tv.transfer.TransferStore.get(this).all()
            .filter { it.state != com.streamarc.tv.transfer.TransferState.FAILED && it.state != com.streamarc.tv.transfer.TransferState.CANCELLED }
            .map { it.url }.toSet()
        return com.streamarc.tv.data.SeriesCache.ordered(episodes).filter { ep ->
            !WatchProgress.isWatched(WatchProgress.episodeKey(ep.id)) && item(ep)?.url !in jobs
        }
    }

    private fun downloadAll() {
        if (episodes.isEmpty()) return
        val pending = unwatchedNotDownloaded()
        val options = arrayOf(
            getString(R.string.download_next_n_fmt, 3), getString(R.string.download_next_n_fmt, 5), getString(R.string.download_next_n_fmt, 10),
            getString(R.string.download_all_unwatched_fmt, pending.size), getString(R.string.download_entire_series_fmt, episodes.size)
        )
        FocusDialog(this)
            .setTitle(b.txtTitle.text)
            .setItems(options) { _, which ->
                val chosen = when (which) {
                    0 -> pending.take(3); 1 -> pending.take(5); 2 -> pending.take(10); 3 -> pending; else -> episodes
                }
                if (chosen.isEmpty()) { Toast.makeText(this, R.string.nothing_to_download, Toast.LENGTH_SHORT).show(); return@setItems }
                TransferDialogs.download(this, picker, chosen.mapNotNull { item(it) })
            }
            .setNegativeButton(android.R.string.cancel, null)
            .show()
    }

    private fun play(ep: Episode) {
        val account = Prefs(this).account(service) ?: return
        val url = try {
            XtreamApi.episodeUrl(service, account, ep)
        } catch (e: Exception) {
            b.txtError.text = e.message; b.txtError.visibility = View.VISIBLE; return
        }
        val title = "${b.txtTitle.text} · ${com.streamarc.tv.data.Format.se(ep.season, ep.number)} ${ep.title}"
        WatchProgress.describe(
            WatchProgress.episodeKey(ep.id), WatchProgress.KIND_EPISODE, b.txtTitle.text.toString(),
            intent.getStringExtra(EXTRA_COVER), ep.containerExtension, ep.id,
            subtitle = ep.title, seriesId = intent.getStringExtra(EXTRA_ID), season = ep.season, episode = ep.number
        )
        startActivity(PlayerActivity.intent(this, url, title, false, WatchProgress.episodeKey(ep.id)))
    }

    private class EpisodeAdapter(val onClick: (Episode) -> Unit, val onLongClick: (Episode) -> Unit, val onSeason: (Int) -> Unit) : RecyclerView.Adapter<RecyclerView.ViewHolder>() {
        private var items: List<Episode> = emptyList()
        /** Cast and similar titles, shown after the last episode. */
        var footer: View? = null

        class VH(val vb: ItemEpisodeBinding) : RecyclerView.ViewHolder(vb.root)
        class FooterVH(v: View) : RecyclerView.ViewHolder(v)

        fun submit(list: List<Episode>) { items = list; notifyDataSetChanged() }

        override fun getItemViewType(position: Int) = if (position >= items.size) 1 else 0

        override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): RecyclerView.ViewHolder =
            if (viewType == 1) FooterVH(footer!!.also { (it.parent as? ViewGroup)?.removeView(it) })
            else VH(ItemEpisodeBinding.inflate(LayoutInflater.from(parent.context), parent, false))

        override fun getItemCount() = items.size + if (footer != null && items.isNotEmpty()) 1 else 0

        override fun onBindViewHolder(viewHolder: RecyclerView.ViewHolder, position: Int) {
            if (viewHolder !is VH) return
            val holder = viewHolder
            val ep = items[position]
            val ctx = holder.vb.root.context
            val firstOfSeason = position == 0 || items[position - 1].season != ep.season
            holder.vb.txtSeason.visibility = if (firstOfSeason) View.VISIBLE else View.GONE
            holder.vb.txtSeason.text = ctx.getString(R.string.season_fmt, ep.season)
            holder.vb.btnSeasonDownload.visibility = if (firstOfSeason) View.VISIBLE else View.GONE
            holder.vb.btnSeasonDownload.setOnClickListener { onSeason(ep.season) }
            val frac = WatchProgress.fraction(WatchProgress.episodeKey(ep.id))
            holder.vb.txtTitle.text = (if (frac != null && frac >= 1f) "✓ " else "") + ctx.getString(R.string.episode_fmt, com.streamarc.tv.data.Format.se(ep.season, ep.number), ep.title)
            holder.vb.progressWatch.visibility = if (frac != null) View.VISIBLE else View.GONE
            if (frac != null) holder.vb.progressWatch.progress = (frac * 1000).toInt()
            val info = listOfNotNull(ep.duration?.takeIf { it.isNotBlank() }, ep.plot?.takeIf { it.isNotBlank() }).joinToString("  ·  ")
            holder.vb.txtInfo.text = info
            holder.vb.txtInfo.visibility = if (info.isBlank()) View.GONE else View.VISIBLE
            holder.vb.row.setOnClickListener { onClick(ep) }
            holder.vb.row.setOnLongClickListener { onLongClick(ep); true }
        }
    }

    companion object {
        private const val EXTRA_SERVICE = "service"
        private const val EXTRA_ID = "id"
        private const val EXTRA_TITLE = "title"
        private const val EXTRA_COVER = "cover"
        private const val EXTRA_PLOT = "plot"
        fun intent(ctx: Context, service: Service, seriesId: String, title: String, cover: String?, plot: String?): Intent =
            Intent(ctx, SeriesActivity::class.java)
                .putExtra(EXTRA_SERVICE, service.name)
                .putExtra(EXTRA_ID, seriesId)
                .putExtra(EXTRA_TITLE, title)
                .putExtra(EXTRA_COVER, cover)
                .putExtra(EXTRA_PLOT, plot)
    }
}
