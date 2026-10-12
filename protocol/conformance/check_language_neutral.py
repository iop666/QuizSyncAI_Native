#!/usr/bin/env python3
"""规范 / Schema / 示例的「语言中立」自检（只用 Python 标准库）。

CONTRIBUTING.md 的评审红线第 1 条：规范里不得出现任何一端的实现词汇。
本脚本扫描 `spec/`、`schema/`、`examples/` 下的全部 Markdown（含子目录），
命中即失败（退出码 1）。规则：大小写不敏感、按**子串**匹配——

    dart flutter kotlin shelf drift dio riverpod pubspec
    quizsync_core quizsync_ui  packages/  apps/  以及任何 Windows 盘符路径

注意子串匹配的副作用（命中后先分辨真假，不是真实现词汇就换个中立写法）；
SQLite 表名 / 列名、JSON 字段名、HTTP 头名、端口号、路由路径是**契约本身**，
不在禁用范围内。

用法：
    python conformance/check_language_neutral.py [仓库根目录]
默认仓库根 = 本脚本的上两级目录（从仓库根目录或别处跑都一样）。
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

# Windows 控制台默认 GBK：显式按 UTF-8 输出，中文信息才不会变成乱码或抛 UnicodeEncodeError。
for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8", errors="replace")
    except Exception:  # 老 Python / 被重定向的流：放弃，不影响检查结果
        pass

# 被检查的目录（相对仓库根）与文件后缀。
SCAN_DIRS = ("spec", "schema", "examples")
SUFFIX = "*.md"

# 禁用词汇（与 CONTRIBUTING.md 的评审红线一一对应）。
# 最后一项是「任何 Windows 绝对路径」：规范里不该出现某一台机器的目录结构。
PATTERN = re.compile(
    r"dart|flutter|kotlin|shelf|drift|dio|riverpod|pubspec"
    r"|quizsync_core|quizsync_ui"
    r"|[A-Za-z]:\\|packages/|apps/",
    re.IGNORECASE,
)

ROOT = Path(__file__).resolve().parent.parent


def main(argv: list[str]) -> int:
    root = Path(argv[1]).resolve() if len(argv) > 1 else ROOT
    print(f"[语言中立] 仓库根：{root}")
    print(f"[语言中立] 扫描：{'、'.join(SCAN_DIRS)} 下的 {SUFFIX}（大小写不敏感、子串匹配）")

    files: list[Path] = []
    missing: list[str] = []
    for name in SCAN_DIRS:
        directory = root / name
        if not directory.is_dir():
            missing.append(name)
            continue
        files.extend(sorted(directory.rglob(SUFFIX)))

    if not files:
        print(f"[语言中立] FAIL：一个 Markdown 都没扫到，检查路径（缺目录：{'、'.join(missing) or '无'}）")
        return 1

    hits: list[str] = []
    for path in files:
        try:
            lines = path.read_text(encoding="utf-8").splitlines()
        except UnicodeDecodeError as exc:
            hits.append(f"{path.relative_to(root)}: 不是合法 UTF-8：{exc}")
            continue
        for lineno, line in enumerate(lines, start=1):
            for match in PATTERN.finditer(line):
                hits.append(
                    f"{path.relative_to(root)}:{lineno}: 命中「{match.group(0)}」"
                    f" → {line.strip()[:120]}"
                )

    if hits:
        print(f"\n[语言中立] FAIL：{len(files)} 个 Markdown 里共 {len(hits)} 处实现词汇")
        for item in hits:
            print(f"  - {item}")
        return 1

    tail = f"（缺目录：{'、'.join(missing)}）" if missing else ""
    print(f"[语言中立] OK：{len(files)} 个 Markdown 全部语言中立{tail}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
