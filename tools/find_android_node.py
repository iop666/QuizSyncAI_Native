import io
import re
import sys

# 在 uiautomator dump 里按文字找节点，输出它的中心坐标（给 `adb shell input tap` 用）。
# 为什么落成脚本文件：中文与正则里的引号写进 `python -c` 的命令行会被 PowerShell 解析，
# 实测直接报「表达式或语句中包含意外的标记」。
path = sys.argv[1]
target = sys.argv[2]
text = io.open(path, encoding="utf-8", errors="replace").read()

for node in re.findall(r"<node[^>]*>", text):
    if target in node:
        bounds = re.search(r'bounds="\[(\d+),(\d+)\]\[(\d+),(\d+)\]"', node)
        if bounds:
            x = (int(bounds.group(1)) + int(bounds.group(3))) // 2
            y = (int(bounds.group(2)) + int(bounds.group(4))) // 2
            print(f"{x} {y}")
            sys.exit(0)

print(f"NOT FOUND: {target}")
sys.exit(1)
