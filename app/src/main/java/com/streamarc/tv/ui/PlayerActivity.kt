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
import androidx.media3.common.Format
import androidx.media3.exoplayer.DecoderReuseEvaluation
import androidx.media3.common.MediaItem
import androidx.media3.common.PlaybackException
import androidx.media3.common.Player
import androidx.media3.common.TrackSelectionParameters
import androidx.media3.common.util.UnstableApi
import androidx.media3.datasource.DataSource
import androidx.media3.datasource.DataSpec
import androidx.media3.datasource.DefaultDataSource
import androidx.media3.datasource.TransferListener
import androidx.media3.datasource.okhttp.OkHttpDataSource
import androidx.media3.datasource.HttpDataSource
import androidx.media3.exoplayer.DefaultLoadControl
import androidx.media3.exoplayer.mediacodec.MediaCodecSelector
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.exoplayer.analytics.AnalyticsListener
import androidx.media3.exoplayer.source.LoadEventInfo
import androidx.media3.exoplayer.source.MediaLoadData
import java.io.IOException
import androidx.media3.exoplayer.source.DefaultMediaSourceFactory
import androidx.media3.exoplayer.upstream.DefaultLoadErrorHandlingPolicy
import com.streamarc.tv.R
import com.streamarc.tv.data.BufferLevel
import com.streamarc.tv.data.Episode
import com.streamarc.tv.data.Prefs
import com.streamarc.tv.data.SeriesCache
import com.streamarc.tv.data.Service
import com.streamarc.tv.transfer.Folders
import com.streamarc.tv.transfer.TransferStore
import androidx.lifecycle.lifecycleScope
import kotlinx.coroutines.launch
import okhttp3.OkHttpClient
import com.streamarc.tv.data.WatchProgress
import com.streamarc.tv.data.XtreamApi
import com.streamarc.tv.databinding.ActivityPlayerBinding
import com.streamarc.tv.player.TimeshiftServer
import com.streamarc.tv.transfer.TransferService
import com.streamarc.tv.update.UpdateChecker
import com.streamarc.tv.util.AppLog

@UnstableApi
class PlayerActivity : AppCompatActivity() {

    private lateinit var b: ActivityPlayerBinding
    /** The PlayerView in use: a TextureView-backed one on emulators, SurfaceView elsewhere. */
    private lateinit var pv: androidx.media3.ui.PlayerView
    private lateinit var prefs: Prefs
    private var player: ExoPlayer? = null
    private var timeshift: TimeshiftServer? = null
    /** Byte offset into the stored stream where the current media item starts (storage buffer only). */
    @Volatile private var itemFromBytes = 0L
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
    @Volatile private var bytesLoaded = 0L
    @Volatile private var loadsStarted = 0
    private var lastLoadError: String? = null
    private var bufferingSince = 0L
    private var videoInfo: String? = null
    private var decoderName: String? = null
    private var firstFrame = false
    private var nudges = 0
    /** Set when the hardware decoder produced nothing: the player is rebuilt preferring software decoders. */
    private var preferSoftware = false
    /** The user chose "Try anyway" after an unsupported-format warning. */
    private var forceTry = false
    /** Episodes started automatically in a row; after a few we ask "Still watching?". */
    private var autoPlays = 0
    private var nextCountdown: Runnable? = null
    private var nextOffered = false
    private var formatChecked = false
    /** Channel up / down is available (opened from the guide with its channel list). */
    private var zap = false
    /** Opened from the guide: its channel is known, so what's on now can be shown. */
    private var fromGuide = false
    private var guideStream: com.streamarc.tv.data.Stream? = null
    private var guideService: com.streamarc.tv.data.Service? = null
    private var nowCheckedAt = 0L
    private var nowFetching = false
    /** The other stream format (HLS / TS) was already tried for this channel. */
    private var triedAlternate = false
    /** Play the plain stream instead of the storage buffer (used when the buffered TS stream failed). */
    private var noTimeshift = false
    /** Chromecast: the stream is playing on a TV, and the local player is stopped. */
    private var casting = false
    private var castReady = false
    private var castContext: com.google.android.gms.cast.framework.CastContext? = null
    private var castListener: com.google.android.gms.cast.framework.SessionManagerListener<com.google.android.gms.cast.framework.CastSession>? = null
    /** Position to start from once the stream is ready (set by the resume prompt). */
    private var startPositionMs = 0L

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
        AppLog.i(TAG, "open live=$isLive title='$title' url=${AppLog.safeUrl(url)} emulator=$isEmulator")
        watchKey = intent.getStringExtra(EXTRA_KEY)?.takeIf { !isLive }
        fromGuide = isLive && intent.getBooleanExtra(EXTRA_ZAP, false)
        zap = fromGuide && com.streamarc.tv.player.ChannelQueue.available
        if (fromGuide) { guideStream = com.streamarc.tv.player.ChannelQueue.current(); guideService = com.streamarc.tv.player.ChannelQueue.service }

        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        b.txtTitle.text = title
        pv = if (isEmulator) b.playerViewTexture else b.playerView
        if (isEmulator) {
            b.playerView.visibility = View.GONE
            b.playerViewTexture.visibility = View.VISIBLE
            preferSoftware = true   // emulated hardware decoders tend to never output a frame
        }
        pv.setShowNextButton(false)
        pv.setShowPreviousButton(false)
        pv.setShowRewindButton(false)
        pv.setShowFastForwardButton(false)
        pv.setShowSubtitleButton(true)
        pv.controllerShowTimeoutMs = 4000
        pv.setControllerVisibilityListener(
            androidx.media3.ui.PlayerView.ControllerVisibilityListener { visibility ->
                b.topInfo.visibility = visibility
                b.txtLiveStatus.visibility = if (isLive) visibility else View.GONE
                if (visibility == View.VISIBLE) refreshNowPlaying(force = true)
            }
        )
        b.txtLiveStatus.visibility = View.GONE
        // The buttons live in the controller's own layout, along the bottom under the progress bar.
        pv.findViewById<View>(R.id.btnTracks).setOnClickListener { showTrackPicker() }
        pv.findViewById<View>(R.id.btnSeekBack).setOnClickListener { seekBy(-SEEK_STEP_MS) }
        pv.findViewById<View>(R.id.btnSeekFwd).setOnClickListener { seekBy(SEEK_STEP_MS) }
        pv.findViewById<View>(R.id.btnChannelUp).setOnClickListener { zapBy(1) }
        pv.findViewById<View>(R.id.btnChannelDown).setOnClickListener { zapBy(-1) }
        pv.findViewById<View>(R.id.channelBox).visibility = if (zap) View.VISIBLE else View.GONE
        setupCast()
        b.txtLiveStatus.setOnClickListener { goLive() }
        b.btnRetry.setOnClickListener { retries = 0; nudges = 0; releasePlayer(); initPlayer() }
        b.btnNextCancel.setOnClickListener { hideNext() }
    }

    override fun onStart() {
        super.onStart()
        hideSystemUi()
        TransferService.playbackActive = true
        nudges = 0
        val key = watchKey
        val saved = if (key != null && !resumeAsked) WatchProgress.resumePosition(key) else 0L
        if (casting) { /* the TV is playing; keep the local player stopped */ } else if (saved > 0) askResume(saved) else initPlayer()
        handler.post(ticker)
    }

    /** Asked before the stream is opened, so the player itself never waits on the answer. */
    private fun askResume(pos: Long) {
        resumeAsked = true
        AlertDialog.Builder(this)
            .setTitle(title.ifBlank { getString(R.string.resume_title) })
            .setMessage(getString(R.string.resume_msg_fmt, clock(pos)))
            .setPositiveButton(getString(R.string.resume_from_fmt, clock(pos))) { _, _ -> startPositionMs = pos; initPlayer() }
            .setNegativeButton(R.string.start_over) { _, _ -> startPositionMs = 0; initPlayer() }
            .setOnCancelListener { startPositionMs = pos; initPlayer() }
            .show()
    }

    override fun onDestroy() {
        super.onDestroy()
        try {
            castListener?.let { castContext?.sessionManager?.removeSessionManagerListener(it, com.google.android.gms.cast.framework.CastSession::class.java) }
            // Nothing in the app controls a cast once this screen is gone, so end it.
            if (isFinishing && casting) castContext?.sessionManager?.endCurrentSession(true)
        } catch (_: Exception) {}
    }

    // ---- Chromecast ----------------------------------------------------------------

    private fun setupCast() {
        if (!com.streamarc.tv.player.CastSupport.available(this)) return
        val cc = com.streamarc.tv.player.CastSupport.context(this) ?: return
        try {
            com.google.android.gms.cast.framework.CastButtonFactory.setUpMediaRouteButton(applicationContext, pv.findViewById<androidx.mediarouter.app.MediaRouteButton>(R.id.btnCast))
        } catch (e: Exception) {
            AppLog.w(TAG, "cast button unavailable: ${e.message}")
            return
        }
        castContext = cc
        castReady = true
        pv.findViewById<View>(R.id.btnCast).visibility = View.VISIBLE
        val listener = object : com.google.android.gms.cast.framework.SessionManagerListener<com.google.android.gms.cast.framework.CastSession> {
            override fun onSessionStarted(s: com.google.android.gms.cast.framework.CastSession, id: String) = startCasting(s)
            override fun onSessionResumed(s: com.google.android.gms.cast.framework.CastSession, wasSuspended: Boolean) = startCasting(s)
            override fun onSessionEnding(s: com.google.android.gms.cast.framework.CastSession) {
                // Remember where the TV had got to, so the phone carries on from there.
                if (!isLive) s.remoteMediaClient?.approximateStreamPosition?.let { if (it > 0) startPositionMs = it }
            }
            override fun onSessionEnded(s: com.google.android.gms.cast.framework.CastSession, error: Int) = stopCasting()
            override fun onSessionStarting(s: com.google.android.gms.cast.framework.CastSession) {}
            override fun onSessionStartFailed(s: com.google.android.gms.cast.framework.CastSession, error: Int) {
                Toast.makeText(this@PlayerActivity, R.string.cast_failed, Toast.LENGTH_LONG).show()
            }
            override fun onSessionResuming(s: com.google.android.gms.cast.framework.CastSession, id: String) {}
            override fun onSessionResumeFailed(s: com.google.android.gms.cast.framework.CastSession, error: Int) {}
            override fun onSessionSuspended(s: com.google.android.gms.cast.framework.CastSession, reason: Int) {}
        }
        castListener = listener
        cc.sessionManager.addSessionManagerListener(listener, com.google.android.gms.cast.framework.CastSession::class.java)
    }

    /** Hands the stream to the TV and stops playing it here. */
    private fun startCasting(session: com.google.android.gms.cast.framework.CastSession) {
        val client = session.remoteMediaClient ?: return
        val pos = if (isLive) 0L else (player?.currentPosition ?: startPositionMs)
        // The TV fetches the stream itself: live goes as HLS, and never through the local relay.
        val castUrl = if (isLive) url.removeSuffix(".ts").removeSuffix(".m3u8") + ".m3u8" else url
        val type = when {
            castUrl.endsWith(".m3u8") -> "application/x-mpegURL"
            castUrl.endsWith(".mkv") -> "video/x-matroska"
            else -> "video/mp4"
        }
        val meta = com.google.android.gms.cast.MediaMetadata(com.google.android.gms.cast.MediaMetadata.MEDIA_TYPE_MOVIE)
        meta.putString(com.google.android.gms.cast.MediaMetadata.KEY_TITLE, title)
        val info = com.google.android.gms.cast.MediaInfo.Builder(castUrl)
            .setStreamType(if (isLive) com.google.android.gms.cast.MediaInfo.STREAM_TYPE_LIVE else com.google.android.gms.cast.MediaInfo.STREAM_TYPE_BUFFERED)
            .setContentType(type)
            .setMetadata(meta)
            .build()
        AppLog.i(TAG, "casting to '${session.castDevice?.friendlyName}': ${AppLog.safeUrl(castUrl)}")
        client.load(com.google.android.gms.cast.MediaLoadRequestData.Builder().setMediaInfo(info).setAutoplay(true).setCurrentTime(pos).build())
        casting = true
        cancelRetry()
        releasePlayer()
        b.bufferBox.visibility = View.GONE
        b.txtCasting.text = getString(R.string.casting_to_fmt, session.castDevice?.friendlyName ?: "")
        b.txtCasting.visibility = View.VISIBLE
    }

    private fun stopCasting() {
        if (!casting) return
        casting = false
        b.txtCasting.visibility = View.GONE
        AppLog.i(TAG, "cast ended; playing here again")
        if (!isFinishing && !isDestroyed && player == null) initPlayer()
    }

    override fun onStop() {
        super.onStop()
        TransferService.playbackActive = false
        saveProgress(force = true)
        handler.removeCallbacks(ticker)
        hideNext()
        cancelRetry()
        releasePlayer()
        deleteIfWatchedDownload()
    }

    /** Settings → "Delete downloads after watching": a finished download goes once the player closes. */
    private fun deleteIfWatchedDownload() {
        val key = watchKey ?: return
        if (!key.startsWith("dl:") || !prefs.deleteAfterWatched || !WatchProgress.isWatched(key)) return
        val store = TransferStore.get(this)
        val job = store.get(key.removePrefix("dl:")) ?: return
        Folders.delete(this, job.fileUri)
        store.remove(job.id)
        WatchProgress.remove(key)
        Toast.makeText(this, getString(R.string.deleted_after_watching_fmt, job.title), Toast.LENGTH_SHORT).show()
    }

    // ---- Auto-play next episode ----------------------------------------------------

    private fun offerNext() {
        val key = watchKey ?: return
        if (nextOffered || !prefs.autoPlayNext || !key.startsWith("ep:")) return
        val entry = WatchProgress.get(key) ?: return
        val seriesId = entry.seriesId ?: return
        val account = prefs.account(Service.VOD) ?: return
        nextOffered = true
        lifecycleScope.launch {
            val eps = try { SeriesCache.ordered(SeriesCache.episodes(Service.VOD, account, seriesId)) } catch (_: Exception) { return@launch }
            val idx = eps.indexOfFirst { it.season == entry.season && it.number == entry.episode }
            val after = eps.drop(idx + 1)
            val next = after.firstOrNull { !WatchProgress.isWatched(WatchProgress.episodeKey(it.id)) } ?: after.firstOrNull() ?: return@launch
            if (player == null) return@launch
            showNext(next, entry.title ?: "", entry.image, seriesId, account)
        }
    }

    private fun showNext(next: Episode, seriesTitle: String, image: String?, seriesId: String, account: com.streamarc.tv.data.Account) {
        b.txtNextTitle.text = getString(R.string.up_next_fmt, next.season, next.number, next.title)
        b.nextBox.visibility = View.VISIBLE
        b.btnNextPlay.setOnClickListener { hideNext(); playNext(next, seriesTitle, image, seriesId, account) }
        b.btnNextPlay.requestFocus()
        if (autoPlays >= MAX_AUTO_PLAYS) {
            // Three in a row without a hand on the remote: wait for a press.
            b.txtNextCount.text = getString(R.string.still_watching)
            return
        }
        var left = NEXT_COUNTDOWN_S
        val tick = object : Runnable {
            override fun run() {
                b.txtNextCount.text = getString(R.string.playing_in_fmt, left)
                if (left <= 0) { hideNext(); playNext(next, seriesTitle, image, seriesId, account); return }
                left--
                handler.postDelayed(this, 1000)
            }
        }
        nextCountdown = tick
        handler.post(tick)
    }

    private fun hideNext() {
        nextCountdown?.let { handler.removeCallbacks(it) }
        nextCountdown = null
        b.nextBox.visibility = View.GONE
    }

    private fun playNext(next: Episode, seriesTitle: String, image: String?, seriesId: String, account: com.streamarc.tv.data.Account) {
        val nextUrl = try { XtreamApi.episodeUrl(Service.VOD, account, next) } catch (e: Exception) {
            Toast.makeText(this, e.message, Toast.LENGTH_LONG).show(); return
        }
        autoPlays++
        val key = WatchProgress.episodeKey(next.id)
        WatchProgress.describe(key, WatchProgress.KIND_EPISODE, seriesTitle, image, next.containerExtension, next.id,
            subtitle = next.title, seriesId = seriesId, season = next.season, episode = next.number)
        AppLog.i(TAG, "auto-play next ${com.streamarc.tv.data.Format.se(next.season, next.number)} (${autoPlays} in a row)")
        saveProgress(force = true)
        url = nextUrl
        title = "$seriesTitle · ${com.streamarc.tv.data.Format.se(next.season, next.number)} ${next.title}"
        b.txtTitle.text = title
        watchKey = key
        nextOffered = false
        resumeAsked = true
        startPositionMs = WatchProgress.resumePosition(key)
        retries = 0; nudges = 0
        releasePlayer()
        initPlayer()
    }


    override fun dispatchKeyEvent(event: KeyEvent): Boolean {
        if (event.action == KeyEvent.ACTION_DOWN) {
            autoPlays = 0   // someone is definitely watching
            when (event.keyCode) {
                KeyEvent.KEYCODE_MEDIA_REWIND -> { seekBy(-SEEK_STEP_MS); return true }
                KeyEvent.KEYCODE_MEDIA_FAST_FORWARD -> { seekBy(SEEK_STEP_MS); return true }
                // With the controls hidden, left/right on the remote skip; with them shown they move focus.
                KeyEvent.KEYCODE_CHANNEL_UP, KeyEvent.KEYCODE_PAGE_UP -> if (zap) { zapBy(1); return true }
                KeyEvent.KEYCODE_CHANNEL_DOWN, KeyEvent.KEYCODE_PAGE_DOWN -> if (zap) { zapBy(-1); return true }
                // With the controls hidden, up / down on the remote change channel too.
                KeyEvent.KEYCODE_DPAD_UP -> if (zap && !pv.isControllerFullyVisible) { zapBy(1); return true }
                KeyEvent.KEYCODE_DPAD_DOWN -> if (zap && !pv.isControllerFullyVisible) { zapBy(-1); return true }
                KeyEvent.KEYCODE_DPAD_LEFT -> if (!pv.isControllerFullyVisible) { seekBy(-SEEK_STEP_MS); return true }
                KeyEvent.KEYCODE_DPAD_RIGHT -> if (!pv.isControllerFullyVisible) { seekBy(SEEK_STEP_MS); return true }
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
        AppLog.i(TAG, "initPlayer level=${level.key} preferSoftware=$preferSoftware start=${startPositionMs}ms heap=${Runtime.getRuntime().maxMemory() shr 20}MB")

        // Any idle keep-alive connection to the provider (a paused download, say) still counts as
        // a stream on most panels; drop them so this stream gets the slot.
        Thread { try { XtreamApi.client.connectionPool.evictAll() } catch (_: Exception) {} }.start()

        // The player shares the app's HTTP client (and its connection pool) so a connection can be
        // cut the moment the player closes, and asks the server not to keep it alive at all.
        // With storage timeshift the player only ever talks to the local relay, which keeps the
        // upstream connection alive itself (reconnecting for up to 90 s). Give the player a long
        // read timeout there so a provider stall shows as buffering instead of an error.
        val disk = level.onDisk && !noTimeshift
        val useRelay = isLive && disk
        val httpFactory = OkHttpDataSource.Factory(if (useRelay) relayClient else playerClient)
            .setUserAgent(XtreamApi.USER_AGENT)
            .setDefaultRequestProperties(mapOf("Connection" to "close"))
            .setTransferListener(object : TransferListener {
                override fun onTransferInitializing(source: DataSource, dataSpec: DataSpec, isNetwork: Boolean) {}
                override fun onTransferStart(source: DataSource, dataSpec: DataSpec, isNetwork: Boolean) {
                    loadsStarted++
                    // The relay decides where a (re)connection starts; keep the behind-live maths in step.
                    timeshift?.let { ts -> if (dataSpec.uri.host == "127.0.0.1") itemFromBytes = ts.lastStart }
                }
                override fun onBytesTransferred(source: DataSource, dataSpec: DataSpec, isNetwork: Boolean, bytesTransferred: Int) { bytesLoaded += bytesTransferred }
                override fun onTransferEnd(source: DataSource, dataSpec: DataSpec, isNetwork: Boolean) {}
            })

        val mediaSources = DefaultMediaSourceFactory(this)
            .setDataSourceFactory(DefaultDataSource.Factory(this, httpFactory))
            .setLoadErrorHandlingPolicy(DefaultLoadErrorHandlingPolicy(2))

        val renderers = com.streamarc.tv.player.AppRenderersFactory(this)
        if (preferSoftware) {
            // Emulators and some boxes advertise a hardware decoder that never outputs a frame.
            renderers.setMediaCodecSelector { mime, secure, tunneling ->
                MediaCodecSelector.DEFAULT.getDecoderInfos(mime, secure, tunneling).sortedBy { if (it.softwareOnly) 0 else 1 }
            }
        }

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
            .build()
        player = p
        pv.player = p
        bytesLoaded = 0L; loadsStarted = 0; lastLoadError = null; bufferingSince = SystemClock.elapsedRealtime()
        videoInfo = null; decoderName = null; firstFrame = false; formatChecked = false
        p.addAnalyticsListener(object : AnalyticsListener {
            override fun onLoadStarted(eventTime: AnalyticsListener.EventTime, loadEventInfo: LoadEventInfo, mediaLoadData: MediaLoadData) {
                AppLog.i(TAG, "load start ${AppLog.safeUrl(loadEventInfo.uri.toString())} range=${loadEventInfo.dataSpec.position}")
            }
            override fun onLoadError(eventTime: AnalyticsListener.EventTime, loadEventInfo: LoadEventInfo, mediaLoadData: MediaLoadData, error: IOException, wasCanceled: Boolean) {
                lastLoadError = describeIo(error)
                AppLog.e(TAG, "load error bytes=${loadEventInfo.bytesLoaded} cancelled=$wasCanceled", error)
            }
            override fun onVideoInputFormatChanged(eventTime: AnalyticsListener.EventTime, format: Format, decoderReuseEvaluation: DecoderReuseEvaluation?) {
                videoInfo = "${format.sampleMimeType ?: "?"} ${format.width}x${format.height}"
                AppLog.i(TAG, "video format $videoInfo codecs=${format.codecs} fps=${format.frameRate} bitrate=${format.bitrate}")
            }
            override fun onAudioInputFormatChanged(eventTime: AnalyticsListener.EventTime, format: Format, decoderReuseEvaluation: DecoderReuseEvaluation?) {
                AppLog.i(TAG, "audio format ${format.sampleMimeType} ch=${format.channelCount} rate=${format.sampleRate}")
            }
            override fun onVideoDecoderInitialized(eventTime: AnalyticsListener.EventTime, decoderName: String, initializedTimestampMs: Long, initializationDurationMs: Long) {
                this@PlayerActivity.decoderName = decoderName
                AppLog.i(TAG, "video decoder $decoderName (${initializationDurationMs}ms)")
            }
            override fun onAudioDecoderInitialized(eventTime: AnalyticsListener.EventTime, decoderName: String, initializedTimestampMs: Long, initializationDurationMs: Long) {
                AppLog.i(TAG, "audio decoder $decoderName")
            }
            override fun onRenderedFirstFrame(eventTime: AnalyticsListener.EventTime, output: Any, renderTimeMs: Long) {
                firstFrame = true
                AppLog.i(TAG, "first frame rendered")
            }
            override fun onDroppedVideoFrames(eventTime: AnalyticsListener.EventTime, droppedFrames: Int, elapsedMs: Long) {
                AppLog.w(TAG, "dropped $droppedFrames frames in ${elapsedMs}ms")
            }
            override fun onVideoCodecError(eventTime: AnalyticsListener.EventTime, videoCodecError: Exception) {
                AppLog.e(TAG, "video codec error", videoCodecError)
            }
            override fun onAudioCodecError(eventTime: AnalyticsListener.EventTime, audioCodecError: Exception) {
                AppLog.e(TAG, "audio codec error", audioCodecError)
            }
            override fun onTracksChanged(eventTime: AnalyticsListener.EventTime, tracks: androidx.media3.common.Tracks) {
                val desc = tracks.groups.joinToString(" | ") { g ->
                    val f = g.getTrackFormat(0)
                    "${f.sampleMimeType} sel=${g.isSelected} sup=${g.isSupported}"
                }
                AppLog.i(TAG, "tracks: $desc")
                checkFormatSupport(p, tracks)
            }
        })
        // Subtitles follow the saved preference; the CC button in the controls changes and remembers it.
        p.trackSelectionParameters = p.trackSelectionParameters.buildUpon()
            .setTrackTypeDisabled(C.TRACK_TYPE_TEXT, !prefs.subtitles)
            .apply {
                prefs.audioLanguage?.let { setPreferredAudioLanguage(it) }
                prefs.subtitleLanguage?.let { setPreferredTextLanguage(it) }
            }
            .build()

        p.addListener(object : Player.Listener {
            override fun onPlayerError(error: PlaybackException) {
                AppLog.e(TAG, "player error ${error.errorCodeName}", error)
                handleError(error)
            }

            override fun onTrackSelectionParametersChanged(parameters: TrackSelectionParameters) {
                prefs.subtitles = !parameters.disabledTrackTypes.contains(C.TRACK_TYPE_TEXT)
            }

            override fun onPlaybackStateChanged(playbackState: Int) {
                AppLog.i(TAG, "state=${stateName(playbackState)} playWhenReady=${p.playWhenReady} pos=${p.currentPosition} buffered=${p.totalBufferedDuration}ms")
                b.bufferBox.visibility =
                    if (playbackState == Player.STATE_BUFFERING) View.VISIBLE else View.GONE
                if (playbackState == Player.STATE_BUFFERING) bufferingSince = SystemClock.elapsedRealtime()
                if (playbackState == Player.STATE_ENDED) {
                    watchKey?.let { WatchProgress.ended(it) }
                    if (!isLive) offerNext()
                }
                if (playbackState == Player.STATE_READY) {
                    retries = 0
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
                AppLog.i(TAG, "playWhenReady=$playWhenReady reason=$reason (1 user, 2 focus loss, 3 becoming noisy, 4 remote, 5 end of item, 6 suppressed)")
                if (!playWhenReady && pausedSince == 0L) pausedSince = SystemClock.elapsedRealtime()
                updateStatus()
            }

            override fun onPlaybackSuppressionReasonChanged(playbackSuppressionReason: Int) {
                AppLog.i(TAG, "suppression=$playbackSuppressionReason")
            }
        })

        var playUrl = url
        if (isLive && disk) {
            // Storage timeshift works on the raw MPEG-TS stream; swap an HLS address for it.
            val tsUrl = url.replace(Regex("\\.m3u8$"), ".ts")
            timeshift = TimeshiftServer(this, tsUrl, level.diskMinutes * 60_000L)
            playUrl = timeshift!!.localUrl
        }
        val item = MediaItem.Builder().setUri(playUrl)
        if (isLive && !disk) {
            // Never speed playback up to "catch up" with the live edge: pausing must not creep back to live.
            item.setLiveConfiguration(
                MediaItem.LiveConfiguration.Builder()
                    .setMinPlaybackSpeed(1f)
                    .setMaxPlaybackSpeed(1f)
                    .build()
            )
        }
        if (startPositionMs > 0) p.setMediaItem(item.build(), startPositionMs) else p.setMediaItem(item.build())
        startPositionMs = 0
        p.playWhenReady = true
        p.prepare()
    }

    // ---- Resume & watched ------------------------------------------------------

    /**
     * A player that has plenty buffered but never becomes ready is stuck in the decoder, not the
     * network. Nudge it with a seek, then a fresh prepare, then give up with a clear message.
     */
    private fun watchdog(p: ExoPlayer) {
        if (p.playbackState != Player.STATE_BUFFERING) return
        val waited = SystemClock.elapsedRealtime() - bufferingSince
        val buffered = p.totalBufferedDuration
        if (buffered < 4_000 || waited < 8_000) return
        AppLog.w(TAG, "watchdog stage=$nudges buffered=${buffered}ms waited=${waited}ms firstFrame=$firstFrame playWhenReady=${p.playWhenReady} decoder=$decoderName")
        if (!p.playWhenReady) p.playWhenReady = true
        when (nudges) {
            0 -> { nudges = 1; bufferingSince = SystemClock.elapsedRealtime(); p.seekTo(p.currentPosition) }
            1 -> { nudges = 2; bufferingSince = SystemClock.elapsedRealtime(); val pos = p.currentPosition; p.stop(); p.seekTo(pos); p.prepare(); p.play() }
            2 -> if (!firstFrame && !preferSoftware) {
                // Data is there but no frame has ever been drawn: try the software decoder.
                nudges = 3
                preferSoftware = true
                val pos = p.currentPosition
                releasePlayer()
                startPositionMs = pos
                initPlayer()
                bufferingSince = SystemClock.elapsedRealtime()
            } else {
                nudges = 4
                p.pause()
                b.bufferBox.visibility = View.GONE
                b.txtError.text = getString(R.string.err_decoder_stuck_fmt, videoInfo ?: "?", decoderName ?: "?")
                b.txtError.visibility = View.VISIBLE
                b.btnRetry.visibility = View.VISIBLE
                b.btnRetry.requestFocus()
            }
            3 -> {
                nudges = 4
                p.pause()
                b.bufferBox.visibility = View.GONE
                b.txtError.text = getString(R.string.err_decoder_stuck_fmt, videoInfo ?: "?", decoderName ?: "?")
                b.txtError.visibility = View.VISIBLE
                b.btnRetry.visibility = View.VISIBLE
                b.btnRetry.requestFocus()
            }
        }
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
        player?.let { AppLog.i(TAG, "release pos=${it.currentPosition} state=${stateName(it.playbackState)}") }
        player?.release()
        player = null
        pv.player = null
        timeshift?.close()
        timeshift = null
        // Sever every connection to the provider now, not when the pool feels like it.
        Thread { try { XtreamApi.client.connectionPool.evictAll() } catch (_: Exception) {} }.start()
    }

    // ---- Seeking ---------------------------------------------------------------

    private fun seekBy(deltaMs: Long) {
        val p = player ?: return
        pv.showController()
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
        if (isLive && tryAlternateFormat(error)) return
        b.bufferBox.visibility = View.GONE
        b.txtError.text = describeError(error)
        AppLog.e(TAG, "giving up: ${b.txtError.text} | bytes=$bytesLoaded loads=$loadsStarted video=$videoInfo decoder=$decoderName firstFrame=$firstFrame " +
            "network=${com.streamarc.tv.data.Diagnose.describeNetwork(this)} | ${com.streamarc.tv.data.Diagnose.chain(error)}")
        b.txtError.visibility = View.VISIBLE
        b.btnRetry.visibility = View.VISIBLE
        b.btnRetry.requestFocus()
    }

    /**
     * Some channels only work as HLS and some only as raw TS. When one fails, quietly try the other
     * once before showing an error. With a storage buffer (always TS through the relay) the other
     * format is the plain HLS stream.
     */
    private fun tryAlternateFormat(error: PlaybackException): Boolean {
        if (triedAlternate) return false
        val base = url.removeSuffix(".m3u8").removeSuffix(".ts")
        if (base == url) return false
        val onDisk = BufferLevel.from(prefs.bufferLevel).onDisk && !noTimeshift
        val newUrl = when {
            onDisk -> { noTimeshift = true; "$base.m3u8" }
            url.endsWith(".m3u8") -> "$base.ts"
            else -> "$base.m3u8"
        }
        triedAlternate = true
        AppLog.w(TAG, "format fallback after ${error.errorCodeName}: ${AppLog.safeUrl(url)} -> ${AppLog.safeUrl(newUrl)}")
        Toast.makeText(this, R.string.trying_other_format, Toast.LENGTH_SHORT).show()
        url = newUrl
        retries = 0; nudges = 0
        cancelRetry()
        releasePlayer()
        initPlayer()
        return true
    }

    // ---- Channel up / down -------------------------------------------------------

    private fun zapBy(delta: Int) {
        val s = com.streamarc.tv.player.ChannelQueue.step(delta) ?: return
        val svc = com.streamarc.tv.player.ChannelQueue.service ?: return
        val acct = prefs.account(svc) ?: return
        val newUrl = try { XtreamApi.streamUrl(svc, acct, s, prefs.liveFormat, true) } catch (e: Exception) { return }
        AppLog.i(TAG, "channel ${if (delta > 0) "up" else "down"} -> '${s.name}'")
        url = newUrl
        title = s.name ?: ""
        b.txtTitle.text = title
        retries = 0; nudges = 0
        triedAlternate = false; noTimeshift = false
        guideStream = s
        b.txtNow.visibility = View.GONE
        cancelRetry()
        releasePlayer()
        initPlayer()
        pv.showController()   // brings up the title so the new channel is named
        refreshNowPlaying(force = true)
    }

    // ---- Audio and subtitle tracks ---------------------------------------------------

    private class TrackChoice(val label: String, val action: () -> Unit)

    private fun shortCodec(mime: String?): String = when {
        mime == null -> ""
        mime == "audio/mp4a-latm" -> "AAC"
        mime == "audio/ac3" -> "Dolby Digital"
        mime == "audio/eac3" || mime == "audio/eac3-joc" -> "Dolby Digital Plus"
        mime == "audio/mpeg-L2" -> "MP2"
        mime == "audio/mpeg" -> "MP3"
        mime == "audio/true-hd" -> "TrueHD"
        mime.startsWith("audio/vnd.dts") -> "DTS"
        else -> mime.substringAfter('/')
    }

    private fun channelLayout(n: Int) = when (n) { 1 -> "mono"; 2 -> "stereo"; 6 -> "5.1"; 8 -> "7.1"; else -> "$n ch" }

    private fun trackName(f: Format, number: Int, audio: Boolean): String {
        val lang = f.language?.takeIf { it != "und" && it.isNotBlank() }
            ?.let { java.util.Locale(it).displayLanguage.replaceFirstChar { c -> c.titlecase() } }
        val main = f.label?.takeIf { it.isNotBlank() } ?: lang ?: getString(R.string.track_number_fmt, number)
        val extra = ArrayList<String>()
        if (f.label != null && lang != null && !f.label!!.contains(lang, ignoreCase = true)) extra.add(lang)
        if (audio) {
            if (f.channelCount > 0) extra.add(channelLayout(f.channelCount))
            shortCodec(f.sampleMimeType).takeIf { it.isNotEmpty() }?.let { extra.add(it) }
        }
        return if (extra.isEmpty()) main else "$main · ${extra.joinToString(" · ")}"
    }

    /** Lists the audio and subtitle tracks of the stream; choosing one switches to it and remembers the language. */
    private fun showTrackPicker() {
        val p = player ?: return
        val choices = ArrayList<TrackChoice>()
        val textDisabled = p.trackSelectionParameters.disabledTrackTypes.contains(C.TRACK_TYPE_TEXT)
        var audioNo = 0
        var textNo = 0
        for (g in p.currentTracks.groups) {
            if (g.type != C.TRACK_TYPE_AUDIO && g.type != C.TRACK_TYPE_TEXT) continue
            for (i in 0 until g.length) {
                if (!g.isTrackSupported(i)) continue
                val f = g.getTrackFormat(i)
                val audio = g.type == C.TRACK_TYPE_AUDIO
                val n = if (audio) ++audioNo else ++textNo
                val name = trackName(f, n, audio)
                val on = g.isTrackSelected(i) && (audio || !textDisabled)
                val label = (if (on) "✓ " else "") + getString(if (audio) R.string.tracks_audio_fmt else R.string.tracks_subtitle_fmt, name)
                val group = g
                val index = i
                choices.add(TrackChoice(label) {
                    val params = p.trackSelectionParameters.buildUpon()
                        .setOverrideForType(androidx.media3.common.TrackSelectionOverride(group.mediaTrackGroup, index))
                    if (audio) {
                        f.language?.takeIf { it != "und" }?.let { prefs.audioLanguage = it }
                    } else {
                        params.setTrackTypeDisabled(C.TRACK_TYPE_TEXT, false)
                        f.language?.takeIf { it != "und" }?.let { prefs.subtitleLanguage = it }
                    }
                    p.trackSelectionParameters = params.build()
                })
            }
        }
        if (textNo > 0) {
            choices.add(TrackChoice((if (textDisabled) "✓ " else "") + getString(R.string.tracks_subtitles_off)) {
                p.trackSelectionParameters = p.trackSelectionParameters.buildUpon().setTrackTypeDisabled(C.TRACK_TYPE_TEXT, true).build()
            })
        }
        if (choices.size <= 1 && audioNo <= 1 && textNo == 0) {
            Toast.makeText(this, R.string.tracks_none, Toast.LENGTH_LONG).show()
            return
        }
        FocusDialog(this)
            .setTitle(R.string.tracks_title)
            .setItems(choices.map { it.label }.toTypedArray()) { _, which -> choices[which].action() }
            .setNegativeButton(android.R.string.cancel, null)
            .show()
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

    /**
     * If no decoder on this device can handle the video (or the audio), say so straight away
     * with the format names rather than buffering forever. Video: stop and offer "Try anyway";
     * audio only: keep playing silently and say why.
     */
    private fun checkFormatSupport(p: ExoPlayer, tracks: androidx.media3.common.Tracks) {
        if (formatChecked) return
        val video = tracks.groups.filter { it.type == C.TRACK_TYPE_VIDEO }
        val audio = tracks.groups.filter { it.type == C.TRACK_TYPE_AUDIO }
        if (video.isEmpty() && audio.isEmpty()) return
        formatChecked = true
        fun handled(g: androidx.media3.common.Tracks.Group) = (0 until g.length).any { g.getTrackSupport(it) == C.FORMAT_HANDLED }
        val badVideo = video.isNotEmpty() && video.none { handled(it) }
        val badAudio = audio.isNotEmpty() && audio.none { handled(it) }
        if (!badVideo && !badAudio) return
        val names = listOfNotNull(
            video.firstOrNull()?.getTrackFormat(0)?.let { codecName(it) }?.let { if (badVideo) "$it (video)" else null },
            audio.firstOrNull()?.getTrackFormat(0)?.let { codecName(it) }?.let { if (badAudio) "$it (audio)" else null },
        ).joinToString(", ")
        AppLog.w(TAG, "unsupported on this device: $names (forceTry=$forceTry)")
        if (badVideo && !forceTry) {
            p.pause()
            b.bufferBox.visibility = View.GONE
            b.txtError.text = getString(R.string.err_unsupported_fmt, names)
            b.txtError.visibility = View.VISIBLE
            b.btnRetry.text = getString(R.string.try_anyway)
            b.btnRetry.visibility = View.VISIBLE
            b.btnRetry.setOnClickListener {
                forceTry = true; retries = 0; nudges = 0
                b.btnRetry.text = getString(R.string.retry)
                releasePlayer(); initPlayer()
            }
            b.btnRetry.requestFocus()
        } else if (badAudio) {
            Toast.makeText(this, getString(R.string.no_audio_fmt, names), Toast.LENGTH_LONG).show()
        }
    }

    private fun codecName(f: Format): String {
        val mime = f.sampleMimeType ?: return "?"
        val codecs = f.codecs.orEmpty()
        return when {
            mime == "video/hevc" && (codecs.startsWith("hvc1.2") || codecs.startsWith("hev1.2")) -> "HEVC 10-bit (H.265 Main 10)"
            mime == "video/hevc" -> "HEVC (H.265)"
            mime == "video/avc" -> "H.264"
            mime == "video/av01" -> "AV1"
            mime == "video/x-vnd.on2.vp9" -> "VP9"
            mime == "audio/eac3" || mime == "audio/eac3-joc" -> "Dolby Digital Plus (E-AC3)"
            mime == "audio/ac3" -> "Dolby Digital (AC3)"
            mime == "audio/true-hd" -> "Dolby TrueHD"
            mime.startsWith("audio/vnd.dts") -> "DTS"
            mime == "audio/mp4a-latm" -> "AAC"
            else -> mime
        } + if (codecs.isNotBlank()) " · $codecs" else ""
    }

    private fun stateName(s: Int) = when (s) {
        Player.STATE_IDLE -> "IDLE"; Player.STATE_BUFFERING -> "BUFFERING"; Player.STATE_READY -> "READY"; Player.STATE_ENDED -> "ENDED"; else -> "$s"
    }

    private fun cancelRetry() {
        pendingRetry?.let { handler.removeCallbacks(it) }
        pendingRetry = null
    }

    // ---- Status line -----------------------------------------------------------

    /** What's on now and next for a channel opened from the guide; shown under the title while the controls are up. */
    private fun refreshNowPlaying(force: Boolean = false) {
        val s = guideStream ?: return
        val svc = guideService ?: return
        val now = SystemClock.elapsedRealtime()
        if (!force && now - nowCheckedAt < 20_000) return
        nowCheckedAt = now
        val id = s.streamId
        val list = com.streamarc.tv.data.EpgCache.guideFor(svc, s.epgChannelId, s.name)
            ?: id?.let { com.streamarc.tv.data.EpgCache.peek(svc, it) }
        if (list == null) {
            val account = prefs.account(svc)
            if (id != null && account != null && !nowFetching) {
                nowFetching = true
                lifecycleScope.launch {
                    try { com.streamarc.tv.data.EpgCache.get(svc, account, id) } catch (_: Exception) {} finally { nowFetching = false }
                    if (!isDestroyed) refreshNowPlaying(force = true)
                }
            }
            return
        }
        val sec = System.currentTimeMillis() / 1000
        val cur = list.firstOrNull { it.isOnNow(sec) }
        val next = list.filter { it.start >= (cur?.end ?: sec) }.minByOrNull { it.start }
        val text = listOfNotNull(
            cur?.let { getString(R.string.on_now_fmt, it.title, com.streamarc.tv.data.Format.timeRange(it.start, it.end)) },
            next?.let { getString(R.string.on_next_fmt, it.title, com.streamarc.tv.data.Format.time(it.start)) }
        ).joinToString("\n")
        b.txtNow.text = text
        b.txtNow.visibility = if (text.isEmpty()) View.GONE else View.VISIBLE
    }

    private fun updateStatus() {
        val p = player ?: return
        refreshNowPlaying()
        if (!isLive) saveProgress()
        watchdog(p)
        if (p.playbackState == Player.STATE_BUFFERING && pendingRetry == null) {
            val ready = (p.totalBufferedDuration / 1000).toInt()
            val waited = (SystemClock.elapsedRealtime() - bufferingSince) / 1000
            val head = if (ready > 0) getString(R.string.buffering_fmt, ready) else getString(R.string.buffering)
            val detail = if (waited >= 3) "\n" + getString(
                R.string.buffer_detail_fmt, UpdateChecker.formatSize(bytesLoaded), waited,
                if (loadsStarted == 0) getString(R.string.connecting) else ""
            ).trimEnd() +
                (if (waited >= 6) "\n" + getString(R.string.video_detail_fmt, videoInfo ?: "?", decoderName ?: "?",
                    getString(if (firstFrame) R.string.frame_yes else R.string.frame_no)) else "") +
                (lastLoadError?.let { "\n$it" } ?: "") else ""
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
        private const val TAG = "Player"
        private const val EXTRA_URL = "url"
        private const val EXTRA_TITLE = "title"
        private const val EXTRA_LIVE = "live"
        private const val EXTRA_KEY = "key"
        private const val EXTRA_ZAP = "zap"
        private const val MAX_RETRIES = 4
        private const val BEHIND_THRESHOLD_MS = 2_000L
        private const val SEEK_STEP_MS = 10_000L
        private const val NEXT_COUNTDOWN_S = 10
        private const val MAX_AUTO_PLAYS = 3
        /** BlueStacks, Genymotion, the Android emulator: x86 builds or telltale fingerprints. */
        val isEmulator: Boolean by lazy {
            val abis = android.os.Build.SUPPORTED_ABIS.joinToString().lowercase()
            val fp = (android.os.Build.FINGERPRINT + android.os.Build.MANUFACTURER + android.os.Build.MODEL + android.os.Build.PRODUCT + android.os.Build.HARDWARE).lowercase()
            abis.contains("x86") || listOf("generic", "vbox", "bluestacks", "genymotion", "goldfish", "ranchu", "emulator", "sdk_gphone", "nox", "ldplayer", "memu")
                .any { fp.contains(it) }
        }
        /** For the loopback timeshift relay: it reconnects upstream for up to 90 s, so wait it out. */
        private val relayClient by lazy {
            OkHttpClient.Builder()
                .connectTimeout(5, java.util.concurrent.TimeUnit.SECONDS)
                .readTimeout(120, java.util.concurrent.TimeUnit.SECONDS)
                .callTimeout(0, java.util.concurrent.TimeUnit.MILLISECONDS)
                .build()
        }

        /** Same pool as the API client, with streaming timeouts. */
        private val playerClient by lazy {
            XtreamApi.client.newBuilder()
                .connectTimeout(15, java.util.concurrent.TimeUnit.SECONDS)
                .readTimeout(20, java.util.concurrent.TimeUnit.SECONDS)
                .callTimeout(0, java.util.concurrent.TimeUnit.MILLISECONDS)
                .followRedirects(true).followSslRedirects(true)
                .build()
        }
        /** [zap]: opened from the guide, so channel up / down can use the guide's channel list. */
        fun intent(ctx: Context, url: String, title: String, live: Boolean, watchKey: String? = null, zap: Boolean = false): Intent =
            Intent(ctx, PlayerActivity::class.java)
                .putExtra(EXTRA_URL, url)
                .putExtra(EXTRA_TITLE, title)
                .putExtra(EXTRA_LIVE, live)
                .putExtra(EXTRA_KEY, watchKey)
                .putExtra(EXTRA_ZAP, zap)
    }
}
