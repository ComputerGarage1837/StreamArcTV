package com.streamarc.tv.ui

import android.content.Context
import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.Rect
import android.graphics.RectF
import android.graphics.drawable.Drawable
import android.text.TextPaint
import android.text.TextUtils
import android.util.AttributeSet
import android.view.GestureDetector
import android.view.KeyEvent
import android.view.MotionEvent
import android.view.View
import com.bumptech.glide.Glide
import com.bumptech.glide.request.target.CustomTarget
import com.bumptech.glide.request.transition.Transition
import com.streamarc.tv.data.EpgProgramme
import com.streamarc.tv.data.Format
import com.streamarc.tv.data.Stream
import kotlin.math.max
import kotlin.math.min

/**
 * Classic TV-guide grid: channels down the left, a time ruler across the top,
 * programme blocks sized by duration, a "now" line, and D-pad navigation
 * (left/right between programmes, up/down between channels, OK to play,
 * long-press OK for the context menu). Everything is drawn on a canvas so it
 * stays smooth with hundreds of channels.
 */
class EpgGridView @JvmOverloads constructor(context: Context, attrs: AttributeSet? = null) : View(context, attrs) {

    interface Listener {
        fun onFocusChanged(channel: Stream, programme: EpgProgramme?)
        fun onChannelClick(channel: Stream)
        fun onChannelLongClick(channel: Stream)
        /** Called once per channel the first time it scrolls into view. */
        fun onNeedEpg(channel: Stream)
    }

    var listener: Listener? = null

    /** Returns the channel's programmes from a preloaded guide, or null to fall back to a fetch. */
    var guideLookup: ((Stream) -> List<EpgProgramme>?)? = null
    var favorites: Set<String> = emptySet()
        set(value) { field = value; invalidate() }

    private var channels: List<Stream> = emptyList()
    private val epg = HashMap<String, List<EpgProgramme>?>()   // null = requested, not loaded yet
    private val logos = HashMap<String, Bitmap?>()

    private val d = resources.displayMetrics.density
    private val channelColW = 170 * d
    private val rowH = 58 * d
    private val headerH = 32 * d
    private val pxPerMin = 3.4f * d
    private val gap = 2 * d
    private val corner = 6 * d

    private val windowStart: Long
    private val windowEnd: Long
    private var scrollX = 0f
    private var scrollY = 0f
    private var focusRow = 0
    private var focusTime: Long = System.currentTimeMillis() / 1000

    private val bgPaint = Paint().apply { color = 0xFF0A1628.toInt() }
    private val colPaint = Paint().apply { color = 0xFF0D1F3C.toInt() }
    private val headerPaint = Paint().apply { color = 0xFF0F2140.toInt() }
    private val linePaint = Paint().apply { color = 0xFF1E3A5F.toInt(); strokeWidth = 1 * d }
    private val nowPaint = Paint().apply { color = 0xFFA78BFA.toInt(); strokeWidth = 2 * d }
    private val cellPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = 0xFF2B3243.toInt() }
    private val cellNowPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = 0xFF3A4358.toInt() }
    private val cellPastPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = 0xFF222838.toInt() }
    private val cellFocusPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = 0xFFF8FAFC.toInt() }
    private val focusStroke = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = 0xFFA78BFA.toInt(); style = Paint.Style.STROKE; strokeWidth = 2.5f * d }
    private val rowFocusPaint = Paint().apply { color = 0x1AFFFFFF }
    private val titlePaint = TextPaint(Paint.ANTI_ALIAS_FLAG).apply { color = Color.WHITE; textSize = 14 * d }
    private val titleFocusPaint = TextPaint(Paint.ANTI_ALIAS_FLAG).apply { color = 0xFF0A1628.toInt(); textSize = 14 * d }
    private val subPaint = TextPaint(Paint.ANTI_ALIAS_FLAG).apply { color = 0xFF94A3B8.toInt(); textSize = 11 * d }
    private val subFocusPaint = TextPaint(Paint.ANTI_ALIAS_FLAG).apply { color = 0xFF475569.toInt(); textSize = 11 * d }
    private val markerPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = 0xFFA78BFA.toInt() }
    private val dateFmt = java.text.SimpleDateFormat("EEE MMM d", java.util.Locale.getDefault())
    private val channelPaint = TextPaint(Paint.ANTI_ALIAS_FLAG).apply { color = Color.WHITE; textSize = 13 * d }
    private val timePaint = TextPaint(Paint.ANTI_ALIAS_FLAG).apply { color = 0xFFCBD5E1.toInt(); textSize = 12 * d }
    private val placeholder: Drawable? = context.getDrawable(com.streamarc.tv.R.drawable.ic_placeholder)

    private val ticker = object : Runnable {
        override fun run() { invalidate(); postDelayed(this, 60_000) }
    }

    init {
        val now = System.currentTimeMillis() / 1000
        windowStart = (now / 1800) * 1800 - 1800          // half-hour boundary, one slot back
        windowEnd = windowStart + 24 * 3600
        isFocusable = true
        isClickable = true
    }

    // ---- Data ----------------------------------------------------------

    fun setChannels(list: List<Stream>) {
        channels = list
        focusRow = focusRow.coerceIn(0, max(0, list.size - 1))
        scrollY = 0f
        invalidate()
        if (list.isNotEmpty()) post { notifyFocus() }
    }

    fun setEpg(streamId: String, programmes: List<EpgProgramme>) {
        epg[streamId] = programmes
        invalidate()
        if (channels.getOrNull(focusRow)?.streamId == streamId) notifyFocus()
    }

    fun focusedChannel(): Stream? = channels.getOrNull(focusRow)

    // ---- Lifecycle -----------------------------------------------------

    override fun onAttachedToWindow() { super.onAttachedToWindow(); postDelayed(ticker, 60_000) }
    override fun onDetachedFromWindow() { removeCallbacks(ticker); super.onDetachedFromWindow() }

    override fun onFocusChanged(gainFocus: Boolean, direction: Int, previouslyFocusedRect: Rect?) {
        super.onFocusChanged(gainFocus, direction, previouslyFocusedRect)
        invalidate()
        if (gainFocus) notifyFocus()
    }

    // ---- Programme helpers ---------------------------------------------

    private fun programmesFor(ch: Stream): List<EpgProgramme> {
        val id = ch.streamId ?: return emptyList()
        if (!epg.containsKey(id)) {
            val bulk = guideLookup?.invoke(ch)
            if (bulk != null) {
                epg[id] = bulk
            } else {
                epg[id] = null
                listener?.onNeedEpg(ch)
            }
        }
        return epg[id] ?: emptyList()
    }

    /** Call after the full guide arrives: rows that were still loading resolve from it. */
    fun guideLoaded() {
        val pending = epg.filterValues { it == null || it.isEmpty() }.keys.toList()
        for (id in pending) epg.remove(id)
        invalidate()
        notifyFocus()
    }

    private fun visiblePlaceholder(ch: Stream): EpgProgramme {
        val loaded = ch.streamId?.let { epg[it] } != null
        return EpgProgramme(if (loaded) "No programme information" else "Loading…", "", windowStart, windowEnd)
    }

    /** The programme under the focus time on a row, or a placeholder covering the window. */
    private fun programmeAt(ch: Stream, time: Long): EpgProgramme {
        val list = programmesFor(ch)
        return list.firstOrNull { time >= it.start && time < it.end }
            ?: list.firstOrNull { it.start > time }?.let { next ->
                // Gap: synthesize an empty block up to the next programme.
                val prevEnd = list.lastOrNull { it.end <= time }?.end ?: windowStart
                EpgProgramme("", "", prevEnd, next.start)
            }
            ?: if (list.isEmpty()) visiblePlaceholder(ch)
            else EpgProgramme("", "", list.last().end, windowEnd)
    }

    private fun notifyFocus() {
        val ch = channels.getOrNull(focusRow) ?: return
        val p = programmeAt(ch, focusTime)
        listener?.onFocusChanged(ch, p.takeIf { it.title.isNotBlank() && it.title != "Loading…" && it.title != "No programme information" })
    }

    // ---- Geometry ------------------------------------------------------

    private fun xFor(time: Long): Float = channelColW + (time - windowStart) / 60f * pxPerMin - scrollX
    private fun maxScrollX(): Float = max(0f, (windowEnd - windowStart) / 60f * pxPerMin - (width - channelColW))
    private fun maxScrollY(): Float = max(0f, channels.size * rowH - (height - headerH))

    private fun ensureFocusVisible() {
        val ch = channels.getOrNull(focusRow) ?: return
        val p = programmeAt(ch, focusTime)
        val startX = (max(p.start, windowStart) - windowStart) / 60f * pxPerMin
        val endX = (min(p.end, windowEnd) - windowStart) / 60f * pxPerMin
        val viewW = width - channelColW
        if (startX < scrollX) scrollX = startX
        else if (min(endX, startX + viewW * 0.6f) > scrollX + viewW) scrollX = startX - viewW * 0.25f
        scrollX = scrollX.coerceIn(0f, maxScrollX())

        val top = focusRow * rowH
        val viewH = height - headerH
        if (top < scrollY) scrollY = top
        else if (top + rowH > scrollY + viewH) scrollY = top + rowH - viewH
        scrollY = scrollY.coerceIn(0f, maxScrollY())
    }

    // ---- Drawing -------------------------------------------------------

    override fun onDraw(c: Canvas) {
        c.drawRect(0f, 0f, width.toFloat(), height.toFloat(), bgPaint)
        val now = System.currentTimeMillis() / 1000
        val firstRow = max(0, (scrollY / rowH).toInt())
        val lastRow = min(channels.size - 1, ((scrollY + height - headerH) / rowH).toInt() + 1)

        // Programme area
        c.save()
        c.clipRect(channelColW, headerH, width.toFloat(), height.toFloat())
        for (row in firstRow..lastRow) {
            val ch = channels[row]
            val top = headerH + row * rowH - scrollY
            val focused = row == focusRow && hasFocus()
            if (focused) c.drawRect(channelColW, top, width.toFloat(), top + rowH, rowFocusPaint)
            val list = programmesFor(ch)
            val blocks = if (list.isEmpty()) listOf(visiblePlaceholder(ch)) else list
            for (p in blocks) {
                if (p.end <= windowStart || p.start >= windowEnd) continue
                val x1 = xFor(max(p.start, windowStart)) + gap / 2
                val x2 = xFor(min(p.end, windowEnd)) - gap / 2
                if (x2 < channelColW || x1 > width) continue
                val rect = RectF(x1, top + gap, x2, top + rowH - gap)
                val isFocusedCell = focused && focusTime >= p.start && focusTime < p.end
                val paint = when {
                    isFocusedCell -> cellFocusPaint
                    now >= p.start && now < p.end -> cellNowPaint
                    p.end <= now -> cellPastPaint
                    else -> cellPaint
                }
                c.drawRoundRect(rect, corner, corner, paint)
                if (isFocusedCell) c.drawRoundRect(rect, corner, corner, focusStroke)
                // Text is pinned to the visible left edge so long blocks stay readable.
                val textLeft = max(rect.left, channelColW) + 8 * d
                val avail = rect.right - textLeft - 6 * d
                if (avail > 20 * d) {
                    val tp = if (isFocusedCell) titleFocusPaint else titlePaint
                    val sp = if (isFocusedCell) subFocusPaint else subPaint
                    val title = TextUtils.ellipsize(p.title, tp, avail, TextUtils.TruncateAt.END)
                    c.drawText(title, 0, title.length, textLeft, top + rowH / 2 - 2 * d, tp)
                    if (p.title.isNotBlank() && p.end > p.start && p.end - p.start < 24 * 3600) {
                        val sub = TextUtils.ellipsize(Format.timeRange(p.start, p.end), sp, avail, TextUtils.TruncateAt.END)
                        c.drawText(sub, 0, sub.length, textLeft, top + rowH / 2 + 13 * d, sp)
                    }
                }
            }
        }
        // Now line
        if (now in windowStart..windowEnd) {
            val nx = xFor(now)
            c.drawLine(nx, headerH, nx, height.toFloat(), nowPaint)
        }
        c.restore()

        // Time header with the date on the left and a marker at "now"
        c.drawRect(0f, 0f, width.toFloat(), headerH, headerPaint)
        c.drawText(dateFmt.format(java.util.Date(now * 1000)), 12 * d, headerH - 10 * d, timePaint)
        c.save()
        c.clipRect(channelColW, 0f, width.toFloat(), headerH)
        var t = windowStart
        while (t <= windowEnd) {
            val x = xFor(t)
            if (x > channelColW - 40 * d && x < width) {
                c.drawLine(x, headerH - 8 * d, x, headerH, linePaint)
                c.drawText(Format.time(t), x + 6 * d, headerH - 10 * d, timePaint)
            }
            t += 1800
        }
        if (now in windowStart..windowEnd) {
            val nx = xFor(now)
            val path = android.graphics.Path().apply {
                moveTo(nx - 7 * d, headerH - 9 * d); lineTo(nx + 7 * d, headerH - 9 * d); lineTo(nx, headerH); close()
            }
            c.drawPath(path, markerPaint)
        }
        c.restore()

        // Channel column
        c.drawRect(0f, 0f, channelColW, height.toFloat(), colPaint)
        c.save()
        c.clipRect(0f, headerH, channelColW, height.toFloat())
        for (row in firstRow..lastRow) {
            val ch = channels[row]
            val top = headerH + row * rowH - scrollY
            if (row == focusRow && hasFocus()) c.drawRect(0f, top, channelColW, top + rowH, rowFocusPaint)
            c.drawLine(0f, top + rowH, channelColW, top + rowH, linePaint)
            val logoW = 44 * d; val logoH = 30 * d
            val lx = 8 * d; val ly = top + (rowH - logoH) / 2
            val bmp = logoFor(ch)
            if (bmp != null) {
                val scale = min(logoW / bmp.width, logoH / bmp.height)
                val w = bmp.width * scale; val h = bmp.height * scale
                c.drawBitmap(bmp, null, RectF(lx + (logoW - w) / 2, ly + (logoH - h) / 2, lx + (logoW - w) / 2 + w, ly + (logoH - h) / 2 + h), null)
            } else {
                placeholder?.setBounds(lx.toInt(), ly.toInt(), (lx + logoW).toInt(), (ly + logoH).toInt())
                placeholder?.draw(c)
            }
            val name = (if (favorites.contains(ch.streamId)) "★ " else "") + (ch.name ?: "")
            val nameAvail = channelColW - lx - logoW - 16 * d
            val txt = TextUtils.ellipsize(name, channelPaint, nameAvail, TextUtils.TruncateAt.END)
            c.drawText(txt, 0, txt.length, lx + logoW + 8 * d, top + rowH / 2 + 5 * d, channelPaint)
        }
        c.restore()
        c.drawLine(channelColW, 0f, channelColW, height.toFloat(), linePaint)
        c.drawLine(0f, headerH, width.toFloat(), headerH, linePaint)
    }

    private fun logoFor(ch: Stream): Bitmap? {
        val id = ch.streamId ?: return null
        if (logos.containsKey(id)) return logos[id]
        logos[id] = null
        val url = ch.icon?.takeIf { it.isNotBlank() } ?: return null
        Glide.with(context).asBitmap().load(url).override((88 * d).toInt(), (60 * d).toInt())
            .into(object : CustomTarget<Bitmap>() {
                override fun onResourceReady(resource: Bitmap, transition: Transition<in Bitmap>?) {
                    logos[id] = resource; invalidate()
                }
                override fun onLoadCleared(placeholder: Drawable?) {}
            })
        return null
    }

    // ---- D-pad ---------------------------------------------------------

    override fun onKeyDown(keyCode: Int, event: KeyEvent): Boolean {
        val ch = channels.getOrNull(focusRow) ?: return super.onKeyDown(keyCode, event)
        when (keyCode) {
            KeyEvent.KEYCODE_DPAD_RIGHT -> {
                val p = programmeAt(ch, focusTime)
                if (p.end >= windowEnd) return true
                focusTime = p.end
                moved(); return true
            }
            KeyEvent.KEYCODE_DPAD_LEFT -> {
                val p = programmeAt(ch, focusTime)
                val now = System.currentTimeMillis() / 1000
                if (p.start <= windowStart || p.start <= now && focusTime <= now) return false   // let focus leave to the category list
                focusTime = max(p.start - 1, windowStart)
                moved(); return true
            }
            KeyEvent.KEYCODE_DPAD_DOWN -> {
                if (focusRow >= channels.size - 1) return true
                focusRow++; moved(); return true
            }
            KeyEvent.KEYCODE_DPAD_UP -> {
                if (focusRow == 0) return false   // let focus leave to the top bar
                focusRow--; moved(); return true
            }
            KeyEvent.KEYCODE_DPAD_CENTER, KeyEvent.KEYCODE_ENTER, KeyEvent.KEYCODE_NUMPAD_ENTER -> {
                event.startTracking(); return true
            }
        }
        return super.onKeyDown(keyCode, event)
    }

    override fun onKeyLongPress(keyCode: Int, event: KeyEvent): Boolean {
        if (keyCode == KeyEvent.KEYCODE_DPAD_CENTER || keyCode == KeyEvent.KEYCODE_ENTER || keyCode == KeyEvent.KEYCODE_NUMPAD_ENTER) {
            channels.getOrNull(focusRow)?.let { listener?.onChannelLongClick(it) }
            return true
        }
        return super.onKeyLongPress(keyCode, event)
    }

    override fun onKeyUp(keyCode: Int, event: KeyEvent): Boolean {
        if (keyCode == KeyEvent.KEYCODE_DPAD_CENTER || keyCode == KeyEvent.KEYCODE_ENTER || keyCode == KeyEvent.KEYCODE_NUMPAD_ENTER) {
            if (!event.isCanceled && (event.flags and KeyEvent.FLAG_LONG_PRESS) == 0) {
                channels.getOrNull(focusRow)?.let { listener?.onChannelClick(it) }
            }
            return true
        }
        return super.onKeyUp(keyCode, event)
    }

    private fun moved() {
        ensureFocusVisible()
        invalidate()
        notifyFocus()
    }

    // ---- Touch ---------------------------------------------------------

    private val gestures = GestureDetector(context, object : GestureDetector.SimpleOnGestureListener() {
        override fun onDown(e: MotionEvent): Boolean = true
        override fun onScroll(e1: MotionEvent?, e2: MotionEvent, dx: Float, dy: Float): Boolean {
            scrollX = (scrollX + dx).coerceIn(0f, maxScrollX())
            scrollY = (scrollY + dy).coerceIn(0f, maxScrollY())
            invalidate(); return true
        }
        override fun onSingleTapUp(e: MotionEvent): Boolean {
            if (!focusAt(e.x, e.y)) return false
            requestFocus(); moved()
            channels.getOrNull(focusRow)?.let { listener?.onChannelClick(it) }
            return true
        }
        override fun onLongPress(e: MotionEvent) {
            if (!focusAt(e.x, e.y)) return
            requestFocus(); moved()
            channels.getOrNull(focusRow)?.let { listener?.onChannelLongClick(it) }
        }
    })

    private fun focusAt(x: Float, y: Float): Boolean {
        if (y < headerH) return false
        val row = ((y - headerH + scrollY) / rowH).toInt()
        if (row !in channels.indices) return false
        focusRow = row
        if (x > channelColW) {
            focusTime = (windowStart + ((x - channelColW + scrollX) / pxPerMin * 60).toLong()).coerceIn(windowStart, windowEnd - 1)
        }
        return true
    }

    override fun onTouchEvent(event: MotionEvent): Boolean = gestures.onTouchEvent(event) || super.onTouchEvent(event)
}
