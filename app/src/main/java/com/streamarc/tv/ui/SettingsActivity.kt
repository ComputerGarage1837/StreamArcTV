package com.streamarc.tv.ui

import android.content.Intent
import android.net.Uri
import android.os.Bundle
import android.view.View
import android.widget.Toast
import androidx.appcompat.app.AlertDialog
import androidx.appcompat.app.AppCompatActivity
import com.streamarc.tv.BuildConfig
import com.streamarc.tv.R
import com.streamarc.tv.data.Prefs
import com.streamarc.tv.data.Service
import com.streamarc.tv.databinding.ActivitySettingsBinding
import com.streamarc.tv.update.UpdateChecker

class SettingsActivity : AppCompatActivity() {

    private lateinit var b: ActivitySettingsBinding
    private lateinit var prefs: Prefs

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        b = ActivitySettingsBinding.inflate(layoutInflater)
        setContentView(b.root)
        Edge.pad(b.root)
        prefs = Prefs(this)

        b.btnBack.setOnClickListener { finish() }

        b.switchAutoUpdate.isChecked = prefs.autoCheckUpdates
        b.switchAutoUpdate.setOnCheckedChangeListener { _, checked -> prefs.autoCheckUpdates = checked }

        b.rowLiveFormat.setOnClickListener { pickLiveFormat() }
        renderLiveFormat()
        b.rowLayout.setOnClickListener { pickLayout() }
        renderLayout()

        b.btnCheckUpdates.setOnClickListener { UpdateChecker.check(this, manual = true) }
        b.btnClearSkipped.setOnClickListener {
            prefs.skippedVersion = null
            Toast.makeText(this, R.string.skipped_cleared, Toast.LENGTH_SHORT).show()
            renderSkipped()
        }

        b.btnLogoutLive.setOnClickListener { logout(Service.LIVE) }
        b.btnLogoutVod.setOnClickListener { logout(Service.VOD) }

        b.txtVersion.text = getString(R.string.version_fmt, BuildConfig.VERSION_NAME)
        b.txtRepo.text = "github.com/${BuildConfig.GITHUB_REPO}"
        b.rowGithub.setOnClickListener {
            startActivity(Intent(Intent.ACTION_VIEW, Uri.parse("https://github.com/${BuildConfig.GITHUB_REPO}/releases")))
        }
        b.switchAutoUpdate.requestFocus()
    }

    override fun onResume() {
        super.onResume()
        renderSkipped()
        renderAccounts()
    }

    private fun renderLiveFormat() {
        b.txtLiveFormatValue.text = if (prefs.liveFormat == "ts") "MPEG-TS (.ts)" else "HLS (.m3u8)"
    }

    private fun pickLiveFormat() {
        val options = arrayOf("HLS (.m3u8)", "MPEG-TS (.ts)")
        val current = if (prefs.liveFormat == "ts") 1 else 0
        AlertDialog.Builder(this)
            .setTitle(R.string.live_stream_format)
            .setSingleChoiceItems(options, current) { d, which ->
                prefs.liveFormat = if (which == 1) "ts" else "m3u8"
                renderLiveFormat()
                d.dismiss()
            }
            .show()
    }

    private fun renderLayout() {
        b.txtLayoutValue.text = getString(if (prefs.layoutMode == "phone") R.string.layout_phone else R.string.layout_tv)
    }

    private fun pickLayout() {
        val options = arrayOf(getString(R.string.layout_phone), getString(R.string.layout_tv))
        val current = if (prefs.layoutMode == "phone") 0 else 1
        AlertDialog.Builder(this)
            .setTitle(R.string.display_layout)
            .setSingleChoiceItems(options, current) { d, which ->
                prefs.layoutMode = if (which == 0) "phone" else "tv"
                renderLayout()
                d.dismiss()
            }
            .show()
    }

    private fun renderSkipped() {
        val v = prefs.skippedVersion
        b.txtSkipped.text = if (v == null) getString(R.string.no_skipped_version)
        else getString(R.string.skipped_version_fmt, v)
        b.btnClearSkipped.visibility = if (v == null) View.GONE else View.VISIBLE
    }

    private fun renderAccounts() {
        val live = prefs.account(Service.LIVE)
        val vod = prefs.account(Service.VOD)
        b.btnLogoutLive.isEnabled = live != null
        b.btnLogoutLive.text = if (live != null) getString(R.string.log_out_of_fmt, Service.LIVE.title, live.username)
        else getString(R.string.not_signed_in_to_fmt, Service.LIVE.title)
        b.btnLogoutVod.isEnabled = vod != null
        b.btnLogoutVod.text = if (vod != null) getString(R.string.log_out_of_fmt, Service.VOD.title, vod.username)
        else getString(R.string.not_signed_in_to_fmt, Service.VOD.title)
    }

    private fun logout(service: Service) {
        AlertDialog.Builder(this)
            .setTitle(R.string.log_out)
            .setMessage(getString(R.string.log_out_confirm_fmt, service.title))
            .setPositiveButton(R.string.log_out) { _, _ ->
                prefs.clearAccount(service)
                renderAccounts()
            }
            .setNegativeButton(android.R.string.cancel, null)
            .show()
    }
}
