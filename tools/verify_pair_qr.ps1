# 配对二维码取证（第 168 轮）：设置 →「连接设备」页上**真实画出来**的那张二维码，
# 解回来必须逐字等于协议 §2.1 的深链，且 host 是局域网地址、code 是主机控制面给的那个。
#
# 为什么不能只测编码函数：那只证明「PairQr.Encode 没错」，证不了「界面上画的是它」——
# 用户报的正是「连接界面没有二维码」（整块漏做），这一条从**屏幕像素**往回解。
#
# 用法：powershell -ExecutionPolicy Bypass -File tools\verify_pair_qr.ps1
# 退出码：0 = 全过；1 = 有 FAIL。

param(
    [string]$Root = 'D:\DeepseekHarness\TemporaryConversationFile\pair-qr-probe',
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $repo ("windows\QuizSync.App\bin\$Configuration\net10.0-windows10.0.26100.0\win-x64\QuizSync.App.exe")
$cli = Join-Path $repo ("windows\QuizSync.Provider.Cli\bin\$Configuration\net10.0-windows\QuizSync.Provider.Cli.exe")

$pass = 0
$fail = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { $script:pass++; Write-Host ("  PASS  " + $name + $(if ($detail) { "   ($detail)" } else { '' })) -ForegroundColor Green }
    else { $script:fail++; Write-Host ("  FAIL  " + $name + $(if ($detail) { "   ($detail)" } else { '' })) -ForegroundColor Red }
}

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

# 截图取的是**屏幕上**那一块，被别的窗口压住就会拍到别人（实测：紧跟在界面探针之后跑，
# 控制台窗口在最前，二维码那块截出来是控制台 → 解码失败、假红）。所以截图前要确认
# 「自己就是前台窗口」，并且允许重试几次。
Add-Type -Namespace PairQrProbe -Name Win -MemberDefinition @'
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
'@

if (-not (Test-Path $exe)) { throw "找不到 $exe —— 先构建" }
if (-not (Test-Path $cli)) { throw "找不到 $cli —— 先构建 QuizSync.Provider.Cli" }

Get-Process QuizSync.App -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 900
Remove-Item -Recurse -Force $Root -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $Root | Out-Null

# 只写要用的两项：开连接设备（会把主机内嵌起来）、关悬浮窗（别挡住截图）。
@{ connect_enabled = $true; float_enabled = $false; theme = 'light' } |
    ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $Root 'app.json')

# ⚠️ **服务端数据目录也指到临时目录**（第 170 轮补的，原来只指了 app 目录）：
# 只指 app 目录时，临时实例会拿**真实主机目录**的控制令牌去配对，而服务端 `PairingService`
# 每次配对都发新令牌并覆盖 `devices.token_hash`（`paired_at` 保留首次配对时间，所以从
# 「已配对设备」列表上完全看不出来）—— 结果是**用户那份 `device.token` 当场作废**：
# 用户侧表现是「识别完了推送 401、改动留在本机、手机再也不同步」。
# 合并到临时主机之后，这一跑用的是临时主机自己的控制令牌与设备表，用户数据一个字节都不碰。
$serverDir = Join-Path $Root 'server'
New-Item -ItemType Directory -Force -Path $serverDir | Out-Null
$env:QS_APP_DATA = $Root
$env:QS_SERVER_DATA = $serverDir
$proc = Start-Process -FilePath $exe -PassThru
Remove-Item Env:\QS_APP_DATA -ErrorAction SilentlyContinue
Remove-Item Env:\QS_SERVER_DATA -ErrorAction SilentlyContinue

try {
    Write-Host "`n=== 等主机就绪（最多 30 秒）===" -ForegroundColor Cyan
    $tokenPath = Join-Path $serverDir 'control.token'
    $legacyTokenPath = Join-Path $Root 'control.token'
    $baseUrl = $null
    foreach ($attempt in 1..120) {
        Start-Sleep -Milliseconds 250
        foreach ($candidate in 8765..8770) {
            try {
                $health = Invoke-RestMethod -Uri "http://127.0.0.1:$candidate/health" -TimeoutSec 1
                if ($health) { $baseUrl = "http://127.0.0.1:$candidate"; break }
            }
            catch { }
        }
        if ($baseUrl) { break }
    }
    Check '主机已在监听（内嵌在应用进程内）' ($null -ne $baseUrl) "$baseUrl"

    # 控制令牌：临时主机起来时自己写的（`HostControl.ReadOrCreateControlToken`）。
    $control = if (Test-Path $tokenPath) { (Get-Content $tokenPath -Raw).Trim() } else { $null }
    if (-not $control -and (Test-Path $legacyTokenPath)) { $control = (Get-Content $legacyTokenPath -Raw).Trim() }
    Check '读到控制令牌（control.token）' (-not [string]::IsNullOrWhiteSpace($control)) $(if ($control) { "$($control.Length) 字符" } else { '没有' })

    $expected = $null
    if ($baseUrl -and $control) {
        $expected = Invoke-RestMethod -Uri "$baseUrl/api/v1/pair/code" -Headers @{ 'X-QS-Control' = $control } -TimeoutSec 5
    }
    Check '控制面给出了配对码' ($null -ne $expected.code) $(if ($expected) { "code=$($expected.code) port=$($expected.port)" } else { '' })

    Write-Host "`n=== 进「设置 → 连接设备」（UIA 点齿轮 + 点左导航）===" -ForegroundColor Cyan
    $window = $null
    foreach ($attempt in 1..40) {
        Start-Sleep -Milliseconds 500
        $candidates = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
            [System.Windows.Automation.TreeScope]::Children,
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)))
        $window = $candidates |
            Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Window } |
            Sort-Object { -($_.Current.BoundingRectangle.Width * $_.Current.BoundingRectangle.Height) } |
            Select-Object -First 1
        if ($window) { break }
    }
    Check '找到应用主窗口（UIA）' ($null -ne $window)

    # 全新数据目录会落在「第一次使用」门页（一个合集都没有，SPEC §2.3.1）——
    # 这里像用户那样**真的建一个合集**走进去，别为了省事去改判据。
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
            $nameBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('探针合集')
            Start-Sleep -Milliseconds 600
        }

        $gate.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Seconds 4
    }

    $gear = $window.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'SettingsButton')))
    Check '找到顶栏齿轮' ($null -ne $gear)
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

    # ---- 先把这一页滚到底，再断言 ----
    #
    # 两个坑都是**探针自己的**（第一版跑绿、第二跑变红就是因为它们）：
    #   ① 二维码在配对块里，第一屏往往只露一半（实测元素被视口裁成 540×445）——
    #      裁图裁到半张二维码，当然解不出来；`ScrollItemPattern.ScrollIntoView()` 也不保证
    #      「完整可见」，它只保证「贴到最近的边」。
    #   ② UIA 树只列**已实现**的节点：按钮滚出视口就查不到，于是「页面上有『复制配对链接』」
    #      这条断言会随滚动位置假红。
    # 所以顺序改成：滚到底 → 单独找滚动容器把二维码滚进完整可见 → 再 dump + 断言 + 截屏。
    Write-Host "`n=== 把二维码滚成「完整可见」 ===" -ForegroundColor Cyan
    $scroll = $null
    foreach ($el in $window.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)) {
        $pattern = $null
        if ($el.TryGetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern, [ref]$pattern) -and
            $pattern.Current.VerticallyScrollable) {
            $scroll = $pattern
            break
        }
    }

    $bounds = $window.Current.BoundingRectangle
    $qr = $null
    foreach ($attempt in 1..10) {
        $images = $window.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::Image)))
        $qr = $images | Where-Object { $_.Current.BoundingRectangle.Width -gt 300 } |
            Sort-Object { -$_.Current.BoundingRectangle.Width } | Select-Object -First 1
        if ($qr) { break }
        Start-Sleep -Milliseconds 500
    }
    Check '页面上真的有一个二维码图像元素（宽 > 300）' ($null -ne $qr) `
        $(if ($qr) { "$([int]$qr.Current.BoundingRectangle.Width)x$([int]$qr.Current.BoundingRectangle.Height)" } else { '没找到' })

    # 判据：正方形图片元素被视口裁掉时高度会小于宽度（实测 540×445）。
    # 手法：先 ScrollIntoView（贴到最近的边），再按需要一小步一小步挪 —— 只挪动的方向来自
    # 「元素底边越过视口下沿」还是「顶边被顶栏挡住」，不靠预设的滚动量（页面高度随数据变，
    # 先前那版「先滚到底」就把配对块顶出视口了）。
    if ($qr) {
        try {
            $qr.GetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern).ScrollIntoView()
            Start-Sleep -Milliseconds 400
        }
        catch {
            Write-Host "  （ScrollIntoView 不可用：$($_.Exception.Message)）" -ForegroundColor DarkGray
        }

        foreach ($attempt in 1..14) {
            $rect = $qr.Current.BoundingRectangle
            $fullyVisible = ($rect.Height -ge ($rect.Width - 4)) -and
                ($rect.Top -ge ($bounds.Top + 70)) -and ($rect.Bottom -le ($bounds.Bottom - 4))
            if ($fullyVisible) { break }
            if (-not $scroll) { break }

            $amount = if ($rect.Bottom -gt ($bounds.Bottom - 4)) {
                [System.Windows.Automation.ScrollAmount]::SmallIncrement   # 内容上移
            }
            else {
                [System.Windows.Automation.ScrollAmount]::SmallDecrement   # 内容下移
            }

            try {
                $scroll.Scroll([System.Windows.Automation.ScrollAmount]::NoAmount, $amount)
            }
            catch {
                Write-Host "  （滚动失败：$($_.Exception.Message)）" -ForegroundColor DarkGray
                break
            }

            Start-Sleep -Milliseconds 250
        }

        $rect = $qr.Current.BoundingRectangle
        Check '二维码元素完整可见（没被滚动视口裁掉）' `
            (($rect.Height -ge ($rect.Width - 4)) -and ($rect.Top -ge ($bounds.Top + 70)) -and ($rect.Bottom -le ($bounds.Bottom - 4))) `
            ("元素 $([int]$rect.Width)x$([int]$rect.Height)，窗口 y {0:N0}..{1:N0}" -f ($bounds.Top + 70), ($bounds.Bottom - 4))
    }

    # 页面上必须真的有配对码文字与地址（二维码本身在 UIA 里看不到，只能靠像素解）。
    $dump = Join-Path $Root 'uia.txt'
    & powershell -ExecutionPolicy Bypass -File (Join-Path $repo 'tools\dump_uia.ps1') `
        -ProcessName 'QuizSync.App' -MaxDepth 10 -OutFile $dump | Out-Null
    $lines = Get-Content $dump -Encoding UTF8
    $has = { param($pattern) (($lines | Select-String -Pattern $pattern -SimpleMatch | Measure-Object).Count -gt 0) }

    Check '页面上有「刷新配对码」按钮' (& $has '刷新配对码')
    if ($expected.code) {
        Check "页面上显示出配对码 $($expected.code)" (& $has ("name=[" + $expected.code + "]"))
    }
    Check '页面上写了局域网地址（192.168/10./172.）' (((& $has '地址：192.168') -or (& $has '地址：10.') -or (& $has '地址：172.')))
    Check '页面上的倒计时有内容（还剩 N 秒 / 已过期）' (((& $has '还剩') -or (& $has '已过期')))

    Write-Host "`n=== 截屏 + 解二维码（像素取证）===" -ForegroundColor Cyan

    $bounds = $window.Current.BoundingRectangle
    $shot = Join-Path $Root 'pair-page.png'
    $crop = Join-Path $Root 'qr-crop.png'
    $hwnd = [IntPtr]$window.Current.NativeWindowHandle
    $decoded = $null
    $attempts = 0

    # 最多试 4 次：每次先把自己弄成前台窗口，再截屏 → 裁出二维码 → 解码。
    # `CopyFromScreen` 拍的是**屏幕上**那一块，被别的窗口压住就拍到别人 ——
    # 实测：紧跟在界面探针之后跑（控制台在最前）会稳定假红，所以必须「确认前台 + 重试」。
    foreach ($attempt in 1..4) {
        $attempts = $attempt

        $foreground = [PairQrProbe.Win]::GetForegroundWindow()
        if ($hwnd -ne [IntPtr]::Zero -and $foreground -ne $hwnd) {
            [void][PairQrProbe.Win]::SetForegroundWindow($hwnd)
            try { $window.SetFocus() } catch { }
            Start-Sleep -Milliseconds 700
        }

        $bmp = New-Object System.Drawing.Bitmap([int]$bounds.Width, [int]$bounds.Height)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen([int]$bounds.X, [int]$bounds.Y, 0, 0, (New-Object System.Drawing.Size([int]$bounds.Width, [int]$bounds.Height)))
        $g.Dispose()
        $bmp.Save($shot, [System.Drawing.Imaging.ImageFormat]::Png)

        # 只把二维码那一块裁出来解：整屏 2360×1520 里有大量文字，二值化后反而更难定位。
        if ($qr) {
            $r = $qr.Current.BoundingRectangle
            $x = [int]([math]::Max(0, $r.X - $bounds.X - 8))
            $y = [int]([math]::Max(0, $r.Y - $bounds.Y - 8))
            $w = [int]([math]::Min($r.Width + 16, $bounds.Width - $x))
            $h = [int]([math]::Min($r.Height + 16, $bounds.Height - $y))
            if ($attempt -eq 1) {
                Write-Host "  二维码元素在窗口内 ($x,$y) $($w)x$($h)（窗口 $([int]$bounds.Width)x$([int]$bounds.Height)）"
            }

            if ($w -gt 20 -and $h -gt 20) {
                $piece = $bmp.Clone((New-Object System.Drawing.Rectangle($x, $y, $w, $h)), $bmp.PixelFormat)
                $piece.Save($crop, [System.Drawing.Imaging.ImageFormat]::Png)
                $piece.Dispose()
                & $cli qrz $crop | Out-File -Encoding utf8 (Join-Path $Root 'decoded.txt')
                $decoded = (Get-Content (Join-Path $Root 'decoded.txt') -Encoding UTF8 | Select-Object -First 1)
            }
        }

        $bmp.Dispose()
        if (-not [string]::IsNullOrWhiteSpace($decoded)) { break }

        Write-Host "  （第 $attempt 次没解出来：多半是截图被别的窗口盖住了，重试）" -ForegroundColor DarkGray
        Start-Sleep -Milliseconds 600
    }

    Write-Host "  截图：$shot；裁剪：$crop（试了 $attempts 次）"
    Check '从屏幕像素里解出了二维码' (-not [string]::IsNullOrWhiteSpace($decoded)) "$decoded"

    if ($decoded -and $expected.code) {
        Check '解出来的深链是 quizsync://pair?...&v=1' ($decoded -match '^quizsync://pair\?host=.+&port=\d+&code=\d{6}(&sid=.+)?&v=1$') $decoded
        Check '解出来的 code 与主机控制面一致' ($decoded -match ("code=" + [regex]::Escape($expected.code))) "期望 code=$($expected.code)"
        Check '解出来的 port 与主机实际端口一致' ($decoded -match ("port=" + $expected.port + "&")) "期望 port=$($expected.port)"
        Check 'host 不是 127.0.0.1（否则手机扫到会连自己）' (-not ($decoded -match 'host=127\.0\.0\.1')) $decoded
    }

    # 「复制配对链接」按钮在二维码**下面**一排：把二维码顶成完整可见之后它就落在视口外，
    # 而 UIA 树只列已实现的节点 —— 再往下挪两步单独断言它（顺序不能省：一条断言一次 dump）。
    if ($scroll) {
        foreach ($step in 1..3) {
            try {
                $scroll.Scroll(
                    [System.Windows.Automation.ScrollAmount]::NoAmount,
                    [System.Windows.Automation.ScrollAmount]::SmallIncrement)
            }
            catch {
                break
            }

            Start-Sleep -Milliseconds 250
        }

        $dump2 = Join-Path $Root 'uia-buttons.txt'
        & powershell -ExecutionPolicy Bypass -File (Join-Path $repo 'tools\dump_uia.ps1') `
            -ProcessName 'QuizSync.App' -MaxDepth 10 -OutFile $dump2 | Out-Null
        $lines2 = Get-Content $dump2 -Encoding UTF8
        $has2 = { param($pattern) (($lines2 | Select-String -Pattern $pattern -SimpleMatch | Measure-Object).Count -gt 0) }
        Check '页面上有「复制配对链接」按钮' (& $has2 '复制配对链接')
    }
}
finally {
    Get-Process QuizSync.App -ErrorAction SilentlyContinue | Stop-Process -Force
}

Write-Host ("`n结果：PASS {0} / FAIL {1}" -f $pass, $fail) -ForegroundColor $(if ($fail -eq 0) { 'Green' } else { 'Red' })
exit $(if ($fail -eq 0) { 0 } else { 1 })
