package com.streamarc.tv.ui

import android.view.View
import androidx.core.view.ViewCompat
import androidx.core.view.WindowInsetsCompat

/**
 * Android 15 draws apps edge-to-edge, so screens must keep their content
 * clear of the status bar, navigation bar and display cutouts themselves.
 */
object Edge {
    fun pad(view: View) {
        val l = view.paddingLeft; val t = view.paddingTop; val r = view.paddingRight; val b = view.paddingBottom
        ViewCompat.setOnApplyWindowInsetsListener(view) { v, insets ->
            val i = insets.getInsets(WindowInsetsCompat.Type.systemBars() or WindowInsetsCompat.Type.displayCutout())
            v.setPadding(l + i.left, t + i.top, r + i.right, b + i.bottom)
            insets
        }
        ViewCompat.requestApplyInsets(view)
    }
}
