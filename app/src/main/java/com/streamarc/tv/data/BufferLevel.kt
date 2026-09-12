package com.streamarc.tv.data

/**
 * How much of a live stream the player keeps in memory. Bigger buffers ride out network
 * hiccups and let you pause for longer, at the cost of a longer start and more RAM.
 * The byte figure is a ceiling; the player also caps it to half of the app's heap.
 */
enum class BufferLevel(
    val key: String,
    val minBufferMs: Int,
    val maxBufferMs: Int,
    val playbackMs: Int,
    val rebufferMs: Int,
    val bytes: Int,
    val backBufferMs: Int,
    val liveOffsetMs: Long,
    /** Over 0: keep this many minutes of the channel on device storage instead of only in memory. */
    val diskMinutes: Int = 0,
) {
    SMALL("small", 8_000, 30_000, 1_500, 3_000, 24 shl 20, 10_000, 6_000),
    NORMAL("normal", 20_000, 60_000, 2_500, 5_000, 48 shl 20, 20_000, 10_000),
    LARGE("large", 45_000, 120_000, 4_000, 8_000, 96 shl 20, 30_000, 20_000),
    XLARGE("xlarge", 90_000, 300_000, 5_000, 10_000, 192 shl 20, 60_000, 40_000),
    MAX("max", 180_000, 600_000, 6_000, 12_000, 384 shl 20, 120_000, 60_000),
    DISK20("disk20", 20_000, 60_000, 2_500, 5_000, 48 shl 20, 20_000, 10_000, diskMinutes = 20),
    DISK30("disk30", 20_000, 60_000, 2_500, 5_000, 48 shl 20, 20_000, 10_000, diskMinutes = 30),
    DISK60("disk60", 20_000, 60_000, 2_500, 5_000, 48 shl 20, 20_000, 10_000, diskMinutes = 60);

    val onDisk: Boolean get() = diskMinutes > 0

    companion object {
        fun from(key: String?): BufferLevel = entries.firstOrNull { it.key == key } ?: NORMAL
    }
}
