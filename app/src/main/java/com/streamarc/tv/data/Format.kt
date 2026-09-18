package com.streamarc.tv.data

import android.content.Context
import java.text.SimpleDateFormat
import java.util.Calendar
import java.util.Date
import java.util.Locale
import java.util.TimeZone

/**
 * Every clock time the app shows goes through here, so they all follow the app's own time
 * zone setting (Eastern by default) rather than whatever the box happens to be set to.
 */
object Format {
    const val DEVICE_ZONE = "device"
    const val DEFAULT_ZONE = "America/New_York"

    private var prefs: Prefs? = null
    fun init(context: Context) { prefs = Prefs(context) }

    /** The zone id in use: a real id, or [DEVICE_ZONE] to follow the box. */
    fun zoneId(): String = prefs?.timeZoneId ?: DEFAULT_ZONE

    fun zone(): TimeZone {
        val id = zoneId()
        return if (id == DEVICE_ZONE) TimeZone.getDefault() else TimeZone.getTimeZone(id)
    }

    /** A formatter in the app's zone (new each call: SimpleDateFormat isn't thread-safe and zones change). */
    fun fmt(pattern: String): SimpleDateFormat = SimpleDateFormat(pattern, Locale.getDefault()).apply { timeZone = zone() }

    fun calendar(): Calendar = Calendar.getInstance(zone())

    /** "S01E05" style episode code. */
    fun se(season: Int, episode: Int): String = "S%02dE%02d".format(season, episode)

    fun time(epochSeconds: Long): String = fmt("h:mm a").format(Date(epochSeconds * 1000L))
    fun timeMs(epochMs: Long): String = fmt("h:mm a").format(Date(epochMs))
    fun dayMs(epochMs: Long): String = fmt("EEE MMM d").format(Date(epochMs))

    fun timeRange(start: Long, end: Long): String = "${time(start)} – ${time(end)}"

    fun expiry(epochSeconds: Long?): String =
        if (epochSeconds == null) "Unlimited" else fmt("MMM d, yyyy").format(Date(epochSeconds * 1000L))

    fun date(epochSeconds: Long?): String =
        if (epochSeconds == null) "—" else fmt("MMM d, yyyy").format(Date(epochSeconds * 1000L))

    fun isExpired(epochSeconds: Long?): Boolean =
        epochSeconds != null && epochSeconds * 1000L < System.currentTimeMillis()

    /** Zones offered in Settings: id to a friendly place name. */
    val zoneChoices: List<Pair<String, String>> = listOf(
        "America/St_Johns" to "Newfoundland (St. John's)",
        "America/Halifax" to "Atlantic (Halifax)",
        "America/New_York" to "Eastern (Toronto / New York)",
        "America/Chicago" to "Central (Winnipeg / Chicago)",
        "America/Regina" to "Saskatchewan (Regina)",
        "America/Denver" to "Mountain (Edmonton / Denver)",
        "America/Phoenix" to "Arizona (Phoenix)",
        "America/Los_Angeles" to "Pacific (Vancouver / Los Angeles)",
        "America/Anchorage" to "Alaska (Anchorage)",
        "Pacific/Honolulu" to "Hawaii (Honolulu)",
        "America/Mexico_City" to "Mexico City",
        "Europe/London" to "United Kingdom (London)",
        "Europe/Paris" to "Central Europe (Paris / Berlin / Madrid)",
        "Europe/Athens" to "Eastern Europe (Athens / Kyiv)",
        "Asia/Dubai" to "Gulf (Dubai)",
        "Asia/Karachi" to "Pakistan (Karachi)",
        "Asia/Kolkata" to "India (Kolkata)",
        "Asia/Bangkok" to "Indochina (Bangkok)",
        "Asia/Singapore" to "Singapore / Hong Kong",
        "Asia/Tokyo" to "Japan (Tokyo)",
        "Australia/Perth" to "Western Australia (Perth)",
        "Australia/Adelaide" to "Central Australia (Adelaide)",
        "Australia/Sydney" to "Eastern Australia (Sydney)",
        "Pacific/Auckland" to "New Zealand (Auckland)",
        "UTC" to "UTC",
    )

    /** "Eastern (Toronto / New York) · EDT, UTC−04:00" for the picker. */
    fun describeZone(id: String): String {
        val tz = if (id == DEVICE_ZONE) TimeZone.getDefault() else TimeZone.getTimeZone(id)
        val now = Date()
        val offsetMin = tz.getOffset(now.time) / 60_000
        val sign = if (offsetMin < 0) "−" else "+"
        val abs = Math.abs(offsetMin)
        val offset = "UTC%s%02d:%02d".format(sign, abs / 60, abs % 60)
        val name = zoneChoices.firstOrNull { it.first == id }?.second ?: tz.id
        return "$name · ${tz.getDisplayName(tz.inDaylightTime(now), TimeZone.SHORT)}, $offset"
    }
}
