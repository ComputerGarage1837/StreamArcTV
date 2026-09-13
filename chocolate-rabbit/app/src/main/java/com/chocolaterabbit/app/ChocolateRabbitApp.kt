package com.chocolaterabbit.app

import android.app.Application
import com.chocolaterabbit.app.util.AppLog

class ChocolateRabbitApp : Application() {
    override fun onCreate() {
        super.onCreate()
        AppLog.init(this)
        val previous = Thread.getDefaultUncaughtExceptionHandler()
        Thread.setDefaultUncaughtExceptionHandler { thread, error ->
            try {
                AppLog.crash("Crash", "uncaught exception on ${thread.name}", error)
            } catch (_: Throwable) {}
            previous?.uncaughtException(thread, error)
        }
    }
}
