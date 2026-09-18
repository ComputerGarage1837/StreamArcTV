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
        com.streamarc.tv.data.WatchProgress.init(this)
        Thread { com.streamarc.tv.player.TimeshiftServer.cleanup(this) }.start()
    }

    companion object {
        lateinit var instance: StreamArcApp
            private set
    }
}
