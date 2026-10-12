# 性能门禁：把 NATIVE-2.0-PLAN §13 那张表**逐项跑出数来**。
#
# 为什么需要它：§13 列了 15 行指标、每行都写了「测量方法」，但**一条脚本都没有** ——
# 于是「不达标就如实标不达标」这句话没有执行者。这个脚本就是那个执行者。
#
# 三个原则（都是这一程摔出来的）：
#  ① **先构建再测**：测到的必须是刚构建出来的东西（本仓踩过「三条绿线描述一份编译不过的代码」）。
#  ② **绝不碰用户数据**：应用用 `QS_APP_DATA` 指到临时目录，主机用 `--data` 指到临时目录。
#     踩过：探针把 `QS_APP_DATA` 指到空目录本身没错，但**忘了指**就会读到用户真实库。
#  ③ **测不了就写清原因**，不许编数字，也不许拿别的量凑（§13 原文：「没有基线就写未测」）。
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File tools\perf_gate.ps1
#   powershell -ExecutionPolicy Bypass -File tools\perf_gate.ps1 -SkipAndroid -SkipServer
# 退出码：0 = 跑完（**不代表全部达标** —— 达标与否看表里的「判定」列）；1 = 脚本自身出错。

param(
    [switch]$NoBuild,          # 复用已构建的产物（迭代时用；正式采集不要加）
    [switch]$SkipAndroid,
    [switch]$SkipServer,
    [int]$SteadySeconds = 45,  # 空闲内存/CPU 的采样时长
    [int]$Runs = 5,            # 冷启动取中位的次数
    [string]$Root = 'D:\DeepseekHarness\TemporaryConversationFile\perf'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$dotnet = if ($env:QS_DOTNET) { $env:QS_DOTNET } else { 'dotnet' }
$adb = 'D:\Windows\Apps\Android\Sdk\platform-tools\adb.exe'
$androidPkg = 'com.quizsync.android.native.dev'

# 前置检查：**本机的 dotnet 不在 PATH 上**（仓库惯例是走 `QS_DOTNET`，见 AGENTS.md）。
# 没有这道检查时，脚本会跑到「构建」那一步才炸，报的是 `CommandNotFoundException` ——
# 看起来像脚本坏了，其实是环境变量没设。
if (-not (Get-Command $dotnet -ErrorAction SilentlyContinue)) {
    Write-Host ("找不到 dotnet：{0}" -f $dotnet) -ForegroundColor Red
    Write-Host '本机请先设：$env:QS_DOTNET=''D:\Windows\Apps\DSH_Tools\dotnet\dotnet.exe''' -ForegroundColor Yellow
    exit 1
}

$appExe = Join-Path $repo 'windows\QuizSync.App\bin\Release\net10.0-windows10.0.26100.0\win-x64\QuizSync.App.exe'
$appOutDir = Split-Path -Parent $appExe
$serverRepo = $repo
$serverDll = Join-Path $serverRepo 'src\QuizSync.Server.Cli\bin\Release\publish\QuizSync.Server.Cli.dll'
$apkPath = Join-Path $repo 'android\app\build\outputs\apk\debug\app-debug.apk'

# 结果行：指标 / 契约 / 实测 / 判定 / 备注
$script:rows = New-Object System.Collections.Generic.List[object]
function Row([string]$name, [string]$contract, [string]$measured, [string]$verdict, [string]$note = '') {
    $script:rows.Add([pscustomobject]@{ 指标 = $name; 契约 = $contract; 实测 = $measured; 判定 = $verdict; 备注 = $note })
    $color = switch ($verdict) { '达标' { 'Green' } '不达标' { 'Red' } 'N/A' { 'DarkGray' } default { 'Yellow' } }
    Write-Host ("  {0,-34} 实测 {1,-22} [{2}] {3}" -f $name, $measured, $verdict, $note) -ForegroundColor $color
}

# ---------------------------------------------------------------------------
# 窗口测量（P/Invoke）：主窗口句柄 + 尺寸。
# 为什么不用 `$proc.MainWindowHandle` 了事：**悬浮球开着时它返回的是那个 96×96 的球**（踩过），
# 所以这里按「本进程可见的、面积最大的顶层窗口」判，和 verify_windows_ui.ps1 同一口径。
# ---------------------------------------------------------------------------
Add-Type -Namespace PerfGate -Name Win -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
public delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
'@

function Get-LargestWindow([int]$processId) {
    $script:bestHwnd = [IntPtr]::Zero
    $script:bestArea = 0
    $cb = [PerfGate.Win+EnumWindowsProc]{
        param($h, $l)
        $pid2 = 0
        [void][PerfGate.Win]::GetWindowThreadProcessId($h, [ref]$pid2)
        if ($pid2 -eq $processId -and [PerfGate.Win]::IsWindowVisible($h)) {
            $r = New-Object PerfGate.Win+RECT
            if ([PerfGate.Win]::GetWindowRect($h, [ref]$r)) {
                $area = ($r.Right - $r.Left) * ($r.Bottom - $r.Top)
                if ($area -gt $script:bestArea) { $script:bestArea = $area; $script:bestHwnd = $h }
            }
        }
        return $true
    }
    [void][PerfGate.Win]::EnumWindows($cb, [IntPtr]::Zero)
    return @{ Hwnd = $script:bestHwnd; Area = $script:bestArea }
}

function Stop-App {
    Get-Process 'QuizSync.App' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 600
}

function Median([double[]]$values) {
    $sorted = $values | Sort-Object
    $n = $sorted.Count
    if ($n -eq 0) { return 0 }
    if ($n % 2 -eq 1) { return $sorted[[int](($n - 1) / 2)] }
    return ($sorted[$n / 2 - 1] + $sorted[$n / 2]) / 2
}

# 原生命令输出：**不要用 `2>$null`** —— stderr 每行会变成 ErrorRecord，而本脚本是 Stop 策略，
# 第一行 stderr 就会终止整个脚本（AGENTS.md 记过这个坑）。统一走这个包装。
function Invoke-Native([string]$file, [string[]]$arguments) {
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $out = & $file @arguments 2>&1 | Where-Object { $_ -is [string] }
        return ($out | Out-String)
    } finally { $ErrorActionPreference = $prev }
}

Remove-Item -Recurse -Force $Root -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $Root | Out-Null

Write-Host "`n=== 0. 环境与构建 ===" -ForegroundColor Cyan
if (-not $NoBuild) {
    $build = Invoke-Native $dotnet @('build', (Join-Path $repo 'windows\QuizSync.App\QuizSync.App.csproj'),
        '-c', 'Release', '-v', 'q', '--nologo')
    if ($build -match ': error ') {
        Write-Host $build
        throw 'Release 构建失败 —— 性能数据必须来自刚构建的 Release 产物，本次不测。'
    }
    Write-Host '  Windows Release 构建：0 错误'
} else {
    Write-Host '  （-NoBuild：复用已有产物）'
}
if (-not (Test-Path $appExe)) { throw "Release 产物不存在：$appExe" }

$vs = [System.Diagnostics.Stopwatch]::StartNew()

# ---------------------------------------------------------------------------
# 一、Windows 客户端
# ---------------------------------------------------------------------------
Write-Host "`n=== 1. Windows 客户端（Release）===" -ForegroundColor Cyan

# W1 冷启动：进程启动 → 主窗口可见（面积最大的那个窗口出现）。
# ⚠️ 口径说明：这是「窗口句柄出现」而不是像素级「首帧画完」，比 §13 的字面口径**偏乐观**；
#    真正的首帧要么靠 ETW/directwrite 埋点，要么靠录屏逐帧 —— 都不在本轮能力内，如实标注。
$cold = @()
foreach ($i in 1..$Runs) {
    Stop-App
    $appData = Join-Path $Root ("appdata-start-$i")
    New-Item -ItemType Directory -Force -Path $appData | Out-Null
    # ⚠️ 服务端目录**也要**指到临时目录（第 170 轮）：只指 app 目录时，这个空实例会去
    # 跟**真实主机**配对，而服务端每次配对都换令牌并覆盖 devices.token_hash ——
    # 用户那份 device.token 当场作废（表现：推送 401、手机再也不同步，而且界面上看不出来）。
    $env:QS_APP_DATA = $appData
    $env:QS_SERVER_DATA = Join-Path $appData 'server'
    New-Item -ItemType Directory -Force -Path (Join-Path $appData 'server') | Out-Null
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $proc = Start-Process -FilePath $appExe -PassThru
    $seenAt = -1
    while ($sw.ElapsedMilliseconds -lt 20000) {
        $w = Get-LargestWindow $proc.Id
        if ($w.Area -gt 400 * 300) { $seenAt = $sw.Elapsed.TotalMilliseconds; break }
        Start-Sleep -Milliseconds 25
    }
    $sw.Stop()
    Remove-Item Env:\QS_APP_DATA -ErrorAction SilentlyContinue
    Remove-Item Env:\QS_SERVER_DATA -ErrorAction SilentlyContinue
    if ($seenAt -gt 0) { $cold += $seenAt }
    Write-Host ("    第 {0} 次：{1:N0} ms" -f $i, $seenAt)
}
if ($cold.Count -gt 0) {
    $med = Median $cold
    Row 'Windows 冷启动（进程→主窗口可见）' '≤1.5s' ("{0:N0} ms（{1} 次中位）" -f $med, $cold.Count) `
        $(if ($med -le 1000) { '达标' } elseif ($med -le 1500) { '仅达契约' } else { '不达标' }) `
        '口径比「首帧画完」偏乐观'
} else {
    Row 'Windows 冷启动' '≤1.5s' '测不到' 'N/A' '5 次都没抓到主窗口'
}

# W2 空闲内存：稳态 $SteadySeconds 秒后的工作集（§13 的基线是 Flutter 版 277MB）。
Stop-App
$appData = Join-Path $Root 'appdata-idle'
New-Item -ItemType Directory -Force -Path $appData | Out-Null
$env:QS_APP_DATA = $appData
$env:QS_SERVER_DATA = Join-Path $appData 'server'   # 同上：别让它去配真实主机
New-Item -ItemType Directory -Force -Path (Join-Path $appData 'server') | Out-Null
$proc = Start-Process -FilePath $appExe -PassThru
Remove-Item Env:\QS_APP_DATA -ErrorAction SilentlyContinue
Remove-Item Env:\QS_SERVER_DATA -ErrorAction SilentlyContinue
Start-Sleep -Seconds $SteadySeconds
$proc.Refresh()
$rss = $proc.WorkingSet64 / 1MB
$priv = $proc.PrivateMemorySize64 / 1MB
Row 'Windows 空闲内存（工作集）' '≤150MB' ("{0:N0} MB（私有 {1:N0} MB）" -f $rss, $priv) `
    $(if ($rss -le 150) { '达标' } else { '不达标' }) `
    ("稳态 {0}s；Flutter 1.1.0 基线 277MB" -f $SteadySeconds)
Stop-App

# W3 便携包体积：2.0 还没有打包脚本（Phase 8 的事），先用**构建输出目录**近似。
$size = (Get-ChildItem -Recurse -File $appOutDir | Measure-Object -Property Length -Sum).Sum / 1MB
Row 'Windows 分发包体积' '≤80MB' ("{0:N0} MB（构建输出目录）" -f $size) `
    $(if ($size -le 80) { '达标' } else { '不达标' }) '尚未有 2.0 打包脚本，用产物目录近似'

# W4/W5/W6：§13 里需要埋点或**在 2.0 没有对等物**的三行 —— 如实标 N/A（不编数字）。
Row 'Windows 点击→开始识别' '≤800ms' '未测' 'N/A' '热键/按钮路径没有耗时日志埋点；要补一行日志才能采'
Row 'Windows 取帧（GDI）' '≤300ms' '未测' 'N/A' '截屏路径没有耗时日志埋点'
# ⚠️ 这一行的理由第 166 轮更正过：悬浮窗**已经做完**（probe_float_window.ps1 7 PASS），
#    所以原来是「悬浮窗本身还没做」已经过期。真正的原因是**指标没有对等物**：
#    1.1.0 的悬浮窗是「离屏渲染 + 分层窗口合成」，那一套才有"内容帧"；
#    2.0 换成**原生 XAML 窗口**（WinUI 自己排版与合成），所以按 1.1.0 的口径测不出来。
Row '悬浮窗内容帧' '≤16ms' '未测' 'N/A' '2.0 的悬浮窗是原生 XAML 窗口，与 1.1.0 的离屏合成无对等物；行为验收见 probe_float_window.ps1（7 PASS）'

# ---------------------------------------------------------------------------
# 二、主机服务端
# ---------------------------------------------------------------------------
$serverProc = $null
$providerProc = $null
if (-not $SkipServer) {
    Write-Host "`n=== 2. 主机服务端（C#，端口 8792，数据目录在临时区）===" -ForegroundColor Cyan

    if (-not (Test-Path $serverDll)) {
        Write-Host '  发布服务端…'
        $pub = Invoke-Native $dotnet @('publish',
            (Join-Path $serverRepo 'src\QuizSync.Server.Cli'), '-c', 'Release',
            '-o', (Join-Path $serverRepo 'src\QuizSync.Server.Cli\bin\Release\publish'))
        if ($pub -match ': error ') { throw "服务端发布失败：`n$pub" }
    }

    $port = 8792
    $serverDir = Join-Path $Root 'server'
    New-Item -ItemType Directory -Force -Path $serverDir | Out-Null
    $serverOut = Join-Path $Root 'server.out.log'
    $serverErr = Join-Path $Root 'server.err.log'

    # S1 启动 → /api/v1/info 可用
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $serverProc = Start-Process -FilePath $dotnet `
        -ArgumentList @($serverDll, 'run', '--port', $port, '--data', $serverDir) `
        -PassThru -NoNewWindow -RedirectStandardOutput $serverOut -RedirectStandardError $serverErr
    $readyAt = -1
    while ($sw.ElapsedMilliseconds -lt 30000) {
        $info = Invoke-Native 'curl.exe' @('-s', '-m', '2', "http://127.0.0.1:$port/api/v1/info")
        if ($info -match '"') { $readyAt = $sw.Elapsed.TotalMilliseconds; break }
        Start-Sleep -Milliseconds 30
    }
    $sw.Stop()
    if ($readyAt -gt 0) {
        Row 'Server 启动 → /info 可用' '≤200ms' ("{0:N0} ms" -f $readyAt) `
            $(if ($readyAt -le 200) { '达标' } else { '不达标' }) '含进程启动 + 端口绑定 + 建库'
    } else {
        Row 'Server 启动 → /info 可用' '≤200ms' '30s 内没起来' 'N/A' "看 $serverErr"
    }

    # S2/S3 空闲内存与 CPU：稳态采样
    Start-Sleep -Seconds $SteadySeconds
    $serverProc.Refresh()
    $sRss = $serverProc.WorkingSet64 / 1MB
    $cpu1 = $serverProc.TotalProcessorTime.TotalMilliseconds
    $wall = [System.Diagnostics.Stopwatch]::StartNew()
    Start-Sleep -Seconds 10
    $wall.Stop()
    $serverProc.Refresh()
    $cpu2 = $serverProc.TotalProcessorTime.TotalMilliseconds
    $cores = [Environment]::ProcessorCount
    $cpuPct = (($cpu2 - $cpu1) / $wall.Elapsed.TotalMilliseconds) * 100 / $cores
    Row 'Server 空闲 RSS' '≤40MB' ("{0:N0} MB" -f $sRss) `
        $(if ($sRss -le 40) { '达标' } else { '不达标' }) ("稳态 {0}s" -f $SteadySeconds)
    Row 'Server 空闲 CPU' '≤0.5%' ("{0:N2} %（{1} 核归一）" -f $cpuPct, $cores) `
        $(if ($cpuPct -le 0.5) { '达标' } else { '不达标' }) '10s 采样窗口'

    # S4 无 Provider 时任务响应：配对 → 上传 → 建任务 → 轮询到 provider_offline
    $control = (Get-Content (Join-Path $serverDir 'control.token')).Trim()
    $pairCode = (Invoke-Native 'curl.exe' @('-s', '-H', "X-QS-Control: $control",
        "http://127.0.0.1:$port/api/v1/pair/code") | ConvertFrom-Json)
    $bodyDir = Join-Path $Root 'body'
    New-Item -ItemType Directory -Force -Path $bodyDir | Out-Null
    $pairBody = Join-Path $bodyDir 'pair.json'
    @{ code = $pairCode.code; device_id = 'perf-client'; device_name = '性能门禁'
       platform = 'android'; app_version = '2.0.0' } |
        ConvertTo-Json -Compress | Set-Content -Encoding UTF8 $pairBody
    $pair = (Invoke-Native 'curl.exe' @('-s', '-X', 'POST', "http://127.0.0.1:$port/api/v1/pair",
        '-H', 'Content-Type: application/json; charset=utf-8', '--data-binary', "@$pairBody") | ConvertFrom-Json)
    $token = $pair.token
    if ($token) {
        # 上传一张**真的 JPEG**：用 System.Drawing 现场生成 200×200 白图。
        # ⚠️ 踩过一次：第一版手搓了一段 1×1 JPEG 的字节数组，服务端不收 —— 于是
        #    「无 Provider 时任务响应」那行只能记成 N/A。性能表上看起来像「这项测不了」，
        #    其实是**测量脚本自己的图不合法**。测不到和测不了必须分清。
        $jpg = Join-Path $bodyDir 'probe.jpg'
        Add-Type -AssemblyName System.Drawing
        $bmp = New-Object System.Drawing.Bitmap 200, 200
        $graphics = [System.Drawing.Graphics]::FromImage($bmp)
        $graphics.Clear([System.Drawing.Color]::White)
        $bmp.Save($jpg, [System.Drawing.Imaging.ImageFormat]::Jpeg)
        $graphics.Dispose()
        $bmp.Dispose()
        # 上传走 **multipart/form-data**（协议 `07-images`：字段名 `file`）。
        # ⚠️ 这一行踩了两次坑，两次都让本行变成 N/A —— 而 N/A 看起来像「这项测不了」，其实是脚本错：
        #    ① 手搓的 1×1 JPEG 本身不合法；② 图合法但用 `--data-binary` 发裸 body，
        #    服务端如实回 `invalid_request: 需要 multipart/form-data`。
        $uploadRaw = Invoke-Native 'curl.exe' @('-s', '-X', 'POST', "http://127.0.0.1:$port/api/v1/images",
            '-H', "Authorization: Bearer $token", '-F', "file=@$jpg;type=image/jpeg")
        $upload = $null
        try { $upload = ($uploadRaw | Out-String).Trim() | ConvertFrom-Json } catch { $upload = $null }
        # ⚠️ 字段名是 **`image_hash`** 不是 `hash` —— 上传响应用前者、图片实体用后者，
        # 这处不对称是 v1 的既成事实（`versions/compat-matrix.md` 待决策 C29 记着它）。
        # 我第一版读了 `.hash`，于是上传明明成功、这一行还是 N/A。
        if ($upload -and $upload.image_hash) {
            # 建任务**必须先有活跃合集**（协议 `08-tasks` §1.6：没有 → `409 no_active_collection`）。
            # 配方照抄已经验过的那条链：推一条 collection op → 切到它 → 再建任务。
            $nowMs = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
            $opsBody = Join-Path $bodyDir 'ops.json'
            @{ ops = @(@{
                        op_id       = [guid]::NewGuid().ToString()
                        device_id   = 'perf-client'
                        lamport     = 1
                        entity      = 'collection'
                        entity_id   = 'c-perf'
                        op_type     = 'upsert'
                        fields_json = @{ name = '性能门禁'; created_at = $nowMs; updated_at = $nowMs; updated_by = 'perf-client' }
                    }) } | ConvertTo-Json -Depth 6 -Compress | Set-Content -Encoding UTF8 $opsBody
            $null = Invoke-Native 'curl.exe' @('-s', '-X', 'POST', "http://127.0.0.1:$port/api/v1/sync/ops",
                '-H', 'Content-Type: application/json; charset=utf-8', '-H', "Authorization: Bearer $token",
                '--data-binary', "@$opsBody")
            $null = Invoke-Native 'curl.exe' @('-s', '-X', 'POST',
                "http://127.0.0.1:$port/api/v1/collections/c-perf/select", '-H', "Authorization: Bearer $token")

            # `image_hash`（单数）+ `source_device`：与验过的脚本一致。
            $taskBody = Join-Path $bodyDir 'task.json'
            @{ task_id = 'perf-offline'; image_hash = $upload.image_hash; source_device = 'perf-client' } |
                ConvertTo-Json -Compress | Set-Content -Encoding UTF8 $taskBody
            $sw = [System.Diagnostics.Stopwatch]::StartNew()
            $createRaw = Invoke-Native 'curl.exe' @('-s', '-X', 'POST', "http://127.0.0.1:$port/api/v1/tasks",
                '-H', "Authorization: Bearer $token", '-H', 'Content-Type: application/json; charset=utf-8',
                '--data-binary', "@$taskBody")
            $offlineAt = -1
            while ($sw.ElapsedMilliseconds -lt 10000) {
                $t = Invoke-Native 'curl.exe' @('-s', '-H', "Authorization: Bearer $token",
                    "http://127.0.0.1:$port/api/v1/tasks/perf-offline")
                if ($t -match 'provider_offline') { $offlineAt = $sw.Elapsed.TotalMilliseconds; break }
                Start-Sleep -Milliseconds 40
            }
            $sw.Stop()
            if ($offlineAt -gt 0) {
                Row '无 Provider 时任务响应' '≤2s' ("{0:N0} ms 返回 provider_offline" -f $offlineAt) `
                    $(if ($offlineAt -le 2000) { '达标' } else { '不达标' }) '从提交任务算起（含建任务往返）'
            } else {
                Row '无 Provider 时任务响应' '≤2s' '10s 内没有 provider_offline' 'N/A' `
                    ('建任务响应：' + (($createRaw | Out-String).Trim() -replace '\s+', ' '))
            }
        } else {
            Row '无 Provider 时任务响应' '≤2s' '上传失败' 'N/A' `
                ('服务端回了：' + (($uploadRaw | Out-String).Trim() -replace '\s+', ' '))
        }
    } else {
        Row '无 Provider 时任务响应' '≤2s' '配对失败' 'N/A' '拿不到令牌'
    }
} else {
    Write-Host "`n=== 2. 主机服务端：跳过（-SkipServer）===" -ForegroundColor DarkGray
}

# ---------------------------------------------------------------------------
# 三、Android（真机）
# ---------------------------------------------------------------------------
if (-not $SkipAndroid) {
    Write-Host "`n=== 3. Android（真机 $androidPkg）===" -ForegroundColor Cyan
    $devices = Invoke-Native $adb @('devices')
    if ($devices -notmatch "$androidPkg|device\s*$") {
        Write-Host '  没有设备在线，整段跳过' -ForegroundColor Yellow
    } else {
        # A1 冷启动：am start -W 的 TotalTime（**口径是代理**，不是 Macrobenchmark 的 StartupTimingMetric）
        $starts = @()
        foreach ($i in 1..$Runs) {
            $null = Invoke-Native $adb @('-s', 'e5b1641d', 'shell', 'am', 'force-stop', $androidPkg)
            Start-Sleep -Milliseconds 400
            $out = Invoke-Native $adb @('-s', 'e5b1641d', 'shell', 'am', 'start', '-W', '-n',
                "$androidPkg/.MainActivity")
            $m = [regex]::Match($out, 'TotalTime:\s*(\d+)')
            if ($m.Success) { $starts += [double]$m.Groups[1].Value }
            Write-Host ("    第 {0} 次：{1}" -f $i, $m.Groups[1].Value + ' ms')
            Start-Sleep -Milliseconds 300
        }
        if ($starts.Count -gt 0) {
            $med = Median $starts
            Row 'Android 冷启动' '先采基线' ("{0:N0} ms（am start -W 中位）" -f $med) '基线' `
                '代理口径：不含 Application 之前的部分；Macrobenchmark 未接'
        } else {
            Row 'Android 冷启动' '先采基线' '解析不到 TotalTime' 'N/A' '检查 adb 输出'
        }

        # A2 内存：dumpsys meminfo 的 TOTAL PSS
        Start-Sleep -Seconds 3
        $mem = Invoke-Native $adb @('-s', 'e5b1641d', 'shell', 'dumpsys', 'meminfo', $androidPkg)
        $mm = [regex]::Match($mem, 'TOTAL PSS:\s*(\d+)')
        $pss = if ($mm.Success) { [double]$mm.Groups[1].Value / 1024 } else { 0 }
        Row 'Android 内存（PSS）' '≤200MB' ("{0:N0} MB" -f $pss) `
            $(if ($pss -eq 0) { 'N/A' } elseif ($pss -le 200) { '达标' } else { '不达标' }) `
            'dumpsys meminfo（不是 Profiler 的 Java heap）'

        # A3 APK 体积
        if (Test-Path $apkPath) {
            $apkMb = (Get-Item $apkPath).Length / 1MB
            Row 'APK 体积（debug）' '单 ABI ≤40MB' ("{0:N0} MB" -f $apkMb) `
                $(if ($apkMb -le 40) { '达标' } else { '不达标' }) '2.0 还没有 release 打包；debug 体积偏大'
        } else {
            Row 'APK 体积' '单 ABI ≤40MB' '没有构建产物' 'N/A' '先跑 gradle :app:assembleDebug'
        }

        # A4 列表滚动掉帧：dumpsys gfxinfo 的 Janky frames（**代理**，要求列表真的能滚）
        $null = Invoke-Native $adb @('-s', 'e5b1641d', 'shell', 'dumpsys', 'gfxinfo', $androidPkg, 'reset')
        foreach ($i in 1..3) {
            $null = Invoke-Native $adb @('-s', 'e5b1641d', 'shell', 'input', 'swipe', '900', '1800', '900', '900', '200')
            Start-Sleep -Milliseconds 500
        }
        $gfx = Invoke-Native $adb @('-s', 'e5b1641d', 'shell', 'dumpsys', 'gfxinfo', $androidPkg)
        # ⚠️ 必须同时读**总帧数**：第一版只读「Janky frames: 1 (7.69%)」就判成「不达标」——
        #    而 7.69% = 13 帧里的 1 帧，是噪声不是结论。帧数不够时只能记 N/A。
        $jm = [regex]::Match($gfx, 'Janky frames:\s*(\d+)\s*\(([\d\.]+)%\)')
        $tm = [regex]::Match($gfx, 'Total frames rendered:\s*(\d+)')
        if ($jm.Success -and $tm.Success) {
            $jank = [double]$jm.Groups[2].Value
            $janky = [int]$jm.Groups[1].Value
            $total = [int]$tm.Groups[1].Value
            if ($total -lt 100) {
                Row 'Android 列表滚动掉帧' '<1%' ("样本不足：只渲染了 {0} 帧（卡顿 {1}）" -f $total, $janky) 'N/A' `
                    '列表滚不动（历史页数据太少）或首屏静态帧占多数 —— 帧数不到 100 的百分比不可作判据'
            } else {
                Row 'Android 列表滚动掉帧' '<1%' ("{0}% ({1}/{2} 帧卡顿)" -f $jank, $janky, $total) `
                    $(if ($jank -lt 1) { '达标' } else { '不达标' }) `
                    '代理口径：gfxinfo 不是 Macrobenchmark'
            }
        } else {
            Row 'Android 列表滚动掉帧' '<1%' 'gfxinfo 没有帧统计' 'N/A' '需要真的滚动过列表（本机历史页可能不可滚）'
        }

        Row 'Android 截屏取帧' '≤500ms 且有超时' '未测' 'N/A' '需真机手势 + 悬浮球权限 + logcat 埋点'
    }
} else {
    Write-Host "`n=== 3. Android：跳过（-SkipAndroid）===" -ForegroundColor DarkGray
}

# ---------------------------------------------------------------------------
# 收尾：停掉自己起的进程，打印表
# ---------------------------------------------------------------------------
if ($serverProc -and -not $serverProc.HasExited) { $serverProc.Kill() }
if ($providerProc -and -not $providerProc.HasExited) { $providerProc.Kill() }
Stop-App
$vs.Stop()

Write-Host "`n=== §13 性能表（实测）===" -ForegroundColor Cyan
$script:rows | Format-Table -AutoSize | Out-String -Width 200 | Write-Host

$ok = ($script:rows | Where-Object { $_.判定 -eq '达标' }).Count
$bad = ($script:rows | Where-Object { $_.判定 -eq '不达标' }).Count
$na = ($script:rows | Where-Object { $_.判定 -eq 'N/A' }).Count
Write-Host ("达标 {0} / 不达标 {1} / N/A {2}（总 {3} 行）；整轮耗时 {4:N0}s" -f `
    $ok, $bad, $na, $script:rows.Count, $vs.Elapsed.TotalSeconds)

$md = Join-Path $Root 'perf-table.md'
$lines = @('# §13 性能门禁实测（' + (Get-Date -Format 'yyyy-MM-dd HH:mm') + '）', '',
    '| 指标 | 契约 | 实测 | 判定 | 备注 |', '|---|---|---|---|---|')
foreach ($r in $script:rows) { $lines += ("| {0} | {1} | {2} | {3} | {4} |" -f $r.指标, $r.契约, $r.实测, $r.判定, $r.备注) }
$lines | Set-Content -Encoding UTF8 $md
Write-Host "表格已写入 $md"
exit 0
