package com.streamarc.tv.ui

import android.content.ActivityNotFoundException
import android.content.Intent
import android.net.Uri
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AppCompatActivity

/**
 * Opens the system folder picker. Must be created before the activity is
 * started (a property initialiser is fine). Calls back with the picked tree
 * URI, or null when the user cancelled or the device has no picker (Android
 * TV boxes often don't), in which case callers fall back to the app folder.
 */
class FolderPicker(private val activity: AppCompatActivity) {
    private var callback: ((Uri?) -> Unit)? = null

    private val launcher = activity.registerForActivityResult(ActivityResultContracts.OpenDocumentTree()) { uri ->
        if (uri != null) {
            try {
                activity.contentResolver.takePersistableUriPermission(
                    uri, Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION
                )
            } catch (_: Exception) {}
        }
        callback?.invoke(uri)
        callback = null
    }

    /** Returns false when no picker exists on this device. */
    fun launch(onPicked: (Uri?) -> Unit): Boolean {
        callback = onPicked
        return try {
            launcher.launch(null)
            true
        } catch (_: ActivityNotFoundException) {
            callback = null
            false
        }
    }
}
