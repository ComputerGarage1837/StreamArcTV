package com.streamarc.tv.ui

import android.app.Activity
import android.content.Context
import android.graphics.Canvas
import android.graphics.ColorFilter
import android.graphics.Paint
import android.graphics.PixelFormat
import android.graphics.Rect
import android.graphics.drawable.Drawable
import android.text.InputType
import android.view.Gravity
import android.view.View
import android.widget.EditText
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import android.widget.Toast
import androidx.appcompat.app.AlertDialog
import com.streamarc.tv.R
import com.streamarc.tv.data.VodUsers

/** A round coloured avatar with the user's initial. */
class AvatarDrawable(name: String, color: Int) : Drawable() {
    private val letter = (name.trim().firstOrNull()?.uppercaseChar() ?: '?').toString()
    private val fill = Paint(Paint.ANTI_ALIAS_FLAG).apply { this.color = color }
    private val text = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        this.color = 0xFFFFFFFF.toInt(); textAlign = Paint.Align.CENTER; isFakeBoldText = true
    }

    override fun draw(canvas: Canvas) {
        val r: Rect = bounds
        val size = minOf(r.width(), r.height()).toFloat()
        canvas.drawCircle(r.exactCenterX(), r.exactCenterY(), size / 2f, fill)
        text.textSize = size * 0.5f
        canvas.drawText(letter, r.exactCenterX(), r.exactCenterY() - (text.descent() + text.ascent()) / 2f, text)
    }
    override fun setAlpha(alpha: Int) { fill.alpha = alpha }
    override fun setColorFilter(cf: ColorFilter?) { fill.colorFilter = cf }
    @Deprecated("Deprecated in Java") override fun getOpacity() = PixelFormat.TRANSLUCENT
}

/** The "Who's watching?" screens: pick, add, edit and remove VOD users. */
object UserDialogs {

    /** Settles who is watching when VOD opens: asks for a first user if there is none, offers the choice once per launch if there are several. */
    fun ensure(activity: Activity, onChanged: () -> Unit) {
        if (!VodUsers.hasUsers()) {
            create(activity, first = true) { VodUsers.chosenThisLaunch = true; onChanged() }
        } else if (VodUsers.all().size > 1 && !VodUsers.chosenThisLaunch) {
            VodUsers.chosenThisLaunch = true
            choose(activity, onChanged)
        } else VodUsers.chosenThisLaunch = true
    }

    fun avatar(name: String, color: Int): Drawable = AvatarDrawable(name, color)

    private fun dp(c: Context, v: Int) = (v * c.resources.displayMetrics.density).toInt()

    private fun row(ctx: Context, name: String, color: Int, checked: Boolean, label: String? = null, plus: Boolean = false): LinearLayout {
        val row = LinearLayout(ctx).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            setBackgroundResource(R.drawable.bg_row)
            isFocusable = true; isClickable = true
            setPadding(dp(ctx, 14), dp(ctx, 10), dp(ctx, 14), dp(ctx, 10))
            layoutParams = LinearLayout.LayoutParams(-1, -2).apply { bottomMargin = dp(ctx, 6) }
        }
        val icon = ImageView(ctx).apply { layoutParams = LinearLayout.LayoutParams(dp(ctx, 44), dp(ctx, 44)) }
        if (plus) {
            icon.setImageDrawable(AvatarDrawable("+", 0xFF475569.toInt()))
        } else icon.setImageDrawable(AvatarDrawable(name, color))
        val tv = TextView(ctx).apply {
            text = label ?: name
            setTextColor(ctx.getColor(R.color.text)); textSize = 17f
            layoutParams = LinearLayout.LayoutParams(0, -2, 1f).apply { marginStart = dp(ctx, 14) }
        }
        row.addView(icon); row.addView(tv)
        if (checked) row.addView(TextView(ctx).apply { text = "✓"; setTextColor(ctx.getColor(R.color.accent)); textSize = 18f })
        return row
    }

    private fun listDialog(ctx: Context, title: Int, fill: (LinearLayout, AlertDialog) -> Unit, cancelable: Boolean = true): AlertDialog {
        val col = LinearLayout(ctx).apply { orientation = LinearLayout.VERTICAL; setPadding(dp(ctx, 16), dp(ctx, 8), dp(ctx, 16), dp(ctx, 8)) }
        val dialog = AlertDialog.Builder(ctx).setTitle(title).setView(ScrollView(ctx).apply { addView(col) }).setCancelable(cancelable).create()
        fill(col, dialog)
        return dialog
    }

    /** Pick who is watching. */
    fun choose(activity: Activity, onChanged: () -> Unit) {
        val cur = VodUsers.current()
        val d = listDialog(activity, R.string.whos_watching, { col, dlg ->
            var first: View? = null
            for (u in VodUsers.all()) {
                val r = row(activity, u.name, u.color, u.id == cur?.id)
                r.setOnClickListener {
                    if (u.id != cur?.id) {
                        VodUsers.setActive(u.id)
                        Toast.makeText(activity, activity.getString(R.string.user_switched_fmt, u.name), Toast.LENGTH_SHORT).show()
                        onChanged()
                    }
                    dlg.dismiss()
                }
                col.addView(r)
                if (first == null || u.id == cur?.id) first = r
            }
            val add = row(activity, "+", 0, false, activity.getString(R.string.add_user), plus = true)
            add.setOnClickListener { dlg.dismiss(); create(activity, first = false) { onChanged() } }
            col.addView(add)
            val manage = row(activity, "", 0, false, activity.getString(R.string.manage_users), plus = true)
            (manage.getChildAt(0) as ImageView).setImageDrawable(AvatarDrawable("⚙", 0xFF475569.toInt()))
            manage.setOnClickListener { dlg.dismiss(); manage(activity, onChanged) }
            col.addView(manage)
            col.post { first?.requestFocus() }
        })
        d.show()
    }

    /** Add a user (or, with [edit], rename / recolour one). */
    fun create(activity: Activity, first: Boolean, edit: VodUsers.User? = null, onDone: () -> Unit) {
        val ctx = activity
        var color = edit?.color ?: VodUsers.COLORS[VodUsers.all().size % VodUsers.COLORS.size]
        val col = LinearLayout(ctx).apply { orientation = LinearLayout.VERTICAL; setPadding(dp(ctx, 20), dp(ctx, 8), dp(ctx, 20), dp(ctx, 4)) }
        if (first) col.addView(TextView(ctx).apply {
            setText(R.string.new_user_first_msg); setTextColor(ctx.getColor(R.color.text_muted)); textSize = 14f
            setPadding(0, 0, 0, dp(ctx, 12))
        })
        val preview = ImageView(ctx).apply { layoutParams = LinearLayout.LayoutParams(dp(ctx, 72), dp(ctx, 72)).apply { gravity = Gravity.CENTER_HORIZONTAL; bottomMargin = dp(ctx, 10) } }
        val input = EditText(ctx).apply {
            setHint(R.string.user_name_hint); setSingleLine(); inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_FLAG_CAP_WORDS
            setTextColor(ctx.getColor(R.color.text)); setHintTextColor(ctx.getColor(R.color.text_muted))
            setText(edit?.name ?: ""); setSelection(text.length)
            filters = arrayOf(android.text.InputFilter.LengthFilter(20))
        }
        fun refresh() { preview.setImageDrawable(AvatarDrawable(input.text.toString(), color)) }
        input.addTextChangedListener(object : android.text.TextWatcher {
            override fun afterTextChanged(s: android.text.Editable?) = refresh()
            override fun beforeTextChanged(s: CharSequence?, a: Int, b: Int, c: Int) {}
            override fun onTextChanged(s: CharSequence?, a: Int, b: Int, c: Int) {}
        })
        val dots = LinearLayout(ctx).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER; setPadding(0, dp(ctx, 12), 0, 0) }
        val dotViews = ArrayList<ImageView>()
        for (c in VodUsers.COLORS) {
            val v = ImageView(ctx).apply {
                layoutParams = LinearLayout.LayoutParams(dp(ctx, 30), dp(ctx, 30)).apply { marginStart = dp(ctx, 5); marginEnd = dp(ctx, 5) }
                setImageDrawable(AvatarDrawable(" ", c)); isFocusable = true; isClickable = true
                setBackgroundResource(R.drawable.fg_focus_circle)
                setPadding(dp(ctx, 2), dp(ctx, 2), dp(ctx, 2), dp(ctx, 2))
                alpha = if (c == color) 1f else 0.45f
                setOnClickListener { color = c; dotViews.forEach { d -> d.alpha = 0.45f }; alpha = 1f; refresh() }
            }
            dotViews.add(v); dots.addView(v)
        }
        col.addView(preview); col.addView(input); col.addView(dots)
        refresh()
        val b = AlertDialog.Builder(ctx)
            .setTitle(if (edit != null) R.string.user_rename else R.string.new_user_title)
            .setView(col)
            .setCancelable(!first)
            .setPositiveButton(if (edit != null) R.string.user_save else R.string.user_create, null)
        if (!first) b.setNegativeButton(android.R.string.cancel, null)
        val dlg = b.create()
        dlg.setOnShowListener {
            input.requestFocus()
            dlg.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener {
                val name = input.text.toString().trim()
                when {
                    name.isEmpty() -> input.error = ctx.getString(R.string.user_name_needed)
                    VodUsers.nameTaken(name, edit?.id) -> input.error = ctx.getString(R.string.user_name_taken)
                    else -> {
                        if (edit != null) VodUsers.update(edit.id, name, color)
                        else { val u = VodUsers.add(name, color); VodUsers.setActive(u.id) }
                        dlg.dismiss(); onDone()
                    }
                }
            }
        }
        dlg.show()
    }

    /** Edit or remove users. */
    fun manage(activity: Activity, onChanged: () -> Unit) {
        val d = listDialog(activity, R.string.manage_users, { col, dlg ->
            for (u in VodUsers.all()) {
                val r = row(activity, u.name, u.color, false)
                r.setOnClickListener {
                    dlg.dismiss()
                    val opts = arrayOf<CharSequence>(activity.getString(R.string.user_rename), activity.getString(R.string.user_delete))
                    FocusDialog(activity).setTitle(u.name).setItems(opts) { _, which ->
                        if (which == 0) create(activity, first = false, edit = u) { onChanged(); manage(activity, onChanged) }
                        else confirmDelete(activity, u, onChanged)
                    }.show()
                }
                col.addView(r)
            }
            col.post { col.getChildAt(0)?.requestFocus() }
        })
        d.show()
    }

    private fun confirmDelete(activity: Activity, u: VodUsers.User, onChanged: () -> Unit) {
        if (VodUsers.all().size <= 1) { Toast.makeText(activity, R.string.user_last_cannot_delete, Toast.LENGTH_LONG).show(); return }
        AlertDialog.Builder(activity)
            .setTitle(R.string.user_delete)
            .setMessage(activity.getString(R.string.user_delete_confirm_fmt, u.name))
            .setPositiveButton(R.string.user_delete) { _, _ -> VodUsers.remove(u.id); onChanged() }
            .setNegativeButton(android.R.string.cancel, null)
            .show()
    }
}
