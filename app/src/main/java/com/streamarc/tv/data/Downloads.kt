package com.streamarc.tv.data

import android.app.DownloadManager
import android.content.Context
import android.net.Uri
import android.os.Build
import android.os.Environment
import com.google.gson.Gson
import com.google.gson.reflect.TypeToken
import java.io.File

/** One movie or episode the user asked to download. */
data class DownloadRecord(
    val downloadId: Long,
    val title: String,
    val subtitle: String,
    val fileName: String,
    val location: String,          // "app" or "public"
    val addedAt: Long
)

/** Where a download is saved; both are handled by Android's DownloadManager. */
enum class DownloadLocation(val key: String) {
    APP("app"),        // Android/data/<app>/files/Download — removed with the app
    PUBLIC("public");  // the device's shared Download folder

    companion object {
        fun from(key: String) = values().firstOrNull { it.key == key } ?: APP
    }
}

/** Persists the list of downloads (SharedPreferences + JSON). */
class DownloadStore(context: Context) {
    private val sp = context.applicationContext.getSharedPreferences("streamarc_downloads", Context.MODE_PRIVATE)
    private val gson = Gson()
    private val type = TypeToken.getParameterized(List::class.java, DownloadRecord::class.java).type

    fun all(): List<DownloadRecord> {
        val json = sp.getString("items", null) ?: return emptyList()
        return try { gson.fromJson<List<DownloadRecord>>(json, type) ?: emptyList() } catch (_: Exception) { emptyList() }
    }

    fun add(r: DownloadRecord) = save(all().filter { it.downloadId != r.downloadId } + r)

    fun remove(downloadId: Long) = save(all().filter { it.downloadId != downloadId })

    private fun save(list: List<DownloadRecord>) {
        sp.edit().putString("items", gson.toJson(list, type)).apply()
    }
}

object Downloads {
    /** Live status of a download as reported by DownloadManager. */
    data class Status(val state: Int, val reason: Int, val downloaded: Long, val total: Long, val localUri: String?)

    fun sanitizeFileName(name: String): String =
        name.replace(Regex("[\\\\/:*?\"<>|\\x00-\\x1F]"), " ").replace(Regex("\\s+"), " ").trim().take(120).ifBlank { "video" }

    fun publicAllowed(): Boolean = Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q

    /** Queues the download and records it; returns the DownloadManager id. */
    fun start(context: Context, title: String, subtitle: String, url: String, ext: String, location: DownloadLocation): Long {
        val app = context.applicationContext
        val fileName = "${sanitizeFileName(title)}.${ext.ifBlank { "mp4" }}"
        val dm = app.getSystemService(Context.DOWNLOAD_SERVICE) as DownloadManager
        val req = DownloadManager.Request(Uri.parse(url))
            .setTitle(title)
            .setDescription(subtitle)
            .setNotificationVisibility(DownloadManager.Request.VISIBILITY_VISIBLE)
            .setAllowedOverMetered(true)
            .setAllowedOverRoaming(true)
            .addRequestHeader("User-Agent", XtreamApi.USER_AGENT)
        if (location == DownloadLocation.PUBLIC && publicAllowed()) {
            req.setDestinationInExternalPublicDir(Environment.DIRECTORY_DOWNLOADS, "Stream Arc TV/$fileName")
        } else {
            req.setDestinationInExternalFilesDir(app, Environment.DIRECTORY_DOWNLOADS, fileName)
        }
        val id = dm.enqueue(req)
        DownloadStore(app).add(DownloadRecord(id, title, subtitle, fileName, location.key, System.currentTimeMillis()))
        return id
    }

    fun status(context: Context, downloadId: Long): Status? {
        val dm = context.getSystemService(Context.DOWNLOAD_SERVICE) as DownloadManager
        dm.query(DownloadManager.Query().setFilterById(downloadId))?.use { c ->
            if (!c.moveToFirst()) return null
            return Status(
                state = c.getInt(c.getColumnIndexOrThrow(DownloadManager.COLUMN_STATUS)),
                reason = c.getInt(c.getColumnIndexOrThrow(DownloadManager.COLUMN_REASON)),
                downloaded = c.getLong(c.getColumnIndexOrThrow(DownloadManager.COLUMN_BYTES_DOWNLOADED_SO_FAR)),
                total = c.getLong(c.getColumnIndexOrThrow(DownloadManager.COLUMN_TOTAL_SIZE_BYTES)),
                localUri = c.getString(c.getColumnIndexOrThrow(DownloadManager.COLUMN_LOCAL_URI))
            )
        }
        return null
    }

    /** Removes the download (and its file) from DownloadManager and from our list. */
    fun delete(context: Context, downloadId: Long) {
        val dm = context.getSystemService(Context.DOWNLOAD_SERVICE) as DownloadManager
        try { dm.remove(downloadId) } catch (_: Exception) {}
        DownloadStore(context).remove(downloadId)
    }

    /** A playable URI for a finished download, or null. */
    fun playableUri(context: Context, downloadId: Long): Uri? {
        val st = status(context, downloadId) ?: return null
        if (st.state != DownloadManager.STATUS_SUCCESSFUL) return null
        st.localUri?.let { u ->
            val uri = Uri.parse(u)
            if (uri.scheme == "file") {
                val f = File(uri.path ?: return@let)
                if (f.exists()) return uri
            } else return uri
        }
        val dm = context.getSystemService(Context.DOWNLOAD_SERVICE) as DownloadManager
        return try { dm.getUriForDownloadedFile(downloadId) } catch (_: Exception) { null }
    }

    fun describeLocation(location: String): String = when (DownloadLocation.from(location)) {
        DownloadLocation.PUBLIC -> "Downloads / Stream Arc TV"
        DownloadLocation.APP -> "Inside Stream Arc TV"
    }
}
