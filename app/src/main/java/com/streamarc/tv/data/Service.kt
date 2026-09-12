package com.streamarc.tv.data

import com.streamarc.tv.BuildConfig

/**
 * The two content services the app connects to. Each has its own
 * Xtream Codes server (supplied at build time) and its own sign-in.
 */
enum class Service(val title: String, val baseUrl: String, val kind: ContentKind) {
    LIVE("Live TV", BuildConfig.LIVE_URL.trim().trimEnd('/'), ContentKind.LIVE),
    VOD("Video on Demand", BuildConfig.VOD_URL.trim().trimEnd('/'), ContentKind.MOVIE);

    /** False when this build was made without a server address for the service. */
    val isConfigured: Boolean get() = baseUrl.isNotBlank()
}

enum class ContentKind { LIVE, MOVIE }
