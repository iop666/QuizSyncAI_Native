import io
import re
import sys

# 打印若干文字节点的 bounds（用于「手势/动画进行中坐标是否变了」这类**数值**判据）。
#
# 为什么不用颜色判断：压栈页与它下面那一页的边缘颜色往往相同（都是白 AppBar / 同色画布），
# 采点分不出来；而**坐标内缩**是缩放变换的直接证据，没有歧义。
#
# 用法：python node_bounds.py <dump.xml> "文字1" ["文字2" ...]
# 文字请用 --text-file 传？不需要 —— 本脚本**只用 ASCII 参数**太受限，
# 所以支持从文件读：把每行一个关键字写进 <dump>.keys 或显式给 --keys <file>。

dump = sys.argv[1]
args = sys.argv[2:]
keys = []
if args and args[0] == "--keys":
    with io.open(args[1], "r", encoding="utf-8") as f:
        keys = [line.strip() for line in f if line.strip()]
else:
    keys = args

text = io.open(dump, encoding="utf-8", errors="replace").read()
nodes = re.findall(r"<node[^>]*>", text)

for key in keys:
    hit = None
    for node in nodes:
        m = re.search(r'text="([^"]*)"', node)
        b = re.search(r'bounds="\[(\d+),(\d+)\]\[(\d+),(\d+)\]"', node)
        if m and b and key in m.group(1):
            hit = (int(b.group(1)), int(b.group(2)), int(b.group(3)), int(b.group(4)))
            break
    if hit:
        x1, y1, x2, y2 = hit
        print(f"  {key[:24]:26s} 左={x1:4d} 上={y1:4d} 右={x2:4d} 下={y2:4d}  宽={x2 - x1:4d} 中心x={(x1 + x2) // 2:4d}")
    else:
        print(f"  {key[:24]:26s} （没找到）")
