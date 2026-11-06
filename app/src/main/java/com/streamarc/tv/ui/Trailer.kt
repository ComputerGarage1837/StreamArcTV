package com.streamarc.tv.ui

import android.content.Context
import android.content.Intent
import android.net.Uri
import android.widget.Toast
import com.streamarc.tv.R

/** Opens a trailer address in whatever can show it (the YouTube app or a browser). */
fun openTrailer(context: Context, url: String?) {
    if (url.isNullOrBlank()) { Toast.makeText(context, R.string.no_trailer, Toast.LENGTH_SHORT).show(); return }
    try {
        context.startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(url)).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
    } catch (_: Exception) {
        Toast.makeText(context, R.string.no_trailer_app, Toast.LENGTH_LONG).show()
    }
}
