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
import com.streamarc.tv.transfer.TransferType
import com.streamarc.tv.update.Announcement
import com.streamarc.tv.update.UpdateChecker
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
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

    private var noticeJob: Job? = null
    private var lastNoticeFetch = 0L

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        prefs = Prefs(this)
        inflateHome()

        if (prefs.layoutMode == null) askLayout() else maybeAskInstallPermission()
        if (prefs.crashed) {
            prefs.crashed = false
            FocusDialog(this)
                .setTitle(R.string.crash_title)
                .setMessage(R.string.crash_msg)
                .setPositiveButton(R.string.share) { _, _ -> com.streamarc.tv.util.AppLog.share(this) }
                .setNeutralButton(R.string.copy) { _, _ -> com.streamarc.tv.util.AppLog.copy(this) }
                .setNegativeButton(R.string.not_now, null)
                .show()
        }

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
        findViewById<View>(R.id.btnGuide).setOnClickListener { GuideRefresh.fromHome(this, prefs) }

        findViewById<View>(R.id.navHome).isSelected = true
        findViewById<View>(R.id.navHome).setOnClickListener { live.requestFocus() }
        findViewById<View>(R.id.navSearch).setOnClickListener { pickSearch() }
        findViewById<View>(R.id.navDownloads).setOnClickListener { startActivity(TransfersActivity.intent(this, TransferType.DOWNLOAD)) }
        findViewById<View>(R.id.navRecordings).setOnClickListener { startActivity(TransfersActivity.intent(this, TransferType.RECORDING)) }
        findViewById<View>(R.id.navProfile).setOnClickListener { pickProfile() }

        // Service notice: show the last one seen straight away, then refresh from the repository.
        showNotice(Announcement.cached(this))
    }

    /** Renders (or hides) the service notice banner above the two big buttons. */
    private fun showNotice(notice: Announcement.Notice?) {
        val box = findViewById<View>(R.id.noticeBox) ?: return
        if (notice == null || !notice.isActive()) {
            box.visibility = View.GONE
            box.isFocusable = false
            box.isClickable = false
            return
        }
        val (stroke, fill) = when (notice.level) {
            Announcement.Level.OUTAGE -> 0xFFEF4444.toInt() to 0x33EF4444
            Announcement.Level.WARNING -> 0xFFF59E0B.toInt() to 0x2EF59E0B
            Announcement.Level.INFO -> 0xFF3B82F6.toInt() to 0x1F3B82F6
        }
        (box.background.mutate() as? android.graphics.drawable.GradientDrawable)?.let {
            it.setColor(fill)
            it.setStroke((2 * resources.displayMetrics.density).toInt(), stroke)
        }
        box.findViewById<TextView>(R.id.txtNoticeIcon).setTextColor(stroke)
        box.findViewById<TextView>(R.id.txtNoticeTitle).apply {
            text = notice.title
            visibility = if (notice.title.isBlank()) View.GONE else View.VISIBLE
        }
        box.findViewById<TextView>(R.id.txtNoticeMessage).text = notice.message
        val link = notice.link
        box.findViewById<TextView>(R.id.txtNoticeLink).visibility = if (link == null) View.GONE else View.VISIBLE
        box.isFocusable = link != null
        box.isClickable = link != null
        box.setOnClickListener(if (link == null) null else View.OnClickListener {
            try {
                startActivity(Intent(Intent.ACTION_VIEW, android.net.Uri.parse(link)))
            } catch (_: Exception) {
                android.widget.Toast.makeText(this, R.string.notice_no_browser, android.widget.Toast.LENGTH_SHORT).show()
            }
        })
        box.visibility = View.VISIBLE
    }

    /**
     * Fetches the notice now (unless fetched in the last minute) and keeps refreshing it every
     * few minutes while the home screen stays open, so an outage notice appears without any
     * action on the box, and disappears the same way once it is taken down.
     */
    private fun watchNotice() {
        noticeJob?.cancel()
        noticeJob = lifecycleScope.launch {
            while (isActive) {
                val now = System.currentTimeMillis()
                if (now - lastNoticeFetch >= 60_000L) {
                    lastNoticeFetch = now
                    val notice = Announcement.fetch(this@MainActivity)
                    if (notice != null) showNotice(notice)
                } else {
                    // Even without a fetch, an "until" time may have passed.
                    showNotice(Announcement.cached(this@MainActivity))
                }
                delay(5 * 60_000L)
            }
        }
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
        FocusDialog(this)
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
        maybeAskInstallPermission()
    }

    /**
     * First-time setup: Android must allow this app to install updates. Ask once (or until
     * granted) and open the exact system screen, so a new box is ready before its first update.
     */
    private fun maybeAskInstallPermission() {
        if (android.os.Build.VERSION.SDK_INT < android.os.Build.VERSION_CODES.O) return
        if (packageManager.canRequestPackageInstalls()) { prefs.installPermissionAsked = true; return }
        if (prefs.installPermissionAsked) return
        FocusDialog(this)
            .setTitle(R.string.install_permission_title)
            .setMessage(R.string.install_permission_message)
            .setCancelable(false)
            .setPositiveButton(R.string.open_setting) { _, _ ->
                prefs.installPermissionAsked = true
                openInstallPermissionSetting(this)
            }
            .setNegativeButton(R.string.later) { _, _ -> prefs.installPermissionAsked = true }
            .show()
    }

    /** Rotation is handled here (no recreate) so nothing resets; the phone layout is swapped. */
    override fun onConfigurationChanged(newConfig: Configuration) {
        super.onConfigurationChanged(newConfig)
        inflateHome()
        refreshAll()
    }

    override fun onResume() {
        super.onResume()
        // Back from the system screen with the permission granted: nothing more to ask.
        if (android.os.Build.VERSION.SDK_INT >= android.os.Build.VERSION_CODES.O && packageManager.canRequestPackageInstalls()) {
            prefs.installPermissionAsked = true
        }
        if (prefs.layoutMode != null && prefs.layoutMode != appliedLayout) inflateHome()
        // The header clock follows the app's own time zone setting.
        findViewById<android.widget.TextClock>(R.id.txtClock)?.timeZone =
            if (com.streamarc.tv.data.Format.zoneId() == com.streamarc.tv.data.Format.DEVICE_ZONE) null else com.streamarc.tv.data.Format.zoneId()
        refreshAll()
        watchNotice()
        if (currentFocus == null) findViewById<View>(R.id.btnLive).requestFocus()
    }

    override fun onPause() {
        super.onPause()
        noticeJob?.cancel()
        noticeJob = null
    }

    private fun refreshAll() {
        refreshStatus(Service.LIVE, txtLiveStatus)
        refreshStatus(Service.VOD, txtVodStatus)
    }

    private fun open(service: Service) {
        val intent = if (prefs.isSignedIn(service)) {
            if (service == Service.VOD) VodHomeActivity.intent(this, service) else BrowseActivity.intent(this, service)
        } else {
            LoginActivity.intent(this, service)
        }
        startActivity(intent)
    }

    private fun pickSearch() {
        val options = arrayOf(getString(R.string.live_tv), getString(R.string.video_on_demand))
        FocusDialog(this)
            .setTitle(R.string.search_where)
            .setItems(options) { _, which -> open(if (which == 0) Service.LIVE else Service.VOD) }
            .show()
    }

    private fun pickProfile() {
        val signedIn = Service.values().filter { prefs.isSignedIn(it) }
        when (signedIn.size) {
            0 -> open(Service.LIVE)
            1 -> startActivity(ProfileActivity.intent(this, signedIn[0]))
            else -> FocusDialog(this)
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
            } catch (e: Exception) {
                // Keep the cached expiry, but say plainly why the service can't be reached right now.
                val why = com.streamarc.tv.data.Diagnose.explain(this@MainActivity, e)
                view.text = why
                view.setTextColor(ContextCompat.getColor(this@MainActivity, R.color.danger))
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

    companion object {
        /** Opens Android's "Install unknown apps" page for this app (or general security settings). */
        fun openInstallPermissionSetting(activity: android.app.Activity) {
            val i = Intent(android.provider.Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES)
                .setData(android.net.Uri.parse("package:${activity.packageName}"))
            try {
                activity.startActivity(i)
            } catch (_: Exception) {
                try { activity.startActivity(Intent(android.provider.Settings.ACTION_SECURITY_SETTINGS)) } catch (_: Exception) {}
            }
        }
    }
}
