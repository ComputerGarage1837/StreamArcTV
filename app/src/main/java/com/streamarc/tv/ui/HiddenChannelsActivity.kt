package com.streamarc.tv.ui

import android.content.Context
import android.content.Intent
import android.os.Bundle
import android.text.Editable
import android.text.TextWatcher
import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import android.widget.Toast
import androidx.appcompat.app.AppCompatActivity
import androidx.lifecycle.lifecycleScope
import androidx.recyclerview.widget.LinearLayoutManager
import androidx.recyclerview.widget.RecyclerView
import com.bumptech.glide.Glide
import com.streamarc.tv.R
import com.streamarc.tv.data.Account
import com.streamarc.tv.data.Category
import com.streamarc.tv.data.ContentKind
import com.streamarc.tv.data.Prefs
import com.streamarc.tv.data.Service
import com.streamarc.tv.data.Stream
import com.streamarc.tv.data.XtreamApi
import com.streamarc.tv.databinding.ActivityHiddenChannelsBinding
import com.streamarc.tv.databinding.ItemCategoryChipBinding
import com.streamarc.tv.databinding.ItemChannelToggleBinding
import kotlinx.coroutines.launch

/**
 * Settings → Channels shown: pick a category (only the ones you have enabled are listed) and
 * untick the channels you never want to see. Hidden channels disappear from the guide, the
 * lists and the multi-view picker.
 */
class HiddenChannelsActivity : AppCompatActivity() {

    private lateinit var b: ActivityHiddenChannelsBinding
    private lateinit var prefs: Prefs
    private lateinit var account: Account
    private val service = Service.LIVE

    private var categories: List<Category> = emptyList()
    private var selected: Category? = null
    private var channels: List<Stream> = emptyList()
    private val cache = HashMap<String, List<Stream>>()

    private val catAdapter = ChipAdapter { select(it) }
    private val chanAdapter = ChannelAdapter()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        b = ActivityHiddenChannelsBinding.inflate(layoutInflater)
        setContentView(b.root)
        Edge.pad(b.root)
        prefs = Prefs(this)
        account = prefs.account(service) ?: run {
            Toast.makeText(this, R.string.sign_in_live_first, Toast.LENGTH_SHORT).show(); finish(); return
        }
        b.btnBack.setOnClickListener { finish() }
        b.btnShowAll.setOnClickListener {
            prefs.hiddenLiveChannels = emptySet()
            chanAdapter.notifyDataSetChanged()
            renderCount()
            Toast.makeText(this, R.string.all_channels_shown, Toast.LENGTH_SHORT).show()
        }
        b.listCategories.layoutManager = LinearLayoutManager(this, LinearLayoutManager.HORIZONTAL, false)
        b.listCategories.adapter = catAdapter
        b.listChannels.layoutManager = LinearLayoutManager(this)
        b.listChannels.adapter = chanAdapter
        b.inputFilter.addTextChangedListener(object : TextWatcher {
            override fun beforeTextChanged(s: CharSequence?, a: Int, b2: Int, c: Int) {}
            override fun onTextChanged(s: CharSequence?, a: Int, b2: Int, c: Int) {}
            override fun afterTextChanged(s: Editable?) { applyFilter() }
        })
        renderCount()
        loadCategories()
    }

    private fun renderCount() {
        val n = prefs.hiddenLiveChannels.size
        b.txtHiddenCount.text = if (n == 0) "" else getString(R.string.hidden_count_fmt, n)
        b.btnShowAll.visibility = if (n == 0) View.GONE else View.VISIBLE
    }

    private fun loadCategories() {
        b.progress.visibility = View.VISIBLE
        lifecycleScope.launch {
            try {
                val hiddenCats = prefs.hiddenLiveCategories
                categories = XtreamApi.categories(service, account, ContentKind.LIVE).filter { it.id != null && it.id !in hiddenCats }
                catAdapter.submit(categories)
                categories.firstOrNull()?.let { select(it) } ?: run { b.progress.visibility = View.GONE }
            } catch (e: Exception) {
                b.progress.visibility = View.GONE
                Toast.makeText(this@HiddenChannelsActivity, e.message ?: getString(R.string.load_failed), Toast.LENGTH_LONG).show()
            }
        }
    }

    private fun select(cat: Category) {
        selected = cat
        catAdapter.selectedId = cat.id
        val id = cat.id ?: return
        cache[id]?.let { channels = it; applyFilter(); return }
        b.progress.visibility = View.VISIBLE
        lifecycleScope.launch {
            try {
                val list = XtreamApi.streams(service, account, id, ContentKind.LIVE)
                cache[id] = list
                if (selected?.id == id) { channels = list; applyFilter() }
            } catch (e: Exception) {
                Toast.makeText(this@HiddenChannelsActivity, e.message ?: getString(R.string.load_failed), Toast.LENGTH_LONG).show()
            } finally {
                b.progress.visibility = View.GONE
            }
        }
    }

    private fun applyFilter() {
        val q = b.inputFilter.text?.toString()?.trim().orEmpty()
        chanAdapter.submit(if (q.isEmpty()) channels else channels.filter { it.name?.contains(q, true) == true })
        b.progress.visibility = View.GONE
    }

    private inner class ChipAdapter(val onClick: (Category) -> Unit) : RecyclerView.Adapter<ChipAdapter.VH>() {
        private var items: List<Category> = emptyList()
        var selectedId: String? = null
            set(value) { field = value; notifyDataSetChanged() }
        inner class VH(val vb: ItemCategoryChipBinding) : RecyclerView.ViewHolder(vb.root)
        fun submit(list: List<Category>) { items = list; notifyDataSetChanged() }
        override fun onCreateViewHolder(parent: ViewGroup, viewType: Int) =
            VH(ItemCategoryChipBinding.inflate(LayoutInflater.from(parent.context), parent, false))
        override fun getItemCount() = items.size
        override fun onBindViewHolder(holder: VH, position: Int) {
            val c = items[position]
            holder.vb.txtName.text = c.name ?: "—"
            holder.vb.root.isSelected = c.id == selectedId
            holder.vb.root.setOnClickListener { onClick(c) }
        }
    }

    private inner class ChannelAdapter : RecyclerView.Adapter<ChannelAdapter.VH>() {
        private var items: List<Stream> = emptyList()
        inner class VH(val vb: ItemChannelToggleBinding) : RecyclerView.ViewHolder(vb.root)
        fun submit(list: List<Stream>) { items = list; notifyDataSetChanged() }
        override fun onCreateViewHolder(parent: ViewGroup, viewType: Int) =
            VH(ItemChannelToggleBinding.inflate(LayoutInflater.from(parent.context), parent, false))
        override fun getItemCount() = items.size
        override fun onBindViewHolder(holder: VH, position: Int) {
            val s = items[position]
            val vb = holder.vb
            vb.txtName.text = s.name ?: "—"
            Glide.with(vb.imgLogo).load(s.image).placeholder(R.drawable.ic_placeholder).fitCenter().into(vb.imgLogo)
            val hidden = prefs.hiddenLiveChannels
            vb.check.isChecked = s.id !in hidden
            vb.txtName.alpha = if (vb.check.isChecked) 1f else 0.5f
            vb.root.setOnClickListener {
                val id = s.id ?: return@setOnClickListener
                val set = prefs.hiddenLiveChannels.toMutableSet()
                if (id in set) set.remove(id) else set.add(id)
                prefs.hiddenLiveChannels = set
                notifyItemChanged(position)
                renderCount()
            }
        }
    }

    companion object {
        fun intent(ctx: Context): Intent = Intent(ctx, HiddenChannelsActivity::class.java)
    }
}
