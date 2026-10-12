import sys

from PIL import Image

# 采一张截图上某个点的颜色。
# 用途：agent 看不到屏幕，但「主题到底有没有变」可以用背景像素量出来 ——
# 只看控件树是看不出来的（深浅两套主题的控件树**完全一样**）。
path = sys.argv[1]
x = int(sys.argv[2])
y = int(sys.argv[3])
image = Image.open(path).convert("RGB")
width, height = image.size
px = max(0, min(width - 1, x))
py = max(0, min(height - 1, y))
r, g, b = image.getpixel((px, py))
print(f"{width}x{height} ({px},{py}) #{r:02X}{g:02X}{b:02X}")
