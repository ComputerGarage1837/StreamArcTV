package com.streamarc.tv.ui

import android.content.Context
import android.content.Intent
import android.os.Bundle
import android.text.Editable
import android.text.TextWatcher
import android.view.LayoutInflater
import android.view.View
import android.widget.ArrayAdapter
import android.widget.Toast
import androidx.appcompat.app.AlertDialog
import androidx.appcompat.app.AppCompatActivity
import androidx.lifecycle.lifecycleScope
import androidx.media3.common.C
import androidx.media3.common.MediaItem
import androidx.media3.common.PlaybackException
import androidx.media3.common.Player
import androidx.media3.common.util.UnstableApi
import androidx.media3.datasource.DefaultDataSource
import androidx.media3.datasource.okhttp.OkHttpDataSource
import androidx.media3.exoplayer.DefaultLoadControl
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.exoplayer.source.DefaultMediaSourceFactory
import com.streamarc.tv.R
import com.streamarc.tv.data.Account
import com.streamarc.tv.data.ContentKind
import com.streamarc.tv.data.Prefs
import com.streamarc.tv.data.Service
import com.streamarc.tv.data.Stream
import com.streamarc.tv.data.XtreamApi
import com.streamarc.tv.databinding.ActivityMultiviewBinding
import com.streamarc.tv.databinding.DialogChannelPickerBinding
import com.streamarc.tv.databinding.ItemTileBinding
import com.streamarc.tv.util.AppLog
import kotlinx.coroutines.launch
import java.util.concurrent.TimeUnit

/**
 * Two or four live channels at once. The selected tile carries the sound; OK on a tile picks
 * its channel, a long press offers full screen or removal. Each tile is its own small player,
 * so the provider must allow that many streams on the account.
 */
@UnstableApi
class MultiViewActivity : AppCompatActivity() {

    private lateinit var b: ActivityMultiviewBinding
    private lateinit var prefs: Prefs
    private lateinit var service: Service
    private lateinit var account: Account

    private class Tile(val vb: ItemTileBinding) {
        var player: ExoPlayer? = null
        var stream: Stream? = null
    }

    private val tiles = ArrayList<Tile>()
    private var selected = 0
    private var channels: List<Stream> = emptyList()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        b = ActivityMultiviewBinding.inflate(layoutInflater)
        setContentView(b.root)
        Edge.pad(b.root)
        prefs = Prefs(this)
        service = Service.valueOf(intent.getStringExtra(EXTRA_SERVICE) ?: Service.LIVE.name)
        account = prefs.account(service) ?: run { finish(); return }

        b.btnBack.setOnClickListener { finish() }
        b.btnTiles.setOnClickListener {
            prefs.multiviewTiles = if (prefs.multiviewTiles == 4) 2 else 4
            buildGrid()
        }
        buildGrid()
        loadChannels()
    }

    override fun onStart() {
        super.onStart()
        startAll()
    }

    override fun onStop() {
        super.onStop()
        stopAll()
    }

    // ---- Grid -------------------------------------------------------------------

    private fun buildGrid() {
        stopAll()
        b.rowTop.removeAllViews(); b.rowBottom.removeAllViews()
        tiles.clear()
        val count = prefs.multiviewTiles
        b.btnTiles.text = if (count == 4) "2 × 2" else "1 × 2"
        b.rowBottom.visibility = if (count == 4) View.VISIBLE else View.GONE
        val slots = prefs.multiviewSlots
        for (i in 0 until count) {
            val vb = ItemTileBinding.inflate(LayoutInflater.from(this), if (i < 2) b.rowTop else b.rowBottom, true)
            val tile = Tile(vb)
            tiles.add(tile)
            vb.root.setOnClickListener { select(i); if (tile.stream == null) pickChannel(i) else pickChannel(i) }
            vb.root.setOnLongClickListener { select(i); tileMenu(i); true }
            vb.root.setOnFocusChangeListener { _, has -> if (has) select(i) }
            slots.getOrNull(i)?.takeIf { it.isNotBlank() }?.let { id -> pendingIds[i] = id }
        }
        select(0)
        tiles.firstOrNull()?.vb?.root?.requestFocus()
        applyPending()
        if (started) startAll()
    }

    private val pendingIds = HashMap<Int, String>()
    private var started = false

    /** Channels saved from last time are attached once the channel list is in. */
    private fun applyPending() {
        if (channels.isEmpty()) return
        for ((i, id) in pendingIds.toMap()) {
            channels.firstOrNull { it.id == id }?.let { assign(i, it, save = false) }
            pendingIds.remove(i)
        }
    }

    private fun loadChannels() {
        lifecycleScope.launch {
            try {
                val all = cached ?: XtreamApi.streams(service, account, null, ContentKind.LIVE).also { cached = it }
                val favs = prefs.favorites(service, ContentKind.LIVE)
                val hidden = prefs.hiddenLiveChannels
                channels = all.filter { it.id !in hidden }.sortedWith(compareBy({ !favs.contains(it.id) }, { it.name?.lowercase() ?: "" }))
                applyPending()
            } catch (e: Exception) {
                Toast.makeText(this@MultiViewActivity, com.streamarc.tv.data.Diagnose.explain(this@MultiViewActivity, e), Toast.LENGTH_LONG).show()
            }
        }
    }

    private fun select(i: Int) {
        selected = i
        for ((j, t) in tiles.withIndex()) {
            t.vb.root.isSelected = j == i
            t.player?.volume = if (j == i) 1f else 0f
            t.vb.txtTileName.text = (if (j == i && t.stream != null) "🔊 " else "") + (t.stream?.name ?: "")
        }
    }

    // ---- Channel choice ---------------------------------------------------------

    private fun pickChannel(i: Int) {
        if (channels.isEmpty()) { Toast.makeText(this, R.string.loading_channels, Toast.LENGTH_SHORT).show(); return }
        val vb = DialogChannelPickerBinding.inflate(LayoutInflater.from(this))
        var shown = channels
        val adapter = ArrayAdapter(this, android.R.layout.simple_list_item_1, shown.map { it.name ?: "" }.toMutableList())
        vb.listChannels.adapter = adapter
        val dialog = FocusDialog(this).setTitle(R.string.choose_channel).setView(vb.root)
            .setNegativeButton(android.R.string.cancel, null).create()
        vb.inputFilter.addTextChangedListener(object : TextWatcher {
            override fun beforeTextChanged(s: CharSequence?, a: Int, b2: Int, c: Int) {}
            override fun onTextChanged(s: CharSequence?, a: Int, b2: Int, c: Int) {}
            override fun afterTextChanged(s: Editable?) {
                val q = s?.toString()?.trim().orEmpty()
                shown = if (q.isEmpty()) channels else channels.filter { it.name?.contains(q, true) == true }
                adapter.clear(); adapter.addAll(shown.map { it.name ?: "" })
            }
        })
        vb.listChannels.setOnItemClickListener { _, _, pos, _ ->
            shown.getOrNull(pos)?.let { assign(i, it, save = true) }
            dialog.dismiss()
        }
        dialog.show()
    }

    private fun assign(i: Int, stream: Stream, save: Boolean) {
        val tile = tiles.getOrNull(i) ?: return
        tile.stream = stream
        tile.vb.txtTileHint.visibility = View.GONE
        tile.vb.txtTileName.visibility = View.VISIBLE
        if (save) prefs.multiviewSlots = List(4) { j -> if (j == i) (stream.id ?: "") else (tiles.getOrNull(j)?.stream?.id ?: prefs.multiviewSlots[j]) }
        select(selected)
        if (started) play(tile)
    }

    private fun remove(i: Int) {
        val tile = tiles.getOrNull(i) ?: return
        release(tile)
        tile.stream = null
        tile.vb.txtTileHint.text = getString(R.string.tile_empty)
        tile.vb.txtTileHint.visibility = View.VISIBLE
        tile.vb.txtTileName.visibility = View.GONE
        prefs.multiviewSlots = List(4) { j -> if (j == i) "" else prefs.multiviewSlots[j] }
    }

    private fun tileMenu(i: Int) {
        val tile = tiles.getOrNull(i) ?: return
        val hasStream = tile.stream != null
        val opts = if (hasStream) arrayOf(getString(R.string.full_screen), getString(R.string.change_channel), getString(R.string.remove))
        else arrayOf(getString(R.string.choose_channel))
        FocusDialog(this).setTitle(tile.stream?.name ?: getString(R.string.multiview))
            .setItems(opts) { _, which ->
                if (!hasStream) { pickChannel(i); return@setItems }
                when (which) {
                    0 -> tile.stream?.let { s ->
                        val url = try { XtreamApi.streamUrl(service, account, s, prefs.liveFormat) } catch (_: Exception) { return@setItems }
                        startActivity(PlayerActivity.intent(this, url, s.name ?: "", true))
                    }
                    1 -> pickChannel(i)
                    2 -> remove(i)
                }
            }
            .setNegativeButton(android.R.string.cancel, null).show()
    }

    // ---- Players ----------------------------------------------------------------

    private fun startAll() {
        started = true
        for (t in tiles) if (t.stream != null && t.player == null) play(t)
    }

    private fun stopAll() {
        started = false
        for (t in tiles) release(t)
        Thread { try { XtreamApi.client.connectionPool.evictAll() } catch (_: Exception) {} }.start()
    }

    private fun play(tile: Tile) {
        val s = tile.stream ?: return
        release(tile)
        val url = try { XtreamApi.streamUrl(service, account, s, prefs.liveFormat) } catch (e: Exception) {
            tile.vb.txtTileHint.text = e.message; tile.vb.txtTileHint.visibility = View.VISIBLE; return
        }
        val http = OkHttpDataSource.Factory(client).setUserAgent(XtreamApi.USER_AGENT)
            .setDefaultRequestProperties(mapOf("Connection" to "close"))
        // Small buffers: four of these must fit beside each other in memory.
        val load = DefaultLoadControl.Builder().setBufferDurationsMs(5_000, 20_000, 1_000, 1_000).build()
        val p = ExoPlayer.Builder(this)
            .setMediaSourceFactory(DefaultMediaSourceFactory(this).setDataSourceFactory(DefaultDataSource.Factory(this, http)))
            .setLoadControl(load)
            .setWakeMode(C.WAKE_MODE_NETWORK)
            .build()
        p.addListener(object : Player.Listener {
            override fun onPlayerError(error: PlaybackException) {
                AppLog.e("MultiView", "tile '${s.name}' error ${error.errorCodeName}", error)
                tile.vb.txtTileHint.text = getString(R.string.tile_error_fmt, error.errorCodeName)
                tile.vb.txtTileHint.visibility = View.VISIBLE
            }
            override fun onPlaybackStateChanged(playbackState: Int) {
                if (playbackState == Player.STATE_READY) tile.vb.txtTileHint.visibility = View.GONE
            }
        })
        p.setMediaItem(MediaItem.Builder().setUri(url)
            .setLiveConfiguration(MediaItem.LiveConfiguration.Builder().setMinPlaybackSpeed(1f).setMaxPlaybackSpeed(1f).build()).build())
        p.volume = if (tiles.indexOf(tile) == selected) 1f else 0f
        tile.vb.tilePlayer.player = p
        tile.player = p
        tile.vb.txtTileHint.visibility = View.GONE
        p.playWhenReady = true
        p.prepare()
    }

    private fun release(tile: Tile) {
        tile.vb.tilePlayer.player = null
        tile.player?.release()
        tile.player = null
    }

    companion object {
        private const val EXTRA_SERVICE = "service"
        private var cached: List<Stream>? = null
        private val client by lazy {
            XtreamApi.client.newBuilder()
                .connectTimeout(15, TimeUnit.SECONDS).readTimeout(20, TimeUnit.SECONDS)
                .callTimeout(0, TimeUnit.MILLISECONDS).build()
        }
        fun intent(ctx: Context, service: Service): Intent =
            Intent(ctx, MultiViewActivity::class.java).putExtra(EXTRA_SERVICE, service.name)
    }
}
