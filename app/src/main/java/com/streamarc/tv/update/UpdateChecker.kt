package com.streamarc.tv.update

import android.app.Activity
import android.net.Uri
import android.widget.Toast
import androidx.appcompat.app.AlertDialog
import androidx.appcompat.app.AppCompatActivity
import androidx.lifecycle.lifecycleScope
import com.google.gson.JsonObject
import com.google.gson.JsonParser
import com.streamarc.tv.BuildConfig
import com.streamarc.tv.data.Prefs
import com.streamarc.tv.data.XtreamApi
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import okhttp3.Request

/**
 * Checks the repository's `release/update.json` feed (see UPDATE_FORMAT.md)
 * for a newer build and offers to download and install it, with a
 * "skip this version" option.
 *
 * The feed is the source of truth: it names the APK asset attached to the
 * matching GitHub release (`v<versionName>`) and carries its SHA-256 and size
 * so the download can be verified before it is handed to the installer.
 * The release notes are fetched from the GitHub release itself, best-effort.
 */
object UpdateChecker {

    data class Release(
        val versionName: String,
        val versionCode: Int,
        val notes: String,
        val apkUrl: String,
        val assetName: String,
        val sha256: String?,
        val sizeBytes: Long,
        val htmlUrl: String
    )

    private const val SUPPORTED_SCHEMA = 1

    private val repo: String get() = BuildConfig.GITHUB_REPO

    // The Contents API is not behind GitHub's 5-minute raw-file CDN cache, so a
    // check right after a release sees the new feed. raw.githubusercontent.com
    // is the fallback if the API is unavailable or rate-limited.
    private val feedApiUrl: String
        get() = "https://api.github.com/repos/$repo/contents/release/update.json?ref=main"

    private val feedRawUrl: String
        get() = "https://raw.githubusercontent.com/$repo/main/release/update.json"

    /**
     * @param manual true when the user pressed the Update button: always
     *   reports the outcome and ignores the "skipped" version.
     */
    fun check(activity: AppCompatActivity, manual: Boolean) {
        val prefs = Prefs(activity)
        activity.lifecycleScope.launch {
            if (manual) toast(activity, "Checking for updates…")
            val release = try {
                withContext(Dispatchers.IO) { fetchLatest() }
            } catch (e: Exception) {
                if (manual) toast(activity, "Couldn't check for updates: ${e.message}")
                return@launch
            }
            if (release.versionCode <= BuildConfig.VERSION_CODE) {
                if (manual) toast(activity, "You're on the latest version (v${BuildConfig.VERSION_NAME})")
                return@launch
            }
            if (!manual && prefs.skippedVersion == release.versionName) return@launch
            if (activity.isFinishing || activity.isDestroyed) return@launch
            showDialog(activity, release, prefs)
        }
    }

    /** Reads and validates the update feed. Throws with a user-readable message on failure. */
    fun fetchLatest(): Release {
        val feed = try {
            getJson(feedApiUrl, accept = "application/vnd.github.raw")
        } catch (_: Exception) {
            null
        } ?: getJson(feedRawUrl, accept = "application/json")
            ?: throw IllegalStateException("Update feed not found")

        val schema = feed.get("schemaVersion")?.asInt ?: 0
        if (schema != SUPPORTED_SCHEMA) throw IllegalStateException("Unsupported update feed (schema $schema)")

        val pkg = feed.get("packageName")?.asString
        if (pkg != BuildConfig.APPLICATION_ID) throw IllegalStateException("Update feed is for a different app")

        val versionName = feed.get("versionName")?.asString?.trim()?.takeIf { it.isNotEmpty() }
            ?: throw IllegalStateException("Update feed has no version")
        val versionCode = feed.get("versionCode")?.asInt
            ?: throw IllegalStateException("Update feed has no version code")

        val apk = feed.getAsJsonObject("apk") ?: throw IllegalStateException("Update feed has no APK entry")
        val assetName = apk.get("assetName")?.asString?.trim()?.takeIf { it.isNotEmpty() }
            ?: throw IllegalStateException("Update feed has no APK name")
        val sha256 = apk.get("sha256")?.asString?.trim()?.lowercase()
            ?.takeIf { it.length == 64 && it.all { c -> c in '0'..'9' || c in 'a'..'f' } }
        val sizeBytes = apk.get("sizeBytes")?.asLong ?: 0L

        val tag = "v$versionName"
        val apkUrl = "https://github.com/$repo/releases/download/$tag/${Uri.encode(assetName)}"
        val htmlUrl = "https://github.com/$repo/releases/tag/$tag"

        // Release notes come from the GitHub release body when available.
        val notes = try {
            getJson("https://api.github.com/repos/$repo/releases/tags/$tag", accept = "application/vnd.github+json")
                ?.get("body")?.takeIf { !it.isJsonNull }?.asString ?: ""
        } catch (_: Exception) {
            ""
        }

        return Release(versionName, versionCode, notes, apkUrl, assetName, sha256, sizeBytes, htmlUrl)
    }

    /** GET a JSON document; returns null on 404. */
    private fun getJson(url: String, accept: String): JsonObject? {
        val req = Request.Builder()
            .url(url)
            .header("Accept", accept)
            .header("User-Agent", XtreamApi.USER_AGENT)
            .header("Cache-Control", "no-cache")
            .build()
        XtreamApi.client.newCall(req).execute().use { resp ->
            if (resp.code == 404) return null
            if (!resp.isSuccessful) throw IllegalStateException("GitHub returned HTTP ${resp.code}")
            val body = resp.body?.string() ?: ""
            return JsonParser.parseString(body).asJsonObject
        }
    }

    private fun showDialog(activity: Activity, release: Release, prefs: Prefs) {
        val notes = release.notes.ifBlank { "No release notes provided." }
        val size = if (release.sizeBytes > 0) " (${formatSize(release.sizeBytes)})" else ""
        val message = "Version ${release.versionName}$size is available (you have v${BuildConfig.VERSION_NAME}).\n\n" +
            "What's new:\n\n" + plainTextFromMarkdown(notes)

        val dialog = AlertDialog.Builder(activity)
            .setTitle("Update available — v${release.versionName}")
            .setMessage(message)
            .setPositiveButton("Update now") { _, _ -> ApkInstaller.download(activity, release) }
            .setNegativeButton("Skip this version") { _, _ ->
                prefs.skippedVersion = release.versionName
                toast(activity, "v${release.versionName} skipped. You'll be asked again for the next version.")
            }
            .setNeutralButton("Later", null)
            .create()
        dialog.show()
        // Positive button gets initial focus for D-pad users.
        dialog.getButton(AlertDialog.BUTTON_POSITIVE)?.requestFocus()
    }

    fun formatSize(bytes: Long): String = when {
        bytes >= 1L shl 30 -> String.format("%.1f GB", bytes / (1L shl 30).toDouble())
        bytes >= 1L shl 20 -> String.format("%.1f MB", bytes / (1L shl 20).toDouble())
        bytes >= 1L shl 10 -> String.format("%.0f KB", bytes / (1L shl 10).toDouble())
        else -> "$bytes B"
    }

    private fun plainTextFromMarkdown(md: String): String =
        md.lines().joinToString("\n") { line ->
            var l = line.trimEnd()
            l = l.replace(Regex("^#{1,6}\\s*"), "")
            l = l.replace(Regex("^\\s*[-*]\\s+"), "• ")
            l = l.replace("**", "").replace("__", "")
            l = l.replace("`", "")
            l
        }.trim()

    private fun toast(activity: Activity, msg: String) {
        Toast.makeText(activity, msg, Toast.LENGTH_LONG).show()
    }
}
