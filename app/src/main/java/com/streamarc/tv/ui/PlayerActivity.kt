package com.streamarc.tv.ui

import android.content.Context
import android.content.Intent
import android.os.Bundle
import android.view.View
import android.view.WindowManager
import androidx.appcompat.app.AppCompatActivity
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import androidx.media3.common.MediaItem
import androidx.media3.common.PlaybackException
import androidx.media3.common.Player
import androidx.media3.common.util.UnstableApi
import androidx.media3.datasource.DefaultHttpDataSource
import androidx.media3.exoplayer.DefaultRenderersFactory
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.exoplayer.source.DefaultMediaSourceFactory
import com.streamarc.tv.data.XtreamApi
import com.streamarc.tv.databinding.ActivityPlayerBinding

@UnstableApi
class PlayerActivity : AppCompatActivity() {

    private lateinit var b: ActivityPlayerBinding
    private var player: ExoPlayer? = null
    private lateinit var url: String
    private var title: String = ""
    private var isLive: Boolean = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        b = ActivityPlayerBinding.inflate(layoutInflater)
        setContentView(b.root)

        url = intent.getStringExtra(EXTRA_URL) ?: run { finish(); return }
        title = intent.getStringExtra(EXTRA_TITLE) ?: ""
        isLive = intent.getBooleanExtra(EXTRA_LIVE, false)

        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        b.txtTitle.text = title
        b.playerView.setShowNextButton(false)
        b.playerView.setShowPreviousButton(false)
        b.playerView.controllerShowTimeoutMs = 4000
        b.playerView.setControllerVisibilityListener(
            androidx.media3.ui.PlayerView.ControllerVisibilityListener { visibility ->
                b.txtTitle.visibility = visibility
            }
        )
        b.btnRetry.setOnClickListener { releasePlayer(); initPlayer() }
    }

    override fun onStart() {
        super.onStart()
        hideSystemUi()
        initPlayer()
    }

    override fun onStop() {
        super.onStop()
        releasePlayer()
    }

    private fun initPlayer() {
        if (player != null) return
        b.txtError.visibility = View.GONE
        b.btnRetry.visibility = View.GONE

        val httpFactory = DefaultHttpDataSource.Factory()
            .setUserAgent(XtreamApi.USER_AGENT)
            .setAllowCrossProtocolRedirects(true)
            .setConnectTimeoutMs(20_000)
            .setReadTimeoutMs(30_000)

        val renderers = DefaultRenderersFactory(this)
            .setExtensionRendererMode(DefaultRenderersFactory.EXTENSION_RENDERER_MODE_PREFER)

        val p = ExoPlayer.Builder(this, renderers)
            .setMediaSourceFactory(DefaultMediaSourceFactory(this).setDataSourceFactory(httpFactory))
            .build()
        player = p
        b.playerView.player = p

        p.addListener(object : Player.Listener {
            override fun onPlayerError(error: PlaybackException) {
                b.progress.visibility = View.GONE
                b.txtError.text = "Playback failed: ${error.errorCodeName}"
                b.txtError.visibility = View.VISIBLE
                b.btnRetry.visibility = View.VISIBLE
                b.btnRetry.requestFocus()
            }

            override fun onPlaybackStateChanged(playbackState: Int) {
                b.progress.visibility =
                    if (playbackState == Player.STATE_BUFFERING) View.VISIBLE else View.GONE
            }
        })

        p.setMediaItem(MediaItem.fromUri(url))
        p.playWhenReady = true
        p.prepare()
    }

    private fun releasePlayer() {
        player?.release()
        player = null
        b.playerView.player = null
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
        fun intent(ctx: Context, url: String, title: String, live: Boolean): Intent =
            Intent(ctx, PlayerActivity::class.java)
                .putExtra(EXTRA_URL, url)
                .putExtra(EXTRA_TITLE, title)
                .putExtra(EXTRA_LIVE, live)
    }
}
