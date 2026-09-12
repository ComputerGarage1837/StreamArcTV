package com.streamarc.tv.ui

import android.app.UiModeManager
import android.content.Context
import android.content.Intent
import android.content.res.Configuration
import android.os.Bundle
import android.text.SpannableString
import android.text.Spanned
import android.text.style.ForegroundColorSpan
import android.view.View
import android.widget.TextView
import android.widget.Toast
import androidx.appcompat.app.AlertDialog
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
import com.streamarc.tv.update.UpdateChecker
import kotlinx.coroutines.launch

/**
 * Home screen. Inflates the TV/tablet or phone layout depending on the
 * user's choice (asked once on first launch, changeable in Settings).
 */
class MainActivity : AppCompatActivity() {

    private lateinit var prefs: Prefs
    private var appliedLayout: String? = null
    private var checkedUpdatesThisLaunch = false

    private lateinit var txtLiveStatus: TextView
    private lateinit var txtVodStatus: TextView

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        prefs = Prefs(this)
        inflateHome()

        if (prefs.layoutMode == null) askLayout()

        if (savedInstanceState == null && prefs.autoCheckUpdates && !checkedUpdatesThisLaunch) {
            checkedUpdatesThisLaunch = true
            UpdateChecker.check(this, manual = false)
        }
    }

    private fun currentLayout(): String = prefs.layoutMode ?: if (looksLikeTv()) "tv" else "phone"

    private fun inflateHome() {
        val mode = currentLayout()
        appliedLayout = mode
        setContentView(if (mode == "phone") R.layout.activity_main_phone else R.layout.activity_main_tv)
        Edge.pad(findViewById(R.id.content))

        txtLiveStatus = findViewById(R.id.txtLiveStatus)
        txtVodStatus = findViewById(R.id.txtVodStatus)

        findViewById<TextView>(R.id.txtBrand).text = brandText()
        findViewById<TextView>(R.id.txtVersion).text = getString(R.string.version_fmt, BuildConfig.VERSION_NAME)
        findViewById<View>(R.id.txtClock).visibility = if (mode == "tv") View.VISIBLE else View.GONE

        val live = findViewById<View>(R.id.btnLive)
        val vod = findViewById<View>(R.id.btnVod)
        live.setOnClickListener { open(Service.LIVE) }
        vod.setOnClickListener { open(Service.VOD) }
        live.onFocusChangeListener = cardFocus
        vod.onFocusChangeListener = cardFocus

        findViewById<View>(R.id.btnSettings).setOnClickListener { startActivity(Intent(this, SettingsActivity::class.java)) }
        findViewById<View>(R.id.btnUpdate).setOnClickListener { UpdateChecker.check(this, manual = true) }

        findViewById<View>(R.id.navHome).isSelected = true
        findViewById<View>(R.id.navHome).setOnClickListener { live.requestFocus() }
        findViewById<View>(R.id.navSearch).setOnClickListener { pickSearch() }
        findViewById<View>(R.id.navDownloads).setOnClickListener {
            Toast.makeText(this, R.string.downloads_unavailable, Toast.LENGTH_SHORT).show()
        }
        findViewById<View>(R.id.navProfile).setOnClickListener { pickProfile() }
    }

    /** "Stream Arc" in white with "TV" in the logo's cyan. */
    private fun brandText(): CharSequence {
        val name = getString(R.string.app_name)
        val span = SpannableString(name)
        val i = name.lastIndexOf("TV")
        if (i >= 0) span.setSpan(ForegroundColorSpan(ContextCompat.getColor(this, R.color.accent)), i, i + 2, Spanned.SPAN_EXCLUSIVE_EXCLUSIVE)
        return span
    }

    /** Grow the big cards slightly when the remote focuses them. */
    private val cardFocus = View.OnFocusChangeListener { v, hasFocus ->
        v.animate().scaleX(if (hasFocus) 1.03f else 1f).scaleY(if (hasFocus) 1.03f else 1f)
            .setDuration(140).start()
    }

    private fun looksLikeTv(): Boolean {
        val ui = getSystemService(Context.UI_MODE_SERVICE) as? UiModeManager
        if (ui?.currentModeType == Configuration.UI_MODE_TYPE_TELEVISION) return true
        if (packageManager.hasSystemFeature("android.software.leanback")) return true
        return resources.configuration.smallestScreenWidthDp >= 600
    }

    private fun askLayout() {
        val guessTv = looksLikeTv()
        AlertDialog.Builder(this)
            .setTitle(R.string.layout_question_title)
            .setMessage(R.string.layout_question_message)
            .setCancelable(false)
            .setPositiveButton(R.string.layout_tv) { _, _ -> applyLayout("tv") }
            .setNegativeButton(R.string.layout_phone) { _, _ -> applyLayout("phone") }
            .create()
            .also { d ->
                d.show()
                d.getButton(if (guessTv) AlertDialog.BUTTON_POSITIVE else AlertDialog.BUTTON_NEGATIVE)?.requestFocus()
            }
    }

    private fun applyLayout(mode: String) {
        prefs.layoutMode = mode
        if (mode != appliedLayout) {
            inflateHome()
            refreshAll()
        }
    }

    override fun onResume() {
        super.onResume()
        if (prefs.layoutMode != null && prefs.layoutMode != appliedLayout) inflateHome()
        refreshAll()
        if (currentFocus == null) findViewById<View>(R.id.btnLive).requestFocus()
    }

    private fun refreshAll() {
        refreshStatus(Service.LIVE, txtLiveStatus)
        refreshStatus(Service.VOD, txtVodStatus)
    }

    private fun open(service: Service) {
        val intent = if (prefs.isSignedIn(service)) {
            BrowseActivity.intent(this, service)
        } else {
            LoginActivity.intent(this, service)
        }
        startActivity(intent)
    }

    private fun pickSearch() {
        val options = arrayOf(getString(R.string.live_tv), getString(R.string.video_on_demand))
        AlertDialog.Builder(this)
            .setTitle(R.string.search_where)
            .setItems(options) { _, which -> open(if (which == 0) Service.LIVE else Service.VOD) }
            .show()
    }

    private fun pickProfile() {
        val signedIn = Service.values().filter { prefs.isSignedIn(it) }
        when (signedIn.size) {
            0 -> open(Service.LIVE)
            1 -> startActivity(ProfileActivity.intent(this, signedIn[0]))
            else -> AlertDialog.Builder(this)
                .setTitle(R.string.profile)
                .setItems(signedIn.map { it.title }.toTypedArray()) { _, which ->
                    startActivity(ProfileActivity.intent(this, signedIn[which]))
                }
                .show()
        }
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
        view.setTextColor(ContextCompat.getColor(this, if (expired) R.color.danger else R.color.accent))
    }
}
