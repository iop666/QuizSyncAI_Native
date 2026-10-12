#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""数据真伪体检：统计库里有多少条是**真实识别结果**、多少条是**桩/联调数据**。

**为什么需要这个工具**（这不是洁癖，是一次真实的教训）：
第 51 轮的 Phase 5 取证引用了「真主机库里有 16 条识别记录」，措辞上把整库当成了用户数据；
第 83 轮做体检才发现 —— **应用库 17 题里真 2 桩 15、主机库 17 题里真 1 桩 16**。
若不区分，后面的人会把「桩服务端返回的题」当成用户真实使用数据来论证功能可用性，
这正是"污染证据链"最典型的方式。

判据（**保守**：宁可漏判桩，也不把真题误判成桩）：
* 题干 / 解析 / `raw_response` 里出现 `桩`，或 `raw_response` 里出现 `fixture`（大小写不敏感）→ **桩**；
* 其余 → **真**。

用法：
    python tools/audit_data_authenticity.py                    # 查默认的两个库
    python tools/audit_data_authenticity.py --db <路径> [...]   # 查指定库
    python tools/audit_data_authenticity.py --json             # 机器可读

默认路径（Windows）：
    应用库  %LOCALAPPDATA%\\QuizSyncAI\\app\\quizsync.db
    主机库  %LOCALAPPDATA%\\QuizSyncAI\\server\\quizsync.db
（这两个目录由 `QS_APP_DATA` / 服务端 `--data` 决定；本机是默认值。）
"""

from __future__ import annotations

import argparse
import json
import os
import sqlite3
import sys
from pathlib import Path

# ⚠️ 本机控制台是 **GBK**：直接 print emoji 会 `UnicodeEncodeError` 把脚本打死
#    （实测 `⚠️` 崩在最后一行）。统一把 stdout 切到 UTF-8 并允许替换，
#    这样中文正常、编不出的字符也不会中断流程。
try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except (AttributeError, ValueError):
    pass

STUB_MARK = "桩"


def default_databases() -> list[Path]:
    local = os.environ.get("LOCALAPPDATA") or str(Path.home() / "AppData" / "Local")
    base = Path(local) / "QuizSyncAI"
    return [base / "app" / "quizsync.db", base / "server" / "quizsync.db"]


def classify(stem: str, analysis: str, raw: str) -> str:
    blob = f"{stem or ''}{analysis or ''}{raw or ''}"
    if STUB_MARK in blob or "fixture" in blob.lower():
        return "stub"
    return "real"


def inspect(db: Path) -> dict:
    result: dict = {"database": str(db), "exists": db.exists()}
    if not db.exists():
        return result

    # **只读**打开：绝不能因为体检而写用户的库（也避免拿到写锁）。
    connection = sqlite3.connect(f"file:{db}?mode=ro", uri=True)
    connection.row_factory = sqlite3.Row
    try:
        result["sessions"] = connection.execute("select count(*) from sessions").fetchone()[0]
        questions = connection.execute(
            "select question_id, session_id, coalesce(stem,'') as stem, coalesce(analysis,'') as analysis "
            "from questions where deleted_at is null",
        ).fetchall()
        result["questions"] = len(questions)

        raw_by_session: dict[str, str] = {}
        try:
            for row in connection.execute(
                "select session_id, coalesce(raw_response,'') as raw from sessions",
            ):
                raw_by_session[row["session_id"]] = row["raw"]
        except sqlite3.Error:
            result["raw_response"] = "不可读（该库没有这一列）"

        kinds = {"real": 0, "stub": 0}
        real_questions: list[str] = []
        for question in questions:
            kind = classify(
                question["stem"],
                question["analysis"],
                raw_by_session.get(question["session_id"], ""),
            )
            kinds[kind] += 1
            if kind == "real":
                real_questions.append((question["stem"] or "")[:60])
        result["real"] = kinds["real"]
        result["stub"] = kinds["stub"]
        result["real_samples"] = real_questions[:5]

        try:
            result["by_source_device"] = {
                row["source_device"]: row["n"]
                for row in connection.execute(
                    "select source_device, count(*) as n from sessions group by source_device order by n desc",
                )
            }
        except sqlite3.Error:
            result["by_source_device"] = {}
    finally:
        connection.close()
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description="统计库里的真实/桩数据条数（只读）")
    parser.add_argument("--db", action="append", default=[], help="要检查的库路径（可多次）")
    parser.add_argument("--json", action="store_true", help="输出 JSON")
    args = parser.parse_args()

    databases = [Path(p) for p in args.db] if args.db else default_databases()
    reports = [inspect(db) for db in databases]

    if args.json:
        print(json.dumps(reports, ensure_ascii=False, indent=2))
        return 0

    for report in reports:
        print(f"\n=== {report['database']} ===")
        if not report["exists"]:
            print("  库不存在")
            continue
        print(f"  会话 {report['sessions']} 条，题目 {report['questions']} 条")
        print(f"  分类：真 {report['real']} 条 / 桩 {report['stub']} 条")
        if report.get("raw_response"):
            print(f"  raw_response：{report['raw_response']}")
        for device, count in (report.get("by_source_device") or {}).items():
            print(f"  来源：{device} → {count} 条")
        if report["real"]:
            print("  真题样本（前 60 字）：")
            for sample in report["real_samples"]:
                print(f"    {sample}")

    total_real = sum(r.get("real", 0) for r in reports)
    total_stub = sum(r.get("stub", 0) for r in reports)
    print(f"\n合计：真 {total_real} 条 / 桩 {total_stub} 条")
    if total_real and total_stub > total_real:
        # 不用 emoji：GBK 控制台编不出来（见文件头注释）。
        print("[!] 以桩为主 —— 任何「用真实数据验证过」的说法都必须限定条数，别把整库当成用户数据。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
