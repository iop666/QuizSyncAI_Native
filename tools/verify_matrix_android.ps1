# 组合矩阵第 3 / 4 格：**Kotlin 原生客户端（2.0 内核）** × {C# v2 主机, Dart v1 宿主}
#
# 前两格（Flutter 1.1.0 形状的客户端 × 两种宿主）在 `tools\verify_matrix.ps1` 里；
# 本脚本补上「客户端换成 2.0 的 Kotlin 内核」那两格。两格用的是同一条真 E2E：
# `android/core` 的 `HostApiEndToEndTest` —— 探活 → 未配对 401 → 配对 → 上传去重
# → 播合集 → 建任务 202 → 轮询到 done → 同 id 幂等复提 → 冒充别人推 op 被拒。
#
# 第 4 格的宿主是**外部**的 `dart run tool/matrix_host.dart`（v1 参考宿主）：
# 它的配对码只能由启动方从 `READY` 行里取，所以走 `QS_HOST_URL` / `QS_PAIR_CODE` /
# `QS_HOST_DATA_DIR` 三个环境变量传进去（`android/core/build.gradle.kts` 负责转发进测试 JVM）。
# 跑完再**查它自己的库**反向证明「这一格真的打了 Dart 宿主，而不是静默退化成了自己起 C# 主机」。
#
# 用法：powershell -ExecutionPolicy Bypass -File tools\verify_matrix_android.ps1
# 退出码：0 = 两格全过；1 = 有 FAIL 或前置失败。

param(
    [int]$Port = 8798,
    [string]$Root = 'D:\DeepseekHarness\TemporaryConversationFile\matrix-android'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$android = Join-Path $repo 'android'
$testClass = 'com.quizsync.android.core.net.HostApiEndToEndTest'

$gradle = 'D:\Windows\Apps\Gradle\gradle-8.13\bin\gradle.bat'
$dart = 'D:\Windows\Apps\flutter\flutter\bin\dart.bat'
$python = if ($env:QS_PYTHON) { $env:QS_PYTHON } else { 'python' }
$dbq = 'D:\DeepseekHarness\TemporaryConversationFile\code\dbq.py'

$env:JAVA_HOME = if ($env:JAVA_HOME) { $env:JAVA_HOME } else { 'D:\Windows\Apps\Java\jdk-17.0.18' }
$env:ANDROID_HOME = if ($env:ANDROID_HOME) { $env:ANDROID_HOME } else { 'D:\Windows\Apps\Android\Sdk' }
$env:ANDROID_SDK_ROOT = $env:ANDROID_HOME
if (-not $env:QS_DOTNET) { $env:QS_DOTNET = 'D:\Windows\Apps\DSH_Tools\dotnet\dotnet.exe' }
if (-not $env:QS_PROTOCOL_DIR) { $env:QS_PROTOCOL_DIR = 'D:\ZCode\QuizSyncProtocol' }

$pass = 0
$fail = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { $script:pass++; Write-Host ("  PASS  " + $name + $(if ($detail) { "   ($detail)" } else { '' })) -ForegroundColor Green }
    else { $script:fail++; Write-Host ("  FAIL  " + $name + $(if ($detail) { "   ($detail)" } else { '' })) -ForegroundColor Red }
}

# 原生命令输出：**不要用 `2>$null`**（stderr 每行都是 ErrorRecord，Stop 策略下第一行就终止脚本）。
function Native([string]$file, [string[]]$arguments) {
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { return ((& $file @arguments 2>&1 | Where-Object { $_ -is [string] }) | Out-String).Trim() }
    finally { $ErrorActionPreference = $prev }
}

# 跑一格：返回 exit code / 是否有 PASSED / 是否有 FAILED / 日志路径
function RunCell([string]$title) {
    Write-Host "`n=== $title ===" -ForegroundColor Cyan
    $stamp = [guid]::NewGuid().ToString('N').Substring(0, 6)
    $log = Join-Path $Root ("gradle-$stamp.log")
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $lines = & $gradle -p $android ':core:test' '--tests' $testClass '--rerun' 2>&1 |
        Where-Object { $_ -is [string] }
    $code = $LASTEXITCODE
    $ErrorActionPreference = $prev
    $lines | Set-Content -Encoding UTF8 $log
    $lines | Select-String -Pattern 'PASSED|FAILED|BUILD |tests completed' | ForEach-Object { "  " + $_.Line.Trim() }
    $text = ($lines | Out-String)
    return @{
        Code = $code
        Passed = [bool]($text -match 'PASSED')
        Failed = [bool]($text -match 'FAILED')
        Log = $log
    }
}

Remove-Item -Recurse -Force $Root -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $Root | Out-Null

# ---------- 第 3 格：Kotlin 客户端 × C# v2 主机（用例自己起主机）----------
foreach ($name in 'QS_HOST_URL', 'QS_PAIR_CODE', 'QS_HOST_DATA_DIR') {
    Remove-Item ("Env:" + $name) -ErrorAction SilentlyContinue
}
$cellC = RunCell '第 3 格：Kotlin 原生客户端 × C# v2 主机（走 /api/v1/* 兼容层）'
Check '第 3 格用例通过（配对→上传→任务→幂等→拒冒充）' `
    ($cellC.Code -eq 0 -and $cellC.Passed -and -not $cellC.Failed) "exit=$($cellC.Code) 日志=$($cellC.Log)"

# ---------- 第 4 格：Kotlin 客户端 × Dart v1 宿主（外部进程）----------
$dataDir = Join-Path $Root 'dart-host'
$outLog = Join-Path $Root 'dart-host.out.log'
$errLog = Join-Path $Root 'dart-host.err.log'
New-Item -ItemType Directory -Force -Path $dataDir | Out-Null

Write-Host "`n=== 起 Dart v1 宿主（v1 参考实现，端口 $Port）===" -ForegroundColor Cyan
# ⚠️ 变量**不能叫 `$host`**：PowerShell 里 `$Host` 是只读的自动变量（宿主 UI 对象），
#    赋值会以 `VariableNotWritable` 终止脚本 —— 而 `Start-Process` 已经先跑起来了，
#    于是留下一个没人收尾的 Dart 宿主（第一次跑就是这么挂住的）。
$dartHost = Start-Process -FilePath $dart `
    -ArgumentList @('run', 'tool/matrix_host.dart', "$Port", $dataDir) `
    -WorkingDirectory (Join-Path $repo 'packages\quizsync_core') -PassThru -NoNewWindow `
    -RedirectStandardOutput $outLog -RedirectStandardError $errLog

try {
    # 首次 `dart run` 要跑 build hooks（远超 30 秒）—— 给 5 分钟。
    $ready = $null
    foreach ($attempt in 1..1000) {
        Start-Sleep -Milliseconds 300
        if (Test-Path $outLog) {
            $ready = (Get-Content $outLog | Where-Object { $_ -match '^READY ' } | Select-Object -First 1)
        }
        if ($ready) { break }
    }
    Check 'Dart 宿主打印 READY' ($null -ne $ready) "$ready"
    if (-not $ready) { throw "Dart 宿主没起来（看 $errLog）" }

    $pairCode = [regex]::Match($ready, 'code=(\d{6})').Groups[1].Value
    Check '从 READY 行拿到 6 位配对码' ($pairCode.Length -eq 6) "code=$pairCode"

    $env:QS_HOST_URL = "http://127.0.0.1:$Port"
    $env:QS_PAIR_CODE = $pairCode
    $env:QS_HOST_DATA_DIR = $dataDir

    $cellD = RunCell '第 4 格：Kotlin 原生客户端 × Dart v1 宿主'
    Check '第 4 格用例通过（同一条 E2E，换成 v1 宿主）' `
        ($cellD.Code -eq 0 -and $cellD.Passed -and -not $cellD.Failed) "exit=$($cellD.Code) 日志=$($cellD.Log)"

    # 反向证明：Dart 宿主自己的库里应当有这台测试设备与那条任务。
    # 没有这一条时，「用例绿了」也可能是它偷偷自己起了 C# 主机（那就等于第 3 格跑了两次）。
    $db = Join-Path $dataDir 'quizsync.db'
    $sql = "select (select count(*) from devices where device_id='android-kotlin-1')," +
        "(select count(*) from tasks where task_id='t-kotlin-1')," +
        "(select status from tasks where task_id='t-kotlin-1')"
    $row = Native $python @($dbq, $db, $sql)
    Check 'Dart 宿主的库里落下这台客户端与这条任务（证明真的打了它）' `
        ($row -match "1, 1, 'done'") "$row"
}
finally {
    if ($dartHost) {
        $kill = Native 'taskkill.exe' @('/T', '/F', '/PID', "$($dartHost.Id)")
        if ($kill -notmatch 'SUCCESS|成功') {
            Write-Host ("  ⚠️ 收尾没杀掉 Dart 宿主 {0}：{1}" -f $dartHost.Id, $kill) -ForegroundColor Yellow
        }
    }
}

Write-Host ("`n结果：PASS {0} / FAIL {1}" -f $pass, $fail) -ForegroundColor $(if ($fail -eq 0) { 'Green' } else { 'Red' })
Write-Host '矩阵第 3 格 = Kotlin 原生客户端 × C# v2 主机；第 4 格 = Kotlin 原生客户端 × Dart v1 宿主'
exit $(if ($fail -eq 0) { 0 } else { 1 })
