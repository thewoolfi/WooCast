// SPDX-License-Identifier: MIT
// Copyright (c) 2025 WooCast contributors

plugins {
    // Начиная с AGP 9 поддержка Kotlin встроена в плагин Android:
    // отдельный org.jetbrains.kotlin.android подключать нельзя (сборка падает с подсказкой).
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.compose)
    alias(libs.plugins.kotlin.serialization)
}

android {
    namespace = "app.woocast.tv"

    // Compose 1.12 и androidx.core 1.19 требуют компиляции против API 37.
    // Начиная с AGP 9 платформа адресуется парой «мажорная.минорная» версия.
    compileSdk = 37
    compileSdkMinor = 0

    defaultConfig {
        applicationId = "app.woocast.tv"
        // Android 7.0: покрывает подавляющее большинство ТВ-приставок.
        minSdk = 24

        // Осознанно ниже compileSdk: на приставках с Android 9–12 новые ограничения
        // targetSdk только мешают, а весь нужный функционал доступен и так.
        targetSdk = 36
        versionCode = 1
        versionName = "0.1.0"
    }

    androidResources {
        // Интерфейс приёмника только на русском; английский оставлен для системных строк.
        localeFilters += listOf("ru", "en")
    }

    signingConfigs {
        // Release builds reuse the debug keystore so that `assembleRelease` produces an
        // installable APK on a developer machine / TV box without extra setup.
        create("localRelease") {
            storeFile = file("${System.getProperty("user.home")}/.android/debug.keystore")
            storePassword = "android"
            keyAlias = "androidkey"
            keyPassword = "android"
        }
    }

    buildTypes {
        debug {
            isMinifyEnabled = false
            isDebuggable = true
        }
        release {
            isMinifyEnabled = false
            isShrinkResources = false
            isDebuggable = false
            proguardFiles(
                getDefaultProguardFile("proguard-android-optimize.txt"),
                "proguard-rules.pro",
            )
            signingConfig = signingConfigs.getByName("localRelease")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    buildFeatures {
        compose = true
        buildConfig = true
    }

    packaging {
        resources {
            excludes += setOf(
                "/META-INF/{AL2.0,LGPL2.1}",
                "/META-INF/DEPENDENCIES",
                "/META-INF/LICENSE*",
                "/META-INF/NOTICE*",
            )
        }
    }

    lint {
        abortOnError = false
        checkReleaseBuilds = false
    }
}

kotlin {
    compilerOptions {
        jvmTarget.set(org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_17)
    }
}

dependencies {
    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.activity.compose)
    implementation(libs.androidx.lifecycle.runtime.ktx)
    implementation(libs.androidx.lifecycle.runtime.compose)

    implementation(platform(libs.compose.bom))
    implementation(libs.compose.ui)
    implementation(libs.compose.ui.graphics)
    implementation(libs.compose.foundation)
    implementation(libs.compose.material3)
    implementation(libs.compose.ui.tooling.preview)
    debugImplementation(libs.compose.ui.tooling)

    implementation(libs.androidx.tv.material)

    implementation(libs.kotlinx.serialization.json)

    // Модульные тесты разбора протокола выполняются на JVM, без устройства.
    testImplementation(libs.junit)
}
