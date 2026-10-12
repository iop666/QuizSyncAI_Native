# 内嵌主机验收（第 167 轮）：证明「一个应用就是全部」—— 主机跑在 QuizSync.App **进程里面**。
#
# 为什么值得单独一条脚本：这一项以前是「应用 + 独立的主机程序」两样东西，
# 用户要装两次、更新两次，还多一层「找不到主机程序」。改成内嵌之后，
# **唯一能把它与「又偷偷起了个进程」区分开的证据就是端口属主是谁** —— 所以判据围绕它来写。
#
# 九条判据：
#   ① 应用起来后 /health 可用（内嵌宿主真的在服务）；
#   ② 8765 的**属主 PID == 应用 PID**（核心：这就是「一个应用」的硬证据）；
#   ③ 属主进程名是 QuizSync.App；
#   ④ 机器上**没有**独立主机进程（QuizSync.Server.Cli / dotnet 跑 Server.Cli.dll）；
#   ⑤ /api/v2/info 报协议 v2（内嵌的是一个真 2.0 主机，不是残废的兼容壳）；
#   ⑥ 控制面能用 `server\control.token` 读到配对码（= 内嵌宿主用的是**同一个**数据目录与令牌，
#      所以配对过的手机与历史一条都不会丢）；
#   ⑦ 应用自己的日志里有「内嵌在本应用进程内」；
#   ⑧ **换的是进程边界，不是行为**：真的起一个 `QuizSync.Provider.Cli provider` 子进程去连它 ——
#      主机的 `/api/v2/info` 必须如实变成 `provider_online=true`；关掉它再变回 false（负向对照）。
#      这一条替代了原来想用的「Kotlin E2E 打内嵌宿主」：那条**用错了**（见文末注释），
#      Kotlin 客户端 × C# 主机的互通本来就由 `tools\verify_matrix_android.ps1` 第 3 格覆盖
#      （两边的宿主是同一份 `QuizSyncHost` 代码，差别只在进程边界）。
#   ⑨ 负向对照：应用进程退出后端口必须被放掉（这正是内嵌的**代价**：关掉应用手机就断，
#      要「关掉客户端主机仍在」得显式走独立进程）。
#
# 前置：本机不能有别的进程占着 8765–8770。若是**独立主机进程**（Server.Cli），
# 脚本会停下它（那是上一次运行的遗留），用 -NoStopHost 可以要求改为直接失败。
# 用法：powershell -ExecutionPolicy Bypass -File tools\verify_embedded_host.ps1 [-KeepRunning] [-NoProvider]
# 退出码：0 = 全过（SKIP 不算失败）；1 = 有 FAIL 或前置失败。
#
# 【第 8 条为什么要这么写 —— 一段走错的路，留在这里免得下次再走】
# 第一版我拿 Android 的 `HostApiEndToEndTest` 打内嵌宿主（它支持 QS_HOST_URL 打外部宿主），
# 结果是红的：`HostApiEndToEndTest.kt:207 任务应当跑到 done`。真因不是内嵌宿主坏了，而是那条用例
# **要求宿主带 fixture 假识别器**（它自己起主机时显式传 `--analyzer fixture`，代码里有注释说明）。
# 生产宿主（内嵌的、以及独立进程的，都一样）的识别器是「派发给 Provider」：没有 Provider 在线时
# 任务**立刻**以 `provider_offline` 失败 —— 这是协议规定的正确行为，不是缺陷。
# 所以那条用例对外部生产宿主**根本不适用**，是判据设计错了；要证的事改成第 8 条（真实客户端进程登记）。

param(
    [string]$Exe = '',
    [int]$WaitSeconds = 40,
    [switch]$NoStopHost,
    [switch]$KeepRunning,
    [switch]$NoProvider
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$serverDir = Join-Path $env:LOCALAPPDATA 'QuizSyncAI\server'
$tokenPath = Join-Path $serverDir 'control.token'
$appJson = Join-Path $env:LOCALAPPDATA 'QuizSyncAI\app\app.json'
$log = Join-Path $env:LOCALAPPDATA 'QuizSyncAI\app\logs\crash.log'
$ports = 8765..8770
$dotnet = if ($env:QS_DOTNET) { $env:QS_DOTNET } else { 'dotnet' }

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

function Skip([string]$name, [string]$why) {
    Write-Host ("  SKIP  " + $name + "   (" + $why + ")") -ForegroundColor Yellow
}

function ListenerPid([int]$port) {
    $c = Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $c) { return $null }
    return [int]$c.OwningProcess
}

function Health() {
    try { return Invoke-RestMethod -Uri 'http://127.0.0.1:8765/health' -TimeoutSec 3 } catch { return $null }
}

function ProviderOnline() {
    try {
        $info = Invoke-RestMethod -Uri 'http://127.0.0.1:8765/api/v2/info' -TimeoutSec 3
        return [bool]$info.provider_online
    }
    catch { return $null }
}

function HostProcesses() {
    $found = @()
    Get-Process 'QuizSync.Server.Cli' -ErrorAction SilentlyContinue | ForEach-Object { $found += "QuizSync.Server.Cli(pid=$($_.Id))" }
    # 用 dotnet 跑 dll 的那种形态（publish 出 apphost 之前最常见）
    foreach ($p in @(Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" -ErrorAction SilentlyContinue)) {
        if ($p.CommandLine -and $p.CommandLine -match 'QuizSync\.Server\.Cli') { $found += "dotnet Server.Cli(pid=$($p.ProcessId))" }
    }
    return $found
}

function WaitFor([scriptblock]$test, [int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        if (& $test) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

function LogText() {
    if (-not (Test-Path $log)) { return '' }
    return (Get-Content $log -Encoding UTF8 -Raw)
}

function ResolveProviderDll() {
    if ($env:QS_PROVIDER_DLL -and (Test-Path $env:QS_PROVIDER_DLL)) { return $env:QS_PROVIDER_DLL }
    $candidates = @(
        (Join-Path $repo 'windows\QuizSync.Provider.Cli\bin\Release\publish\QuizSync.Provider.Cli.dll'),
        (Join-Path $repo 'windows\QuizSync.Provider.Cli\bin\Debug\net10.0-windows\QuizSync.Provider.Cli.dll'),
        (Join-Path $repo 'windows\QuizSync.Provider.Cli\bin\Debug\net10.0\QuizSync.Provider.Cli.dll')
    )
    return ($candidates | Where-Object { Test-Path $_ } | Select-Object -First 1)
}

# ---------------- 前置 ----------------
if (-not $Exe -or -not (Test-Path $Exe)) { throw "找不到 QuizSync.App.exe（用 -Exe 指定）" }
if (Get-Process 'QuizSync.App' -ErrorAction SilentlyContinue) {
    throw '已经有一个 QuizSync.App 在跑：本脚本**不替你杀进程**，请先自己关掉再跑'
}

# ⚠️ 第 170 轮补的前置：**残留的识别服务（Provider）必须清掉**，否则「关掉它之后主机如实报
# 不在线」那条负向对照必然假红 —— 残留的 Provider 会自己重连到这次起的主机上，
# `provider_online` 一直是 True（实测踩到：上一条探针留下的 QuizSync.Provider.Cli 还在跑）。
# 与上面那条同一个口径：不替你杀进程，但要说清为什么不能接着跑。
$strayProviders = @()
Get-Process 'QuizSync.Provider.Cli' -ErrorAction SilentlyContinue |
    ForEach-Object { $strayProviders += "QuizSync.Provider.Cli(pid=$($_.Id))" }
foreach ($p in @(Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" -ErrorAction SilentlyContinue)) {
    if ($p.CommandLine -and $p.CommandLine -match 'QuizSync\.Provider\.Cli') {
        $strayProviders += "dotnet Provider.Cli(pid=$($p.ProcessId))"
    }
}

if ($strayProviders.Count -gt 0) {
    # 逐句拼接：PS 5.1 对「跨行 + 行首加号」的括号表达式会解析失败
    # （实测报 Missing closing ')'，位置指在第一行的 `-join` 上）。
    $msg = "还有识别服务在跑：" + ($strayProviders -join '、')
    $msg += "。本脚本要验「Provider 不在线」的负向对照，请先结束它们再跑"
    $msg += "：Get-Process QuizSync.Provider.Cli | Stop-Process -Force"
    throw $msg
}

Write-Host "`n=== 前置：清空 8765–8770 ===" -ForegroundColor Cyan
foreach ($port in $ports) {
    $owner = ListenerPid $port
    if (-not $owner) { continue }

    $proc = Get-Process -Id $owner -ErrorAction SilentlyContinue
    $name = if ($proc) { $proc.ProcessName } else { '?' }
    $isHost = $name -eq 'QuizSync.Server.Cli'
    if (-not $isHost -and $name -eq 'dotnet') {
        $isHost = @(Get-CimInstance Win32_Process -Filter "ProcessId=$owner" -ErrorAction SilentlyContinue |
            Where-Object { $_.CommandLine -match 'QuizSync\.Server\.Cli' }).Count -gt 0
    }

    if (-not $isHost) {
        throw "端口 $port 被 $name(pid=$owner) 占着 —— 不是本项目的独立主机进程，脚本不动它。请先处理掉再跑。"
    }

    if ($NoStopHost) { throw "端口 $port 上有一个独立主机进程（pid=$owner），-NoStopHost 要求直接失败" }

    Write-Host "  停掉遗留的独立主机进程 $name(pid=$owner)" -ForegroundColor DarkGray
    Stop-Process -Id $owner -Force
    if (-not (WaitFor { -not (ListenerPid $port) } 10)) { throw "停掉 pid=$owner 之后端口 $port 还被占着" }
}

if (-not (Test-Path $tokenPath)) { throw "找不到主机控制令牌：$tokenPath（先手动起一次主机）" }
$tokenBefore = (Get-Content $tokenPath -Encoding UTF8 -Raw).Trim()
$hostProcessesBefore = HostProcesses
if ($hostProcessesBefore.Count -gt 0) { throw ("还有独立主机进程在跑：" + ($hostProcessesBefore -join ', ')) }

$originalJson = $null
if (Test-Path $appJson) { $originalJson = Get-Content $appJson -Encoding UTF8 -Raw }

$app = $null
$provider = $null
try {
    # 「开启连接设备」开着，应用启动时才会把主机拉起来（SPEC §2.2）。
    if ($originalJson -and $originalJson -notmatch '"connect_enabled"\s*:\s*true') {
        $on = $originalJson -replace '"connect_enabled"\s*:\s*false', '"connect_enabled": true'
        [System.IO.File]::WriteAllText($appJson, $on, (New-Object System.Text.UTF8Encoding($false)))
        Write-Host '  （已把 app.json 的 connect_enabled 临时改成 true）' -ForegroundColor DarkGray
    }

    Write-Host "`n=== 启动应用（$Exe）===" -ForegroundColor Cyan
    $app = Start-Process -FilePath $Exe -PassThru
    $appPid = $app.Id
    Write-Host "  应用 pid=$appPid" -ForegroundColor DarkGray

    $healthy = WaitFor { $null -ne (Health) } $WaitSeconds
    Check '应用起来后 /health 可用（内嵌宿主在服务）' $healthy 'http://127.0.0.1:8765/health'

    $health = Health
    Check '健康检查自述 status=ok' ($null -ne $health -and $health.status -eq 'ok') `
        $(if ($health) { "status=$($health.status) protocol_version=$($health.protocol_version)" } else { '没有应答' })

    $owner = ListenerPid 8765
    Check '8765 的属主就是应用进程（一个应用，没有第二个程序）' ($owner -eq $appPid) `
        "属主 pid=$owner / 应用 pid=$appPid"

    $ownerName = if ($owner) { (Get-Process -Id $owner -ErrorAction SilentlyContinue).ProcessName } else { '?' }
    Check '8765 的属主进程名是 QuizSync.App' ($ownerName -eq 'QuizSync.App') "属主进程名=$ownerName"

    $hostProcesses = HostProcesses
    Check '机器上没有独立的主机进程' ($hostProcesses.Count -eq 0) `
        $(if ($hostProcesses.Count -eq 0) { 'QuizSync.Server.Cli / dotnet Server.Cli 都没在跑' } else { $hostProcesses -join ', ' })

    $info = $null
    try { $info = Invoke-RestMethod -Uri 'http://127.0.0.1:8765/api/v2/info' -TimeoutSec 3 } catch { $info = $null }
    Check '内嵌的是真 2.0 主机（/api/v2/info 通）' ($null -ne $info) `
        $(if ($info) { "protocol_version=$($info.protocol_version) provider_online=$($info.provider_online)" } else { '没有应答' })

    $tokenNow = (Get-Content $tokenPath -Encoding UTF8 -Raw).Trim()
    $pairCode = $null
    try {
        $pairCode = (Invoke-RestMethod -Uri 'http://127.0.0.1:8765/api/v1/pair/code' -TimeoutSec 3 `
                -Headers @{ 'X-QS-Control' = $tokenNow }).code
    }
    catch { $pairCode = $null }
    Check '控制面用同一个 control.token 能读配对码（数据目录没换）' ($null -ne $pairCode -and $pairCode.Length -eq 6) `
        $(if ($pairCode) { "配对码 $pairCode（token 前 8 位 $($tokenNow.Substring(0,8))…）" } else { '读不到（令牌或数据目录不一致）' })

    Check 'control.token 没有被重写（还是原来那一个）' ($tokenNow -eq $tokenBefore) `
        $(if ($tokenNow -eq $tokenBefore) { '内容一致' } else { '内容变了 —— 两处各写了一份令牌' })

    Check '应用日志写明主机内嵌在本进程内' ((LogText) -match '启动时主机：内嵌在本应用进程内') `
        "日志：$log"

    # ---------------- 第 8 条：真实客户端进程（Provider）连上内嵌宿主 ----------------
    Write-Host "`n=== 第 8 条：真实客户端进程连上内嵌宿主 ===" -ForegroundColor Cyan
    $providerDll = ResolveProviderDll
    if ($NoProvider) {
        Skip '真实客户端进程（Provider）能连上内嵌宿主' '-NoProvider'
        Skip '关掉它之后主机如实报不在线（负向对照）' '-NoProvider'
    }
    elseif (-not $providerDll) {
        Skip '真实客户端进程（Provider）能连上内嵌宿主' '找不到 QuizSync.Provider.Cli.dll（先构建它）'
        Skip '关掉它之后主机如实报不在线（负向对照）' '找不到 QuizSync.Provider.Cli.dll'
    }
    else {
        Write-Host "  $providerDll" -ForegroundColor DarkGray
        $env:QS_SERVER_DATA = $serverDir   # Provider 靠它找 control.token
        # **一定把子进程的输出抓下来**：Provider CLI 的退出码 2 有两个来源
        # （没配 AI；以及**任何未捕获异常**—— `catch { Console.Error.WriteLine($"失败：{e.Message}"); return 2; }`）。
        # 第一版没抓输出，于是把一次真异常当成"没配 Key"跳过了 —— 这正是"不许猜"的反面教材。
        $providerLog = 'D:\DeepseekHarness\TemporaryConversationFile\embedded-host-provider.log'
        Remove-Item -Force $providerLog -ErrorAction SilentlyContinue
        $provider = Start-Process -FilePath $dotnet -PassThru -WindowStyle Hidden `
            -WorkingDirectory (Split-Path -Parent $providerDll) `
            -ArgumentList @("`"$providerDll`"", 'provider') `
            -RedirectStandardOutput $providerLog -RedirectStandardError "$providerLog.err"

        $cameOnline = WaitFor { (ProviderOnline) -eq $true } 30
        if (-not $cameOnline -and $provider.HasExited) {
            $providerCode = $provider.ExitCode
            $said = @(
                $(if (Test-Path $providerLog) { Get-Content $providerLog -Encoding UTF8 -Raw })
                $(if (Test-Path "$providerLog.err") { Get-Content "$providerLog.err" -Encoding UTF8 -Raw })
            ) -join ' '
            $said = ($said -replace '\s+', ' ').Trim()

            # 「没配 AI Key」是**唯一**允许跳过的情形，而且要按它自己的话认，不是按退出码猜。
            if ($providerCode -eq 2 -and $said -match 'Key|还没配|配好|API') {
                Skip '真实客户端进程（Provider）能连上内嵌宿主' "退出码 2 且它自己说没配 AI：$said"
                Skip '关掉它之后主机如实报不在线（负向对照）' '同上'
            }
            else {
                Check '真实客户端进程（Provider）能连上内嵌宿主' $false `
                    "刚起来就退出了（退出码 $providerCode）；它的话：$said（日志 $providerLog）"
            }
        }
        else {
            Check '真实客户端进程（Provider）能连上内嵌宿主（process 边界没变行为）' $cameOnline `
                "provider_online=$((ProviderOnline))"

            Stop-Process -Id $provider.Id -Force
            $null = $provider.WaitForExit(5000)

            # ⚠️ 第 170 轮：**还要关掉应用自己拉起的那个 Provider**（第 169 轮起
            # 「开启连接设备」一键把主机 + 识别服务一起起起来），否则主机照旧报 online，
            # 这条负向对照就成了假红（实测：只杀本脚本起的那个，provider_online 仍是 True）。
            $others = @(Get-Process 'QuizSync.Provider.Cli' -ErrorAction SilentlyContinue |
                Where-Object { $_.Id -ne $provider.Id })
            foreach ($p in $others) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
            if ($others.Count -gt 0) {
                Write-Host ("  （同时关掉应用自启的识别服务：{0} 个）" -f $others.Count) -ForegroundColor DarkGray
            }

            $backOffline = WaitFor { (ProviderOnline) -eq $false } 20
            Check '关掉它之后主机如实报不在线（负向对照）' $backOffline "provider_online=$((ProviderOnline))"
        }
    }
}
finally {
    if ($provider -and -not $provider.HasExited) {
        Stop-Process -Id $provider.Id -Force
        Write-Host '  （已关掉本次启动的识别服务进程）' -ForegroundColor DarkGray
    }

    # 第 170 轮：**应用自己也会拉起识别服务**（第 169 轮起「开启连接设备」一键启动主机 + Provider），
    # 所以只关应用会留下一只 Provider —— 而它下一次会自己重连到新主机的 8765 上，
    # 让本脚本的负向对照（「关掉 Provider 之后主机如实报不在线」）变成假红，
    # 并且下一次运行会被上面的前置检查拒掉。这里一并收尾。
    $leftover = @(Get-Process 'QuizSync.Provider.Cli' -ErrorAction SilentlyContinue)
    foreach ($p in $leftover) {
        Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    }

    if ($leftover.Count -gt 0) {
        Write-Host ("  （已清掉 {0} 个残留的识别服务进程）" -f $leftover.Count) -ForegroundColor DarkGray
    }

    if ($originalJson) {
        [System.IO.File]::WriteAllText($appJson, $originalJson, (New-Object System.Text.UTF8Encoding($false)))
        Write-Host '  （已把 app.json 恢复原样）' -ForegroundColor DarkGray
    }

    if ($app -and -not $KeepRunning) {
        Write-Host "`n=== 第 9 条：关掉应用之后（负向对照）===" -ForegroundColor Cyan
        $app.CloseMainWindow() | Out-Null
        $exited = $app.WaitForExit(8000)
        if (-not $exited) {
            # 关窗 = 收进托盘（1.1.0 的语义），所以主机这时**还在**跑 —— 手机照旧连得上。
            # 真正结束进程的只有托盘的「退出」；这里为了继续验下面的判据才强制结束它。
            Write-Host '  （关窗后进程还在托盘里，强制结束 —— 与 1.1.0 的托盘语义一致）' -ForegroundColor DarkGray
            $app.Kill()
            $null = $app.WaitForExit(5000)
        }

        $gone = WaitFor { -not (ListenerPid 8765) } 10
        Check '应用退出后端口被放掉（内嵌的代价：关掉应用手机就断）' $gone `
            $(if ($gone) { '8765 已无监听' } else { "8765 仍被 pid=$(ListenerPid 8765) 占着" })
    }
}

Write-Host ("`n结果：PASS {0} / FAIL {1}" -f $pass, $fail) -ForegroundColor $(if ($fail -eq 0) { 'Green' } else { 'Red' })
exit $(if ($fail -eq 0) { 0 } else { 1 })
