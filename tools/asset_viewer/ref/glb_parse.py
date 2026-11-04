#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
glb_parse.py —— GLB / glTF 解析参考实现（AssetViewer 的 Python 真源）

定位：这是 C++ 模块 (glb_loader.cpp / anim_sampler.cpp / thumb_renderer.cpp) 的
     **行为参考**：在移植前先用 Python 跑通全部 301 个 glb，把语义、边界、耗时钉死，
     再让 C++ 按同样语义实现。本文件不被编译进产品。

依赖：只用标准库 + numpy（若不可用自动回退到纯 Python 慢路径）。
      PNG/JPEG 解码：标准库没有，故本文件只做「几何+动画+贴图索引」层面的完整解析，
      像素级预览纹理烘焙在 C++ 侧用 WIC 完成（见 SHARED_SPEC §6）。

用法：
    python ref/glb_parse.py <素材根目录>              # 扫描统计
    python ref/glb_parse.py <单个.glb> --dump         # 打印结构详情
    python ref/glb_parse.py <素材根目录> --bench      # 计时
"""
import json
import os
import struct
import sys
import time
import math
from collections import OrderedDict

try:
    import numpy as np
    HAVE_NUMPY = True
except Exception:  # pragma: no cover
    np = None
    HAVE_NUMPY = False


# ---------------------------------------------------------------------------
# 工具
# ---------------------------------------------------------------------------
def utext(p):
    """Windows 文件系统 API 是宽字符的；路径统一用 str 处理。"""
    return p


COMPONENT = {
    5120: ('b', 1), 5121: ('B', 1), 5122: ('h', 2),
    5123: ('H', 2), 5125: ('I', 4), 5126: ('f', 4),
}
NUM_COMPONENTS = {'SCALAR': 1, 'VEC2': 2, 'VEC3': 3, 'VEC4': 4,
                  'MAT2': 4, 'MAT3': 9, 'MAT4': 16}


def load_glb(path):
    """返回 (json_dict, bin_bytes or None)。支持 .glb 与 .gltf(+外置 buffer)。"""
    ext = os.path.splitext(path)[1].lower()
    if ext in ('.gltf', '.gltf.json'):
        with open(path, 'rb') as f:
            js = json.load(f)
        base = os.path.dirname(path)
        bins = []
        for b in js.get('buffers', []):
            uri = b.get('uri', '')
            if uri.startswith('data:'):
                bins.append(_b64(uri))
            elif uri:
                with open(os.path.join(base, uri), 'rb') as bf:
                    bins.append(bf.read())
            else:
                bins.append(None)
        return js, bins
    with open(path, 'rb') as f:
        data = f.read()
    if len(data) < 12:
        raise ValueError('文件太小')
    magic, ver, total = struct.unpack('<III', data[0:12])
    if magic != 0x46546C67:
        raise ValueError('不是 GLB（魔数 0x%08X）' % magic)
    off = 12
    js = None
    bins = []
    while off + 8 <= len(data):
        clen, ctype = struct.unpack('<II', data[off:off + 8])
        off += 8
        chunk = data[off:off + clen]
        off += clen
        if ctype == 0x4E4F534A:      # JSON
            js = json.loads(chunk.decode('utf-8').rstrip('\x00 \t\r\n'))
        elif ctype == 0x004E4942:    # BIN
            bins.append(chunk)
    if js is None:
        raise ValueError('GLB 缺少 JSON chunk')
    # 外置 buffer（.glb 理论上不该有，容错处理）
    for i, b in enumerate(js.get('buffers', [])):
        if i < len(bins) and bins[i] is not None:
            continue
        uri = b.get('uri', '')
        if uri.startswith('data:'):
            bins.insert(i, _b64(uri))
        elif uri:
            base = os.path.dirname(path)
            try:
                with open(os.path.join(base, uri), 'rb') as bf:
                    bins.insert(i, bf.read())
            except OSError:
                bins.insert(i, None)
        else:
            bins.insert(i, None)
    return js, bins


def _b64(uri):
    import base64
    return base64.b64decode(uri.split(',', 1)[1])


def accessor_data(js, bins, idx):
    """读取 accessor → list[tuple]。处理 byteStride、normalized、sparse。"""
    acc = js['accessors'][idx]
    n = acc.get('count', 0)
    ctype = acc.get('componentType', 5126)
    ncomp = NUM_COMPONENTS[acc['type']]
    kind, csize = COMPONENT[ctype]
    normalized = acc.get('normalized', False)

    out = [tuple([0.0] * ncomp) for _ in range(n)]
    if 'bufferView' in acc:
        bv = js['bufferViews'][acc['bufferView']]
        buf = bins[bv.get('buffer', 0)] if bv.get('buffer', 0) < len(bins) else None
        if buf is None:
            buf = b''
        base = bv.get('byteOffset', 0) + acc.get('byteOffset', 0)
        stride = bv.get('byteStride', 0) or (csize * ncomp)
        one = csize * ncomp
        fmt = '<' + kind * ncomp
        for i in range(n):
            s = base + i * stride
            if s + one > len(buf):
                break
            vals = struct.unpack(fmt, buf[s:s + one])
            if normalized:
                vals = tuple(_denorm(v, ctype) for v in vals)
            out[i] = vals

    # sparse 覆盖
    if 'sparse' in acc:
        sp = acc['sparse']
        scnt = sp['count']
        ibv = js['bufferViews'][sp['indices']['bufferView']]
        ibuf = bins[ibv.get('buffer', 0)] or b''
        icmp = sp['indices'].get('componentType', 5125)
        ik, isz = COMPONENT[icmp]
        ioff = ibv.get('byteOffset', 0) + sp['indices'].get('byteOffset', 0)
        sidx = [struct.unpack_from('<' + ik, ibuf, ioff + i * isz)[0] for i in range(scnt)]
        vbv = js['bufferViews'][sp['values']['bufferView']]
        vbuf = bins[vbv.get('buffer', 0)] or b''
        voff = vbv.get('byteOffset', 0) + sp['values'].get('byteOffset', 0)
        fmt = '<' + kind * ncomp
        one = csize * ncomp
        for i, vi in enumerate(sidx):
            if vi < n and voff + i * one + one <= len(vbuf):
                vals = struct.unpack(fmt, vbuf[voff + i * one:voff + i * one + one])
                if normalized:
                    vals = tuple(_denorm(v, ctype) for v in vals)
                out[vi] = vals
    return out, ncomp


def _denorm(v, ctype):
    if ctype == 5120:
        return max(v / 127.0, -1.0)
    if ctype == 5121:
        return v / 255.0
    if ctype == 5122:
        return max(v / 32767.0, -1.0)
    if ctype == 5123:
        return v / 65535.0
    return v


# ---------------------------------------------------------------------------
# 矩阵工具（行优先，行向量 × 矩阵，与 model_data.h 一致）
# ---------------------------------------------------------------------------
def mat_id():
    return [1.0, 0, 0, 0, 0, 1.0, 0, 0, 0, 0, 1.0, 0, 0, 0, 0, 1.0]


def mat_mul(a, b):
    r = [0.0] * 16
    for i in range(4):
        for j in range(4):
            r[i * 4 + j] = (a[i * 4] * b[j] + a[i * 4 + 1] * b[4 + j] +
                            a[i * 4 + 2] * b[8 + j] + a[i * 4 + 3] * b[12 + j])
    return r


def mat_from_gltf(cm):
    """glTF 列主序 16 元素 → 行优先（转置）。"""
    r = [0.0] * 16
    for row in range(4):
        for col in range(4):
            r[row * 4 + col] = cm[col * 4 + row]
    return r


def trs_to_mat(t, q, s):
    x, y, z, w = q
    xx, yy, zz = x * x, y * y, z * z
    xy, xz, yz = x * y, x * z, y * z
    wx, wy, wz = w * x, w * y, w * z
    # 行向量约定：上 3x3 为 R 的转置，此处按 R 的列向量定义好再转置
    rcol = [
        [1 - 2 * (yy + zz), 2 * (xy - wz), 2 * (xz + wy)],
        [2 * (xy + wz), 1 - 2 * (xx + zz), 2 * (yz - wx)],
        [2 * (xz - wy), 2 * (yz + wx), 1 - 2 * (xx + yy)],
    ]
    m = mat_id()
    for row in range(3):
        for col in range(3):
            m[row * 4 + col] = rcol[col][row] * s[col]
    m[12], m[13], m[14] = t[0], t[1], t[2]
    return m


def node_local(node):
    if 'matrix' in node:
        return mat_from_gltf(node['matrix'])
    t = node.get('translation', [0, 0, 0])
    q = node.get('rotation', [0, 0, 0, 1])
    s = node.get('scale', [1, 1, 1])
    return trs_to_mat(t, q, s)


def transform_pt(m, p):
    x, y, z = p
    return (x * m[0] + y * m[4] + z * m[8] + m[12],
            x * m[1] + y * m[5] + z * m[9] + m[13],
            x * m[2] + y * m[6] + z * m[10] + m[14])


def transform_dir(m, p):
    x, y, z = p
    return (x * m[0] + y * m[4] + z * m[8],
            x * m[1] + y * m[5] + z * m[9],
            x * m[2] + y * m[6] + z * m[10])


# ---------------------------------------------------------------------------
# 解析主体
# ---------------------------------------------------------------------------
def parse_glb(path):
    """返回一个 dict（与 ModelData 字段一一对应，便于对照移植）。"""
    t0 = time.perf_counter()
    js, bins = load_glb(path)
    out = {
        'path': path,
        'name': os.path.splitext(os.path.basename(path))[0],
        'category': os.path.basename(os.path.dirname(path)),
        'ok': True, 'error': '', 'warning': '',
        'vertices': [], 'indices': [], 'subMeshes': [], 'materials': [],
        'triangles': 0, 'vcount': 0, 'bones': [], 'clips': [], 'textures': [],
        'parseMs': 0.0,
    }
    nodes = js.get('nodes', [])
    meshes = js.get('meshes', [])
    mats = js.get('materials', [])
    accs = js.get('accessors', [])

    # ---- 节点父子表 ----
    parent = {}
    for i, n in enumerate(nodes):
        for c in n.get('children', []):
            parent[c] = i

    def world_of(i):
        m = node_local(nodes[i])
        p = parent.get(i, -1)
        while p != -1:
            m = mat_mul(m, node_local(nodes[p]))
            p = parent.get(p, -1)
        return m

    # ---- 材质 ----
    for mi, m in enumerate(mats):
        pbr = m.get('pbrMetallicRoughness', {})
        base = pbr.get('baseColorFactor', [1, 1, 1, 1])
        tex = -1
        if 'baseColorTexture' in pbr:
            tex = pbr['baseColorTexture'].get('index', -1)
        out['materials'].append({
            'name': m.get('name', 'mat%d' % mi),
            'baseColor': list(base),
            'diffuseTex': tex,
            'doubleSided': bool(m.get('doubleSided', False)),
            'metallic': pbr.get('metallicFactor', 1.0),
            'roughness': pbr.get('roughnessFactor', 1.0),
        })

    # ---- 网格（含蒙皮：需要 joint 世界矩阵）----
    skin_js = js.get('skins', [])

    def skin_joint_worlds(skin_index):
        sk = skin_js[skin_index]
        joints = sk.get('joints', [])
        ibm_idx = sk.get('inverseBindMatrices', -1)
        ibms = []
        if ibm_idx >= 0 and ibm_idx < len(accs):
            flat, _ = accessor_data(js, bins, ibm_idx)
            for j in range(len(joints)):
                if j < len(flat) and len(flat[j]) == 16:
                    ibms.append(list(flat[j]))
                else:
                    ibms.append(list(mat_id()))
        else:
            ibms = [list(mat_id()) for _ in joints]
        return joints, ibms

    # 收集骨骼（按 mesh node 的 skin 展开）。
    # C++ 侧会建立统 bones 表 + parent 关系 + bind（局部逆绑定）。
    bone_names = []
    bone_index = {}

    def intern_bone(node_i):
        if node_i in bone_index:
            return bone_index[node_i]
        bone_index[node_i] = len(bone_names)
        nm = nodes[node_i].get('name', 'bone%d' % node_i)
        bone_names.append((nm, node_i))
        return bone_index[node_i]

    # 静态模型不展开骨骼。
    # 节点遍历：优先用场景根；无 scene 时用没有父的节点
    scene = js.get('scene', 0)
    scenes = js.get('scenes', [])
    roots = []
    if scene < len(scenes):
        roots = scenes[scene].get('nodes', [])
    if not roots:
        roots = [i for i in range(len(nodes)) if i not in parent]

    def walk(ni):
        n = nodes[ni]
        if 'mesh' in n:
            mesh = meshes[n['mesh']]
            node_world = world_of(ni)
            joint_map = None
            if 'skin' in n and n['skin'] < len(skin_js):
                joints, ibms = skin_joint_worlds(n['skin'])
                joint_map = {}
                for k, jn in enumerate(joints):
                    b = intern_bone(jn)
                    joint_map[k] = b
                    # 世界矩阵修正：jointWorld * ibm 再乘父链之外的根修正
                    # glTF 规范的蒙皮矩阵 = inverse(globalTransform(node)) * jointWorld * ibm
                    sk = skin_js[n['skin']]
                    sm = sk.get('skeleton', -1)
                    if sm == -1:
                        skel_world = world_of(joints[0])
                    else:
                        skel_world = world_of(sm)
                    joint_world = world_of(jn)
                    # * local world (mesh node) 的逆
                    # 简化：用 skeleton 根而非 mesh node 做逆（多数导出器二者重合）
                    out['_skinRootWorld'] = skel_world
            for pi, prim in enumerate(mesh.get('primitives', [])):
                if prim.get('mode', 4) not in (4,):
                    # 跳过非三角形（本素材集实测 944 个 primitive 全为 4）
                    out['warning'] = (out['warning'] + '; ' if out['warning'] else '') + \
                                     'mode=%d 非三角形已跳过' % prim.get('mode', 4)
                    continue
                attr = prim.get('attributes', {})
                pos_i = attr.get('POSITION')
                if pos_i is None:
                    continue
                pos, _ = accessor_data(js, bins, pos_i)
                nrm, _ = accessor_data(js, bins, attr['NORMAL']) if 'NORMAL' in attr else ([], 0)
                uv, _ = accessor_data(js, bins, attr['TEXCOORD_0']) if 'TEXCOORD_0' in attr else ([], 0)
                idx = []
                if 'indices' in prim:
                    flat, _ = accessor_data(js, bins, prim['indices'])
                    idx = [v[0] for v in flat]
                else:
                    idx = list(range(len(pos)))
                base_v = out['vcount']
                for vi, p in enumerate(pos):
                    wp = transform_pt(node_world, p)
                    wn = transform_dir(node_world, nrm[vi]) if vi < len(nrm) else (0.0, 0.0, 0.0)
                    wuv = uv[vi] if vi < len(uv) else (0.0, 0.0)
                    out['vertices'].append((wp[0], wp[1], wp[2], wn[0], wn[1], wn[2], wuv[0], wuv[1]))
                mat_idx = prim.get('material', -1)
                out['subMeshes'].append({
                    'materialIndex': mat_idx,
                    'indexOffset': len(out['indices']),
                    'indexCount': len(idx),
                    'name': mesh.get('name', 'mesh%d' % n['mesh']) + ('_%d' % pi if pi else ''),
                })
                out['indices'].extend([v + base_v for v in idx])
                out['vcount'] += len(pos)
            out['triangles'] += sum(len(mesh.get('primitives', [])[k].get('indices', [])) or 0 for k in [pi]) // 3 if False else 0
            # 三角形数单独统计
            tri = 0
            for prim in mesh.get('primitives', []):
                if prim.get('mode', 4) != 4:
                    continue
                if 'indices' in prim:
                    tri += accs[prim['indices']].get('count', 0) // 3
                elif 'POSITION' in prim.get('attributes', {}):
                    tri += accs[prim['attributes']['POSITION']].get('count', 0) // 3
            out['triangles'] += tri
        for c in n.get('children', []):
            walk(c)

    for r in roots:
        walk(r)

    # ---- 骨骼表 ----
    for bi, (nm, ni) in enumerate(bone_names):
        p = parent.get(ni, -1)
        pb = bone_index.get(p, -1)
        out['bones'].append({
            'name': nm, 'parent': pb,
            'local': node_local(nodes[ni]),
            'bind': mat_id(),   # 由 skin 绑定矩阵另行处理
        })

    # ---- 动画 ----
    for ai, anim in enumerate(js.get('animations', [])):
        clip = {'name': anim.get('name', 'clip%d' % ai), 'duration': 0.0, 'tracks': []}
        tracks = {}
        for ch in anim.get('channels', []):
            tgt = ch.get('target', {})
            node_i = tgt.get('node', -1)
            if node_i < 0 or node_i not in bone_index:
                continue
            if tgt.get('path') != 'rotation':
                continue  # 本素材集动画以旋转为主；C++ 侧补 T/S
            smp = anim['samplers'][ch['sampler']]
            times, _ = accessor_data(js, bins, smp['input'])
            vals, _ = accessor_data(js, bins, smp['output'])
            t_flat = [v[0] for v in times]
            bi = bone_index[node_i]
            tracks.setdefault(bi, []).append((t_flat, [list(v) for v in vals]))
            if t_flat:
                clip['duration'] = max(clip['duration'], t_flat[-1])
        for bi, chans in tracks.items():
            rot = None
            for t_flat, vals in chans:
                rot = (t_flat, vals)
                break
            if rot:
                clip['tracks'].append((bi, rot[0], rot[1]))
        out['clips'].append(clip)

    # ---- 纹理 ----
    for ii, img in enumerate(js.get('images', [])):
        out['textures'].append({
            'name': img.get('name', 'img%d' % ii),
            'embedded': ('bufferView' in img) or str(img.get('uri', '')).startswith('data:'),
            'mime': img.get('mimeType', ''),
            'uri': img.get('uri', ''),
        })

    out['parseMs'] = (time.perf_counter() - t0) * 1000.0
    return out


# ---------------------------------------------------------------------------
# 动画采样（移植参考：anim_sampler.cpp）
# ---------------------------------------------------------------------------
def _lerp(a, b, k):
    return a + (b - a) * k


def slerp(q0, q1, k):
    d = sum(a * b for a, b in zip(q0, q1))
    if d < 0.0:
        q1 = [-v for v in q1]
        d = -d
    if d > 0.9995:
        r = [_lerp(a, b, k) for a, b in zip(q0, q1)]
    else:
        th = math.acos(max(-1.0, min(1.0, d)))
        s0 = math.sin((1 - k) * th) / math.sin(th)
        s1 = math.sin(k * th) / math.sin(th)
        r = [a * s0 + b * s1 for a, b in zip(q0, q1)]
    ln = math.sqrt(sum(v * v for v in r)) or 1.0
    return [v / ln for v in r]


def sample_track(times, vals, t, interp='LINEAR'):
    """返回 t 时刻的值（CUBICSPLINE 步长 3 的处理见 C++ 注释）。"""
    n = len(times)
    if n == 0:
        return [0, 0, 0, 1]
    if t <= times[0]:
        return vals[0]
    if t >= times[-1]:
        return vals[-1]
    lo, hi = 0, n - 1
    while hi - lo > 1:
        mid = (lo + hi) // 2
        if times[mid] <= t:
            lo = mid
        else:
            hi = mid
    span = times[hi] - times[lo]
    k = 0.0 if span <= 1e-12 else (t - times[lo]) / span
    a, b = vals[lo], vals[hi]
    if len(a) == 4:
        return slerp(a, b, k)
    return [_lerp(x, y, k) for x, y in zip(a, b)]


# ---------------------------------------------------------------------------
# 自检 / 统计 / 计时
# ---------------------------------------------------------------------------
def scan(root):
    files = []
    for dirpath, _dirs, names in os.walk(root):
        # 跳过 Godot 的 .godot 缓存
        if '.godot' in dirpath.split(os.sep):
            continue
        for nm in names:
            if nm.lower().endswith('.glb'):
                files.append(os.path.join(dirpath, nm))
    return sorted(files)


def main(argv):
    if len(argv) < 2:
        print(__doc__)
        return 1
    target = argv[1]
    dump = '--dump' in argv
    bench = '--bench' in argv

    if os.path.isfile(target):
        m = parse_glb(target)
        if dump:
            print(json.dumps({k: v for k, v in m.items() if not isinstance(v, list)},
                             indent=2, ensure_ascii=False))
            print('vertices=%d tris=%d subMeshes=%d materials=%d bones=%d clips=%d' % (
                len(m['vertices']), m['triangles'], len(m['subMeshes']),
                len(m['materials']), len(m['bones']), len(m['clips'])))
            for c in m['clips']:
                print('  clip: %-40s dur=%.3fs tracks=%d' % (c['name'][:40], c['duration'], len(c['tracks'])))
        return 0

    files = scan(target)
    print('found %d glb under %s' % (len(files), target))
    ok = 0
    fails = []
    total_v = total_t = 0
    t0 = time.perf_counter()
    per = []
    for p in files:
        try:
            m = parse_glb(p)
            if not m['ok']:
                fails.append((p, m['error']))
                continue
            ok += 1
            total_v += len(m['vertices'])
            total_t += m['triangles']
            per.append((m['parseMs'], p))
        except Exception as e:  # noqa: BLE001
            fails.append((p, repr(e)))
    dt = (time.perf_counter() - t0) * 1000.0
    print('ok=%d fail=%d  total verts=%d tris=%d' % (ok, len(fails), total_v, total_t))
    print('total %.1f ms  avg %.2f ms/file' % (dt, dt / max(1, len(files))))
    if bench and per:
        per.sort(reverse=True)
        print('slowest:')
        for ms, p in per[:10]:
            print('  %8.2f ms  %s' % (ms, os.path.basename(p)))
    for p, e in fails[:20]:
        print('FAIL %s : %s' % (p, e))
    return 0 if not fails else 2


if __name__ == '__main__':
    sys.exit(main(sys.argv))
