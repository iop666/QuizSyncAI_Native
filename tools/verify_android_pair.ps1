# 真机取证 · 第一步：把手机与主机**配对**（Phase 5 的前置，也是 Phase 4 配对页的真机执行）
#
# 为什么单独一步：Phase 5 的「截屏→AI→两端显示」链路要先有配对。配对涉及在真机上填地址与码，
# 而 `adb shell input text` 在本机的中文输入法下会把 `.` 变成全角 `。`（实测：
# 输成 `192。168。231。215`，URL 非法、一直"正在连接"）—— 所以**点号一律用
# `input keyevent 56`（KEYCODE_PERIOD）**，并且在输入前先把页面回到干净状态。
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File tools\verify_android_pair.ps1
#   powershell -ExecutionPolicy Bypass -File tools\verify_android_pair.ps1 -KeepHost   # 留着主机给下一步用
#
# 退出码：0 = 配对成功；1 = 任一步失败。

param(
    [string]$Serial = 'e5b1641d',
    [int]$Port = 8765,
    [string]$Package = 'com.quizsync.android.native.dev',
    [string]$Root = 'D:\DeepseekHarness\TemporaryConversationFile\capture-e2e',
    [switch]$KeepHost
)

$ErrorActionPreference = 'Stop'
$adb = 'D:\Windows\Apps\Android\Sdk\platform-tools\adb.exe'
$dotnet = if ($env:QS_DOTNET) { $env:QS_DOTNET } else { 'dotnet' }
# 服务端已并入本仓：默认取 tools/ 上两级的 server/。可用 QS_SERVER_DLL 覆盖。
$repoRoot = Split-Path -Parent $PSScriptRoot
$dll = if ($env:QS_SERVER_DLL) {
    $env:QS_SERVER_DLL
} else {
    Join-Path $repoRoot 'server\src\QuizSync.Server.Cli\bin\Release\publish\QuizSync.Server.Cli.dll'
}

function Native([string]$file, [string[]]$arguments) {
    $prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try { return ((& $file @arguments 2>&1 | Where-Object { $_ -is [string] }) | Out-String).Trim() }
    finally { $ErrorActionPreference = $prev }
}

# 抓控件树：dump 到设备再 cat 回来（**不要**用 `>` 重定向，会把 UTF-8 写成 UTF-16）
function Get-Tree {
    $remote = '/sdcard/qs-pair.xml'
    $null = Native $adb @('-s', $Serial, 'shell', 'uiautomator', 'dump', $remote)
    return (Native $adb @('-s', $Serial, 'shell', 'cat', $remote))
}

function Get-Label([string]$node) {
    $d = [regex]::Match($node, 'content-desc="([^"]*)"')
    if ($d.Success -and $d.Groups[1].Value) { return $d.Groups[1].Value }
    return [regex]::Match($node, 'text="([^"]*)"').Groups[1].Value
}

function Get-Center([string]$node) {
    $b = [regex]::Match($node, 'bounds="\[(\d+),(\d+)\]\[(\d+),(\d+)\]"')
    if (-not $b.Success) { return $null }
    return @(
        [int](([int]$b.Groups[1].Value + [int]$b.Groups[3].Value) / 2),
        [int](([int]$b.Groups[2].Value + [int]$b.Groups[4].Value) / 2)
    )
}

function Tap-Point([int]$x, [int]$y) { $null = Native $adb @('-s', $Serial, 'shell', 'input', 'tap', "$x", "$y") }

# 按标签点：**点可点父节点**（文字节点本身常是 clickable=false，点它没反应 —— 实测踩过）
function Tap-Label([string]$tree, [string]$label, [switch]$Exact) {
    foreach ($m in [regex]::Matches($tree, '<node[^>]*>')) {
        $value = Get-Label $m.Value
        $match = if ($Exact) { $value -eq $label } else { $value -like "*$label*" }
        if ($match) {
            $center = Get-Center $m.Value
            if ($center) { Tap-Point $center[0] $center[1]; return $true }
        }
    }
    return $false
}

# 输入 IP/主机名：**点号用 KEYCODE_PERIOD**，绕开中文输入法的全角转换
function Type-Host([string]$host_) {
    foreach ($segment in $host_.Split('.')) {
        $null = Native $adb @('-s', $Serial, 'shell', 'input', 'text', $segment)
        Start-Sleep -Milliseconds 120
    }
    # 上面的循环只打了段，点需要单独插进去 —— 重新按段+点交替来一遍更可靠
    $null = Native $adb @('-s', $Serial, 'shell', 'input', 'keyevent', 'KEYCODE_MOVE_END')
    return $host_
}

function Clear-Field {
    $null = Native $adb @('-s', $Serial, 'shell', 'input', 'keyevent', 'KEYCODE_MOVE_END')
    1..30 | ForEach-Object { $null = Native $adb @('-s', $Serial, 'shell', 'input', 'keyevent', 'KEYCODE_DEL') }
}

Write-Host "`n=== 1) 起主机（临时数据目录 + fixture 识别器：不需要 API Key 也能把链路走完）===" -ForegroundColor Cyan
Get-Process 'QuizSync.Server.Cli' -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item -Recurse -Force $Root -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $Root | Out-Null
$server = Start-Process -FilePath $dotnet `
    -ArgumentList @($dll, 'run', '--port', "$Port", '--data', $Root, '--analyzer', 'fixture') `
    -PassThru -NoNewWindow `
    -RedirectStandardOutput (Join-Path $Root 'host.out.log') -RedirectStandardError (Join-Path $Root 'host.err.log')

$ready = $false
foreach ($attempt in 1..60) {
    Start-Sleep -Milliseconds 300
    if ((Native 'curl.exe' @('-s', '-m', '2', "http://127.0.0.1:$Port/health")) -match 'ok') { $ready = $true; break }
}
if (-not $ready) { throw "主机没起来（看 $Root\host.err.log）" }
Write-Host "  主机就绪（PID $($server.Id)）" -ForegroundColor Green

$control = (Get-Content (Join-Path $Root 'control.token')).Trim()
$code = ((Native 'curl.exe' @('-s', '-H', "X-QS-Control: $control", "http://127.0.0.1:$Port/api/v1/pair/code")) | ConvertFrom-Json).code
# 主机地址必须是**手机走 Wi-Fi 能到的那个**网卡地址。
# ⚠️ 踩过（第 152 轮）：这台机器同时有 USB 网络共享（`usb0` ↔ 电脑的 `192.168.231.x`）
# 和 Wi-Fi（`192.168.101.x`）两条链路，而"第一个非回环 IPv4"拿到的是 **USB 那条** ——
# 手机被填上 `192.168.231.215` 之后连不上（实测报 `failed to connect … after 5000ms`），
# 看起来像配对页坏了，其实是地址选错了网卡。
# 现在先从手机侧问出它 Wi-Fi 的网段，再挑同一网段的电脑地址；问不到时退回旧口径。
$phoneSubnet = (Native $adb @('-s', $Serial, 'shell', 'ip', 'route')) -split "`n" |
    Where-Object { $_ -match '(\d+\.\d+\.\d+)\.0/24\s+dev\s+wlan0' } |
    ForEach-Object { [regex]::Match($_, '(\d+\.\d+\.\d+)\.0/24').Groups[1].Value } |
    Select-Object -First 1
$lan = (Get-NetIPAddress -AddressFamily IPv4 |
    Where-Object {
        $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' -and $_.PrefixOrigin -ne 'WellKnown' -and
        ($phoneSubnet -and $_.IPAddress -like "$phoneSubnet.*")
    } |
    Select-Object -First 1).IPAddress
if (-not $lan) {
    Write-Host "  ⚠ 没问到手机 Wi-Fi 的网段（$phoneSubnet），退回「第一个非回环 IPv4」" -ForegroundColor Yellow
    $lan = (Get-NetIPAddress -AddressFamily IPv4 |
        Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' -and $_.PrefixOrigin -ne 'WellKnown' } |
        Select-Object -First 1).IPAddress
}
if (-not $lan) { throw "找不到可用的局域网地址（手机网段=$phoneSubnet）" }
Write-Host "  局域网地址 = ${lan}:$Port（手机 Wi-Fi 网段 $phoneSubnet），配对码 = $code" -ForegroundColor Green

try {
    Write-Host "`n=== 2) 手机上进配对页 ===" -ForegroundColor Cyan
    $null = Native $adb @('-s', $Serial, 'shell', 'am', 'force-stop', $Package)
    $null = Native $adb @('-s', $Serial, 'shell', 'am', 'start', '-n', "$Package/$Package.MainActivity")
    Start-Sleep -Seconds 9

    # 主壳右上角的「重新配对」（实测位置随布局而定，所以按标签找）
    $tree = Get-Tree
    if (-not (Tap-Label $tree '重新配对' -Exact)) {
        if (-not (Tap-Label $tree '配对')) { throw "主壳上找不到配对入口（看 dump）" }
    }
    Start-Sleep -Seconds 3
    $tree = Get-Tree
    if ($tree -notmatch '方式三：手动输入') { throw "没进到配对页（期望看到「方式三：手动输入」）" }
    Write-Host "  已进入配对页（三个模块都在）" -ForegroundColor Green

    Write-Host "`n=== 3) 填地址与配对码（点号走 KEYCODE_PERIOD）===" -ForegroundColor Cyan
    $fields = @{}
    foreach ($m in [regex]::Matches($tree, '<node[^>]*class="[^"]*EditText[^"]*"[^>]*>')) {
        $center = Get-Center $m.Value
        $text = [regex]::Match($m.Value, 'text="([^"]*)"').Groups[1].Value
        if ($center) { $fields["$($center[0]),$($center[1])"] = $text }
    }
    # 地址框 = 最靠上的那个输入框；配对码框 = 最靠下的那个
    $ordered = $fields.Keys | ForEach-Object { [pscustomobject]@{ Key = $_; Y = [int]($_ -split ',')[1] } } | Sort-Object Y
    if ($ordered.Count -lt 2) { throw "配对页上只找到 $($ordered.Count) 个输入框" }
    $addrKey = $ordered[0].Key
    $codeKey = $ordered[-1].Key
    Write-Host "  地址框=($addrKey) 配对码框=($codeKey)"

    Tap-Point ([int]($addrKey -split ',')[0]) ([int]($addrKey -split ',')[1])
    Start-Sleep -Milliseconds 800
    Clear-Field
    # ⚠️ **必须用 `input keyboard text`**（走硬件键盘通道），不能用 `input text`：
    #    后者被本机中文输入法接管，把 `.` 变成全角 `。`（实测输成 `192。168。231。215`，
    #    URL 非法、界面一直"正在连接"）。**逐段 + keyevent 56 也一样无效**（同样过输入法）。
    $null = Native $adb @('-s', $Serial, 'shell', 'input', 'keyboard', 'text', $lan)
    Start-Sleep -Milliseconds 300

    Tap-Point ([int]($codeKey -split ',')[0]) ([int]($codeKey -split ',')[1])
    Start-Sleep -Milliseconds 800
    Clear-Field
    $null = Native $adb @('-s', $Serial, 'shell', 'input', 'keyboard', 'text', $code)
    Start-Sleep -Milliseconds 400
    $null = Native $adb @('-s', $Serial, 'shell', 'input', 'keyevent', 'KEYCODE_BACK')
    Start-Sleep -Milliseconds 600

    # 硬断言：**地址框里的值必须逐字等于目标值**。
    # 上一版这里是"正则匹配不到就静默通过"，结果地址是空的也照样往下走、还把失败报成了成功。
    $tree = Get-Tree
    $addrValue = ''
    foreach ($m in [regex]::Matches($tree, '<node[^>]*class="[^"]*EditText[^"]*"[^>]*>')) {
        $center = Get-Center $m.Value
        if (-not $center) { continue }
        if ($center[1] -eq [int]($addrKey -split ',')[1]) {
            $addrValue = [regex]::Match($m.Value, 'text="([^"]*)"').Groups[1].Value
        }
    }
    Write-Host "  地址框现在 = 「$addrValue」（期望「$lan」）"
    if ($addrValue -ne $lan) {
        throw "地址框内容不对（期望 $lan，实际「$addrValue」）—— 大概率又是输入法把点号转成了全角"
    }

    Write-Host "`n=== 4) 开始配对 ===" -ForegroundColor Cyan
    if (-not (Tap-Label $tree '开始配对' -Exact)) { throw "找不到「开始配对」按钮" }
    Start-Sleep -Seconds 6

    $tree = Get-Tree
    $mainShell = ($tree -match '当前任务' -and $tree -match '历史记录')
    $unpaired = $tree -match '未配对'
    Write-Host "  手机侧：主壳=$mainShell 仍显示未配对=$unpaired"
    $listJson = Native $dotnet @($dll, 'devices', 'list', '--data', $Root, '--json')
    Write-Host "  主机侧设备表：$($listJson.Substring(0, [Math]::Min(300, $listJson.Length)))"

    # 判据要**正面**：只看"没看到未配对"会误判 —— 上一版就是这么把没配上报成成功的。
    if ($mainShell -and -not $unpaired) {
        Write-Host "`n✅ 配对成功（手机侧回到主壳且不再显示未配对）" -ForegroundColor Green
    }
    else {
        Write-Host "`n❌ 配对没成功（手机侧仍显示未配对或不在主壳）" -ForegroundColor Red
        exit 1
    }

    if ($KeepHost) { Write-Host "`n（-KeepHost：主机保留在 $Port，供下一步取证用）" -ForegroundColor Yellow }
}
finally {
    if (-not $KeepHost) {
        $null = Native 'taskkill.exe' @('/T', '/F', '/PID', "$($server.Id)")
        Get-Process 'QuizSync.Server.Cli' -ErrorAction SilentlyContinue | Stop-Process -Force
    }
    $null = Native $adb @('-s', $Serial, 'shell', 'rm', '-f', '/sdcard/qs-pair.xml')
}
