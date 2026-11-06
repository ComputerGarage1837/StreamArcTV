package com.streamarc.tv.reminders

import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import android.os.Build
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import androidx.work.Constraints
import androidx.work.CoroutineWorker
import androidx.work.ExistingPeriodicWorkPolicy
import androidx.work.NetworkType
import androidx.work.PeriodicWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.WorkerParameters
import com.streamarc.tv.R
import com.streamarc.tv.data.CatalogCache
import com.streamarc.tv.data.ContentKind
import com.streamarc.tv.data.Episode
import com.streamarc.tv.data.Format
import com.streamarc.tv.data.Prefs
import com.streamarc.tv.data.Service
import com.streamarc.tv.data.XtreamApi
import com.streamarc.tv.data.toAccount
import com.streamarc.tv.transfer.Folders
import com.streamarc.tv.transfer.TransferJob
import com.streamarc.tv.transfer.TransferService
import com.streamarc.tv.transfer.TransferStore
import com.streamarc.tv.transfer.TransferType
import com.streamarc.tv.ui.MainActivity
import com.streamarc.tv.ui.SeriesActivity
import com.streamarc.tv.util.AppLog
import kotlinx.coroutines.delay
import java.util.Calendar
import java.util.UUID
import java.util.concurrent.TimeUnit

/**
 * Background reminders: a heads-up a week (and a day) before a subscription ends, an alert when a
 * show in My List gets a new episode, and, if switched on, overnight downloads of those episodes
 * over Wi-Fi. A periodic check runs every few hours; nothing is sent while the setting is off.
 */
object Reminders {
    private const val TAG = "Reminders"
    private const val CHANNEL = "reminders"
    private const val CHANNEL_EPISODES = "new_episodes"
    private const val WORK = "reminders-check"
    private const val MAX_SHOWS = 40

    fun schedule(context: Context) {
        val req = PeriodicWorkRequestBuilder<ReminderWorker>(6, TimeUnit.HOURS)
            .setConstraints(Constraints.Builder().setRequiredNetworkType(NetworkType.CONNECTED).build())
            .build()
        WorkManager.getInstance(context).enqueueUniquePeriodicWork(WORK, ExistingPeriodicWorkPolicy.KEEP, req)
    }

    private fun ensureChannel(context: Context, channel: String) {
        if (Build.VERSION.SDK_INT < 26) return
        val nm = context.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        if (nm.getNotificationChannel(channel) == null) {
            val name = context.getString(if (channel == CHANNEL_EPISODES) R.string.new_episodes_channel else R.string.reminders_channel)
            nm.createNotificationChannel(NotificationChannel(channel, name, NotificationManager.IMPORTANCE_DEFAULT))
        }
    }

    private fun notify(context: Context, id: Int, title: String, text: String, open: Intent, channel: String = CHANNEL) {
        ensureChannel(context, channel)
        val pi = PendingIntent.getActivity(
            context, id, open.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )
        val n = NotificationCompat.Builder(context, channel)
            .setSmallIcon(R.drawable.ic_update)
            .setContentTitle(title)
            .setContentText(text)
            .setStyle(NotificationCompat.BigTextStyle().bigText(text))
            .setContentIntent(pi)
            .setAutoCancel(true)
            .build()
        try { NotificationManagerCompat.from(context).notify(id, n) } catch (e: SecurityException) {
            AppLog.w(TAG, "notification not allowed: ${e.message}")
        }
    }

    // ---- Subscription expiry -------------------------------------------------------

    /** Reminds a week before, a day before, and once when a subscription has ended. */
    suspend fun checkExpiry(context: Context) {
        val prefs = Prefs(context)
        val now = System.currentTimeMillis() / 1000
        for (service in Service.values()) {
            var account = prefs.account(service) ?: continue
            try {
                val info = XtreamApi.login(service, account.username, account.password)
                account = info.toAccount(account.username, account.password)
                prefs.saveAccount(service, account)
            } catch (_: Exception) { /* keep the saved expiry */ }
            val exp = account.expDate ?: continue
            val left = exp - now
            val stage = when {
                left <= 0 -> 3
                left <= 86_400 -> 2
                left <= 7 * 86_400 -> 1
                else -> 0
            }
            if (stage == 0) continue
            val last = prefs.expiryNotified(service)?.split(':')
            val lastExp = last?.getOrNull(0)?.toLongOrNull()
            val lastStage = last?.getOrNull(1)?.toIntOrNull() ?: 0
            if (lastExp == exp && lastStage >= stage) continue
            prefs.setExpiryNotified(service, "$exp:$stage")
            val date = Format.expiry(exp)
            val text = when (stage) {
                3 -> context.getString(R.string.expired_notice_fmt, service.title, date)
                2 -> context.getString(R.string.expires_tomorrow_fmt, service.title, date)
                else -> context.getString(R.string.expires_soon_fmt, service.title, date, (left / 86_400).coerceAtLeast(1))
            }
            AppLog.i(TAG, "expiry reminder ${service.name}: $text")
            notify(context, 7100 + service.ordinal, context.getString(R.string.expiry_title), text, Intent(context, MainActivity::class.java))
        }
    }

    // ---- New episodes -------------------------------------------------------------

    private fun code(e: Episode) = e.season * 10_000 + e.number

    /** Alerts for My List shows with an episode newer than the last one seen, and queues them for download. */
    suspend fun checkNewEpisodes(context: Context) {
        val prefs = Prefs(context)
        val service = Service.VOD
        val account = prefs.account(service) ?: return
        val ids = prefs.favorites(service, ContentKind.SERIES).take(MAX_SHOWS)
        if (ids.isEmpty()) return
        val names = try {
            CatalogCache.get(context, service, account, ContentKind.SERIES).associateBy { it.id }
        } catch (_: Exception) { emptyMap() }
        val known = prefs.knownEpisodes().toMutableMap()
        val firstRun = known.isEmpty()
        for (id in ids) {
            val eps = try { XtreamApi.seriesInfo(service, account, id) } catch (_: Exception) { continue }
            delay(400)   // be gentle with the provider
            if (eps.isEmpty()) continue
            val latest = eps.maxOf { code(it) }
            val before = known[id]
            known[id] = latest
            if (before == null || latest <= before) continue   // first sighting: just remember it
            val fresh = eps.filter { code(it) > before }.sortedBy { code(it) }
            val show = names[id]?.name ?: context.getString(R.string.a_show)
            val newest = fresh.last()
            val text = if (fresh.size == 1) context.getString(R.string.new_episode_fmt, show, Format.se(newest.season, newest.number), newest.title)
                else context.getString(R.string.new_episodes_fmt, show, fresh.size)
            AppLog.i(TAG, "new episodes for '$show': ${fresh.size}")
            if (prefs.newEpisodeAlerts) {
                notify(context, 7200 + (id.hashCode() and 0xFFF), context.getString(R.string.new_episode_title), text,
                    SeriesActivity.intent(context, service, id, show, names[id]?.image, names[id]?.plot), CHANNEL_EPISODES)
            }
            if (prefs.autoDownloadNew) {
                val add = fresh.map { "${id}\t${it.id}\t${it.season}\t${it.number}\t${it.containerExtension}\t${show.replace('\t', ' ')}\t${it.title.replace('\t', ' ')}" }
                prefs.pendingEpisodes = prefs.pendingEpisodes + add
            }
        }
        prefs.setKnownEpisodes(known)
        if (firstRun) AppLog.i(TAG, "remembered the current episodes of ${known.size} My List shows")
    }

    // ---- Overnight downloads ----------------------------------------------------------

    private fun unmetered(context: Context): Boolean {
        val cm = context.getSystemService(Context.CONNECTIVITY_SERVICE) as? ConnectivityManager ?: return false
        val caps = cm.getNetworkCapabilities(cm.activeNetwork ?: return false) ?: return false
        return caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_METERED)
    }

    /**
     * Queues the downloads of new episodes: from the background check only overnight (1 am to 7 am)
     * on Wi-Fi, and whenever the app is opened on Wi-Fi.
     */
    fun processPendingDownloads(context: Context, fromBackground: Boolean) {
        val prefs = Prefs(context)
        if (!prefs.autoDownloadNew) return
        val pending = prefs.pendingEpisodes
        if (pending.isEmpty() || !unmetered(context)) return
        if (fromBackground && Calendar.getInstance().get(Calendar.HOUR_OF_DAY) !in 1..7) return
        val account = prefs.account(Service.VOD) ?: return
        val folder = prefs.downloadFolder?.takeIf { Folders.usable(context, it) }
        val existing = TransferStore.get(context).all().map { it.url }.toSet()
        val left = pending.toMutableSet()
        for (line in pending.take(12)) {
            left.remove(line)
            val p = line.split('\t')
            if (p.size < 7) continue
            val ep = Episode(p[1], p[6], p[2].toIntOrNull() ?: 0, p[3].toIntOrNull() ?: 0, p[4], null, null)
            val url = try { XtreamApi.episodeUrl(Service.VOD, account, ep) } catch (_: Exception) { continue }
            if (url in existing) continue
            val show = p[5]
            val job = TransferJob(
                id = UUID.randomUUID().toString(), type = TransferType.DOWNLOAD,
                title = "$show ${Format.se(ep.season, ep.number)} ${ep.title}", subtitle = show, url = url,
                fileName = Folders.safeName(Folders.episodeFileName(show, ep.season, ep.number, ep.title)) + "." + ep.containerExtension.ifBlank { "mp4" },
                folder = folder, startAt = 0, endAt = 0, createdAt = System.currentTimeMillis()
            )
            try { TransferService.enqueue(context, job) } catch (e: Exception) {
                // The job is stored; the service starts the next time the app is open.
                AppLog.w(TAG, "couldn't start the download service from the background: ${e.message}")
            }
            AppLog.i(TAG, "queued auto-download: ${job.title}")
        }
        prefs.pendingEpisodes = left
    }
}

class ReminderWorker(context: Context, params: WorkerParameters) : CoroutineWorker(context, params) {
    override suspend fun doWork(): Result {
        val ctx = applicationContext
        val prefs = Prefs(ctx)
        if (prefs.expiryReminders) {
            try { Reminders.checkExpiry(ctx) } catch (e: Exception) { AppLog.w("Reminders", "expiry check failed: ${e.message}") }
        }
        if (prefs.newEpisodeAlerts || prefs.autoDownloadNew) {
            try { Reminders.checkNewEpisodes(ctx) } catch (e: Exception) { AppLog.w("Reminders", "episode check failed: ${e.message}") }
            try { Reminders.processPendingDownloads(ctx, fromBackground = true) } catch (e: Exception) { AppLog.w("Reminders", "auto-download failed: ${e.message}") }
        }
        return Result.success()
    }
}
