package com.streamarc.tv.update

import android.app.Activity
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
import com.streamarc.tv.data.XtreamApi
import com.streamarc.tv.databinding.DialogDownloadBinding
import com.streamarc.tv.util.AppLog
import okhttp3.Call
import okhttp3.OkHttpClient
import okhttp3.Request
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.File
import java.io.FileInputStream
import java.security.MessageDigest

/**
 * Downloads a release APK inside the app, shows the progress in a
 * dialog, verifies the SHA-256 from the update feed, and hands the file to
 * the package installer.
 */
object ApkInstaller {

    @Volatile private var active = false
    @Volatile private var call: Call? = null

    private val client: OkHttpClient by lazy {
        XtreamApi.client.newBuilder()
            .connectTimeout(20, java.util.concurrent.TimeUnit.SECONDS)
            .readTimeout(30, java.util.concurrent.TimeUnit.SECONDS)
            .callTimeout(0, java.util.concurrent.TimeUnit.MILLISECONDS)
            .followRedirects(true).followSslRedirects(true)
            .build()
    }

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

        if (active) {
            Toast.makeText(app, "An update is already downloading.", Toast.LENGTH_SHORT).show()
            return
        }

        val dir = app.getExternalFilesDir(Environment.DIRECTORY_DOWNLOADS) ?: app.filesDir
        dir.mkdirs()
        val file = File(dir, release.assetName)
        if (file.exists()) file.delete()

        if (activity is AppCompatActivity && !activity.isFinishing) {
            showProgress(activity, file, release)
        } else {
            Toast.makeText(app, "Open Stream Arc TV and press Update to download v${release.versionName}.", Toast.LENGTH_LONG).show()
        }
    }

    /**
     * The download happens inside the app, over the same kind of connection the sign-in and the
     * video use. Android's own download manager only runs when the system considers the network
     * "validated" and sat on "waiting for network" forever on boxes where that check fails even
     * though the internet works.
     */
    private fun showProgress(activity: AppCompatActivity, file: File, release: UpdateChecker.Release) {
        val vb = DialogDownloadBinding.inflate(LayoutInflater.from(activity))
        vb.txtStatus.text = "Starting download…"
        vb.progress.isIndeterminate = true

        val dialog = AlertDialog.Builder(activity)
            .setTitle("Downloading v${release.versionName}")
            .setView(vb.root)
            .setCancelable(false)
            .setNegativeButton("Cancel") { _, _ -> call?.cancel() }
            .create()
        dialog.show()
        active = true

        activity.lifecycleScope.launch {
            var failure: Exception? = null
            val ok = try {
                withContext(Dispatchers.IO) {
                    fetch(release, file) { downloaded, total ->
                        activity.runOnUiThread {
                            if (total > 0) {
                                vb.progress.isIndeterminate = false
                                vb.progress.max = 1000
                                vb.progress.progress = ((downloaded * 1000) / total).toInt().coerceIn(0, 1000)
                                vb.txtStatus.text = "${UpdateChecker.formatSize(downloaded)} of ${UpdateChecker.formatSize(total)}"
                            } else {
                                vb.txtStatus.text = UpdateChecker.formatSize(downloaded)
                            }
                        }
                    }
                }
            } catch (e: Exception) {
                failure = e
                false
            } finally {
                active = false
                call = null
            }

            if (!ok) {
                dialog.dismiss()
                file.delete()
                val cancelled = failure == null || (failure is java.io.IOException && failure.message?.contains("Canceled", true) == true)
                if (!cancelled) {
                    val why = com.streamarc.tv.data.Diagnose.explain(activity, failure!!)
                    AppLog.e("Update", "download failed: ${failure.javaClass.simpleName}: ${failure.message}", failure)
                    Toast.makeText(activity, "Update download failed: $why", Toast.LENGTH_LONG).show()
                }
                return@launch
            }

            vb.progress.isIndeterminate = true
            vb.txtStatus.text = "Verifying download…"
            val verified = withContext(Dispatchers.IO) { verify(file, release) }
            dialog.dismiss()
            if (!verified) {
                AppLog.e("Update", "downloaded file failed verification (${file.length()} bytes, expected ${release.sizeBytes})")
                file.delete()
                Toast.makeText(activity, "The downloaded update didn't match the release. Please try again.", Toast.LENGTH_LONG).show()
                return@launch
            }
            install(activity, file)
        }
    }

    /** Streams the APK into [file], resuming with a Range request after a dropped connection. */
    private fun fetch(release: UpdateChecker.Release, file: File, onProgress: (Long, Long) -> Unit): Boolean {
        var attempt = 0
        var total = release.sizeBytes
        AppLog.i("Update", "downloading ${release.apkUrl}")
        while (true) {
            val have = if (file.exists()) file.length() else 0L
            val req = Request.Builder().url(release.apkUrl)
                .header("User-Agent", "StreamArcTV")
                .apply { if (have > 0) header("Range", "bytes=$have-") }
                .build()
            val c = client.newCall(req)
            call = c
            try {
                c.execute().use { resp ->
                    if (resp.code == 416) return@use   // already have all of it
                    if (!resp.isSuccessful) throw java.io.IOException("HTTP ${resp.code}")
                    val resumed = resp.code == 206 && have > 0
                    val len = resp.body?.contentLength() ?: -1L
                    if (len > 0) total = if (resumed) have + len else len
                    val body = resp.body ?: throw java.io.IOException("Empty response")
                    java.io.FileOutputStream(file, resumed).use { out ->
                        var done = if (resumed) have else 0L
                        var last = 0L
                        val buf = ByteArray(64 * 1024)
                        body.byteStream().use { input ->
                            while (true) {
                                val n = input.read(buf)
                                if (n < 0) break
                                out.write(buf, 0, n)
                                done += n
                                val now = android.os.SystemClock.elapsedRealtime()
                                if (now - last >= 250) { last = now; onProgress(done, total) }
                            }
                        }
                        onProgress(done, total)
                    }
                }
                if (total <= 0 || file.length() >= total) {
                    AppLog.i("Update", "download complete (${file.length()} bytes)")
                    return true
                }
                throw java.io.IOException("Connection ended early (${file.length()} of $total bytes)")
            } catch (e: java.io.IOException) {
                if (c.isCanceled()) return false
                attempt++
                AppLog.w("Update", "download interrupted (attempt $attempt): ${e.message}")
                if (attempt >= 5) throw e
                try { Thread.sleep(1500L * attempt) } catch (_: InterruptedException) { return false }
            }
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
