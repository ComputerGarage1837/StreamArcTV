package com.streamarc.tv

import android.app.Application

class StreamArcApp : Application() {
    override fun onCreate() {
        super.onCreate()
        instance = this
    }

    companion object {
        lateinit var instance: StreamArcApp
            private set
    }
}
