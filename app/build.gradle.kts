plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

val versionNameProp: String = (project.findProperty("VERSION_NAME") as String?)?.removePrefix("v") ?: "1.0.0"

fun versionCodeFrom(v: String): Int {
    val parts = v.split(".").map { p -> p.takeWhile { it.isDigit() }.toIntOrNull() ?: 0 }
    return parts.getOrElse(0) { 0 } * 10000 + parts.getOrElse(1) { 0 } * 100 + parts.getOrElse(2) { 0 }
}

/** Project property, then environment variable, then default. */
fun prop(name: String, default: String): String =
    (project.findProperty(name) as String?)?.takeIf { it.isNotBlank() }
        ?: System.getenv(name)?.takeIf { it.isNotBlank() }
        ?: default

android {
    namespace = "com.streamarc.tv"
    compileSdk = 35

    defaultConfig {
        // Must never change: the installed base and release/update.json use this ID.
        applicationId = "com.computergarage.streamarctv"
        minSdk = 21
        targetSdk = 35
        versionCode = versionCodeFrom(versionNameProp)
        versionName = versionNameProp

        buildConfigField("String", "GITHUB_REPO", "\"${prop("GITHUB_REPO", "ComputerGarage1837/StreamArcTV")}\"")
        buildConfigField("String", "LIVE_URL", "\"${prop("LIVE_URL", "https://mediahere.ca/")}\"")
        buildConfigField("String", "VOD_URL", "\"${prop("VOD_URL", "https://onlypuds.fans:2083/")}\"")
    }

    signingConfigs {
        create("release") {
            val ksPath = System.getenv("KEYSTORE_PATH")
            if (!ksPath.isNullOrBlank()) {
                storeFile = file(ksPath)
                storePassword = System.getenv("KEYSTORE_PASSWORD")
                keyAlias = System.getenv("KEY_ALIAS")
                keyPassword = System.getenv("KEY_PASSWORD")
            }
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            if (!System.getenv("KEYSTORE_PATH").isNullOrBlank()) {
                signingConfig = signingConfigs.getByName("release")
            }
        }
    }

    buildFeatures {
        buildConfig = true
        viewBinding = true
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlinOptions {
        jvmTarget = "17"
    }
}

dependencies {
    implementation("androidx.core:core-ktx:1.13.1")
    implementation("androidx.appcompat:appcompat:1.7.0")
    implementation("com.google.android.material:material:1.12.0")
    implementation("androidx.constraintlayout:constraintlayout:2.1.4")
    implementation("androidx.recyclerview:recyclerview:1.3.2")
    implementation("androidx.documentfile:documentfile:1.0.1")
    implementation("androidx.lifecycle:lifecycle-runtime-ktx:2.8.6")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.8.1")

    implementation("com.squareup.okhttp3:okhttp:4.12.0")
    implementation("com.google.code.gson:gson:2.11.0")
    implementation("com.github.bumptech.glide:glide:4.16.0")

    implementation("androidx.media3:media3-exoplayer:1.4.1")
    implementation("androidx.media3:media3-exoplayer-hls:1.4.1")
    implementation("androidx.media3:media3-ui:1.4.1")
}
