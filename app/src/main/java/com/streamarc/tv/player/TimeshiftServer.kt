package com.streamarc.tv.player

import android.content.Context
import android.os.StatFs
import android.os.SystemClock
import android.util.Log
import com.streamarc.tv.util.AppLog
import com.streamarc.tv.data.XtreamApi
import okhttp3.Call
import okhttp3.OkHttpClient
import okhttp3.Request
import java.io.BufferedReader
import java.io.File
import java.io.IOException
import java.io.InputStreamReader
import java.io.RandomAccessFile
import java.net.InetAddress
import java.net.ServerSocket
import java.net.Socket
import java.util.concurrent.TimeUnit

/**
 * Storage-backed timeshift for a live MPEG-TS stream.
 *
 * Keeps downloading the channel into chunk files in the app cache no matter what the player
 * does, and serves those bytes back to the player over a loopback HTTP connection at whatever
 * pace it reads. Pausing the player therefore never stops the download, and resuming carries on
 * from the same byte. Chunks older than [windowMs] (or the oldest ones when storage runs low)
 * are deleted; a reader that has fallen off the back of the window jumps forward to the oldest
 * data that is left.
 */
class TimeshiftServer(context: Context, private val upstream: String, private val windowMs: Long) {

    private class Chunk(val start: Long, val file: File, val createdAt: Long)

    private val dir = File(context.cacheDir, DIR).apply { mkdirs() }
    private val socket = ServerSocket(0, 4, InetAddress.getByName("127.0.0.1"))
    /** Plays from the start of what has been stored (the point the channel was opened). */
    val localUrl = "http://127.0.0.1:${socket.localPort}/live.ts"

    /** Plays from an absolute byte offset into the stored stream. */
    fun urlFrom(offset: Long) = "http://127.0.0.1:${socket.localPort}/live.ts?from=$offset"

    /** Plays from just behind the newest data. */
    fun urlLive() = "http://127.0.0.1:${socket.localPort}/live.ts?from=live"

    private val lock = Object()
    private val chunks = ArrayList<Chunk>()          // oldest first
    private var written = 0L                          // absolute end of the data
    private var base = 0L                             // absolute start of the oldest chunk
    private var firstByteAt = 0L
    @Volatile private var closed = false
    @Volatile private var failed = false
    @Volatile private var call: Call? = null
    @Volatile var readerPos = 0L; private set
    /** Set when a reader had to skip forward because the data it wanted was already dropped. */
    @Volatile var jumped = false

    private val client = OkHttpClient.Builder()
        .connectTimeout(15, TimeUnit.SECONDS)
        .readTimeout(20, TimeUnit.SECONDS)
        .followRedirects(true).followSslRedirects(true)
        .build()

    init {
        Thread(::downloadLoop, "timeshift-download").apply { isDaemon = true }.start()
        Thread(::acceptLoop, "timeshift-accept").apply { isDaemon = true }.start()
    }

    // ---- Download side ----------------------------------------------------------

    private fun downloadLoop() {
        var attempt = 0
        var lastFailure = 0L
        var raf: RandomAccessFile? = null
        var chunkLen = 0L
        val buf = ByteArray(64 * 1024)
        while (!closed) {
            try {
                val req = Request.Builder().url(upstream).header("User-Agent", XtreamApi.USER_AGENT).header("Connection", "close").build()
                val c = client.newCall(req).also { call = it }
                c.execute().use { resp ->
                    if (!resp.isSuccessful) throw IOException("HTTP ${resp.code}")
                    val input = resp.body?.byteStream() ?: throw IOException("Empty body")
                    while (!closed) {
                        val n = input.read(buf)
                        if (n < 0) throw IOException("Stream ended")
                        if (raf == null || chunkLen >= CHUNK_BYTES) {
                            raf?.close()
                            val chunk = Chunk(written, File(dir, "c${written}.ts"), SystemClock.elapsedRealtime())
                            raf = RandomAccessFile(chunk.file, "rw")
                            chunkLen = 0
                            synchronized(lock) { chunks.add(chunk) }
                            trim()
                        }
                        raf!!.write(buf, 0, n)
                        chunkLen += n
                        synchronized(lock) {
                            if (firstByteAt == 0L) firstByteAt = SystemClock.elapsedRealtime()
                            written += n
                            lock.notifyAll()
                        }
                        attempt = 0
                    }
                }
            } catch (e: Exception) {
                if (closed) break
                val now = SystemClock.elapsedRealtime()
                if (attempt == 0) lastFailure = now
                attempt++
                AppLog.w(TAG, "upstream dropped (attempt $attempt): ${e.message}")
                if (now - lastFailure > GIVE_UP_MS) {
                    failed = true
                    synchronized(lock) { lock.notifyAll() }
                    break
                }
                try { Thread.sleep((1000L * attempt).coerceAtMost(5000L)) } catch (_: InterruptedException) { break }
            }
        }
        try { raf?.close() } catch (_: IOException) {}
    }

    /** Drops chunks that are entirely older than the window, or the oldest ones when storage is short. */
    private fun trim() {
        val now = SystemClock.elapsedRealtime()
        synchronized(lock) {
            while (chunks.size > 1) {
                val oldest = chunks[0]
                val next = chunks[1]
                val stale = next.createdAt < now - windowMs
                val total = written - base
                val low = freeBytes() < MIN_FREE_BYTES || total > MAX_TOTAL_BYTES
                if (!stale && !low) break
                chunks.removeAt(0)
                oldest.file.delete()
                base = next.start
            }
        }
    }

    private fun freeBytes(): Long = try { StatFs(dir.path).availableBytes } catch (_: Exception) { Long.MAX_VALUE }

    // ---- Serving side -----------------------------------------------------------

    private fun acceptLoop() {
        while (!closed) {
            val s = try { socket.accept() } catch (_: IOException) { break }
            Thread({ serve(s) }, "timeshift-serve").apply { isDaemon = true }.start()
        }
    }

    private fun serve(s: Socket) {
        s.use { sock ->
            try {
                sock.tcpNoDelay = true
                val reader = BufferedReader(InputStreamReader(sock.getInputStream(), Charsets.ISO_8859_1))
                var start = 0L
                var ranged = false
                var first = true
                while (true) {
                    val line = reader.readLine() ?: return
                    if (line.isEmpty()) break
                    if (first) {
                        first = false
                        Regex("[?&]from=(\\d+|live)").find(line)?.groupValues?.get(1)?.let { v ->
                            start = if (v == "live") liveOffset() else v.toLong()
                        }
                        continue
                    }
                    if (line.startsWith("Range:", ignoreCase = true)) {
                        Regex("bytes=(\\d+)-").find(line)?.groupValues?.get(1)?.toLongOrNull()?.let { start += it; ranged = true }
                    }
                }
                val out = sock.getOutputStream()
                val status = if (ranged) "206 Partial Content" else "200 OK"
                out.write(("HTTP/1.1 $status\r\nContent-Type: video/mp2t\r\nAccept-Ranges: bytes\r\n" +
                    "Cache-Control: no-store\r\nConnection: close\r\n\r\n").toByteArray(Charsets.ISO_8859_1))

                var pos = start
                var raf: RandomAccessFile? = null
                var current: Chunk? = null
                val buf = ByteArray(64 * 1024)
                while (!closed) {
                    val chunk: Chunk
                    val avail: Long
                    synchronized(lock) {
                        while (!closed && !failed && pos >= written) lock.wait(250)
                        if (closed || (failed && pos >= written)) return
                        if (pos < base) { pos = base; jumped = true }
                        chunk = chunks.last { it.start <= pos }
                        val chunkEnd = chunks.getOrNull(chunks.indexOf(chunk) + 1)?.start ?: written
                        avail = minOf(chunkEnd, written) - pos
                    }
                    if (avail <= 0) continue
                    if (current !== chunk) {
                        raf?.close()
                        raf = RandomAccessFile(chunk.file, "r")
                        current = chunk
                    }
                    raf!!.seek(pos - chunk.start)
                    val n = raf.read(buf, 0, minOf(buf.size.toLong(), avail).toInt())
                    if (n <= 0) continue
                    out.write(buf, 0, n)
                    pos += n
                    readerPos = pos
                }
                raf?.close()
            } catch (_: Exception) {
                // Player closed the connection or storage vanished; the player will reconnect if it wants to.
            }
        }
    }

    // ---- Status -----------------------------------------------------------------

    /** Absolute end of the stored data. */
    fun writtenBytes(): Long = synchronized(lock) { written }

    /** Absolute start of the oldest stored data. */
    fun baseBytes(): Long = synchronized(lock) { base }

    /** Average stream rate seen so far, in bytes per millisecond (0 until data arrives). */
    fun rateBytesPerMs(): Double = synchronized(lock) {
        if (written == 0L || firstByteAt == 0L) return 0.0
        val elapsed = (SystemClock.elapsedRealtime() - firstByteAt).coerceAtLeast(1L)
        written.toDouble() / elapsed
    }

    /** Byte offset a little behind the newest data, so playback from there has something to read. */
    private fun liveOffset(): Long = synchronized(lock) { (written - LIVE_HEADROOM).coerceAtLeast(base) }

    fun close() {
        closed = true
        try { call?.cancel() } catch (_: Exception) {}
        try { client.connectionPool.evictAll() } catch (_: Exception) {}
        try { socket.close() } catch (_: IOException) {}
        synchronized(lock) { lock.notifyAll() }
        Thread({ cleanup(dir) }, "timeshift-clean").start()
    }

    companion object {
        private const val TAG = "Timeshift"
        private const val DIR = "timeshift"
        private const val CHUNK_BYTES = 8L * 1024 * 1024
        private const val MIN_FREE_BYTES = 1L * 1024 * 1024 * 1024   // keep at least 1 GB free
        private const val MAX_TOTAL_BYTES = 8L * 1024 * 1024 * 1024  // hard ceiling per channel
        private const val GIVE_UP_MS = 90_000L
        private const val LIVE_HEADROOM = 512L * 1024

        /** Removes leftovers from a previous run (crash, task kill). */
        fun cleanup(context: Context) = cleanup(File(context.cacheDir, DIR))

        private fun cleanup(dir: File) {
            dir.listFiles()?.forEach { it.delete() }
        }

        fun freeSpaceBytes(context: Context): Long =
            try { StatFs(context.cacheDir.path).availableBytes } catch (_: Exception) { 0L }
    }
}
