package com.streamarc.tv.ui

import android.content.Context
import android.content.Intent
import android.os.Bundle
import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import androidx.appcompat.app.AppCompatActivity
import androidx.core.widget.doAfterTextChanged
import androidx.lifecycle.lifecycleScope
import androidx.recyclerview.widget.GridLayoutManager
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
import com.streamarc.tv.databinding.ActivityBrowseBinding
import com.streamarc.tv.databinding.ItemCategoryBinding
import com.streamarc.tv.databinding.ItemStreamBinding
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch

class BrowseActivity : AppCompatActivity() {

    private lateinit var b: ActivityBrowseBinding
    private lateinit var service: Service
    private lateinit var prefs: Prefs
    private lateinit var account: Account

    private val categoryAdapter = CategoryAdapter { cat -> selectCategory(cat) }
    private val streamAdapter = StreamAdapter { s -> play(s) }

    private var allStreams: List<Stream> = emptyList()
    private var loadJob: Job? = null
    private var selectedCategoryId: String? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        b = ActivityBrowseBinding.inflate(layoutInflater)
        setContentView(b.root)
        prefs = Prefs(this)
        service = Service.valueOf(intent.getStringExtra(EXTRA_SERVICE) ?: Service.LIVE.name)

        val acct = prefs.account(service)
        if (acct == null) {
            startActivity(LoginActivity.intent(this, service))
            finish()
            return
        }
        account = acct

        b.txtTitle.text = service.title
        b.btnBack.setOnClickListener { finish() }
        b.btnProfile.setOnClickListener { startActivity(ProfileActivity.intent(this, service)) }

        b.listCategories.layoutManager = LinearLayoutManager(this)
        b.listCategories.adapter = categoryAdapter

        val isGrid = service.kind == ContentKind.MOVIE
        streamAdapter.grid = isGrid
        b.listStreams.layoutManager = if (isGrid) {
            GridLayoutManager(this, resources.getInteger(R.integer.poster_columns))
        } else {
            LinearLayoutManager(this)
        }
        b.listStreams.adapter = streamAdapter

        b.inputSearch.doAfterTextChanged { applyFilter() }

        loadCategories()
    }

    override fun onResume() {
        super.onResume()
        // If the user signed out from the profile screen, leave.
        if (!prefs.isSignedIn(service)) finish()
    }

    private fun loadCategories() {
        setLoading(true)
        lifecycleScope.launch {
            try {
                val cats = XtreamApi.categories(service, account)
                val all = listOf(Category(null, getString(R.string.all_categories))) + cats
                categoryAdapter.submit(all)
                selectCategory(all.first())
            } catch (e: Exception) {
                showError(e.message ?: getString(R.string.load_failed))
            }
        }
    }

    private fun selectCategory(cat: Category) {
        selectedCategoryId = cat.id
        categoryAdapter.selectedId = cat.id
        b.txtCategory.text = cat.name
        loadJob?.cancel()
        setLoading(true)
        loadJob = lifecycleScope.launch {
            try {
                allStreams = XtreamApi.streams(service, account, cat.id)
                applyFilter()
                setLoading(false)
                if (allStreams.isEmpty()) b.txtEmpty.visibility = View.VISIBLE
            } catch (e: Exception) {
                showError(e.message ?: getString(R.string.load_failed))
            }
        }
    }

    private fun applyFilter() {
        val q = b.inputSearch.text?.toString()?.trim().orEmpty()
        val list = if (q.isEmpty()) allStreams
        else allStreams.filter { it.name?.contains(q, ignoreCase = true) == true }
        streamAdapter.submit(list)
        b.txtEmpty.visibility = if (list.isEmpty() && allStreams.isNotEmpty()) View.VISIBLE else View.GONE
    }

    private fun play(stream: Stream) {
        val url = try {
            XtreamApi.streamUrl(service, account, stream, prefs.liveFormat)
        } catch (e: Exception) {
            showError(e.message ?: getString(R.string.load_failed)); return
        }
        startActivity(PlayerActivity.intent(this, url, stream.name ?: "", service.kind == ContentKind.LIVE))
    }

    private fun setLoading(loading: Boolean) {
        b.progress.visibility = if (loading) View.VISIBLE else View.GONE
        b.txtError.visibility = View.GONE
        if (loading) b.txtEmpty.visibility = View.GONE
    }

    private fun showError(msg: String) {
        b.progress.visibility = View.GONE
        b.txtError.text = msg
        b.txtError.visibility = View.VISIBLE
    }

    // ---- Adapters ------------------------------------------------------

    private class CategoryAdapter(val onClick: (Category) -> Unit) :
        RecyclerView.Adapter<CategoryAdapter.VH>() {

        private var items: List<Category> = emptyList()
        var selectedId: String? = null
            set(value) { field = value; notifyDataSetChanged() }

        class VH(val vb: ItemCategoryBinding) : RecyclerView.ViewHolder(vb.root)

        fun submit(list: List<Category>) { items = list; notifyDataSetChanged() }

        override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): VH =
            VH(ItemCategoryBinding.inflate(LayoutInflater.from(parent.context), parent, false))

        override fun getItemCount() = items.size

        override fun onBindViewHolder(holder: VH, position: Int) {
            val c = items[position]
            holder.vb.txtName.text = c.name ?: "—"
            holder.vb.root.isSelected = c.id == selectedId
            holder.vb.root.setOnClickListener { onClick(c) }
        }
    }

    private class StreamAdapter(val onClick: (Stream) -> Unit) :
        RecyclerView.Adapter<StreamAdapter.VH>() {

        private var items: List<Stream> = emptyList()
        var grid: Boolean = false

        class VH(val vb: ItemStreamBinding) : RecyclerView.ViewHolder(vb.root)

        fun submit(list: List<Stream>) { items = list; notifyDataSetChanged() }

        override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): VH {
            val vb = ItemStreamBinding.inflate(LayoutInflater.from(parent.context), parent, false)
            if (grid) {
                vb.root.orientation = android.widget.LinearLayout.VERTICAL
                vb.imgIcon.layoutParams = vb.imgIcon.layoutParams.apply {
                    width = ViewGroup.LayoutParams.MATCH_PARENT
                    height = parent.resources.getDimensionPixelSize(R.dimen.poster_height)
                }
                vb.txtName.maxLines = 2
                vb.txtName.gravity = android.view.Gravity.CENTER_HORIZONTAL
            }
            return VH(vb)
        }

        override fun getItemCount() = items.size

        override fun onBindViewHolder(holder: VH, position: Int) {
            val s = items[position]
            holder.vb.txtName.text = s.name ?: "—"
            Glide.with(holder.vb.imgIcon)
                .load(s.icon?.takeIf { it.isNotBlank() })
                .placeholder(R.drawable.ic_placeholder)
                .error(R.drawable.ic_placeholder)
                .centerCrop()
                .into(holder.vb.imgIcon)
            holder.vb.root.setOnClickListener { onClick(s) }
        }
    }

    companion object {
        private const val EXTRA_SERVICE = "service"
        fun intent(ctx: Context, service: Service): Intent =
            Intent(ctx, BrowseActivity::class.java).putExtra(EXTRA_SERVICE, service.name)
    }
}
