plugins {
    kotlin("jvm")
    kotlin("plugin.serialization")
}

kotlin {
    jvmToolchain(17)
    compilerOptions {
        allWarningsAsErrors.set(true)
    }
}

dependencies {
    // androidx.sqlite 的 BundledSQLiteDriver：**自带 SQLite**（含 FTS5 + trigram），
    // 不赌系统 SQLite 版本 —— Phase 3 计划 §12 点名的风险项就是它。
    api("androidx.sqlite:sqlite-bundled:2.7.1")

    // kotlinx-serialization-json 要 **api**：`:core` 的公共 API 直接泄漏了它的类型
    // （`SyncOp.fields_json: Map<String, JsonElement>`、`HostResponse.jsonObject()`），
    // 用 implementation 的话调用方（`:app`）拿不到这些类型、编译不过。
    // 这正是 Gradle 的规则：公共 API 里出现的类型必须 api 导出。
    api("org.jetbrains.kotlinx:kotlinx-serialization-json:1.11.0")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-core:1.10.2")

    // HTTP 客户端：Android 上没有 java.net.http（API 34 才有），所以按计划用 OkHttp。
    implementation("com.squareup.okhttp3:okhttp:4.12.0")

    testImplementation(kotlin("test"))
    testImplementation("org.jetbrains.kotlinx:kotlinx-coroutines-test:1.10.2")
}

tasks.test {
    useJUnitPlatform()
    // 协议 schema/向量目录：默认取**本仓内的 `protocol/`**（协议已并入本仓，
    // rootProject 是 `android/`，往上一级即仓根）；可用环境变量覆盖，CI 里指到 checkout 的路径。
    environment("QS_PROTOCOL_DIR", System.getenv("QS_PROTOCOL_DIR")
        ?: rootProject.file("../protocol").absolutePath)
    // `dotnet` 的位置：跨实现 E2E（`HostApiEndToEndTest`）要 `dotnet publish` 出来的服务端起进程。
    // **本机的 dotnet 不在 PATH 上**（仓库惯例是走 `QS_DOTNET`），不转发的话那条用例会以
    // `Cannot run program "dotnet"` 失败 —— 看起来像产品坏了，其实是环境没传进去。
    // CI 上 dotnet 在 PATH 里，转发空值不影响。
    System.getenv("QS_DOTNET")?.let { environment("QS_DOTNET", it) }
    // 组合矩阵第四格「Kotlin 原生客户端 × Dart v1 宿主」：宿主由**外部**进程提供
    // （`packages/quizsync_core/tool/matrix_host.dart`），它的配对码只能由启动方从
    // 它自己的 `READY` 行里取，所以这三个值必须从构建环境转发进测试 JVM。
    // 不转发的话 `System.getenv` 在测试进程里恒为 null —— 那一格会**静默退化成**
    // 「自己起 C# 主机」，跑出来还是绿的，其实压根没打 Dart 宿主。
    listOf("QS_HOST_URL", "QS_PAIR_CODE", "QS_HOST_DATA_DIR").forEach { name ->
        System.getenv(name)?.let { environment(name, it) }
    }
    testLogging {
        events("passed", "failed", "skipped")
    }
}
