package com.streamarc.tv.data

import android.content.Context
import android.content.SharedPreferences

class Prefs(context: Context) {

    private val sp: SharedPreferences =
        context.applicationContext.getSharedPreferences("streamarc", Context.MODE_PRIVATE)

    // ---- Accounts ------------------------------------------------------

    fun account(service: Service): Account? {
        val user = sp.getString(k(service, "user"), null) ?: return null
        val pass = sp.getString(k(service, "pass"), null) ?: return null
        return Account(
            username = user,
            password = pass,
            status = sp.getString(k(service, "status"), null),
            expDate = sp.getLong(k(service, "exp"), -1L).takeIf { it > 0 },
            maxConnections = sp.getString(k(service, "maxc"), null),
            activeConnections = sp.getString(k(service, "activec"), null),
            createdAt = sp.getLong(k(service, "created"), -1L).takeIf { it > 0 },
            isTrial = sp.getBoolean(k(service, "trial"), false)
        )
    }

    fun isSignedIn(service: Service): Boolean = account(service) != null

    fun saveAccount(service: Service, a: Account) {
        sp.edit()
            .putString(k(service, "user"), a.username)
            .putString(k(service, "pass"), a.password)
            .putString(k(service, "status"), a.status)
            .putLong(k(service, "exp"), a.expDate ?: -1L)
            .putString(k(service, "maxc"), a.maxConnections)
            .putString(k(service, "activec"), a.activeConnections)
            .putLong(k(service, "created"), a.createdAt ?: -1L)
            .putBoolean(k(service, "trial"), a.isTrial)
            .apply()
    }

    fun clearAccount(service: Service) {
        val e = sp.edit()
        listOf("user", "pass", "status", "exp", "maxc", "activec", "created", "trial")
            .forEach { e.remove(k(service, it)) }
        e.apply()
    }

    // ---- Favorites -----------------------------------------------------

    fun favorites(service: Service): Set<String> =
        sp.getStringSet(k(service, "favs"), emptySet())?.toSet() ?: emptySet()

    fun isFavorite(service: Service, streamId: String?): Boolean =
        streamId != null && favorites(service).contains(streamId)

    /** Adds or removes the stream; returns true when it is now a favorite. */
    fun toggleFavorite(service: Service, streamId: String): Boolean {
        val set = favorites(service).toMutableSet()
        val nowFav = if (set.contains(streamId)) { set.remove(streamId); false } else { set.add(streamId); true }
        sp.edit().putStringSet(k(service, "favs"), set).apply()
        return nowFav
    }

    // ---- Updates -------------------------------------------------------

    var skippedVersion: String?
        get() = sp.getString("skipped_version", null)
        set(value) { sp.edit().putString("skipped_version", value).apply() }

    var autoCheckUpdates: Boolean
        get() = sp.getBoolean("auto_check_updates", true)
        set(value) { sp.edit().putBoolean("auto_check_updates", value).apply() }

    // ---- Playback ------------------------------------------------------

    /** "m3u8" (HLS) or "ts" (MPEG-TS) for live streams. */
    var liveFormat: String
        get() = sp.getString("live_format", "m3u8") ?: "m3u8"
        set(value) { sp.edit().putString("live_format", value).apply() }

    private fun k(s: Service, key: String) = "${s.name.lowercase()}_$key"
}
