param(
    [string]$ProcessName = 'QuizSync.App',
    [ValidateSet('copy', 'delete-dialog')][string]$Action = 'copy'
)

# 验证会话头上那几个动作**真的做了事**（而不是只在控件树里存在）。
#
# 为什么必须单独验：「按钮在」和「按钮有用」是两件事 ——
# 这一版之前那四个按钮点下去只弹一句「还没接上」，控件树里一样看得见它们。
# 判据要落在**副作用**上：复制 → 剪贴板内容变了；删除 → 弹出确认框。

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$proc = Get-Process $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { Write-Host "NO PROCESS: $ProcessName"; exit 1 }

$condition = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
$candidates = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
    [System.Windows.Automation.TreeScope]::Children, $condition)
$window = $candidates |
    Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Window } |
    Sort-Object { -($_.Current.BoundingRectangle.Width * $_.Current.BoundingRectangle.Height) } |
    Select-Object -First 1

if (-not $window) { Write-Host 'NO WINDOW'; exit 1 }

function Invoke-ById([string]$id) {
    $element = $window.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)))
    if (-not $element) { return $false }
    $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    return $true
}

switch ($Action) {
    'copy' {
        $sentinel = 'QS_SENTINEL_BEFORE_COPY'
        Set-Clipboard -Value $sentinel
        Start-Sleep -Milliseconds 400
        if (-not (Invoke-ById 'HeaderCopyButton')) { Write-Host 'FAIL 找不到 HeaderCopyButton'; exit 1 }
        Start-Sleep -Seconds 2
        $text = Get-Clipboard -Raw
        if ($null -eq $text) { $text = '' }
        Write-Host ("剪贴板长度：{0} → {1}" -f $sentinel.Length, $text.Length)
        if ($text -ne $sentinel -and $text.Length -gt 0) {
            Write-Host 'PASS 复制全部真的把内容写进了剪贴板'
            Write-Host '--- 前 300 字 ---'
            Write-Host ($text.Substring(0, [Math]::Min(300, $text.Length)))
            exit 0
        }
        Write-Host 'FAIL 剪贴板没变（还是哨兵值）'
        exit 1
    }

    'delete-dialog' {
        if (-not (Invoke-ById 'HeaderDeleteButton')) { Write-Host 'FAIL 找不到 HeaderDeleteButton'; exit 1 }
        Start-Sleep -Seconds 2
        # ⚠️ WinUI 的 ContentDialog 是**窗口内的弹出层**，不是独立顶级窗口 ——
        # 一开始按顶级窗口找，明明弹出来了却报「没看到确认框」。
        # 判据要落在窗口子树里：找标题文本「删除这条记录？」。
        $dialog = $window.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::NameProperty, '删除这条记录？')))
        if (-not $dialog) {
            $dialog = $window.FindFirst(
                [System.Windows.Automation.TreeScope]::Descendants,
                (New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::NameProperty, '删除这条记录')))
        }

        if ($dialog) {
            Write-Host ('PASS 点「删除本次」弹出了二次确认框：name=[' + $dialog.Current.Name + ']')
            # 顺手验一下「取消」能关掉它（不真的删数据）。
            $cancel = $window.FindFirst(
                [System.Windows.Automation.TreeScope]::Descendants,
                (New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::NameProperty, '取消')))
            if ($cancel) {
                $cancel.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
                Write-Host 'PASS 确认框里的「取消」可点（没有真的删数据）'
            }
            exit 0
        }

        Write-Host 'FAIL 没看到确认框'
        exit 1
    }
}
