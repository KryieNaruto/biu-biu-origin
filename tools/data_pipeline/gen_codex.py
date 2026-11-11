"""图鉴原型生成器：把 combat.xlsx + characters.xlsx 渲染成一张可浏览的 HTML 图鉴。

两个用途
--------
1. **快速数值预览** —— 策划改完表，跑一次就能看到全部数字，不用开引擎。
2. **真正图鉴的原型** —— 未来的游戏内图鉴要显示哪些字段、按什么分组，
   先在这里把信息层级定下来，再搬进 UI。

为什么是"按标签轴分栏"而不是按类型分栏
--------------------------------------
这个工程的物件全部是 `Objects` 表里的一行 + 一串标签（§17）。
所以图鉴最自然的组织方式就是**沿标签轴切分**：
   · 先按 Kind 轴（武器/子弹/怪物/人物）分大类
   · 再按 Element / Rarity / Role 轴做筛选与配色
这样加一个新轴就能加一种新视图，而不用改渲染逻辑。

用法
----
    python tools/data_pipeline/gen_codex.py [--out docs/codex/index.html]
"""
from __future__ import annotations

import argparse
import html
import sys
from pathlib import Path

from openpyxl import load_workbook

ROOT = Path(__file__).resolve().parents[2]
EXCEL = ROOT / "data" / "excel"
DEFAULT_OUT = ROOT / "docs" / "codex" / "index.html"

FIRST_DATA_ROW = 6

# 轴 → 配色（只影响观感，不参与任何玩法判定）
ELEMENT_COLOR = {
    "kinetic": "#8C8C99",
    "energy": "#5B9BD5",
    "explosive": "#D97A3F",
}
RARITY_COLOR = {
    "common": "#9AA0A6",
    "uncommon": "#5DD97A",
    "rare": "#5B9BD5",
    "legendary": "#D9A93F",
}


# ─────────────────────────────────────────────────────────────── 本地化

class L10n:
    """中文名查询。真源是 data/excel/l10n.xlsx（规则4：文案也走 Excel）。

    `DisplayKey` 列明文规定「不写中文」，所以中文名集中在一张表里，
    **图鉴和将来的游戏 UI 读同一份** —— 不会出现"图鉴叫飞弹、游戏里叫炮弹"。

    查不到时**原样返回 key** 并在生成时打一条警告，绝不静默变成空白
    （图鉴上出现一个英文 key 是最好的缺名提示）。
    """

    def __init__(self, path: Path) -> None:
        self.map: "dict[str, str]" = {}
        self.missing: "set[str]" = set()
        if not path.exists():
            print(f"[warn] 找不到 {path}；图鉴将显示英文 key", file=sys.stderr)
            return
        _, rows = read_rows(load_workbook(path)["L10n"])
        for r in rows:
            k, zh = r.get("Key"), r.get("Zh")
            if k and zh:
                self.map[str(k).strip()] = str(zh).strip()

    def __call__(self, key: object) -> str:
        """查中文名；查不到返回原 key 并记账。"""
        if key is None or key == "":
            return "—"
        k = str(key).strip()
        zh = self.map.get(k)
        if zh is None:
            self.missing.add(k)
            return k
        return zh

    def tag(self, name: str) -> str:
        """标签名：表里存的是 `tag.xxx`，调用方传进来的是去前缀的 `xxx`。"""
        return self("tag." + name)


# ─────────────────────────────────────────────────────────────── 读表

def read_rows(ws) -> "tuple[dict[str, int], list[dict[str, object]]]":
    """四行表头 + `#data` 标记行 + 从第 6 行开始的数据。"""
    hdr = {}
    for c in range(1, ws.max_column + 1):
        v = ws.cell(row=1, column=c).value
        if v:
            hdr[str(v)] = c
    rows = []
    for r in range(FIRST_DATA_ROW, ws.max_row + 1):
        vals = {name: ws.cell(row=r, column=col).value for name, col in hdr.items()}
        if any(v not in (None, "") for v in vals.values()):
            rows.append(vals)
    return hdr, rows


def tags_of(row: dict[str, object]) -> "list[str]":
    """把 'tag.a,tag.b' 拆成 ['a','b']（去掉 tag. 前缀，图鉴里不用看前缀）。"""
    raw = str(row.get("Tags") or "")
    return [t.strip()[4:] if t.strip().startswith("tag.") else t.strip()
            for t in raw.split(",") if t.strip()]


def tag_axes(tags: "list[str]") -> "dict[str, list[str]]":
    """沿已知的轴给标签分桶。未识别的标签落到 'other'，保证不漏显示。"""
    axes: "dict[str, list[str]]" = {
        "kind": [], "element": [], "rarity": [], "role": [], "ability": [], "other": []}
    for t in tags:
        if t in ("actor", "weapon", "projectile"):
            axes["kind"].append(t)
        elif t in ("pistol", "rifle", "bow", "melee"):
            axes["kind"].append(t)
        elif t in ("kinetic", "energy", "explosive"):
            axes["element"].append(t)
        elif t in ("common", "uncommon", "rare", "legendary"):
            axes["rarity"].append(t)
        elif t in ("hero", "grunt", "ranged", "caster", "elite", "assassin"):
            axes["role"].append(t)
        elif t in ("pierce", "bounce", "split", "summon", "chain", "ricochet",
                   "multi_shot", "charge", "high_damage", "homing",
                   "projectile_fired", "short_range", "volatile"):
            axes["ability"].append(t)
        elif t in ("humanoid", "goblin", "slime", "ogre"):
            axes["other"].append(t)
        else:
            axes["other"].append(t)
    return axes


def num(v) -> str:
    """数值单元格 → 展示串；None/空 → '—'。"""
    if v is None or v == "":
        return "—"
    if isinstance(v, float):
        return f"{v:g}"
    return html.escape(str(v))


def build_loadouts(skill_rows: "list[dict[str, object]]") -> "dict[str, str]":
    """LoadoutId → 左键槽的 WeaponId。图鉴要在人物卡片上显示"这人用什么枪"。"""
    out: "dict[str, str]" = {}
    for r in skill_rows:
        if str(r.get("Slot")) == "PrimaryAttack" and r.get("WeaponId"):
            out[str(r.get("LoadoutId"))] = str(r.get("WeaponId"))
    return out


# ─────────────────────────────────────────────────────────────── 渲染小组件

def chip(text: str, color: str | None = None, tag: str | None = None) -> str:
    style = f' style="--c:{color}"' if color else ""
    extra = f' data-tag="{html.escape(tag)}"' if tag else ""
    return f'<span class="chip"{style}{extra}>{html.escape(text)}</span>'


def tag_chips(axes: "dict[str, list[str]]", l10n: "L10n") -> str:
    """标签芯片。显示中文名，但 `data-tag` 保留英文标识 —— 搜索两种都能命中。"""
    out = []
    for t in axes["element"]:
        out.append(chip(l10n.tag(t), ELEMENT_COLOR.get(t), tag=t))
    for t in axes["rarity"]:
        out.append(chip(l10n.tag(t), RARITY_COLOR.get(t), tag=t))
    for t in axes["role"] + axes["ability"] + axes["other"]:
        out.append(chip(l10n.tag(t), tag=t))
    return "".join(out)


def stat(label: str, value) -> str:
    return (f'<div class="stat"><span class="k">{html.escape(label)}</span>'
            f'<span class="v">{num(value)}</span></div>')


def card(title: str, title_en: str, subtitle: str, chips: str,
         stats: "list[tuple[str, object]]", note: str = "",
         accent: str | None = None) -> str:
    """一张图鉴卡。`title` 中文名（大字号），`title_en` 英文 id（小字，便于对表）。

    `accent` 决定卡片左侧色条：有稀有度用稀有度色，否则用元素色，再否则不染色。
    """
    body = "".join(stat(k, v) for k, v in stats)
    note_html = f'<p class="note">{html.escape(note)}</p>' if note else ""
    search = html.escape(f"{title} {title_en} {subtitle}".lower())
    style = f' style="--c:{accent}"' if accent else ""
    return f'''<article class="card" data-search="{search}" data-chips="{html.escape(chips)}"{style}>
  <header><h3>{html.escape(title)}</h3><code class="id">{html.escape(title_en)}</code></header>
  <div class="sub">{html.escape(subtitle)}</div>
  <div class="chips">{chips}</div>
  <div class="stats">{body}</div>
  {note_html}
</article>'''


def accent_of(axes: "dict[str, list[str]]") -> str | None:
    """卡片左侧色条：稀有度优先，其次元素。"""
    for t in axes["rarity"]:
        if t in RARITY_COLOR:
            return RARITY_COLOR[t]
    for t in axes["element"]:
        if t in ELEMENT_COLOR:
            return ELEMENT_COLOR[t]
    return None


# ─────────────────────────────────────────────────────────────── 主流程

def build(out_path: Path) -> int:
    combat = load_workbook(EXCEL / "combat.xlsx")
    chars = load_workbook(EXCEL / "characters.xlsx")
    l10n = L10n(EXCEL / "l10n.xlsx")

    _, objects = read_rows(combat["Objects"])
    _, weapons = read_rows(combat["Weapons"])
    _, projectiles = read_rows(combat["Projectiles"])
    _, chars_rows = read_rows(chars["Characters"])
    _, sp_rows = read_rows(chars["StatPacks"])
    _, hero_weapons = read_rows(chars["Weapons"])
    _, skill_rows = read_rows(chars["SkillLoads"])

    obj_by_id = {str(o["Id"]): o for o in objects if o.get("Id")}
    weapon_by_obj = {str(w["ObjectId"]): w for w in weapons if w.get("ObjectId")}
    proj_by_obj = {str(p["ObjectId"]): p for p in projectiles if p.get("ObjectId")}
    hw_by_id = {str(w["Id"]): w for w in hero_weapons if w.get("Id")}
    loadouts = build_loadouts(skill_rows)

    packs: "dict[str, dict[str, object]]" = {}
    for r in sp_rows:
        p = str(r.get("PackId") or "")
        if p and str(r.get("Layer")) == "Base":
            packs.setdefault(p, {})[str(r.get("StatId"))] = r.get("Value")

    def obj_cards(kinds: "tuple[str, ...]") -> str:
        out = []
        for obj_id, o in obj_by_id.items():
            tg = tags_of(o)
            if not any(k in tg for k in kinds):
                continue
            axes = tag_axes(tg)
            display = l10n(o.get("DisplayKey"))
            stats: "list[tuple[str, object]]" = [("半径", o.get("Radius"))]
            w = weapon_by_obj.get(obj_id)
            if w:
                stats += [("伤害", w.get("BaseDamage")), ("射速间隔", w.get("FireInterval")),
                          ("弹速", w.get("ProjectileSpeed")),
                          ("触发", l10n(w.get("Trigger"))),
                          ("投送", l10n(w.get("Delivery"))), ("射程", w.get("Range"))]
            p = proj_by_obj.get(obj_id)
            if p:
                stats += [("伤害", p.get("Damage")), ("速度", p.get("Speed")),
                          ("存活", p.get("Lifetime")), ("穿透", p.get("Pierce")),
                          ("弹跳", p.get("Bounce")), ("运动", l10n(p.get("Motion")))]
            out.append(card(display, obj_id, str(o.get("DisplayKey") or ""),
                            tag_chips(axes, l10n), stats, str(o.get("Note") or ""),
                            accent_of(axes)))
        return "\n".join(out)

    # 人物：Kind=Player
    hero_cards = []
    for c in chars_rows:
        if str(c.get("Kind")) != "Player":
            continue
        pack = packs.get(str(c.get("BasePackage")), {})
        # 冲刺：没配 DashDuration 按代码语义就是「冲不了」，图鉴明确标红而不是显示 —，否则像缺数据
        dash_mul, dash_dur = pack.get("DashSpeedMul"), pack.get("DashDuration")
        if dash_dur in (None, ""):
            dash_txt = "未配置（不能冲刺）"
        else:
            dash_txt = f'{num(dash_mul)}× / {num(dash_dur)} 秒'
        stats = [
            ("生命", pack.get("Hp")), ("法力", pack.get("Mana")),
            ("护甲", pack.get("Armor")), ("移速", pack.get("MoveSpeed")),
            ("暴击率", pack.get("CritChance")), ("暴击倍率", pack.get("CritMultiplier")),
            ("幸运", pack.get("Luck")),
            ("冲刺", dash_txt),
        ]
        # 这个人物的左键武器：从 SkillLoads 里找 PrimaryAttack 槽的 WeaponId
        kit_id = str(c.get("SkillLoadout") or "")
        weapon_id = loadouts.get(kit_id)
        if weapon_id and weapon_id in hw_by_id:
            w = hw_by_id[weapon_id]
            stats += [("武器", l10n(weapon_id)), ("武器伤害", w.get("Damage")),
                      ("武器冷却", w.get("ShotCooldown"))]
        chips = chip(l10n(c.get("Kind")), "#5DD97A")
        hero_cards.append(card(l10n(c.get("DisplayKey")), str(c["Id"]),
                               str(c.get("SkillLoadout") or ""), chips, stats))
    hero_html = "\n".join(hero_cards)

    monster_cards = []
    for c in chars_rows:
        if str(c.get("Kind")) not in ("Monster", "Elite", "Boss"):
            continue
        pack = packs.get(str(c.get("BasePackage")), {})
        kind_zh = l10n(c.get("Kind"))
        monster_cards.append(card(
            l10n(c.get("DisplayKey")), str(c["Id"]),
            f'{kind_zh} · {l10n(c.get("DefaultTeam"))}',
            chip(kind_zh, "#D97A3F"),
            [("生命", pack.get("Hp")), ("护甲", pack.get("Armor")),
             ("移速", pack.get("MoveSpeed")), ("力量", pack.get("Strength")),
             ("受击无敌", pack.get("HurtInvulnTime")),
             ("受击半径", c.get("HitRadius"))]))
    monster_html = "\n".join(monster_cards)

    hw_cards = []
    for hid, hw in hw_by_id.items():
        hw_cards.append(card(l10n(hid), hid, f"武器数值 {hw.get('WeaponId')}",
                             chip(l10n.tag("weapon"), "#5B9BD5"), [
            ("伤害", hw.get("Damage")), ("弹速", hw.get("BulletSpeed")),
            ("射程", hw.get("RangeMeters")), ("散布°", hw.get("SpreadDegrees")),
            ("暴击率", hw.get("CriticalChance")), ("暴击倍率", hw.get("CriticalMultiplier")),
            ("冷却", hw.get("ShotCooldown")), ("弹丸数", hw.get("PelletsPerShot"))]))
    hw_html = "\n".join(hw_cards)

    prop_cards = []
    for pid, p in proj_by_obj.items():
        o = obj_by_id.get(pid, {})
        prop_cards.append(card(
            l10n(o.get("DisplayKey")), pid, l10n(p.get("Motion")),
            tag_chips(tag_axes(tags_of(o)), l10n), [
                ("命中策略", l10n(p.get("HitPolicy"))), ("速度", p.get("Speed")),
                ("伤害", p.get("Damage")), ("存活", p.get("Lifetime")),
                ("穿透", p.get("Pierce")), ("弹跳", p.get("Bounce")),
                ("重力", p.get("Gravity")), ("追踪转向", p.get("HomingTurn"))],
            accent=accent_of(tag_axes(tags_of(o)))))
    prop_html = "\n".join(prop_cards)

    total = len(obj_by_id) + len(chars_rows) + len(hw_by_id) + len(prop_cards)
    page = TEMPLATE.format(
        heroes=hero_html, monsters=monster_html,
        hero_weapons=hw_html, projcards=prop_html,
        count=total, n_hero=len(hero_cards), n_mon=len(monster_cards),
        n_hw=len(hw_by_id), n_prop=len(prop_cards))

    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_text(page, encoding="utf-8")
    print(f"[ok] 图鉴已生成：{out_path}")
    print(f"     人物 {len(hero_cards)} · 怪物 {len(monster_cards)} · "
          f"武器数值 {len(hw_by_id)} · 子弹 {len(prop_cards)}")
    print(f"     本地化条目 {len(l10n.map)} 条")
    if l10n.missing:
        miss = sorted(l10n.missing)
        print(f"[warn] {len(miss)} 个 key 缺中文名（已原样显示英文）：", file=sys.stderr)
        for m in miss:
            print(f"         - {m}", file=sys.stderr)
    return 0


TEMPLATE = """<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<title>BiuBiuOrigin · 图鉴原型</title>
<style>
  :root {{
    --bg:#11131a; --panel:#1a1d27; --line:#2a2f3d; --fg:#e6e8ee;
    --dim:#8b93a7; --accent:#5B9BD5;
  }}
  * {{ box-sizing:border-box; }}
  body {{
    margin:0; background:var(--bg); color:var(--fg);
    font:14px/1.6 "Segoe UI","Microsoft YaHei",sans-serif;
  }}
  header.top {{
    position:sticky; top:0; z-index:10; background:rgba(17,19,26,.95);
    backdrop-filter:blur(8px); border-bottom:1px solid var(--line);
    padding:14px 24px; display:flex; align-items:center; gap:16px; flex-wrap:wrap;
  }}
  header.top h1 {{ font-size:16px; margin:0; font-weight:600; }}
  header.top .meta {{ color:var(--dim); font-size:12px; }}
  input#q {{
    margin-left:auto; background:var(--panel); border:1px solid var(--line);
    color:var(--fg); padding:7px 12px; border-radius:8px; width:240px;
    font-size:13px; outline:none;
  }}
  input#q:focus {{ border-color:var(--accent); }}
  main {{ padding:24px; max-width:1500px; margin:0 auto; }}
  section {{ margin-bottom:38px; }}
  section > h2 {{
    font-size:14px; text-transform:uppercase; letter-spacing:.12em;
    color:var(--dim); border-bottom:1px solid var(--line);
    padding-bottom:8px; margin:0 0 16px; font-weight:600;
  }}
  section > h2 .cnt {{ color:var(--accent); margin-left:8px; }}
  .grid {{ display:grid; grid-template-columns:repeat(auto-fill,minmax(280px,1fr)); gap:14px; }}
  .card {{
    background:var(--panel); border:1px solid var(--line); border-radius:12px;
    padding:14px 16px; transition:border-color .15s, transform .15s;
  }}
  .card:hover {{ border-color:var(--accent); transform:translateY(-2px); }}
  .card header {{ display:flex; align-items:baseline; gap:8px; flex-wrap:wrap; }}
  .card h3 {{ margin:0; font-size:15px; font-weight:600; }}
  /* 英文 id 是给策划对表用的副标题，必须小、灰、等宽 —— 不能和中文名抢视觉 */
  .card .id {{
    color:var(--dim); font-size:10.5px; opacity:.75;
    font-family:ui-monospace,Consolas,monospace; letter-spacing:.02em;
  }}
  .card .sub {{ color:var(--dim); font-size:11px; }}
  /* 中文名左侧的色条 = 稀有度/阵营的快速识别，卡片悬停时提亮 */
  .card {{ border-left:3px solid var(--c,var(--line)); }}
  .chips {{ margin:8px 0 10px; display:flex; gap:5px; flex-wrap:wrap; }}
  .chip {{
    font-size:11px; padding:2px 8px; border-radius:99px;
    border:1px solid var(--c,#3a4053); color:var(--c,#aab1c4);
    background:color-mix(in srgb, var(--c,#3a4053) 12%, transparent);
  }}
  .stats {{ display:grid; grid-template-columns:1fr 1fr; gap:3px 14px; }}
  .stat {{ display:flex; justify-content:space-between; gap:8px; font-size:12.5px; }}
  .stat .k {{ color:var(--dim); }}
  .stat .v {{ font-variant-numeric:tabular-nums; font-weight:600; }}
  .note {{ margin:10px 0 0; font-size:11.5px; color:var(--dim); border-top:1px dashed var(--line); padding-top:8px; }}
  .hidden {{ display:none !important; }}
  footer {{ color:var(--dim); font-size:12px; text-align:center; padding:30px 0 60px; }}
</style>
</head>
<body>
<header class="top">
  <h1>BiuBiuOrigin 图鉴原型</h1>
  <span class="meta">共 {count} 条 · 名字读 l10n.xlsx · 数值直读 Excel</span>
  <input id="q" placeholder="搜索中文名 / 英文 id / 标签…">
</header>
<main>
  <section id="sec-heroes"><h2>人物 <span class="cnt">{n_hero}</span></h2><div class="grid">
  {heroes}
  </div></section>

  <section id="sec-monsters"><h2>怪物 <span class="cnt">{n_mon}</span></h2><div class="grid">
  {monsters}
  </div></section>

  <section id="sec-heroweapons"><h2>人物武器 <span class="cnt">{n_hw}</span></h2><div class="grid">
  {hero_weapons}
  </div></section>

  <section id="sec-proj"><h2>子弹 <span class="cnt">{n_prop}</span></h2><div class="grid">
  {projcards}
  </div></section>

  <footer>由 tools/data_pipeline/gen_codex.py 生成 · 名字改 data/excel/l10n.xlsx · 数值改 combat.xlsx / characters.xlsx</footer>
</main>
<script>
const q = document.getElementById('q');
q.addEventListener('input', () => {{
  const s = q.value.trim().toLowerCase();
  document.querySelectorAll('.card').forEach(c => {{
    const hay = (c.dataset.search || '') + ' ' + (c.dataset.chips || '');
    c.classList.toggle('hidden', s !== '' && !hay.includes(s));
  }});
  document.querySelectorAll('section').forEach(sec => {{
    const any = [...sec.querySelectorAll('.card')].some(c => !c.classList.contains('hidden'));
    sec.classList.toggle('hidden', !any);
  }});
}});
</script>
</body>
</html>
"""


def main(argv: "list[str]") -> int:
    ap = argparse.ArgumentParser(description="生成 HTML 图鉴原型")
    ap.add_argument("--out", type=Path, default=DEFAULT_OUT)
    args = ap.parse_args(argv)
    return build(args.out)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
