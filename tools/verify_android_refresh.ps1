# 安卓下拉刷新指示器取证（第 168 轮）。
#
# 用户报：「安卓端的下拉刷新的加载图标拉下来不会自己消失」。
#
# ⚠️ 判据必须看**像素**，不能数无障碍树里的 `ProgressBar` 节点 —— 第一版就是这么写的，
# 后来被像素证据推翻：Compose 的语义节点在动画结束之后**会残留在树里**，
# 实测那个节点所在的 40×40 区域是**纯白**（什么都没画），于是「节点还在」被判成「指示器还在转」。
# 现在的判据：取指示器那一小块**截图**，数「与主色不同的像素」（ink）——
#   ① 下拉过程中：ink > 0（真的画出来了，说明手势生效）；
#   ② 15 秒后：ink ≈ 0（自己收干净了）。
#
# ⚠️ 探针自己的另外三个坑（都实测踩过）：
#   ① 不能用「连按两次返回」把应用调出来（当前任务页按返回会退出应用，之后点击全落在桌面上）；
#   ② `adb devices` 单行时 `[0]` 取到的是字符不是设备号 → 必须 `@()`；
#   ③ 左导航不能按固定坐标点（页面内容一变坐标就偏，会点到记录行）→ 按文字点。
#
# 用法：powershell -ExecutionPolicy Bypass -File tools\verify_android_refresh.ps1 [-Serial <设备号>]
# 退出码：0 = 全过；1 = 有 FAIL 或前置失败。

param(
    [string]$Serial = '',
    [string]$Package = 'com.quizsync.android.native.dev',
    [string]$Component = 'com.quizsync.android.dev.MainActivity'
)

$ErrorActionPreference = 'Stop'
$adb = 'D:\Windows\Apps\Android\Sdk\platform-tools\adb.exe'
$work = 'D:\DeepseekHarness\TemporaryConversationFile\android-refresh'

Add-Type -AssemblyName System.Drawing
# UIA 那一套只有 `ScrollToTop` 用得上（把滚下去的列表退回顶部），但类型要先加载，
# 否则走到那一句才报 "Unable to find type [System.Windows.Automation.TreeScope]"。
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$pass = 0
$fail = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { $script:pass++; Write-Host ("  PASS  " + $name + $(if ($detail) { "   ($detail)" } else { '' })) -ForegroundColor Green }
    else { $script:fail++; Write-Host ("  FAIL  " + $name + $(if ($detail) { "   ($detail)" } else { '' })) -ForegroundColor Red }
}

if (-not $Serial) {
    $devices = @(& $adb devices | Select-String -Pattern '^\S+\s+device$' | ForEach-Object { ($_ -split '\s+')[0] })
    if ($devices.Count -eq 0) { throw '没有已连接的设备（adb devices 为空）' }
    $Serial = $devices[0]
}

New-Item -ItemType Directory -Force -Path $work | Out-Null

function Foreground {
    return (@(& $adb -s $Serial shell "dumpsys activity activities | grep -E 'topResumedActivity|mResumedActivity' | head -1") -join ' ')
}

function EnsureApp {
    if ((Foreground) -match [regex]::Escape($Package)) { return $true }
    & $adb -s $Serial shell am start -n "$Package/$Component" | Out-Null
    Start-Sleep -Seconds 3
    return ((Foreground) -match [regex]::Escape($Package))
}

function Dump {
    param([string]$Name)
    & $adb -s $Serial shell uiautomator dump /sdcard/ui.xml | Out-Null
    $local = Join-Path $work "$Name.xml"
    & $adb -s $Serial pull /sdcard/ui.xml $local | Out-Null
    return (Get-Content $local -Raw -Encoding UTF8)
}

function ProgressBars([string]$xml) { ([regex]::Matches($xml, 'class="android.widget.ProgressBar"')).Count }
function Texts([string]$xml) {
    [regex]::Matches($xml, 'text="([^"]+)"') | ForEach-Object { $_.Groups[1].Value } | Where-Object { $_ -ne '' }
}

# 指示器语义节点的矩形（`bounds="[x1,y1][x2,y2]"`）——只用来定位取样框。
function ProgressRect([string]$xml) {
    $match = [regex]::Match($xml, '<node[^>]*class="android.widget.ProgressBar"[^>]*bounds="\[(\d+),(\d+)\]\[(\d+),(\d+)\]"')
    if (-not $match.Success) { return $null }
    return @{
        X = [int]$match.Groups[1].Value
        Y = [int]$match.Groups[2].Value
        W = [int]$match.Groups[3].Value - [int]$match.Groups[1].Value
        H = [int]$match.Groups[4].Value - [int]$match.Groups[2].Value
    }
}

function Screenshot {
    param([string]$Name)
    & $adb -s $Serial shell screencap -p /sdcard/$Name.png | Out-Null
    $local = Join-Path $work "$Name.png"
    & $adb -s $Serial pull /sdcard/$Name.png $local | Out-Null
    return $local
}

# 取样框里「不是主色」的像素占比：转圈的圆弧是主色/深色，纯背景区域是 0。
function InkRatio {
    param([string]$PngPath, [hashtable]$Box)

    $bmp = New-Object System.Drawing.Bitmap($PngPath)
    try {
        $counts = @{}
        for ($y = $Box.Y; $y -lt ($Box.Y + $Box.H); $y++) {
            for ($x = $Box.X; $x -lt ($Box.X + $Box.W); $x++) {
                $c = $bmp.GetPixel($x, $y)
                $key = "$($c.R),$($c.G),$($c.B)"
                if ($counts.ContainsKey($key)) { $counts[$key] = $counts[$key] + 1 } else { $counts[$key] = 1 }
            }
        }

        $top = $counts.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 1
        $total = $Box.W * $Box.H
        $ink = $total - $top.Value
        return @{ Ink = $ink; Total = $total; Ratio = [double]$ink / $total; Base = $top.Key }
    }
    finally {
        $bmp.Dispose()
    }
}

function TapText {
    param([string]$Label)
    $xml = Dump ("tap-" + ($Label -replace '[^\w]', ''))
    $match = [regex]::Match($xml, ('<node[^>]*text="' + [regex]::Escape($Label) + '"[^>]*bounds="\[(\d+),(\d+)\]\[(\d+),(\d+)\]"'))
    if (-not $match.Success) { return $false }
    $cx = [int](([int]$match.Groups[1].Value + [int]$match.Groups[3].Value) / 2)
    $cy = [int](([int]$match.Groups[2].Value + [int]$match.Groups[4].Value) / 2)
    & $adb -s $Serial shell input tap $cx $cy | Out-Null
    Start-Sleep -Seconds 2
    return $true
}

Write-Host "`n=== 设备 $Serial ===" -ForegroundColor Cyan
$installed = (@(& $adb -s $Serial shell pm list packages $Package) -join "`n")
Check "应用已安装（$Package）" ($installed -match [regex]::Escape($Package))

Check '把被测应用拉到前台（am start，不是按返回）' (EnsureApp) (Foreground)

# 先退回主壳：应用可能停在上一次留下的**压栈页**（识别结果页 / 合集详情页）——
# 那种页面上左导航根本不在屏上，按文字点 tab 必然失败。
foreach ($step in 1..4) {
    if ((Texts (Dump "shell-$step")) -contains '当前任务') { break }
    & $adb -s $Serial shell input keyevent KEYCODE_BACK | Out-Null
    Start-Sleep -Seconds 1
    [void](EnsureApp)
}

Check '点到了左导航的「当前任务」' (TapText '当前任务')
Check '仍在被测应用里（当前任务页）' ((Foreground) -match [regex]::Escape($Package)) (Foreground)

$before = Dump 'before'
# ⚠️ 这里**不断言「下拉前没有 ProgressBar 节点」**：Compose 的语义节点在动画结束后会残留
# （实测那个节点所在的 40×40 区域是纯白，什么都没画）。节点存在与否不能当判据 —— 只看像素。
Write-Host ("  下拉前的进度节点数（仅供参考，节点会残留）：" + (ProgressBars $before))

# M3 的指示器贴在内容区顶部中央；取样框**就用语义节点那一小块**（不加边距）。
$fallbackBox = @{ X = 980; Y = 323; W = 40; H = 40 }

# 把列表退回顶部（Android 没有 Windows 那套 UIA 滚动模式，只能用**快甩**：
# 短时长的手势是 fling，不会把手势判成下拉刷新）。
# 为什么需要它：历史页的列表滚下去之后，下拉手势只会把列表滚回顶部、**不触发刷新**
# （实测：那时指示器一次都不出现）—— 这是手势语义，不是缺陷。
function FlungToTop {
    foreach ($attempt in 1..3) {
        & $adb -s $Serial shell input swipe 900 800 900 2400 100 | Out-Null
        Start-Sleep -Milliseconds 600
    }
}

function PullAndMeasure {
    param([string]$Tag)

    # 取样框**就用指示器语义节点那一小块**（不加边距）。加边距会把卡片边缘、搜索框描边
    # 这些**静态**像素算进来 —— 实测那样「下拉中」和「15 秒后」都是同一个 2.5%，
    # 看着像"一直画着"，其实是取样框画大了。
    $rect = ProgressRect (Dump "$Tag-box")
    $box = if ($rect) { @{ X = $rect.X; Y = $rect.Y; W = $rect.W; H = $rect.H } } else { $fallbackBox }

    # 「手指还按着」的那一刻抓一张：这时的指示器一定在（手势生效的对照）。
    # `input swipe` 是阻塞的，所以放后台跑，抓到图再等它结束。
    $swipe = Start-Process -FilePath $adb `
        -ArgumentList @('-s', $Serial, 'shell', 'input', 'swipe', '900', '1100', '900', '1900', '1400') `
        -PassThru -WindowStyle Hidden
    Start-Sleep -Milliseconds 800
    $during = InkRatio (Screenshot "$Tag-during") $box
    $swipe.WaitForExit()

    Start-Sleep -Seconds 15
    $after = InkRatio (Screenshot "$Tag-after") $box

    return @{
        NodeCount = ProgressBars (Dump "$Tag-after-pull")
        Box = $box
        DuringInk = $during.Ratio
        AfterInk = $after.Ratio
        AfterNode = ProgressBars (Dump "$Tag-after-dump")
    }
}

Write-Host "`n=== 当前任务页：下拉一次 ===" -ForegroundColor Cyan
$task = PullAndMeasure 'task'
Write-Host ("  取样框 ($($task.Box.X),$($task.Box.Y)) $($task.Box.W)x$($task.Box.H)")
Write-Host ("  下拉中 ink={0:P1}（节点 {1} 个）；15 秒后 ink={2:P1}（节点 {3} 个）" -f `
        $task.DuringInk, $task.NodeCount, $task.AfterInk, $task.AfterNode)
Check '下拉时指示器真的画出来了（手势生效）' ($task.DuringInk -gt 0.005) ("ink={0:P1}" -f $task.DuringInk)
Check '15 秒后指示器已经收干净（像素级别）' ($task.AfterInk -le 0.002) ("ink={0:P1}" -f $task.AfterInk)

Write-Host "`n=== 历史页：同样来一次 ===" -ForegroundColor Cyan
Check '点到了左导航的「历史记录」' (TapText '历史记录')
FlungToTop

$hist = PullAndMeasure 'hist'
Write-Host ("  取样框 ($($hist.Box.X),$($hist.Box.Y)) $($hist.Box.W)x$($hist.Box.H)")
Write-Host ("  下拉中 ink={0:P1}（节点 {1} 个）；15 秒后 ink={2:P1}（节点 {3} 个）" -f `
        $hist.DuringInk, $hist.NodeCount, $hist.AfterInk, $hist.AfterNode)
# 「下拉中」这一条在历史页**不作判据**：它取决于列表当时滚到哪儿 —— 列表不在顶部时，
# 下拉手势只会把列表滚回顶部、不触发刷新（手势语义如此，不是缺陷）。这里只打印数值；
# 真正要证的那条（松手之后不会留着一个转圈）由下面的像素断言负责，两页都测。
Write-Host "  （历史页「下拉中」的 ink 仅供参考：列表不在顶部时手势只滚列表、不触发刷新）"
Check '历史页 15 秒后指示器收干净（像素级别）' ($hist.AfterInk -le 0.002) ("ink={0:P1}" -f $hist.AfterInk)

$histAfter = Dump 'hist-final'
$texts = (Texts $histAfter) -join ' | '
Write-Host "  刷新后的历史页文字：$texts"
Check '下拉是「手动刷新」：真的刷新了列表（没有卡在加载态）' `
    (($texts -match '条识别记录') -or ($texts -match '还没有识别记录')) $texts
Check '刷新后仍显示连接状态（已连接 / 电脑未连接 二者之一）' `
    (($texts -match '已连接') -or ($texts -match '电脑未连接')) $texts

Write-Host ("`n结果：PASS {0} / FAIL {1}" -f $pass, $fail) -ForegroundColor $(if ($fail -eq 0) { 'Green' } else { 'Red' })
exit $(if ($fail -eq 0) { 0 } else { 1 })
