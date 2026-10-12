r"""从 uiautomator dump 里定位节点并算出点击坐标。

为什么要有这个脚本（这一程反复摔的三件事）：

1. **中文不能当命令行参数**。`python x.py dump.xml 识别出` 里的中文会被 PowerShell /
   控制台代码页吃掉，回显成 `NOT FOUND: ʶ���`，于是「找不到节点」被误判成「界面上没有」。
   所以选择器走 **文件**（`--text-file`，UTF-8），不走进程参数。
2. **按文字找会命中标题**。「删除这条识别记录？」里含「删除」，按文字找按钮会点到标题上，
   表现为「点了没反应」，而真正的按钮差着几百像素。所以默认只找 `clickable="true"` 的节点。
3. **坐标解析不能靠 split**。`list_android_clickable` 那种带缩进的输出用 `-split ' '`
   会切出空串，拼成 `1467,,1610` 让 `adb shell input tap` 直接报错。
   这里一律用正则取 `(\d+),(\d+)`。

用法：

    python android_find.py <dump.xml> --text-file sel.txt [--nth 0] [--all] [--any-clickable]

输出一行 `x y`（或每个候选一行 `x y text`，供人工确认）。
退出码 1 表示没找到 —— 调用方据此报错，不要拿空坐标去 tap。
"""

import io
import re
import sys

NODE_RE = re.compile(r"<node[^>]*>")
BOUNDS_RE = re.compile(r'bounds="\[(\d+),(\d+)\]\[(\d+),(\d+)\]"')
TEXT_RE = re.compile(r'text="([^"]*)"')
DESC_RE = re.compile(r'content-desc="([^"]*)"')
ID_RE = re.compile(r'resource-id="([^"]*)"')


def center(node):
    bounds = BOUNDS_RE.search(node)
    if not bounds:
        return None
    x = (int(bounds.group(1)) + int(bounds.group(3))) // 2
    y = (int(bounds.group(2)) + int(bounds.group(4))) // 2
    return x, y


def label(node):
    for pattern in (TEXT_RE, DESC_RE, ID_RE):
        match = pattern.search(node)
        if match and match.group(1):
            return match.group(1)
    return ""


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 1

    dump = sys.argv[1]
    args = sys.argv[2:]
    needle = None
    nth = 0
    show_all = False
    # 默认**不**限制 clickable：标签页那种「可点容器 + 不可点子文本」的结构下，
    # 只找 clickable 会一个都找不到（实测：历史记录 tab NOT FOUND）。
    # 需要精确点按钮时再显式加 --clickable。
    clickable_only = False
    substring = True

    index = 0
    while index < len(args):
        arg = args[index]
        if arg == "--text-file":
            with io.open(args[index + 1], "r", encoding="utf-8") as handle:
                needle = handle.read().strip()
            index += 2
            continue
        if arg == "--nth":
            nth = int(args[index + 1])
            index += 2
            continue
        if arg == "--all":
            show_all = True
        elif arg == "--any-clickable":
            clickable_only = False
        elif arg == "--exact":
            substring = False
        index += 1

    with io.open(dump, "r", encoding="utf-8", errors="replace") as handle:
        xml = handle.read()

    hits = []
    for node in NODE_RE.findall(xml):
        if clickable_only and 'clickable="true"' not in node:
            continue
        text = label(node)
        if needle is not None:
            matched = (needle in text) if substring else (needle == text)
            if not matched:
                continue
        point = center(node)
        if point:
            hits.append((point[0], point[1], text))

    if not hits:
        print("NOT FOUND")
        return 1

    if show_all:
        for x, y, text in hits:
            print(f"{x} {y} {text}")
        return 0

    x, y, text = hits[min(nth, len(hits) - 1)]
    print(f"{x} {y}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
