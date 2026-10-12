import io
import re
import sys

# 列出 dump 里**可点**的节点（文本 + 坐标）。
# 为什么要按 clickable 过滤：按文字找会命中标题（「允许显示在其他应用的上层」里也含「允许」），
# 于是「点了允许但权限没变」—— 其实点的是标题。判据要落在「这个节点能不能点」上。
path = sys.argv[1]
text = io.open(path, encoding="utf-8", errors="replace").read()

for node in re.findall(r"<node[^>]*>", text):
    if 'clickable="true"' not in node:
        continue
    bounds = re.search(r'bounds="\[(\d+),(\d+)\]\[(\d+),(\d+)\]"', node)
    if not bounds:
        continue
    x = (int(bounds.group(1)) + int(bounds.group(3))) // 2
    y = (int(bounds.group(2)) + int(bounds.group(4))) // 2
    label = re.search(r'text="([^"]*)"', node)
    desc = re.search(r'content-desc="([^"]*)"', node)
    cls = re.search(r'class="([^"]*)"', node)
    name = (label.group(1) if label else "") or (desc.group(1) if desc else "")
    print(f"  {x:5d},{y:5d}  {cls.group(1).split('.')[-1] if cls else '?':18s} {name}")
