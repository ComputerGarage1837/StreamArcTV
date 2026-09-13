package com.streamarc.tv.transfer

import android.content.Context
import android.net.Uri
import android.os.Environment
import androidx.documentfile.provider.DocumentFile
import com.google.gson.Gson
import com.google.gson.reflect.TypeToken
import java.io.File
import java.io.FileOutputStream
import java.io.OutputStream

enum class TransferType { DOWNLOAD, RECORDING }

enum class TransferState { SCHEDULED, QUEUED, RUNNING, DONE, FAILED, CANCELLED }

/**
 * One download or recording. Recordings have a start and end time; downloads
 * run as soon as a slot is free. `folder` is a SAF tree URI the user picked,
 * or null for the app's own Download directory.
 */
data class TransferJob(
    val id: String,
    val type: TransferType,
    val title: String,
    val subtitle: String,
    val url: String,
    val fileName: String,
    val folder: String?,
    val startAt: Long,          // epoch ms; recordings start here, downloads use 0
    val endAt: Long,            // epoch ms; recordings stop here, downloads use 0
    val createdAt: Long,
    var state: TransferState = if (startAt > 0) TransferState.SCHEDULED else TransferState.QUEUED,
    var bytes: Long = 0,
    var total: Long = -1,
    var fileUri: String? = null,
    var error: String? = null
) {
    val isActive: Boolean get() = state == TransferState.QUEUED || state == TransferState.RUNNING || state == TransferState.SCHEDULED
}

/** Persistent list of jobs shared by the service and the screens. */
class TransferStore private constructor(context: Context) {
    private val sp = context.getSharedPreferences("streamarc_transfers", Context.MODE_PRIVATE)
    private val gson = Gson()
    private val type = TypeToken.getParameterized(List::class.java, TransferJob::class.java).type
    private val lock = Any()

    fun all(): List<TransferJob> = synchronized(lock) {
        val json = sp.getString("jobs", null) ?: return emptyList()
        try { gson.fromJson<List<TransferJob>>(json, type) ?: emptyList() } catch (_: Exception) { emptyList() }
    }

    fun get(id: String): TransferJob? = all().firstOrNull { it.id == id }

    fun put(job: TransferJob) = synchronized(lock) { save(all().filter { it.id != job.id } + job) }

    fun update(id: String, change: (TransferJob) -> Unit) = synchronized(lock) {
        val list = all().toMutableList()
        val i = list.indexOfFirst { it.id == id }
        if (i >= 0) { change(list[i]); save(list) }
    }

    fun remove(id: String) = synchronized(lock) { save(all().filter { it.id != id }) }

    private fun save(list: List<TransferJob>) {
        sp.edit().putString("jobs", gson.toJson(list, type)).apply()
    }

    companion object {
        @Volatile private var instance: TransferStore? = null
        fun get(context: Context): TransferStore =
            instance ?: synchronized(this) { instance ?: TransferStore(context.applicationContext).also { instance = it } }
    }
}

/** Folder handling for user-picked (SAF) folders and the app's own directory. */
object Folders {
    fun appDir(context: Context, type: TransferType): File {
        val base = context.getExternalFilesDir(Environment.DIRECTORY_DOWNLOADS) ?: context.filesDir
        val dir = File(base, if (type == TransferType.RECORDING) "Recordings" else "Downloads")
        dir.mkdirs()
        return dir
    }

    /** Human-readable name of a folder choice. */
    fun describe(context: Context, folder: String?, type: TransferType): String {
        if (folder == null) return "Inside Stream Arc TV / " + (if (type == TransferType.RECORDING) "Recordings" else "Downloads")
        val uri = Uri.parse(folder)
        val name = try { DocumentFile.fromTreeUri(context, uri)?.name } catch (_: Exception) { null }
        val path = uri.lastPathSegment?.substringAfter(':')?.takeIf { it.isNotBlank() }
        return name ?: path ?: "Chosen folder"
    }

    /** True when the picked tree is still accessible (permission not revoked, storage present). */
    fun usable(context: Context, folder: String?): Boolean {
        if (folder == null) return true
        return try { DocumentFile.fromTreeUri(context, Uri.parse(folder))?.canWrite() == true } catch (_: Exception) { false }
    }

    class Target(val uri: Uri, val stream: OutputStream)

    /** Creates the output file (replacing a same-named one) and opens it for writing. */
    fun create(context: Context, folder: String?, type: TransferType, fileName: String, mime: String): Target {
        if (folder != null) {
            val tree = DocumentFile.fromTreeUri(context, Uri.parse(folder)) ?: throw IllegalStateException("Folder unavailable")
            tree.findFile(fileName)?.delete()
            val doc = tree.createFile(mime, fileName) ?: throw IllegalStateException("Can't create file in folder")
            val out = context.contentResolver.openOutputStream(doc.uri, "w") ?: throw IllegalStateException("Can't open file")
            return Target(doc.uri, out)
        }
        val f = File(appDir(context, type), fileName)
        if (f.exists()) f.delete()
        return Target(Uri.fromFile(f), f.outputStream())
    }

    /** Reopens an existing output file for appending (used to resume an interrupted download). */
    fun append(context: Context, uri: Uri): Target {
        if (uri.scheme == "file") {
            val f = File(uri.path ?: throw IllegalStateException("Bad file"))
            return Target(uri, FileOutputStream(f, true))
        }
        val out = context.contentResolver.openOutputStream(uri, "wa") ?: throw IllegalStateException("Can't reopen file")
        return Target(uri, out)
    }

    fun delete(context: Context, fileUri: String?) {
        if (fileUri == null) return
        val uri = Uri.parse(fileUri)
        try {
            if (uri.scheme == "file") File(uri.path ?: return).delete()
            else DocumentFile.fromSingleUri(context, uri)?.delete()
        } catch (_: Exception) {}
    }

    fun exists(context: Context, fileUri: String?): Boolean {
        if (fileUri == null) return false
        val uri = Uri.parse(fileUri)
        return try {
            if (uri.scheme == "file") File(uri.path ?: return false).exists()
            else DocumentFile.fromSingleUri(context, uri)?.exists() == true
        } catch (_: Exception) { false }
    }

    /**
     * "Show Name - S01E05 - Episode Title". Provider episode titles usually repeat the show name
     * and the episode code, sometimes with release tags; those are stripped, and if nothing
     * meaningful is left the file is just "Show Name - S01E05".
     */
    fun episodeFileName(series: String, season: Int, episode: Int, episodeTitle: String?): String {
        val show = cleanTitle(series)
        val code = "S%02dE%02d".format(season, episode)
        var ep = episodeTitle.orEmpty()
        ep = ep.replace(series, "", ignoreCase = true).replace(show, "", ignoreCase = true)
        ep = ep.replace(Regex("(?i)\\bS\\d{1,2}\\s*E\\d{1,3}\\b"), "")
            .replace(Regex("(?i)\\b\\d{1,2}x\\d{1,3}\\b"), "")
            .replace(Regex("(?i)\\bseason\\s*\\d+\\b"), "")
            .replace(Regex("(?i)\\bepisode\\s*\\d+\\b"), "")
        ep = cleanTitle(ep).trim(' ', '-', '–', ':', '.', '_', '|')
        return if (ep.isBlank() || ep.length > 70) "$show - $code" else "$show - $code - $ep"
    }

    /** Movie names minus provider tags such as [4K], (MULTI-SUB), 1080p, HEVC. */
    fun movieFileName(name: String): String = cleanTitle(name)

    private fun cleanTitle(s: String): String = s
        .replace(Regex("\\[[^\\]]*]"), " ")                                   // [4K], [EN]
        .replace(Regex("(?i)\\((?:multi|sub|dub|eng|hd|uhd|4k|hdr)[^)]*\\)"), " ")  // (MULTI-SUB)
        .replace(Regex("(?i)\\b(2160p|1080p|720p|480p|4k|uhd|hdr10?|hevc|x26[45]|h\\.?26[45]|web-?dl|webrip|bluray|hdtv)\\b"), " ")
        .replace(Regex("\\s*[-–|:]\\s*$"), "")
        .replace(Regex("\\s+"), " ")
        .trim(' ', '-', '–', '|')

    fun safeName(name: String): String =
        name.replace(Regex("[\\\\/:*?\"<>|\\x00-\\x1F]"), " ").replace(Regex("\\s+"), " ").trim().take(120).ifBlank { "video" }
}
