# 端到端验收：**识别成功之后**那条链路（用户 2026-09-30 报的第 3 条）。
#
# 用户原话：「api识别后当前合计看不到（也看不到图片缩略图），安卓也不同步，悬浮窗也不显示内容。」
#
# 这一轮查到的两个真凶（都有当场取证，见 docs/DECISIONS.md 第 170 轮）：
#   ① 三个 `SessionRecorder.Record(...)` 调用点**都没传 `collectionId`** → 识别出来的记录全是
#      「未归类」，而主界面左侧列表、悬浮窗读的都是 `CollectionStore.SessionsOf(当前合集)`：
#      界面上表现为「当前 0 / 共 N 条记录」、没有缩略图、悬浮窗「还没有识别记录」。
#      取证：`QuizSync.Provider.Cli sessions` → 「识别记录 19 条（未归类 19 条）」。
#   ② 桌面端的设备令牌**被动失效**（服务端每次配对都换 token，而 `paired_at` 保留首次时间，
#      所以从设备列表上看不出来）：日志一行
#      `同步：推送失败（改动仍在本机）：unauthorized (401): token 无效`，
#      水位停在 38，22 道题一条都没上主机 → 安卓端自然什么都没有。
#
# 本脚本把「AI 那一跳」换成**本机假 provider**（tools\fake_ai_provider.ps1，不出网、结果固定），
# 其余每一步都是真代码：剪贴板监听 → 分析引擎 → 落库 → 合集归属 → 推 op（含 401 自愈）→ 两端渲染。
#
# 用法：powershell -ExecutionPolicy Bypass -File tools\verify_recognition_collection.ps1
#   [-Root <临时目录>] [-AiPort 8799] [-KeepRunning]
#
# 数据安全：应用数据目录 **只读拷贝**一份到临时目录（绝不改用户的库 / app.json / 令牌）；
# 起应用时 `QS_APP_DATA` / `QS_SERVER_DATA` **两个都指到临时目录** ——
# 只指 app 那个会让临时实例跟**真实主机**配对，把用户那份 device.token 顶掉（这正是②的成因之一）。
param(
    [string]$Root = 'D:\DeepseekHarness\TemporaryConversationFile\recognition-e2e',
    [int]$AiPort = 8799,
    [switch]$KeepRunning
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $repo 'windows\QuizSync.App\bin\Debug\net10.0-windows10.0.26100.0\win-x64\QuizSync.App.exe'
$cli = Join-Path $repo 'windows\QuizSync.Provider.Cli\bin\Debug\net10.0-windows\QuizSync.Provider.Cli.exe'
$stub = Join-Path $PSScriptRoot 'fake_ai_provider.ps1'
$clipboard = Join-Path $PSScriptRoot 'clipboard_put_image.ps1'
$realApp = Join-Path $env:LOCALAPPDATA 'QuizSyncAI\app'

$pass = 0
$fail = 0
function Check([string]$name, [bool]$ok, [string]$detail) {
    if ($ok) { $script:pass++; Write-Host ("  [PASS] {0} —— {1}" -f $name, $detail) -ForegroundColor Green }
    else { $script:fail++; Write-Host ("  [FAIL] {0} —— {1}" -f $name, $detail) -ForegroundColor Red }
}

function Get-UiaText([int]$OwnerPid) {
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $OwnerPid)
    $wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
    $texts = New-Object System.Collections.Generic.List[string]
    foreach ($w in $wins) {
        $all = $w.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)
        foreach ($el in $all) {
            try { if ($el.Current.Name) { $texts.Add($el.Current.Name) } } catch { }
        }
    }
    return $texts
}

Write-Host "`n=== 准备：只读拷贝用户数据到 $Root ===" -ForegroundColor Cyan
Get-Process QuizSync.App, QuizSync.Provider.Cli -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 900

$appDir = Join-Path $Root 'app'
$serverDir = Join-Path $Root 'server'
Remove-Item -Recurse -Force $Root -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $appDir, $serverDir | Out-Null

if (-not (Test-Path (Join-Path $realApp 'quizsync.db'))) { throw "找不到真实本地库：$realApp\quizsync.db" }
foreach ($suffix in @('', '-wal', '-shm')) {
    $src = Join-Path $realApp "quizsync.db$suffix"
    if (Test-Path $src) { Copy-Item $src (Join-Path $appDir "quizsync.db$suffix") -Force }
}
Write-Host "  拷贝完成：$appDir"

# ---- 夹具：目标合集 = 用户当前选中的那个（不在拷贝里就退到第一个合集）----
$userSettings = Get-Content (Join-Path $realApp 'app.json') -Raw | ConvertFrom-Json
$before = & $cli sessions --data $appDir --json | ConvertFrom-Json
Check '夹具：本地库里有识别记录（否则说明拷错了库）' ($before.total -gt 0) "total=$($before.total)"
Check '夹具：存在「未归类」记录（= ① 的现场）' ($before.unassigned -gt 0) "unassigned=$($before.unassigned)"

$target = $userSettings.current_collection_id
$targetName = ($before.collections | Where-Object { $_.collection_id -eq $target } | Select-Object -First 1).name
if (-not $targetName) {
    $first = $before.collections | Where-Object { $_.collection_id } | Select-Object -First 1
    if (-not $first) { throw '拷贝出来的库里一个合集都没有，无法验证归属' }
    $target = $first.collection_id
    $targetName = $first.name
}
$targetBefore = [int](($before.collections | Where-Object { $_.collection_id -eq $target } | Select-Object -First 1).count)
Write-Host ("  目标合集：{0}（{1}）当前 {2} 条" -f $targetName, $target, $targetBefore)

# ---- 沙盒设置：当前合集 = 目标；剪贴板监听开；悬浮窗开 ----
@{
    current_collection_id = $target
    clipboard_watch       = $true
    save_images           = $true
    float_enabled         = $true
    float_aspect          = 'portrait'
    float_scale           = 1.0
    float_topmost         = $true
    float_minimal         = $true
    connect_enabled       = $true
    theme                 = 'light'
} | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $appDir 'app.json')

# ---- AI 指向本机假 provider（真 HTTP、真信封、不出网）----
@{
    provider_id     = 'openai-compatible'
    base_url        = "http://127.0.0.1:$AiPort/v1"
    api_key         = 'fake-key-local-stub'
    model           = 'fake-model'
    timeout_seconds = 30
    max_retries     = 0
    daily_limit     = 200
} | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $appDir 'ai.json')

# ---- 故意写一个假设备令牌：验 ② 的自愈（真令牌是 dpapi: 开头的密文）----
[System.IO.File]::WriteAllText(
    (Join-Path $appDir 'device.token'), ('0' * 64), (New-Object System.Text.UTF8Encoding($false)))
Write-Host '  已写入假设备令牌（用来触发 401 → 自愈重新配对）'

# ===================================================================
Write-Host "`n=== 起假 provider 与应用 ===" -ForegroundColor Cyan
$readyFile = Join-Path $Root 'stub.ready'
$callLog = Join-Path $Root 'stub.calls.log'
$stubProc = Start-Process -FilePath 'powershell.exe' -PassThru -WindowStyle Hidden -ArgumentList @(
    '-ExecutionPolicy', 'Bypass', '-File', $stub,
    '-Port', "$AiPort", '-ReadyFile', $readyFile, '-CallLog', $callLog)

$waited = 0
while (-not (Test-Path $readyFile) -and $waited -lt 10000) { Start-Sleep -Milliseconds 200; $waited += 200 }
Check '假 provider 已监听' (Test-Path $readyFile) "端口 $AiPort（等了 $waited ms，进程 $($stubProc.Id)）"

$env:QS_APP_DATA = $appDir
$env:QS_SERVER_DATA = $serverDir
$appProc = Start-Process -FilePath $exe -PassThru
Remove-Item Env:\QS_APP_DATA -ErrorAction SilentlyContinue
Remove-Item Env:\QS_SERVER_DATA -ErrorAction SilentlyContinue

try {
    # 等悬浮窗（顺带证明窗口还起得来）
    Add-Type -Namespace Rec -Name Win -MemberDefinition @'
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
public delegate bool EnumProc(IntPtr h, IntPtr p);
'@
    function Test-FloatWindow([int]$OwnerPid) {
        $script:seen = $false
        $cb = [Rec.Win+EnumProc]{
            param($h, $p)
            $owner = 0
            [void][Rec.Win]::GetWindowThreadProcessId($h, [ref]$owner)
            if ($owner -eq $OwnerPid) {
                $t = New-Object System.Text.StringBuilder 256
                [void][Rec.Win]::GetWindowTextW($h, $t, 256)
                if ($t.ToString() -eq 'QuizSyncFloat' -and [Rec.Win]::IsWindowVisible($h)) { $script:seen = $true }
            }
            return $true
        }
        [void][Rec.Win]::EnumWindows($cb, [IntPtr]::Zero)
        return $script:seen
    }

    $floatSeen = $false
    foreach ($i in 1..80) {
        Start-Sleep -Milliseconds 250
        if (Test-FloatWindow $appProc.Id) { $floatSeen = $true; break }
    }
    Check '悬浮窗已显示' $floatSeen "pid=$($appProc.Id)"

    # 等剪贴板监听挂上（应用启动序列的最后几步）
    Start-Sleep -Seconds 3

    Write-Host "`n=== 放一张新图到剪贴板（真剪贴板 + 真监听）===" -ForegroundColor Cyan

    # ⚠️ **最多放两张图**：剪贴板监听是「变化才触发」的，而它可能在两处漏掉一张 ——
    # ① 监听还没注册完（应用启动序列的最后几步）；② 那一刻剪贴板被别的进程占着
    # （`CLIPBRD_E_CANT_OPEN`，应用侧已加退避重试，但重试窗口之外仍会丢）。
    # 实测：第一次跑就撞到过一次（同一份代码第二次跑全绿）。
    # 桩 provider 是即时的，所以重放一张图的代价只有几秒。
    $after = $null
    $clipAttempts = @()
    foreach ($attempt in 1..2) {
        $seed = Get-Random -Minimum 1 -Maximum 200
        $clipOut = & powershell.exe -STA -ExecutionPolicy Bypass -File $clipboard -Seed $seed 2>&1
        if ($attempt -eq 1) {
            Check '剪贴板已放入一张新图' (($clipOut -join ' ') -match 'OK \d+x\d+') ($clipOut -join ' ')
        }
        $clipAttempts += ("第 {0} 张（seed={1}）：{2}" -f $attempt, $seed, ($clipOut -join ' '))

        Write-Host ("`n=== 等识别落库（第 {0} 张，上限 25 秒）===" -f $attempt) -ForegroundColor Cyan
        $deadline = (Get-Date).AddSeconds(25)
        while ((Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 700
            $after = & $cli sessions --data $appDir --json | ConvertFrom-Json
            if ($after.total -gt $before.total) { break }
        }
        if ($after.total -gt $before.total) { break }
        Write-Host '  这一张没被识别，再放一张（见脚本里的说明）' -ForegroundColor DarkGray
        Start-Sleep -Seconds 1
    }

    $newCount = $after.total - $before.total
    Check '识别产生了新记录' ($newCount -ge 1) (
        "记录数 $($before.total) → $($after.total)（" + ($clipAttempts -join '；') + "）")
    $stubDetail = '没有调用日志'
    if (Test-Path $callLog) { $stubDetail = ((Get-Content $callLog -Raw).Trim() -replace "`r?`n", ' | ') }
    Check '假 provider 真被调用过' (Test-Path $callLog) $stubDetail

    $targetAfter = [int](($after.collections | Where-Object { $_.collection_id -eq $target } | Select-Object -First 1).count)
    Check '新记录挂到了「当前合集」（① 的修复）' ($targetAfter -gt $targetBefore) (
        "合集「$targetName」$targetBefore → $targetAfter 条")
    Check '未归类条数没有增加（新记录不是又变成未归类）' (
        [int]$after.unassigned -eq [int]$before.unassigned) (
        "未归类 $($before.unassigned) → $($after.unassigned)")
    $twoQuestion = @($after.sessions | Where-Object { $_.collection_id -eq $target -and $_.question_count -eq 2 }).Count
    $newest = ($after.sessions | Select-Object -First 1) | ConvertTo-Json -Compress
    Check '新记录的题数 = 桩答案的 2 题' ($twoQuestion -ge 1) $newest

    # ⚠️ **落库 ≠ 界面已经画完**：会话行是后台线程写的，UI 的 Reload / 悬浮窗刷新、
    # 以及识别结束后的那几行日志（含推 op 与 401 自愈）都在它之后。原来这里立刻读 UIA 与日志，
    # 于是拿到的全是「上一帧」：`当前 0 / 共 19 条记录`、日志里没有 401 —— 见 docs/DECISIONS.md
    # 「落库与渲染之间要等一帧」那条。这里等 6 秒并轮询，判据仍然是「界面真的变了」。
    Write-Host "`n=== 等界面与日志跟上（落库之后还有 UI 重画 + 推 op）===" -ForegroundColor Cyan
    $log = Join-Path $appDir 'logs\crash.log'
    $watermarkPath = Join-Path $appDir 'sync.watermark'
    $renderDeadline = (Get-Date).AddSeconds(25)
    $floatText = $null
    $countText = $null
    $logText = ''
    while ((Get-Date) -lt $renderDeadline) {
        Start-Sleep -Milliseconds 800
        $texts = Get-UiaText $appProc.Id
        # ⚠️ 每一轮都要**重新取**：第一次取样往往还是重画前的那一帧
        # （实测拿到 `当前 0 / 共 19 条记录`），只在「没取到」时才赋值会把这个旧值钉死到循环结束。
        $sample = @($texts | Where-Object { $_ -match '次识别' } | Select-Object -First 1)
        if ($sample.Count -gt 0) { $floatText = $sample[0] }
        $sample2 = @($texts | Where-Object { $_ -match '条记录' } | Select-Object -First 1)
        if ($sample2.Count -gt 0) { $countText = $sample2[0] }
        if (Test-Path $log) { $logText = Get-Content $log -Encoding UTF8 -Raw }
        $uiDone = $floatText -and ($countText -match ('当前 ' + $targetAfter + ' /'))
        if ($uiDone -and $logText -match '已推送') { break }
    }

    Write-Host "`n=== 两端渲染 + 同步自愈 ===" -ForegroundColor Cyan
    $floatDetail = '没找到「第 N/N 次识别」'
    if ($floatText) { $floatDetail = $floatText }
    Check '悬浮窗显示了识别内容（不再是「还没有识别记录」）' ($null -ne $floatText) $floatDetail

    $countDetail = '没找到「当前 N / 共 M 条记录」'
    if ($countText) { $countDetail = $countText }
    Check '主界面计数显示当前合集里有记录' ($countText -match ('当前 ' + $targetAfter + ' /')) $countDetail

    $syncLines = (($logText -split "`n" | Where-Object { $_ -match '推送|重配对|同步' } | Select-Object -Last 3) -join ' | ').Trim()
    Check '补推（含 401 自愈）在日志里留了现场' (
        $logText -match '已自动重新配对' -and $logText -match '已推送') $syncLines

    # 「安卓也不同步」这一条的正面判据：**新那条记录要真的到主机上**。
    # 安卓端的历史 / 当前任务都是从主机这份数据同步过去的，所以「主机库里有这条会话」
    # 就是「手机上能看到」的机器判据。主机库用同一条命令读（`--data <server 目录>`）——
    # 这一层两边的表结构一样（sessions / questions / collections）。
    # 识别那一下自己会推一次；即便那次没推成，30 秒一次的补推脉动也一定会补上，所以给 45 秒。
    $newSessionId = @($after.sessions | Select-Object -First 1)[0].session_id
    $hostDb = $null
    $hostDbTotal = 0
    $hostDbTarget = 0
    $hostDeadline = (Get-Date).AddSeconds(45)
    while ((Get-Date) -lt $hostDeadline) {
        $hostDb = & $cli sessions --data $serverDir --json | ConvertFrom-Json
        $hostDbTotal = $hostDb.total
        $hostDbTarget = [int](@($hostDb.collections | Where-Object { $_.collection_id -eq $target } | Select-Object -First 1).count)
        if (@($hostDb.sessions | Where-Object { $_.session_id -eq $newSessionId }).Count -ge 1) { break }
        Start-Sleep -Seconds 2
    }
    $hostHas = @($hostDb.sessions | Where-Object { $_.session_id -eq $newSessionId }).Count -ge 1
    Check '新记录已经推到主机（安卓端同步的就是这份数据）' $hostHas (
        "会话 $newSessionId；主机库 total=$hostDbTotal，「$targetName」$hostDbTarget 条")

    $tokenText = if (Test-Path (Join-Path $appDir 'device.token')) {
        (Get-Content (Join-Path $appDir 'device.token') -Raw).Trim() } else { '' }
    Check '假令牌已被换掉（新的那份是 dpapi 密文）' ($tokenText -ne ('0' * 64) -and $tokenText.Length -gt 0) (
        "device.token 长度 $($tokenText.Length)，前缀 $($tokenText.Substring(0, [Math]::Min(6, $tokenText.Length)))")
}
finally {
    if (-not $KeepRunning) {
        Stop-Process -Id $appProc.Id -Force -ErrorAction SilentlyContinue
        Stop-Process -Id $stubProc.Id -Force -ErrorAction SilentlyContinue
    }
}

Write-Host "`n=== 汇总 ===" -ForegroundColor Cyan
Write-Host ("PASS {0} / FAIL {1}" -f $pass, $fail) -ForegroundColor $(if ($fail -eq 0) { 'Green' } else { 'Red' })
exit $(if ($fail -eq 0) { 0 } else { 1 })
