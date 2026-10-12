param(
    [string]$ProcessName = 'QuizSync.App',
    [Parameter(Mandatory = $true)][int]$MenuId
)

# 往托盘隐藏窗口发一条 WM_COMMAND，验证「托盘菜单命令 → 真实动作」这条链路。
#
# 为什么要单独验：菜单本身走的是 TrackPopupMenuEx 的返回值，而**弹出菜单会阻塞**，
# 没法自动化点击。窗口过程里那条 WM_COMMAND 分支就是给这种场合（以及无障碍工具 / 将来的快捷键）留的，
# 而它同时也是「id → 命令」映射最容易出差错的地方（差一位 = 点了 A 执行了 B）。
#
# 用法：powershell -File probe_tray_command.ps1 -MenuId 6      # 6 = 显示/隐藏悬浮球

Add-Type -Namespace QsTrayCmd -Name Native -MemberDefinition @'
public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder name, int max);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int max);
[DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
'@

$proc = Get-Process $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { Write-Host "NO PROCESS: $ProcessName"; exit 1 }
$script:pid2 = $proc.Id
$script:tray = [IntPtr]::Zero
$script:balls = 0
$script:title = ''
$script:main = [IntPtr]::Zero
$script:mainArea = 0.0

$cb = [QsTrayCmd.Native+EnumProc]{
    param($h, $l)
    $other = 0
    [void][QsTrayCmd.Native]::GetWindowThreadProcessId($h, [ref]$other)
    if ($other -eq $script:pid2) {
        $cls = New-Object System.Text.StringBuilder 256
        [void][QsTrayCmd.Native]::GetClassName($h, $cls, 256)
        switch ($cls.ToString()) {
            'QuizSyncTrayWindow' { $script:tray = $h }
            'QuizSyncFloatingBall' { $script:balls++ }
            default {
                $text = New-Object System.Text.StringBuilder 512
                [void][QsTrayCmd.Native]::GetWindowText($h, $text, 512)
                # 悬浮窗：标题是稳定的 `QuizSyncFloat`（它没有标题栏，用户看不到这个名字）。
                if ($text.ToString() -eq 'QuizSyncFloat') { $script:floats++ }
                elseif ($text.ToString() -like '*AI 双端搜题*') { $script:title = $text.ToString() }
            }
        }
    }
    return $true
}

function Snapshot {
    $script:balls = 0
    $script:floats = 0
    $script:title = ''
    [void][QsTrayCmd.Native]::EnumWindows($cb, [IntPtr]::Zero)
}

Snapshot
Write-Host ("发命令前：托盘窗口={0}  悬浮球窗口={1}  悬浮窗={2}  标题=[{3}]" -f $script:tray, $script:balls, $script:floats, $script:title)

if ($script:tray -eq [IntPtr]::Zero) { Write-Host 'FAIL 找不到托盘窗口'; exit 1 }

[void][QsTrayCmd.Native]::PostMessage($script:tray, 0x0111, [IntPtr]$MenuId, [IntPtr]::Zero)
Start-Sleep -Milliseconds 1500
Snapshot
Write-Host ("发命令后：托盘窗口={0}  悬浮球窗口={1}  悬浮窗={2}  标题=[{3}]" -f $script:tray, $script:balls, $script:floats, $script:title)
Write-Host ("（MenuId={0}）" -f $MenuId)
