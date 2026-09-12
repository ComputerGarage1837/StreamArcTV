package com.streamarc.tv.data

import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

object Format {
    private val dateFmt = SimpleDateFormat("MMM d, yyyy", Locale.getDefault())
    private val timeFmt = SimpleDateFormat("h:mm a", Locale.getDefault())

    fun time(epochSeconds: Long): String = synchronized(timeFmt) { timeFmt.format(Date(epochSeconds * 1000L)) }

    fun timeRange(start: Long, end: Long): String = "${time(start)} – ${time(end)}"

    fun expiry(epochSeconds: Long?): String =
        if (epochSeconds == null) "Unlimited" else dateFmt.format(Date(epochSeconds * 1000L))

    fun date(epochSeconds: Long?): String =
        if (epochSeconds == null) "—" else dateFmt.format(Date(epochSeconds * 1000L))

    fun isExpired(epochSeconds: Long?): Boolean =
        epochSeconds != null && epochSeconds * 1000L < System.currentTimeMillis()
}
