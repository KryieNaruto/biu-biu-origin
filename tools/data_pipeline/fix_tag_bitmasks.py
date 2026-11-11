"""一次性修复：把 Tags 表里被「重跑生成器」冲掉的 BitMask 补回来（2026-11-06）。

背景：`gen_combat_xlsx.py` 里 TAGS 的 BitMask 列曾被写成 -1（只有 volatile 才是 -1），
有人在 hero-1 会话里重跑生成器覆盖了 combat.xlsx，于是 0..15 的配方位全部丢失，
Weapons / Projectiles 的 Recipe 全部变成「引用没有配方位的 tag」。

本脚本只做一件事：按 `gen_combat_xlsx.TAGS`（生成器的内存数据，已是正确值）把 Tags 表的
`BitMask` 列对齐回去 —— 只写 BitMask 一格，其余列（Family / Category / Group / Note）一律不碰，
并把 Note 为空的行补上。
"""
from __future__ import annotations

import sys
from pathlib import Path

from openpyxl import load_workbook

sys.path.insert(0, str(Path(__file__).resolve().parent))
import gen_combat_xlsx as gen  # noqa: E402

ROOT = Path(__file__).resolve().parents[2] / "data" / "excel"
COMBAT = ROOT / "combat.xlsx"
FIRST_DATA_ROW = 6


def main() -> int:
    wb = load_workbook(COMBAT)
    ws = wb["Tags"]
    want = {row[0]: row[5] for row in gen.TAGS}      # TagId → BitMask
    note_by_id = {row[0]: row[6] for row in gen.TAGS}

    changed = 0
    seen = set()
    for r in range(FIRST_DATA_ROW, ws.max_row + 1):
        tag_id = ws.cell(row=r, column=1).value
        if not tag_id:
            continue
        tag_id = str(tag_id).strip()
        seen.add(tag_id)
        cell = ws.cell(row=r, column=6)              # F 列 = BitMask
        if tag_id not in want:
            print(f"[错误] Tags 第 {r} 行「{tag_id}」不在 gen_combat_xlsx.TAGS 里（表里有生成器没登记的行）")
            return 1
        if cell.value != want[tag_id]:
            cell.value = want[tag_id]
            changed += 1
        if not ws.cell(row=r, column=7).value:
            ws.cell(row=r, column=7, value=note_by_id[tag_id])

    missing = [t for t in want if t not in seen]
    if missing:
        print(f"[错误] 表里缺少这些 tag：{missing}")
        return 1

    wb.save(COMBAT)
    print(f"[ok] Tags: 修正 {changed} 个 BitMask，写回 {COMBAT}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
