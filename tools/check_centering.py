import io
import re
import sys

# 量「这些文字在屏幕上是否水平居中」。
#
# 为什么需要它：居不居中是**布局**问题，控件树只看「有没有这个节点」是看不出来的；
# 而这次用户报的正是「空态提示不居中、居左了」。判据要落在**数值**上：
# 节点中心与屏幕中心差多少像素。
#
# 用法：python check_centering.py <dump.xml> <文字1> [文字2 ...]

dump = sys.argv[1]
targets = sys.argv[2:]

text = io.open(dump, encoding="utf-8", errors="replace").read()
root = re.search(r'bounds="\[0,0\]\[(\d+),(\d+)\]"', text)
width = int(root.group(1)) if root else 0
center = width // 2

print(f"屏幕宽 {width}，中心 x = {center}")

found = 0
for node in re.findall(r"<node[^>]*>", text):
    m = re.search(r'text="([^"]*)"', node)
    b = re.search(r'bounds="\[(\d+),(\d+)\]\[(\d+),(\d+)\]"', node)
    if not m or not b:
        continue
    value = m.group(1)
    if value not in targets:
        continue
    x1, y1, x2, y2 = (int(b.group(i)) for i in (1, 2, 3, 4))
    node_center = (x1 + x2) // 2
    # 文字宽度不同，允许的偏差取文字自身宽度的一半（即「中心对齐」而不是「左边缘对齐」）
    tolerance = max(24, (x2 - x1) // 2)
    ok = abs(node_center - center) <= tolerance
    found += 1
    print(
        f"  [{'OK  ' if ok else 'FAIL'}] 中心x={node_center:4d} (偏差{node_center - center:+4d}, 容差±{tolerance}) "
        f"左={x1:4d} 右={x2:4d}  {value[:28]}"
    )

if found == 0:
    print("  （这些文字一个都没找到）")
    sys.exit(2)

sys.exit(0)
