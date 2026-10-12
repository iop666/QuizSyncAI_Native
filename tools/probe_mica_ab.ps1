# Mica 像素 A/B 探针：把窗口平移后、在同一逻辑位置采样标题栏条，颜色若变化说明背板真的合成出来了。
# 用法：powershell -ExecutionPolicy Bypass -File probe_mica_ab.ps1
$ErrorActionPreference = 'Continue'

Add-Type -ReferencedAssemblies 'System.Drawing' -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class QsWin {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  public delegate bool EnumProc(IntPtr h, IntPtr p);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, System.Text.StringBuilder s, int n);
  public static IntPtr Largest(uint targetPid) {
    IntPtr best = IntPtr.Zero; long bestArea = 0;
    EnumWindows((h, p) => {
      uint pid; GetWindowThreadProcessId(h, out pid);
      if (pid != targetPid || !IsWindowVisible(h)) return true;
      RECT r; if (!GetWindowRect(h, out r)) return true;
      long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
      if (area > bestArea) { bestArea = area; best = h; }
      return true;
    }, IntPtr.Zero);
    return best;
  }
}
"@
[void][QsWin]::SetProcessDPIAware()

$exe = 'D:\ZCode\QuizSync_AI\windows\QuizSync.App\bin\Debug\net10.0-windows10.0.26100.0\win-x64\QuizSync.App.exe'
Get-Process QuizSync.App -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1
$proc = Start-Process $exe -PassThru
Start-Sleep -Seconds 9
# 注意：$proc.MainWindowHandle 会抓到**悬浮球**（96x96）—— 必须自己枚举，
# 取该进程可见顶层窗口里面积最大的那个（这才是主窗口）。
$hwnd = [QsWin]::Largest([uint32]$proc.Id)
if ($hwnd -eq [IntPtr]::Zero) { Write-Host "FAIL: 没找到该进程的可见顶层窗口"; exit 1 }
Write-Host ("主窗口句柄 = {0}（按面积最大挑的，避开悬浮球）" -f $hwnd)

$rect = New-Object QsWin+RECT
[void][QsWin]::GetWindowRect($hwnd, [ref]$rect)
Write-Host ("窗口物理矩形 ({0},{1})-({2},{3})  尺寸 {4}x{5}" -f $rect.Left, $rect.Top, $rect.Right, $rect.Bottom, ($rect.Right - $rect.Left), ($rect.Bottom - $rect.Top))
if ($rect.Right - $rect.Left -lt 200) { Write-Host "FAIL: 窗口矩形不合理（可能是虚拟化坐标）"; exit 1 }

function Sample([int]$px, [int]$py) {
    $bmp = New-Object System.Drawing.Bitmap 1, 1
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($px, $py, 0, 0, (New-Object System.Drawing.Size 1, 1))
    $c = $bmp.GetPixel(0, 0)
    $g.Dispose(); $bmp.Dispose()
    return $c
}

# 标题栏条（物理 y = 顶 + 28 ≈ 逻辑 14）；内容区作对照
$stripDx = 600; $stripDy = 28; $bodyDx = 600; $bodyDy = 420
$strip1 = Sample ($rect.Left + $stripDx) ($rect.Top + $stripDy)
$body1  = Sample ($rect.Left + $bodyDx)  ($rect.Top + $bodyDy)

$shiftX = 520; $shiftY = 260
[void][QsWin]::SetWindowPos($hwnd, [IntPtr]::Zero, $rect.Left + $shiftX, $rect.Top + $shiftY, 0, 0, 0x0001 -bor 0x0004)  # SWP_NOSIZE | SWP_NOZORDER
Start-Sleep -Seconds 3

$strip2 = Sample ($rect.Left + $stripDx + $shiftX) ($rect.Top + $stripDy + $shiftY)
$body2  = Sample ($rect.Left + $bodyDx + $shiftX)  ($rect.Top + $bodyDy + $shiftY)

function Hex($c) { '#{0:X2}{1:X2}{2:X2}' -f $c.R, $c.G, $c.B }
$stripChanged = (Hex $strip1) -ne (Hex $strip2)
$bodyChanged = (Hex $body1) -ne (Hex $body2)

Write-Host ""
Write-Host ("标题栏条  移前 {0}   移后 {1}   {2}" -f (Hex $strip1), (Hex $strip2), $(if ($stripChanged) { '变化 -> 背板在合成桌面' } else { '无变化 -> 背板没生效（或那条不透明）' }))
Write-Host ("内容区    移前 {0}   移后 {1}   {2}" -f (Hex $body1), (Hex $body2), $(if ($bodyChanged) { '变化（不该变，说明内容区也透明了）' } else { '无变化（符合预期：内容区不透明）' }))
Write-Host ""
if ($stripChanged -and -not $bodyChanged) {
    Write-Host "判定：PASS —— 标题栏条能看到背板，内容区保持不透明"
} elseif (-not $stripChanged) {
    Write-Host "判定：FAIL —— 标题栏条没有背板效果"
} else {
    Write-Host "判定：存疑 —— 两处都变了，需要人工看一眼"
}

Get-Process QuizSync.App -ErrorAction SilentlyContinue | Stop-Process -Force
Write-Host "（探针已把应用关掉）"
