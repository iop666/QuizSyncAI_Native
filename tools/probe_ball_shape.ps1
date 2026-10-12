param(
    [Parameter(Mandatory = $true)][string]$ProcessName,
    [int]$MaxSide = 200          # 小于这个边长的顶级窗口 = 悬浮球（不是主窗口）
)

# 把悬浮球窗口所在的那块屏幕截下来，打印**过中心的行/列**的颜色游程。
# 用途：agent 看不到图像，但「球到底是正圆还是椭圆」可以用「中心行有色的宽度」与
# 「中心列有色的高度」是否相等来判定，不需要肉眼看。
#
# 必须处理：本进程默认 DPI-unaware，CopyFromScreen 坐标会被虚拟化（本机 200% 差 2 倍）。

Add-Type -AssemblyName System.Drawing
Add-Type -Namespace QsShape -Name Native -MemberDefinition @'
public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
[DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
'@

[void][QsShape.Native]::SetProcessDpiAwarenessContext([IntPtr](-4))

$proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { Write-Host "NO PROCESS: $ProcessName"; exit 1 }

$script:pid2 = $proc.Id
$script:ball = [IntPtr]::Zero
$script:rect = New-Object QsShape.Native+RECT
$cb = [QsShape.Native+EnumProc]{
    param($h, $l)
    $other = 0
    [void][QsShape.Native]::GetWindowThreadProcessId($h, [ref]$other)
    if ($other -eq $script:pid2 -and [QsShape.Native]::IsWindowVisible($h)) {
        $r = New-Object QsShape.Native+RECT
        [void][QsShape.Native]::GetWindowRect($h, [ref]$r)
        $w = $r.Right - $r.Left; $ht = $r.Bottom - $r.Top
        if ($w -le $MaxSide -and $ht -le $MaxSide -and $w -gt 4 -and $ht -gt 4) {
            $script:ball = $h; $script:rect = $r
        }
    }
    return $true
}
[void][QsShape.Native]::EnumWindows($cb, [IntPtr]::Zero)

if ($script:ball -eq [IntPtr]::Zero) { Write-Host 'NO BALL WINDOW'; exit 1 }
$r = $script:rect
$w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
Write-Host ("球窗口 hwnd={0} rect=({1},{2}) {3}x{4}   宽高相等? {5}" -f $script:ball, $r.Left, $r.Top, $w, $h, ($w -eq $h))

$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
$g.Dispose()

$cx = [int]($w / 2); $cy = [int]($h / 2)

function Runs($colors) {
    $out = @()
    $prev = $null; $count = 0; $start = 0
    for ($i = 0; $i -lt $colors.Count; $i++) {
        $c = $colors[$i]
        if ($c -eq $prev) { $count++ }
        else {
            if ($null -ne $prev) { $out += ("[{0}..{1}] x{2} {3}" -f $start, ($i - 1), $count, $prev) }
            $prev = $c; $count = 1; $start = $i
        }
    }
    if ($null -ne $prev) { $out += ("[{0}..{1}] x{2} {3}" -f $start, ($colors.Count - 1), $count, $prev) }
    return $out
}

$row = @(); for ($x = 0; $x -lt $w; $x++) { $c = $bmp.GetPixel($x, $cy); $row += ("#{0:X2}{1:X2}{2:X2}" -f $c.R, $c.G, $c.B) }
$col = @(); for ($y = 0; $y -lt $h; $y++) { $c = $bmp.GetPixel($cx, $y); $col += ("#{0:X2}{1:X2}{2:X2}" -f $c.R, $c.G, $c.B) }

Write-Host ("--- 过中心的行 y={0} ---" -f $cy)
Runs $row | ForEach-Object { Write-Host ('  ' + $_) }
Write-Host ("--- 过中心的列 x={0} ---" -f $cx)
Runs $col | ForEach-Object { Write-Host ('  ' + $_) }

$bmp.Dispose()
