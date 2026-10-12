#!/usr/bin/env python3
"""一致性向量的语言无关自检（只用 Python 标准库，零第三方依赖）。

检查项（任一失败 → 退出码 1）：
  ① NDJSON 可解析：一行一个 JSON 对象（空行跳过，非对象行即失败）；
  ② step 从 1 起连续递增（第 n 个有效步骤必须是 step n，跳号即失败）；
  ③ step 在同一个文件里唯一；
  ④ 出现的 $变量 必须在同文件**更早**的步骤里被 capture 过，或属于内置变量
     （pairingCode / port）—— 与 Dart 回放器 `_var()` 的语义一致：动作先执行、
     断言后 capture，所以同一步里不能引用同一步 capture 出来的变量；
  ⑤ **域（domain）合法且声明在第一行**：`v1`（默认，所有实现都要放）或 `v2-provider`
     （只有带 Provider 角色的实现参与回放）。域写错，「谁该放这组向量」就成了一句空话。

用法：
    python conformance/check_vectors.py [向量目录]
默认向量目录 = 本脚本同级的 `vectors/`（从仓库根目录或别处跑都一样）。
"""

from __future__ import annotations

import json
import re
import sys
from pathlib import Path

# Windows 控制台默认 GBK：显式按 UTF-8 输出，中文信息才不会变成乱码或抛 UnicodeEncodeError。
for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8", errors="replace")
    except Exception:  # 老 Python / 被重定向的流：放弃，不影响检查结果
        pass

# 回放器内置变量（不需要 capture）：配对码与端口由回放器运行时提供。
BUILTIN_VARS = frozenset({"pairingCode", "port"})

# 合法域：`v1` = 所有实现都要放；`v2-provider` = 只有带 Provider 角色的实现参与。
# 加新域时必须同时改 `conformance/README.md` 的域表与回放器的分组开关。
DOMAINS = frozenset({"v1", "v2-provider"})
DEFAULT_DOMAIN = "v1"

# `$name` 形式的变量引用；`^[0-9a-f]{64}$` 这类正则里的尾部 $ 后面不是标识符，不会误命中。
VAR_RE = re.compile(r"\$([A-Za-z_][A-Za-z0-9_]*)")

DEFAULT_DIR = Path(__file__).resolve().parent / "vectors"


def iter_var_refs(node: object):
    """深度遍历一个 JSON 节点，产出其中所有 $变量名。"""
    if isinstance(node, str):
        for match in VAR_RE.finditer(node):
            yield match.group(1)
    elif isinstance(node, list):
        for item in node:
            yield from iter_var_refs(item)
    elif isinstance(node, dict):
        for value in node.values():
            yield from iter_var_refs(value)


def check_file(path: Path) -> tuple[int, list[str], str]:
    """检查一个 .ndjson，返回 (有效步骤数, 错误列表, 域)。"""
    errors: list[str] = []
    try:
        text = path.read_text(encoding="utf-8")
    except UnicodeDecodeError as exc:
        return 0, [f"{path.name}: ① 不是合法 UTF-8：{exc}"], DEFAULT_DOMAIN

    domain = DEFAULT_DOMAIN
    saw_header = False
    steps: list[tuple[int, dict]] = []  # (行号, 步骤对象)
    for lineno, raw in enumerate(text.splitlines(), start=1):
        line = raw.strip()
        if not line:
            continue
        try:
            obj = json.loads(line)
        except json.JSONDecodeError as exc:
            errors.append(f"{path.name}:{lineno}: ① 不是合法 JSON：{exc}")
            continue
        if not isinstance(obj, dict):
            errors.append(
                f"{path.name}:{lineno}: ① 一行必须是一个 JSON 对象，"
                f"实际是 {type(obj).__name__}"
            )
            continue

        # 头行：**没有 step 的第一行**，用来声明本文件的域（v1 文件可以不写，默认 v1）。
        if "step" not in obj and not saw_header and not steps:
            saw_header = True
            declared = obj.get("domain")
            if declared is None:
                errors.append(
                    f"{path.name}:{lineno}: ⑤ 头行缺少 domain"
                    f"（可选值：{'、'.join(sorted(DOMAINS))}）"
                )
            elif declared not in DOMAINS:
                errors.append(
                    f"{path.name}:{lineno}: ⑤ domain={declared!r} 不是合法域"
                    f"（可选值：{'、'.join(sorted(DOMAINS))}）"
                )
            else:
                domain = str(declared)
            continue

        steps.append((lineno, obj))

    if not steps:
        return 0, [*errors, f"{path.name}: ① 文件里没有任何步骤"], domain

    captured: set[str] = set()  # 到「当前步骤之前」为止 capture 出来的变量
    seen: dict[int, int] = {}  # step 值 -> 首次出现的行号
    for index, (lineno, step) in enumerate(steps):
        want = index + 1
        got = step.get("step")
        if not isinstance(got, int) or isinstance(got, bool):
            errors.append(f"{path.name}:{lineno}: ② 缺少整数 step（实际 {got!r}），应为 {want}")
        else:
            if got != want:
                errors.append(f"{path.name}:{lineno}: ② step={got}，应为 {want}（必须从 1 起连续）")
            if got in seen:
                errors.append(f"{path.name}:{lineno}: ③ step={got} 与第 {seen[got]} 行重复")
            else:
                seen[got] = lineno

        for name in sorted(set(iter_var_refs(step))):
            if name not in BUILTIN_VARS and name not in captured:
                errors.append(
                    f"{path.name}:{lineno}: ④ ${name} 在同文件更早的步骤里没有 capture 过"
                    f"（内置变量只有 {'、'.join(sorted(BUILTIN_VARS))}）"
                )

        expect = step.get("expect")
        if isinstance(expect, dict) and isinstance(expect.get("capture"), dict):
            captured.update(str(k) for k in expect["capture"])

        # WS 步骤的断言与 capture 都在 `do.ws` 里（`ws.expect` 收到的帧上取值，
        # 例如 `recognition_job.job_id` —— 回帧要用它当认领键）。
        ws = step.get("do", {}).get("ws") if isinstance(step.get("do"), dict) else None
        if isinstance(ws, dict):
            if isinstance(ws.get("capture"), dict):
                captured.update(str(k) for k in ws["capture"])
            inner = ws.get("expect")
            if isinstance(inner, dict) and isinstance(inner.get("capture"), dict):
                captured.update(str(k) for k in inner["capture"])

    return len(steps), errors, domain


def main(argv: list[str]) -> int:
    directory = Path(argv[1]).resolve() if len(argv) > 1 else DEFAULT_DIR
    print(f"[向量自检] 向量目录：{directory}")
    if not directory.is_dir():
        print(f"[向量自检] FAIL：目录不存在：{directory}")
        return 1

    files = sorted(directory.glob("*.ndjson"))
    if not files:
        print(f"[向量自检] FAIL：目录里没有 *.ndjson：{directory}")
        return 1

    all_errors: list[str] = []
    total_steps = 0
    by_domain: dict[str, list[str]] = {}
    for path in files:
        count, errors, domain = check_file(path)
        total_steps += count
        all_errors.extend(errors)
        by_domain.setdefault(domain, []).append(path.name)
        mark = "FAIL" if errors else "OK  "
        tail = f"，{len(errors)} 处问题" if errors else ""
        print(f"  {mark} {path.name}：{count} 步（域 {domain}）{tail}")

    if all_errors:
        print(f"\n[向量自检] FAIL：{len(files)} 个文件里共 {len(all_errors)} 处问题")
        for item in all_errors:
            print(f"  - {item}")
        return 1

    summary = "；".join(f"{domain} {len(names)} 组" for domain, names in sorted(by_domain.items()))
    print(
        f"\n[向量自检] OK：{len(files)} 个文件 / {total_steps} 步全部通过（{summary}）"
        "（① NDJSON 可解析 ② step 从 1 连续 ③ step 唯一 ④ 变量先 capture 后使用 ⑤ 域合法）"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
