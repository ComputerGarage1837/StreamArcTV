package com.streamarc.tv.ui

import android.view.LayoutInflater
import android.widget.Toast
import androidx.appcompat.app.AlertDialog
import androidx.appcompat.app.AppCompatActivity
import androidx.lifecycle.lifecycleScope
import com.streamarc.tv.R
import com.streamarc.tv.data.EpgCache
import com.streamarc.tv.data.Prefs
import com.streamarc.tv.data.Service
import com.streamarc.tv.databinding.DialogProgressBinding
import com.streamarc.tv.update.UpdateChecker
import kotlinx.coroutines.launch

/** The "how much guide do you want?" question, shared by the home screen and the Live TV screen. */
object GuideRefresh {
    val modes = arrayOf("auto", "full", "channel")

    fun askMode(activity: AppCompatActivity, prefs: Prefs, onChosen: (String) -> Unit) {
        val labels = arrayOf(
            activity.getString(R.string.guide_mode_auto) + " – " + activity.getString(R.string.guide_mode_auto_hint),
            activity.getString(R.string.guide_mode_full) + " – " + activity.getString(R.string.guide_mode_full_hint),
            activity.getString(R.string.guide_mode_channel) + " – " + activity.getString(R.string.guide_mode_channel_hint),
        )
        AlertDialog.Builder(activity)
            .setTitle(R.string.refresh_guide)
            .setSingleChoiceItems(labels, modes.indexOf(prefs.guideMode).coerceAtLeast(0)) { d, which ->
                d.dismiss()
                prefs.guideMode = modes[which]
                if (modes[which] == "full") prefs.setGuideSize(Service.LIVE, 0)   // a deliberate full download is never "too big"
                onChosen(modes[which])
            }
            .setNegativeButton(android.R.string.cancel, null)
            .show()
    }

    /** From the home screen: ask, then download with a progress dialog (no guide grid to show it in). */
    fun fromHome(activity: AppCompatActivity, prefs: Prefs) {
        val account = prefs.account(Service.LIVE)
        if (account == null) {
            Toast.makeText(activity, R.string.guide_sign_in_first, Toast.LENGTH_LONG).show()
            return
        }
        askMode(activity, prefs) { mode ->
            if (mode == "channel") {
                EpgCache.clear()
                Toast.makeText(activity, R.string.guide_lite_note, Toast.LENGTH_LONG).show()
                return@askMode
            }
            val vb = DialogProgressBinding.inflate(LayoutInflater.from(activity))
            vb.txtProgress.text = activity.getString(R.string.guide_downloading)
            val dialog = AlertDialog.Builder(activity).setTitle(R.string.refresh_guide).setView(vb.root).setCancelable(false).create()
            dialog.show()
            val remembered = prefs.guideSize(Service.LIVE)
            activity.lifecycleScope.launch {
                val ok = try {
                    EpgCache.loadGuide(Service.LIVE, account, force = true, onProgress = { bytes, total ->
                        val shown = maxOf(if (total > 0) total else if (remembered > 0) remembered else 25L * 1024 * 1024, bytes + 1)
                        activity.runOnUiThread {
                            vb.progress.isIndeterminate = false
                            vb.progress.progress = (bytes * 1000 / shown).toInt()
                            vb.txtProgress.text = activity.getString(
                                R.string.guide_downloading_pct_fmt, UpdateChecker.formatSize(bytes),
                                (if (total > 0) "" else "~") + UpdateChecker.formatSize(shown), (bytes * 100 / shown).toInt().coerceIn(0, 99)
                            )
                        }
                    })
                } catch (_: Exception) { false }
                dialog.dismiss()
                Toast.makeText(activity, if (ok) R.string.guide_updated else R.string.guide_refresh_failed, Toast.LENGTH_LONG).show()
            }
        }
    }
}
