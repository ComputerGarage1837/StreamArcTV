package com.chocolaterabbit.app.net

import okhttp3.OkHttpClient
import java.util.concurrent.TimeUnit

object Http {
    const val USER_AGENT = "TheChocolateRabbit"

    val client: OkHttpClient = OkHttpClient.Builder()
        .connectTimeout(15, TimeUnit.SECONDS)
        .readTimeout(30, TimeUnit.SECONDS)
        .followRedirects(true)
        .build()
}
