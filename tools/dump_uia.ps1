param(
    [Parameter(Mandatory = $true)][string]$ProcessName,
    [int]$MaxDepth = 12,
    [string]$OutFile
)

# 用 UI Automation 把某个进程的**顶级窗口**控件树 dump 出来。
# 用途：agent 看不到图像，只能靠控件树核对「界面到底有没有画出来、结构对不对」。
#
# 两个踩过的坑：
# ① 这个脚本必须存成 **UTF-8 with BOM**（本机是 PowerShell 5.1，无 BOM 会把中文按 GBK 读，
#    格式串直接解析失败）。
# ② 不能用 Process.MainWindowHandle —— 本进程还有一个**悬浮球分层窗口**，
#    MainWindowHandle 会挑到它（实测拿到 56×56 的球，而不是 1180×760 的主窗口）。
#    改成从桌面根节点枚举、按 ProcessId 过滤，再按面积挑最大的那个。

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { Write-Host "NO PROCESS: $ProcessName"; exit 1 }

$root = [System.Windows.Automation.AutomationElement]::RootElement
$condition = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
$windows = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $condition)

Write-Host ("pid={0}  顶级窗口数={1}" -f $proc.Id, $windows.Count)

$target = $null
$best = 0.0
foreach ($w in $windows) {
    $r = $w.Current.BoundingRectangle
    $area = [double]$r.Width * [double]$r.Height
    Write-Host ("  - type={0} name=[{1}] rect=({2},{3},{4}x{5})" -f `
        ($w.Current.ControlType.ProgrammaticName -replace '^ControlType\.', ''), `
        $w.Current.Name, [int]$r.X, [int]$r.Y, [int]$r.Width, [int]$r.Height)
    if ($area -gt $best) { $best = $area; $target = $w }
}

if (-not $target) { Write-Host 'NO TOP-LEVEL WINDOW'; exit 1 }
Write-Host ("选中面积最大的窗口：name=[{0}] {1}x{2}" -f $target.Current.Name, [int]$target.Current.BoundingRectangle.Width, [int]$target.Current.BoundingRectangle.Height)

$sb = New-Object System.Text.StringBuilder
$walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker

function Walk($element, $depth) {
    if ($depth -gt $MaxDepth) { return }
    $pad = '  ' * $depth
    try {
        $name = $element.Current.Name
        $type = $element.Current.ControlType.ProgrammaticName -replace '^ControlType\.', ''
        $rect = $element.Current.BoundingRectangle
        $id = $element.Current.AutomationId
        $line = "{0}{1} name=[{2}] id=[{3}] rect=({4},{5},{6}x{7})" -f `
            $pad, $type, $name, $id, `
            [int]$rect.X, [int]$rect.Y, [int]$rect.Width, [int]$rect.Height
        [void]$sb.AppendLine($line)
    }
    catch { }

    $child = $walker.GetFirstChild($element)
    while ($child) {
        Walk $child ($depth + 1)
        $child = $walker.GetNextSibling($child)
    }
}

Walk $target 0
$text = $sb.ToString()
if ($OutFile) {
    [System.IO.File]::WriteAllText($OutFile, $text, (New-Object System.Text.UTF8Encoding($false)))
    $lines = ($text -split "`n").Count
    Write-Host ("已写出 {0}（{1} 行）" -f $OutFile, $lines)
}
else {
    Write-Host $text
}
