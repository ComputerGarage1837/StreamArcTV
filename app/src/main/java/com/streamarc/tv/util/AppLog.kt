package com.streamarc.tv.util

import android.app.Activity
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.util.Log
import android.widget.Toast
import androidx.core.content.FileProvider
import com.streamarc.tv.BuildConfig
import java.io.File
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/**
 * App-wide diagnostic log: kept in memory and appended to a file under the app's private
 * storage, exportable from Settings. Never log credentials: use [safeUrl] for stream addresses.
 */
object AppLog {
    private const val MAX_BYTES = 2L * 1024 * 1024
    private val fmt = SimpleDateFormat("MM-dd HH:mm:ss.SSS", Locale.US)
    private var file: File? = null
    private val lock = Any()

    fun init(context: Context) {
        val dir = File(context.filesDir, "logs").apply { mkdirs() }
        file = File(dir, "streamarc.log")
        i("App", "Stream Arc TV ${BuildConfig.VERSION_NAME} · Android ${android.os.Build.VERSION.RELEASE} · " +
            "${android.os.Build.MANUFACTURER} ${android.os.Build.MODEL} · abis ${android.os.Build.SUPPORTED_ABIS.joinToString()}")
    }

    fun i(tag: String, msg: String) = write("I", tag, msg)
    fun w(tag: String, msg: String) = write("W", tag, msg)
    fun e(tag: String, msg: String, t: Throwable? = null) =
        write("E", tag, if (t != null) msg + "\n" + Log.getStackTraceString(t) else msg)

    private fun write(level: String, tag: String, msg: String) {
        Log.println(if (level == "E") Log.ERROR else if (level == "W") Log.WARN else Log.INFO, tag, msg)
        val line = "${fmt.format(Date())} $level/$tag: $msg\n"
        val f = file ?: return
        Thread {
            synchronized(lock) {
                try {
                    if (f.length() > MAX_BYTES) {
                        val old = File(f.parentFile, "streamarc.1.log")
                        old.delete(); f.renameTo(old)
                    }
                    f.appendText(line)
                } catch (_: Exception) {}
            }
        }.start()
    }

    /** Strips the user name and password segments out of an Xtream stream address. */
    fun safeUrl(url: String): String =
        url.replace(Regex("/(live|movie|series|timeshift)/[^/]+/[^/]+/"), "/$1/***/***/")
            .replace(Regex("(username|password)=[^&]*"), "$1=***")

    fun text(): String = synchronized(lock) {
        val f = file ?: return ""
        val old = File(f.parentFile, "streamarc.1.log")
        (if (old.exists()) old.readText() else "") + (if (f.exists()) f.readText() else "")
    }

    fun clear() = synchronized(lock) {
        file?.delete()
        file?.parentFile?.let { File(it, "streamarc.1.log").delete() }
    }

    /** Offers the log to any app that accepts a text file (email, Drive, messaging…). */
    fun share(activity: Activity) {
        val f = file ?: return
        try {
            val export = File(activity.cacheDir, "streamarc-log.txt").apply { writeText(text()) }
            val uri = FileProvider.getUriForFile(activity, activity.packageName + ".files", export)
            val send = Intent(Intent.ACTION_SEND)
                .setType("text/plain")
                .putExtra(Intent.EXTRA_SUBJECT, "Stream Arc TV log ${BuildConfig.VERSION_NAME}")
                .putExtra(Intent.EXTRA_STREAM, uri)
                .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
            activity.startActivity(Intent.createChooser(send, "Export log"))
        } catch (e: Exception) {
            Toast.makeText(activity, "Couldn't share the log: ${e.message}. Path: ${f.absolutePath}", Toast.LENGTH_LONG).show()
        }
    }

    fun copy(context: Context) {
        val cm = context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
        cm.setPrimaryClip(ClipData.newPlainText("Stream Arc TV log", text().takeLast(200_000)))
        Toast.makeText(context, "Log copied to the clipboard", Toast.LENGTH_SHORT).show()
    }
}
