# 剪贴板监听验收：往系统剪贴板放一张图 → 应用应当**自己**开始识别（静默、不抢焦点）。
#
# 为什么值得单独一条脚本：这一项在 2.0 里长期是「设置项存在、实现没接上」，
# 而它恰恰是最容易「点了没反应」的功能 —— 用户复制一张图，屏幕上什么都不跳。
# 监听是系统消息（`WM_CLIPBOARDUPDATE`），除了应用自己的日志没有别的可观测面，
# 所以判据全部读 `logs\crash.log`。
#
# 四条判据：
#   ① 应用起来后注册了监听（日志有「剪贴板监听：已注册」）；
#   ② 放一张新图 → 应用自己读了剪贴板并开始识别（「识别新图片」）；
#   ③ 再放**同一张**图 → 不再识别（按内容摘要去重，不能每次复制都重跑一遍 AI）；
#   ④ 负向对照：把「剪贴板监听」关掉 → 放**新**图也不识别（证明设置真的控住了它）。
#      ④ 会临时改 `<数据目录>\app.json`，**无论成败都在 finally 里改回去**。
#
# 用法：powershell -ExecutionPolicy Bypass -File tools\verify_clipboard_watch.ps1 [-KeepRunning]
# 退出码：0 = 全过；1 = 有 FAIL 或前置失败。

param(
    [string]$Exe = '',
    [int]$WaitSeconds = 30,
    [switch]$KeepRunning
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$putImage = Join-Path $PSScriptRoot 'clipboard_put_image.ps1'
$log = Join-Path $env:LOCALAPPDATA 'QuizSyncAI\app\logs\crash.log'
$appJson = Join-Path $env:LOCALAPPDATA 'QuizSyncAI\app\app.json'

if (-not $Exe) {
    $candidates = @(
        (Join-Path $repo 'windows\QuizSync.App\bin\Debug\net10.0-windows10.0.26100.0\win-x64\QuizSync.App.exe'),
        (Join-Path $repo 'windows\QuizSync.App\bin\Release\net10.0-windows10.0.26100.0\win-x64\QuizSync.App.exe')
    )
    $Exe = ($candidates | Where-Object { Test-Path $_ } | Select-Object -First 1)
}

$pass = 0
$fail = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { $script:pass++; Write-Host ("  PASS  " + $name + $(if ($detail) { "   ($detail)" } else { '' })) -ForegroundColor Green }
    else { $script:fail++; Write-Host ("  FAIL  " + $name + $(if ($detail) { "   ($detail)" } else { '' })) -ForegroundColor Red }
}

function LogText() {
    if (-not (Test-Path $log)) { return '' }
    return (Get-Content $log -Encoding UTF8 -Raw)
}

function CountOf([string]$pattern) {
    $text = LogText
    return ([regex]::Matches($text, [regex]::Escape($pattern))).Count
}

function WaitForCount([string]$pattern, [int]$atLeast, [int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        if ((CountOf $pattern) -ge $atLeast) { return $true }
        Start-Sleep -Milliseconds 400
    }
    return $false
}

function PutImage([int]$seed) {
    $out = & powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -File $putImage -Seed $seed 2>&1
    return (($out | Where-Object { $_ -is [string] }) -join ' ').Trim()
}

# ---------------- 前置 ----------------
if (-not $Exe -or -not (Test-Path $Exe)) { throw "找不到 QuizSync.App.exe（用 -Exe 指定）" }
if (-not (Test-Path $log)) { throw "找不到应用日志：$log（先手动启动一次应用）" }
if (Get-Process 'QuizSync.App' -ErrorAction SilentlyContinue) {
    throw '已经有一个 QuizSync.App 在跑：本脚本**不替你杀进程**，请先自己关掉再跑'
}

$originalJson = $null
if (Test-Path $appJson) { $originalJson = Get-Content $appJson -Encoding UTF8 -Raw }

$app = $null
try {
    Write-Host "`n=== 启动应用（$Exe）===" -ForegroundColor Cyan
    $registeredBefore = CountOf '剪贴板监听：已注册'
    $app = Start-Process -FilePath $Exe -PassThru
    Check '应用起来后注册了剪贴板监听' (WaitForCount '剪贴板监听：已注册' ($registeredBefore + 1) 30) `
        "日志：$log"

    Write-Host "`n=== ① 放一张新图（Seed 7）===" -ForegroundColor Cyan
    $analyzedBefore = CountOf '剪贴板监听：识别新图片'
    $put = PutImage 7
    Check '把图片放进剪贴板成功' ($put -match '^OK \d+x\d+$') "$put"
    Check '放图后应用自己开始识别（静默）' (WaitForCount '剪贴板监听：识别新图片' ($analyzedBefore + 1) $WaitSeconds) `
        "「识别新图片」计数 $analyzedBefore → $(CountOf '剪贴板监听：识别新图片')"

    Write-Host "`n=== ② 再放同一张图（Seed 7，应按摘要去重）===" -ForegroundColor Cyan
    $afterFirst = CountOf '剪贴板监听：识别新图片'
    $put2 = PutImage 7
    Start-Sleep -Seconds 4
    Check '同一张图不会重复识别' ((CountOf '剪贴板监听：识别新图片') -eq $afterFirst) `
        "计数仍是 $afterFirst（放图结果：$put2）"

    Write-Host "`n=== ③ 负向对照：关掉「剪贴板监听」再放新图 ===" -ForegroundColor Cyan
    if ($originalJson) {
        # 直接改盘上的设置：应用每次事件都重新读一次（`AppSettings.Load()`），所以立刻生效。
        $off = $originalJson -replace '"clipboard_watch"\s*:\s*true', '"clipboard_watch": false'
        [System.IO.File]::WriteAllText($appJson, $off, (New-Object System.Text.UTF8Encoding($false)))
        Start-Sleep -Seconds 1
        $beforeOff = CountOf '剪贴板监听：识别新图片'
        $put3 = PutImage 9
        Start-Sleep -Seconds 6
        Check '关掉监听后放新图也不识别' ((CountOf '剪贴板监听：识别新图片') -eq $beforeOff) `
            "计数仍是 $beforeOff（放图结果：$put3）"
    }
    else {
        Check '负向对照（需要 app.json）' $false "找不到 $appJson"
    }
}
finally {
    # 设置**必须**改回去：不改回去等于把用户的开关偷偷关了。
    if ($originalJson) {
        [System.IO.File]::WriteAllText($appJson, $originalJson, (New-Object System.Text.UTF8Encoding($false)))
        Write-Host '  （已把 app.json 恢复原样）' -ForegroundColor DarkGray
    }

    if ($app -and -not $KeepRunning) {
        Start-Sleep -Milliseconds 300
        $app.CloseMainWindow() | Out-Null
        if (-not $app.WaitForExit(5000)) { $app.Kill() }
        Write-Host '  （已关闭本次启动的应用；要留着看界面就加 -KeepRunning）' -ForegroundColor DarkGray
    }
}

Write-Host ("`n结果：PASS {0} / FAIL {1}" -f $pass, $fail) -ForegroundColor $(if ($fail -eq 0) { 'Green' } else { 'Red' })
exit $(if ($fail -eq 0) { 0 } else { 1 })
