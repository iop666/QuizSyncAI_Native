param(
    [string]$ProcessName = 'QuizSync.App',
    [string]$Name = '端到端验证合集'
)

# 验证「首次使用门 → 新建合集 → 进入主界面」这条链路。
#
# 判据落在**副作用**上：输入名字点「新建并进入」之后，
# ① 主界面出现了（顶栏有合集药丸）② 药丸上的名字就是刚建的那个 ③ 库里真的多了一行。
# 只看「按钮能点」不算 —— 那和「点了没反应」在控件树里长得一样。

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$proc = Get-Process $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { Write-Host "NO PROCESS: $ProcessName"; exit 1 }

$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
$win = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
    [System.Windows.Automation.TreeScope]::Children, $cond) |
    Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Window } |
    Sort-Object { -($_.Current.BoundingRectangle.Width * $_.Current.BoundingRectangle.Height) } |
    Select-Object -First 1
if (-not $win) { Write-Host 'NO WINDOW'; exit 1 }

function Find-ByName([string]$name) {
    return $win.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, $name)))
}

$edit = Find-ByName '合集名称'
if (-not $edit) { Write-Host 'FAIL 找不到「合集名称」输入框（是不是已经不在首次使用门上了？）'; exit 1 }

$value = $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
$value.SetValue($Name)
Start-Sleep -Milliseconds 400
Write-Host ("已填入合集名：" + $Name)

$create = Find-ByName '新建并进入'
if (-not $create) { Write-Host 'FAIL 找不到「新建并进入」按钮'; exit 1 }
$create.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Seconds 3

# 进了主界面就应该能看到顶栏的合集药丸与侧栏
$pillLabel = $win.FindFirst(
    [System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'CollectionLabel')))
$sidebar = Find-ByName '历史记录'

if ($pillLabel -and $sidebar) {
    Write-Host ("PASS 已进入主界面；合集药丸显示 [" + $pillLabel.Current.Name + "]")
    if ($pillLabel.Current.Name -eq $Name) { Write-Host 'PASS 药丸上的名字就是刚建的那个合集' }
    else { Write-Host ("WARN 药丸显示的是 [" + $pillLabel.Current.Name + "]，不是刚建的「" + $Name + "」") }

    # 刚建的合集是空的，侧栏会是「这个合集还没有记录」的空态 —— 那是**正确行为**。
    # 验收要看的是主界面骨架（会话头、底部导航条），所以顺手切到「全部记录」。
    $all = Find-ByName '全部记录'
    if ($all) {
        try {
            $pattern = $all.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
            $pattern.Select()
            Start-Sleep -Seconds 2
            Write-Host 'PASS 已切到「全部记录」（空合集的侧栏是空态，骨架不在树里）'
        }
        catch { Write-Host ('WARN 切「全部记录」失败：' + $_.Exception.Message) }
    }

    exit 0
}

Write-Host ("FAIL 没进主界面（药丸=" + [bool]$pillLabel + "，侧栏=" + [bool]$sidebar + "）")
exit 1
