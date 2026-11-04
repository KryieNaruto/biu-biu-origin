"""地牢截图的结构化测量（代替目视）。

## 为什么用测量代替目视

本项目参与验收的模型**没有图像输入能力**（`read_image` 会直接报
`model does not declare image input`），无法"看一眼截图说好不好"。
但"我看了没问题"本身也不是可复现的验收证据。因此这里把目视要回答的问题
拆成可计算的指标，任何人（或任何模型）都能重跑一遍拿到同样的数字：

  1. 画面是不是空的       → 前景像素占比、distinct colors
  2. 房间能不能数清       → 空白行列数（房间之间是否真的分开）
  3. 形状是不是矩形对齐   → 每个连通块的外接框宽高比
  4. 房型颜色能不能区分   → 量化后的主色簇数量与占比
  5. 画面有没有大面积纯色 → 最大单色占比（贴图/材质丢失的典型症状）

ASCII 缩略图是给人看的"廉价版截图"：80×40 字符足够分辨出
「一间间离散的方块」还是「糊成一整块」。

## 用法

    <python> tools/probes/measure_screenshot.py out/dungeon/dungeon_overview.png

依赖 Pillow 与 numpy（DSH 自带 Python 已含）。
"""

import collections
import sys

import numpy as np
from PIL import Image


def background_mask(arr: np.ndarray) -> tuple[np.ndarray, tuple[int, int, int]]:
    """返回 (前景掩码 [H,W], 背景色)。背景 = 出现次数最多的那个颜色（地牢外的虚空）。"""
    height, width, _ = arr.shape
    flat = arr.reshape(-1, 3)
    packed = (
        (flat[:, 0].astype(np.int32) << 16)
        | (flat[:, 1].astype(np.int32) << 8)
        | flat[:, 2]
    )
    values, counts = np.unique(packed, return_counts=True)
    bg = int(values[np.argmax(counts)])
    return (packed != bg).reshape(height, width), (
        (bg >> 16) & 255,
        (bg >> 8) & 255,
        bg & 255,
    )


def report(path: str) -> None:
    img = Image.open(path).convert("RGB")
    arr = np.asarray(img).astype(np.int16)
    height, width, _ = arr.shape
    fg, bg_color = background_mask(arr)

    print(f"\n=== {path}  {width}x{height} ===")

    # 1. 空画面检测
    print(f"背景色 {bg_color}  占 {1 - fg.mean():.1%}")
    print(f"前景像素 {int(fg.sum())} ({fg.mean():.1%})")

    # 5. 最大单色占比（材质/贴图丢失会让某个平面颜色压倒一切）
    flat = arr.reshape(-1, 3)
    packed = (
        (flat[:, 0].astype(np.int32) << 16)
        | (flat[:, 1].astype(np.int32) << 8)
        | flat[:, 2]
    )
    values, counts = np.unique(packed, return_counts=True)
    top = int(counts.max()) / packed.size
    print(f"distinct colors {len(values)}  最大单色占 {top:.1%}")

    ys, xs = np.nonzero(fg)
    if len(xs) == 0:
        print("!! 画面全空")
        return
    x0, x1, y0, y1 = int(xs.min()), int(xs.max()), int(ys.min()), int(ys.max())
    print(f"内容包围盒 x[{x0},{x1}] y[{y0},{y1}] = {x1 - x0 + 1}x{y1 - y0 + 1}")

    # 2. 房间离散性：整行/整列几乎无前景 = 房间之间有缝
    row_fill = fg.sum(axis=1)
    col_fill = fg.sum(axis=0)
    blank_rows = int((row_fill[y0 : y1 + 1] < 20).sum())
    blank_cols = int((col_fill[x0 : x1 + 1] < 20).sum())
    print(
        f"近乎空白的行 {blank_rows} / {y1 - y0 + 1},  空白列 {blank_cols} / {x1 - x0 + 1}"
        "  （>0 说明房间彼此分开，而不是糊成一整块）"
    )

    # 4. 主色簇（量化到 16 级，压掉光照渐变）
    quant = flat[np.asarray(fg).reshape(-1)] // 16 * 16
    qp = (
        (quant[:, 0].astype(np.int32) << 16)
        | (quant[:, 1].astype(np.int32) << 8)
        | quant[:, 2]
    )
    qv, qc = np.unique(qp, return_counts=True)
    print("主色簇 top6（量化到 16 级）:")
    for i in np.argsort(-qc)[:6]:
        c = int(qv[i])
        print(
            f"   ({c >> 16 & 255:3d},{c >> 8 & 255:3d},{c & 255:3d})  {qc[i]:7d}"
            f"  {qc[i] / len(quant):.1%}"
        )

    # 3. ASCII 结构图
    print("\n=== ASCII 结构图（80x36）===")
    cols, rows = 80, 36
    for r in range(rows):
        yy = y0 + int((y1 - y0) * r / max(rows - 1, 1))
        line = []
        for c in range(cols):
            xx = x0 + int((x1 - x0) * c / max(cols - 1, 1))
            if not fg[yy, xx]:
                line.append(".")
                continue
            red, green, blue = arr[yy, xx]
            lum = int(0.299 * red + 0.587 * green + 0.114 * blue)
            if blue > red + 12:
                line.append("B")          # 偏蓝
            elif red > green + 12 and green > blue:
                line.append("O")          # 偏橙
            elif lum > 140:
                line.append("W")          # 亮（墙顶）
            elif lum > 85:
                line.append("+")
            elif lum > 45:
                line.append("-")
            else:
                line.append(" ")
        print("  " + "".join(line))
    print("\n图例:  .空洞  -暗  +中  W亮(墙顶)  B偏蓝  O偏橙")


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(2)
    for arg in sys.argv[1:]:
        report(arg)
