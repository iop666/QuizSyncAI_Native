param(
    [Parameter(Mandatory = $true)][string]$Region,   # 屏幕物理坐标 "x,y,w,h"
    [Parameter(Mandatory = $true)][string]$Rows      # "y1,y2,..." —— 相对 Region 顶部的行号
)

# 打印屏幕上任意矩形里若干行的颜色游程。
# 判形状用：胶囊（两端半圆）的**顶行**颜色跨度 ≈ 宽 − 高；真正的椭圆顶行只有几个像素。
# 这样不用肉眼看截图也能区分「圆角 999 渲染成胶囊」还是「渲染成椭圆」。

Add-Type -AssemblyName System.Drawing
Add-Type -Namespace QsRuns -Name Native -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
'@
[void][QsRuns.Native]::SetProcessDpiAwarenessContext([IntPtr](-4))

$p = $Region.Split(',')
$x = [int]$p[0]; $y = [int]$p[1]; $w = [int]$p[2]; $h = [int]$p[3]

$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($x, $y, 0, 0, (New-Object System.Drawing.Size($w, $h)))
$g.Dispose()
Write-Host ("Region ({0},{1}) {2}x{3}" -f $x, $y, $w, $h)

foreach ($rs in $Rows.Split(',')) {
    $ry = [int]$rs
    if ($ry -lt 0 -or $ry -ge $h) { Write-Host ("  行 {0} 超出范围" -f $ry); continue }
    $colors = @()
    for ($i = 0; $i -lt $w; $i++) { $c = $bmp.GetPixel($i, $ry); $colors += ("#{0:X2}{1:X2}{2:X2}" -f $c.R, $c.G, $c.B) }
    $out = @(); $prev = $null; $count = 0; $start = 0
    for ($i = 0; $i -lt $colors.Count; $i++) {
        if ($colors[$i] -eq $prev) { $count++ }
        else {
            if ($null -ne $prev) { $out += ("[{0}..{1}]x{2} {3}" -f $start, ($i - 1), $count, $prev) }
            $prev = $colors[$i]; $count = 1; $start = $i
        }
    }
    if ($null -ne $prev) { $out += ("[{0}..{1}]x{2} {3}" -f $start, ($colors.Count - 1), $count, $prev) }
    Write-Host ("  行 y={0}:" -f $ry)
    $out | ForEach-Object { Write-Host ('    ' + $_) }
}
$bmp.Dispose()
