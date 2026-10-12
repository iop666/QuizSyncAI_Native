param(
    [string]$ProcessName = 'QuizSync.App'
)

# 证明托盘图标**真的挂进了通知区域**。
#
# 为什么不能只看「代码没报错」：`Shell_NotifyIcon(NIM_ADD)` 是**没有返回值可靠判据**的老 API ——
# 窗口建好了、图标文件加载失败、或者资源管理器没接住，它都可能「什么都不说」地失败，
# 界面上唯一的表现是「托盘区没有那个图标」，而 agent 看不到屏幕。
#
# 客观判据是 `Shell_NotifyIconGetRect`（Win7+）：给「窗口句柄 + 图标 id」，
# 它返回图标的**屏幕矩形**。图标不在托盘区时它返回失败（HRESULT 非 0）。
# 这同时也是一个可用的**负向对照**：应用退出后同一个句柄应该查不到。

Add-Type -Namespace QsTray -Name Native -MemberDefinition @'
public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder name, int max);
[DllImport("shell32.dll")] public static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER id, out RECT rect);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
[StructLayout(LayoutKind.Sequential)] public struct NOTIFYICONIDENTIFIER { public int cbSize; public IntPtr hWnd; public uint uID; public Guid guidItem; }
'@

$proc = Get-Process $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { Write-Host "NO PROCESS: $ProcessName"; exit 1 }

$script:pid2 = $proc.Id
$script:found = [IntPtr]::Zero
$cb = [QsTray.Native+EnumProc]{
    param($h, $l)
    $other = 0
    [void][QsTray.Native]::GetWindowThreadProcessId($h, [ref]$other)
    if ($other -eq $script:pid2) {
        $sb = New-Object System.Text.StringBuilder 256
        [void][QsTray.Native]::GetClassName($h, $sb, 256)
        if ($sb.ToString() -eq 'QuizSyncTrayWindow') { $script:found = $h }
    }
    return $true
}
[void][QsTray.Native]::EnumWindows($cb, [IntPtr]::Zero)

if ($script:found -eq [IntPtr]::Zero) {
    Write-Host 'FAIL  进程里找不到类名为 QuizSyncTrayWindow 的托盘窗口'
    exit 1
}
Write-Host ("托盘隐藏窗口 hwnd={0}（类名 QuizSyncTrayWindow）" -f $script:found)

$id = New-Object QsTray.Native+NOTIFYICONIDENTIFIER
$id.cbSize = [System.Runtime.InteropServices.Marshal]::SizeOf($id)
$id.hWnd = $script:found
$id.uID = 1

$rect = New-Object QsTray.Native+RECT
$hr = [QsTray.Native]::Shell_NotifyIconGetRect([ref]$id, [ref]$rect)
Write-Host ("Shell_NotifyIconGetRect -> hr=0x{0:X8}" -f $hr)
if ($hr -eq 0) {
    $w = $rect.Right - $rect.Left
    $h = $rect.Bottom - $rect.Top
    Write-Host ("PASS  图标在通知区域里，矩形=({0},{1}) {2}x{3}" -f $rect.Left, $rect.Top, $w, $h)
    exit 0
}
else {
    Write-Host 'FAIL  托盘区里查不到这个图标'
    exit 1
}
