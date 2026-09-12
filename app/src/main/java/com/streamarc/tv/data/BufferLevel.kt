package com.streamarc.tv.data

/**
 * How much of a live stream the player keeps ahead. Bigger buffers ride out network hiccups and
 * let you pause for longer, at the cost of a longer start and more RAM. The byte figure is a
 * ceiling; the player also caps it to half of the app's heap. Levels with [diskMinutes] > 0 keep
 * the stream on device storage instead, and only use a modest in-memory buffer in front of it.
 */
enum class BufferLevel(
    val key: String,
    val minBufferMs: Int,
    val maxBufferMs: Int,
    val playbackMs: Int,
    val rebufferMs: Int,
    val bytes: Int,
    val diskMinutes: Int = 0,
) {
    SMALL("small", 8_000, 30_000, 1_500, 3_000, 24 shl 20),
    NORMAL("normal", 20_000, 60_000, 2_500, 5_000, 64 shl 20),
    LARGE("large", 45_000, 120_000, 4_000, 8_000, 128 shl 20),
    XLARGE("xlarge", 90_000, 300_000, 5_000, 10_000, 256 shl 20),
    MAX("max", 180_000, 600_000, 6_000, 12_000, 512 shl 20),
    DISK20("disk20", 20_000, 60_000, 2_500, 5_000, 64 shl 20, diskMinutes = 20),
    DISK30("disk30", 20_000, 60_000, 2_500, 5_000, 64 shl 20, diskMinutes = 30),
    DISK60("disk60", 20_000, 60_000, 2_500, 5_000, 64 shl 20, diskMinutes = 60);

    val onDisk: Boolean get() = diskMinutes > 0

    companion object {
        /** Huge is the default: the heap cap already scales it down on small devices. */
        fun from(key: String?): BufferLevel = entries.firstOrNull { it.key == key } ?: MAX
    }
}
