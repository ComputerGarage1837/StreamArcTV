package com.streamarc.tv.data

import com.google.gson.JsonPrimitive
import com.google.gson.annotations.SerializedName

data class LoginResponse(
    @SerializedName("user_info") val userInfo: UserInfo?,
    @SerializedName("server_info") val serverInfo: ServerInfo?
)

data class UserInfo(
    val username: String?,
    val password: String?,
    val message: String?,
    val auth: JsonPrimitive?,
    val status: String?,
    @SerializedName("exp_date") val expDate: String?,
    @SerializedName("is_trial") val isTrial: String?,
    @SerializedName("active_cons") val activeCons: String?,
    @SerializedName("created_at") val createdAt: String?,
    @SerializedName("max_connections") val maxConnections: String?
) {
    val isAuthenticated: Boolean
        get() {
            val a = auth ?: return false
            return if (a.isBoolean) a.asBoolean else a.asString == "1" || a.asString.equals("true", true)
        }

    val expDateEpochSeconds: Long?
        get() = expDate?.trim()?.toLongOrNull()?.takeIf { it > 0 }
}

data class ServerInfo(
    val url: String?,
    val port: String?,
    @SerializedName("https_port") val httpsPort: String?,
    @SerializedName("server_protocol") val serverProtocol: String?
)

data class Category(
    @SerializedName("category_id") val id: String?,
    @SerializedName("category_name") val name: String?
)

data class Stream(
    val name: String?,
    @SerializedName("stream_id") val streamId: String?,
    @SerializedName("series_id") val seriesId: String?,
    @SerializedName("stream_icon") val icon: String?,
    @SerializedName("cover") val cover: String?,
    val plot: String?,
    @SerializedName("category_id") val categoryId: String?,
    @SerializedName("container_extension") val containerExtension: String?,
    @SerializedName("epg_channel_id") val epgChannelId: String?,
    @SerializedName("num") val number: String?,
    val rating: String?,
    val added: String?
) {
    /** Stream id for live/movies, series id for series. */
    val id: String? get() = streamId ?: seriesId
    val image: String? get() = icon?.takeIf { it.isNotBlank() } ?: cover?.takeIf { it.isNotBlank() }
}

/** One episode of a series, as returned by `get_series_info`. */
data class Episode(
    val id: String,
    val title: String,
    val season: Int,
    val number: Int,
    val containerExtension: String,
    val plot: String?,
    val duration: String?
)

/** Locally stored, signed-in account for one service. */
data class Account(
    val username: String,
    val password: String,
    val status: String?,
    val expDate: Long?,           // epoch seconds, null = unlimited/unknown
    val maxConnections: String?,
    val activeConnections: String?,
    val createdAt: Long?,
    val isTrial: Boolean
)

fun UserInfo.toAccount(username: String, password: String) = Account(
    username = username,
    password = password,
    status = status,
    expDate = expDateEpochSeconds,
    maxConnections = maxConnections,
    activeConnections = activeCons,
    createdAt = createdAt?.trim()?.toLongOrNull()?.takeIf { it > 0 },
    isTrial = isTrial == "1" || isTrial.equals("true", true)
)
