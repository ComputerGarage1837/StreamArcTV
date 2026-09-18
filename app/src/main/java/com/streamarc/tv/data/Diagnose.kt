package com.streamarc.tv.data

import android.content.Context
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import okhttp3.OkHttpClient
import okhttp3.Request
import java.net.ConnectException
import java.net.SocketTimeoutException
import java.net.UnknownHostException
import java.util.concurrent.TimeUnit
import javax.net.ssl.SSLException

/**
 * Turns a failure into a plain sentence a customer can act on: no network, no internet, the
 * provider's server unreachable, an expired or disabled account, wrong password, and so on.
 */
object Diagnose {

    /** Best-effort explanation without touching the network (safe on the main thread). */
    fun quick(context: Context, e: Throwable): String {
        (e as? XtreamApi.AccountException)?.let { return it.message ?: "Account problem." }
        val root = rootCause(e)
        return when {
            e is XtreamApi.HttpException -> http(e.code)
            e is XtreamApi.BadResponseException -> "The service sent back a web page instead of data. That usually means a block page, a maintenance page or a wrong address. Try again in a few minutes."
            !hasNetwork(context) -> "This device is not connected to any network. Check the Wi-Fi or the Ethernet cable."
            root is UnknownHostException -> "Can't find the service's server. The internet may be down, or your internet provider may be blocking it; a VPN or a different DNS often fixes that."
            root is SSLException -> "Secure connection to the service failed. Check that the device's date and time are correct, then try again."
            root is SocketTimeoutException -> "The service's server is not answering. It may be down or overloaded, or your internet provider may be blocking it. Try again shortly."
            root is ConnectException -> "Can't connect to the service's server. It may be down, or your connection may be blocking it."
            e is XtreamApi.NetworkException -> "Network problem: ${root.message ?: "connection failed"}."
            else -> e.message ?: "Something went wrong."
        }
    }

    /**
     * Like [quick], but for network failures it also checks whether the internet itself works,
     * so "no internet" and "the provider is down" are told apart. Runs a short probe off the UI thread.
     */
    suspend fun explain(context: Context, e: Throwable): String {
        val root = rootCause(e)
        val networkish = e is XtreamApi.NetworkException || root is UnknownHostException || root is SocketTimeoutException ||
            root is ConnectException || root is SSLException
        if (!networkish) return quick(context, e)
        if (!hasNetwork(context)) return "This device is not connected to any network. Check the Wi-Fi or the Ethernet cable."
        val internet = withContext(Dispatchers.IO) { internetReachable() }
        return if (!internet) {
            "Connected to the network, but there is no internet access. Restart the router or check with your internet provider."
        } else when (root) {
            is UnknownHostException -> "The internet works, but the service's server can't be found. Your internet provider may be blocking it (a VPN or a different DNS such as 1.1.1.1 usually fixes that), or the service's address has changed."
            is SSLException -> "The internet works, but the secure connection to the service failed. Check the device's date and time, then try again."
            else -> "The internet works, but the service's server is not answering. It may be down or overloaded, or your internet provider may be blocking it. Try again in a few minutes, or try a VPN."
        }
    }

    private fun http(code: Int): String = when (code) {
        401, 403 -> "The service refused the sign-in. Check the username and password, or ask your provider whether the account is active."
        404 -> "The service's address was not found (HTTP 404). The service may have moved."
        429, 458, 509 -> "Too many connections on this account at once (HTTP $code). Close the app on other devices and try again."
        in 500..599 -> "The service's server has a problem right now (HTTP $code). Try again in a few minutes."
        else -> "The service returned an error (HTTP $code)."
    }

    fun hasNetwork(context: Context): Boolean {
        val cm = context.getSystemService(Context.CONNECTIVITY_SERVICE) as? ConnectivityManager ?: return true
        val caps = cm.getNetworkCapabilities(cm.activeNetwork ?: return false) ?: return false
        return caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
    }

    private val probeClient by lazy {
        OkHttpClient.Builder().connectTimeout(5, TimeUnit.SECONDS).readTimeout(5, TimeUnit.SECONDS).build()
    }

    /** True if a well-known public endpoint answers; false when the box has a network but no way out. */
    fun internetReachable(): Boolean {
        val urls = listOf("https://connectivitycheck.gstatic.com/generate_204", "https://www.cloudflare.com/cdn-cgi/trace")
        for (u in urls) {
            try {
                probeClient.newCall(Request.Builder().url(u).header("User-Agent", XtreamApi.USER_AGENT).build()).execute().use { r ->
                    if (r.code in 200..399) return true
                }
            } catch (_: Exception) {}
        }
        return false
    }

    private fun rootCause(e: Throwable): Throwable {
        var t = e
        while (t.cause != null && t.cause !== t) t = t.cause!!
        return t
    }
}
