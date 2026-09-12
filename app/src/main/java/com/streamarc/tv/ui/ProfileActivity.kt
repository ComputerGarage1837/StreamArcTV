package com.streamarc.tv.ui

import android.content.Context
import android.content.Intent
import android.os.Bundle
import androidx.appcompat.app.AlertDialog
import androidx.appcompat.app.AppCompatActivity
import com.streamarc.tv.R
import com.streamarc.tv.data.Format
import com.streamarc.tv.data.Prefs
import com.streamarc.tv.data.Service
import com.streamarc.tv.databinding.ActivityProfileBinding

class ProfileActivity : AppCompatActivity() {

    private lateinit var b: ActivityProfileBinding
    private lateinit var service: Service
    private lateinit var prefs: Prefs

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        b = ActivityProfileBinding.inflate(layoutInflater)
        setContentView(b.root)
        Edge.pad(b.root)
        prefs = Prefs(this)
        service = Service.valueOf(intent.getStringExtra(EXTRA_SERVICE) ?: Service.LIVE.name)

        val a = prefs.account(service)
        if (a == null) { finish(); return }

        b.txtTitle.text = getString(R.string.profile_title_fmt, service.title)
        b.txtServer.text = service.baseUrl
        b.txtUsername.text = a.username
        b.txtStatus.text = a.status ?: "—"
        b.txtExpiry.text = Format.expiry(a.expDate)
        b.txtConnections.text = "${a.activeConnections ?: "0"} / ${a.maxConnections ?: "?"}"
        b.txtCreated.text = Format.date(a.createdAt)
        b.txtTrial.text = if (a.isTrial) getString(R.string.yes) else getString(R.string.no)

        b.btnBack.setOnClickListener { finish() }
        b.btnLogout.setOnClickListener { confirmLogout() }
        b.btnLogout.requestFocus()
    }

    private fun confirmLogout() {
        AlertDialog.Builder(this)
            .setTitle(R.string.log_out)
            .setMessage(getString(R.string.log_out_confirm_fmt, service.title))
            .setPositiveButton(R.string.log_out) { _, _ ->
                prefs.clearAccount(service)
                val i = Intent(this, MainActivity::class.java)
                    .addFlags(Intent.FLAG_ACTIVITY_CLEAR_TOP or Intent.FLAG_ACTIVITY_SINGLE_TOP)
                startActivity(i)
                finish()
            }
            .setNegativeButton(android.R.string.cancel, null)
            .show()
    }

    companion object {
        private const val EXTRA_SERVICE = "service"
        fun intent(ctx: Context, service: Service): Intent =
            Intent(ctx, ProfileActivity::class.java).putExtra(EXTRA_SERVICE, service.name)
    }
}
