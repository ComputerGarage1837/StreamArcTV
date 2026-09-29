package com.streamarc.tv.ui

import android.app.Activity
import com.streamarc.tv.R
import com.streamarc.tv.util.AppLog

/** Saves the log as a file and tells the user where it went (or why it couldn't). */
fun saveLog(activity: Activity) {
    val where = try {
        AppLog.save(activity)
    } catch (e: Exception) {
        FocusDialog(activity)
            .setTitle(R.string.export_logs)
            .setMessage(activity.getString(R.string.log_save_failed_fmt, e.message ?: "unknown error"))
            .setPositiveButton(android.R.string.ok, null)
            .show()
        return
    }
    FocusDialog(activity)
        .setTitle(R.string.log_saved_title)
        .setMessage(activity.getString(R.string.log_saved_msg, where))
        .setPositiveButton(android.R.string.ok, null)
        .show()
}
