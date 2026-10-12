# 悬浮窗运行时探针：窗口真的建出来了吗？尺寸/置顶/任务栏可见性对不对？
#
# 为什么必须有它：编译通过只说明**代码能编**，说明不了「窗口出现了、大小按外观算对了、
# 置顶标志设上了」。而这几件事恰好是悬浮窗最容易「看起来做了其实没做」的地方。
#
# 用法：powershell -ExecutionPolicy Bypass -File tools\probe_float_window.ps1
# 退出码：0 = 断言全过；1 = 有 FAIL 或前置失败。

param(
    [string]$ProcessName = 'QuizSync.App',
    [string]$Root = 'D:\DeepseekHarness\TemporaryConversationFile\float-probe',
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    # 外观与显示比例：默认标准外观 @100%。换一组参数重跑 = **尺寸换算的第二个数据点**
    # （只测一组时，「算对了」与「恰好蒙对」分不开）。
    [ValidateSet('portrait', 'landscape')][string]$Aspect = 'portrait',
    [double]$Scale = 1.0,
    # 真鼠标那两条判据（默认关）：圆角到底切没切、顶栏到底能不能拖，最终判据是
    # 「按住它拖一下，窗口动不动」。它依赖 `SetCursorPos` 真的生效 —— 而这台机器上
    # **人手一动鼠标就会把坐标抢走**（实测：同一次运行里三次落点各不相同），
    # 所以默认不跑，需要取证时显式加 `-Mouse`（脚本会在按下去之前复核落点、最多重试 3 次）。
    [switch]$Mouse
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $repo ("windows\QuizSync.App\bin\$Configuration\net10.0-windows10.0.26100.0\win-x64\QuizSync.App.exe")
$dotnet = if ($env:QS_DOTNET) { $env:QS_DOTNET } else { 'dotnet' }

$pass = 0
$fail = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { $script:pass++; Write-Host ("  PASS  " + $name + $(if ($detail) { "   ($detail)" } else { '' })) -ForegroundColor Green }
    else { $script:fail++; Write-Host ("  FAIL  " + $name + $(if ($detail) { "   ($detail)" } else { '' })) -ForegroundColor Red }
}

Add-Type -Namespace FloatProbe -Name Win -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
public delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
[DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
[DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string win);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
[DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
[DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
[DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
[DllImport("gdi32.dll")] public static extern int GetPixel(IntPtr dc, int x, int y);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
[StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
[DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
[DllImport("user32.dll")] public static extern IntPtr SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
[DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);
'@

# 第 168 轮新增的三条判据（用户报「悬浮窗有点拖拽不动，也没圆角」）：
#   * `WM_NCHITTEST`  = 0x0084：顶栏空白处必须回 `HTCAPTION(2)`（Windows 会自己拖这个窗口），
#                        图标按钮那一片必须仍是 `HTCLIENT(1)`（否则按钮点不下去、只会拖窗）；
#                        lParam 是**屏幕**坐标，且要投给**顶层窗口**（内容子窗口永远回 HTCLIENT）。
#   * `WM_MOUSEACTIVATE` = 0x0021：必须回 `MA_NOACTIVATE(3)`（拖一下不该把焦点抢走，SPEC §6.1）。
#   * 圆角：同一位置在「窗口显示 / 隐藏」两种状态下取色 —— 角点颜色不变 = 那一点不属于窗口
#          （被窗口区域切掉了），顶边中点会变 = 那条边确实是我们的窗口（对照组）。
function Get-HitTest([IntPtr]$hwnd, [int]$screenX, [int]$screenY) {
    $lp = [IntPtr](($screenY -shl 16) -bor ($screenX -band 0xFFFF))
    return [int]([FloatProbe.Win]::SendMessage($hwnd, 0x0084, [IntPtr]::Zero, $lp))
}

function HitTestName([int]$rc) {
    if ($rc -eq 1) { return 'HTCLIENT' }
    if ($rc -eq 2) { return 'HTCAPTION' }
    if ($rc -eq 0) { return 'HTNOWHERE' }
    return "其他($rc)"
}

function Get-Windows([int]$processId) {
    $list = New-Object System.Collections.Generic.List[object]
    $cb = [FloatProbe.Win+EnumWindowsProc]{
        param($h, $l)
        $owner = 0
        [void][FloatProbe.Win]::GetWindowThreadProcessId($h, [ref]$owner)
        if ($owner -eq $processId) {
            $r = New-Object FloatProbe.Win+RECT
            [void][FloatProbe.Win]::GetWindowRect($h, [ref]$r)
            $cls = New-Object System.Text.StringBuilder 256
            [void][FloatProbe.Win]::GetClassName($h, $cls, 256)
            $title = New-Object System.Text.StringBuilder 256
            [void][FloatProbe.Win]::GetWindowTextW($h, $title, 256)
            $ex = [int64][FloatProbe.Win]::GetWindowLongPtr($h, -20)
            $list.Add([pscustomobject]@{
                Hwnd     = $h
                Class    = $cls.ToString()
                Title    = $title.ToString()
                Left     = $r.Left
                Top      = $r.Top
                Width    = $r.Right - $r.Left
                Height   = $r.Bottom - $r.Top
                Visible  = [FloatProbe.Win]::IsWindowVisible($h)
                Topmost  = ($ex -band 0x8) -ne 0
                ToolWin  = ($ex -band 0x80) -ne 0
                Dpi      = [FloatProbe.Win]::GetDpiForWindow($h)
            })
        }
        return $true
    }
    [void][FloatProbe.Win]::EnumWindows($cb, [IntPtr]::Zero)
    return $list
}

Write-Host "`n=== 准备 ===" -ForegroundColor Cyan
# ⚠️ **必须先声明 DPI 感知**：PowerShell 默认不是 DPI-aware，`GetWindowRect` 会返回被系统
# 缩放过的**虚拟化**坐标（本机 200% → 一律小一半），于是一个完全正确的窗口会被判成
# 「尺寸只有期望值的一半」。第一次跑就踩了：主窗口报 1180×760 而 DPI=192 —— 1180 是**逻辑**值，
# 物理值应是 2360。AGENTS.md 也记过这条（对比坐标前先统一口径）。
[void][FloatProbe.Win]::SetProcessDPIAware()

if (-not (Test-Path $exe)) { throw "找不到 $exe —— 先构建（-Configuration $Configuration）" }

Get-Process $ProcessName -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800

Remove-Item -Recurse -Force $Root -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $Root | Out-Null

# 只写要断言的几项：其余键由 AppSettings 的默认值兜底（默认极简 = true、置顶 = true）。
@{
    float_enabled = $true
    float_aspect  = $Aspect
    float_scale   = $Scale
    float_topmost = $true
    theme         = 'light'
} | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $Root 'app.json')

# 外观表与两端实现同源（`Pages/FloatWindow.xaml.cs` 的 Aspects / `SettingsTabs.cs` 的 FloatAspects）。
# 第 170 轮按用户要求删掉了 `tall`（加长外观 9:30）。
$aspects = @{
    portrait  = @{ Ratio = 9.0 / 20.0; BaseWidth = 342; Hint = '342 × 760' }
    landscape = @{ Ratio = 20.0 / 9.0; BaseWidth = 560; Hint = '560 × 252' }
}
$shape = $aspects[$Aspect]
# 显示比例的合法区间（与 `AppSettings.MinScale`/`MaxScale` 同源）：超出的值会被夹回来，
# 所以「期望尺寸」也要按夹取后的比例算，否则用 -Scale 0.5 跑会得到一个假的 FAIL。
$clampedScale = [math]::Max([math]::Min($Scale, 2.0), 1.0)
$expectedLogicalWidth = $shape.BaseWidth * $clampedScale
$expectedLogicalHeight = $expectedLogicalWidth / $shape.Ratio
Write-Host ("`n=== 本轮参数：{0} @ {1:N0}%（夹取后 {2:N0}%）→ 期望 {3:N0}×{4:N0} 逻辑像素 ===" -f `
    $Aspect, ($Scale * 100), ($clampedScale * 100), $expectedLogicalWidth, $expectedLogicalHeight) -ForegroundColor Cyan

# ⚠️ **两个数据目录都要指到临时目录**（第 170 轮血的教训）：只指 `QS_APP_DATA` 的话，
# 临时实例会拿**真实主机目录**里的控制令牌去配对，服务端每次配对都发新 token 并覆盖
# `devices.token_hash`（`paired_at` 保留首次时间，所以从设备列表上看不出来），
# 于是**用户那份 `device.token` 当场作废** —— 表现是用户侧「识别完推送 401、手机再也不同步」。
# 这一条的现场就是 `probe_float_window.ps1` / `verify_pair_qr.ps1` 这类脚本造成的。
$env:QS_APP_DATA = $Root
$env:QS_SERVER_DATA = Join-Path $Root 'server'
New-Item -ItemType Directory -Force -Path (Join-Path $Root 'server') | Out-Null
$proc = Start-Process -FilePath $exe -PassThru
Remove-Item Env:\QS_APP_DATA -ErrorAction SilentlyContinue
Remove-Item Env:\QS_SERVER_DATA -ErrorAction SilentlyContinue

try {
    Write-Host "`n=== 等窗口出现（最多 20 秒）===" -ForegroundColor Cyan
    $float = $null
    foreach ($attempt in 1..80) {
        Start-Sleep -Milliseconds 250
        $windows = Get-Windows $proc.Id
        # 判据：标题是 `QuizSyncFloat`（它没有标题栏，用户看不到这个名字，是给探针认窗口用的）。
        # 早期版本按「宽高比 = 9:20」找 —— 换外观就失效了，而且主窗口换尺寸后可能撞上。
        $float = $windows | Where-Object { $_.Visible -and $_.Title -eq 'QuizSyncFloat' } | Select-Object -First 1
        if ($float) { break }
    }

    $windows = Get-Windows $proc.Id
    Write-Host "`n=== 该进程的顶层窗口 ===" -ForegroundColor Cyan
    $windows | ForEach-Object {
        Write-Host ("  {0,-24} {1,5}x{2,-5} at ({3},{4}) visible={5} topmost={6} toolwin={7} dpi={8} title=[{9}]" -f `
            $_.Class, $_.Width, $_.Height, $_.Left, $_.Top, $_.Visible, $_.Topmost, $_.ToolWin, $_.Dpi, $_.Title)
    }

    Write-Host "`n=== 断言 ===" -ForegroundColor Cyan
    Check '悬浮窗存在（按标题 QuizSyncFloat 认窗口）' ($null -ne $float) $(if ($float) { "$($float.Width)x$($float.Height)" } else { '没找到' })

    if ($float) {
        $factor = $float.Dpi / 96.0
        $expectedW = [int][math]::Round($expectedLogicalWidth * $factor)
        $expectedH = [int][math]::Round($expectedLogicalHeight * $factor)
        Check ("宽度 = {0:N0} 逻辑像素 × DPI" -f $expectedLogicalWidth) `
            ([math]::Abs($float.Width - $expectedW) -le 2) "实际 $($float.Width) 期望 $expectedW（DPI $($float.Dpi)，$Aspect @$([int]($Scale*100))%）"
        Check ("高度 = {0:N0} 逻辑像素 × DPI" -f $expectedLogicalHeight) `
            ([math]::Abs($float.Height - $expectedH) -le 2) "实际 $($float.Height) 期望 $expectedH"
        Check '置顶标志已设（float_topmost=true）' $float.Topmost 'WS_EX_TOPMOST'
        Check '不进任务栏 / Alt+Tab（IsShownInSwitchers=false）' $float.ToolWin 'WS_EX_TOOLWINDOW'
        Check '默认位置在屏幕右侧（右边缘留 24 逻辑像素）' ($float.Left -gt 0) "x=$($float.Left)"

        # ---- 拖动区（用户 2026-09-29：「悬浮窗有点拖拽不动」）----
        # 顶栏被登记成 `NonClientRegionKind.Caption` 之后，Windows 会**自己**拖这个窗口：
        # 判据就是 `WM_NCHITTEST` 的返回值。**不去模拟真实拖动** —— 本环境的合成鼠标输入
        # 不生效（对照：注入的点击连任务栏时钟都点不开；SendInput 返回 1 但系统没受理），
        # 那是探针的已知限制，不是界面的判据。
        $hwnd = $float.Hwnd
        $floatRight = $float.Left + $float.Width
        $floatBottom = $float.Top + $float.Height
        $topBarY = $float.Top + [int](14 * $factor)
        $leftBlank = $float.Left + [int](40 * $factor)

        # 「顶栏空白处」要**按实际登记下来的拖动区宽度**取样（第 170 轮）：
        # 图标按钮从 30 加宽到 34 逻辑像素之后拖动区跟着变窄（342 逻辑宽的窗口里约 150），
        # 而这里原来硬编码 160 —— 取样点会落到按钮那一片，判据就假红了。
        # 拖动区宽度是应用自己写进日志的（`悬浮窗：标题栏拖动区已登记（宽 N px …）`），读它就是读事实。
        $regionPx = 0
        $logPath = Join-Path $Root 'logs\crash.log'
        if (Test-Path $logPath) {
            $match = Select-String -Path $logPath -Encoding UTF8 -Pattern '标题栏拖动区已登记（宽 (\d+) px' |
                Select-Object -Last 1
            if ($match) { $regionPx = [int]$match.Matches[0].Groups[1].Value }
        }
        if ($regionPx -le 8) { $regionPx = [int](140 * $factor) }   # 没读到日志就退到一个保守值
        $midBlank = $float.Left + [int]($regionPx / 2)
        Write-Host ("  拖动区宽度（读应用日志）：$regionPx px → 取样点距左边 {0} px" -f ($midBlank - $float.Left))

        $buttons = $floatRight - [int](30 * $factor)
        $body = $float.Top + [int](400 * $factor)

        $rcLeft = Get-HitTest $hwnd $leftBlank $topBarY
        $rcMid = Get-HitTest $hwnd $midBlank $topBarY
        $rcButtons = Get-HitTest $hwnd $buttons $topBarY
        $rcBody = Get-HitTest $hwnd $midBlank $body

        Check '顶栏左侧空白处是系统的标题栏拖动区（按住由 Windows 拖窗口）' ($rcLeft -eq 2) (HitTestName $rcLeft)
        Check '顶栏中部空白处是系统的标题栏拖动区' ($rcMid -eq 2) (HitTestName $rcMid)
        # ⚠️ 判据是「**不是** HTCAPTION」，不能写成「等于 HTCLIENT」：
        # 登记 caption 区域之后，WinUI 对「既不在 caption 区、也不是顶层窗口自己客户区」的点回
        # `HTNOWHERE`（实测：按钮那一片回 0）。真正接住内容点击的是内容子窗口
        # `Microsoft.UI.Content.DesktopChildSiteBridge`（它整片回 HTCLIENT，见下面那条断言）。
        Check '顶栏右侧图标按钮那一片不会被当成拖动（按钮还能点）' ($rcButtons -ne 2) (HitTestName $rcButtons)
        Check '内容区不会被当成拖动' ($rcBody -ne 2) (HitTestName $rcBody)

        # ---- 非客户区边框：**一圈黑线 + 一圈点不动的死区**（用户 2026-09-30 报的第 2、5 条）----
        # 修之前：`SetBorderAndTitleBar(false,false)` 之后窗口仍带 `WS_DLGFRAME`，
        # 客户区比窗口小 3 物理像素（684×1520 → 678×1514），四条边最外 3 像素一律回
        # `HTBORDER(18)` —— 系统画的那圈边就是用户看到的黑线，而 HTBORDER 上的点击
        # 系统什么都不做（点不动的死区）。修法：去掉边框样式、改 `WS_POPUP`（`StripNonClientFrame`）。
        $clientRect = New-Object FloatProbe.Win+RECT
        $clientOrigin = New-Object FloatProbe.Win+POINT
        [void][FloatProbe.Win]::GetClientRect($hwnd, [ref]$clientRect)
        [void][FloatProbe.Win]::ClientToScreen($hwnd, [ref]$clientOrigin)
        $insetLeft = $clientOrigin.X - $float.Left
        $insetTop = $clientOrigin.Y - $float.Top
        $insetRight = $floatRight - ($clientOrigin.X + ($clientRect.Right - $clientRect.Left))
        $insetBottom = $floatBottom - ($clientOrigin.Y + ($clientRect.Bottom - $clientRect.Top))
        Check '客户区 = 整个窗口（不再有非客户区边框 = 用户看到的那圈黑线）' `
            (($insetLeft -eq 0) -and ($insetTop -eq 0) -and ($insetRight -eq 0) -and ($insetBottom -eq 0)) `
            ("inset=($insetLeft,$insetTop,$insetRight,$insetBottom)，客户区 $($clientRect.Right - $clientRect.Left)x$($clientRect.Bottom - $clientRect.Top)")

        $borderHits = @()
        foreach ($y in @(2, [int]($float.Height / 2), ($float.Height - 3))) {
            foreach ($x in @(2, [int]($float.Width / 2), ($float.Width - 3))) {
                $borderHits += (Get-HitTest $hwnd ($float.Left + $x) ($float.Top + $y))
            }
        }
        Check '窗口内没有任何 HTBORDER 死区（四条边 + 四角都是可交互的）' `
            (@($borderHits | Where-Object { $_ -eq 18 }).Count -eq 0) `
            ("采样命中码：" + (($borderHits | ForEach-Object { HitTestName $_ }) -join ','))

        # ---- 圆角（用户 2026-09-29：「也没圆角」）----
        # 判据：**窗口显示 / 隐藏两次取同样的点**，四个角上的像素在两种状态下必须一样
        # （说明那一点不属于窗口，被 `SetWindowRgn` 的圆角切掉了），
        # 四条边的中点必须不一样（对照组：那些点确实是窗口画的）。
        # 为什么不用 `GetWindowRgn` 直接读区域：实测它在自绘窗口上回 ERROR，判不出有没有区域。
        $cornerShown = @{}
        $cornerHidden = @{}
        foreach ($state in @('shown', 'hidden')) {
            [void][FloatProbe.Win]::ShowWindow($hwnd, $(if ($state -eq 'shown') { 5 } else { 0 }))
            Start-Sleep -Milliseconds 800
            $dc = [FloatProbe.Win]::GetDC([IntPtr]::Zero)
            $r = New-Object FloatProbe.Win+RECT
            [void][FloatProbe.Win]::GetWindowRect($hwnd, [ref]$r)
            $midX = [int](($r.Left + $r.Right) / 2)
            $midY = [int](($r.Top + $r.Bottom) / 2)
            $points = @{
                'cornerTL' = @(($r.Left + 2), ($r.Top + 2))
                'cornerTR' = @(($r.Right - 3), ($r.Top + 2))
                'cornerBL' = @(($r.Left + 2), ($r.Bottom - 3))
                'cornerBR' = @(($r.Right - 3), ($r.Bottom - 3))
                'edgeTop'  = @($midX, ($r.Top + 2))
                'edgeLeft' = @(($r.Left + 2), $midY)
            }
            $colors = @{}
            foreach ($key in $points.Keys) {
                $colors[$key] = [FloatProbe.Win]::GetPixel($dc, $points[$key][0], $points[$key][1])
            }

            [void][FloatProbe.Win]::ReleaseDC([IntPtr]::Zero, $dc)
            if ($state -eq 'shown') { $cornerShown = $colors } else { $cornerHidden = $colors }
        }
        [void][FloatProbe.Win]::ShowWindow($hwnd, 5)
        Start-Sleep -Milliseconds 500

        foreach ($key in @('cornerTL', 'cornerTR', 'cornerBL', 'cornerBR')) {
            Check ("{0} 不是窗口画的像素（圆角切掉了）" -f $key) `
                ($cornerShown[$key] -eq $cornerHidden[$key]) `
                ("显示时 0x{0:X6} / 隐藏后 0x{1:X6}" -f $cornerShown[$key], $cornerHidden[$key])
        }
        foreach ($key in @('edgeTop', 'edgeLeft')) {
            Check ("{0} 确实是窗口画的像素（圆角判据的对照组）" -f $key) `
                ($cornerShown[$key] -ne $cornerHidden[$key]) `
                ("显示时 0x{0:X6} / 隐藏后 0x{1:X6}" -f $cornerShown[$key], $cornerHidden[$key])
        }

        # 内容子窗口：内容区必须回 HTCLIENT（真实点击是它接住的）。
        $child = [FloatProbe.Win]::FindWindowEx($hwnd, [IntPtr]::Zero, 'Microsoft.UI.Content.DesktopChildSiteBridge', $null)
        Check '内容子窗口存在（DesktopChildSiteBridge）' ($child -ne [IntPtr]::Zero) `
            $(if ($child -ne [IntPtr]::Zero) { "0x$('{0:X}' -f $child.ToInt64())" } else { '没有' })
        if ($child -ne [IntPtr]::Zero) {
            $rcChild = Get-HitTest $child $midBlank $body
            Check '内容子窗口在内容区回 HTCLIENT（点击会进 XAML）' ($rcChild -eq 1) (HitTestName $rcChild)
        }

        # WM_MOUSEACTIVATE：wParam=顶层窗口，lParam=MAKELPARAM(命中码, WM_LBUTTONDOWN)
        $ma = [int]([FloatProbe.Win]::SendMessage($hwnd, 0x0021, $hwnd, [IntPtr]((0x0201 -shl 16) -bor 2)))
        Check '拖它不会抢焦点（SPEC §6.1：点击浮层不该把前台窗口换掉）' ($ma -eq 3) "WM_MOUSEACTIVATE=$ma（3=MA_NOACTIVATE）"

        # ---- 圆角（用户 2026-09-29：「也没圆角」）----
        # 同一位置在「显示 / 隐藏」两种状态下取色：角点颜色**不变**说明那一点不属于窗口
        # 见上面「四个角 / 两条边的对照」那一段（第 170 轮已扩成多点）。

        # ---- 真鼠标那种判据（默认关，见 `-Mouse` 的说明）----
        if ($Mouse) {
            function Get-FloatRect([IntPtr]$h) {
                $r = New-Object FloatProbe.Win+RECT
                [void][FloatProbe.Win]::GetWindowRect($h, [ref]$r)
                return $r
            }

            # 在窗口内某一点按住、拖 (dx,dy)、松手；返回窗口是否真的跟着动了。
            # 落点用 SetCursorPos 摆好之后**立刻**复核一次：本机人手一动鼠标就会把坐标抢走，
            # 抢走就重试（最多 3 次），三次都被抢走才算判据不可用（当作 FAIL 并写清原因）。
            function Test-DragAt([int]$clientX, [int]$clientY, [int]$dx, [int]$dy) {
                $before = Get-FloatRect $hwnd
                $target = New-Object FloatProbe.Win+POINT
                $target.X = $clientX
                $target.Y = $clientY
                [void][FloatProbe.Win]::ClientToScreen($hwnd, [ref]$target)
                for ($try = 1; $try -le 3; $try++) {
                    [void][FloatProbe.Win]::SetCursorPos($target.X, $target.Y)
                    $now = New-Object FloatProbe.Win+POINT
                    [void][FloatProbe.Win]::GetCursorPos([ref]$now)
                    if (([math]::Abs($now.X - $target.X) -le 2) -and ([math]::Abs($now.Y - $target.Y) -le 2)) {
                        [FloatProbe.Win]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)
                        Start-Sleep -Milliseconds 120
                        foreach ($step in 1..6) {
                            [void][FloatProbe.Win]::SetCursorPos(
                                $target.X + [int]($dx * $step / 6), $target.Y + [int]($dy * $step / 6))
                            Start-Sleep -Milliseconds 40
                        }
                        [FloatProbe.Win]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)
                        Start-Sleep -Milliseconds 400
                        $after = Get-FloatRect $hwnd
                        # 拖回原位（下一次判据要用同一块地方）
                        if (($after.Left -ne $before.Left) -or ($after.Top -ne $before.Top)) {
                            [void][FloatProbe.Win]::SetCursorPos($after.Left + 30, $after.Top + 20)
                            Start-Sleep -Milliseconds 100
                            [FloatProbe.Win]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)
                            Start-Sleep -Milliseconds 80
                            foreach ($step in 1..6) {
                                [void][FloatProbe.Win]::SetCursorPos(
                                    $after.Left + 30 - [int](($after.Left - $before.Left) * $step / 6),
                                    $after.Top + 20 - [int](($after.Top - $before.Top) * $step / 6))
                                Start-Sleep -Milliseconds 30
                            }
                            [FloatProbe.Win]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)
                            Start-Sleep -Milliseconds 300
                        }
                        return (($after.Left -ne $before.Left) -or ($after.Top -ne $before.Top))
                    }
                }
                return $null   # 判据不可用（光标被抢走）
            }

            $stripMoved = Test-DragAt 150 25 30 30
            Check '按住顶栏空白处真的能拖动窗口（对照组）' ($stripMoved -eq $true) `
                $(if ($null -eq $stripMoved) { '光标被抢走，判据不可用' } else { "moved=$stripMoved" })

            $cornerMoved = Test-DragAt 2 2 30 30
            Check '窗口最外圈（圆角切掉的那 2 像素）不属于窗口，按住它拖不动' ($cornerMoved -eq $false) `
                $(if ($null -eq $cornerMoved) { '光标被抢走，判据不可用' } else { "moved=$cornerMoved" })
        }

        # ---- 位置落库（第 168 轮补的缺口）----
        # 拖动现在主要由 Windows 自己完成，那条路径**不经过** XAML 的 `PointerReleased` ——
        # 所以位置改为听 `AppWindow.Changed` + 400 ms 防抖落库。
        # 这里模拟「Windows 把窗口挪了」（SetWindowPos），断言设置文件跟着更新。
        $settingsPath = Join-Path $Root 'app.json'
        [void][FloatProbe.Win]::SetWindowPos($hwnd, [IntPtr]::Zero, $float.Left - 140, $float.Top + 90, 0, 0, 0x0011)  # NOSIZE|NOZORDER|NOACTIVATE
        Start-Sleep -Milliseconds 1600
        $moved = Get-Windows $proc.Id | Where-Object { $_.Title -eq 'QuizSyncFloat' } | Select-Object -First 1
        $saved = Get-Content -Raw -Encoding UTF8 $settingsPath | ConvertFrom-Json
        $savedX = [double]$saved.float_x
        $savedY = [double]$saved.float_y
        Check '窗口被外部挪动之后，位置会自己落库（原生拖动那条路也不丢位置）' `
            (([math]::Abs($savedX - ($moved.Left / $factor)) -le 2) -and ([math]::Abs($savedY - ($moved.Top / $factor)) -le 2)) `
            ("落库 ($savedX),($savedY) ← 实际逻辑 ({0:N1},{1:N1})" -f ($moved.Left / $factor), ($moved.Top / $factor))

        # 位置持久化：把实际位置写回设置（模拟用户拖过），再启动一次应当落在同一个位置。
        # 没有这条，「记住位置」就只是代码里的一句话。
        $settingsPath = Join-Path $Root 'app.json'
        $json = Get-Content -Raw -Encoding UTF8 $settingsPath | ConvertFrom-Json
        $json | Add-Member -NotePropertyName float_x -NotePropertyValue ($moved.Left / $factor) -Force
        $json | Add-Member -NotePropertyName float_y -NotePropertyValue ($moved.Top / $factor) -Force
        # 顺手塞一组**老配置文件里的值**（第 170 轮删掉了「加长外观」`tall` 与透明度那一项）：
        # 第二轮的窗口必须是「竖屏 9:20 @100%」—— 这同时钉住三件事：
        #   * `float_aspect = tall` 被归一成 portrait（不归一的话设置页那句
        #     `FloatAspects.First(...)` 会抛异常 = 进设置页就崩）；
        #   * `float_scale = 0.5` 被夹到 1.0（小于 1.0 时顶栏按钮会被切出窗外、点不动）；
        #   * `float_opacity` 这个键整个被忽略（透明度功能已删，窗口始终 100% 不透明）。
        $json | Add-Member -NotePropertyName float_aspect -NotePropertyValue 'tall' -Force
        $json | Add-Member -NotePropertyName float_scale -NotePropertyValue 0.5 -Force
        $json | Add-Member -NotePropertyName float_opacity -NotePropertyValue 0.3 -Force
        $json | ConvertTo-Json -Depth 4 | Set-Content -Encoding UTF8 $settingsPath
        $dpi = $float.Dpi
        $float = $moved   # 第二轮与「上次位置」比的是挪过之后的位置
    }
}
finally {
    Get-Process $ProcessName -ErrorAction SilentlyContinue | Stop-Process -Force
}

# 第二轮：同一个数据目录重启，浮窗应当回到记下来的位置（而不是又回到右侧默认位），
# 并且把老的 `tall` / `0.5` / `float_opacity` 按新口径读回来。
if ($dpi) {
    Start-Sleep -Milliseconds 800
    $env:QS_APP_DATA = $Root
    $env:QS_SERVER_DATA = Join-Path $Root 'server'
    $again = Start-Process -FilePath $exe -PassThru
    Remove-Item Env:\QS_APP_DATA -ErrorAction SilentlyContinue
    Remove-Item Env:\QS_SERVER_DATA -ErrorAction SilentlyContinue
    try {
        $restored = $null
        foreach ($attempt in 1..80) {
            Start-Sleep -Milliseconds 250
            $restored = Get-Windows $again.Id |
                Where-Object { $_.Visible -and $_.Title -eq 'QuizSyncFloat' } | Select-Object -First 1
            if ($restored) { break }
        }

        if ($restored) {
            $factor = $restored.Dpi / 96.0
            Check '重启后回到上次的位置（float_x / float_y 真的被读回来了）' `
                (([math]::Abs($restored.Left - $float.Left) -le 2) -and ([math]::Abs($restored.Top - $float.Top) -le 2)) `
                ("上次 ($($float.Left),$($float.Top)) → 本次 ($($restored.Left),$($restored.Top))")

            # portrait @100% = 342×760 逻辑像素（老配置里的 tall@50% 与它差得很远，不会被蒙对）
            $wantW = [int][math]::Round(342 * $factor)
            $wantH = [int][math]::Round(760 * $factor)
            Check '老配置里的「加长外观 + 50%」被归一/夹取成「竖屏 9:20 @100%」' `
                (([math]::Abs($restored.Width - $wantW) -le 2) -and ([math]::Abs($restored.Height - $wantH) -le 2)) `
                ("实际 $($restored.Width)x$($restored.Height)，期望 $wantW x $wantH（竖屏 @100%）")
            Check '老配置里的 float_opacity 不再影响窗口（透明度那一项已删）' `
                (([math]::Abs($restored.Width - $wantW) -le 2)) `
                "窗口尺寸与外层不透明度无关（不透明度不再被读）"
        }
        else {
            Check '重启后回到上次的位置（float_x / float_y 真的被读回来了）' $false '第二次启动没抓到浮窗'
        }
    }
    finally {
        Get-Process $ProcessName -ErrorAction SilentlyContinue | Stop-Process -Force
    }
}

Write-Host ("`n结果：PASS {0} / FAIL {1}" -f $pass, $fail) -ForegroundColor $(if ($fail -eq 0) { 'Green' } else { 'Red' })
exit $(if ($fail -eq 0) { 0 } else { 1 })
