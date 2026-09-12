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
        lifecycleScope.launch {
            while (isActive && lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED)) {
                delay(1000)
                refresh(keepScroll = true)
            }
        }
    }

    private fun renderFolder() {
        val prefs = Prefs(this)
        val folder = if (type == TransferType.RECORDING) prefs.recordingFolder else prefs.downloadFolder
        b.txtFolder.text = Folders.describe(this, folder?.takeIf { Folders.usable(this, it) }, type)
    }

    private fun refresh(keepScroll: Boolean = false) {
        val items = TransferStore.get(this).all().filter { it.type == type }.sortedByDescending { it.createdAt }
        adapter.submit(items, keepScroll)
        b.txtEmpty.text = getString(if (type == TransferType.RECORDING) R.string.recordings_empty else R.string.downloads_empty)
        b.txtEmpty.visibility = if (items.isEmpty()) View.VISIBLE else View.GONE
    }

    private fun play(j: TransferJob) {
        if (j.state != TransferState.DONE && j.state != TransferState.RUNNING) {
            Toast.makeText(this, R.string.download_not_ready, Toast.LENGTH_SHORT).show(); return
        }
        val uri = j.fileUri?.takeIf { Folders.exists(this, it) } ?: run {
            Toast.makeText(this, R.string.download_missing, Toast.LENGTH_SHORT).show(); return
        }
        startActivity(PlayerActivity.intent(this, uri, j.title, false))
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
            vb.txtSubtitle.text = listOf(j.subtitle, Folders.describe(this@TransfersActivity, j.folder, j.type)).filter { it.isNotBlank() }.joinToString("  ·  ")
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
                    } else if (j.total > 0) {
                        vb.progress.isIndeterminate = false; vb.progress.max = 1000; vb.progress.progress = (j.bytes * 1000 / j.total).toInt()
                        "${UpdateChecker.formatSize(j.bytes)} / ${UpdateChecker.formatSize(j.total)}"
                    } else {
                        vb.progress.isIndeterminate = true
                        UpdateChecker.formatSize(j.bytes)
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
