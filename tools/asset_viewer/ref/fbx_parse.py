#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
fbx_parse.py —— FBX 二进制(7400) 解析参考实现

素材实测：70 个 .fbx，全部 version 7400（64 位偏移），全部在
    素材/POLYGON_Starter_SourceFiles_v2/SourceFiles/FBX/ 及 Collision/ 下。

定位：行为参考（给 fbx_loader.cpp 对照）。只用标准库。

FBX 二进制结构：
  头 27 字节: "Kaydara FBX Binary  \x00" + 2 字节 \x1a\x00 + uint32 version(小端)
  之后是节点序列，每个节点：
      EndOffset    u32 (7400) / u64 (7500+)
      NumProperties u32 / u64
      PropertyListLen u32 / u64
      NameLen      u8
      Name         NameLen 字节
      属性列表     (按类型编码)
      子节点序列   (直到 EndOffset)
  节点全结束标志：EndOffset==0
  每个节点的 "null record"：13 字节全 0 (7400) / 25 字节 (7500+)

属性类型编码：
  'Y' i16, 'C' u8(bool), 'I' i32, 'F' f32, 'D' f64, 'L' i64,
  'f' f32 数组 / 'd' f64 数组 / 'l' i64 数组 / 'i' i32 数组 / 'b' bool 数组  (u32 len + u32 encoding + u32 compressedLen + data)
  'S' string (u32 len + bytes), 'R' raw(u32 len + bytes)

用法：
    python ref/fbx_parse.py <单个.fbx> --dump
    python ref/fbx_parse.py <素材根目录>
"""
import os
import struct
import sys
import zlib
import json

_header = b'Kaydara FBX Binary  \x00'


class Node(object):
    __slots__ = ('name', 'props', 'children')

    def __init__(self, name, props):
        self.name = name
        self.props = props
        self.children = []

    def child(self, name):
        for c in self.children:
            if c.name == name:
                return c
        return None

    def childs(self, name):
        return [c for c in self.children if c.name == name]

    def __repr__(self):
        return '<Node %s props=%d children=%d>' % (self.name, len(self.props), len(self.children))


def _read_array(buf, off, kind):
    ulen, enc, clen = struct.unpack_from('<III', buf, off)
    off += 12
    raw = buf[off:off + clen]
    off += clen
    if enc == 1:
        raw = zlib.decompress(raw)
    fmt = {'f': 'f', 'd': 'd', 'l': 'q', 'i': 'i', 'b': 'B'}[kind]
    n = len(raw) // struct.calcsize(fmt)
    arr = list(struct.unpack_from('<%d%s' % (n, fmt), raw, 0))
    if kind == 'b':
        arr = [bool(v) for v in arr]
    return arr, off


def _read_props(buf, off, count, big=False):
    props = []
    for _ in range(count):
        t = chr(buf[off])
        off += 1
        if t == 'Y':
            props.append(struct.unpack_from('<h', buf, off)[0]); off += 2
        elif t == 'C':
            props.append(struct.unpack_from('<B', buf, off)[0]); off += 1
        elif t == 'I':
            props.append(struct.unpack_from('<i', buf, off)[0]); off += 4
        elif t == 'F':
            props.append(struct.unpack_from('<f', buf, off)[0]); off += 4
        elif t == 'D':
            props.append(struct.unpack_from('<d', buf, off)[0]); off += 8
        elif t == 'L':
            props.append(struct.unpack_from('<q', buf, off)[0]); off += 8
        elif t in ('f', 'd', 'l', 'i', 'b'):
            arr, off = _read_array(buf, off, t)
            props.append(arr)
        elif t == 'S':
            ln = struct.unpack_from('<I', buf, off)[0]; off += 4
            props.append(buf[off:off + ln].decode('utf-8', 'replace')); off += ln
        elif t == 'R':
            ln = struct.unpack_from('<I', buf, off)[0]; off += 4
            props.append(buf[off:off + ln]); off += ln
        else:
            raise ValueError('未知属性类型 %r @%d' % (t, off - 1))
    return props, off


def _read_node(buf, off, ver):
    big = ver >= 7500
    if big:
        end, nprops, plen = struct.unpack_from('<QQQ', buf, off)
        off += 24
    else:
        end, nprops, plen = struct.unpack_from('<III', buf, off)
        off += 12
    namelen = buf[off]
    off += 1
    if end == 0 and nprops == 0 and plen == 0 and namelen == 0:
        return None, off
    name = buf[off:off + namelen].decode('ascii', 'replace')
    off += namelen
    props, off = _read_props(buf, off, nprops, big)
    node = Node(name, props)
    while off < end:
        child, next_off = _read_node(buf, off, ver)
        if child is None:
            off = next_off
            break
        node.children.append(child)
        off = next_off
    return node, end


def parse_fbx(path):
    with open(path, 'rb') as f:
        buf = f.read()
    if not buf.startswith(_header):
        raise ValueError('不是 FBX 二进制（可能是 ASCII 或非 FBX）')
    ver = struct.unpack_from('<I', buf, 23)[0]
    off = 27
    root = Node('ROOT', [])
    while off < len(buf) - 4:
        node, next_off = _read_node(buf, off, ver)
        if node is None:
            break
        root.children.append(node)
        if next_off <= off:
            break
        off = next_off
    return root, ver


# ---------------------------------------------------------------------------
# 网格 / 三角化
# ---------------------------------------------------------------------------
def triangulate(pidx):
    """PolygonVertexIndex: 每条多边形以 (index ^ -1) 即 ~index 结束。"""
    tris = []
    poly = []
    out_polys = []
    for v in pidx:
        if v < 0:
            poly.append(~v)
            out_polys.append(poly)
            poly = []
        else:
            poly.append(v)
    if poly:
        out_polys.append(poly)
    for p in out_polys:
        n = len(p)
        if n < 3:
            continue
        # 扇形三角化（凸多边形足够；四边形/五边形素材集实测均可）
        for k in range(1, n - 1):
            tris.append((p[0], p[k], p[k + 1]))
    return tris


def get_geometry(node):
    """Geometry 节点 → dict(vertices, tris, uvs, materialMap)"""
    if node is None:
        return None
    verts = []
    pidx = []
    for c in node.children:
        if c.name == 'Vertices':
            verts = c.props[0]
        elif c.name == 'PolygonVertexIndex':
            pidx = c.props[0]
    if not verts or not pidx:
        return None
    tris = triangulate(pidx)
    # 按材质拆子网格
    mats = {}
    for c in node.children:
        if c.name == 'LayerElementMaterial':
            for cc in c.children:
                if cc.name == 'Materials':
                    mats['materials'] = cc.props[0]
                    mats['mapping'] = c.child('MappingInformationType').props[0]
                    mats['refmode'] = c.child('ReferenceInformationType').props[0]
    return {'verts': verts, 'tris': tris, 'nv': len(verts) // 3}


def collect_meshes(root):
    objs = root.child('Objects')
    if objs is None:
        return []
    res = []
    for g in objs.childs('Geometry'):
        if len(g.props) > 2 and g.props[2] != 'Mesh':
            continue
        geo = get_geometry(g)
        if geo is None:
            continue
        geo['id'] = g.props[0]
        geo['name'] = g.props[1].split('\x00')[0] if len(g.props) > 1 else ''
        res.append(geo)
    return res


def main(argv):
    if len(argv) < 2:
        print(__doc__)
        return 1
    target = argv[1]
    if os.path.isfile(target):
        root, ver = parse_fbx(target)
        meshes = collect_meshes(root)
        print('%s  version=%d  topNodes=%s' % (os.path.basename(target), ver,
                                               [c.name for c in root.children]))
        for m in meshes:
            print('  mesh %-40s verts=%d tris=%d' % (m['name'][:40], m['nv'], len(m['tris'])))
        if '--dump' in argv:
            def walk(n, d=0):
                print('  ' * d + n.name + ' props=' + str(n.props)[:100])
                for c in n.children[:30]:
                    walk(c, d + 1)
            walk(root)
        return 0
    # 目录扫描
    files = []
    for dp, _d, names in os.walk(target):
        for nm in names:
            if nm.lower().endswith('.fbx'):
                files.append(os.path.join(dp, nm))
    ok = fail = 0
    tm = ttr = 0
    for p in sorted(files):
        try:
            root, ver = parse_fbx(p)
            ms = collect_meshes(root)
            if ms:
                ok += 1
                ttr += sum(len(m['tris']) for m in ms)
                tm += len(ms)
            else:
                fail += 1
                print('NO-MESH', p)
        except Exception as e:  # noqa: BLE001
            fail += 1
            print('FAIL', p, repr(e))
    print('fbx ok=%d fail=%d meshes=%d tris=%d' % (ok, fail, tm, ttr))
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv))
