package com.streamarc.tv

import android.app.Application

class StreamArcApp : Application() {
    override fun onCreate() {
        super.onCreate()
        instance = this
        com.streamarc.tv.util.AppLog.init(this)
        com.streamarc.tv.data.Format.init(this)
        val previous = Thread.getDefaultUncaughtExceptionHandler()
        Thread.setDefaultUncaughtExceptionHandler { thread, error ->
            try {
                com.streamarc.tv.util.AppLog.crash("Crash", "uncaught exception on ${thread.name}", error)
                com.streamarc.tv.data.Prefs(this).crashed = true
            } catch (_: Throwable) {}
            previous?.uncaughtException(thread, error)
        }
        com.streamarc.tv.data.EpgCache.init(this)
        try { com.streamarc.tv.reminders.Reminders.schedule(this) } catch (e: Exception) { com.streamarc.tv.util.AppLog.w("Reminders", "not scheduled: ${e.message}") }
        if (android.os.Build.VERSION.SDK_INT < 25) {
            // Poster and logo downloads go through the shared client too, so the extra trusted roots apply to them.
            try {
                com.bumptech.glide.Glide.get(this).registry.replace(
                    com.bumptech.glide.load.model.GlideUrl::class.java, java.io.InputStream::class.java,
                    com.bumptech.glide.integration.okhttp3.OkHttpUrlLoader.Factory(com.streamarc.tv.data.XtreamApi.client)
                )
            } catch (e: Exception) {
                com.streamarc.tv.util.AppLog.w("Tls", "Glide client not replaced: ${e.message}")
            }
        }
        com.streamarc.tv.data.WatchProgress.init(this)
        Thread { com.streamarc.tv.player.TimeshiftServer.cleanup(this) }.start()
    }

    companion object {
        lateinit var instance: StreamArcApp
            private set
    }
}
