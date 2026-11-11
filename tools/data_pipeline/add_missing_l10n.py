"""把人物武器 id 与遗漏的枚举补进 l10n.xlsx（幂等，可重复跑）。

第一轮生成时我只覆盖了 `Objects.DisplayKey` 的前缀空间（actor./item./proj./fx.）和 `Tags.TagId`，
**漏了两类**：

  1. `characters.xlsx/Weapons.Id` —— 人物左键武器的**装备 id**（`pistol_basic` 等），
     它不是 DisplayKey，所以前缀猜不出来，第一轮直接漏了；
  2. 枚举值 `Homing`（在 `ProjectileMotion` 的兄弟枚举里出现过，但我只登记了 Straight 等）。

这正是 `gen_codex.py` 里 `l10n.missing` 记账机制存在的意义：**漏了会在生成时报错列出来**，
而不是在图鉴上静默显示成一个英文单词。以后加内容时，这个警告就是检查清单。
"""
from __future__ import annotations

import sys
from pathlib import Path

from openpyxl import load_workbook

ROOT = Path(__file__).resolve().parents[2]
PATH = ROOT / "data" / "excel" / "l10n.xlsx"

# 人物武器 id：中文名对齐 combat.xlsx 里对应的物件名（手枪弹/重炮弹/冲锋枪弹/幸运弹/元素球）
ADD = [
    # 键                        中文名            说明
    ("pistol_basic",           "制式手枪",        "人物左键武器 id（≠ 物件 id pistol_101）"),
    ("cannon_heavy",           "重炮弹武器",      "重甲变体的左键"),
    ("smg_light",              "冲锋枪武器",      "敏捷变体的左键"),
    ("ricochet_lucky",         "幸运弹射武器",    "幸运变体的左键"),
    ("orb_elemental",          "元素法球武器",    "元素变体的左键"),
    ("Homing",                 "追踪",            "枚举值（若表里出现）"),
]

FIRST_DATA_ROW = 6


def main() -> int:
    wb = load_workbook(PATH)
    ws = wb["L10n"]

    hdr = {}
    for c in range(1, ws.max_column + 1):
        v = ws.cell(row=1, column=c).value
        if v:
            hdr[str(v)] = c

    existing = {}
    for r in range(FIRST_DATA_ROW, ws.max_row + 1):
        k = ws.cell(row=r, column=hdr["Key"]).value
        if k:
            existing[str(k)] = r

    added, updated = 0, 0
    for key, zh, note in ADD:
        if key in existing:
            r = existing[key]
            ws.cell(row=r, column=hdr["Zh"], value=zh)
            if note:
                ws.cell(row=r, column=hdr["Note"], value=note)
            updated += 1
        else:
            r = ws.max_row + 1
            ws.cell(row=r, column=hdr["Key"], value=key)
            ws.cell(row=r, column=hdr["Zh"], value=zh)
            if note:
                ws.cell(row=r, column=hdr["Note"], value=note)
            added += 1

    wb.save(PATH)
    print(f"[ok] {PATH}")
    print(f"     新增 {added} 条 · 更新 {updated} 条 · 现有 {len(existing) + added} 条")
    return 0


if __name__ == "__main__":
    sys.exit(main())
