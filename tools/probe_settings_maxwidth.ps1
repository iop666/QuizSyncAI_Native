# 验证「限宽后的居中分支」：把窗口拉宽到内容撞上 MaxWidth=940，量卡片与内容区的左右边距是否相等。
#
# 为什么单独跑：`HorizontalAlignment="Stretch"` + `MaxWidth` 在**未触顶**时是「铺满」，
# 触顶之后必须变成「居中」—— 这两个分支的观感完全不同，而只有把窗口拉宽才会走到第二个分支。
# 判据：卡片宽度 == 940 逻辑像素（200dpi 下 1880 物理），且左右边距符合「在内容区里居中」。
#
# ⚠️ 本机是 PowerShell **5.1**：管道符 `|` 不能出现在新行行首（报 Missing ')'），
# 所以每处都是「先收集到变量、再在行末接管道」。
#
# 用法：powershell -ExecutionPolicy Bypass -File tools/probe_settings_maxwidth.ps1

param(
    [string]$ProcessName = 'QuizSync.App',
    [int]$Dpi = 192
)

$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Get-MainWindow([int]$processId) {
    $candidates = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
        [System.Windows.Automation.TreeScope]::Children,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $processId)))
    $windows = $candidates |
        Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Window } |
        Sort-Object { -($_.Current.BoundingRectangle.Width * $_.Current.BoundingRectangle.Height) }
    return $windows | Select-Object -First 1
}

$proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { Write-Host 'ABORT: 应用没在运行（本脚本不负责启动）'; exit 2 }

$window = Get-MainWindow $proc.Id
if (-not $window) { Write-Host 'ABORT: 找不到主窗口'; exit 2 }

# 最大化（走 WindowPattern，等价于用户点最大化按钮）
$pattern = $window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
$pattern.SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Maximized)
Start-Sleep -Seconds 3

$window = Get-MainWindow $proc.Id
$winRect = $window.Current.BoundingRectangle

# 内容区卡片（Group 里最宽的那个 = 设置卡片）
$groups = $window.FindAll(
    [System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Group)))

$best = $null
foreach ($card in $groups) {
    $r = $card.Current.BoundingRectangle
    if ($r.Width -gt 400) {
        if ($null -eq $best -or $r.Width -gt $best.Width) { $best = $r }
    }
}

if ($null -eq $best) {
    # 设置页可能没打开：报出来，不假装通过
    Write-Host 'ABORT: 没找到设置卡片（先手动进设置页，或让 verify_windows_ui.ps1 把应用留在设置页）'
    $pattern.SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Normal)
    exit 2
}

$scale = $Dpi / 96.0
$expectedPhysical = 940 * $scale
$leftGap = $best.Left - $winRect.Left
$rightGap = $winRect.Right - $best.Right
# 左边距天然包含「左导航 232 逻辑 + 内容内边距 48 逻辑」；若内容在内容区里居中，
# 右边距应 ≈ 左边距 - 那两项。
$paneAndPadding = (232 + 48) * $scale
$expectedRightGap = $leftGap - $paneAndPadding

Write-Host ("窗口   : {0}x{1}   左上=({2},{3})" -f [int]$winRect.Width, [int]$winRect.Height, [int]$winRect.Left, [int]$winRect.Top)
Write-Host ("卡片   : 宽 {0}   左={1}" -f [int]$best.Width, [int]$best.Left)
Write-Host ("缩放   : {0}     期望卡片宽（940 逻辑）= {1} 物理" -f $scale, [int]$expectedPhysical)
Write-Host ("左边距 : {0}" -f [int]$leftGap)
Write-Host ("右边距 : {0}     （居中的话应 ≈ {1}）" -f [int]$rightGap, [int]$expectedRightGap)

$okWidth = [math]::Abs($best.Width - $expectedPhysical) -le 6
$okCenter = [math]::Abs($rightGap - $expectedRightGap) -le 8

Write-Host ''
if ($okWidth) { Write-Host 'PASS 卡片宽度触顶 940 逻辑像素' } else { Write-Host 'FAIL 宽度不是 940 逻辑（限宽没生效或没触顶）' }
if ($okCenter) { Write-Host 'PASS 限宽后内容在内容区里居中' } else { Write-Host 'FAIL 限宽后没有居中（贴左或贴右）' }

# 复位成普通尺寸，别把用户的窗口留在最大化
$pattern.SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Normal)
if ($okWidth -and $okCenter) { exit 0 } else { exit 1 }
