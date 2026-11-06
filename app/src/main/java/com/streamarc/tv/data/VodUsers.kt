package com.streamarc.tv.data

import android.content.Context
import android.content.SharedPreferences
import org.json.JSONArray
import org.json.JSONObject

/**
 * Viewers on the VOD side. Each has their own watched list and progress, My List, sort choice and
 * new-episode memory. The first user keeps the unprefixed keys (so everything that existed before
 * users were added simply belongs to them); every other user's keys carry their id as a prefix.
 * Users live in the normal preferences, so a backup carries them.
 */
object VodUsers {
    class User(val id: String, var name: String, var color: Int)

    private const val FIRST = "u1"
    private const val KEY_USERS = "vod_users"
    private const val KEY_ACTIVE = "vod_active_user"

    /** Keys (unprefixed form) that hold per-user data in the main preferences. */
    private val SCOPED = listOf("vod_favs_movie", "vod_favs_series", "vod_favs_live", "vod_sort", "known_episodes")

    val COLORS = intArrayOf(0xFF7C3AED.toInt(), 0xFF22D3EE.toInt(), 0xFFF59E0B.toInt(), 0xFFEF4444.toInt(),
        0xFF10B981.toInt(), 0xFFEC4899.toInt(), 0xFF3B82F6.toInt(), 0xFF84CC16.toInt())

    private lateinit var sp: SharedPreferences
    private var users: List<User> = emptyList()
    @Volatile private var active: String = ""
    /** True once the "Who's watching?" question has been settled since the app started. */
    @Volatile var chosenThisLaunch = false

    fun init(context: Context) {
        sp = context.applicationContext.getSharedPreferences("streamarc", Context.MODE_PRIVATE)
        reload()
    }

    /** Re-reads the users, e.g. after a backup was imported. */
    fun reload() {
        users = try {
            val a = JSONArray(sp.getString(KEY_USERS, null) ?: "[]")
            (0 until a.length()).map { val o = a.getJSONObject(it); User(o.getString("id"), o.getString("name"), o.optInt("color", COLORS[0])) }
        } catch (_: Exception) { emptyList() }
        active = sp.getString(KEY_ACTIVE, "") ?: ""
        WatchProgress.reload()
    }

    private fun save() {
        val a = JSONArray()
        for (u in users) a.put(JSONObject().put("id", u.id).put("name", u.name).put("color", u.color))
        sp.edit().putString(KEY_USERS, a.toString()).putString(KEY_ACTIVE, active).commit()
    }

    fun all(): List<User> = users
    fun hasUsers() = users.isNotEmpty()
    fun current(): User? = users.firstOrNull { it.id == active } ?: users.firstOrNull()

    /** Prefix-free for the first user (and before any exist), id-prefixed for everyone else. */
    fun scope(base: String): String {
        val id = current()?.id ?: return base
        return if (id == FIRST) base else "${id}_$base"
    }

    /** Name of the watch-progress entry set of the current user. */
    fun watchKey(): String {
        val id = current()?.id ?: return "entries"
        return if (id == FIRST) "entries" else "entries_$id"
    }

    fun setActive(id: String) {
        if (users.none { it.id == id }) return
        active = id
        sp.edit().putString(KEY_ACTIVE, id).commit()
        WatchProgress.reload()
    }

    fun add(name: String, color: Int): User {
        val next = (users.mapNotNull { it.id.removePrefix("u").toIntOrNull() }.maxOrNull() ?: 0) + 1
        val u = User("u$next", name.trim(), color)
        users = users + u
        if (active.isEmpty()) active = u.id
        save()
        return u
    }

    fun update(id: String, name: String, color: Int) {
        users.firstOrNull { it.id == id }?.let { it.name = name.trim(); it.color = color }
        save()
    }

    fun nameTaken(name: String, exceptId: String? = null) =
        users.any { it.id != exceptId && it.name.equals(name.trim(), ignoreCase = true) }

    /** Removes the user and everything that was theirs. The last user cannot be removed. */
    fun remove(id: String): Boolean {
        if (users.size <= 1 || users.none { it.id == id }) return false
        val ed = sp.edit()
        for (base in SCOPED) ed.remove(if (id == FIRST) base else "${id}_$base")
        ed.commit()
        WatchProgress.wipe(if (id == FIRST) "entries" else "entries_$id")
        users = users.filter { it.id != id }
        if (active == id) active = users.first().id
        save()
        WatchProgress.reload()
        return true
    }
}
