param(
    [string]$ProcessName = 'QuizSync.App',
    # 收尾不删应用数据副本（排查「到底写进哪个文件」时用）。
    [switch]$KeepData
)

# 核验本轮补上的四个「还没接上」缺口（真实 UI + 真实按键，不是读代码）：
#   ① 热键设置：更改… 录入框真的能录、能保存、能落盘；恢复默认真的回到 F8/F9；
#   ② 数据管理：导出合集 / 导出全部历史（Markdown、JSON）/ 导出日志 都是真按钮（不再是「还没接上」）；
#   ③ 连接设备：已配对设备列表真的从主机读出来，并且能吊销（吊销只在预演模式下点，见下）；
#   ④ 停在设置页时热键被吞掉（按 F9 不该截屏），标题里必须出现「热键被设置页吞掉」。
#
# 用法：powershell -ExecutionPolicy Bypass -File tools\probe_settings_gaps.ps1
# 退出码：0 = 全过；1 = 有 FAIL。
#
# ⚠️ 它会把**应用数据目录复制一份到临时目录**再启动（QS_APP_DATA），
# 所以热键的改动落在副本里，**不动用户的真实设置**。
# 但「已配对设备」那一块读的是真实主机数据目录（`%LOCALAPPDATA%\QuizSyncAI\server`），
# 因为桌面端的设备令牌与控制令牌都在那里 —— 只读 + 必要时用配对码换一次自己的令牌，
# 不新增设备行、不吊销任何东西。

$ErrorActionPreference = 'Continue'
# 拉起主机要用 `dotnet <dll>`（本机 .NET 10 是私有安装，不在 PATH 上）—— 与日常跑法一致。
if (-not $env:QS_DOTNET -and (Test-Path 'D:\Windows\Apps\DSH_Tools\dotnet\dotnet.exe')) {
    $env:QS_DOTNET = 'D:\Windows\Apps\DSH_Tools\dotnet\dotnet.exe'
}
$root = 'D:\ZCode\QuizSync_AI'
$tmpRoot = 'D:\DeepseekHarness\TemporaryConversationFile'
$appData = Join-Path $tmpRoot 'probe-appdata-gaps'
$uiaPath = Join-Path $tmpRoot 'probe-settings-gaps.txt'

$pass = 0
$fail = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { $script:pass++; Write-Host ("  PASS  " + $name + $(if ($detail) { "   ($detail)" } else { '' })) }
    else { $script:fail++; Write-Host ("  FAIL  " + $name + $(if ($detail) { "   ($detail)" } else { '' })) }
}

# ---------------------------------------------------------------------------
# 准备：杀旧实例 → 复制应用数据到临时目录 → 启动
# ---------------------------------------------------------------------------
Get-Process $ProcessName -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

$realApp = Join-Path $env:LOCALAPPDATA 'QuizSyncAI\app'
if (Test-Path $appData) { Remove-Item -Recurse -Force $appData }
New-Item -ItemType Directory -Force -Path $appData | Out-Null
if (Test-Path $realApp) {
    Copy-Item -Path (Join-Path $realApp '*') -Destination $appData -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host ("应用数据副本：" + $appData)

$exe = Join-Path $root 'windows\QuizSync.App\bin\Debug\net10.0-windows10.0.26100.0\win-x64\QuizSync.App.exe'
if (-not (Test-Path $exe)) { Write-Host "找不到 exe：$exe"; exit 1 }

$env:QS_APP_DATA = $appData
Start-Process -FilePath $exe | Out-Null
Start-Sleep -Seconds 8

$proc = Get-Process $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { Write-Host '进程没起来'; exit 1 }
Write-Host ("进程 pid={0}" -f $proc.Id)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms

function Get-AppWindow {
    $candidates = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
        [System.Windows.Automation.TreeScope]::Children,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)))
    return $candidates |
        Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Window } |
        Sort-Object { -($_.Current.BoundingRectangle.Width * $_.Current.BoundingRectangle.Height) } |
        Select-Object -First 1
}

function Find-ByName([System.Windows.Automation.AutomationElement]$scope, [string]$name) {
    return $scope.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, $name)))
}

function Invoke-Element([System.Windows.Automation.AutomationElement]$element) {
    # 先试自己，再**沿父链往上找**：`TextButton` 的内容是 `StackPanel { FontIcon; TextBlock }`，
    # 所以「按名字找到」的那个元素往往是里面的 **Text**，而它不支持 InvokePattern ——
    # 第一版就栽在这里：找得到按钮、点下去什么都没发生（脚本却以为点过了）。
    $current = $element
    for ($level = 0; $level -lt 5 -and $null -ne $current; $level++) {
        try {
            $current.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
            return $true
        } catch { }

        try {
            $current.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
            return $true
        } catch { }

        try { $current = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($current) }
        catch { $current = $null }
    }

    # 最后一条路：**按坐标点**。WinUI 的 `SettingsCard` 里按钮的 UIA 名字常常是空的
    # （内容是一个 panel，不是一个字符串），InvokePattern 也就拿不到 —— 实测踩到过：
    # 「找到按钮」为真、点下去毫无反应。坐标点虽然土，但它用的就是真实鼠标。
    return Click-Element $element
}

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class QsMouse {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    public static void Click(int x, int y) {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(80);
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);   // LEFTDOWN
        System.Threading.Thread.Sleep(40);
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);   // LEFTUP
    }
}
"@

function Click-Element([System.Windows.Automation.AutomationElement]$element) {
    try {
        $rect = $element.Current.BoundingRectangle
        if ($rect.Width -le 0 -or $rect.Height -le 0) { return $false }
        # **点之前先把应用窗口拉到前台**：坐标点击打的是屏幕，不是控件 ——
        # 探针自己的 PowerShell 窗口在前面时，这一下会点在它身上（实测：点了没反应）。
        try { (Get-AppWindow).SetFocus() } catch { }
        Start-Sleep -Milliseconds 250
        $x = [int]($rect.X + $rect.Width / 2)
        $y = [int]($rect.Y + $rect.Height / 2)
        [QsMouse]::Click($x, $y)
        return $true
    } catch { return $false }
}

# 按名字点一个**按钮**：同名元素可能有多个（`SettingsCard` 的标题文字与它右边按钮里的文字
# **同名**），所以要挑「有 Button 祖先」的那一个，并点那个 Button 的中心 ——
# 点标题文字的中心什么都不会发生（第一版就是这么误判「导出日志点了没反应」的）。
function Click-ByName([string]$name) {
    $hits = Find-AllInAnyWindow $name
    foreach ($hit in $hits) {
        $cur = $hit
        for ($level = 0; $level -lt 4 -and $null -ne $cur; $level++) {
            try {
                if ($cur.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button) {
                    if (Click-Element $cur) { return $true }
                }
            } catch { }

            try { $cur = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($cur) }
            catch { $cur = $null }
        }
    }

    return $false
}

# 在**含某段标记文本的那个窗口**里按名字找元素。弹层（ContentDialog）是独立顶级窗口，
# 而它里面按钮的名字（例如「恢复默认」）和主窗口里那一行**同名** ——
# 不限定窗口就会点到主窗口那一行上，于是「确认框一直开着、什么都没发生」（实测踩到）。
function Find-InWindowContaining([string]$marker, [string]$name) {
    foreach ($w in Get-WindowElements) {
        $text = ''
        try {
            $children = $w.FindAll(
                [System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.Condition]::TrueCondition)
            $names = New-Object System.Collections.Generic.List[string]
            foreach ($c in $children) {
                try { [void]$names.Add($c.Current.Name) } catch { }
            }
            $text = ($names -join "`n")
        } catch { continue }

        if ($text -notlike "*$marker*") { continue }

        try {
            $hit = $w.FindFirst(
                [System.Windows.Automation.TreeScope]::Descendants,
                (New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::NameProperty, $name)))
            if ($hit) { return $hit }
        } catch { }
    }

    return $null
}

function Dump-Tree {
    & powershell -ExecutionPolicy Bypass -File (Join-Path $root 'tools\dump_uia.ps1') `
        -ProcessName $ProcessName -MaxDepth 9 -OutFile $uiaPath | Out-Null
    return Get-Content $uiaPath -Encoding UTF8
}

function Window-Title {
    $w = Get-AppWindow
    if ($w) { return $w.Current.Name }
    return ''
}

# ---------------------------------------------------------------------------
# 弹层（ContentDialog）不在主窗口那棵树里 —— WinUI 3 把它放在**另一个顶级窗口**。
# 所以「录入框有没有弹出来」不能只看主窗口的 dump，要把该进程**所有**顶级窗口的
# 全部控件名拼起来找。这一条是实测踩出来的：第一版只看主窗口，录入框明明弹了却报 FAIL。
# ---------------------------------------------------------------------------
function Get-WindowElements {
    $all = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
        [System.Windows.Automation.TreeScope]::Children,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)))
    return @($all)
}

function Dump-AllWindowsText {
    $names = New-Object System.Collections.Generic.List[string]
    foreach ($w in Get-WindowElements) {
        [void]$names.Add($w.Current.Name)
        try {
            $children = $w.FindAll(
                [System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.Condition]::TrueCondition)
            foreach ($c in $children) {
                try { [void]$names.Add($c.Current.Name) } catch { }
            }
        } catch { }
    }

    return ($names -join "`n")
}

function Find-InAnyWindow([string]$name) {
    foreach ($w in Get-WindowElements) {
        try {
            $hit = $w.FindFirst(
                [System.Windows.Automation.TreeScope]::Descendants,
                (New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::NameProperty, $name)))
            if ($hit) { return $hit }
        } catch { }
    }

    return $null
}

function Find-AllInAnyWindow([string]$name) {
    $found = New-Object System.Collections.Generic.List[object]
    foreach ($w in Get-WindowElements) {
        try {
            $hits = $w.FindAll(
                [System.Windows.Automation.TreeScope]::Descendants,
                (New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::NameProperty, $name)))
            foreach ($h in $hits) { [void]$found.Add($h) }
        } catch { }
    }

    return $found
}

# 等主界面（侧栏 + 顶栏药丸都在）画完
$lines = @()
for ($attempt = 1; $attempt -le 12; $attempt++) {
    Start-Sleep -Seconds 2
    $lines = Dump-Tree
    if (($lines | Select-String -Pattern 'SettingsButton' -SimpleMatch | Measure-Object).Count -gt 0) { break }
}
Check '应用起来了（找到顶栏齿轮按钮）' (($lines | Select-String -Pattern 'SettingsButton' -SimpleMatch | Measure-Object).Count -gt 0)

# 进设置页：点齿轮
$window = Get-AppWindow
$gear = $window.FindFirst(
    [System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'SettingsButton')))
Check '找到顶栏齿轮' ($null -ne $gear)
if ($gear) { Invoke-Element $gear | Out-Null; Start-Sleep -Seconds 3 }

function Open-Category([string]$name) {
    $w = Get-AppWindow
    $item = Find-ByName $w $name
    if (-not $item) { return $false }
    Invoke-Element $item | Out-Null
    Start-Sleep -Seconds 3
    return $true
}

# ---------------------------------------------------------------------------
# ① 热键设置
# ---------------------------------------------------------------------------
Write-Host ''
Write-Host '=== ① 热键设置 ==='
$ok = Open-Category '热键设置'
Check '左导航能选中「热键设置」' $ok

$lines = Dump-Tree
$has = { param($p) ($lines | Select-String -Pattern $p -SimpleMatch | Measure-Object).Count -gt 0 }
Check '有「更改…」按钮（自定义热键入口是真的）' (& $has '更改…')
Check '有「恢复默认」按钮' (& $has '恢复默认')
Check '多页模式行不再写「还没接上」' (-not (& $has '还没接上'))
Check '多页模式行显示真实键位' ((& $has '当前：F9') -or (& $has '当前：F8'))

# 弹录入框 → 按 Ctrl+Alt+K → 保存
$window = Get-AppWindow
$change = Find-ByName $window '更改…'
Check '找到「更改…」按钮' ($null -ne $change)
if ($change) {
    Invoke-Element $change | Out-Null
    Start-Sleep -Seconds 2
    $all = Dump-AllWindowsText
    $dialog = $all -match '设置「截屏识别」热键'
    Check '录入框弹出来了（标题含「设置「截屏识别」热键」）' $dialog

    if ($dialog) {
        # 焦点给录入框所在的那个窗口，再合成 Ctrl+Alt+K
        try { (Get-AppWindow).SetFocus() } catch { }
        # 录入框是个**只读 TextBox**：先按 ControlType=Edit 找到它并给焦点
        # （不能按名字找 —— 文字一变，名字就变了）。
        $edit = $null
        foreach ($w in Get-WindowElements) {
            try {
                $edit = $w.FindFirst(
                    [System.Windows.Automation.TreeScope]::Descendants,
                    (New-Object System.Windows.Automation.PropertyCondition(
                        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                        [System.Windows.Automation.ControlType]::Edit)))
            } catch { }
            if ($edit) { break }
        }
        if ($edit) { try { $edit.SetFocus() } catch { } }
        Start-Sleep -Milliseconds 400
        [System.Windows.Forms.SendKeys]::SendWait('^%k')
        Start-Sleep -Seconds 2

        # 文本在 **ValuePattern** 里，不在 UIA 的 Name 上（第一版按 Name 找，判据用错了对象）。
        $shown = ''
        if ($edit) {
            try { $shown = $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value } catch { }
        }
        Check '按 Ctrl+Alt+K 后录入框显示组合键' ($shown -match 'Ctrl\+Alt\+K') $shown

        $save = Find-InAnyWindow '保存'
        if ($save) { Invoke-Element $save | Out-Null; Start-Sleep -Seconds 3 }
        $title = Window-Title
        Check '保存后标题报出真实注册结果' ($title -match '热键') $title

        $json = Join-Path $appData 'app.json'
        $settings = if (Test-Path $json) { Get-Content $json -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }
        Check '自定义热键已落盘（app.json 的 capture_hotkey）' `
            ($settings.capture_hotkey.label -eq 'Ctrl+Alt+K') `
            ("capture_hotkey = " + ($settings.capture_hotkey | ConvertTo-Json -Compress))
    }
}

# 恢复默认 → app.json 里应为 null
$reset = Find-InAnyWindow '恢复默认'
if ($reset) {
    Invoke-Element $reset | Out-Null
    Start-Sleep -Seconds 2
    $all = Dump-AllWindowsText
    Check '恢复默认弹出了二次确认框' ($all -match '恢复默认热键？')
    if ($all -match '恢复默认热键？') {
        # ⚠️ 必须在**确认框那个窗口里**点主按钮：主窗口里有一行也叫「恢复默认」，
        # 点错了就会走回那一行（而 `_dialogOpen` 守卫会让它静默失败）。
        $button = Find-InWindowContaining '恢复默认热键？' '恢复默认'
        Check '找到确认框里的「恢复默认」主按钮' ($null -ne $button)
        if ($button) { Invoke-Element $button | Out-Null }
        Start-Sleep -Seconds 3
    }

    # JSON 里的 `+` 会被转义成 `\u002B` —— **必须按 JSON 解析**，不能拿字面量去搜索
    # （第一版就是这么误判的：明明写进文件了，却被判成「没落盘」）。
    $json = Join-Path $appData 'app.json'
    $settings = if (Test-Path $json) { Get-Content $json -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }
    Check '恢复默认后 app.json 里不再有自定义键' ($null -eq $settings.capture_hotkey)
    Check '恢复默认后标题报出两个用途的真实状态' ((Window-Title) -match '默认热键') (Window-Title)
}

# ---------------------------------------------------------------------------
# ② 停在设置页时热键被吞掉（按 F9 不该截屏）
# ---------------------------------------------------------------------------
Write-Host ''
Write-Host '=== ② 设置页吞掉热键 ==='
$window = Get-AppWindow
try { $window.SetFocus() } catch { }
Start-Sleep -Milliseconds 400
[System.Windows.Forms.SendKeys]::SendWait('{F9}')
Start-Sleep -Seconds 2
$title = Window-Title
Check '按 F9 被吞掉（标题里说明原因，且没有跳走）' ($title -match '吞掉') $title

# ---------------------------------------------------------------------------
# ③ 数据管理：三个导出入口都是真按钮
# ---------------------------------------------------------------------------
Write-Host ''
Write-Host '=== ③ 数据管理 ==='
$ok = Open-Category '数据管理'
Check '左导航能选中「数据管理」' $ok

$lines = Dump-Tree
$has = { param($p) ($lines | Select-String -Pattern $p -SimpleMatch | Measure-Object).Count -gt 0 }
Check '有「导出合集」按钮' (& $has '导出合集')
Check '有「导出全部历史」两个格式按钮（Markdown / JSON）' ((& $has 'Markdown') -and (& $has 'JSON'))
Check '有「导出日志」按钮' (& $has '导出日志')
Check '数据管理页不再有「还没接上」' (-not (& $has '还没接上'))

# 真点一次「导出日志」：系统另存为对话框会弹出来 —— 证明这条链路是通的，然后取消。
$log = Find-ByName (Get-AppWindow) '导出日志'
Check '找到「导出日志」按钮' ($null -ne $log)
if ($log) {
    Click-ByName '导出日志' | Out-Null
    Start-Sleep -Seconds 4
    # 判据用**标题里的结果**，不去数窗口：系统「另存为」对话框是另一个进程的窗口，
    # 枚举本进程顶级窗口看不到它（第一版这么判，于是永远 FAIL）。
    # 真跑通的证据：对话框弹出来 → 我们按 ESC → 导出函数返回「已取消导出」→ 标题里出现它。
    $before = Window-Title
    [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
    Start-Sleep -Seconds 2
    $after = Window-Title
    $cancelled = ($after -match '已取消导出') -or ($after -match '已导出')
    Check '点「导出日志」走通了（ESC 后返回「已取消导出」）' $cancelled ("$before -> $after")
}

# ---------------------------------------------------------------------------
# ④ 连接设备：已配对设备列表真的读出来了
# ---------------------------------------------------------------------------
Write-Host ''
Write-Host '=== ④ 已配对设备 ==='
$ok = Open-Category '连接设备'
Check '左导航能选中「连接设备」' $ok

# 「正在读取主机上的设备…」→ 结果文案：主机没在跑时会先把它拉起来，启动超时是 **15s**
# （`HostLauncher.EnsureRunningAsync` 的默认值），所以这里最多等 45 秒。
$status = ''
for ($attempt = 1; $attempt -le 23; $attempt++) {
    Start-Sleep -Seconds 2
    $lines = Dump-Tree
    $statusLine = $lines | Select-String -Pattern 'name=\[(共 |还没有配对的设备|读不到设备|读设备列表失败)' | Select-Object -First 1
    if ($statusLine) { $status = $statusLine.Line.Trim(); break }
}

$has = { param($p) ($lines | Select-String -Pattern $p -SimpleMatch | Measure-Object).Count -gt 0 }
Check '有「刷新设备列表」按钮' (& $has '刷新设备列表')
Check '已配对设备区不再是「还没接上」' (-not (& $has '还没接上'))
Check '设备列表有结果（共 N 台 或 明确说没有）' ($status -ne '') $status

Write-Host ''
Write-Host ("=== 结果：PASS {0} / FAIL {1} ===" -f $pass, $fail)

# 收尾：把副本删掉（真实数据目录一个字节都没动）
Get-Process $ProcessName -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
if (-not $KeepData) {
    Remove-Item -Recurse -Force $appData -ErrorAction SilentlyContinue
} else {
    Write-Host ("保留了应用数据副本（-KeepData）：" + $appData)
}

if ($fail -gt 0) { exit 1 }
exit 0
