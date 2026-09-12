package com.streamarc.tv

import android.app.Application

class StreamArcApp : Application() {
    override fun onCreate() {
        super.onCreate()
        instance = this
        com.streamarc.tv.data.EpgCache.init(this)
        Thread { com.streamarc.tv.player.TimeshiftServer.cleanup(this) }.start()
    }

    companion object {
        lateinit var instance: StreamArcApp
            private set
    }
}
