package com.streamarc.tv.ui

import android.Manifest
import android.content.Intent
import android.net.Uri
import android.provider.Settings
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
data class DownloadItem(val title: String, val subtitle: String, val url: String, val ext: String, val fileBase: String? = null)

object TransferDialogs {

    /**
     * Queues every item into the saved download folder. The picker only appears the first time
     * (or if the saved folder is no longer reachable); the folder is changed in Settings.
     * Falls back to the app folder on devices without a picker.
     */
    fun download(activity: AppCompatActivity, picker: FolderPicker, items: List<DownloadItem>) {
        if (items.isEmpty()) return
        val prefs = Prefs(activity)
        val current = prefs.downloadFolder?.takeIf { Folders.usable(activity, it) }
        if (current == null) {
            pickFolder(activity, picker, TransferType.DOWNLOAD) { folder -> enqueueDownloads(activity, items, folder) }
            return
        }
        enqueueDownloads(activity, items, current)
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
                fileName = Folders.safeName(it.fileBase ?: it.title) + "." + it.ext.ifBlank { "mp4" },
                folder = folder,
                startAt = 0, endAt = 0,
                createdAt = System.currentTimeMillis()
            )
            TransferService.enqueue(activity, job)
        }
        val msg = if (items.size == 1) activity.getString(R.string.download_started_fmt, items[0].title)
        else activity.getString(R.string.downloads_started_fmt, items.size)
        // Queued: offer the Downloads screen, or stay put.
        AlertDialog.Builder(activity)
            .setTitle(R.string.download_queued_title)
            .setMessage(msg)
            .setPositiveButton(R.string.go_to_downloads) { _, _ ->
                activity.startActivity(TransfersActivity.intent(activity, TransferType.DOWNLOAD))
            }
            .setNegativeButton(R.string.stay_here, null)
            .show()
    }

    /** Start and end clock times (to the minute) on hour / minute / AM-PM wheels, then folder and schedule. */
    fun record(activity: AppCompatActivity, picker: FolderPicker, channelName: String, url: String) {
        val vb = DialogRecordBinding.inflate(LayoutInflater.from(activity))
        val now = com.streamarc.tv.data.Format.calendar()
        val startCal = (now.clone() as java.util.Calendar).apply { add(java.util.Calendar.MINUTE, 1) }
        val endCal = (startCal.clone() as java.util.Calendar).apply { add(java.util.Calendar.HOUR_OF_DAY, 1) }

        val start = TimeWheels(vb.startHour, vb.startMinute)
        val end = TimeWheels(vb.endHour, vb.endMinute)
        start.set(startCal.get(java.util.Calendar.HOUR_OF_DAY), startCal.get(java.util.Calendar.MINUTE))
        end.set(endCal.get(java.util.Calendar.HOUR_OF_DAY), endCal.get(java.util.Calendar.MINUTE))

        fun times(): Pair<Long, Long> = resolve(start.hour24(), start.minute(), end.hour24(), end.minute())
        fun summarize() {
            val (st, en) = times()
            val f = com.streamarc.tv.data.Format
            val sameDay = f.dayMs(st) == f.dayMs(en)
            vb.txtSummary.text = if (sameDay) "${f.dayMs(st)}   ${f.timeMs(st)} – ${f.timeMs(en)}"
            else "${f.dayMs(st)} ${f.timeMs(st)} – ${f.dayMs(en)} ${f.timeMs(en)}"
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
            .setNegativeButton(android.R.string.cancel, null)
            .create()
        dialog.show()
        vb.startHour.requestFocus()
    }

    /**
     * One hour wheel that runs through the whole day (12 AM … 11 AM, 12 PM … 11 PM and
     * wraps to the next day) plus a minute wheel. Works by touch and with a remote.
     */
    private class TimeWheels(val hour: android.widget.NumberPicker, val minute: android.widget.NumberPicker) {
        var onChange: (() -> Unit)? = null

        init {
            hour.minValue = 0; hour.maxValue = 23; hour.wrapSelectorWheel = true
            hour.displayedValues = Array(24) { h ->
                val h12 = if (h % 12 == 0) 12 else h % 12
                "$h12 ${if (h < 12) "AM" else "PM"}"
            }
            minute.minValue = 0; minute.maxValue = 59; minute.wrapSelectorWheel = true
            minute.setFormatter { v -> String.format(Locale.US, "%02d", v) }
            for (p in listOf(hour, minute)) {
                p.descendantFocusability = android.view.ViewGroup.FOCUS_BLOCK_DESCENDANTS   // wheels only, no keyboard
                p.setOnValueChangedListener { _, _, _ -> onChange?.invoke() }
            }
        }

        fun set(hour24: Int, min: Int) { hour.value = hour24; minute.value = min }
        fun hour24(): Int = hour.value
        fun minute(): Int = minute.value
    }

    /**
     * Turns start/end clock times into epoch millis: a start earlier than now means
     * tomorrow (unless it is within the last minute, which means "now"), and an end
     * at or before the start rolls over to the next day.
     */
    private fun resolve(sh: Int, sm: Int, eh: Int, em: Int): Pair<Long, Long> {
        val now = System.currentTimeMillis()
        val start = com.streamarc.tv.data.Format.calendar().apply {
            set(java.util.Calendar.HOUR_OF_DAY, sh); set(java.util.Calendar.MINUTE, sm); set(java.util.Calendar.SECOND, 0); set(java.util.Calendar.MILLISECOND, 0)
        }
        var st = start.timeInMillis
        if (st < now - 90_000) st += 24 * 3600 * 1000L
        if (st < now) st = now
        val end = com.streamarc.tv.data.Format.calendar().apply {
            timeInMillis = st
            set(java.util.Calendar.HOUR_OF_DAY, eh); set(java.util.Calendar.MINUTE, em); set(java.util.Calendar.SECOND, 0); set(java.util.Calendar.MILLISECOND, 0)
        }
        var en = end.timeInMillis
        if (en <= st) en += 24 * 3600 * 1000L
        return st to en
    }

    private fun schedule(activity: AppCompatActivity, channel: String, url: String, startAt: Long, endAt: Long, folder: String?) {
        ensureNotifications(activity)
        val f = com.streamarc.tv.data.Format
        val stamp = f.fmt("yyyy-MM-dd HH.mm").format(Date(startAt))
        val job = TransferJob(
            id = UUID.randomUUID().toString(),
            type = TransferType.RECORDING,
            title = channel,
            subtitle = "${f.dayMs(startAt)} · ${f.timeMs(startAt)} – ${f.timeMs(endAt)}",
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
        else activity.getString(R.string.recording_scheduled_fmt, channel, f.timeMs(startAt))
        Toast.makeText(activity, msg, Toast.LENGTH_LONG).show()
        if (!startsNow) checkRecordingReadiness(activity)
    }

    /**
     * A scheduled recording needs Android to wake the app on time and to leave it running.
     * If exact alarms are blocked or battery optimisation is on, say so now and open the
     * setting, rather than finding an empty file in the morning.
     */
    fun checkRecordingReadiness(activity: AppCompatActivity) {
        val problems = ArrayList<Pair<String, Intent>>()
        val pkg = activity.packageName
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            val am = activity.getSystemService(android.content.Context.ALARM_SERVICE) as android.app.AlarmManager
            if (!am.canScheduleExactAlarms()) {
                problems.add(activity.getString(R.string.readiness_exact_alarms) to
                    Intent(Settings.ACTION_REQUEST_SCHEDULE_EXACT_ALARM, Uri.parse("package:$pkg")))
            }
        }
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
            val pm = activity.getSystemService(android.content.Context.POWER_SERVICE) as android.os.PowerManager
            if (!pm.isIgnoringBatteryOptimizations(pkg)) {
                val direct = Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS, Uri.parse("package:$pkg"))
                val list = Intent(Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS)
                val intent = when {
                    direct.resolveActivity(activity.packageManager) != null -> direct
                    list.resolveActivity(activity.packageManager) != null -> list
                    else -> null
                }
                if (intent != null) problems.add(activity.getString(R.string.readiness_battery) to intent)
            }
        }
        if (problems.isEmpty()) return
        var i = 0
        fun showNext() {
            if (i >= problems.size) return
            val (text, intent) = problems[i++]
            AlertDialog.Builder(activity)
                .setTitle(R.string.readiness_title)
                .setMessage(text)
                .setPositiveButton(R.string.open_settings) { _, _ ->
                    try { activity.startActivity(intent) } catch (_: Exception) {
                        Toast.makeText(activity, R.string.readiness_no_screen, Toast.LENGTH_LONG).show()
                    }
                    showNext()
                }
                .setNegativeButton(R.string.not_now) { _, _ -> showNext() }
                .show()
        }
        showNext()
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
