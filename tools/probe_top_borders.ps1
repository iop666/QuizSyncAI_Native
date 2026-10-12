# 取「主界面顶部那两条黑框」的像素证据。
#
# 用户报告：「windows 主界面内容区块的黑框和上方标题栏下方的黑框重叠，导致视觉看比别的地方宽」。
# agent 看不到屏幕，所以这里把**标题栏以下的每一行**都量出来：
#   * 每行的「深色像素数」（亮度 < 100）与最深色的值、以及深色像素的 x 范围；
#   * 由此可以定位：黑框在第几行、有多厚、横跨多宽、有没有两条贴在一起。
#
# 两个已知坑（都写在 tools/probe_mica_ab.ps1 里，这里沿用）：
#   ① `$proc.MainWindowHandle` 会抓到悬浮球（96×96）→ 自己 EnumWindows 取面积最大者；
#   ② 必须先 SetProcessDPIAware()，否则拿到的是虚拟化坐标（本机 200% DPI）。
#
# 用法：powershell -ExecutionPolicy Bypass -File tools/probe_top_borders.ps1 [-Rows 240]

param(
    [int]$Rows = 240,
    [string]$OutPng = 'D:\DeepseekHarness\TemporaryConversationFile\misc\p4-verify\win-top.png',
    [string]$ProcessName = 'QuizSync.App',
    [switch]$Profile,
    [int]$ProfileFrom = 56,
    [int]$ProfileTo = 110,
    [int]$ProfileStep = 220
)

$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Drawing

Add-Type @'
using System;
using System.Runtime.InteropServices;
public class QsWin2 {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern IntPtr GetWindowDC(IntPtr h);
  [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
  [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
  [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
  [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
  [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
  [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  public delegate bool EnumProc(IntPtr h, IntPtr p);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

  public static IntPtr Largest(uint target) {
    IntPtr best = IntPtr.Zero; long bestArea = 0;
    EnumWindows(delegate(IntPtr h, IntPtr p) {
      uint pid; GetWindowThreadProcessId(h, out pid);
      if (pid != target || !IsWindowVisible(h)) return true;
      RECT r; if (!GetWindowRect(h, out r)) return true;
      long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
      if (area > bestArea) { bestArea = area; best = h; }
      return true;
    }, IntPtr.Zero);
    return best;
  }
}
'@
[void][QsWin2]::SetProcessDPIAware()

$proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { Write-Host 'ABORT: 应用没在运行'; exit 2 }

$hwnd = [QsWin2]::Largest([uint32]$proc.Id)
if ($hwnd -eq [IntPtr]::Zero) { Write-Host 'ABORT: 找不到主窗口'; exit 2 }


$rect = New-Object QsWin2+RECT
[void][QsWin2]::GetWindowRect($hwnd, [ref]$rect)
$w = $rect.Right - $rect.Left
$h = $rect.Bottom - $rect.Top
Write-Host ("窗口物理矩形 ({0},{1})  {2}x{3}" -f $rect.Left, $rect.Top, $w, $h)

# ---------------------------------------------------------------------------
# 抓图：① 先 PrintWindow（抓窗口自身内容，与遮挡无关）；② 自检像不像真内容；③ 不行才退回截屏。
# ---------------------------------------------------------------------------
function Test-Plausible([System.Drawing.Bitmap]$bitmap, [int]$stepX, [int]$stepY) {
    $seen = @{}
    for ($y = 0; $y -lt $bitmap.Height; $y += $stepY) {
        for ($x = 0; $x -lt $bitmap.Width; $x += $stepX) {
            $c = $bitmap.GetPixel($x, $y)
            $key = '{0:X2}{1:X2}{2:X2}' -f $c.R, $c.G, $c.B
            $seen[$key] = 1
            if ($seen.Count -ge 5) { return $true }
        }
    }
    return $false
}

$bmp = $null
$method = ''
try {
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $hdc = [QsWin2]::GetWindowDC($hwnd)
    $memDc = [QsWin2]::CreateCompatibleDC($hdc)
    $memBmp = [QsWin2]::CreateCompatibleBitmap($hdc, $w, $h)
    $old = [QsWin2]::SelectObject($memDc, $memBmp)
    $ok = [QsWin2]::PrintWindow($hwnd, $memDc, 2)   # 2 = PW_RENDERFULLCONTENT（DirectComposition 需要它）
    [void][QsWin2]::SelectObject($memDc, $old)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.GetHdc() | Out-Null
    $g.ReleaseHdc() | Out-Null
    # 从 memBmp 拷到托管位图
    $g2 = [System.Drawing.Graphics]::FromImage($bmp)
    $g2.Dispose()
    $src = [System.Drawing.Image]::FromHbitmap($memBmp)
    $g3 = [System.Drawing.Graphics]::FromImage($bmp)
    $g3.DrawImageUnscaled($src, 0, 0)
    $g3.Dispose(); $src.Dispose()
    [void][QsWin2]::DeleteObject($memBmp)
    [void][QsWin2]::DeleteDC($memDc)
    [void][QsWin2]::ReleaseDC($hwnd, $hdc)
    $method = 'PrintWindow'
    if (-not (Test-Plausible $bmp 40 40)) {
        Write-Host 'PrintWindow 拿到的图像几乎是单色 —— 判定为「没抓到真内容」，改用前台截屏'
        $bmp.Dispose(); $bmp = $null
    }
} catch {
    Write-Host ("PrintWindow 失败：{0}" -f $_.Exception.Message)
    if ($bmp) { $bmp.Dispose(); $bmp = $null }
}

if ($null -eq $bmp) {
    # 退回：置前 + 截屏（置前可能被系统拒绝，所以再看一次像不像真内容）
    if ([QsWin2]::IsIconic($hwnd)) { [void][QsWin2]::ShowWindow($hwnd, 9) }
    [void][QsWin2]::SetForegroundWindow($hwnd)
    Start-Sleep -Seconds 2
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size $w, $h))
    $g.Dispose()
    $method = '前台截屏'
    if (-not (Test-Plausible $bmp 40 40)) {
        Write-Host 'FAIL: 两种方式都没拿到可信图像（可能是窗口被完全遮挡）。下面的数据不可信，已中止。'
        $bmp.Dispose()
        exit 3
    }
}

Write-Host ("抓图方式：{0}（已通过『不是单色』自检）" -f $method)

$outDir = Split-Path -Parent $OutPng
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }
$bmp.Save($OutPng, [System.Drawing.Imaging.ImageFormat]::Png)
Write-Host ("截图已存 {0}" -f $OutPng)
Write-Host ''

Write-Host '行号  深色像素数  深色x范围        该行最深色   整行最常见色'
for ($y = 0; $y -lt [Math]::Min($Rows, $h); $y++) {
    $dark = 0; $minX = -1; $maxX = -1; $darkest = 999; $darkestHex = ''
    $colors = @{}
    for ($x = 0; $x -lt $w; $x += 2) {
        $c = $bmp.GetPixel($x, $y)
        $lum = [int](0.299 * $c.R + 0.587 * $c.G + 0.114 * $c.B)
        $key = '{0:X2}{1:X2}{2:X2}' -f $c.R, $c.G, $c.B
        if ($colors.ContainsKey($key)) { $colors[$key]++ } else { $colors[$key] = 1 }
        if ($lum -lt 100) {
            $dark++
            if ($minX -lt 0) { $minX = $x }
            $maxX = $x
            if ($lum -lt $darkest) { $darkest = $lum; $darkestHex = $key }
        }
    }
    if ($dark -gt 0) {
        $top = ($colors.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 1)
        Write-Host ("{0,4}  {1,10}  {2,6}..{3,-6}  #{4}    #{5} ({6} 次)" -f `
            $y, $dark, $minX, $maxX, $darkestHex, $top.Key, $top.Value)
    }
}

Write-Host ''
Write-Host '（只打印有深色像素的行；深色判据 = 亮度 < 100）'

# ---------------------------------------------------------------------------
# 剖面模式：同一行在若干 x 上取样，最容易看出「横线在第几行、横跨多宽、是不是两条贴一起」。
# 用户报的「内容区块的黑框和标题栏下方的黑框重叠」就是这种形态 —— 只看深色计数看不出来。
# ---------------------------------------------------------------------------
if ($Profile) {
    Write-Host ''
    Write-Host ("=== 逐行剖面（y {0}..{1}，x 步长 {2}）===" -f $ProfileFrom, $ProfileTo, $ProfileStep)
    $xs = @()
    for ($x = 8; $x -lt $w; $x += $ProfileStep) { $xs += $x }
    Write-Host (('y'.PadLeft(5)) + '  ' + (($xs | ForEach-Object { ('x' + $_).PadLeft(9) }) -join ''))

    for ($y = $ProfileFrom; $y -le [Math]::Min($ProfileTo, $h - 1); $y++) {
        $cells = @()
        foreach ($x in $xs) {
            $c = $bmp.GetPixel($x, $y)
            $cells += ('#{0:X2}{1:X2}{2:X2}' -f $c.R, $c.G, $c.B).PadLeft(9)
        }
        Write-Host (('{0,5}' -f $y) + '  ' + ($cells -join ''))
    }

    Write-Host ''
    Write-Host '=== 与上一行不同的取样点数（横线的上下边界就在这里）==='
    $prevRow = $null
    for ($y = $ProfileFrom; $y -le [Math]::Min($ProfileTo, $h - 1); $y++) {
        $row = @()
        for ($x = 0; $x -lt $w; $x += 4) {
            $c = $bmp.GetPixel($x, $y)
            $row += ('{0:X2}{1:X2}{2:X2}' -f $c.R, $c.G, $c.B)
        }
        if ($null -ne $prevRow) {
            $same = 0
            for ($i = 0; $i -lt $row.Count; $i++) { if ($row[$i] -eq $prevRow[$i]) { $same++ } }
            $diff = $row.Count - $same
            if ($diff -gt 0) { Write-Host ("  y={0,4}  与上一行不同 = {1}/{2}" -f $y, $diff, $row.Count) }
        }
        $prevRow = $row
    }
}

# 位图必须在**所有**取样之后才释放
# （踩过：提前释放 -> GetPixel 报 Parameter is not valid，探针自己崩且看不出原因）
$bmp.Dispose()
