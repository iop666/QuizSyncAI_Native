# 真机配对（**已跑通**的配方）—— Phase 5 取证的前置
#
# 这条路上踩了 5 个坑，全部记在这里，别再重走：
#
# ① **选对网段**（最致命的一个）：本机有多张网卡（以太网 5 = 192.168.231.215、以太网 = 192.168.101.23、
#    WLAN = 192.168.101.22），而手机在 **192.168.101.84/24**。我用"第一个非回环 IPv4"取到了
#    192.168.231.215 —— **跟手机根本不在一个网段**，于是怎么试都连不上。
#    正确做法：从手机读 `ip -4 addr show wlan0` 拿它的网段，再在**本机同网段的地址**里挑一个。
# ② **不要在应用里打字**：MIUI 的 `input` 通道会把 `.` 变成全角 `。`（`input text`、
#    `input keyboard text`、逐字符 `keyevent` 三种都中招）→ IP 永远输不对。
# ③ 用 **deep link 免打字**：应用注册了 `quizsync://pair?host=…&port=…&code=…`
#    （`AndroidManifest.xml` 里 `android:scheme="quizsync" android:host="pair"`），
#    `am start -a android.intent.action.VIEW -d '<link>'` 会把地址/端口/配对码**预填**进表单
#    （注意是预填，不自动提交，还要点一下「开始配对」）。
# ④ 端口要按**占用者**杀，不要按进程名：服务端跑在 `dotnet` 里，`Get-Process QuizSync.Server.Cli`
#    一个都杀不到 —— 我因此被一个"上一轮遗留的服务端"骗了三轮（它占着 8765，新起的绑不上就静默退出，
#    而取配对码时是它在答，于是永远 `forbidden`）。
# ⑤ 取配对码要**重试到 6 位数字**，并校验响应形态：`control.token` 落盘晚于 `/health` 就绪，
#    抢跑会拿到错误体，而错误体里也有个 `code` 字段（值 `forbidden`）—— 别把它当配对码。
#
# ⑥ **deep link 要指定组件**（第 167 轮实测）：机器上不止一个应用注册了 `quizsync://pair`
#    （Flutter 的 1.2.1 也注册了同一个 scheme），只给 `-a VIEW -d <link>` 会弹系统的「打开方式」
#    选择框，预填当然不会发生（表现是"deep link 没生效"，很容易误判成应用不认链接）。
#    正确做法：`-n <applicationId>/<namespace>.MainActivity` —— 包名与组件名是**两件事**
#    （组件名跟着 `namespace` 走，见 `android/app/build.gradle.kts` 的注释）。
# ⑦ 换包名后**应用常常自己就配好了**（deep link 直接走完配对、进主壳显示「已连接」），
#    老脚本只认「表单里填上了 IP」，于是配对成功却报「deep link 没生效」—— 典型假红，已改判据。
# ⑧ ⚠️ **本脚本起的是"临时主机"**（`--data` 指向临时目录、识别器 fixture），跑完就杀。
#    所以它跑完之后平板记住的是**那个临时主机的设备令牌**：再去连真主机（应用内嵌的那个、
#    数据目录 `%LOCALAPPDATA%\QuizSyncAI\server`）时，平板会显示「电脑未连接」并反复重连
#    （TCP 连得上、鉴权过不去）。这不是故障，是两台不同的主机。要连真主机就带真主机的码再配一次：
#      $t=(gc "$env:LOCALAPPDATA\QuizSyncAI\server\control.token" -Raw).Trim()
#      $code=(irm http://127.0.0.1:8765/api/v1/pair/code -Headers @{'X-QS-Control'=$t}).code
#      adb shell am start -a android.intent.action.VIEW -d "quizsync://pair?host=<局域网IP>&port=8765&code=$code" -n <包>/<组件>
#
# 用法：powershell -ExecutionPolicy Bypass -File tools\pair_android_via_link.ps1 [-Serial e5b1641d]

param(
    [string]$Serial = 'e5b1641d',
    [int]$Port = 8765,
    [string]$Package = 'com.quizsync.android.native.dev',
    [string]$Component = 'com.quizsync.android.dev.MainActivity',
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

function Get-Tree {
    $null = Native $adb @('-s', $Serial, 'shell', 'uiautomator', 'dump', '/sdcard/qs-pair.xml')
    return (Native $adb @('-s', $Serial, 'shell', 'cat', '/sdcard/qs-pair.xml'))
}

# ① 先看手机在哪个网段（这一步以前没做，是全部失败的根源）
Write-Host "`n=== 1) 找与手机同网段的本机地址 ===" -ForegroundColor Cyan
$phoneIp = [regex]::Match((Native $adb @('-s', $Serial, 'shell', "ip -4 addr show wlan0 | grep inet")), 'inet (\d+\.\d+\.\d+)\.\d+').Groups[1].Value
if (-not $phoneIp) { throw "读不到手机的 wlan0 地址" }
Write-Host "  手机网段 = $phoneIp.0/24"
$lan = (Get-NetIPAddress -AddressFamily IPv4 |
    Where-Object { $_.IPAddress -like "$phoneIp.*" } |
    Select-Object -First 1).IPAddress
if (-not $lan) { throw "本机没有 $phoneIp.* 网段的地址 —— 手机与电脑不在同一个 Wi-Fi 下" }
Write-Host "  用本机地址 = $lan（同网段）" -ForegroundColor Green

# ④ 端口占用者优先清理
foreach ($owner in (Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty OwningProcess -Unique)) {
    Write-Host "  清掉占用 $Port 的进程 PID $owner" -ForegroundColor Yellow
    Stop-Process -Id $owner -Force -ErrorAction SilentlyContinue
}

Write-Host "`n=== 2) 起主机 ===" -ForegroundColor Cyan
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
if (-not $ready) { throw "主机没起来" }
Write-Host "  主机 PID = $($server.Id)" -ForegroundColor Green

# ⑤ 取码：重试到 6 位数字，且不认错误体里的 code
$code = ''
for ($attempt = 1; $attempt -le 25; $attempt++) {
    $token = ((Get-Content (Join-Path $Root 'control.token') -ErrorAction SilentlyContinue) -join '').Trim()
    if ($token.Length -eq 64) {
        $body = Native 'curl.exe' @('-s', '-H', "X-QS-Control: $token", "http://127.0.0.1:$Port/api/v1/pair/code")
        $match = [regex]::Match($body, '"code"\s*:\s*"(\d{6})"')
        if ($match.Success) { $code = $match.Groups[1].Value; break }
    }
    Start-Sleep -Milliseconds 400
}
if (-not $code) { throw "取不到 6 位配对码" }
Write-Host "  配对码 = $code" -ForegroundColor Green

try {
    Write-Host "`n=== 3) deep link 预填（不打字）===" -ForegroundColor Cyan
    $link = "quizsync://pair?host=$lan&port=$Port&code=$code"
    $null = Native $adb @('-s', $Serial, 'shell', 'am', 'force-stop', $Package)
    Start-Sleep -Milliseconds 500
    Write-Host "  $link"
    $null = Native $adb @('-s', $Serial, 'shell', 'am', 'start', '-a', 'android.intent.action.VIEW', '-d', "'$link'", '-n', "$Package/$Component")
    Start-Sleep -Seconds 8

    Write-Host "`n=== 4) 看情况：应用自己配好了 / 还需要点「开始配对」===" -ForegroundColor Cyan
    $tree = Get-Tree

    # 判据见文件头 ⑦：先看是不是已经连上了，没连上才去找表单与按钮。
    if ($tree -match '已连接') {
        Write-Host "  应用自己就配好了（主壳显示「已连接」），不需要点按钮" -ForegroundColor Green
    }
    else {
        if ($tree -notmatch [regex]::Escape($lan)) { throw "表单里没填上 $lan（deep link 没生效？）" }
        $tapped = $false
        foreach ($node in [regex]::Matches($tree, '<node[^>]*>')) {
            $text = [regex]::Match($node.Value, 'text="([^"]*)"').Groups[1].Value
            if ($text -eq '开始配对') {
                $bounds = [regex]::Match($node.Value, 'bounds="\[(\d+),(\d+)\]\[(\d+),(\d+)\]"')
                if ($bounds.Success) {
                    $x = [int](([int]$bounds.Groups[1].Value + [int]$bounds.Groups[3].Value) / 2)
                    $y = [int](([int]$bounds.Groups[2].Value + [int]$bounds.Groups[4].Value) / 2)
                    $null = Native $adb @('-s', $Serial, 'shell', 'input', 'tap', "$x", "$y")
                    $tapped = $true
                    break
                }
            }
        }
        if (-not $tapped) { throw "找不到「开始配对」按钮，也没看到「已连接」" }
        Start-Sleep -Seconds 12
    }

    Write-Host "`n=== 5) 两端各自取证 ===" -ForegroundColor Cyan
    $tree = Get-Tree
    $connected = $tree -match '已连接'
    $hostList = Native $dotnet @($dll, 'devices', 'list', '--data', $Root)
    Write-Host "  手机侧已连接 = $connected"
    Write-Host "  主机侧设备表："
    $hostList -split "`n" | ForEach-Object { "    $_" }

    if ($connected -and $hostList -notmatch '还没有已配对设备') {
        Write-Host "`n✅ 配对成功（两端都有证据）" -ForegroundColor Green
    }
    else {
        Write-Host "`n❌ 配对未完成" -ForegroundColor Red
        exit 1
    }
}
finally {
    if (-not $KeepHost) {
        $null = Native 'taskkill.exe' @('/T', '/F', '/PID', "$($server.Id)")
    }
    $null = Native $adb @('-s', $Serial, 'shell', 'rm', '-f', '/sdcard/qs-pair.xml')
}
