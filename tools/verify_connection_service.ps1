# 连接服务取证（第 169 轮）：用户两条要求 ——
#   ① 「开启连接设备」就是启动整个连接服务的那一个按钮（主机 + 识别服务一起起来，无需手动配置）；
#   ② 识别服务在**应用开启后就自动起来**。
# 外加一条：设置页那个「重启连接服务」必须真的重启（识别服务进程号要变），而不是空操作。
#
# 判据只认两条硬事实：
#   * 主机自己的 `/api/v2/info` 里的 `provider_online`（不问进程在不在，问主机认没认到）；
#   * `QuizSync.Provider.Cli` 进程号 —— 重启前后必须不同（同一个号就说明只是"看起来重启了"）。
#
# 用法：powershell -ExecutionPolicy Bypass -File tools\verify_connection_service.ps1
# 退出码：0 = 全过；1 = 有 FAIL 或前置失败。

param(
    [string]$Root = 'D:\DeepseekHarness\TemporaryConversationFile\connection-probe',
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $repo ("windows\QuizSync.App\bin\$Configuration\net10.0-windows10.0.26100.0\win-x64\QuizSync.App.exe")

$pass = 0
$fail = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { $script:pass++; Write-Host ("  PASS  " + $name + $(if ($detail) { "   ($detail)" } else { '' })) -ForegroundColor Green }
    else { $script:fail++; Write-Host ("  FAIL  " + $name + $(if ($detail) { "   ($detail)" } else { '' })) -ForegroundColor Red }
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

if (-not (Test-Path $exe)) { throw "找不到 $exe —— 先构建" }

function Get-HostInfo([string]$baseUrl) {
    try { return Invoke-RestMethod -Uri "$baseUrl/api/v2/info" -TimeoutSec 3 } catch { return $null }
}

function Get-ProviderPids {
    return @(Get-Process -Name 'QuizSync.Provider.Cli' -ErrorAction SilentlyContinue |
        Sort-Object Id | ForEach-Object { $_.Id })
}

Get-Process QuizSync.App -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process QuizSync.Provider.Cli -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 1200

Remove-Item -Recurse -Force $Root -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $Root | Out-Null

# 探针要证的是「自动把识别服务拉起来」，所以**必须有可用的 AI 配置** ——
# 把本机已有的 ai.json 拷进临时数据目录（DPAPI 密文是当前用户范围内的，同一个用户解得开）。
# `QS_APP_DATA` 影响 app 目录（ai.json 就在那儿），服务端数据目录另用 `QS_SERVER_DATA`。
$realApp = Join-Path $env:LOCALAPPDATA 'QuizSyncAI\app'
$aiJson = Join-Path $realApp 'ai.json'
Check '本机已有 AI 配置（ai.json）可拷进探针目录' (Test-Path $aiJson) $aiJson
if (Test-Path $aiJson) { Copy-Item $aiJson (Join-Path $Root 'ai.json') -Force }

@{ connect_enabled = $true; float_enabled = $false; theme = 'light' } |
    ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $Root 'app.json')

$env:QS_APP_DATA = $Root
$env:QS_SERVER_DATA = Join-Path $Root 'server'
$proc = Start-Process -FilePath $exe -PassThru
Remove-Item Env:\QS_APP_DATA -ErrorAction SilentlyContinue
Remove-Item Env:\QS_SERVER_DATA -ErrorAction SilentlyContinue

$logPath = Join-Path $Root 'logs\crash.log'
try {
    Write-Host "`n=== 等主机与识别服务自己起来（最多 60 秒，**不点任何按钮**）===" -ForegroundColor Cyan
    $baseUrl = $null
    $online = $false
    foreach ($attempt in 1..120) {
        Start-Sleep -Milliseconds 500
        if (-not $baseUrl) {
            foreach ($port in 8765..8770) {
                $info = Get-HostInfo "http://127.0.0.1:$port"
                if ($info) { $baseUrl = "http://127.0.0.1:$port"; break }
            }
        }

        if ($baseUrl) {
            $info = Get-HostInfo $baseUrl
            if ($info.provider_online -eq $true) { $online = $true; break }
        }
    }

    Check '主机起来了（内嵌在应用进程内）' ($null -ne $baseUrl) "$baseUrl"
    Check '识别服务**自己**上线了（应用开启后自动启动，没点任何按钮）' $online `
        $(if ($online) { "provider_online=true" } else { '60 秒内没上线' })

    $pidsBefore = Get-ProviderPids
    Check '有一个 QuizSync.Provider.Cli 进程在跑' ($pidsBefore.Count -ge 1) ("进程：" + ($pidsBefore -join ', '))

    if (Test-Path $logPath) {
        $lines = Get-Content $logPath -Encoding UTF8
        $startLine = ($lines | Select-String -Pattern '启动时识别服务：' | Select-Object -Last 1).Line
        Check '应用日志写明了自动启动识别服务' ($null -ne $startLine) "$startLine"
    }
    else {
        Check '应用日志写明了自动启动识别服务' $false "没有 $logPath"
    }

    Write-Host "`n=== 点「重启连接服务」（UIA 按名字点，第 0 个匹配就是它）===" -ForegroundColor Cyan
    $window = $null
    foreach ($attempt in 1..20) {
        $candidates = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
            [System.Windows.Automation.TreeScope]::Children,
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)))
        $window = $candidates |
            Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Window } |
            Sort-Object { -($_.Current.BoundingRectangle.Width * $_.Current.BoundingRectangle.Height) } |
            Select-Object -First 1
        if ($window) { break }
        Start-Sleep -Milliseconds 500
    }
    Check '找到应用主窗口（UIA）' ($null -ne $window)

    # 全新数据目录会落在「第一次使用」门页（一个合集都没有，SPEC §2.3.1）——
    # 那一页没有齿轮，不先走进去就永远到不了设置页（实测第一版就卡在这里：后面 4 条全假红）。
    $gate = $window.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, '新建并进入')))
    if ($gate) {
        Write-Host '  （落在首次使用门页：先建一个合集）' -ForegroundColor DarkGray
        $nameBox = $window.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::Edit)))
        if ($nameBox) {
            $nameBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('连接探针合集')
            Start-Sleep -Milliseconds 600
        }

        $gate.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Seconds 4
    }

    $gear = $window.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'SettingsButton')))
    if ($gear) {
        $gear.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Seconds 3
    }

    $nav = $window.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, '连接设备')))
    Check '左导航里有「连接设备」' ($null -ne $nav)
    if ($nav) {
        try { $nav.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
        catch { try { $nav.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } catch { } }
        Start-Sleep -Seconds 4
    }

    # 页面里不该再有那两个手动按钮（用户要求删掉多余的手动设置）。
    $dump = Join-Path $Root 'uia.txt'
    # `-MaxDepth 14`：按钮里的文字（TextBlock）在设置卡片里嵌得很深，深度不够时 dump 查不到 ——
    # 实测：同样的文字用 UIA 元素搜索能找到（下面那条断言），dump 却因为深度截断报"页面上没有"。
    & powershell -ExecutionPolicy Bypass -File (Join-Path $repo 'tools\dump_uia.ps1') `
        -ProcessName 'QuizSync.App' -MaxDepth 14 -OutFile $dump | Out-Null
    $lines = Get-Content $dump -Encoding UTF8
    $has = { param($pattern) (($lines | Select-String -Pattern $pattern -SimpleMatch | Measure-Object).Count -gt 0) }

    # 先证明这份 dump 真的覆盖了本页：否则「没有某个按钮」会因为在空/半页 dump 上查找而白白通过
    # （实测踩过：两条"手动按钮已删掉"在 dump 只截到半页时是假绿）。
    Check 'UIA dump 覆盖了本页（有「主机状态」这一行）' (& $has '主机状态') ("$($lines.Count) 行")
    Check '「主机服务端」那一块手动设置已经删掉（没有「检测并启动主机」）' (-not (& $has '检测并启动主机'))
    Check '「主机服务端」那一块手动设置已经删掉（没有「启动识别服务」按钮）' (-not (& $has '启动识别服务'))

    # 「重启连接服务」按钮**不靠 dump 判定**：它在页面偏下，ControlView 遍历不一定覆盖到。
    # 更强的判据是下面那条 —— 在 UIA 元素树里按文字找到**那个 Button**、并且真的点得下去；
    # 点下去之后识别服务必须换进程号，那才是"重启真的生效"的证据。

    # ⚠️ 不能直接按**名字**找一个元素就 Invoke：`TextButton` 造出来的按钮，它自己的 UIA Name 是空的
    # （名字在里面的 `TextBlock` 上），按名字找到的往往是**承载它的设置卡片行**或那个 TextBlock ——
    # 两者都不支持 `InvokePattern`（实测：`Unsupported Pattern`，于是"重启"根本没被点到，
    # 后面「必须换进程号」那条假红）。正确的做法：遍历所有 Button，看哪一棵子树里有这段文字，
    # 再对**那个 Button** 调 Invoke。
    function Find-ButtonByText {
        param([System.Windows.Automation.AutomationElement]$Window, [string]$Label)

        $buttons = $Window.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::Button)))

        foreach ($button in $buttons) {
            if ($button.Current.Name -eq $Label) { return $button }
            $inner = $button.FindFirst(
                [System.Windows.Automation.TreeScope]::Descendants,
                (New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::NameProperty, $Label)))
            if ($inner) { return $button }
        }

        return $null
    }

    $restart = Find-ButtonByText -Window $window -Label '重启连接服务'
    Check 'UIA 能拿到「重启连接服务」按钮（按文字找到那个 Button 本身）' ($null -ne $restart)
    if ($restart) {
        try {
            $restart.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
            Write-Host '  已点下「重启连接服务」'
        }
        catch {
            Write-Host "  （Invoke 不可用：$($_.Exception.Message)）" -ForegroundColor DarkGray
        }
    }

    Write-Host "`n=== 等重启完成（最多 45 秒）===" -ForegroundColor Cyan
    $pidsAfter = @()
    $onlineAgain = $false
    foreach ($attempt in 1..90) {
        Start-Sleep -Milliseconds 500
        $pidsAfter = Get-ProviderPids
        $info = if ($baseUrl) { Get-HostInfo $baseUrl } else { $null }
        $onlineAgain = ($info -and $info.provider_online -eq $true)
        if ($pidsAfter.Count -ge 1 -and $onlineAgain -and (($pidsAfter -join ',') -ne ($pidsBefore -join ','))) { break }
    }

    Check '重启后主机还在（没把独立主机进程杀掉、也没把内嵌宿主搞丢）' ($null -ne (Get-HostInfo $baseUrl)) $baseUrl
    Check '重启后识别服务重新上线' $onlineAgain ("进程：" + ($pidsAfter -join ', '))
    Check '识别服务**真的**换了进程（旧进程号不在新列表里）' `
        (($pidsBefore | Where-Object { $pidsAfter -contains $_ }).Count -eq 0) `
        ("重启前 " + ($pidsBefore -join ',') + " → 重启后 " + ($pidsAfter -join ','))
}
finally {
    Get-Process QuizSync.App -ErrorAction SilentlyContinue | Stop-Process -Force
    Get-Process QuizSync.Provider.Cli -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 800
    Remove-Item -Recurse -Force $Root -ErrorAction SilentlyContinue
}

Write-Host ("`n结果：PASS {0} / FAIL {1}" -f $pass, $fail) -ForegroundColor $(if ($fail -eq 0) { 'Green' } else { 'Red' })
exit $(if ($fail -eq 0) { 0 } else { 1 })
