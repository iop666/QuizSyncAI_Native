plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("org.jetbrains.kotlin.plugin.compose")
}

android {
    // ⚠️ `namespace` **保持 `com.quizsync.android.dev` 不动**（M24 的老规矩，见 `docs/DECISIONS.md`）：
    //    它决定 R 类与 manifest 里 `.MainActivity` 这类相对类名的解析基准，**不是安装身份**；
    //    改它要连带搬 40+ 个 Kotlin 文件的目录与 import，没有功能收益、只有风险。
    //    所以「源码包叫 .dev、装出来叫 .native」是**故意**的，不是漏改。
    namespace = "com.quizsync.android.dev"
    compileSdk = 36

    defaultConfig {
        // **正式版包名**（用户 2026-09-28 拍板）：`com.quizsync.android.native` ——
        // 与 Flutter 线的 `com.quizsync.android`（1.2.1）**明确区分**：两者是系统眼里两个应用，
        // 可并存、永不互相覆盖，一眼也能从包名看出这是原生 2.0 那条线。
        applicationId = "com.quizsync.android.native"
        minSdk = 26
        targetSdk = 36
        versionCode = 1
        versionName = "1.1.0-native"
        // 真机仪器测试（Phase 3 的缺口：这个目录以前根本不存在，安卓验证全靠 PowerShell 脚本）。
        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
    }

    buildTypes {
        debug {
            // 开发包 = `com.quizsync.android.native.dev`：与正式版（`.native`）、
            // 与 Flutter 的 1.2.1（`.android`）**三者并存**，开发包绝不覆盖用户能用的版本
            // （计划 §Phase 3 的回滚策略）。版本号也带后缀，界面上一眼分得清装的是哪个。
            applicationIdSuffix = ".dev"
            versionNameSuffix = "-dev"
        }

        release {
            // 开发期不配签名：release 只用于「能不能编出来」的验证。
            isMinifyEnabled = false
        }

        // Baseline Profile 采集用的变体（Phase 3）。
        // ⚠️ **必须 `isDebuggable = false`**：Macrobenchmark 会拒绝 debuggable 的包
        //    （否则测出来的启动路径根本不是发布的那个）。其余照 release 抄。
        create("benchmark") {
            initWith(getByName("release"))
            signingConfig = signingConfigs.getByName("debug")
            matchingFallbacks += listOf("release")
            isDebuggable = false
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlin {
        jvmToolchain(17)
    }

    buildFeatures {
        compose = true
    }
}

dependencies {
    implementation(project(":core"))
    implementation("androidx.core:core-ktx:1.17.0")
    implementation("androidx.activity:activity-compose:1.11.0")
    implementation("androidx.lifecycle:lifecycle-runtime-compose:2.9.4")
    // ViewModel + viewModel()（ui-spec-android 要求「UI 状态集中在 UiState、StateFlow」）。
    implementation("androidx.lifecycle:lifecycle-viewmodel-compose:2.9.4")

    // Compose BOM 统一版本（避免各库版本互相打架）。
    implementation(platform("androidx.compose:compose-bom:2025.09.00"))
    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.material3:material3")
    // 规格 §1.2 / §2.x 用的图标（history_outlined / qr_code_scanner / folder_off_outlined…）
    // 不在 material-icons-core 里，必须带 extended 才有。
    implementation("androidx.compose.material:material-icons-extended")
    implementation("androidx.compose.ui:ui-tooling-preview")
    // 窗口尺寸类（手机 / 平板 / 折叠屏）：给 `currentWindowAdaptiveInfo()` 用。
    // ⚠️ **不写版本号** —— 交给上面的 compose-bom 决定（BOM 2025.09.00 把 adaptive pin 在 1.1.0、
    // navigation-suite pin 在 1.3.2，是一起测试过的组合）。
    // 也**不要**加 `androidx.window:window-core`：adaptive 已经传递依赖它，
    // 显式写会把一件拉出 BOM 的测试矩阵（见 docs/design-system-plan.md §6.3 的字节码实证）。
    implementation("androidx.compose.material3.adaptive:adaptive")
    implementation("androidx.compose.material3:material3-adaptive-navigation-suite")

    // 配对页「方式一：扫码」（规格 §2.1 / 计划 §5.2）。两条取舍写在这里，别下一个人再猜：
    // ① **不用 ML Kit barcode**：它要 Google Play 服务，而目标机是小米平板（大概率没有 Play），
    //    计划 §5.2 列的备选「ZXing-embedded（无 Play 依赖）」才是这台机器上跑得起来的那条；
    // ② **只引 ZXing core**（纯 Java，无 Android / Play 依赖），预览与取帧用 CameraX，
    //    不引 `zxing-android-embedded`（它自带一个 Activity，与手写路由打架）。
    // 版本是镜像实测的（`maven.aliyun.com`）：CameraX stable **1.6.2**、zxing core **3.5.4**。
    implementation("androidx.camera:camera-core:1.6.2")
    implementation("androidx.camera:camera-camera2:1.6.2")
    implementation("androidx.camera:camera-lifecycle:1.6.2")
    implementation("androidx.camera:camera-view:1.6.2")
    implementation("com.google.zxing:core:3.5.4")

    debugImplementation("androidx.compose.ui:ui-tooling")

    testImplementation(kotlin("test"))

    // ===== 真机仪器测试（androidTest）=====
    // 与 JVM 单测分工：单测覆盖纯逻辑（解析/状态），仪器测试覆盖"真的装到机器上、真的画出来"。
    // 版本交给 compose-bom（ui-test-* 在里面），只有 test 基础设施写死版本。
    androidTestImplementation("androidx.test.ext:junit:1.3.0")
    androidTestImplementation("androidx.test:runner:1.7.0")
    androidTestImplementation("androidx.test:rules:1.7.0")
    androidTestImplementation(platform("androidx.compose:compose-bom:2025.09.00"))
    androidTestImplementation("androidx.compose.ui:ui-test-junit4")
    // `createAndroidComposeRule` 需要它把测试专用的 Activity 清单合进来。
    debugImplementation("androidx.compose.ui:ui-test-manifest")
}
