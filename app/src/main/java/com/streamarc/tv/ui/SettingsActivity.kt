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
import androidx.lifecycle.lifecycleScope
import com.streamarc.tv.data.Category
import com.streamarc.tv.data.ContentKind
import com.streamarc.tv.data.Prefs
import com.streamarc.tv.data.XtreamApi
import kotlinx.coroutines.launch
import com.streamarc.tv.data.Service
import com.streamarc.tv.databinding.ActivitySettingsBinding
import com.streamarc.tv.transfer.Folders
import com.streamarc.tv.transfer.TransferType
import com.streamarc.tv.update.UpdateChecker

class SettingsActivity : AppCompatActivity() {

    private lateinit var b: ActivitySettingsBinding
    private lateinit var prefs: Prefs
    private val picker = FolderPicker(this)

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
        b.rowDiagnostics.setOnClickListener { runDiagnostics() }
        b.rowLiveCategories.setOnClickListener { pickLiveCategories() }
        b.rowDefaultCategory.setOnClickListener { pickDefaultCategory() }
        renderLiveCategories()
        b.rowDownloadFolder.setOnClickListener { TransferDialogs.pickFolder(this, picker, TransferType.DOWNLOAD) { renderFolders() } }
        b.rowRecordingFolder.setOnClickListener { TransferDialogs.pickFolder(this, picker, TransferType.RECORDING) { renderFolders() } }
        renderFolders()

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

    // ---- Diagnostics -------------------------------------------------------

    /** Compares the panel's category lists with the categories actually attached to items. */
    private fun runDiagnostics() {
        val account = prefs.account(Service.VOD)
        if (account == null) { Toast.makeText(this, R.string.sign_in_vod_first, Toast.LENGTH_SHORT).show(); return }
        val progress = AlertDialog.Builder(this).setMessage(R.string.diagnostics_running).setCancelable(false).show()
        lifecycleScope.launch {
            val report = StringBuilder()
            for (kind in listOf(ContentKind.MOVIE, ContentKind.SERIES)) {
                try {
                    val cats = XtreamApi.categories(Service.VOD, account, kind)
                    val items = com.streamarc.tv.data.CatalogCache.get(Service.VOD, account, kind)
                    val listed = cats.mapNotNull { it.id }.toSet()
                    val used = items.flatMap { it.allCategoryIds }.toSet()
                    val missing = used - listed
                    val emptyCats = listed - used
                    report.append(if (kind == ContentKind.MOVIE) "MOVIES\n" else "\nSERIES\n")
                    report.append("Categories listed by panel: ${cats.size}\n")
                    report.append("Items in catalogue: ${items.size}\n")
                    report.append("Category ids used by items: ${used.size}\n")
                    report.append("Used but not listed: ${missing.size}${if (missing.isEmpty()) "" else "  (" + missing.take(12).joinToString(", ") + ")"}\n")
                    report.append("Listed but empty: ${emptyCats.size}\n")
                    report.append("Names: " + cats.take(40).joinToString(", ") { it.name ?: "?" } + (if (cats.size > 40) " …" else "") + "\n")
                } catch (e: Exception) {
                    report.append("${kind.name}: ${e.message}\n")
                }
            }
            progress.dismiss()
            AlertDialog.Builder(this@SettingsActivity)
                .setTitle(R.string.category_diagnostics)
                .setMessage(report.toString())
                .setPositiveButton(android.R.string.ok, null)
                .show()
        }
    }

    // ---- Live TV categories ---------------------------------------------

    private var liveCategories: List<Category>? = null

    private fun renderLiveCategories() {
        val hidden = prefs.hiddenLiveCategories.size
        b.txtLiveCategoriesValue.text = if (hidden == 0) getString(R.string.all_shown) else getString(R.string.hidden_count_fmt, hidden)
        b.txtDefaultCategoryValue.text = when (val d = prefs.defaultLiveCategory) {
            null -> getString(R.string.default_auto)
            Prefs.CATEGORY_FAVORITES -> getString(R.string.favorites)
            Prefs.CATEGORY_ALL -> getString(R.string.all_categories)
            else -> liveCategories?.firstOrNull { it.id == d }?.name ?: getString(R.string.chosen_category)
        }
    }

    /** Loads the provider's live categories once (needs a Live TV sign-in). */
    private fun withLiveCategories(then: (List<Category>) -> Unit) {
        liveCategories?.let { then(it); return }
        val account = prefs.account(Service.LIVE)
        if (account == null) { Toast.makeText(this, R.string.sign_in_live_first, Toast.LENGTH_SHORT).show(); return }
        lifecycleScope.launch {
            try {
                val cats = XtreamApi.categories(Service.LIVE, account, ContentKind.LIVE).filter { it.id != null }
                liveCategories = cats
                renderLiveCategories()
                then(cats)
            } catch (e: Exception) {
                Toast.makeText(this@SettingsActivity, e.message ?: getString(R.string.load_failed), Toast.LENGTH_LONG).show()
            }
        }
    }

    private fun pickLiveCategories() = withLiveCategories { cats ->
        val hidden = prefs.hiddenLiveCategories.toMutableSet()
        val names = cats.map { it.name ?: "—" }.toTypedArray()
        val checked = BooleanArray(cats.size) { i -> cats[i].id !in hidden }
        AlertDialog.Builder(this)
            .setTitle(R.string.live_categories_shown)
            .setMultiChoiceItems(names, checked) { _, which, isChecked ->
                val id = cats[which].id ?: return@setMultiChoiceItems
                if (isChecked) hidden.remove(id) else hidden.add(id)
            }
            .setPositiveButton(android.R.string.ok) { _, _ ->
                prefs.hiddenLiveCategories = hidden
                if (prefs.defaultLiveCategory in hidden) prefs.defaultLiveCategory = null
                renderLiveCategories()
            }
            .setNegativeButton(android.R.string.cancel, null)
            .show()
    }

    private fun pickDefaultCategory() = withLiveCategories { cats ->
        val visible = cats.filter { it.id !in prefs.hiddenLiveCategories }
        val labels = listOf(getString(R.string.default_auto), "★ " + getString(R.string.favorites), getString(R.string.all_categories)) + visible.map { it.name ?: "—" }
        val values: List<String?> = listOf(null, Prefs.CATEGORY_FAVORITES, Prefs.CATEGORY_ALL) + visible.map { it.id }
        val current = values.indexOf(prefs.defaultLiveCategory).coerceAtLeast(0)
        AlertDialog.Builder(this)
            .setTitle(R.string.default_live_category)
            .setSingleChoiceItems(labels.toTypedArray(), current) { d, which ->
                prefs.defaultLiveCategory = values[which]
                renderLiveCategories()
                d.dismiss()
            }
            .setNegativeButton(android.R.string.cancel, null)
            .show()
    }

    private fun renderFolders() {
        b.txtDownloadFolderValue.text = Folders.describe(this, prefs.downloadFolder?.takeIf { Folders.usable(this, it) }, TransferType.DOWNLOAD)
        b.txtRecordingFolderValue.text = Folders.describe(this, prefs.recordingFolder?.takeIf { Folders.usable(this, it) }, TransferType.RECORDING)
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
