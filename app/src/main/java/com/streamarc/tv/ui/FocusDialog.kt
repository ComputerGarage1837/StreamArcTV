package com.streamarc.tv.ui

import android.content.Context
import android.content.DialogInterface
import android.view.LayoutInflater
import android.view.View
import android.widget.LinearLayout
import android.widget.ScrollView
import androidx.appcompat.app.AlertDialog
import com.streamarc.tv.databinding.ItemChoiceBinding

/**
 * Drop-in AlertDialog.Builder whose item / single-choice / multi-choice lists are made of the
 * app's own focusable rows, so a remote's focus is always visible (the stock dialog list
 * draws no usable highlight on TV).
 */
class FocusDialog(context: Context) : AlertDialog.Builder(context) {

    private var items: Array<out CharSequence>? = null
    private var mode = 0                      // 1 items, 2 single, 3 multi
    private var checkedIndex = -1
    private var checkedFlags: BooleanArray? = null
    private var clickListener: DialogInterface.OnClickListener? = null
    private var multiListener: DialogInterface.OnMultiChoiceClickListener? = null

    override fun setItems(items: Array<out CharSequence>?, listener: DialogInterface.OnClickListener?): AlertDialog.Builder {
        this.items = items; mode = 1; clickListener = listener
        return this
    }

    override fun setSingleChoiceItems(items: Array<out CharSequence>?, checkedItem: Int, listener: DialogInterface.OnClickListener?): AlertDialog.Builder {
        this.items = items; mode = 2; checkedIndex = checkedItem; clickListener = listener
        return this
    }

    override fun setMultiChoiceItems(items: Array<out CharSequence>?, checkedItems: BooleanArray?, listener: DialogInterface.OnMultiChoiceClickListener?): AlertDialog.Builder {
        this.items = items; mode = 3
        checkedFlags = checkedItems ?: BooleanArray(items?.size ?: 0)
        multiListener = listener
        return this
    }

    override fun create(): AlertDialog {
        val list = items
        if (list == null || mode == 0) return super.create()
        val ctx = context
        val d = ctx.resources.displayMetrics.density
        val column = LinearLayout(ctx).apply {
            orientation = LinearLayout.VERTICAL
            setPadding((12 * d).toInt(), (6 * d).toInt(), (12 * d).toInt(), (6 * d).toInt())
        }
        val scroll = ScrollView(ctx).apply { addView(column); isFillViewport = false }
        setView(scroll)
        val dialog = super.create()
        val rows = ArrayList<ItemChoiceBinding>()
        for ((i, label) in list.withIndex()) {
            val vb = ItemChoiceBinding.inflate(LayoutInflater.from(ctx), column, true)
            vb.txtLabel.text = label
            when (mode) {
                2 -> { vb.radio.visibility = View.VISIBLE; vb.radio.isChecked = i == checkedIndex }
                3 -> { vb.check.visibility = View.VISIBLE; vb.check.isChecked = checkedFlags?.getOrNull(i) == true }
            }
            vb.root.setOnClickListener {
                when (mode) {
                    1 -> { clickListener?.onClick(dialog, i); dialog.dismiss() }
                    2 -> {
                        checkedIndex = i
                        for ((j, r) in rows.withIndex()) r.radio.isChecked = j == i
                        clickListener?.onClick(dialog, i)
                    }
                    3 -> {
                        val flags = checkedFlags ?: return@setOnClickListener
                        flags[i] = !flags[i]
                        vb.check.isChecked = flags[i]
                        multiListener?.onClick(dialog, i, flags[i])
                    }
                }
            }
            rows.add(vb)
        }
        // Land the remote on the current choice (or the first row) as soon as the dialog opens.
        dialog.setOnShowListener {
            val start = if (mode == 2 && checkedIndex in rows.indices) checkedIndex else 0
            rows.getOrNull(start)?.root?.requestFocus()
        }
        return dialog
    }
}
