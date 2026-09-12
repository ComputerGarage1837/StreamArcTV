package com.streamarc.tv.ui

import android.app.Activity
import android.widget.Toast
import androidx.appcompat.app.AlertDialog
import com.streamarc.tv.R
import com.streamarc.tv.data.DownloadLocation
import com.streamarc.tv.data.Downloads

/** Asks where to save, then queues the download. */
object DownloadDialogs {
    fun askAndStart(activity: Activity, title: String, subtitle: String, url: String, ext: String) {
        val options = mutableListOf(activity.getString(R.string.location_app))
        val values = mutableListOf(DownloadLocation.APP)
        if (Downloads.publicAllowed()) {
            options.add(activity.getString(R.string.location_public))
            values.add(DownloadLocation.PUBLIC)
        }
        AlertDialog.Builder(activity)
            .setTitle(activity.getString(R.string.download_where_fmt, title))
            .setItems(options.toTypedArray()) { _, which ->
                try {
                    Downloads.start(activity, title, subtitle, url, ext, values[which])
                    Toast.makeText(activity, activity.getString(R.string.download_started_fmt, title), Toast.LENGTH_SHORT).show()
                } catch (e: Exception) {
                    Toast.makeText(activity, activity.getString(R.string.download_failed_fmt, e.message ?: ""), Toast.LENGTH_LONG).show()
                }
            }
            .setNegativeButton(android.R.string.cancel, null)
            .show()
    }
}
