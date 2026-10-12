param(
    [Parameter(Mandatory = $true)][string]$ProcessName,
    [Parameter(Mandatory = $true)][string]$Points,   # 形如 "x,y;x,y" —— 相对主窗口左上角的逻辑像素偏移
    [int]$Dpi = 0,
    # 扫描网格（物理像素步长；0 = 不扫）。为什么要有它：**固定坐标的采样点会随
    # 「当前显示的是哪条记录 / 哪个页面」而变**（第 170 轮实测：同一个 x=406 在短记录时
    # 是画布色、在 17 题的记录下是卡片白），于是「主题资源到底并进去没有」这条判据
    # 时绿时红。改成扫全场出**颜色直方图**：画布色只要真有，就一定会出现在里面，
    # 与布局、数据、滚动位置都无关。
    [int]$ScanGrid = 0
)

# 采主窗口若干点的颜色。用途：agent 看不到图像，但要确认「自定义配色到底有没有生效」
# —— 如果 App.xaml 的资源合并出问题，整个界面会退回 Fluent 默认灰，靠控件树是看不出来的。
#
# 三个必须处理的坑：
# ① 本进程默认 DPI-unaware，CopyFromScreen 的坐标会被系统虚拟化（本机 200% 就是差 2 倍）。
#    必须先 SetProcessDpiAwarenessContext 拿到物理坐标。
# ② 窗口可能有别的窗口压在上面。**光调 SetForegroundWindow 不够**：当调用进程不是前台进程时
#    系统会**直接拒绝**这个请求（实测拿到 #151517 = 终端底色，还被当成「应用没渲染完」）。
#    所以首选 `PrintWindow(..., PW_RENDERFULLCONTENT)` —— 它抓的是窗口自身内容，与遮挡无关。
# ③ 画面可能整体是纯色（PrintWindow 对 Composition 内容偶尔给全黑），所以要有**自检**：
#    如果所有采样点都是同一个颜色且明显不是我们要找的色系，就判定这次抓图不可信并明确报出来，
#    而不是安静地输出一组看起来正常的数字。

Add-Type -AssemblyName System.Drawing
Add-Type -Namespace QsPix -Name Native -MemberDefinition @'
public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
[DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
[DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
[DllImport("user32.dll")] public static extern IntPtr GetWindowDC(IntPtr hWnd);
[DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
[DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
[DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
[DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
[DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
[DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
'@

# -4 = DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
[void][QsPix.Native]::SetProcessDpiAwarenessContext([IntPtr](-4))

$proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { Write-Host "NO PROCESS: $ProcessName"; exit 1 }

# 不能用 MainWindowHandle：本进程还有一个 56×56 的悬浮球分层窗口，它会被挑中（实测就是）。
# 改成枚举该进程的所有可见顶级窗口，取面积最大的那个（= 真正的主窗口）。
$script:targetPid = $proc.Id
$script:best = [IntPtr]::Zero
$script:bestArea = 0.0
$cb = [QsPix.Native+EnumProc]{
    param($h, $l)
    $other = 0
    [void][QsPix.Native]::GetWindowThreadProcessId($h, [ref]$other)
    if ($other -eq $script:targetPid -and [QsPix.Native]::IsWindowVisible($h)) {
        $r = New-Object QsPix.Native+RECT
        [void][QsPix.Native]::GetWindowRect($h, [ref]$r)
        $a = [double]($r.Right - $r.Left) * [double]($r.Bottom - $r.Top)
        if ($a -gt $script:bestArea) { $script:bestArea = $a; $script:best = $h }
    }
    return $true
}
[void][QsPix.Native]::EnumWindows($cb, [IntPtr]::Zero)

$hwnd = $script:best
if ($hwnd -eq [IntPtr]::Zero) { Write-Host 'NO WINDOW'; exit 1 }

$rect = New-Object QsPix.Native+RECT
[void][QsPix.Native]::GetWindowRect($hwnd, [ref]$rect)
$w = $rect.Right - $rect.Left
$h = $rect.Bottom - $rect.Top
$scale = if ($Dpi -gt 0) { $Dpi / 96.0 } else { 1.0 }

# ---- 抓图：先 PrintWindow，拿不到可信内容再退回「置前 + 截屏」 ----
function Capture-PrintWindow([IntPtr]$target, [int]$width, [int]$height) {
    $hdc = [QsPix.Native]::GetWindowDC($target)
    $memDc = [QsPix.Native]::CreateCompatibleDC($hdc)
    $memBmp = [QsPix.Native]::CreateCompatibleBitmap($hdc, $width, $height)
    $old = [QsPix.Native]::SelectObject($memDc, $memBmp)
    $ok = [QsPix.Native]::PrintWindow($target, $memDc, 2)   # 2 = PW_RENDERFULLCONTENT
    [void][QsPix.Native]::SelectObject($memDc, $old)
    $result = $null
    if ($ok) {
        $result = New-Object System.Drawing.Bitmap $width, $height
        $g = [System.Drawing.Graphics]::FromImage($result)
        $src = [System.Drawing.Image]::FromHbitmap($memBmp)
        $g.DrawImageUnscaled($src, 0, 0)
        $g.Dispose(); $src.Dispose()
    }
    [void][QsPix.Native]::DeleteObject($memBmp)
    [void][QsPix.Native]::DeleteDC($memDc)
    [void][QsPix.Native]::ReleaseDC($target, $hdc)
    return $result
}

$bmp = Capture-PrintWindow $hwnd $w $h
$method = 'PrintWindow'

if ($null -eq $bmp) {
    if ([QsPix.Native]::IsIconic($hwnd)) { [void][QsPix.Native]::ShowWindow($hwnd, 9) }
    [void][QsPix.Native]::SetForegroundWindow($hwnd)
    Start-Sleep -Milliseconds 600
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
    $g.Dispose()
    $method = '前台截屏'
}

$fgNow = [QsPix.Native]::GetForegroundWindow()
Write-Host ("窗口 hwnd={0} rect=({1},{2}) {3}x{4}  scale={5}  抓图={6}  当前前台窗口={7}" -f `
        $hwnd, $rect.Left, $rect.Top, $w, $h, $scale, $method, $fgNow)

$colors = @()
foreach ($pair in $Points.Split(';')) {
    $xy = $pair.Split(',')
    $lx = [double]$xy[0]; $ly = [double]$xy[1]
    $px = [int]($lx * $scale); $py = [int]($ly * $scale)
    if ($px -lt 0 -or $py -lt 0 -or $px -ge $w -or $py -ge $h) { Write-Host ("  ({0},{1}) 超出窗口范围" -f $lx, $ly); continue }
    $c = $bmp.GetPixel($px, $py)
    $hex = '#{0:X2}{1:X2}{2:X2}' -f $c.R, $c.G, $c.B
    $colors += $hex
    Write-Host ("  逻辑({0},{1}) 物理({2},{3}) → {4}" -f $lx, $ly, $px, $py, $hex)
}

# 自检：所有采样点同色且是「一片纯黑」→ 这次抓图不可信（PrintWindow 对 Composition 偶尔如此）
$distinct = ($colors | Sort-Object -Unique)
if ($distinct.Count -eq 1 -and $distinct[0] -eq '#000000') {
    Write-Host '警告：所有采样点都是纯黑 —— 这次抓图不可信（PrintWindow 未拿到内容），上面的数字不要用'
}

# 颜色直方图（`-ScanGrid`）：见参数说明 —— 这是「主题色到底有没有画出来」的**布局无关**判据。
if ($ScanGrid -gt 0) {
    $hist = @{}
    $sampled = 0
    for ($y = 0; $y -lt $h; $y += $ScanGrid) {
        for ($x = 0; $x -lt $w; $x += $ScanGrid) {
            $c = $bmp.GetPixel($x, $y)
            $hex = '#{0:X2}{1:X2}{2:X2}' -f $c.R, $c.G, $c.B
            $hist[$hex] = if ($hist.ContainsKey($hex)) { $hist[$hex] + 1 } else { 1 }
            $sampled++
        }
    }

    Write-Host ("颜色直方图：step={0} px，采样 {1} 点，出现最多的 10 种：" -f $ScanGrid, $sampled)
    foreach ($kv in ($hist.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 10)) {
        Write-Host ("  {0} × {1}" -f $kv.Key, $kv.Value)
    }

    # 只把「成规模」的颜色写成机器可读的一行：零星的抗锯齿过渡色没有判据价值。
    $threshold = [Math]::Max(10, [int]($sampled * 0.01))
    foreach ($kv in ($hist.GetEnumerator() | Where-Object { $_.Value -ge $threshold } | Sort-Object Value -Descending)) {
        Write-Host ("scan: {0} x {1}" -f $kv.Key, $kv.Value)
    }
}

$bmp.Dispose()
