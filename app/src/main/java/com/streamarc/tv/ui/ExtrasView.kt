package com.streamarc.tv.ui

import android.content.Context
import android.view.Gravity
import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.TextView
import androidx.recyclerview.widget.LinearLayoutManager
import androidx.recyclerview.widget.RecyclerView
import com.bumptech.glide.Glide
import com.bumptech.glide.load.resource.bitmap.CircleCrop
import com.streamarc.tv.R
import com.streamarc.tv.data.Stream
import com.streamarc.tv.data.Tmdb
import com.streamarc.tv.databinding.ItemVodCardBinding

/** "Cast" (round photos with names) and "Similar" (poster cards) rows shown below a movie or show. */
class ExtrasView(context: Context) : LinearLayout(context) {
    private val castTitle = header(context.getString(R.string.cast_heading))
    private val castList = row(context)
    private val similarTitle = header(context.getString(R.string.similar_heading))
    private val similarList = row(context)
    private val castAdapter = CastAdapter()
    private val similarAdapter = SimilarAdapter()

    init {
        orientation = VERTICAL
        layoutParams = ViewGroup.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT)
        castList.adapter = castAdapter
        similarList.adapter = similarAdapter
        addView(castTitle); addView(castList); addView(similarTitle); addView(similarList)
        visibility = GONE
        castTitle.visibility = GONE; castList.visibility = GONE; similarTitle.visibility = GONE; similarList.visibility = GONE
    }

    private fun dp(v: Int) = (v * resources.displayMetrics.density).toInt()

    private fun header(text: String) = TextView(context).apply {
        this.text = text
        setTextColor(context.getColor(R.color.text)); textSize = 17f
        typeface = android.graphics.Typeface.DEFAULT_BOLD
        setPadding(dp(16), dp(18), dp(16), dp(6))
    }

    private fun row(ctx: Context) = RecyclerView(ctx).apply {
        layoutManager = LinearLayoutManager(ctx, LinearLayoutManager.HORIZONTAL, false)
        clipToPadding = false
        setPadding(dp(12), 0, dp(12), dp(8))
        isNestedScrollingEnabled = false
        overScrollMode = OVER_SCROLL_NEVER
    }

    fun showCast(cast: List<Tmdb.Cast>) {
        if (cast.isEmpty()) return
        castAdapter.items = cast
        castAdapter.notifyDataSetChanged()
        visibility = VISIBLE; castTitle.visibility = VISIBLE; castList.visibility = VISIBLE
    }

    fun showSimilar(list: List<Stream>, onClick: (Stream) -> Unit) {
        if (list.isEmpty()) return
        similarAdapter.items = list
        similarAdapter.onClick = onClick
        similarAdapter.notifyDataSetChanged()
        visibility = VISIBLE; similarTitle.visibility = VISIBLE; similarList.visibility = VISIBLE
    }

    private inner class CastAdapter : RecyclerView.Adapter<RecyclerView.ViewHolder>() {
        var items: List<Tmdb.Cast> = emptyList()
        override fun getItemCount() = items.size
        override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): RecyclerView.ViewHolder {
            val col = LinearLayout(parent.context).apply {
                orientation = VERTICAL; gravity = Gravity.CENTER_HORIZONTAL
                layoutParams = RecyclerView.LayoutParams(dp(92), ViewGroup.LayoutParams.WRAP_CONTENT).apply { setMargins(dp(4), dp(2), dp(4), dp(2)) }
                setPadding(dp(4), dp(6), dp(4), dp(6))
                setBackgroundResource(R.drawable.bg_stream_item)
                isFocusable = true; isClickable = true
            }
            col.addView(ImageView(parent.context).apply { id = R.id.castPhoto; layoutParams = LayoutParams(dp(72), dp(72)); scaleType = ImageView.ScaleType.CENTER_CROP })
            col.addView(TextView(parent.context).apply {
                id = R.id.castName; gravity = Gravity.CENTER; maxLines = 2; ellipsize = android.text.TextUtils.TruncateAt.END
                setTextColor(context.getColor(R.color.text)); textSize = 12f; setPadding(0, dp(4), 0, 0)
            })
            col.addView(TextView(parent.context).apply {
                id = R.id.castRole; gravity = Gravity.CENTER; maxLines = 2; ellipsize = android.text.TextUtils.TruncateAt.END
                setTextColor(context.getColor(R.color.text_muted)); textSize = 11f
            })
            return object : RecyclerView.ViewHolder(col) {}
        }
        override fun onBindViewHolder(holder: RecyclerView.ViewHolder, position: Int) {
            val c = items[position]
            val v = holder.itemView
            v.findViewById<TextView>(R.id.castName).text = c.name
            v.findViewById<TextView>(R.id.castRole).text = c.character.orEmpty()
            val img = v.findViewById<ImageView>(R.id.castPhoto)
            if (c.photo != null) {
                Glide.with(img).load(c.photo).transform(CircleCrop()).placeholder(R.drawable.ic_placeholder).error(R.drawable.ic_placeholder).into(img)
            } else {
                Glide.with(img).clear(img)
                img.setImageDrawable(AvatarDrawable(c.name, 0xFF475569.toInt()))
            }
        }
    }

    private inner class SimilarAdapter : RecyclerView.Adapter<RecyclerView.ViewHolder>() {
        var items: List<Stream> = emptyList()
        var onClick: (Stream) -> Unit = {}
        override fun getItemCount() = items.size
        override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): RecyclerView.ViewHolder =
            object : RecyclerView.ViewHolder(ItemVodCardBinding.inflate(LayoutInflater.from(parent.context), parent, false).root) {}
        override fun onBindViewHolder(holder: RecyclerView.ViewHolder, position: Int) {
            val s = items[position]
            val vb = ItemVodCardBinding.bind(holder.itemView)
            vb.txtTitle.text = s.name.orEmpty()
            vb.txtSub.text = s.rating?.toDoubleOrNull()?.takeIf { it > 0 }?.let { "★ " + String.format("%.1f", it) }.orEmpty()
            vb.txtBadge.visibility = View.GONE
            vb.progressWatch.visibility = View.GONE
            Glide.with(vb.imgPoster).load(s.image).placeholder(R.drawable.ic_placeholder).error(R.drawable.ic_placeholder).centerCrop().into(vb.imgPoster)
            vb.root.setOnClickListener { onClick(s) }
        }
    }
}
