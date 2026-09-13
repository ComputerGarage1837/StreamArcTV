package com.chocolaterabbit.app.util

import android.content.Context
import android.util.Log
import com.chocolaterabbit.app.BuildConfig
import java.io.File
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/** Small diagnostic log kept under the app's private storage (never logs sign-up details). */
object AppLog {
    private const val MAX_BYTES = 1L * 1024 * 1024
    private val fmt = SimpleDateFormat("MM-dd HH:mm:ss.SSS", Locale.US)
    private var file: File? = null
    private val lock = Any()

    fun init(context: Context) {
        val dir = File(context.filesDir, "logs").apply { mkdirs() }
        file = File(dir, "chocolate-rabbit.log")
        i("App", "The Chocolate Rabbit ${BuildConfig.VERSION_NAME} · Android ${android.os.Build.VERSION.RELEASE} · " +
            "${android.os.Build.MANUFACTURER} ${android.os.Build.MODEL}")
    }

    fun i(tag: String, msg: String) = write("I", tag, msg)
    fun w(tag: String, msg: String) = write("W", tag, msg)
    fun e(tag: String, msg: String, t: Throwable? = null) =
        write("E", tag, if (t != null) msg + "\n" + Log.getStackTraceString(t) else msg)

    /** Written on the crashing thread, synchronously, before the process dies. */
    fun crash(tag: String, msg: String, t: Throwable) {
        val line = "${fmt.format(Date())} E/$tag: $msg\n${Log.getStackTraceString(t)}\n"
        Log.e(tag, msg, t)
        val f = file ?: return
        synchronized(lock) { try { f.appendText(line) } catch (_: Exception) {} }
    }

    private fun write(level: String, tag: String, msg: String) {
        Log.println(if (level == "E") Log.ERROR else if (level == "W") Log.WARN else Log.INFO, tag, msg)
        val line = "${fmt.format(Date())} $level/$tag: $msg\n"
        val f = file ?: return
        Thread {
            synchronized(lock) {
                try {
                    if (f.length() > MAX_BYTES) {
                        val old = File(f.parentFile, "chocolate-rabbit.1.log")
                        old.delete(); f.renameTo(old)
                    }
                    f.appendText(line)
                } catch (_: Exception) {}
            }
        }.start()
    }
}
