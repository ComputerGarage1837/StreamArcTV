package com.streamarc.tv.data

import android.content.Context
import com.streamarc.tv.BuildConfig
import org.json.JSONArray
import org.json.JSONObject

/**
 * Everything worth carrying to a new box in one JSON file: settings, favourites, hidden
 * categories and channels, default category, layout, buffer, multi-view slots, watched
 * positions, VOD users (each with their own watched list and My List), and (optionally) the sign-in details.
 */
object Backup {
    private const val SCHEMA = 1
    private const val PREFS = "streamarc"
    private const val WATCH = "streamarc_watch"
    /** Keys that describe this device rather than the user's choices. */
    private val skip = setOf("crashed", "install_permission_asked", "last_auto_backup")

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
        // Every other user's watched list and progress.
        val wsp = context.getSharedPreferences(WATCH, Context.MODE_PRIVATE)
        val others = JSONObject()
        for ((key, value) in wsp.all) if (key.startsWith("entries_") && value is String) others.put(key, JSONObject(value))
        if (others.length() > 0) root.put("watchUsers", others)
        return root.toString(2)
    }

    private const val WEEK_MS = 7L * 24 * 3600 * 1000

    /**
     * Silent weekly backup of everything (settings, users and their lists, watched positions, sign-ins)
     * into the app's own folder. Only the newest file is kept, so it never grows. Returns the file written, or null.
     */
    fun autoBackupIfDue(context: Context): java.io.File? {
        val sp = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        val last = sp.getLong("last_auto_backup", 0L)
        val now = System.currentTimeMillis()
        if (last != 0L && now - last < WEEK_MS) return null
        val dir = java.io.File(context.getExternalFilesDir(null) ?: context.filesDir, "backups")
        if (!dir.exists() && !dir.mkdirs()) return null
        val stamp = java.text.SimpleDateFormat("yyyy-MM-dd", java.util.Locale.US).format(java.util.Date(now))
        val file = java.io.File(dir, "stream-arc-tv-auto-backup-$stamp.json")
        val tmp = java.io.File(dir, "auto-backup.tmp")
        tmp.writeText(export(context, includeAccounts = true))
        if (!tmp.renameTo(file)) { file.writeText(tmp.readText()); tmp.delete() }
        // Delete every earlier automatic backup.
        dir.listFiles()?.filter { it.name.startsWith("stream-arc-tv-auto-backup-") && it != file }?.forEach { it.delete() }
        sp.edit().putLong("last_auto_backup", now).apply()
        return file
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
        // Other users' lists on this device are replaced by the backup's, not mixed with them.
        val wsp = context.getSharedPreferences(WATCH, Context.MODE_PRIVATE)
        val stale = wsp.all.keys.filter { it.startsWith("entries_") }
        if (stale.isNotEmpty() && (root.has("watchUsers") || prefs.has("vod_users"))) {
            val e = wsp.edit(); stale.forEach { e.remove(it) }; e.commit()
        }
        root.optJSONObject("watchUsers")?.let { o ->
            val ed2 = wsp.edit()
            for (key in o.keys()) { ed2.putString(key, o.getJSONObject(key).toString()); watched += o.getJSONObject(key).length() }
            ed2.commit()
        }
        VodUsers.reload()
        return "$count settings, $watched watched items"
    }
}
