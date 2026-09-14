package com.streamarc.tv.data

import android.content.Context
import com.streamarc.tv.BuildConfig
import org.json.JSONArray
import org.json.JSONObject

/**
 * Everything worth carrying to a new box in one JSON file: settings, favourites, hidden
 * categories and channels, default category, layout, buffer, multi-view slots, watched
 * positions, and (optionally) the sign-in details.
 */
object Backup {
    private const val SCHEMA = 1
    private const val PREFS = "streamarc"
    private const val WATCH = "streamarc_watch"
    /** Keys that describe this device rather than the user's choices. */
    private val skip = setOf("crashed", "install_permission_asked")

    fun export(context: Context, includeAccounts: Boolean): String {
        val root = JSONObject()
        root.put("schema", SCHEMA)
        root.put("app", BuildConfig.VERSION_NAME)
        root.put("exportedAt", System.currentTimeMillis())
        val prefs = JSONObject()
        val sp = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        for ((key, value) in sp.all) {
            if (key in skip) continue
            // Accounts are stored as <service>_user / _pass / _status / _exp.
            if (!includeAccounts && (key.endsWith("_user") || key.endsWith("_pass") || key.endsWith("_status") || key.endsWith("_exp"))) continue
            val entry = JSONObject()
            when (value) {
                is String -> { entry.put("t", "s"); entry.put("v", value) }
                is Boolean -> { entry.put("t", "b"); entry.put("v", value) }
                is Int -> { entry.put("t", "i"); entry.put("v", value) }
                is Long -> { entry.put("t", "l"); entry.put("v", value) }
                is Float -> { entry.put("t", "f"); entry.put("v", value.toDouble()) }
                is Set<*> -> { entry.put("t", "set"); entry.put("v", JSONArray(value.map { it.toString() })) }
                else -> continue
            }
            prefs.put(key, entry)
        }
        root.put("prefs", prefs)
        val watch = context.getSharedPreferences(WATCH, Context.MODE_PRIVATE).getString("entries", null)
        if (watch != null) root.put("watch", JSONObject(watch))
        return root.toString(2)
    }

    /** Applies a backup; returns a short summary. Existing values for the same keys are replaced. */
    fun import(context: Context, json: String): String {
        val root = JSONObject(json)
        require(root.optInt("schema", 0) in 1..SCHEMA) { "This file is not a Stream Arc TV backup." }
        val prefs = root.getJSONObject("prefs")
        val sp = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        val ed = sp.edit()
        var count = 0
        for (key in prefs.keys()) {
            if (key in skip) continue
            val entry = prefs.getJSONObject(key)
            when (entry.getString("t")) {
                "s" -> ed.putString(key, entry.getString("v"))
                "b" -> ed.putBoolean(key, entry.getBoolean("v"))
                "i" -> ed.putInt(key, entry.getInt("v"))
                "l" -> ed.putLong(key, entry.getLong("v"))
                "f" -> ed.putFloat(key, entry.getDouble("v").toFloat())
                "set" -> { val a = entry.getJSONArray("v"); ed.putStringSet(key, (0 until a.length()).map { a.getString(it) }.toSet()) }
                else -> continue
            }
            count++
        }
        ed.commit()
        var watched = 0
        root.optJSONObject("watch")?.let { w ->
            context.getSharedPreferences(WATCH, Context.MODE_PRIVATE).edit().putString("entries", w.toString()).commit()
            WatchProgress.reload()
            watched = w.length()
        }
        return "$count settings, $watched watched items"
    }
}
