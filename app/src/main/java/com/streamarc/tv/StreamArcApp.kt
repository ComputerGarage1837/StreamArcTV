package com.streamarc.tv

import android.app.Application

class StreamArcApp : Application() {
    override fun onCreate() {
        super.onCreate()
        instance = this
        com.streamarc.tv.data.EpgCache.init(this)
    }

    companion object {
        lateinit var instance: StreamArcApp
            private set
    }
}
