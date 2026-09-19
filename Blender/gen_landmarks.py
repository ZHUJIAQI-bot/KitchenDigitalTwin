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


def add_sphere(name, location, radius, mat, segments=64, ring_count=32):
    bpy.ops.mesh.primitive_uv_sphere_add(segments=segments, ring_count=ring_count, radius=radius, location=location)
    obj = bpy.context.object
    obj.name = name
    obj.data.materials.append(mat)
    return obj


def add_torus(name, location, major_r, minor_r, mat, rotation=(0, 0, 0)):
    bpy.ops.mesh.primitive_torus_add(major_radius=major_r, minor_radius=minor_r,
                                    major_segments=20, minor_segments=5, location=location)
    obj = bpy.context.object
    obj.name = name
    obj.rotation_euler = rotation
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


# ── 东方明珠（按真实参数缩放，S=0.15：468m → 70.2 游戏单位） ──
def build_sphere_grid(root, name, center_z, radius, grid_mat):
    """球面几何窗格：24 条经线 + 8 条纬线（线框，非贴图）"""
    fr = 0.32  # 窗框线径（游戏单位，略夸张保证远景可见）
    for i in range(1, 6):
        lat = math.radians(-60.0 + i * 30.0)
        r = radius * math.cos(lat)
        z = center_z + radius * math.sin(lat)
        parent_to(add_torus(name + "_Lat%02d" % i, (0, 0, z), r, fr, grid_mat), root)
    for i in range(12):
        lon = math.radians(i * 30.0)
        parent_to(add_torus(name + "_Mer%02d" % i, (0, 0, center_z), radius, fr, grid_mat,
                            rotation=(math.pi * 0.5, 0, lon)), root)


def build_antenna(root, z0, z1, r_bot, r_top, white_mat, red_mat, tip_mat):
    """天线桅杆：红白分段 + 顶部红色警示灯"""
    segs = 6
    seg_h = (z1 - z0) / segs
    for i in range(segs):
        z_c = z0 + seg_h * (i + 0.5)
        r = r_bot + (r_top - r_bot) * ((i + 0.5) / segs)
        parent_to(add_cylinder("Antenna_Seg%02d" % i, (0, 0, z_c), r, seg_h,
                               red_mat if i % 2 == 0 else white_mat), root)
    parent_to(add_sphere("Antenna_TipLight", (0, 0, z1), 0.3, tip_mat, segments=16, ring_count=8), root)


def build_oriental_pearl(root):
    S = 0.15  # 真实米 → 游戏单位

    body = make_material("LJ_Glow_PearlBody", (0.60, 0.63, 0.68), metallic=0.3, roughness=0.35)
    grid = make_material("LJ_Pearl_Grid", (0.38, 0.40, 0.45), metallic=0.35, roughness=0.4)
    glow_low = make_material("LJ_Glow_PearlLow", (0.85, 0.35, 0.55), metallic=0.0, roughness=0.05,
                             emissive=(0.95, 0.30, 0.60), emit_strength=3.0)
    glow_high = make_material("LJ_Glow_PearlHigh", (0.62, 0.42, 0.85), metallic=0.0, roughness=0.05,
                              emissive=(0.55, 0.35, 0.90), emit_strength=3.0)
    glow_cap = make_material("LJ_Glow_PearlSmall", (0.92, 0.62, 0.40), metallic=0.0, roughness=0.05,
                             emissive=(1.0, 0.65, 0.35), emit_strength=3.0)
    ant_white = make_material("LJ_Pearl_AntennaWhite", (0.86, 0.86, 0.87), metallic=0.2, roughness=0.4)
    ant_red = make_material("LJ_Pearl_AntennaRed", (0.72, 0.13, 0.11), metallic=0.2, roughness=0.4)
    tip_light = make_material("LJ_Glow_AntennaTip", (1.0, 0.08, 0.04), metallic=0.0, roughness=0.3,
                              emissive=(1.0, 0.04, 0.02), emit_strength=8.0)

    # 三根擎天柱：r=4.5m，Z=0~250m，半径20m圆，90°/210°/330°
    for k in range(3):
        th = math.radians(90.0 + k * 120.0)
        x = 20 * S * math.cos(th)
        y = 20 * S * math.sin(th)
        parent_to(add_cylinder("Pillar_%02d" % (k + 1), (x, y, 250 * S * 0.5), 8 * S, 250 * S, body), root)

    # 三根斜柱：r=3.5m，底部半径35m Z=0，顶部连下球边缘 Z=68m，30°/150°/270°
    for k in range(3):
        th = math.radians(30.0 + k * 120.0)
        p_bot = Vector((35 * S * math.cos(th), 35 * S * math.sin(th), 0.0))
        p_top = Vector((25 * S * math.cos(th), 25 * S * math.sin(th), 68 * S))
        d = p_top - p_bot
        mid = (p_bot + p_top) * 0.5
        leg = add_cylinder("Diagonal_%02d" % (k + 1), tuple(mid), 6 * S, d.length, body)
        leg.rotation_euler = d.normalized().to_track_quat('Z', 'Y').to_euler()
        parent_to(leg, root)

    # 三个球体：下球 r=25m@68m、上球 r=22.5m@250m、太空舱 r=8m@350m
    parent_to(add_sphere("Sphere_Lower", (0, 0, 68 * S), 25 * S, glow_low), root)
    parent_to(add_sphere("Sphere_Upper", (0, 0, 250 * S), 22.5 * S, glow_high), root)
    parent_to(add_sphere("Capsule", (0, 0, 350 * S), 8 * S, glow_cap), root)

    # 上球到太空舱之间的连接颈柱（真实东方明珠有这段，之前漏了）
    parent_to(add_cylinder("Pearl_Neck", (0, 0, 46), 0.8, 10.4, body), root)

    # 球面窗格（下球/上球各 24 经线 × 8 纬线）
    build_sphere_grid(root, "WindowGrid_Lower", 68 * S, 25 * S, grid)
    build_sphere_grid(root, "WindowGrid_Upper", 250 * S, 22.5 * S, grid)

    # 天线：Z=350m~468m，底 r=3m 顶 r=0.3m，红白涂装 + 红色警示灯
    build_antenna(root, 350 * S, 468 * S, 3 * S, 0.3 * S, ant_white, ant_red, tip_light)


# ── 上海中心 ──────────────────────────────────────────
def build_shanghai_tower(root):
    glass = make_material("LJ_SH_Glass", (0.25, 0.58, 0.85), metallic=0.5, roughness=0.06)
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
    steel = make_material("LJ_SWFC_Steel", (0.72, 0.80, 0.88), metallic=0.7, roughness=0.12)
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
    glass = make_material("LJ_JM_Glass", (0.78, 0.62, 0.36), metallic=0.6, roughness=0.10)
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
