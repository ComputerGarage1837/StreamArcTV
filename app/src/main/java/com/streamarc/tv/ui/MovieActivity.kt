package com.streamarc.tv.ui

import android.content.Context
import android.content.Intent
import android.os.Bundle
import android.view.View
import android.widget.Toast
import androidx.appcompat.app.AppCompatActivity
import androidx.lifecycle.lifecycleScope
import com.bumptech.glide.Glide
import com.streamarc.tv.R
import com.streamarc.tv.data.ContentKind
import com.streamarc.tv.data.Details
import com.streamarc.tv.data.Prefs
import com.streamarc.tv.data.Service
import com.streamarc.tv.data.Stream
import com.streamarc.tv.data.WatchProgress
import com.streamarc.tv.data.XtreamApi
import com.streamarc.tv.databinding.ActivityMovieBinding
import kotlinx.coroutines.launch

/** One movie: poster, facts, plot, cast, trailer, and the Play / Resume / Start over buttons. */
class MovieActivity : AppCompatActivity() {

    private lateinit var b: ActivityMovieBinding
    private lateinit var prefs: Prefs
    private lateinit var service: Service
    private lateinit var id: String
    private var name = ""
    private var image: String? = null
    private var ext: String? = null
    private var details: Details? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        b = ActivityMovieBinding.inflate(layoutInflater)
        setContentView(b.root)
        Edge.pad(b.root)
        prefs = Prefs(this)
        service = Service.valueOf(intent.getStringExtra(EXTRA_SERVICE) ?: Service.VOD.name)
        id = intent.getStringExtra(EXTRA_ID) ?: run { finish(); return }
        val account = prefs.account(service) ?: run { finish(); return }
        name = intent.getStringExtra(EXTRA_NAME) ?: ""
        image = intent.getStringExtra(EXTRA_IMAGE)
        ext = intent.getStringExtra(EXTRA_EXT)

        b.txtTitle.text = name
        bindFacts(
            genre = intent.getStringExtra(EXTRA_GENRE), rating = intent.getStringExtra(EXTRA_RATING),
            year = null, duration = null
        )
        b.txtPlot.text = intent.getStringExtra(EXTRA_PLOT).orEmpty()
        b.txtCast.visibility = View.GONE
        b.txtDirector.visibility = View.GONE
        showPoster(image)

        b.btnBack.setOnClickListener { finish() }
        b.btnPlay.setOnClickListener { play(resume = true) }
        b.btnStartOver.setOnClickListener { play(resume = false) }
        b.btnTrailer.setOnClickListener { openTrailer(this, details?.trailerUrl) }
        b.btnList.setOnClickListener { prefs.toggleFavorite(service, ContentKind.MOVIE, id); renderButtons() }
        b.btnWatched.setOnClickListener {
            val key = WatchProgress.movieKey(service, id)
            WatchProgress.describe(key, WatchProgress.KIND_MOVIE, name, image, ext, id)
            WatchProgress.setWatched(key, !WatchProgress.isWatched(key))
            renderButtons()
        }
        b.btnPlay.requestFocus()
        val extras = ExtrasView(this)
        b.scrollContent.addView(extras)

        // The provider's own cast, rating and trailer, when it has them.
        lifecycleScope.launch {
            val d = try { XtreamApi.vodInfo(service, account, id) } catch (_: Exception) { null } ?: return@launch
            details = d
            if (!d.plot.isNullOrBlank()) b.txtPlot.text = d.plot
            bindFacts(d.genre ?: intent.getStringExtra(EXTRA_GENRE), d.rating ?: intent.getStringExtra(EXTRA_RATING), d.year, d.duration)
            d.cast?.let { b.txtCast.text = getString(R.string.cast_fmt, it); b.txtCast.visibility = View.VISIBLE }
            d.director?.let { b.txtDirector.text = getString(R.string.director_fmt, it); b.txtDirector.visibility = View.VISIBLE }
            if (image.isNullOrBlank()) showPoster(d.poster)
            renderButtons()
        }
        loadExtras(extras, account)
    }

    /** Actor photos and similar titles (from TMDB), matched to what this provider actually has. */
    private fun loadExtras(extras: ExtrasView, account: com.streamarc.tv.data.Account) {
        lifecycleScope.launch {
            val info = com.streamarc.tv.data.Tmdb.lookup(name, null, series = false)
            extras.showCast(info.cast)
            val catalogue = try { com.streamarc.tv.data.CatalogCache.getStored(this@MovieActivity, service, ContentKind.MOVIE) } catch (_: Exception) { emptyList() }
            val similar = com.streamarc.tv.data.Tmdb.similarIn(info, catalogue, id, name)
            extras.showSimilar(similar) { s -> startActivity(MovieActivity.intent(this@MovieActivity, service, s)) }
        }
    }

    override fun onResume() {
        super.onResume()
        renderButtons()
    }

    private fun bindFacts(genre: String?, rating: String?, year: String?, duration: String?) {
        val parts = ArrayList<String>()
        year?.let { parts.add(it) }
        rating?.toDoubleOrNull()?.takeIf { it > 0 }?.let { parts.add("★ " + String.format("%.1f", it)) }
        duration?.takeIf { it.isNotBlank() }?.let { parts.add(it) }
        genre?.takeIf { it.isNotBlank() }?.let { parts.add(it) }
        b.txtFacts.text = parts.joinToString("  ·  ")
        b.txtFacts.visibility = if (parts.isEmpty()) View.GONE else View.VISIBLE
    }

    private fun showPoster(url: String?) {
        Glide.with(b.imgPoster).load(url?.takeIf { it.isNotBlank() })
            .placeholder(R.drawable.ic_placeholder).error(R.drawable.ic_placeholder).centerCrop().into(b.imgPoster)
    }

    private fun renderButtons() {
        val key = WatchProgress.movieKey(service, id)
        val pos = WatchProgress.resumePosition(key)
        b.btnPlay.text = if (pos > 0) getString(R.string.play_from_fmt, clock(pos)) else getString(R.string.play)
        b.btnStartOver.visibility = if (pos > 0) View.VISIBLE else View.GONE
        b.btnTrailer.visibility = if (details?.trailerUrl != null) View.VISIBLE else View.GONE
        val fav = prefs.isFavorite(service, ContentKind.MOVIE, id)
        b.btnList.setText(if (fav) R.string.remove_from_favorites else R.string.add_to_favorites)
        b.btnWatched.setText(if (WatchProgress.isWatched(key)) R.string.mark_unwatched else R.string.mark_watched)
    }

    private fun play(resume: Boolean) {
        val account = prefs.account(service) ?: return
        val url = try { XtreamApi.movieUrl(service, account, id, ext) } catch (e: Exception) {
            Toast.makeText(this, e.message, Toast.LENGTH_LONG).show(); return
        }
        val key = WatchProgress.movieKey(service, id)
        if (!resume) WatchProgress.remove(key)
        WatchProgress.describe(key, WatchProgress.KIND_MOVIE, name, image, ext, id)
        startActivity(PlayerActivity.intent(this, url, name, false, key))
    }

    private fun clock(ms: Long): String {
        val s = ms / 1000
        return if (s >= 3600) "%d:%02d:%02d".format(s / 3600, (s % 3600) / 60, s % 60) else "%d:%02d".format(s / 60, s % 60)
    }

    companion object {
        private const val EXTRA_SERVICE = "service"
        private const val EXTRA_ID = "id"
        private const val EXTRA_NAME = "name"
        private const val EXTRA_IMAGE = "image"
        private const val EXTRA_EXT = "ext"
        private const val EXTRA_PLOT = "plot"
        private const val EXTRA_GENRE = "genre"
        private const val EXTRA_RATING = "rating"

        fun intent(ctx: Context, service: Service, s: Stream): Intent =
            Intent(ctx, MovieActivity::class.java)
                .putExtra(EXTRA_SERVICE, service.name)
                .putExtra(EXTRA_ID, s.streamId ?: s.id)
                .putExtra(EXTRA_NAME, s.name)
                .putExtra(EXTRA_IMAGE, s.image)
                .putExtra(EXTRA_EXT, s.containerExtension)
                .putExtra(EXTRA_PLOT, s.plot)
                .putExtra(EXTRA_GENRE, s.genre)
                .putExtra(EXTRA_RATING, s.rating)
    }
}
