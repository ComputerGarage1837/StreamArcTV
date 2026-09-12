package com.streamarc.tv.ui

import android.Manifest
import android.os.Build
import android.view.LayoutInflater
import android.widget.Toast
import androidx.appcompat.app.AlertDialog
import androidx.appcompat.app.AppCompatActivity
import androidx.core.app.ActivityCompat
import androidx.core.content.ContextCompat
import com.streamarc.tv.R
import com.streamarc.tv.data.Prefs
import com.streamarc.tv.databinding.DialogRecordBinding
import com.streamarc.tv.transfer.Folders
import com.streamarc.tv.transfer.TransferJob
import com.streamarc.tv.transfer.TransferService
import com.streamarc.tv.transfer.TransferType
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.UUID

/** One thing to download: title for the list, URL and file extension. */
data class DownloadItem(val title: String, val subtitle: String, val url: String, val ext: String)

object TransferDialogs {

    /**
     * Confirms the folder (offering to change it or pick one the first time),
     * then queues every item. Falls back to the app folder on devices without a picker.
     */
    fun download(activity: AppCompatActivity, picker: FolderPicker, items: List<DownloadItem>) {
        if (items.isEmpty()) return
        val prefs = Prefs(activity)
        val current = prefs.downloadFolder?.takeIf { Folders.usable(activity, it) }
        if (current == null) {
            pickFolder(activity, picker, TransferType.DOWNLOAD) { folder -> enqueueDownloads(activity, items, folder) }
            return
        }
        val what = if (items.size == 1) items[0].title else activity.getString(R.string.items_fmt, items.size)
        AlertDialog.Builder(activity)
            .setTitle(activity.getString(R.string.download_where_fmt, what))
            .setMessage(activity.getString(R.string.folder_current_fmt, Folders.describe(activity, current, TransferType.DOWNLOAD)))
            .setPositiveButton(R.string.download_here) { _, _ -> enqueueDownloads(activity, items, current) }
            .setNeutralButton(R.string.choose_folder) { _, _ ->
                pickFolder(activity, picker, TransferType.DOWNLOAD) { folder -> enqueueDownloads(activity, items, folder) }
            }
            .setNegativeButton(android.R.string.cancel, null)
            .show()
    }

    /** Opens the picker, saves the choice for this type, and continues with it (null = app folder). */
    fun pickFolder(activity: AppCompatActivity, picker: FolderPicker, type: TransferType, then: (String?) -> Unit) {
        val prefs = Prefs(activity)
        val opened = picker.launch { uri ->
            if (uri != null) {
                val s = uri.toString()
                if (type == TransferType.RECORDING) prefs.recordingFolder = s else prefs.downloadFolder = s
                then(s)
            } else if ((if (type == TransferType.RECORDING) prefs.recordingFolder else prefs.downloadFolder) == null) {
                // Cancelled with nothing saved yet: use the app folder so the action still works.
                then(null)
            }
        }
        if (!opened) {
            Toast.makeText(activity, R.string.no_folder_picker, Toast.LENGTH_LONG).show()
            then(null)
        }
    }

    private fun enqueueDownloads(activity: AppCompatActivity, items: List<DownloadItem>, folder: String?) {
        ensureNotifications(activity)
        for (it in items) {
            val job = TransferJob(
                id = UUID.randomUUID().toString(),
                type = TransferType.DOWNLOAD,
                title = it.title,
                subtitle = it.subtitle,
                url = it.url,
                fileName = Folders.safeName(it.title) + "." + it.ext.ifBlank { "mp4" },
                folder = folder,
                startAt = 0, endAt = 0,
                createdAt = System.currentTimeMillis()
            )
            TransferService.enqueue(activity, job)
        }
        val msg = if (items.size == 1) activity.getString(R.string.download_started_fmt, items[0].title)
        else activity.getString(R.string.downloads_started_fmt, items.size)
        Toast.makeText(activity, msg, Toast.LENGTH_SHORT).show()
    }

    /** Start-time (5-minute steps) and duration pickers, then the folder step, then the schedule. */
    fun record(activity: AppCompatActivity, picker: FolderPicker, channelName: String, url: String) {
        val vb = DialogRecordBinding.inflate(LayoutInflater.from(activity))
        val startLabels = Array(288) { i -> if (i == 0) activity.getString(R.string.record_now) else offsetLabel(i * 5) }
        val durLabels = Array(72) { i -> offsetLabel((i + 1) * 5) }
        vb.pickStart.minValue = 0; vb.pickStart.maxValue = startLabels.size - 1; vb.pickStart.displayedValues = startLabels
        vb.pickStart.wrapSelectorWheel = false
        vb.pickDuration.minValue = 0; vb.pickDuration.maxValue = durLabels.size - 1; vb.pickDuration.displayedValues = durLabels
        vb.pickDuration.value = 11   // 60 minutes
        vb.pickDuration.wrapSelectorWheel = false
        val prefs = Prefs(activity)
        vb.txtFolder.text = activity.getString(
            R.string.folder_current_fmt,
            Folders.describe(activity, prefs.recordingFolder?.takeIf { Folders.usable(activity, it) }, TransferType.RECORDING)
        )
        val dialog = AlertDialog.Builder(activity)
            .setTitle(activity.getString(R.string.record_fmt, channelName))
            .setView(vb.root)
            .setPositiveButton(R.string.record) { _, _ ->
                val startIn = vb.pickStart.value * 5 * 60_000L
                val duration = (vb.pickDuration.value + 1) * 5 * 60_000L
                val current = prefs.recordingFolder?.takeIf { Folders.usable(activity, it) }
                if (current == null && prefs.recordingFolder == null) {
                    pickFolder(activity, picker, TransferType.RECORDING) { f -> schedule(activity, channelName, url, startIn, duration, f) }
                } else schedule(activity, channelName, url, startIn, duration, current)
            }
            .setNeutralButton(R.string.choose_folder, null)
            .setNegativeButton(android.R.string.cancel, null)
            .create()
        dialog.show()
        dialog.getButton(AlertDialog.BUTTON_NEUTRAL)?.setOnClickListener {
            pickFolder(activity, picker, TransferType.RECORDING) { f ->
                vb.txtFolder.text = activity.getString(R.string.folder_current_fmt, Folders.describe(activity, f, TransferType.RECORDING))
            }
        }
    }

    private fun schedule(activity: AppCompatActivity, channel: String, url: String, startIn: Long, duration: Long, folder: String?) {
        ensureNotifications(activity)
        val startAt = System.currentTimeMillis() + startIn
        val stamp = SimpleDateFormat("yyyy-MM-dd HH.mm", Locale.getDefault()).format(Date(startAt))
        val job = TransferJob(
            id = UUID.randomUUID().toString(),
            type = TransferType.RECORDING,
            title = channel,
            subtitle = "$stamp · ${offsetLabel((duration / 60_000).toInt())}",
            url = url,
            fileName = Folders.safeName("$channel $stamp") + ".ts",
            folder = folder,
            startAt = startAt,
            endAt = startAt + duration,
            createdAt = System.currentTimeMillis()
        )
        TransferService.enqueue(activity, job)
        val msg = if (startIn == 0L) activity.getString(R.string.recording_started_fmt, channel)
        else activity.getString(R.string.recording_scheduled_fmt, channel, SimpleDateFormat("h:mm a", Locale.getDefault()).format(Date(startAt)))
        Toast.makeText(activity, msg, Toast.LENGTH_LONG).show()
    }

    fun offsetLabel(minutes: Int): String {
        val h = minutes / 60; val m = minutes % 60
        return when {
            h == 0 -> "$m min"
            m == 0 -> "$h h"
            else -> "$h h $m min"
        }
    }

    private fun ensureNotifications(activity: AppCompatActivity) {
        if (Build.VERSION.SDK_INT >= 33 &&
            ContextCompat.checkSelfPermission(activity, Manifest.permission.POST_NOTIFICATIONS) != android.content.pm.PackageManager.PERMISSION_GRANTED
        ) {
            ActivityCompat.requestPermissions(activity, arrayOf(Manifest.permission.POST_NOTIFICATIONS), 71)
        }
    }
}
