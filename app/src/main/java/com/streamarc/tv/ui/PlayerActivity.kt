package com.streamarc.tv.ui

import android.content.Context
import android.content.Intent
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.view.KeyEvent
import android.view.View
import android.view.WindowManager
import android.widget.Toast
import androidx.appcompat.app.AlertDialog
import androidx.appcompat.app.AppCompatActivity
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import androidx.media3.common.C
import androidx.media3.common.MediaItem
import androidx.media3.common.PlaybackException
import androidx.media3.common.Player
import androidx.media3.common.TrackSelectionParameters
import androidx.media3.common.util.UnstableApi
import androidx.media3.datasource.DefaultDataSource
import androidx.media3.datasource.DefaultHttpDataSource
import androidx.media3.datasource.HttpDataSource
import androidx.media3.exoplayer.DefaultLoadControl
import androidx.media3.exoplayer.DefaultRenderersFactory
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.exoplayer.analytics.AnalyticsListener
import androidx.media3.exoplayer.source.LoadEventInfo
import androidx.media3.exoplayer.source.MediaLoadData
import java.io.IOException
import androidx.media3.exoplayer.source.DefaultMediaSourceFactory
import androidx.media3.exoplayer.upstream.DefaultLoadErrorHandlingPolicy
import com.streamarc.tv.R
import com.streamarc.tv.data.BufferLevel
import com.streamarc.tv.data.Prefs
import com.streamarc.tv.data.WatchProgress
import com.streamarc.tv.data.XtreamApi
import com.streamarc.tv.databinding.ActivityPlayerBinding
import com.streamarc.tv.player.TimeshiftServer
import com.streamarc.tv.transfer.TransferService
import com.streamarc.tv.update.UpdateChecker

@UnstableApi
class PlayerActivity : AppCompatActivity() {

    private lateinit var b: ActivityPlayerBinding
    private lateinit var prefs: Prefs
    private var player: ExoPlayer? = null
    private var timeshift: TimeshiftServer? = null
    /** Byte offset into the stored stream where the current media item starts (storage buffer only). */
    private var itemFromBytes = 0L
    private lateinit var url: String
    private var title: String = ""
    private var isLive: Boolean = false
    /** Progress key for movies, episodes and downloads; null for live TV. */
    private var watchKey: String? = null
    private var resumeAsked = false
    private var lastProgressSave = 0L

    private val handler = Handler(Looper.getMainLooper())
    private var retries = 0
    private var pendingRetry: Runnable? = null

    /** Total time spent paused since the stream was (re)opened; approximates how far behind live a .ts stream is. */
    private var pausedTotalMs = 0L
    private var pausedSince = 0L
    /** Live offset (HLS) measured when playback first became ready; growth beyond it means we're behind. */
    private var liveBaselineMs = C.TIME_UNSET
    private var behindNow = false

    // Diagnostics shown under the spinner while buffering.
    private var bytesLoaded = 0L
    private var loadsStarted = 0
    private var lastLoadError: String? = null
    private var bufferingSince = 0L

    private val ticker = object : Runnable {
        override fun run() {
            updateStatus()
            handler.postDelayed(this, 250)
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        b = ActivityPlayerBinding.inflate(layoutInflater)
        setContentView(b.root)
        prefs = Prefs(this)

        url = intent.getStringExtra(EXTRA_URL) ?: run { finish(); return }
        title = intent.getStringExtra(EXTRA_TITLE) ?: ""
        isLive = intent.getBooleanExtra(EXTRA_LIVE, false)
        watchKey = intent.getStringExtra(EXTRA_KEY)?.takeIf { !isLive }

        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        b.txtTitle.text = title
        b.playerView.setShowNextButton(false)
        b.playerView.setShowPreviousButton(false)
        b.playerView.setShowRewindButton(false)
        b.playerView.setShowFastForwardButton(false)
        b.playerView.setShowSubtitleButton(true)
        b.playerView.controllerShowTimeoutMs = 4000
        b.playerView.setControllerVisibilityListener(
            androidx.media3.ui.PlayerView.ControllerVisibilityListener { visibility ->
                b.txtTitle.visibility = visibility
                b.txtLiveStatus.visibility = if (isLive) visibility else View.GONE
                b.btnSeekBack.visibility = visibility
                b.btnSeekFwd.visibility = visibility
            }
        )
        b.txtLiveStatus.visibility = View.GONE
        b.btnSeekBack.visibility = View.GONE
        b.btnSeekFwd.visibility = View.GONE
        b.txtLiveStatus.setOnClickListener { goLive() }
        b.btnSeekBack.setOnClickListener { seekBy(-SEEK_STEP_MS) }
        b.btnSeekFwd.setOnClickListener { seekBy(SEEK_STEP_MS) }
        b.btnRetry.setOnClickListener { retries = 0; releasePlayer(); initPlayer() }
    }

    override fun onStart() {
        super.onStart()
        hideSystemUi()
        TransferService.playbackActive = true
        initPlayer()
        handler.post(ticker)
    }

    override fun onStop() {
        super.onStop()
        TransferService.playbackActive = false
        saveProgress(force = true)
        handler.removeCallbacks(ticker)
        cancelRetry()
        releasePlayer()
    }

    override fun dispatchKeyEvent(event: KeyEvent): Boolean {
        if (event.action == KeyEvent.ACTION_DOWN) {
            when (event.keyCode) {
                KeyEvent.KEYCODE_MEDIA_REWIND -> { seekBy(-SEEK_STEP_MS); return true }
                KeyEvent.KEYCODE_MEDIA_FAST_FORWARD -> { seekBy(SEEK_STEP_MS); return true }
                // With the controls hidden, left/right on the remote skip; with them shown they move focus.
                KeyEvent.KEYCODE_DPAD_LEFT -> if (!b.playerView.isControllerFullyVisible) { seekBy(-SEEK_STEP_MS); return true }
                KeyEvent.KEYCODE_DPAD_RIGHT -> if (!b.playerView.isControllerFullyVisible) { seekBy(SEEK_STEP_MS); return true }
            }
        }
        return super.dispatchKeyEvent(event)
    }

    // ---- Player set-up ---------------------------------------------------------

    private fun initPlayer() {
        if (player != null) return
        b.txtError.visibility = View.GONE
        b.btnRetry.visibility = View.GONE
        pausedTotalMs = 0L
        pausedSince = 0L
        liveBaselineMs = C.TIME_UNSET
        behindNow = false
        itemFromBytes = 0L

        // Storage levels only make sense for live channels; movies and series get the biggest
        // in-memory buffer instead, since they can already be scrubbed freely.
        val chosen = BufferLevel.from(prefs.bufferLevel)
        val level = if (chosen.onDisk && !isLive) BufferLevel.MAX else chosen

        val httpFactory = DefaultHttpDataSource.Factory()
            .setUserAgent(XtreamApi.USER_AGENT)
            .setAllowCrossProtocolRedirects(true)
            .setConnectTimeoutMs(15_000)
            .setReadTimeoutMs(20_000)

        val mediaSources = DefaultMediaSourceFactory(this)
            .setDataSourceFactory(DefaultDataSource.Factory(this, httpFactory))
            .setLoadErrorHandlingPolicy(DefaultLoadErrorHandlingPolicy(2))

        val renderers = DefaultRenderersFactory(this)
            .setExtensionRendererMode(DefaultRenderersFactory.EXTENSION_RENDERER_MODE_PREFER)
            .setEnableDecoderFallback(true)

        // Buffer sizes come from the user's choice; the byte cap keeps huge buffers inside
        // roughly half of the heap this device gives the app so we never run out of memory.
        val heapCap = (Runtime.getRuntime().maxMemory() / 2).coerceAtMost(Int.MAX_VALUE.toLong()).toInt()
        val loadControl = DefaultLoadControl.Builder()
            .setBufferDurationsMs(level.minBufferMs, level.maxBufferMs, level.playbackMs, level.rebufferMs)
            .setTargetBufferBytes(level.bytes.coerceAtMost(heapCap))
            // Time thresholds decide when playback starts; the byte figure is only a memory ceiling.
            .setPrioritizeTimeOverSizeThresholds(true)
            .build()

        val p = ExoPlayer.Builder(this, renderers)
            .setMediaSourceFactory(mediaSources)
            .setLoadControl(loadControl)
            .setWakeMode(C.WAKE_MODE_NETWORK)
            .setHandleAudioBecomingNoisy(true)
            .build()
        player = p
        b.playerView.player = p
        bytesLoaded = 0L; loadsStarted = 0; lastLoadError = null; bufferingSince = SystemClock.elapsedRealtime()
        p.addAnalyticsListener(object : AnalyticsListener {
            override fun onLoadStarted(eventTime: AnalyticsListener.EventTime, loadEventInfo: LoadEventInfo, mediaLoadData: MediaLoadData) {
                loadsStarted++
            }
            override fun onLoadCompleted(eventTime: AnalyticsListener.EventTime, loadEventInfo: LoadEventInfo, mediaLoadData: MediaLoadData) {
                bytesLoaded += loadEventInfo.bytesLoaded
            }
            override fun onLoadCanceled(eventTime: AnalyticsListener.EventTime, loadEventInfo: LoadEventInfo, mediaLoadData: MediaLoadData) {
                bytesLoaded += loadEventInfo.bytesLoaded
            }
            override fun onLoadError(eventTime: AnalyticsListener.EventTime, loadEventInfo: LoadEventInfo, mediaLoadData: MediaLoadData, error: IOException, wasCanceled: Boolean) {
                bytesLoaded += loadEventInfo.bytesLoaded
                lastLoadError = describeIo(error)
            }
            override fun onBandwidthEstimate(eventTime: AnalyticsListener.EventTime, totalLoadTimeMs: Int, totalBytesLoaded: Long, bitrateEstimate: Long) {
                bytesLoaded += totalBytesLoaded
            }
        })
        // Subtitles follow the saved preference; the CC button in the controls changes and remembers it.
        p.trackSelectionParameters = p.trackSelectionParameters.buildUpon()
            .setTrackTypeDisabled(C.TRACK_TYPE_TEXT, !prefs.subtitles)
            .build()

        p.addListener(object : Player.Listener {
            override fun onPlayerError(error: PlaybackException) = handleError(error)

            override fun onTrackSelectionParametersChanged(parameters: TrackSelectionParameters) {
                prefs.subtitles = !parameters.disabledTrackTypes.contains(C.TRACK_TYPE_TEXT)
            }

            override fun onPlaybackStateChanged(playbackState: Int) {
                b.bufferBox.visibility =
                    if (playbackState == Player.STATE_BUFFERING) View.VISIBLE else View.GONE
                if (playbackState == Player.STATE_BUFFERING) bufferingSince = SystemClock.elapsedRealtime()
                if (playbackState == Player.STATE_ENDED) watchKey?.let { WatchProgress.ended(it) }
                if (playbackState == Player.STATE_READY) {
                    retries = 0
                    maybeOfferResume(p)
                    if (liveBaselineMs == C.TIME_UNSET && p.isCurrentMediaItemLive && p.currentLiveOffset != C.TIME_UNSET)
                        liveBaselineMs = p.currentLiveOffset
                }
            }

            override fun onIsPlayingChanged(isPlaying: Boolean) {
                val now = SystemClock.elapsedRealtime()
                if (isPlaying) {
                    if (pausedSince != 0L) { pausedTotalMs += now - pausedSince; pausedSince = 0L }
                } else if (!p.playWhenReady && pausedSince == 0L) {
                    pausedSince = now
                }
                updateStatus()
            }

            override fun onPlayWhenReadyChanged(playWhenReady: Boolean, reason: Int) {
                if (!playWhenReady && pausedSince == 0L) pausedSince = SystemClock.elapsedRealtime()
                updateStatus()
            }
        })

        var playUrl = url
        if (isLive && level.onDisk) {
            // Storage timeshift works on the raw MPEG-TS stream; swap an HLS address for it.
            val tsUrl = url.replace(Regex("\\.m3u8$"), ".ts")
            timeshift = TimeshiftServer(this, tsUrl, level.diskMinutes * 60_000L)
            playUrl = timeshift!!.localUrl
        }
        val item = MediaItem.Builder().setUri(playUrl)
        if (isLive && !level.onDisk) {
            // Never speed playback up to "catch up" with the live edge: pausing must not creep back to live.
            item.setLiveConfiguration(
                MediaItem.LiveConfiguration.Builder()
                    .setMinPlaybackSpeed(1f)
                    .setMaxPlaybackSpeed(1f)
                    .build()
            )
        }
        p.setMediaItem(item.build())
        // A title with a saved position waits for the resume question before playing.
        p.playWhenReady = !(watchKey != null && !resumeAsked && WatchProgress.resumePosition(watchKey!!) > 0)
        p.prepare()
    }

    // ---- Resume & watched ------------------------------------------------------

    private fun maybeOfferResume(p: ExoPlayer) {
        val key = watchKey ?: return
        if (resumeAsked) return
        resumeAsked = true
        val pos = WatchProgress.resumePosition(key)
        if (pos <= 0) { p.play(); return }
        AlertDialog.Builder(this)
            .setTitle(title.ifBlank { getString(R.string.resume_title) })
            .setMessage(getString(R.string.resume_msg_fmt, clock(pos)))
            .setPositiveButton(getString(R.string.resume_from_fmt, clock(pos))) { _, _ -> p.seekTo(pos); p.play() }
            .setNegativeButton(R.string.start_over) { _, _ -> p.seekTo(0); p.play() }
            .setOnCancelListener { p.seekTo(pos); p.play() }
            .show()
    }

    private fun saveProgress(force: Boolean = false) {
        val key = watchKey ?: return
        val p = player ?: return
        val now = SystemClock.elapsedRealtime()
        if (!force && now - lastProgressSave < 5_000) return
        lastProgressSave = now
        val duration = p.duration
        val pos = p.currentPosition
        if (duration == C.TIME_UNSET || duration <= 0 || pos <= 0) return
        if (p.playbackState == Player.STATE_IDLE) return
        WatchProgress.save(key, pos, duration)
    }

    private fun releasePlayer() {
        player?.release()
        player = null
        b.playerView.player = null
        timeshift?.close()
        timeshift = null
    }

    // ---- Seeking ---------------------------------------------------------------

    private fun seekBy(deltaMs: Long) {
        val p = player ?: return
        b.playerView.showController()
        val ts = timeshift
        when {
            ts != null -> {
                val rate = ts.rateBytesPerMs()
                if (rate <= 0.0) return
                val posBytes = itemFromBytes + (p.currentPosition * rate).toLong()
                val target = (posBytes + (deltaMs * rate).toLong())
                    .coerceIn(ts.baseBytes(), (ts.writtenBytes() - (rate * 1500).toLong()).coerceAtLeast(ts.baseBytes()))
                if (target == posBytes) return
                val wasPlaying = p.playWhenReady
                itemFromBytes = target
                p.setMediaItem(MediaItem.fromUri(ts.urlFrom(target)))
                p.prepare()
                p.playWhenReady = wasPlaying
            }
            p.isCurrentMediaItemSeekable -> {
                val duration = p.duration
                var target = p.currentPosition + deltaMs
                if (target < 0) target = 0
                if (duration != C.TIME_UNSET && target > duration) target = duration
                p.seekTo(target)
            }
            isLive -> Toast.makeText(this, R.string.seek_needs_buffer, Toast.LENGTH_LONG).show()
        }
        updateStatus()
    }

    private fun goLive() {
        val p = player ?: return
        pausedTotalMs = 0L; pausedSince = 0L; liveBaselineMs = C.TIME_UNSET
        val ts = timeshift
        when {
            ts != null -> {
                itemFromBytes = (ts.writtenBytes() - 512L * 1024).coerceAtLeast(ts.baseBytes())
                p.setMediaItem(MediaItem.fromUri(ts.urlLive()))
                p.prepare()
                p.play()
            }
            p.isCurrentMediaItemLive && p.isCurrentMediaItemSeekable -> {
                p.seekToDefaultPosition()
                p.play()
            }
            else -> {
                // Raw .ts streams can't seek: reopen the stream at the live point.
                retries = 0
                releasePlayer()
                initPlayer()
            }
        }
    }

    // ---- Errors & reconnects ---------------------------------------------------

    private fun handleError(error: PlaybackException) {
        val p = player
        if (error.errorCode == PlaybackException.ERROR_CODE_BEHIND_LIVE_WINDOW && p != null) {
            // Paused for longer than the provider keeps segments: rejoin at the live edge.
            Toast.makeText(this, R.string.live_window_lost, Toast.LENGTH_LONG).show()
            pausedTotalMs = 0L; pausedSince = 0L; liveBaselineMs = C.TIME_UNSET
            p.seekToDefaultPosition()
            p.prepare()
            return
        }
        val transient = error.errorCode in 2000..2999 || // network / IO
            error.errorCode == PlaybackException.ERROR_CODE_TIMEOUT ||
            error.errorCode == PlaybackException.ERROR_CODE_PARSING_CONTAINER_MALFORMED ||
            error.errorCode == PlaybackException.ERROR_CODE_DECODING_FAILED
        if (isLive && transient && retries < MAX_RETRIES) {
            retries++
            val delay = 1000L * retries
            b.bufferBox.visibility = View.VISIBLE
            b.txtBuffer.text = getString(R.string.reconnecting_fmt, retries, MAX_RETRIES)
            // Tear down and reopen off the listener callback.
            pendingRetry = Runnable { pendingRetry = null; releasePlayer(); initPlayer() }
                .also { handler.postDelayed(it, delay) }
            return
        }
        b.bufferBox.visibility = View.GONE
        b.txtError.text = describeError(error)
        b.txtError.visibility = View.VISIBLE
        b.btnRetry.visibility = View.VISIBLE
        b.btnRetry.requestFocus()
    }

    private fun describeError(error: PlaybackException): String =
        describeIo(error) ?: getString(R.string.playback_failed_fmt, error.errorCodeName)

    /** Human words for the network failure inside an ExoPlayer error chain, or null if it isn't one. */
    private fun describeIo(error: Throwable): String? {
        var cause: Throwable? = error
        while (cause != null) {
            when (cause) {
                is HttpDataSource.InvalidResponseCodeException -> {
                    val code = cause.responseCode
                    return when (code) {
                        403, 429, 458, 509 -> getString(R.string.err_stream_limit_fmt, code)
                        404 -> getString(R.string.err_not_found_fmt, code)
                        else -> getString(R.string.err_http_fmt, code)
                    }
                }
                is java.net.SocketTimeoutException -> return getString(R.string.err_timeout)
                is java.net.UnknownHostException -> return getString(R.string.err_dns)
                is java.net.ConnectException -> return getString(R.string.err_connect)
            }
            cause = cause.cause
        }
        return null
    }

    private fun cancelRetry() {
        pendingRetry?.let { handler.removeCallbacks(it) }
        pendingRetry = null
    }

    // ---- Status line -----------------------------------------------------------

    private fun updateStatus() {
        val p = player ?: return
        if (!isLive) saveProgress()
        if (p.playbackState == Player.STATE_BUFFERING && pendingRetry == null) {
            val ready = (p.totalBufferedDuration / 1000).toInt()
            val waited = (SystemClock.elapsedRealtime() - bufferingSince) / 1000
            val head = if (ready > 0) getString(R.string.buffering_fmt, ready) else getString(R.string.buffering)
            val detail = if (waited >= 3) "\n" + getString(
                R.string.buffer_detail_fmt, UpdateChecker.formatSize(bytesLoaded), waited,
                if (loadsStarted == 0) getString(R.string.connecting) else ""
            ).trimEnd() + (lastLoadError?.let { "\n$it" } ?: "") else ""
            b.txtBuffer.text = head + detail
        }
        if (!isLive) return
        val behindMs = behindLiveMs(p)
        val paused = !p.playWhenReady
        behindNow = paused || behindMs >= BEHIND_THRESHOLD_MS
        b.txtLiveStatus.text = when {
            paused -> getString(R.string.paused_behind_fmt, clock(behindMs))
            behindNow -> getString(R.string.behind_live_fmt, clock(behindMs))
            else -> getString(R.string.live_now)
        }
        b.txtLiveStatus.isSelected = behindNow
    }

    private fun behindLiveMs(p: ExoPlayer): Long {
        val paused = if (pausedSince != 0L) SystemClock.elapsedRealtime() - pausedSince else 0L
        val pausedEstimate = pausedTotalMs + paused
        timeshift?.let { ts ->
            if (ts.jumped) {
                ts.jumped = false
                Toast.makeText(this, R.string.timeshift_window_lost, Toast.LENGTH_LONG).show()
            }
            val rate = ts.rateBytesPerMs()
            if (rate <= 0.0) return pausedEstimate
            val posBytes = itemFromBytes + (p.currentPosition * rate).toLong()
            return ((ts.writtenBytes() - posBytes) / rate).toLong().coerceAtLeast(0L)
        }
        if (p.isCurrentMediaItemLive && p.currentLiveOffset != C.TIME_UNSET) {
            val base = if (liveBaselineMs == C.TIME_UNSET) p.currentLiveOffset else liveBaselineMs
            return maxOf(pausedEstimate, p.currentLiveOffset - base)
        }
        return pausedEstimate
    }

    private fun clock(ms: Long): String {
        val s = ms / 1000
        return if (s >= 3600) "%d:%02d:%02d".format(s / 3600, (s % 3600) / 60, s % 60)
        else "%d:%02d".format(s / 60, s % 60)
    }

    private fun hideSystemUi() {
        WindowCompat.setDecorFitsSystemWindows(window, false)
        WindowInsetsControllerCompat(window, b.root).let {
            it.hide(WindowInsetsCompat.Type.systemBars())
            it.systemBarsBehavior = WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
        }
    }

    companion object {
        private const val EXTRA_URL = "url"
        private const val EXTRA_TITLE = "title"
        private const val EXTRA_LIVE = "live"
        private const val EXTRA_KEY = "key"
        private const val MAX_RETRIES = 4
        private const val BEHIND_THRESHOLD_MS = 2_000L
        private const val SEEK_STEP_MS = 10_000L
        fun intent(ctx: Context, url: String, title: String, live: Boolean, watchKey: String? = null): Intent =
            Intent(ctx, PlayerActivity::class.java)
                .putExtra(EXTRA_URL, url)
                .putExtra(EXTRA_TITLE, title)
                .putExtra(EXTRA_LIVE, live)
                .putExtra(EXTRA_KEY, watchKey)
    }
}
