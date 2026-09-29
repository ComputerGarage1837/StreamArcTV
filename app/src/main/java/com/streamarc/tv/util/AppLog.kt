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
            val uri = FileProvider.getUriForFile(activity, activity.packageName + ".fileprovider", export)
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

    /**
     * Writes the log as a text file somewhere a file manager can reach without any other app:
     * the device's public Downloads folder (Android 10+), plus a copy in the folder chosen for
     * downloads in Settings when there is one. Returns a description of where it went.
     */
    fun save(context: Context): String {
        val stamp = SimpleDateFormat("yyyyMMdd-HHmm", Locale.US).format(Date())
        val name = "streamarc-log-${BuildConfig.VERSION_NAME}-$stamp.txt"
        val body = text()
        val places = ArrayList<String>()
        val errors = ArrayList<String>()

        // 1. Public Downloads via MediaStore (no permission needed on Android 10+).
        if (android.os.Build.VERSION.SDK_INT >= 29) {
            try {
                val values = android.content.ContentValues().apply {
                    put(android.provider.MediaStore.Downloads.DISPLAY_NAME, name)
                    put(android.provider.MediaStore.Downloads.MIME_TYPE, "text/plain")
                    put(android.provider.MediaStore.Downloads.RELATIVE_PATH, android.os.Environment.DIRECTORY_DOWNLOADS)
                }
                val uri = context.contentResolver.insert(android.provider.MediaStore.Downloads.EXTERNAL_CONTENT_URI, values)
                    ?: throw IllegalStateException("no entry")
                context.contentResolver.openOutputStream(uri, "w")?.use { it.write(body.toByteArray()) }
                    ?: throw IllegalStateException("can't open")
                places.add("Downloads/$name")
            } catch (e: Exception) {
                errors.add("Downloads: ${e.message}")
            }
        } else {
            // Older Android: the public Downloads folder is writable without a runtime prompt
            // only when the legacy storage permission is held, so use the app's own external folder.
            try {
                val dir = context.getExternalFilesDir(android.os.Environment.DIRECTORY_DOWNLOADS)
                    ?: throw IllegalStateException("no external storage")
                File(dir, name).writeText(body)
                places.add("Android/data/${context.packageName}/files/Download/$name")
            } catch (e: Exception) {
                errors.add("App folder: ${e.message}")
            }
        }

        // 2. The folder chosen for downloads in Settings (USB stick, SD card…), if any.
        val folder = com.streamarc.tv.data.Prefs(context).downloadFolder
        if (folder != null) {
            try {
                val tree = androidx.documentfile.provider.DocumentFile.fromTreeUri(context, android.net.Uri.parse(folder))
                    ?: throw IllegalStateException("folder unavailable")
                val doc = tree.createFile("text/plain", name) ?: throw IllegalStateException("can't create file")
                context.contentResolver.openOutputStream(doc.uri, "w")?.use { it.write(body.toByteArray()) }
                    ?: throw IllegalStateException("can't open")
                places.add("${tree.name ?: "download folder"}/$name")
            } catch (e: Exception) {
                errors.add("Download folder: ${e.message}")
            }
        }

        i("Log", "saved log to ${places.joinToString()}${if (errors.isEmpty()) "" else " (failed: ${errors.joinToString()})"}")
        if (places.isEmpty()) throw IllegalStateException(errors.joinToString("\n").ifEmpty { "nowhere to save" })
        return places.joinToString("\n")
    }

    fun copy(context: Context) {
        val cm = context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
        cm.setPrimaryClip(ClipData.newPlainText("Stream Arc TV log", text().takeLast(200_000)))
        Toast.makeText(context, "Log copied to the clipboard", Toast.LENGTH_SHORT).show()
    }
}
