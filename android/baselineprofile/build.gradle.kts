// Baseline Profile 采集模块（Phase 3 的最后一块）。
//
// 为什么单开一个模块：Baseline Profile 的采集器本身是一个**仪器测试**，但它要"自我插桩"
// （自己指定要测哪个 app 模块），常规 `androidTest` 做不到 —— 必须用 `com.android.test`
// 模块 + `targetProjectPath` + `self-instrumenting`。
//
// 产物：`app/src/main/baselineProfiles/…` 由采集器写回（AGP 负责搬），
// 之后 `:app:assembleRelease` 会自动把它打进 APK 的 `assets/dexopt/baseline.prof`。
plugins {
    id("com.android.test")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "com.quizsync.android.baselineprofile"
    compileSdk = 36

    defaultConfig {
        // 采集器自己要装在设备上跑，minSdk 跟着 app 走（这样每台能装 app 的机器都能采集）。
        minSdk = 26
        targetSdk = 36
        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlin {
        jvmToolchain(17)
    }

    // 被测模块（app 侧需要存在同名的 `benchmark` 变体，见 app/build.gradle.kts）
    targetProjectPath = ":app"
    experimentalProperties["android.experimental.self-instrumenting"] = true

    buildTypes {
        create("benchmark") {
            isDebuggable = true
            signingConfig = signingConfigs.getByName("debug")
            matchingFallbacks += listOf("release")
        }
    }
}

dependencies {
    implementation("androidx.test.ext:junit:1.3.0")
    implementation("androidx.test.uiautomator:uiautomator:2.4.0")
    implementation("androidx.benchmark:benchmark-macro-junit4:1.5.0")
}

// 这个模块**只**产出 benchmark 变体（不然会莫名其妙地同时编 debug/release 两套）。
androidComponents {
    beforeVariants(selector().all()) { variant ->
        variant.enable = variant.buildType == "benchmark"
    }
}
