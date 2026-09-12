package com.streamarc.tv.ui

import android.content.Context
import android.content.Intent
import android.os.Bundle
import android.view.View
import android.view.inputmethod.EditorInfo
import androidx.appcompat.app.AppCompatActivity
import androidx.lifecycle.lifecycleScope
import com.streamarc.tv.R
import com.streamarc.tv.data.Prefs
import com.streamarc.tv.data.Service
import com.streamarc.tv.data.XtreamApi
import com.streamarc.tv.data.toAccount
import com.streamarc.tv.databinding.ActivityLoginBinding
import kotlinx.coroutines.launch

class LoginActivity : AppCompatActivity() {

    private lateinit var b: ActivityLoginBinding
    private lateinit var service: Service

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        b = ActivityLoginBinding.inflate(layoutInflater)
        setContentView(b.root)

        service = Service.valueOf(intent.getStringExtra(EXTRA_SERVICE) ?: Service.LIVE.name)
        b.txtTitle.text = getString(R.string.sign_in_to_fmt, service.title)
        b.txtServer.text = if (service.isConfigured) service.baseUrl else getString(R.string.server_not_configured)
        if (!service.isConfigured) {
            showError(getString(R.string.service_not_configured_fmt, service.title))
            b.btnSignIn.isEnabled = false
        }

        b.btnSignIn.setOnClickListener { signIn() }
        b.btnCancel.setOnClickListener { finish() }
        b.inputPassword.setOnEditorActionListener { _, actionId, _ ->
            if (actionId == EditorInfo.IME_ACTION_DONE || actionId == EditorInfo.IME_ACTION_GO) {
                signIn(); true
            } else false
        }
        b.inputUsername.requestFocus()
    }

    private fun signIn() {
        val user = b.inputUsername.text?.toString()?.trim().orEmpty()
        val pass = b.inputPassword.text?.toString().orEmpty()
        if (user.isEmpty() || pass.isEmpty()) {
            showError(getString(R.string.enter_credentials))
            return
        }
        setBusy(true)
        lifecycleScope.launch {
            try {
                val info = XtreamApi.login(service, user, pass)
                Prefs(this@LoginActivity).saveAccount(service, info.toAccount(user, pass))
                startActivity(BrowseActivity.intent(this@LoginActivity, service))
                finish()
            } catch (e: Exception) {
                showError(e.message ?: getString(R.string.sign_in_failed))
                setBusy(false)
            }
        }
    }

    private fun showError(msg: String) {
        b.txtError.text = msg
        b.txtError.visibility = View.VISIBLE
    }

    private fun setBusy(busy: Boolean) {
        b.progress.visibility = if (busy) View.VISIBLE else View.GONE
        b.btnSignIn.isEnabled = !busy
        b.inputUsername.isEnabled = !busy
        b.inputPassword.isEnabled = !busy
        if (busy) b.txtError.visibility = View.GONE
    }

    companion object {
        private const val EXTRA_SERVICE = "service"
        fun intent(ctx: Context, service: Service): Intent =
            Intent(ctx, LoginActivity::class.java).putExtra(EXTRA_SERVICE, service.name)
    }
}
