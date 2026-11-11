"""只读探针：检查「人物→技能→武器→子弹」「怪物→技能→武器→子弹」这条链当前是否完整接通。

不做任何修改，只打印事实。用来回答「现在有没有一套最基础的测试组件」。
"""
from __future__ import annotations

import pathlib

from openpyxl import load_workbook

ROOT = pathlib.Path(__file__).resolve().parents[2]
CHARS = ROOT / "data" / "excel" / "characters.xlsx"
COMBAT = ROOT / "data" / "excel" / "combat.xlsx"

FIRST = 6


def rows(wb, name):
    ws = wb[name]
    hdr = {}
    for c in range(1, ws.max_column + 1):
        v = ws.cell(row=1, column=c).value
        if v:
            hdr[str(v)] = c
    out = []
    for r in range(FIRST, ws.max_row + 1):
        d = {k: ws.cell(row=r, column=c).value for k, c in hdr.items()}
        if any(v not in (None, "") for v in d.values()):
            out.append(d)
    return out


def main() -> int:
    cw = load_workbook(CHARS)
    bw = load_workbook(COMBAT)

    chars = rows(cw, "Characters")
    loads = rows(cw, "SkillLoads")
    hweps = rows(cw, "Weapons")
    objs = rows(bw, "Objects")
    projs = rows(bw, "Projectiles")
    cweapons = rows(bw, "Weapons")

    # 索引
    hw_by_id = {str(w.get("Id")): w for w in hweps if w.get("Id")}
    obj_by_id = {str(o.get("Id")): o for o in objs if o.get("Id")}
    cw_by_obj = {str(w.get("ObjectId")): w for w in cweapons if w.get("ObjectId")}
    pj_by_obj = {str(p.get("ObjectId")): p for p in projs if p.get("ObjectId")}

    kit = {}
    for d in loads:
        kit.setdefault(str(d.get("LoadoutId")), {})[str(d.get("Slot"))] = d

    print("=" * 78)
    print("人物 / 怪物 → 技能包 → 左键武器 → 子弹  链路")
    print("=" * 78)
    for c in chars:
        cid = str(c.get("Id"))
        kind = str(c.get("Kind"))
        kit_id = str(c.get("SkillLoadout") or "")
        slots = kit.get(kit_id, {})
        primary = slots.get("PrimaryAttack", {})
        wid = primary.get("WeaponId")
        has_skill = primary.get("SkillId")
        w = hw_by_id.get(str(wid)) if wid else None
        # 人物武器表 → 有没有指向子弹物件
        bullet = ""
        if w:
            cand = str(w.get("BulletObjectId") or w.get("ProjectileObjectId") or w.get("BulletId") or "")
            bullet = cand if cand else "（未指定子弹列）"
        print(f"\n{cid:<22} [{kind}]  kit={kit_id or '(空)'}")
        print(f"   槽位数={len(slots):<2}  PrimaryAttack: skill={has_skill or '无'}  weapon={wid or '无'}")
        if w:
            print(f"   武器数值行: {wid}  ObjectId={w.get('ObjectId')}")
        elif kind == "Player":
            print("   ⚠ 人物没有左键武器！")
        else:
            print("   ⚠ 怪物没有左键武器（PrimaryAttack.WeaponId 为空）")

    print()
    print("=" * 78)
    print("combat.xlsx 的武器 → 子弹 链")
    print("=" * 78)
    for w in cweapons:
        oid = str(w.get("ObjectId"))
        bullets = [str(i) for i, o in obj_by_id.items() if str(o.get("Emitter") or "") == oid
                   or str(o.get("Id")) == str(w.get("ProjectileObjectId") or "")]
        print(f"  {oid:<18} 发射物候选={bullets or '（无 Emitter 指向）'}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
