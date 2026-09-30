package com.streamarc.tv.player

import android.content.Context
import androidx.media3.common.audio.ChannelMixingAudioProcessor
import androidx.media3.common.audio.ChannelMixingMatrix
import androidx.media3.common.util.UnstableApi
import androidx.media3.exoplayer.DefaultRenderersFactory
import androidx.media3.exoplayer.audio.AudioCapabilities
import androidx.media3.exoplayer.audio.AudioSink
import androidx.media3.exoplayer.audio.DefaultAudioSink
import com.streamarc.tv.data.Prefs
import com.streamarc.tv.util.AppLog

/**
 * Renderers tuned for the boxes this app runs on:
 *
 * - Audio the box's own decoders can't handle, or handle badly (MPEG Layer II on most boxes,
 *   Dolby on boxes whose Dolby decoder only works as passthrough), is decoded in software by
 *   the bundled FFmpeg renderer, which is tried before the device decoders for the formats it
 *   was built with (AAC is left to the device).
 * - Dolby / DTS bitstreams are never passed through over HDMI unless the user asks for it in
 *   Settings: many boxes claim the TV accepts them, and when it doesn't the channel is silent.
 * - Surround sound is mixed down to stereo in the app, so a 5.1 channel plays the same on a
 *   box whose sound output only really does two channels.
 */
@UnstableApi
class AppRenderersFactory(context: Context) : DefaultRenderersFactory(context) {

    private val passthrough = Prefs(context).dolbyPassthrough

    init {
        setExtensionRendererMode(EXTENSION_RENDERER_MODE_PREFER)
        setEnableDecoderFallback(true)
    }

    override fun buildAudioSink(
        context: Context,
        enableFloatOutput: Boolean,
        enableAudioTrackPlaybackParams: Boolean
    ): AudioSink? {
        if (passthrough) return super.buildAudioSink(context, enableFloatOutput, enableAudioTrackPlaybackParams)
        AppLog.i("Player", "audio sink: decode on device, stereo downmix (Dolby passthrough off)")
        @Suppress("DEPRECATION")
        return DefaultAudioSink.Builder(context)
            .setAudioCapabilities(AudioCapabilities.DEFAULT_AUDIO_CAPABILITIES)
            .setAudioProcessorChain(DefaultAudioSink.DefaultAudioProcessorChain(stereoDownmix()))
            .setEnableFloatOutput(false)
            .setEnableAudioTrackPlaybackParams(enableAudioTrackPlaybackParams)
            .build()
    }

    companion object {
        /**
         * Mixes 3–8 channel PCM down to stereo; mono and stereo pass through untouched. Every
         * plausible channel count needs a matrix, because the processor rejects any input it
         * has no matrix for and that would fail playback outright.
         */
        fun stereoDownmix(): ChannelMixingAudioProcessor {
            val p = ChannelMixingAudioProcessor()
            p.putChannelMixingMatrix(ChannelMixingMatrix.create(1, 1))
            p.putChannelMixingMatrix(ChannelMixingMatrix.create(2, 2))
            // Rows are input channels (Android order), columns are output L, R.
            val c = 0.707f   // centre
            val s = 0.5f     // surrounds
            val f = 0.707f   // front left / right
            p.putChannelMixingMatrix(ChannelMixingMatrix(3, 2, floatArrayOf(      // L R C
                f, 0f,  0f, f,  c, c)))
            p.putChannelMixingMatrix(ChannelMixingMatrix(4, 2, floatArrayOf(      // L R Lb Rb
                f, 0f,  0f, f,  s, 0f,  0f, s)))
            p.putChannelMixingMatrix(ChannelMixingMatrix(5, 2, floatArrayOf(      // L R C Ls Rs
                f, 0f,  0f, f,  c, c,  s, 0f,  0f, s)))
            p.putChannelMixingMatrix(ChannelMixingMatrix(6, 2, floatArrayOf(      // L R C LFE Ls Rs
                f, 0f,  0f, f,  c, c,  0f, 0f,  s, 0f,  0f, s)))
            p.putChannelMixingMatrix(ChannelMixingMatrix(7, 2, floatArrayOf(      // L R C LFE Lb Rb Cb
                f, 0f,  0f, f,  c, c,  0f, 0f,  s, 0f,  0f, s,  s, s)))
            p.putChannelMixingMatrix(ChannelMixingMatrix(8, 2, floatArrayOf(      // L R C LFE Ls Rs Lb Rb
                f, 0f,  0f, f,  c, c,  0f, 0f,  s, 0f,  0f, s,  s, 0f,  0f, s)))
            return p
        }
    }
}
