"""生成 docs/artifacts/decor_contact_sheet.png：把装饰目录里每个 glb 的**真实投影轮廓**画成一张小样图。

为什么用「工程图」而不是渲染图：headless 探针没有可依赖的光照/着色（GDExtension 库缺失、Forward+ 在
`--headless` 下不初始化渲染服务），而这一页的目的是**快速核对「模型长什么样、量到的尺寸对不对」**。
所以这里用引擎实测（`tmp/decor_catalog/godot_measured.json`）与 GLB 直读（`build_catalog`）两种来源画：

  * 俯视（顶视）真实投影轮廓 x/z + 摆一圈同比例人物的头顶圆圈（半径 0.3 逻辑米）做尺度参照；
  * 立面（正视）高度 y + 一条 2 米「真人肩线」参考线；
  * 底栏写 id / 名称 / 层 / 逻辑尺寸。

依赖：Pillow（系统 python 没有 → 用 DSH 自带 python，见 docs/rules 环境说明）。

    & "C:\\Users\\Administrator\\.dsh\\dsh-runtimes\\dsh-primary-runtime\\dependencies\\python\\python.exe" `
        tools/decor_catalog/render_contact_sheet.py
"""

from __future__ import annotations

import json
import math
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
PROJECT_ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
sys.path.insert(0, HERE)

import glb_measure  # noqa: E402
import rules as R  # noqa: E402

CATALOG = os.path.join(PROJECT_ROOT, "config", "decor_catalog.json")
OUT = os.path.join(PROJECT_ROOT, "docs", "artifacts", "decor_contact_sheet.png")

CELL_W, CELL_H = 176, 196
COLS = 10
MARGIN = 14
HEADER_H = 74
FOOTER_H = 0
PAD = 6

BG = (24, 26, 31)
CARD = (36, 39, 46)
CARD_EDGE = (62, 67, 78)
TOP_VIEW_BG = (30, 44, 58)
SIDE_VIEW_BG = (44, 38, 32)
INK = (232, 234, 240)
DIM = (150, 156, 170)
ACCENT = (255, 176, 84)
REF = (120, 200, 255)
HUMAN = (255, 230, 120)

LAYER_COLOR = {"Ground": (110, 200, 130), "Wall": (110, 160, 240), "Ceiling": (200, 140, 240)}


def _pick_font(size):
    from PIL import ImageFont
    for cand in [
        r"C:\Windows\Fonts\msyh.ttc",
        r"C:\Windows\Fonts\msyhbd.ttc",
        r"C:\Windows\Fonts\simhei.ttf",
        r"C:\Windows\Fonts\segoeui.ttf",
    ]:
        if os.path.exists(cand):
            try:
                return ImageFont.truetype(cand, size)
            except Exception:
                continue
    return ImageFont.load_default()


def _project_points(doc, prim_boxes, frame):
    """把每个 primitive 的局部盒角点经 node TRS 变换后，取其 XZ/Y 投影（用于真实轮廓多边形）。

    返回 [(xs, zs, ys)] —— 每个 primitive 一组的投影点云。为了画出「梯形块」的观感，
    这里只取 4 个底角 + 4 个顶角，投影就是把 8 角点丢掉一个轴。
    """
    nodes = doc.get("nodes", [])
    meshes = doc.get("meshes", [])
    accessors = doc.get("accessors", [])
    scenes = doc.get("scenes", [])
    scene_index = doc.get("scene", 0)
    roots = scenes[scene_index].get("nodes", []) if 0 <= scene_index < len(scenes) else []
    out = []
    visited = set()

    def local_box(mesh):
        box = None
        for prim in mesh.get("primitives", []):
            pos = prim.get("attributes", {}).get("POSITION")
            if pos is None or pos >= len(accessors):
                continue
            acc = accessors[pos]
            if "min" not in acc:
                continue
            lo, hi = acc["min"], acc["max"]
            if box is None:
                box = [list(lo), list(hi)]
            else:
                for i in range(3):
                    box[0][i] = min(box[0][i], lo[i])
                    box[1][i] = max(box[1][i], hi[i])
        return box

    def walk(idx, t=(0, 0, 0), r=None, s=(1, 1, 1)):
        if idx in visited or idx >= len(nodes):
            return
        visited.add(idx)
        node = nodes[idx]
        nt = node.get("translation", t)
        nr = node.get("rotation", r)
        ns = node.get("scale", s)
        mi = node.get("mesh")
        if mi is not None and mi < len(meshes):
            box = local_box(meshes[mi])
            if box is not None:
                rot = glb_measure._quat_matrix(nr)
                pts = []
                for cx in (box[0][0], box[1][0]):
                    for cy in (box[0][1], box[1][1]):
                        for cz in (box[0][2], box[1][2]):
                            pts.append(glb_measure._apply_trs((cx, cy, cz), nt, rot, ns))
                out.append(pts)
        for child in node.get("children", []):
            walk(child, nt, nr, ns)

    for root in roots:
        walk(root)
    return out


def main():
    from PIL import Image, ImageDraw

    with open(CATALOG, encoding="utf-8-sig") as f:
        cat = json.load(f)
    items = cat["items"]
    engine_path = os.path.join(PROJECT_ROOT, "tmp", "decor_catalog", "godot_measured.json")
    engine = {}
    if os.path.exists(engine_path):
        with open(engine_path, encoding="utf-8") as f:
            for row in json.load(f).get("items", []):
                if row.get("engineSize"):
                    engine[int(row["id"])] = row["engineSize"]
    lib = cat.get("assetLibraryScale", 1.0)
    target = cat.get("targetMaxHorizontalMeters", 6.0)
    human_m = 2.0  # 参考身高（逻辑米）
    human_r = 0.3  # 参考人物肩宽半径（逻辑米）

    rows = math.ceil(len(items) / COLS)
    W = MARGIN * 2 + COLS * CELL_W
    H = HEADER_H + rows * CELL_H + MARGIN
    img = Image.new("RGB", (W, H), BG)
    d = ImageDraw.Draw(img)
    f_title = _pick_font(22)
    f_sub = _pick_font(13)
    f_name = _pick_font(12)
    f_tiny = _pick_font(11)

    d.text((MARGIN, 12),
           "decor_catalog 小样：%d 条装饰（俯视真实投影 + 立面高度），每格俯视图里白色虚线圆 = %.2f 米半径人体参照"
           % (len(items), human_r),
           font=f_title, fill=INK)
    d.text((MARGIN, 42),
           "来源：config/decor_catalog.json ｜ 尺寸 = 引擎实测 AABB（原始 glb 单位）× 库缩放 %.2f ｜ 目标：最大水平边 → %.0f 米"
           % (lib, target),
           font=f_sub, fill=DIM)

    asset_root = os.path.join(PROJECT_ROOT, "素材")
    for i, it in enumerate(items):
        cx = MARGIN + (i % COLS) * CELL_W
        cy = HEADER_H + (i // COLS) * CELL_H
        card = [cx + 2, cy + 2, cx + CELL_W - 2, cy + CELL_H - 2]
        d.rounded_rectangle(card, radius=8, fill=CARD, outline=CARD_EDGE)
        folder, filename = it["sourceFolder"], it["sourceFile"]
        size_raw = engine.get(it["id"], it["sizeRaw"])
        size_m = [v * lib for v in size_raw]
        horiz = max(size_m[0], size_m[2]) or 1.0

        # 俯视窗
        top = [card[0] + PAD, card[1] + PAD, card[2] - PAD, card[1] + PAD + 70]
        d.rectangle(top, fill=TOP_VIEW_BG)
        # 立面窗
        side = [card[0] + PAD, top[3] + 4, card[2] - PAD, top[3] + 4 + 56]
        d.rectangle(side, fill=SIDE_VIEW_BG)

        try:
            doc = glb_measure._read_json_chunk(os.path.join(asset_root, folder, filename))
            prims = _project_points(doc, None, None)
        except Exception:
            prims = []

        # ── 俯视：x/z，等比缩放到窗口，保持真实长宽比 ──
        tw = top[2] - top[0] - 8
        th = top[3] - top[1] - 8
        span = max(size_m[0], size_m[2], human_r * 2.4) or 1.0
        k = min(tw / span, th / span)
        ox = (top[0] + top[2]) / 2.0
        oy = (top[1] + top[3]) / 2.0
        for pts in prims:
            poly = [(ox + p[0] * k, oy + p[2] * k) for p in pts]
            # 用底 4 点 + 顶 4 点分别画两个四边形，形成「盒套盒」观感
            d.polygon(poly[:4], outline=LAYER_COLOR.get(it["layer"], INK))
            d.polygon(poly[4:], outline=ACCENT)
        rr = human_r * k
        if rr >= 3:
            # 虚线圆：把圆拆成小段画（PIL 没有 line dash）
            seg = 16
            for s in range(seg):
                a0 = 2 * math.pi * s / seg
                a1 = 2 * math.pi * (s + 0.55) / seg
                d.line([ox + rr * math.cos(a0), oy + rr * math.sin(a0),
                        ox + rr * math.cos(a1), oy + rr * math.sin(a1)],
                       fill=(235, 235, 235), width=1)
        # 俯视窗右下角比例尺：1 米
        sx0, sy = top[2] - 10 - k, top[3] - 7
        if k >= 3:
            d.line([sx0, sy, top[2] - 10, sy], fill=DIM, width=1)
            d.text((sx0, sy - 12), "1m", font=f_tiny, fill=DIM)

        # ── 立面：y 与水平，等比（只画高度 + 宽度条，避免误差） ──
        sw = side[2] - side[0] - 8
        sh = side[3] - side[1] - 8
        span_h = max(size_m[1], human_m * 1.15) or 1.0
        kh = min(sw / max(horiz, 0.5), sh / span_h)
        bx = side[0] + 8
        by = side[3] - 4
        bw = max(2.0, horiz * kh)
        bh = max(1.5, size_m[1] * kh)
        d.rectangle([bx, by - bh, bx + bw, by], fill=(90, 78, 62), outline=ACCENT)
        # 真人肩线
        yh = by - human_m * kh
        if side[1] + 2 <= yh <= by:
            d.line([bx - 2, yh, bx + sw - 4, yh], fill=HUMAN, width=1)
            d.text((bx + sw - 26, yh - 12), "2m", font=f_tiny, fill=HUMAN)

        # ── 底栏文字 ──
        ty = side[3] + 6
        d.text((card[0] + PAD, ty), "#%d %s" % (it["id"], it["name"][:22]), font=f_name, fill=INK)
        d.text((card[0] + PAD, ty + 15),
               "%.2f × %.2f × %.2f m" % (size_m[0], size_m[1], size_m[2]), font=f_tiny, fill=DIM)
        d.text((card[0] + PAD, ty + 28),
               "%s · w=%d · %s" % (it["layer"], it["weight"], it["sourceFolder"][:9]),
               font=f_tiny, fill=LAYER_COLOR.get(it["layer"], INK))
        # 层色条
        d.rectangle([card[2] - 10, card[1] + 6, card[2] - 5, card[1] + 70],
                    fill=LAYER_COLOR.get(it["layer"], INK))

    # 图例
    ly = HEADER_H - 16
    d.text((W - MARGIN - 330, ly), "绿=Ground 蓝=Wall 紫=Ceiling 橙=顶面 白=人体参照 黄=2m 肩线",
           font=f_tiny, fill=DIM)

    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    img.save(OUT, "PNG")
    # 顶视窗里「虚线圆」与「1 米比例尺」的最小像素阈值（低于这个值就画不出可读形状）
    print("wrote %s (%dx%d, %d items)" % (OUT, W, H, len(items)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
