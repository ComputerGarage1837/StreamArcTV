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
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.lifecycleScope
import androidx.lifecycle.repeatOnLifecycle
import androidx.recyclerview.widget.LinearLayoutManager
import androidx.recyclerview.widget.RecyclerView
import com.streamarc.tv.R
import com.streamarc.tv.data.Format
import com.streamarc.tv.data.Prefs
import com.streamarc.tv.databinding.ActivityTransfersBinding
import com.streamarc.tv.databinding.ItemTransferBinding
import com.streamarc.tv.transfer.Folders
import com.streamarc.tv.transfer.TransferJob
import com.streamarc.tv.transfer.TransferService
import com.streamarc.tv.transfer.TransferState
import com.streamarc.tv.transfer.TransferStore
import com.streamarc.tv.transfer.TransferType
import com.streamarc.tv.update.UpdateChecker
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch

/** Downloads or Recordings list: progress, Play, Delete, and the folder setting. */
class TransfersActivity : AppCompatActivity() {

    private lateinit var b: ActivityTransfersBinding
    private lateinit var type: TransferType
    private val picker = FolderPicker(this)
    private val adapter = TransferAdapter({ play(it) }, { confirmDelete(it) })

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        b = ActivityTransfersBinding.inflate(layoutInflater)
        setContentView(b.root)
        Edge.pad(b.root)
        type = if (intent.getStringExtra(EXTRA_TYPE) == TransferType.RECORDING.name) TransferType.RECORDING else TransferType.DOWNLOAD
        b.txtTitle.text = getString(if (type == TransferType.RECORDING) R.string.recordings else R.string.nav_downloads)
        b.btnBack.setOnClickListener { finish() }
        b.btnDeleteAll.setOnClickListener { confirmDeleteAll() }
        b.list.layoutManager = LinearLayoutManager(this)
        b.list.adapter = adapter
        b.rowFolder.setOnClickListener {
            TransferDialogs.pickFolder(this, picker, type) { renderFolder() }
        }
        renderFolder()
    }

    override fun onResume() {
        super.onResume()
        refresh()
    }

    init {
        // Live progress: refresh twice a second for as long as the screen is in front.
        // (repeatOnLifecycle starts once RESUMED is actually reached and stops on pause.)
        lifecycleScope.launch {
            repeatOnLifecycle(Lifecycle.State.RESUMED) {
                while (isActive) {
                    delay(500)
                    refresh(keepScroll = true)
                }
            }
        }
    }

    /** Folder names come from the document provider, so look each one up once, not per row per refresh. */
    private val folderNames = HashMap<String?, String>()
    private fun folderName(folder: String?): String =
        folderNames.getOrPut(folder) { Folders.describe(this, folder, type) }

    private fun renderFolder() {
        val prefs = Prefs(this)
        val folder = if (type == TransferType.RECORDING) prefs.recordingFolder else prefs.downloadFolder
        folderNames.clear()
        b.txtFolder.text = folderName(folder?.takeIf { Folders.usable(this, it) })
    }

    private fun refresh(keepScroll: Boolean = false) {
        val all = TransferStore.get(this).all().filter { it.type == type }
        val rank = { j: TransferJob -> when (j.state) { TransferState.RUNNING -> 0; TransferState.QUEUED -> 1; TransferState.SCHEDULED -> 2; else -> 3 } }
        val items = all.sortedWith(compareBy<TransferJob> { rank(it) }.thenBy { if (it.state == TransferState.SCHEDULED) it.startAt else -it.createdAt })
        adapter.submit(items, keepScroll)
        renderNotice(all)
        b.txtEmpty.text = getString(if (type == TransferType.RECORDING) R.string.recordings_empty else R.string.downloads_empty)
        b.txtEmpty.visibility = if (items.isEmpty()) View.VISIBLE else View.GONE
        b.btnDeleteAll.visibility = if (items.isEmpty()) View.GONE else View.VISIBLE
    }

    private fun confirmDeleteAll() {
        val all = TransferStore.get(this).all().filter { it.type == type }
        if (all.isEmpty()) return
        val active = all.count { it.isActive }
        val what = getString(if (type == TransferType.RECORDING) R.string.recordings else R.string.nav_downloads)
        val msg = getString(R.string.delete_all_confirm_fmt, all.size, what.lowercase()) +
            if (active > 0) "\n\n" + getString(R.string.delete_all_active_fmt, active) else ""
        AlertDialog.Builder(this)
            .setTitle(R.string.delete_all)
            .setMessage(msg)
            .setPositiveButton(R.string.delete_all) { _, _ ->
                val store = TransferStore.get(this)
                for (j in all) {
                    if (j.isActive) TransferService.cancel(this, j.id)
                    Folders.delete(this, j.fileUri)
                    store.remove(j.id)
                }
                Toast.makeText(this, getString(R.string.deleted_all_fmt, all.size), Toast.LENGTH_SHORT).show()
                refresh()
            }
            .setNegativeButton(android.R.string.cancel, null)
            .show()
    }

    /** Recordings only: what is recording right now and what is scheduled, above the list. */
    private fun renderNotice(all: List<TransferJob>) {
        if (type != TransferType.RECORDING) { b.txtNotice.visibility = View.GONE; return }
        val running = all.filter { it.state == TransferState.RUNNING }
        val scheduled = all.filter { it.state == TransferState.SCHEDULED }.sortedBy { it.startAt }
        if (running.isEmpty() && scheduled.isEmpty()) { b.txtNotice.visibility = View.GONE; return }
        val day = java.text.SimpleDateFormat("EEE MMM d", java.util.Locale.getDefault())
        val today = day.format(java.util.Date())
        fun whenText(ms: Long): String {
            val d = day.format(java.util.Date(ms))
            return if (d == today) Format.time(ms / 1000) else "$d ${Format.time(ms / 1000)}"
        }
        val lines = ArrayList<String>()
        for (j in running) lines.add(getString(R.string.notice_recording_fmt, j.title, whenText(j.endAt)))
        for (j in scheduled) lines.add(getString(R.string.notice_scheduled_fmt, j.title, whenText(j.startAt), whenText(j.endAt)))
        b.txtNotice.text = lines.joinToString("\n")
        b.txtNotice.visibility = View.VISIBLE
    }

    private fun play(j: TransferJob) {
        if (j.state != TransferState.DONE && j.state != TransferState.RUNNING) {
            Toast.makeText(this, R.string.download_not_ready, Toast.LENGTH_SHORT).show(); return
        }
        val uri = j.fileUri?.takeIf { Folders.exists(this, it) } ?: run {
            Toast.makeText(this, R.string.download_missing, Toast.LENGTH_SHORT).show(); return
        }
        val key = com.streamarc.tv.data.WatchProgress.downloadKey(j.id)
        com.streamarc.tv.data.WatchProgress.describe(key, com.streamarc.tv.data.WatchProgress.KIND_DOWNLOAD, j.title, null, null, j.id, subtitle = j.subtitle)
        startActivity(PlayerActivity.intent(this, uri, j.title, false, key))
    }

    private fun confirmDelete(j: TransferJob) {
        val active = j.isActive
        AlertDialog.Builder(this)
            .setTitle(if (active) R.string.cancel_transfer else R.string.delete_download)
            .setMessage(getString(R.string.delete_download_confirm_fmt, j.title))
            .setPositiveButton(if (active) R.string.cancel_transfer else R.string.delete) { _, _ ->
                if (active) TransferService.cancel(this, j.id)
                Folders.delete(this, j.fileUri)
                TransferStore.get(this).remove(j.id)
                refresh()
            }
            .setNegativeButton(android.R.string.cancel, null)
            .show()
    }

    private inner class TransferAdapter(val onPlay: (TransferJob) -> Unit, val onDelete: (TransferJob) -> Unit) :
        RecyclerView.Adapter<TransferVH>() {

        private var items: List<TransferJob> = emptyList()

        fun submit(list: List<TransferJob>, keepScroll: Boolean) {
            val sameIds = list.map { it.id } == items.map { it.id }
            items = list
            if (sameIds && keepScroll) notifyItemRangeChanged(0, items.size, Unit) else notifyDataSetChanged()
        }

        override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): TransferVH =
            TransferVH(ItemTransferBinding.inflate(LayoutInflater.from(parent.context), parent, false))

        override fun getItemCount() = items.size

        override fun onBindViewHolder(holder: TransferVH, position: Int) {
            val j = items[position]
            val vb = holder.vb
            vb.txtTitle.text = j.title
            vb.txtSubtitle.text = listOf(j.subtitle, folderName(j.folder)).filter { it.isNotBlank() }.joinToString("  ·  ")
            vb.progress.visibility = View.GONE
            vb.txtStatus.text = when (j.state) {
                TransferState.SCHEDULED -> getString(R.string.scheduled_for_fmt, Format.time(j.startAt / 1000))
                TransferState.QUEUED -> getString(R.string.queued)
                TransferState.RUNNING -> {
                    vb.progress.visibility = View.VISIBLE
                    if (j.type == TransferType.RECORDING) {
                        val total = (j.endAt - j.startAt).coerceAtLeast(1)
                        val done = (System.currentTimeMillis() - j.startAt).coerceIn(0, total)
                        vb.progress.isIndeterminate = false; vb.progress.max = 1000; vb.progress.progress = (done * 1000 / total).toInt()
                        getString(R.string.recording_until_fmt, Format.time(j.endAt / 1000), UpdateChecker.formatSize(j.bytes))
                    } else {
                        val lp = TransferService.live[j.id]
                        val bytes = lp?.bytes ?: j.bytes
                        val total = lp?.total ?: j.total
                        val speed = lp?.bytesPerSec?.takeIf { it > 0 }?.let { "  ·  ${UpdateChecker.formatSize(it.toLong())}/s" } ?: ""
                        if (total > 0) {
                            vb.progress.isIndeterminate = false; vb.progress.max = 1000; vb.progress.progress = (bytes * 1000 / total).toInt()
                            "${bytes * 100 / total}%  ·  ${UpdateChecker.formatSize(bytes)} / ${UpdateChecker.formatSize(total)}$speed" +
                                (j.error?.let { "\n$it" } ?: "")
                        } else {
                            vb.progress.isIndeterminate = true
                            UpdateChecker.formatSize(bytes) + speed + (j.error?.let { "\n$it" } ?: "")
                        }
                    }
                }
                TransferState.DONE -> getString(R.string.download_done_fmt, UpdateChecker.formatSize(j.bytes))
                TransferState.FAILED -> getString(R.string.download_failed_short) + (j.error?.let { ": $it" } ?: "")
                TransferState.CANCELLED -> getString(R.string.cancelled)
            }
            val playable = j.state == TransferState.DONE
            vb.btnPlay.isEnabled = playable
            vb.btnPlay.alpha = if (playable) 1f else 0.4f
            vb.btnDelete.text = getString(if (j.isActive) R.string.cancel_transfer else R.string.delete)
            vb.btnPlay.setOnClickListener { onPlay(j) }
            vb.btnDelete.setOnClickListener { onDelete(j) }
            vb.root.setOnClickListener { if (playable) onPlay(j) }
            vb.root.setOnLongClickListener { onDelete(j); true }
        }
    }

    companion object {
        private const val EXTRA_TYPE = "type"
        fun intent(ctx: Context, type: TransferType): Intent =
            Intent(ctx, TransfersActivity::class.java).putExtra(EXTRA_TYPE, type.name)
    }
}

private class TransferVH(val vb: ItemTransferBinding) : RecyclerView.ViewHolder(vb.root)
