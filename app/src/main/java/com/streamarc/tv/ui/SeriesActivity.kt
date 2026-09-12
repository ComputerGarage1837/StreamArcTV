package com.streamarc.tv.ui

import android.content.Context
import android.content.Intent
import android.os.Bundle
import android.view.LayoutInflater
import android.view.View
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
import com.streamarc.tv.data.XtreamApi
import com.streamarc.tv.databinding.ActivitySeriesBinding
import com.streamarc.tv.databinding.ItemEpisodeBinding
import kotlinx.coroutines.launch

/** Seasons and episodes of one series; tapping an episode plays it. */
class SeriesActivity : AppCompatActivity() {

    private lateinit var b: ActivitySeriesBinding
    private lateinit var service: Service
    private val adapter = EpisodeAdapter { ep -> play(ep) }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        b = ActivitySeriesBinding.inflate(layoutInflater)
        setContentView(b.root)
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

        b.listEpisodes.layoutManager = LinearLayoutManager(this)
        b.listEpisodes.adapter = adapter

        b.progress.visibility = View.VISIBLE
        lifecycleScope.launch {
            try {
                val episodes = XtreamApi.seriesInfo(service, account, seriesId)
                b.progress.visibility = View.GONE
                if (episodes.isEmpty()) {
                    b.txtError.text = getString(R.string.no_episodes)
                    b.txtError.visibility = View.VISIBLE
                } else {
                    adapter.submit(episodes)
                    b.listEpisodes.post { b.listEpisodes.getChildAt(0)?.findViewById<View>(R.id.row)?.requestFocus() }
                }
            } catch (e: Exception) {
                b.progress.visibility = View.GONE
                b.txtError.text = e.message ?: getString(R.string.load_failed)
                b.txtError.visibility = View.VISIBLE
            }
        }
    }

    private fun play(ep: Episode) {
        val account = Prefs(this).account(service) ?: return
        val url = try {
            XtreamApi.episodeUrl(service, account, ep)
        } catch (e: Exception) {
            b.txtError.text = e.message; b.txtError.visibility = View.VISIBLE; return
        }
        val title = "${b.txtTitle.text} · S${ep.season}E${ep.number} ${ep.title}"
        startActivity(PlayerActivity.intent(this, url, title, false))
    }

    private class EpisodeAdapter(val onClick: (Episode) -> Unit) : RecyclerView.Adapter<EpisodeAdapter.VH>() {
        private var items: List<Episode> = emptyList()

        class VH(val vb: ItemEpisodeBinding) : RecyclerView.ViewHolder(vb.root)

        fun submit(list: List<Episode>) { items = list; notifyDataSetChanged() }

        override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): VH =
            VH(ItemEpisodeBinding.inflate(LayoutInflater.from(parent.context), parent, false))

        override fun getItemCount() = items.size

        override fun onBindViewHolder(holder: VH, position: Int) {
            val ep = items[position]
            val ctx = holder.vb.root.context
            val firstOfSeason = position == 0 || items[position - 1].season != ep.season
            holder.vb.txtSeason.visibility = if (firstOfSeason) View.VISIBLE else View.GONE
            holder.vb.txtSeason.text = ctx.getString(R.string.season_fmt, ep.season)
            holder.vb.txtTitle.text = ctx.getString(R.string.episode_fmt, ep.number, ep.title)
            val info = listOfNotNull(ep.duration?.takeIf { it.isNotBlank() }, ep.plot?.takeIf { it.isNotBlank() }).joinToString("  ·  ")
            holder.vb.txtInfo.text = info
            holder.vb.txtInfo.visibility = if (info.isBlank()) View.GONE else View.VISIBLE
            holder.vb.row.setOnClickListener { onClick(ep) }
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
