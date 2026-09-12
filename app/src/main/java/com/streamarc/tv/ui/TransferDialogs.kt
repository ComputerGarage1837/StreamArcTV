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

    /** Start and end clock times (to the minute) on hour / minute / AM-PM wheels, then folder and schedule. */
    fun record(activity: AppCompatActivity, picker: FolderPicker, channelName: String, url: String) {
        val vb = DialogRecordBinding.inflate(LayoutInflater.from(activity))
        val now = java.util.Calendar.getInstance()
        val startCal = (now.clone() as java.util.Calendar).apply { add(java.util.Calendar.MINUTE, 1) }
        val endCal = (startCal.clone() as java.util.Calendar).apply { add(java.util.Calendar.HOUR_OF_DAY, 1) }

        val start = TimeWheels(vb.startHour, vb.startMinute, vb.startAmPm)
        val end = TimeWheels(vb.endHour, vb.endMinute, vb.endAmPm)
        start.set(startCal.get(java.util.Calendar.HOUR_OF_DAY), startCal.get(java.util.Calendar.MINUTE))
        end.set(endCal.get(java.util.Calendar.HOUR_OF_DAY), endCal.get(java.util.Calendar.MINUTE))

        fun times(): Pair<Long, Long> = resolve(start.hour24(), start.minute(), end.hour24(), end.minute())
        fun summarize() {
            val (st, en) = times()
            val day = SimpleDateFormat("EEE MMM d", Locale.getDefault())
            val t = SimpleDateFormat("h:mm a", Locale.getDefault())
            val sameDay = day.format(Date(st)) == day.format(Date(en))
            vb.txtSummary.text = if (sameDay) "${day.format(Date(st))}   ${t.format(Date(st))} – ${t.format(Date(en))}"
            else "${day.format(Date(st))} ${t.format(Date(st))} – ${day.format(Date(en))} ${t.format(Date(en))}"
        }
        start.onChange = { summarize() }
        end.onChange = { summarize() }
        summarize()

        val prefs = Prefs(activity)
        vb.txtFolder.text = activity.getString(
            R.string.folder_current_fmt,
            Folders.describe(activity, prefs.recordingFolder?.takeIf { Folders.usable(activity, it) }, TransferType.RECORDING)
        )
        val dialog = AlertDialog.Builder(activity)
            .setTitle(activity.getString(R.string.record_fmt, channelName))
            .setView(vb.root)
            .setPositiveButton(R.string.record) { _, _ ->
                val (st, en) = times()
                val current = prefs.recordingFolder?.takeIf { Folders.usable(activity, it) }
                if (current == null && prefs.recordingFolder == null) {
                    pickFolder(activity, picker, TransferType.RECORDING) { f -> schedule(activity, channelName, url, st, en, f) }
                } else schedule(activity, channelName, url, st, en, current)
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
        vb.startHour.requestFocus()
    }

    /** Hour (1–12), minute (00–59) and AM/PM wheels that work by touch and with a remote. */
    private class TimeWheels(val hour: android.widget.NumberPicker, val minute: android.widget.NumberPicker, val ampm: android.widget.NumberPicker) {
        var onChange: (() -> Unit)? = null

        init {
            hour.minValue = 1; hour.maxValue = 12; hour.wrapSelectorWheel = true
            minute.minValue = 0; minute.maxValue = 59; minute.wrapSelectorWheel = true
            minute.setFormatter { v -> String.format(Locale.US, "%02d", v) }
            ampm.minValue = 0; ampm.maxValue = 1; ampm.displayedValues = arrayOf("AM", "PM"); ampm.wrapSelectorWheel = true
            for (p in listOf(hour, minute, ampm)) {
                p.descendantFocusability = android.view.ViewGroup.FOCUS_BLOCK_DESCENDANTS   // no keyboard pop-up; wheels only
                p.setOnValueChangedListener { _, _, _ -> onChange?.invoke() }
            }
        }

        fun set(hour24: Int, min: Int) {
            hour.value = if (hour24 % 12 == 0) 12 else hour24 % 12
            minute.value = min
            ampm.value = if (hour24 >= 12) 1 else 0
        }

        fun hour24(): Int = (hour.value % 12) + if (ampm.value == 1) 12 else 0
        fun minute(): Int = minute.value
    }

    /**
     * Turns start/end clock times into epoch millis: a start earlier than now means
     * tomorrow (unless it is within the last minute, which means "now"), and an end
     * at or before the start rolls over to the next day.
     */
    private fun resolve(sh: Int, sm: Int, eh: Int, em: Int): Pair<Long, Long> {
        val now = System.currentTimeMillis()
        val start = java.util.Calendar.getInstance().apply {
            set(java.util.Calendar.HOUR_OF_DAY, sh); set(java.util.Calendar.MINUTE, sm); set(java.util.Calendar.SECOND, 0); set(java.util.Calendar.MILLISECOND, 0)
        }
        var st = start.timeInMillis
        if (st < now - 90_000) st += 24 * 3600 * 1000L
        if (st < now) st = now
        val end = java.util.Calendar.getInstance().apply {
            timeInMillis = st
            set(java.util.Calendar.HOUR_OF_DAY, eh); set(java.util.Calendar.MINUTE, em); set(java.util.Calendar.SECOND, 0); set(java.util.Calendar.MILLISECOND, 0)
        }
        var en = end.timeInMillis
        if (en <= st) en += 24 * 3600 * 1000L
        return st to en
    }

    private fun schedule(activity: AppCompatActivity, channel: String, url: String, startAt: Long, endAt: Long, folder: String?) {
        ensureNotifications(activity)
        val stamp = SimpleDateFormat("yyyy-MM-dd HH.mm", Locale.getDefault()).format(Date(startAt))
        val t = SimpleDateFormat("h:mm a", Locale.getDefault())
        val job = TransferJob(
            id = UUID.randomUUID().toString(),
            type = TransferType.RECORDING,
            title = channel,
            subtitle = "${SimpleDateFormat("EEE MMM d", Locale.getDefault()).format(Date(startAt))} · ${t.format(Date(startAt))} – ${t.format(Date(endAt))}",
            url = url,
            fileName = Folders.safeName("$channel $stamp") + ".ts",
            folder = folder,
            startAt = startAt,
            endAt = endAt,
            createdAt = System.currentTimeMillis()
        )
        TransferService.enqueue(activity, job)
        val startsNow = startAt <= System.currentTimeMillis() + 60_000
        val msg = if (startsNow) activity.getString(R.string.recording_started_fmt, channel)
        else activity.getString(R.string.recording_scheduled_fmt, channel, t.format(Date(startAt)))
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
