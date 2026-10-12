# 诊断：Windows 设置页左上角的「返回」到底做了什么。
#
# 用户报告：「windows 设置页左上角返回无用」。
# 这个脚本不去猜，而是把三步都量出来：
#   ① 设置页里那个左上角按钮**是哪一个**（名字、AutomationId、位置、ControlType）——
#      区分「返回」与「展开/收起导航」两种可能；
#   ② 它**是不是 enabled**、能不能 Invoke；
#   ③ 点完之后**页面变了没有**（判据：设置页的 10 个分类还在不在）。
#
# 用法：powershell -ExecutionPolicy Bypass -File tools/probe_settings_back.ps1

param(
    [string]$ProcessName = 'QuizSync.App',
    [int]$MaxDepth = 10
)

$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Get-MainWindow([int]$processId) {
    # ⚠️ 本机是 PowerShell **5.1**：管道符 `|` 不能出现在新行的行首（会报 Missing ')'）。
    # 所以先把候选收集到变量，再在新的一行**末尾**接管道 —— 与 verify_windows_ui.ps1 的写法一致。
    $candidates = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
        [System.Windows.Automation.TreeScope]::Children,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $processId)))
    $windows = $candidates |
        Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Window } |
        Sort-Object { -($_.Current.BoundingRectangle.Width * $_.Current.BoundingRectangle.Height) }
    return $windows | Select-Object -First 1
}

function Get-Descendants($element) {
    $element.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
}

$proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { Write-Host 'ABORT: 应用没在运行'; exit 2 }

$window = Get-MainWindow $proc.Id
if (-not $window) { Write-Host 'ABORT: 找不到主窗口'; exit 2 }

# 先确保在设置页：找左导航的分类节点，找不到就点顶栏齿轮
$categories = @('使用说明', '显示设置', 'API 配置', '连接设备', '识别设置',
                '悬浮球设置', '悬浮窗设置', '热键设置', '数据管理', '关于')
$onSettings = $false
foreach ($name in $categories) {
    $hit = $window.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, $name)))
    if ($hit) { $onSettings = $true; break }
}

if (-not $onSettings) {
    $gear = $window.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'SettingsButton')))
    if ($gear) {
        $gear.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Seconds 3
    }
}

Write-Host '=== 设置页左上角区域的所有按钮 ==='
$buttons = Get-Descendants $window | Where-Object {
    $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button -and
    $_.Current.BoundingRectangle.Top -lt ($window.Current.BoundingRectangle.Top + 220) -and
    $_.Current.BoundingRectangle.Left -lt ($window.Current.BoundingRectangle.Left + 400)
}

$back = $null
foreach ($button in $buttons) {
    $r = $button.Current.BoundingRectangle
    $autoId = $button.Current.AutomationId
    $name = $button.Current.Name
    $enabled = $button.Current.IsEnabled
    Write-Host ("  名字=[{0}]  id=[{1}]  enabled={2}  rect=({3},{4}) {5}x{6}" -f `
        $name, $autoId, $enabled, [int]$r.Left, [int]$r.Top, [int]$r.Width, [int]$r.Height)

    # 候选：名字里带「返回」/Back，或 id 里带 Back
    if ($name -match '返回|Back' -or $autoId -match 'Back') { $back = $button }
}

if (-not $back -and $buttons) {
    Write-Host '  （没有名字含「返回/Back」的按钮，取最靠左上角那个做实验）'
    $back = $buttons | Sort-Object { $_.Current.BoundingRectangle.Top * 10000 + $_.Current.BoundingRectangle.Left } | Select-Object -First 1
}

if (-not $back) { Write-Host 'ABORT: 左上角没有按钮'; exit 2 }

Write-Host ''
Write-Host ("实验对象：名字=[{0}] id=[{1}]" -f $back.Current.Name, $back.Current.AutomationId)

# 点击（NavigationView 的返回按钮通常暴露 Invoke；没有就退回鼠标点击）
$invoked = $false
try {
    $back.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $invoked = $true
} catch {
    Write-Host ("  Invoke 不支持：{0}；改用鼠标点击" -f $_.Exception.Message)
    $r = $back.Current.BoundingRectangle
    Add-Type -AssemblyName System.Windows.Forms
    [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point([int]($r.Left + $r.Width / 2), [int]($r.Top + $r.Height / 2))
    Add-Type @'
using System;
using System.Runtime.InteropServices;
public class QsMouse {
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint x, uint y, uint d, IntPtr e);
    public static void Click() { mouse_event(0x0002, 0, 0, 0, IntPtr.Zero); mouse_event(0x0004, 0, 0, 0, IntPtr.Zero); }
}
'@
    [QsMouse]::Click()
    $invoked = $true
}
Start-Sleep -Seconds 2

Write-Host ("  已触发={0}" -f $invoked)

# 判据：设置页的导航分类还在不在
$stillSettings = $false
foreach ($name in $categories) {
    $hit = $window.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, $name)))
    if ($hit) { $stillSettings = $true; break }
}

# 主界面独有标记：顶栏的搜索按钮 / 「多页暂存」/ 顶栏标题 QuizSync AI
$homeMarkers = @('QuizSync AI', '搜索', '选择合集')
$onHome = $false
foreach ($marker in $homeMarkers) {
    $hit = $window.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, $marker)))
    if ($hit) { $onHome = $true; Write-Host ("  主界面标记命中：[{0}]" -f $marker); break }
}

Write-Host ''
if ($stillSettings) { Write-Host '结论：点了返回之后**仍在设置页** —— 返回确实没用。' }
elseif ($onHome) { Write-Host '结论：已经回到主界面 —— 返回是有效的。' }
else { Write-Host '结论：既不在设置页、也没看到主界面标记 —— 落在第三种状态，需要人工看。' }

if ($stillSettings) { exit 1 } else { exit 0 }
