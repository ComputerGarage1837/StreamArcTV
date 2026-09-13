package com.chocolaterabbit.app.data

import android.content.Context
import android.content.SharedPreferences

class Prefs(context: Context) {
    private val sp: SharedPreferences =
        context.getSharedPreferences("chocolate_rabbit", Context.MODE_PRIVATE)

    /** Version name the user chose to skip in the update prompt. */
    var skippedVersion: String?
        get() = sp.getString("skipped_version", null)
        set(value) { sp.edit().putString("skipped_version", value).apply() }

    /** Check the release feed once per launch. */
    var autoCheckUpdates: Boolean
        get() = sp.getBoolean("auto_check_updates", true)
        set(value) { sp.edit().putBoolean("auto_check_updates", value).apply() }

    /** The one-time "allow installs from this app" prompt has been shown. */
    var installPermissionAsked: Boolean
        get() = sp.getBoolean("install_permission_asked", false)
        set(value) { sp.edit().putBoolean("install_permission_asked", value).apply() }

    /** Remembered so the fields can be pre-filled if the app is reopened. */
    var lastName: String
        get() = sp.getString("last_name", "") ?: ""
        set(value) { sp.edit().putString("last_name", value).apply() }

    var lastEmail: String
        get() = sp.getString("last_email", "") ?: ""
        set(value) { sp.edit().putString("last_email", value).apply() }
}
