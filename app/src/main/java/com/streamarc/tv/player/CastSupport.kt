package com.streamarc.tv.player

import android.content.Context
import com.google.android.gms.cast.CastMediaControlIntent
import com.google.android.gms.cast.framework.CastContext
import com.google.android.gms.cast.framework.CastOptions
import com.google.android.gms.cast.framework.OptionsProvider
import com.google.android.gms.cast.framework.SessionProvider
import com.google.android.gms.common.ConnectionResult
import com.google.android.gms.common.GoogleApiAvailability

/** Chromecast set-up: the stock media receiver plays the stream address the app hands it. */
class CastOptionsProvider : OptionsProvider {
    override fun getCastOptions(context: Context): CastOptions =
        CastOptions.Builder().setReceiverApplicationId(CastMediaControlIntent.DEFAULT_MEDIA_RECEIVER_APPLICATION_ID).build()

    override fun getAdditionalSessionProviders(context: Context): List<SessionProvider>? = null
}

object CastSupport {
    /** Casting needs Google Play services; many TV boxes don't have them. */
    fun available(context: Context): Boolean = try {
        GoogleApiAvailability.getInstance().isGooglePlayServicesAvailable(context) == ConnectionResult.SUCCESS
    } catch (_: Throwable) { false }

    fun context(context: Context): CastContext? = try { CastContext.getSharedInstance(context) } catch (_: Throwable) { null }
}
