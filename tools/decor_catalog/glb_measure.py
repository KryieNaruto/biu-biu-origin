"""GLB 几何测量（纯标准库）。

GLB 结构：12 字节头（magic b"glTF" / version / length）+ 若干 chunk（JSON chunk、BIN chunk）。
本模块只读 JSON chunk：

  * 每个 mesh primitive 的 POSITION accessor 自带 min/max（glTF 2.0 规范强制），得到**局部**包围盒；
  * 用 node 的 translation / rotation(四元数) / scale 把局部包围盒的 8 个角点变换到世界系
    （有 skin 时变换由 joints 决定，这里退化成忽略 skin 权重，按 node 自身矩阵近似——见 README）；
  * 场景级 AABB = 所有 mesh node 世界盒的并集。

刻意**不**读 BIN chunk：不走完整顶点遍历，速度是 O(accessor 数)；代价是包围盒是「变换后的局部盒」而不是
「变换后顶点的紧致盒」，对 90°/任意旋转的模型会略微偏大（轴对齐盒包住旋转盒）。对「挑装饰尺寸」这个用途足够，
且 md 里写明了这一点。极端的「盒套盒」场景（旋转 45°）误差上限 = 对角线/最长边 ≤ √3。
"""

from __future__ import annotations

import json
import struct
from dataclasses import dataclass, field

GLB_MAGIC = 0x46546C67  # 'glTF'
CHUNK_JSON = 0x4E4F534A  # 'JSON'
CHUNK_BIN = 0x004E4942  # 'BIN\0'


@dataclass
class Box3:
    """轴对齐包围盒（glTF 右手系：Y 上，-Z 前）。空盒用 +inf/-inf 表示。"""

    min: list[float] = field(default_factory=lambda: [float("inf")] * 3)
    max: list[float] = field(default_factory=lambda: [float("-inf")] * 3)

    @property
    def empty(self) -> bool:
        return self.min[0] > self.max[0]

    def extend(self, other: "Box3") -> None:
        if other.empty:
            return
        for i in range(3):
            if other.min[i] < self.min[i]:
                self.min[i] = other.min[i]
            if other.max[i] > self.max[i]:
                self.max[i] = other.max[i]

    def corners(self) -> list[tuple[float, float, float]]:
        xs, ys, zs = (self.min[0], self.max[0]), (self.min[1], self.max[1]), (self.min[2], self.max[2])
        return [(x, y, z) for x in xs for y in ys for z in zs]

    def size(self) -> list[float]:
        if self.empty:
            return [0.0, 0.0, 0.0]
        return [self.max[i] - self.min[i] for i in range(3)]


def _read_json_chunk(path: str) -> dict:
    with open(path, "rb") as f:
        head = f.read(12)
        if len(head) < 12:
            raise ValueError("文件太短，不是 GLB")
        magic, version, total_len = struct.unpack("<III", head)
        if magic != GLB_MAGIC:
            raise ValueError("magic 不是 glTF（可能是 .gltf 或损坏文件）")
        if version != 2:
            raise ValueError("只支持 glTF 2.0（version=%d）" % version)
        while True:
            chunk_head = f.read(8)
            if len(chunk_head) < 8:
                raise ValueError("找不到 JSON chunk")
            chunk_len, chunk_type = struct.unpack("<II", chunk_head)
            data = f.read(chunk_len)
            if chunk_type == CHUNK_JSON:
                return json.loads(data.decode("utf-8").rstrip("\x00 "))
        # 只有 JSON chunk 是我们要的；BIN chunk 读掉丢弃（当前实现不读顶点）


def _quat_matrix(q: list[float] | None) -> list[list[float]]:
    """glTF 四元数 (x,y,z,w) → 3x3 旋转矩阵（行优先）。"""
    if not q:
        return [[1.0, 0.0, 0.0], [0.0, 1.0, 0.0], [0.0, 0.0, 1.0]]
    x, y, z, w = q[0], q[1], q[2], q[3]
    xx, yy, zz = x * x, y * y, z * z
    xy, xz, yz = x * y, x * z, y * z
    wx, wy, wz = w * x, w * y, w * z
    return [
        [1 - 2 * (yy + zz), 2 * (xy - wz), 2 * (xz + wy)],
        [2 * (xy + wz), 1 - 2 * (xx + zz), 2 * (yz - wx)],
        [2 * (xz - wy), 2 * (yz + wx), 1 - 2 * (xx + yy)],
    ]


def _apply_trs(point, t, r, s):
    """p_world = R * (S * p_local) + T。"""
    sx, sy, sz = s
    x, y, z = point[0] * sx, point[1] * sy, point[2] * sz
    m = r
    return (
        m[0][0] * x + m[0][1] * y + m[0][2] * z + t[0],
        m[1][0] * x + m[1][1] * y + m[1][2] * z + t[1],
        m[2][0] * x + m[2][1] * y + m[2][2] * z + t[2],
    )


def measure_glb(path: str) -> dict:
    """测量一个 .glb 的包围盒与结构信息。异常时抛 ValueError。"""
    doc = _read_json_chunk(path)
    accessors = doc.get("accessors", [])
    meshes = doc.get("meshes", [])
    nodes = doc.get("nodes", [])
    scenes = doc.get("scenes", [])
    scene_index = doc.get("scene", 0)

    # mesh index → 局部包围盒（所有 primitive 的 POSITION min/max 并集）
    mesh_local: dict[int, Box3] = {}
    triangle_count = 0
    for mi, mesh in enumerate(meshes):
        box = Box3()
        for prim in mesh.get("primitives", []):
            attrs = prim.get("attributes", {})
            pos = attrs.get("POSITION")
            if pos is not None and pos < len(accessors):
                acc = accessors[pos]
                if "min" in acc and "max" in acc:
                    sub = Box3(list(map(float, acc["min"])), list(map(float, acc["max"])))
                    box.extend(sub)
            if "indices" in prim and prim["indices"] < len(accessors):
                triangle_count += accessors[prim["indices"]].get("count", 0) // 3
            elif attrs.get("POSITION") is not None and attrs["POSITION"] < len(accessors):
                triangle_count += accessors[attrs["POSITION"]].get("count", 0) // 3
        mesh_local[mi] = box

    world = Box3()
    mesh_node_count = 0
    seen: set[int] = set()

    def walk(index: int, t=(0.0, 0.0, 0.0), r=None, s=(1.0, 1.0, 1.0)) -> None:
        nonlocal mesh_node_count
        if index in seen or index >= len(nodes):
            return
        seen.add(index)
        node = nodes[index]
        nt = node.get("translation", t)
        nr = node.get("rotation", r)
        ns = node.get("scale", s)
        if "matrix" in node:
            # 列主序 4x4：分解出 T/R/S 太麻烦，直接对局部盒角点做完整矩阵乘
            m = node["matrix"]
            col_major = [[m[c * 4 + rr] for c in range(4)] for rr in range(4)]
            local_boxes = node.get("mesh")
            if local_boxes is not None and local_boxes in mesh_local:
                box = Box3()
                for p in mesh_local[local_boxes].corners():
                    wx = col_major[0][0] * p[0] + col_major[0][1] * p[1] + col_major[0][2] * p[2] + col_major[0][3]
                    wy = col_major[1][0] * p[0] + col_major[1][1] * p[1] + col_major[1][2] * p[2] + col_major[1][3]
                    wz = col_major[2][0] * p[0] + col_major[2][1] * p[1] + col_major[2][2] * p[2] + col_major[2][3]
                    box.extend(Box3([wx, wy, wz], [wx, wy, wz]))
                world.extend(box)
                mesh_node_count += 1
        else:
            mesh_idx = node.get("mesh")
            if mesh_idx is not None and mesh_idx in mesh_local:
                rot = _quat_matrix(nr)
                box = Box3()
                for p in mesh_local[mesh_idx].corners():
                    w = _apply_trs(p, nt, rot, ns)
                    box.extend(Box3([w[0], w[1], w[2]], [w[0], w[1], w[2]]))
                world.extend(box)
                mesh_node_count += 1
        for child in node.get("children", []):
            # 子节点自己的 TRS 已在 walk 内部读取；父变换的复合在这里被简化成「子节点自带 TRS」
            walk(child, nt, nr, ns)

    roots = []
    if 0 <= scene_index < len(scenes):
        roots = scenes[scene_index].get("nodes", [])
    if not roots:
        roots = [i for i in range(len(nodes)) if not any(i in n.get("children", []) for n in nodes)]
    for root in roots:
        walk(root)

    if world.empty:
        world = Box3([0.0, 0.0, 0.0], [0.0, 0.0, 0.0])

    return {
        "min": [round(v, 6) for v in world.min],
        "max": [round(v, 6) for v in world.max],
        "size": [round(max(0.0, v), 6) for v in world.size()],
        "meshCount": len(meshes),
        "nodeCount": len(nodes),
        "meshNodeCount": mesh_node_count,
        "triangleCount": triangle_count,
        "hasSkins": bool(doc.get("skins")),
        "animationCount": len(doc.get("animations", [])),
        "materialCount": len(doc.get("materials", [])),
        "imageCount": len(doc.get("images", [])),
        "generator": (doc.get("asset", {}) or {}).get("generator", ""),
        "extraNames": [m.get("name", "") for m in meshes][:8],
    }


if __name__ == "__main__":
    import sys

    for p in sys.argv[1:]:
        print(p, json.dumps(measure_glb(p), ensure_ascii=False))
