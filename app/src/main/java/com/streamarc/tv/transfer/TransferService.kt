package com.streamarc.tv.transfer

import android.app.AlarmManager
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder
import android.os.PowerManager
import androidx.core.app.NotificationCompat
import androidx.core.app.ServiceCompat
import androidx.core.content.ContextCompat
import com.streamarc.tv.R
import com.streamarc.tv.data.XtreamApi
import com.streamarc.tv.ui.MainActivity
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Semaphore
import kotlinx.coroutines.sync.withPermit
import okhttp3.Request
import java.util.concurrent.TimeUnit

/**
 * Foreground service that performs downloads and live recordings: streams the
 * URL into the chosen folder, reports progress to [TransferStore], stops a
 * recording when its end time passes, and schedules future recordings with
 * AlarmManager.
 */
class TransferService : Service() {

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val running = HashMap<String, Job>()
    private val downloadSlots = Semaphore(2)
    private lateinit var store: TransferStore
    private var wakeLock: PowerManager.WakeLock? = null

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        store = TransferStore.get(this)
        ensureChannel()
        ServiceCompat.startForeground(
            this, NOTIF_ID, buildNotification(getString(R.string.transfers_working), null),
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC else 0
        )
        wakeLock = (getSystemService(Context.POWER_SERVICE) as PowerManager)
            .newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "StreamArcTV:transfers").also { it.acquire(12 * 60 * 60 * 1000L) }
        scope.launch { progressLoop() }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_CANCEL -> intent.getStringExtra(EXTRA_ID)?.let { cancel(it) }
            else -> kick()
        }
        return START_STICKY
    }

    /** Starts anything that is due: queued downloads and recordings whose time has come. */
    private fun kick() {
        val now = System.currentTimeMillis()
        for (job in store.all()) {
            if (running.containsKey(job.id)) continue
            when {
                job.state == TransferState.QUEUED -> start(job)
                job.state == TransferState.SCHEDULED && job.startAt <= now + 15_000 -> start(job)
                job.state == TransferState.SCHEDULED -> scheduleAlarm(this, job)
                job.state == TransferState.RUNNING -> start(job)   // the process died mid-transfer: restart it
            }
        }
        stopIfIdle()
    }

    private fun start(job: TransferJob) {
        running[job.id] = scope.launch {
            if (job.type == TransferType.DOWNLOAD) downloadSlots.withPermit { run(job) } else run(job)
            running.remove(job.id)
            stopIfIdle()
        }
    }

    private fun cancel(id: String) {
        running.remove(id)?.cancel()
        store.get(id)?.let { j ->
            Folders.delete(this, j.fileUri)
            store.update(id) { it.state = TransferState.CANCELLED }
        }
        cancelAlarm(this, id)
        stopIfIdle()
    }

    private suspend fun run(job: TransferJob) {
        val id = job.id
        // A recording scheduled for later waits here (the alarm normally wakes us anyway).
        if (job.type == TransferType.RECORDING) {
            val wait = job.startAt - System.currentTimeMillis()
            if (wait > 0) delay(wait)
        }
        store.update(id) { it.state = TransferState.RUNNING; it.error = null }
        val mime = if (job.fileName.endsWith(".ts", true)) "video/mp2t" else "video/mp4"
        var target: Folders.Target? = null
        try {
            target = Folders.create(this, job.folder, job.type, job.fileName, mime)
            store.update(id) { it.fileUri = target.uri.toString() }
            val client = XtreamApi.client.newBuilder()
                .readTimeout(60, TimeUnit.SECONDS)
                .callTimeout(0, TimeUnit.MILLISECONDS)
                .build()
            val req = Request.Builder().url(job.url).header("User-Agent", XtreamApi.USER_AGENT).build()
            client.newCall(req).execute().use { resp ->
                if (!resp.isSuccessful) throw IllegalStateException("Server returned HTTP ${resp.code}")
                val body = resp.body ?: throw IllegalStateException("Empty response")
                val total = if (job.type == TransferType.DOWNLOAD) body.contentLength() else -1L
                store.update(id) { it.total = total }
                val input = body.byteStream()
                val buf = ByteArray(256 * 1024)
                var done = 0L
                var lastFlush = System.currentTimeMillis()
                while (scope.isActive && running.containsKey(id)) {
                    if (job.type == TransferType.RECORDING && System.currentTimeMillis() >= job.endAt) break
                    val n = input.read(buf)
                    if (n < 0) break
                    target.stream.write(buf, 0, n)
                    done += n
                    val now = System.currentTimeMillis()
                    if (now - lastFlush > 1000) {
                        lastFlush = now
                        val d = done
                        store.update(id) { it.bytes = d }
                    }
                }
                target.stream.flush()
                val d = done
                store.update(id) { it.bytes = d }
            }
            if (!running.containsKey(id)) return   // cancelled
            store.update(id) { it.state = TransferState.DONE }
            notifyDone(job, true, null)
        } catch (e: Exception) {
            if (!running.containsKey(id)) return
            store.update(id) { it.state = TransferState.FAILED; it.error = e.message }
            notifyDone(job, false, e.message)
        } finally {
            try { target?.stream?.close() } catch (_: Exception) {}
        }
    }

    private suspend fun progressLoop() {
        while (scope.isActive) {
            val active = store.all().filter { it.state == TransferState.RUNNING }
            val text = when (active.size) {
                0 -> getString(R.string.transfers_idle)
                1 -> active[0].let { j ->
                    val pct = if (j.total > 0) " ${(j.bytes * 100 / j.total)}%" else ""
                    "${if (j.type == TransferType.RECORDING) getString(R.string.recording) else getString(R.string.downloading)}: ${j.title}$pct"
                }
                else -> getString(R.string.transfers_count_fmt, active.size)
            }
            val nm = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
            nm.notify(NOTIF_ID, buildNotification(text, active.firstOrNull()))
            delay(2000)
        }
    }

    private fun stopIfIdle() {
        scope.launch {
            delay(1500)
            val pending = store.all().any { it.state == TransferState.QUEUED || it.state == TransferState.RUNNING } || running.isNotEmpty()
            if (!pending) stopSelf()
        }
    }

    override fun onDestroy() {
        scope.cancel()
        try { wakeLock?.let { if (it.isHeld) it.release() } } catch (_: Exception) {}
        super.onDestroy()
    }

    private fun ensureChannel() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val nm = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
            nm.createNotificationChannel(NotificationChannel(CHANNEL, getString(R.string.transfers_channel), NotificationManager.IMPORTANCE_LOW))
        }
    }

    private fun buildNotification(text: String, job: TransferJob?): Notification {
        val open = PendingIntent.getActivity(
            this, 0, Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )
        val b = NotificationCompat.Builder(this, CHANNEL)
            .setSmallIcon(R.drawable.ic_update)
            .setContentTitle(getString(R.string.app_name))
            .setContentText(text)
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .setContentIntent(open)
        if (job != null && job.total > 0) b.setProgress(1000, (job.bytes * 1000 / job.total).toInt(), false)
        else if (job != null) b.setProgress(0, 0, true)
        return b.build()
    }

    private fun notifyDone(job: TransferJob, ok: Boolean, error: String?) {
        val nm = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        val text = if (ok) getString(R.string.transfer_done_fmt, job.title) else getString(R.string.transfer_failed_fmt, job.title, error ?: "")
        val n = NotificationCompat.Builder(this, CHANNEL)
            .setSmallIcon(R.drawable.ic_update)
            .setContentTitle(getString(R.string.app_name))
            .setContentText(text)
            .setAutoCancel(true)
            .build()
        nm.notify(job.id.hashCode(), n)
    }

    companion object {
        const val CHANNEL = "transfers"
        const val NOTIF_ID = 4101
        const val ACTION_CANCEL = "com.streamarc.tv.transfer.CANCEL"
        const val EXTRA_ID = "id"

        /** Adds a job and makes sure the service is running (or the alarm is set). */
        fun enqueue(context: Context, job: TransferJob) {
            TransferStore.get(context).put(job)
            if (job.state == TransferState.SCHEDULED && job.startAt > System.currentTimeMillis() + 60_000) {
                scheduleAlarm(context, job)
            } else {
                startService(context)
            }
        }

        fun startService(context: Context) {
            ContextCompat.startForegroundService(context, Intent(context, TransferService::class.java))
        }

        fun cancel(context: Context, id: String) {
            val job = TransferStore.get(context).get(id)
            if (job != null && job.state == TransferState.SCHEDULED) {
                cancelAlarm(context, id)
                Folders.delete(context, job.fileUri)
                TransferStore.get(context).update(id) { it.state = TransferState.CANCELLED }
                return
            }
            val i = Intent(context, TransferService::class.java).setAction(ACTION_CANCEL).putExtra(EXTRA_ID, id)
            ContextCompat.startForegroundService(context, i)
        }

        private fun alarmIntent(context: Context, id: String): PendingIntent =
            PendingIntent.getBroadcast(
                context, id.hashCode(),
                Intent(context, TransferAlarmReceiver::class.java).setAction("com.streamarc.tv.transfer.ALARM").putExtra(EXTRA_ID, id),
                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
            )

        fun scheduleAlarm(context: Context, job: TransferJob) {
            val am = context.getSystemService(Context.ALARM_SERVICE) as AlarmManager
            val pi = alarmIntent(context, job.id)
            val at = job.startAt - 5_000
            val exactOk = Build.VERSION.SDK_INT < Build.VERSION_CODES.S || am.canScheduleExactAlarms()
            try {
                if (exactOk) {
                    am.setAlarmClock(AlarmManager.AlarmClockInfo(at, pi), pi)
                } else {
                    am.setWindow(AlarmManager.RTC_WAKEUP, at, 60_000, pi)
                }
            } catch (_: SecurityException) {
                am.setWindow(AlarmManager.RTC_WAKEUP, at, 60_000, pi)
            }
        }

        fun cancelAlarm(context: Context, id: String) {
            val am = context.getSystemService(Context.ALARM_SERVICE) as AlarmManager
            am.cancel(alarmIntent(context, id))
        }

        /** Re-arms alarms for scheduled recordings (after reboot or app update). */
        fun rescheduleAll(context: Context) {
            val now = System.currentTimeMillis()
            for (job in TransferStore.get(context).all()) {
                if (job.state == TransferState.SCHEDULED) {
                    if (job.endAt <= now) TransferStore.get(context).update(job.id) { it.state = TransferState.FAILED; it.error = "Missed" }
                    else if (job.startAt <= now) startService(context)
                    else scheduleAlarm(context, job)
                }
            }
        }
    }
}

/** Woken by AlarmManager for a scheduled recording, or by boot to re-arm alarms. */
class TransferAlarmReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        when (intent.action) {
            Intent.ACTION_BOOT_COMPLETED, Intent.ACTION_MY_PACKAGE_REPLACED -> TransferService.rescheduleAll(context)
            else -> TransferService.startService(context)
        }
    }
}
