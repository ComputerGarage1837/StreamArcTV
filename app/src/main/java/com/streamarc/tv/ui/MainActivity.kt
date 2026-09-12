package com.streamarc.tv.ui

import android.content.Intent
import android.os.Bundle
import android.view.View
import android.widget.TextView
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat
import androidx.lifecycle.lifecycleScope
import com.streamarc.tv.BuildConfig
import com.streamarc.tv.R
import com.streamarc.tv.data.Account
import com.streamarc.tv.data.Format
import com.streamarc.tv.data.Prefs
import com.streamarc.tv.data.Service
import com.streamarc.tv.data.XtreamApi
import com.streamarc.tv.data.toAccount
import com.streamarc.tv.databinding.ActivityMainBinding
import com.streamarc.tv.update.UpdateChecker
import kotlinx.coroutines.launch

class MainActivity : AppCompatActivity() {

    private lateinit var b: ActivityMainBinding
    private lateinit var prefs: Prefs
    private var checkedUpdatesThisLaunch = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        b = ActivityMainBinding.inflate(layoutInflater)
        setContentView(b.root)
        prefs = Prefs(this)

        b.btnLive.setOnClickListener { open(Service.LIVE) }
        b.btnVod.setOnClickListener { open(Service.VOD) }
        b.btnLive.onFocusChangeListener = cardFocus
        b.btnVod.onFocusChangeListener = cardFocus
        b.btnSettings.setOnClickListener { startActivity(Intent(this, SettingsActivity::class.java)) }
        b.btnUpdate.setOnClickListener { UpdateChecker.check(this, manual = true) }
        b.txtVersion.text = getString(R.string.version_fmt, BuildConfig.VERSION_NAME)

        if (savedInstanceState == null && prefs.autoCheckUpdates && !checkedUpdatesThisLaunch) {
            checkedUpdatesThisLaunch = true
            UpdateChecker.check(this, manual = false)
        }
    }

    override fun onResume() {
        super.onResume()
        refreshStatus(Service.LIVE, b.txtLiveStatus)
        refreshStatus(Service.VOD, b.txtVodStatus)
        if (currentFocus == null) b.btnLive.requestFocus()
    }

    /** Grow the big cards slightly when the remote focuses them. */
    private val cardFocus = View.OnFocusChangeListener { v, hasFocus ->
        v.animate().scaleX(if (hasFocus) 1.05f else 1f).scaleY(if (hasFocus) 1.05f else 1f)
            .setDuration(140).start()
    }

    private fun open(service: Service) {
        val intent = if (prefs.isSignedIn(service)) {
            BrowseActivity.intent(this, service)
        } else {
            LoginActivity.intent(this, service)
        }
        startActivity(intent)
    }

    private fun refreshStatus(service: Service, view: TextView) {
        val account = prefs.account(service)
        render(account, view)
        if (account == null) return

        // Silently refresh the account info so the expiry date stays current.
        lifecycleScope.launch {
            try {
                val info = XtreamApi.login(service, account.username, account.password)
                val updated = info.toAccount(account.username, account.password)
                prefs.saveAccount(service, updated)
                render(updated, view)
            } catch (_: Exception) {
                // Offline or server hiccup: keep showing the cached values.
            }
        }
    }

    private fun render(account: Account?, view: TextView) {
        if (account == null) {
            view.text = getString(R.string.not_signed_in)
            view.setTextColor(ContextCompat.getColor(this, R.color.text_muted))
            return
        }
        val expired = Format.isExpired(account.expDate)
        view.text = if (expired) {
            getString(R.string.expired_on_fmt, Format.expiry(account.expDate))
        } else {
            getString(R.string.expires_fmt, Format.expiry(account.expDate))
        }
        view.setTextColor(
            ContextCompat.getColor(this, if (expired) R.color.danger else R.color.accent)
        )
    }
}
