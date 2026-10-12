import sys

from PIL import Image

# 把一个矩形区域打成 ASCII 色块图（每格取一个像素），用来**看形状**：
# 圆角、内缩、错位这类问题都是「平面上的形状」，采样几个点看不出来，打成图就一目了然。
#
# 用法：python ascii_map.py <png> <x0> <y0> <x1> <y1> [step]

path = sys.argv[1]
x0, y0, x1, y1 = (int(v) for v in sys.argv[2:6])
step = int(sys.argv[6]) if len(sys.argv) > 6 else 8

image = Image.open(path).convert("RGB")


def classify(rgb):
    r, g, b = rgb
    if r > 245 and g > 245 and b > 245:
        return "W"  # 白（表面/AppBar）
    if abs(r - 243) < 12 and abs(g - 245) < 12 and abs(b - 244) < 12:
        return "."  # 画布 #F3F5F4
    if r < 60 and g < 60 and b < 60:
        return "#"  # 很暗（窗口底）
    if abs(r - g) < 6 and abs(g - b) < 6:
        return "o"  # 灰（压暗后的白）
    return "?"


print(f"区域 x={x0}..{x1}, y={y0}..{y1}, 步长 {step}（W=白 .=画布 o=灰 #=很暗 ?=其它）")
header = "      " + "".join(str((x // 100) % 10) for x in range(x0, x1, step))
print(header)
for y in range(y0, y1, step):
    row = "".join(classify(image.getpixel((x, y))) for x in range(x0, x1, step))
    print(f"{y:5d} {row}")
