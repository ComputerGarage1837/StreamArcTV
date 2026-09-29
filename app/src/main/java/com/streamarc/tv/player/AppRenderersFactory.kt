package com.streamarc.tv.player

import android.content.Context
import androidx.media3.common.util.UnstableApi
import androidx.media3.exoplayer.DefaultRenderersFactory
import androidx.media3.exoplayer.audio.AudioCapabilities
import androidx.media3.exoplayer.audio.AudioSink
import androidx.media3.exoplayer.audio.DefaultAudioSink
import com.streamarc.tv.data.Prefs
import com.streamarc.tv.util.AppLog

/**
 * Renderers with one change from the defaults: Dolby (AC3 / E-AC3 / TrueHD / DTS) is decoded
 * on the device unless the user turned passthrough on in Settings.
 *
 * Many boxes claim their HDMI output accepts raw Dolby bitstreams, so the player would skip
 * its decoder and send the bitstream to the TV. When the TV or soundbar can't decode it, those
 * channels play with no sound at all. Declaring "PCM only" makes the player always decode.
 */
@UnstableApi
class AppRenderersFactory(context: Context) : DefaultRenderersFactory(context) {

    private val passthrough = Prefs(context).dolbyPassthrough

    override fun buildAudioSink(
        context: Context,
        enableFloatOutput: Boolean,
        enableAudioTrackPlaybackParams: Boolean
    ): AudioSink? {
        if (passthrough) return super.buildAudioSink(context, enableFloatOutput, enableAudioTrackPlaybackParams)
        AppLog.i("Player", "audio sink: decode on device (Dolby passthrough off)")
        @Suppress("DEPRECATION")
        return DefaultAudioSink.Builder(context)
            .setAudioCapabilities(AudioCapabilities.DEFAULT_AUDIO_CAPABILITIES)
            .setEnableFloatOutput(enableFloatOutput)
            .setEnableAudioTrackPlaybackParams(enableAudioTrackPlaybackParams)
            .build()
    }
}
