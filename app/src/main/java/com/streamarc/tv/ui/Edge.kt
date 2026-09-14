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
        // TVs crop the picture edges (overscan); keep content inside the safe area on television devices.
        val d = view.resources.displayMetrics.density
        val tv = isTelevision(view)
        val ox = if (tv) (24 * d).toInt() else 0
        val oy = if (tv) (12 * d).toInt() else 0
        ViewCompat.setOnApplyWindowInsetsListener(view) { v, insets ->
            val i = insets.getInsets(WindowInsetsCompat.Type.systemBars() or WindowInsetsCompat.Type.displayCutout())
            v.setPadding(l + i.left + ox, t + i.top + oy, r + i.right + ox, b + i.bottom + oy)
            insets
        }
        ViewCompat.requestApplyInsets(view)
    }

    fun isTelevision(view: View): Boolean {
        val ui = view.context.getSystemService(android.content.Context.UI_MODE_SERVICE) as? android.app.UiModeManager
        return ui?.currentModeType == android.content.res.Configuration.UI_MODE_TYPE_TELEVISION ||
            view.context.packageManager.hasSystemFeature(android.content.pm.PackageManager.FEATURE_LEANBACK)
    }
}
