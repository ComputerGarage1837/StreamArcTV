package com.streamarc.tv.player

import com.streamarc.tv.data.Service
import com.streamarc.tv.data.Stream

/**
 * The channels shown in the guide when a live channel was opened full screen, so the player can
 * move to the next or previous one (channel up / down) without going back to the guide.
 */
object ChannelQueue {
    @Volatile var service: Service? = null
        private set
    private var channels: List<Stream> = emptyList()
    private var index = -1

    fun set(service: Service, list: List<Stream>, current: Stream) {
        this.service = service
        channels = list
        index = list.indexOfFirst { it.streamId != null && it.streamId == current.streamId }
    }

    fun clear() { channels = emptyList(); index = -1; service = null }

    /** The channel the player was opened on (and then moved to by channel up / down). */
    fun current(): Stream? = channels.getOrNull(index)

    val available: Boolean get() = service != null && channels.size > 1 && index in channels.indices

    /** Moves by [delta] (wrapping round) and returns the channel there. */
    fun step(delta: Int): Stream? {
        if (!available) return null
        index = ((index + delta) % channels.size + channels.size) % channels.size
        return channels[index]
    }
}
