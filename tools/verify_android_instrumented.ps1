# 真机仪器测试（`androidTest`）的**可复跑入口**。
#
# 为什么不直接 `gradle :app:connectedDebugAndroidTest`：本机这台小米平板（23046RP50C / Android 15）
# 对 **streamed install** 会直接回
#     INSTALL_FAILED_USER_RESTRICTED: Install canceled by user
# 而 **push 安装路径**（`adb install --no-streaming`，等价于 `adb push` + `pm install`）能过。
# Gradle 的 UTP 走的是 ddmlib 的 SplitApkInstaller（streamed），所以那条路在这台机器上必红，
# 且报错信息看起来像"用户点了取消"，很容易被误判成环境偶发。
#
# 本脚本因此：① 只用 gradle **构建**两个 APK；② 用 `--no-streaming` 自己装；
#              ③ 用 `am instrument` 自己跑并解析结果。
#
# 用法：powershell -ExecutionPolicy Bypass -File tools\verify_android_instrumented.ps1 [-InstallOnly]
# 退出码：0 = 全部用例通过；1 = 有失败 / 前置失败。

param(
    [string]$Serial = '',
    [string]$Root = 'D:\DeepseekHarness\TemporaryConversationFile\android-instrumented',
    [switch]$InstallOnly
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$android = Join-Path $repo 'android'
$adb = 'D:\Windows\Apps\Android\Sdk\platform-tools\adb.exe'
$gradle = 'D:\Windows\Apps\Gradle\gradle-8.13\bin\gradle.bat'
$pkg = 'com.quizsync.android.native.dev'
$runner = "$pkg.test/androidx.test.runner.AndroidJUnitRunner"
# ⚠️ **用例类名的包是 `namespace`（`com.quizsync.android.dev`），不是 applicationId** ——
# 第 167 轮把 applicationId 换成 `com.quizsync.android.native[.dev]` 时特意核对过：
# 换安装身份不用动这些类名（`namespace` 决定 R 类与相对类名的解析基准，源码包路径跟着它走）。
$classes = 'com.quizsync.android.dev.AppSmokeInstrumentedTest,com.quizsync.android.dev.QuizRouteStackInstrumentedTest'

$env:JAVA_HOME = if ($env:JAVA_HOME) { $env:JAVA_HOME } else { 'D:\Windows\Apps\Java\jdk-17.0.18' }
$env:ANDROID_HOME = if ($env:ANDROID_HOME) { $env:ANDROID_HOME } else { 'D:\Windows\Apps\Android\Sdk' }
$env:ANDROID_SDK_ROOT = $env:ANDROID_HOME

$pass = 0
$fail = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { $script:pass++; Write-Host ("  PASS  " + $name + $(if ($detail) { "   ($detail)" } else { '' })) -ForegroundColor Green }
    else { $script:fail++; Write-Host ("  FAIL  " + $name + $(if ($detail) { "   ($detail)" } else { '' })) -ForegroundColor Red }
}

function Native([string]$file, [string[]]$arguments) {
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { return ((& $file @arguments 2>&1 | Where-Object { $_ -is [string] }) | Out-String).Trim() }
    finally { $ErrorActionPreference = $prev }
}

New-Item -ItemType Directory -Force -Path $Root | Out-Null

if (-not $Serial) {
    # ⚠️ `@(...)` 不能省：`Native` 返回的是**一个字符串**，`Where-Object` 过滤出 1 条时
    # `$devices` 仍是字符串（不是数组）→ `$devices[0]` 退化成「取第一个字符」，
    # 实测把 `$Serial` 变成了 `e`，安装全报 "device 'e' not found"（第 167 轮踩到）。
    $devices = @((Native $adb @('devices')) -split "`n" | Where-Object { $_ -match '\sdevice$' })
    Check '只连了一台设备（多台时要用 -Serial 指定）' ($devices.Count -eq 1) ($devices -join ' / ')
    if ($devices.Count -ne 1) { exit 1 }
    $Serial = ($devices[0] -split '\s+')[0]
}
Write-Host "`n=== 设备 $Serial ===" -ForegroundColor Cyan

Write-Host "`n=== 构建两个 APK（只构建，不装）===" -ForegroundColor Cyan
$buildLog = Join-Path $Root 'gradle-build.log'
$build = Native $gradle @('-p', $android, ':app:assembleDebug', ':app:assembleDebugAndroidTest')
$build | Set-Content -Encoding UTF8 $buildLog
Check '构建成功（:app:assembleDebug + :app:assembleDebugAndroidTest）' ($build -match 'BUILD SUCCESSFUL') "日志 $buildLog"

$appApk = Join-Path $android 'app\build\outputs\apk\debug\app-debug.apk'
$testApk = Join-Path $android 'app\build\outputs\apk\androidTest\debug\app-debug-androidTest.apk'
Check '两个 APK 都在' ((Test-Path $appApk) -and (Test-Path $testApk)) "$appApk / $testApk"

Write-Host "`n=== 安装（--no-streaming + MIUI 弹窗自动确认）===" -ForegroundColor Cyan

# ⚠️ **全新包**（不是升级）时 MIUI 还会弹「USB安装提示」对话框，而且「拒绝」按钮带
# **2 秒倒计时**（实测文案：`USB安装提示 / 正在通过USB安装此应用，是否继续？ / 继续安装 / 拒绝（2）`）——
# 没人点它就等于"用户拒绝"，`adb install` 回
#     INSTALL_FAILED_USER_RESTRICTED: Install canceled by user
# 看起来像"用户点了取消"，其实是没人在屏幕前点。`--no-streaming` 只解决 streamed 安装被拦那一半
# （第 167 轮换包名时实测：升级已有包**不弹**，装新包**必弹**）。
# 做法：**只在装全新包时**才点（升级已有包不弹，此时盲点等于在应用界面上乱点 40 下），
# 而且点击循环跑在**设备侧**（一次 adb 调用里循环）—— 每点一次都起一个 adb 客户端太慢，
# 2 秒倒计时里点不了几下（实测这样点会输给倒计时）。
function Get-ApkPackage([string]$Path) {
    $aapt = Get-ChildItem (Join-Path $env:ANDROID_HOME 'build-tools\*\aapt2.exe') -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $aapt) { return $null }
    $badging = Native $aapt.FullName @('dump', 'badging', $Path)
    $match = [regex]::Match($badging, "package: name='([^']+)'")
    return $(if ($match.Success) { $match.Groups[1].Value } else { $null })
}

function Install-Apk([string]$Path) {
    $apkPackage = Get-ApkPackage $Path
    $alreadyInstalled = $apkPackage -and
        ((Native $adb @('-s', $Serial, 'shell', 'pm', 'list', 'packages')) -match [regex]::Escape($apkPackage))

    if ($alreadyInstalled) {
        # 升级：MIUI 不弹窗，直接同步装（也绝不在界面上乱点）。
        return Native $adb @('-s', $Serial, 'install', '-r', '-d', '--no-streaming', $Path)
    }

    Write-Host "  （新包 $apkPackage：MIUI 会弹「USB安装提示」，后台点「继续安装」）" -ForegroundColor DarkGray
    $tapJob = Start-Job -ScriptBlock {
        param($adbPath, $serial)
        # 设备侧循环：总共约 12 秒、每 150 ms 一次（覆盖"对话框 2 秒后自动拒绝"那个窗口）。
        & $adbPath -s $serial shell 'i=0; while [ $i -lt 80 ]; do input tap 706 1654; i=$((i+1)); sleep 0.15; done'
    } -ArgumentList $adb, $Serial

    $out = Native $adb @('-s', $Serial, 'install', '-r', '-d', '--no-streaming', $Path)

    Stop-Job $tapJob -ErrorAction SilentlyContinue
    Remove-Job $tapJob -Force -ErrorAction SilentlyContinue
    return $out
}

foreach ($apk in @($appApk, $testApk)) {
    $out = Install-Apk $apk
    $verdict = ($out -split "`n" | Where-Object { $_ -match 'Success|Failure' } | Select-Object -Last 1)
    Check ("安装 " + (Split-Path -Leaf $apk)) ($out -match 'Success') $verdict
}

if ($InstallOnly) {
    Write-Host "`n（-InstallOnly：装完就停，不跑仪器测试）" -ForegroundColor DarkGray
    Write-Host ("`n结果：PASS {0} / FAIL {1}" -f $pass, $fail) -ForegroundColor $(if ($fail -eq 0) { 'Green' } else { 'Red' })
    exit $(if ($fail -eq 0) { 0 } else { 1 })
}

Write-Host "`n=== 跑仪器测试（am instrument 直跑，不走 UTP）===" -ForegroundColor Cyan
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$result = Native $adb @('-s', $Serial, 'shell', 'am', 'instrument', '-w', '-e', 'class', $classes, $runner)
$sw.Stop()
$resultLog = Join-Path $Root 'instrument.log'
$result | Set-Content -Encoding UTF8 $resultLog

# `am instrument` 的成功判据是输出里那行 `OK (N tests)`；失败是 `FAILURES!!!` + 失败清单。
$okLine = ($result -split "`n" | Where-Object { $_ -match '^OK \(\d+ test' } | Select-Object -First 1)
$failed = $result -match 'FAILURES!!!'
Check '仪器测试跑完且没有失败' ((-not $failed) -and ($null -ne $okLine)) `
    $(if ($okLine) { $okLine.Trim() } else { '没看到 OK (N tests) 行' })
Check '用例数与 androidTest 目录里的 @Test 数一致（9 条）' `
    ($okLine -match 'OK \(9 test') "$okLine"

Write-Host "`n耗时 $([int]$sw.Elapsed.TotalSeconds) s；原始输出：$resultLog" -ForegroundColor DarkGray
Write-Host ("`n结果：PASS {0} / FAIL {1}" -f $pass, $fail) -ForegroundColor $(if ($fail -eq 0) { 'Green' } else { 'Red' })
exit $(if ($fail -eq 0) { 0 } else { 1 })
