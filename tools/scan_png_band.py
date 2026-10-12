import sys

from PIL import Image

# 扫一条横带，报告最亮像素与亮像素占比。
# 用途：判断状态栏**图标**在不在 —— 状态栏底色对不对可以采一个点，
# 但「图标是浅色还是深色」要看那一条带里有没有足够亮的像素。
# 图标是细笔画，采单点必然漏掉，所以按带扫。
path = sys.argv[1]
y0 = int(sys.argv[2])
y1 = int(sys.argv[3])
image = Image.open(path).convert("RGB")
width, height = image.size
y0 = max(0, min(height - 1, y0))
y1 = max(0, min(height, y1))

brightest = (0, 0, 0)
bright_count = 0
total = 0
for y in range(y0, y1):
    for x in range(0, width, 2):
        r, g, b = image.getpixel((x, y))
        total += 1
        lum = 0.299 * r + 0.587 * g + 0.114 * b
        if lum > 150:
            bright_count += 1
        if lum > (0.299 * brightest[0] + 0.587 * brightest[1] + 0.114 * brightest[2]):
            brightest = (r, g, b)

print(
    f"带 y={y0}..{y1}  最亮 #{brightest[0]:02X}{brightest[1]:02X}{brightest[2]:02X}  "
    f"亮像素 {bright_count}/{total} ({100.0 * bright_count / max(1, total):.2f}%)"
)
