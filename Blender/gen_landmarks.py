# -*- coding: utf-8 -*-
"""
生成陆家嘴四座地标的真实网格模型（东方明珠/上海中心/环球金融中心/金茂大厦），
导出为 FBX 到 Assets/Resources/Models/Lujiazui/ 供 Unity 运行时 Resources.Load。

用法：
  blender.exe --background --python Blender/gen_landmarks.py
"""
import math
import os

import bpy
import bmesh
from mathutils import Vector

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.abspath(os.path.join(HERE, "..", "Assets", "Resources", "Models", "Lujiazui"))
os.makedirs(OUT, exist_ok=True)


# ── 材质 ──────────────────────────────────────────────
def set_input(node, names, value):
    for n in names:
        if n in node.inputs:
            node.inputs[n].default_value = value
            return True
    return False


def make_material(name, color, metallic=0.0, roughness=0.5, emissive=None, emit_strength=0.0):
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    bsdf = mat.node_tree.nodes["Principled BSDF"]
    set_input(bsdf, ["Base Color"], (*color, 1.0))
    set_input(bsdf, ["Metallic"], metallic)
    set_input(bsdf, ["Roughness"], roughness)
    if emissive is not None:
        set_input(bsdf, ["Emission Color", "Emission"], (*emissive, 1.0))
        set_input(bsdf, ["Emission Strength"], emit_strength)
    return mat


# ── 基础几何 ──────────────────────────────────────────
def new_object(name, data):
    obj = bpy.data.objects.new(name, data)
    bpy.context.collection.objects.link(obj)
    return obj


def add_sphere(name, location, radius, mat):
    bpy.ops.mesh.primitive_uv_sphere_add(segments=32, ring_count=16, radius=radius, location=location)
    obj = bpy.context.object
    obj.name = name
    obj.data.materials.append(mat)
    return obj


def add_cylinder(name, location, radius, depth, mat, rotation=(0, 0, 0)):
    bpy.ops.mesh.primitive_cylinder_add(vertices=24, radius=radius, depth=depth, location=location)
    obj = bpy.context.object
    obj.name = name
    obj.rotation_euler = rotation
    obj.data.materials.append(mat)
    return obj


def add_box(name, location, size, mat):
    bpy.ops.mesh.primitive_cube_add(size=1, location=location)
    obj = bpy.context.object
    obj.name = name
    obj.scale = size
    obj.data.materials.append(mat)
    return obj


def clear_scene():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    for o in list(bpy.data.objects):
        if o.name in ("Cube", "Light", "Camera"):
            bpy.data.objects.remove(o, do_unlink=True)


def root_empty(name):
    empty = bpy.data.objects.new(name, None)
    bpy.context.collection.objects.link(empty)
    return empty


def parent_to(obj, root):
    obj.parent = root


# ── 扭曲收分塔身（上海中心）用 bmesh ──────────────────
def build_twisted_tower(name, base_r, top_r, height, twist_deg, sides, mat):
    mesh = bpy.data.meshes.new(name)
    bm = bmesh.new()
    segs = 28
    prev = None
    for i in range(segs + 1):
        t = i / segs
        z = t * height
        r = base_r + (top_r - base_r) * t
        ang = math.radians(twist_deg) * t
        ring = []
        for s in range(sides):
            a = 2 * math.pi * s / sides + ang
            ring.append(bm.verts.new((r * math.cos(a), r * math.sin(a), z)))
        if prev is not None:
            for s in range(sides):
                s2 = (s + 1) % sides
                bm.faces.new((prev[s], prev[s2], ring[s2], ring[s]))
        prev = ring
    bm.to_mesh(mesh)
    bm.free()
    obj = new_object(name, mesh)
    obj.data.materials.append(mat)
    return obj


# ── 东方明珠 ──────────────────────────────────────────
def build_oriental_pearl(root):
    body = make_material("LJ_Pearl_Body", (0.62, 0.42, 0.52), metallic=0.45, roughness=0.35)
    glow_low = make_material("LJ_Glow_PearlLow", (1.0, 0.30, 0.55), metallic=0.0, roughness=0.25,
                             emissive=(1.0, 0.25, 0.5), emit_strength=6.0)
    glow_high = make_material("LJ_Glow_PearlHigh", (0.62, 0.45, 1.0), metallic=0.0, roughness=0.25,
                              emissive=(0.5, 0.3, 1.0), emit_strength=6.0)
    glow_small = make_material("LJ_Glow_PearlSmall", (1.0, 0.75, 0.4), metallic=0.0, roughness=0.25,
                               emissive=(1.0, 0.7, 0.3), emit_strength=5.0)

    # 底座
    p = add_cylinder("Pearl_Base", (0, 0, 0.75), 6.0, 1.5, body)
    parent_to(p, root)

    # 三根斜腿：底部半径 5、顶部收拢到 1.2，从地面升到 20
    for k in range(3):
        th = math.radians(k * 120.0)
        r_b, r_t, z_t = 5.0, 1.2, 20.0
        d = Vector(((r_t - r_b) * math.cos(th), (r_t - r_b) * math.sin(th), z_t))
        mid = Vector(((r_b + r_t) * 0.5 * math.cos(th), (r_b + r_t) * 0.5 * math.sin(th), z_t * 0.5))
        leg = add_cylinder("Pearl_Leg%d" % k, tuple(mid), 0.9, d.length, body)
        leg.rotation_euler = d.normalized().to_track_quat('Z', 'Y').to_euler()
        parent_to(leg, root)

    # 下球较低；上球用长细柱拉开距离，避免「洋葱/雪人」感；顶球(太空舱)很小
    s1 = add_sphere("Pearl_BallLow", (0, 0, 21), 5.5, glow_low)        # 15.5~26.5
    parent_to(s1, root)
    c1 = add_cylinder("Pearl_ColLong", (0, 0, 32), 1.0, 11.0, body)    # 26.5~37.5
    parent_to(c1, root)
    s2 = add_sphere("Pearl_BallHigh", (0, 0, 40), 4.2, glow_high)      # 35.8~44.2
    parent_to(s2, root)
    c2 = add_cylinder("Pearl_ColShort", (0, 0, 47), 0.6, 5.0, body)    # 44.5~49.5
    parent_to(c2, root)
    s3 = add_sphere("Pearl_BallSmall", (0, 0, 51), 1.6, glow_small)    # 49.4~52.6
    parent_to(s3, root)
    ant = add_cylinder("Pearl_Antenna", (0, 0, 62), 0.22, 16.0, body)  # 54~70
    parent_to(ant, root)


# ── 上海中心 ──────────────────────────────────────────
def build_shanghai_tower(root):
    glass = make_material("LJ_SH_Glass", (0.32, 0.55, 0.72), metallic=0.35, roughness=0.15)
    glow = make_material("LJ_Glow_SH", (0.4, 0.85, 1.0), metallic=0.0, roughness=0.3,
                         emissive=(0.3, 0.8, 1.0), emit_strength=5.0)

    tower = build_twisted_tower("SH_Tower", 7.0, 4.2, 70.0, 120.0, 10, glass)
    tower.location = (0, 0, 0)
    parent_to(tower, root)

    # 顶部收口 + 尖顶
    cap = add_cylinder("SH_Cap", (0, 0, 71.5), 4.0, 3.0, glass)
    parent_to(cap, root)
    spire = add_cylinder("SH_Spire", (0, 0, 75.0), 0.4, 6.0, glow)
    parent_to(spire, root)

    # 分段发光带（沿高度，模拟幕墙光带）
    for z in (12, 24, 36, 48, 60):
        band = add_cylinder("SH_Band%d" % int(z), (0, 0, z), 6.8 - z * 0.05, 1.1, glow)
        parent_to(band, root)


# ── 环球金融中心（开瓶器） ────────────────────────────
def build_swfc(root):
    steel = make_material("LJ_SWFC_Steel", (0.55, 0.62, 0.72), metallic=0.6, roughness=0.25)
    glow = make_material("LJ_Glow_SWFC", (1.0, 0.85, 0.4), metallic=0.0, roughness=0.3,
                         emissive=(1.0, 0.8, 0.3), emit_strength=5.0)

    body = add_box("SWFC_Body", (0, 0, 27), (16, 10, 54), steel)
    parent_to(body, root)
    # 顶部两侧立柱 + 顶梁，中间留出标志性梯形开口
    pl = add_box("SWFC_PillarL", (-4.4, 0, 56), (3.6, 10, 14), steel)
    parent_to(pl, root)
    pr = add_box("SWFC_PillarR", (4.4, 0, 56), (3.6, 10, 14), steel)
    parent_to(pr, root)
    beam = add_box("SWFC_Beam", (0, 0, 63.5), (16, 10, 2.4), steel)
    parent_to(beam, root)
    # 顶部发光轮廓 + 竖向光带
    crown = add_box("SWFC_Crown", (0, 0, 65.2), (16.4, 10.4, 1.2), glow)
    parent_to(crown, root)
    for x in (-5.5, 0, 5.5):
        strip = add_box("SWFC_Strip%d" % int(x * 10), (x, 0, 27), (0.6, 0.3, 52), glow)
        parent_to(strip, root)


# ── 金茂大厦（层叠塔） ────────────────────────────────
def build_jinmao(root):
    glass = make_material("LJ_JM_Glass", (0.60, 0.55, 0.48), metallic=0.5, roughness=0.2)
    glow = make_material("LJ_Glow_JM", (1.0, 0.65, 0.3), metallic=0.0, roughness=0.3,
                         emissive=(1.0, 0.6, 0.25), emit_strength=5.0)

    tiers = [
        (0,  22, 17.0, 15.0),
        (22, 34, 14.0, 12.0),
        (34, 46, 11.5, 10.0),
        (46, 54, 9.0, 8.0),
    ]
    for i, (z0, z1, w, d) in enumerate(tiers):
        zc = (z0 + z1) * 0.5
        box = add_box("JM_Tier%d" % i, (0, 0, zc), (w, d, z1 - z0), glass)
        parent_to(box, root)
        ring = add_box("JM_Ring%d" % i, (0, 0, z1 + 0.3), (w + 0.5, d + 0.5, 0.6), glow)
        parent_to(ring, root)

    spire = add_cylinder("JM_Spire", (0, 0, 60), 0.5, 12.0, glow)
    parent_to(spire, root)


# ── 导出 FBX ──────────────────────────────────────────
def export(root, filename):
    # 只导出该地标的对象树
    bpy.ops.object.select_all(action='DESELECT')
    stack = [root]
    while stack:
        o = stack.pop()
        o.select_set(True)
        stack.extend(o.children)
    path = os.path.join(OUT, filename)
    bpy.ops.export_scene.fbx(
        filepath=path,
        use_selection=True,
        apply_unit_scale=True,
        use_mesh_modifiers=True,
        axis_forward='-Z',
        axis_up='Y',
    )
    print("导出:", path)


def main():
    clear_scene()

    r = root_empty("LJ_OrientalPearl")
    build_oriental_pearl(r)
    export(r, "OrientalPearl.fbx")

    clear_scene()
    r = root_empty("LJ_ShanghaiTower")
    build_shanghai_tower(r)
    export(r, "ShanghaiTower.fbx")

    clear_scene()
    r = root_empty("LJ_SWFC")
    build_swfc(r)
    export(r, "SWFC.fbx")

    clear_scene()
    r = root_empty("LJ_JinMao")
    build_jinmao(r)
    export(r, "JinMao.fbx")

    print("全部导出完成 ->", OUT)


if __name__ == "__main__":
    main()
