# 往系统剪贴板放一张**真图**（验收剪贴板监听用）。
#
# 为什么单独一个脚本：`Clipboard.SetImage` 要求 **STA 线程**，
# 而本机的 pwsh 7 默认是 MTA（会抛 `ThreadStateException`）——
# 所以必须用 `powershell.exe -STA -File` 起一个 5.1 进程来做这件事。
#
# 用法：powershell -ExecutionPolicy Bypass -File tools\clipboard_put_image.ps1 [-Seed 1]
# 打印：`OK <宽>x<高>`；失败时非零退出。
param([int]$Seed = 1)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$w = 320
$h = 180
$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.Clear([System.Drawing.Color]::White)
# 按 Seed 画几笔：同一 Seed 出**同一张图**（用来验证「同一张图不反复识别」），
# 不同 Seed 出不同的图（用来验证「新图会识别」）。
$brush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, (20 * $Seed) % 255, 60, 200))
$g.FillRectangle($brush, 20, 20, 80 + (10 * $Seed), 30)
$g.FillRectangle($brush, 20, 70, 200, 6)
$pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::Black), 2
$g.DrawRectangle($pen, 10, 10, $w - 20, $h - 20)
$g.Dispose()

[System.Windows.Forms.Clipboard]::SetImage($bmp)
$brush.Dispose()
$pen.Dispose()
$bmp.Dispose()

Write-Output ("OK {0}x{1}" -f $w, $h)
