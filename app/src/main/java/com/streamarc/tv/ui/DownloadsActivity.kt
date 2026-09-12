package com.streamarc.tv.ui

import android.app.DownloadManager
import android.os.Bundle
import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import android.widget.Toast
import androidx.appcompat.app.AlertDialog
import androidx.appcompat.app.AppCompatActivity
import androidx.lifecycle.lifecycleScope
import androidx.recyclerview.widget.LinearLayoutManager
import androidx.recyclerview.widget.RecyclerView
import com.streamarc.tv.R
import com.streamarc.tv.data.DownloadRecord
import com.streamarc.tv.data.DownloadStore
import com.streamarc.tv.data.Downloads
import com.streamarc.tv.databinding.ActivityDownloadsBinding
import com.streamarc.tv.databinding.ItemDownloadBinding
import com.streamarc.tv.update.UpdateChecker
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch

/** Every movie/episode download with progress, play and delete. */
class DownloadsActivity : AppCompatActivity() {

    private lateinit var b: ActivityDownloadsBinding
    private lateinit var store: DownloadStore
    private val adapter = DownloadAdapter({ play(it) }, { confirmDelete(it) })

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        b = ActivityDownloadsBinding.inflate(layoutInflater)
        setContentView(b.root)
        Edge.pad(b.root)
        store = DownloadStore(this)
        b.btnBack.setOnClickListener { finish() }
        b.list.layoutManager = LinearLayoutManager(this)
        b.list.adapter = adapter
    }

    override fun onResume() {
        super.onResume()
        refresh()
        lifecycleScope.launch {
            while (isActive) {
                delay(1000)
                if (lifecycle.currentState.isAtLeast(androidx.lifecycle.Lifecycle.State.RESUMED)) adapter.refreshStatuses()
            }
        }
    }

    private fun refresh() {
        val items = store.all().sortedByDescending { it.addedAt }
        adapter.submit(items)
        b.txtEmpty.visibility = if (items.isEmpty()) View.VISIBLE else View.GONE
    }

    private fun play(r: DownloadRecord) {
        val uri = Downloads.playableUri(this, r.downloadId)
        if (uri == null) {
            Toast.makeText(this, R.string.download_not_ready, Toast.LENGTH_SHORT).show()
            return
        }
        startActivity(PlayerActivity.intent(this, uri.toString(), r.title, false))
    }

    private fun confirmDelete(r: DownloadRecord) {
        AlertDialog.Builder(this)
            .setTitle(R.string.delete_download)
            .setMessage(getString(R.string.delete_download_confirm_fmt, r.title))
            .setPositiveButton(R.string.delete) { _, _ ->
                Downloads.delete(this, r.downloadId)
                refresh()
            }
            .setNegativeButton(android.R.string.cancel, null)
            .show()
    }

    private inner class DownloadAdapter(val onPlay: (DownloadRecord) -> Unit, val onDelete: (DownloadRecord) -> Unit) :
        RecyclerView.Adapter<DownloadVH>() {

        private var items: List<DownloadRecord> = emptyList()

        fun submit(list: List<DownloadRecord>) { items = list; notifyDataSetChanged() }
        fun refreshStatuses() { if (items.isNotEmpty()) notifyItemRangeChanged(0, items.size, Unit) }

        override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): DownloadVH =
            DownloadVH(ItemDownloadBinding.inflate(LayoutInflater.from(parent.context), parent, false))

        override fun getItemCount() = items.size

        override fun onBindViewHolder(holder: DownloadVH, position: Int) {
            val r = items[position]
            val vb = holder.vb
            vb.txtTitle.text = r.title
            vb.txtSubtitle.text = listOf(r.subtitle, Downloads.describeLocation(r.location)).filter { it.isNotBlank() }.joinToString("  ·  ")
            val st = Downloads.status(this@DownloadsActivity, r.downloadId)
            when (st?.state) {
                null -> { vb.txtStatus.text = getString(R.string.download_missing); vb.progress.visibility = View.GONE }
                DownloadManager.STATUS_SUCCESSFUL -> {
                    vb.txtStatus.text = getString(R.string.download_done_fmt, UpdateChecker.formatSize(st.total))
                    vb.progress.visibility = View.GONE
                }
                DownloadManager.STATUS_FAILED -> { vb.txtStatus.text = getString(R.string.download_failed_short); vb.progress.visibility = View.GONE }
                DownloadManager.STATUS_PAUSED -> {
                    vb.txtStatus.text = getString(R.string.download_paused)
                    vb.progress.visibility = View.VISIBLE
                    setProgress(vb, st.downloaded, st.total)
                }
                else -> {
                    vb.txtStatus.text = if (st.total > 0) "${UpdateChecker.formatSize(st.downloaded)} / ${UpdateChecker.formatSize(st.total)}"
                    else getString(R.string.download_starting)
                    vb.progress.visibility = View.VISIBLE
                    setProgress(vb, st.downloaded, st.total)
                }
            }
            val done = st?.state == DownloadManager.STATUS_SUCCESSFUL
            vb.btnPlay.isEnabled = done
            vb.btnPlay.alpha = if (done) 1f else 0.4f
            vb.btnPlay.setOnClickListener { onPlay(r) }
            vb.btnDelete.setOnClickListener { onDelete(r) }
            vb.root.setOnClickListener { if (done) onPlay(r) }
            vb.root.setOnLongClickListener { onDelete(r); true }
        }

        private fun setProgress(vb: ItemDownloadBinding, done: Long, total: Long) {
            vb.progress.isIndeterminate = total <= 0
            if (total > 0) { vb.progress.max = 1000; vb.progress.progress = ((done * 1000) / total).toInt().coerceIn(0, 1000) }
        }
    }
}

private class DownloadVH(val vb: ItemDownloadBinding) : RecyclerView.ViewHolder(vb.root)
