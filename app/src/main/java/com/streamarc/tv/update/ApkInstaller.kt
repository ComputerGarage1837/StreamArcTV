package com.streamarc.tv.update

import android.app.Activity
import android.app.DownloadManager
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.os.Environment
import android.provider.Settings
import android.view.LayoutInflater
import android.widget.Toast
import androidx.appcompat.app.AlertDialog
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.FileProvider
import androidx.lifecycle.lifecycleScope
import com.streamarc.tv.BuildConfig
import com.streamarc.tv.databinding.DialogDownloadBinding
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.File
import java.io.FileInputStream
import java.security.MessageDigest

/**
 * Downloads a release APK with DownloadManager, shows the progress in a
 * dialog, verifies the SHA-256 from the update feed, and hands the file to
 * the package installer.
 */
object ApkInstaller {

    private var activeDownloadId: Long = -1L

    fun download(activity: Activity, release: UpdateChecker.Release) {
        val app = activity.applicationContext

        // Ask for install permission up front on Android 8+ so the install
        // prompt can appear as soon as the download completes.
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O &&
            !app.packageManager.canRequestPackageInstalls()
        ) {
            Toast.makeText(
                app,
                "Allow Stream Arc TV to install updates, then press Update again.",
                Toast.LENGTH_LONG
            ).show()
            val i = Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES)
                .setData(Uri.parse("package:${app.packageName}"))
            try {
                activity.startActivity(i)
            } catch (_: Exception) {
                try {
                    activity.startActivity(Intent(Settings.ACTION_SECURITY_SETTINGS))
                } catch (_: Exception) {
                    Toast.makeText(app, "Enable \"Install unknown apps\" for Stream Arc TV in Settings.", Toast.LENGTH_LONG).show()
                }
            }
            return
        }

        if (activeDownloadId != -1L) {
            Toast.makeText(app, "An update is already downloading.", Toast.LENGTH_SHORT).show()
            return
        }

        val dir = app.getExternalFilesDir(Environment.DIRECTORY_DOWNLOADS) ?: app.filesDir
        dir.mkdirs()
        val file = File(dir, release.assetName)
        if (file.exists()) file.delete()

        val dm = app.getSystemService(Context.DOWNLOAD_SERVICE) as DownloadManager
        val req = DownloadManager.Request(Uri.parse(release.apkUrl))
            .setTitle("Stream Arc TV v${release.versionName}")
            .setDescription("Downloading update…")
            .setMimeType("application/vnd.android.package-archive")
            .setNotificationVisibility(DownloadManager.Request.VISIBILITY_VISIBLE)
            .setDestinationUri(Uri.fromFile(file))
            .addRequestHeader("User-Agent", "StreamArcTV")

        val id = try {
            dm.enqueue(req)
        } catch (e: Exception) {
            Toast.makeText(app, "Couldn't start download: ${e.message}", Toast.LENGTH_LONG).show()
            return
        }
        activeDownloadId = id

        if (activity is AppCompatActivity && !activity.isFinishing) {
            showProgress(activity, dm, id, file, release)
        } else {
            Toast.makeText(app, "Downloading v${release.versionName}…", Toast.LENGTH_SHORT).show()
        }
    }

    private fun showProgress(
        activity: AppCompatActivity,
        dm: DownloadManager,
        id: Long,
        file: File,
        release: UpdateChecker.Release
    ) {
        val vb = DialogDownloadBinding.inflate(LayoutInflater.from(activity))
        vb.txtStatus.text = "Starting download…"
        vb.progress.isIndeterminate = true

        val dialog = AlertDialog.Builder(activity)
            .setTitle("Downloading v${release.versionName}")
            .setView(vb.root)
            .setCancelable(false)
            .setNegativeButton("Cancel") { _, _ ->
                dm.remove(id)
                activeDownloadId = -1L
            }
            .create()
        dialog.show()

        activity.lifecycleScope.launch {
            var status = DownloadManager.STATUS_PENDING
            var reason = 0
            while (isActive) {
                var downloaded = 0L
                var total = release.sizeBytes
                dm.query(DownloadManager.Query().setFilterById(id))?.use { c ->
                    if (!c.moveToFirst()) {
                        // Row gone: the download was cancelled/removed.
                        status = DownloadManager.STATUS_FAILED
                        reason = -1
                        return@use
                    }
                    status = c.getInt(c.getColumnIndexOrThrow(DownloadManager.COLUMN_STATUS))
                    reason = c.getInt(c.getColumnIndexOrThrow(DownloadManager.COLUMN_REASON))
                    downloaded = c.getLong(c.getColumnIndexOrThrow(DownloadManager.COLUMN_BYTES_DOWNLOADED_SO_FAR))
                    val t = c.getLong(c.getColumnIndexOrThrow(DownloadManager.COLUMN_TOTAL_SIZE_BYTES))
                    if (t > 0) total = t
                }
                when (status) {
                    DownloadManager.STATUS_SUCCESSFUL, DownloadManager.STATUS_FAILED -> break
                    DownloadManager.STATUS_PAUSED -> vb.txtStatus.text = "Waiting for network…"
                    DownloadManager.STATUS_PENDING -> vb.txtStatus.text = "Starting download…"
                    else -> {
                        if (total > 0) {
                            vb.progress.isIndeterminate = false
                            vb.progress.max = 1000
                            vb.progress.progress = ((downloaded * 1000) / total).toInt().coerceIn(0, 1000)
                            vb.txtStatus.text =
                                "${UpdateChecker.formatSize(downloaded)} of ${UpdateChecker.formatSize(total)}"
                        } else {
                            vb.txtStatus.text = UpdateChecker.formatSize(downloaded)
                        }
                    }
                }
                delay(500)
            }
            activeDownloadId = -1L

            if (status != DownloadManager.STATUS_SUCCESSFUL) {
                dialog.dismiss()
                if (reason != -1) {
                    Toast.makeText(activity, "Update download failed (${describe(reason)}). Please try again.", Toast.LENGTH_LONG).show()
                }
                return@launch
            }

            vb.progress.isIndeterminate = true
            vb.txtStatus.text = "Verifying download…"
            val ok = withContext(Dispatchers.IO) { verify(file, release) }
            dialog.dismiss()
            if (!ok) {
                file.delete()
                Toast.makeText(activity, "The downloaded update didn't match the release. Please try again.", Toast.LENGTH_LONG).show()
                return@launch
            }
            install(activity, file)
        }
    }

    /** Verifies size and SHA-256 against the update feed when they are present. */
    private fun verify(file: File, release: UpdateChecker.Release): Boolean {
        if (!file.exists() || file.length() == 0L) return false
        if (release.sizeBytes > 0 && file.length() != release.sizeBytes) return false
        val expected = release.sha256 ?: return true
        val md = MessageDigest.getInstance("SHA-256")
        FileInputStream(file).use { input ->
            val buf = ByteArray(64 * 1024)
            while (true) {
                val n = input.read(buf)
                if (n <= 0) break
                md.update(buf, 0, n)
            }
        }
        val actual = md.digest().joinToString("") { "%02x".format(it) }
        return actual == expected
    }

    private fun describe(reason: Int): String = when (reason) {
        DownloadManager.ERROR_CANNOT_RESUME -> "cannot resume"
        DownloadManager.ERROR_DEVICE_NOT_FOUND -> "storage not available"
        DownloadManager.ERROR_FILE_ALREADY_EXISTS -> "file already exists"
        DownloadManager.ERROR_FILE_ERROR -> "file error"
        DownloadManager.ERROR_HTTP_DATA_ERROR -> "network data error"
        DownloadManager.ERROR_INSUFFICIENT_SPACE -> "not enough space"
        DownloadManager.ERROR_TOO_MANY_REDIRECTS -> "too many redirects"
        DownloadManager.ERROR_UNHANDLED_HTTP_CODE -> "server error"
        404 -> "APK not found on the release"
        else -> "error $reason"
    }

    fun install(context: Context, file: File) {
        val intent = Intent(Intent.ACTION_VIEW).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.N) {
            val uri = FileProvider.getUriForFile(
                context, "${BuildConfig.APPLICATION_ID}.fileprovider", file
            )
            intent.setDataAndType(uri, "application/vnd.android.package-archive")
            intent.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
        } else {
            @Suppress("DEPRECATION")
            intent.setDataAndType(Uri.fromFile(file), "application/vnd.android.package-archive")
        }
        try {
            context.startActivity(intent)
        } catch (e: Exception) {
            Toast.makeText(context, "Couldn't open installer: ${e.message}", Toast.LENGTH_LONG).show()
        }
    }
}
