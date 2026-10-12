import io
import re
import sys

# 读 Android 的 uiautomator dump，检查主壳该有的东西。
# 刻意用脚本文件而不是 `python -c`：中文写在命令行里会被 PowerShell 按 GBK 吃掉，
# 之前就因此把「三个 tab 都不在」误判成 0 次匹配。
path = sys.argv[1]
text = io.open(path, encoding="utf-8", errors="replace").read()

expect = ["当前任务", "历史记录", "设置"]
for kw in expect:
    print(f"  {kw}: {len(re.findall(re.escape(kw), text))} 次")

# 顺便把带文字的节点打出来，便于人工核对
nodes = re.findall(r'text="([^"]+)"', text)
seen = []
for n in nodes:
    if n and n not in seen:
        seen.append(n)
print("--- 界面上的文字节点 ---")
for n in seen[:40]:
    print("   ", n)
