# -*- coding: utf-8 -*-
"""
用 Blender 渲染从 Unity 导出的场景（Export/scene.obj）。

用法：
  blender.exe --background --python Blender/render_scene.py

输出：Blender/renders/*.png
"""
import math
import os
import sys

import bpy
from mathutils import Vector

HERE = os.path.dirname(os.path.abspath(__file__))
PROJECT = os.path.dirname(HERE)
OBJ_PATH = os.path.join(PROJECT, "Export", "scene.obj")
OUT_DIR = os.path.join(HERE, "renders")

RES_X, RES_Y = 1920, 1080


def clear_scene():
    bpy.ops.wm.read_factory_settings(use_empty=True)


def import_obj():
    # Unity 是 Y 轴向上，导入时转换为 Blender 的 Z 轴向上
    bpy.ops.wm.obj_import(
        filepath=OBJ_PATH,
        forward_axis='NEGATIVE_Z',
        up_axis='Y',
    )
    imported = [o for o in bpy.context.selected_objects]
    print("导入对象数：%d" % len(imported))
    return imported


def setup_world():
    world = bpy.data.worlds.new("Sky")
    bpy.context.scene.world = world
    world.use_nodes = True
    bg = world.node_tree.nodes["Background"]
    bg.inputs[0].default_value = (0.30, 0.45, 0.68, 1.0)   # 天蓝
    bg.inputs[1].default_value = 0.44


def setup_lights():
    # 主光：暖色斜射，模拟午后
    sun_data = bpy.data.lights.new("Sun", type='SUN')
    sun_data.energy = 2.1
    sun_data.angle = math.radians(1.6)
    sun_data.color = (1.0, 0.95, 0.86)
    sun = bpy.data.objects.new("Sun", sun_data)
    sun.rotation_euler = (math.radians(52), 0, math.radians(-42))
    bpy.context.collection.objects.link(sun)

    # 补光：冷色，压低阴影
    fill_data = bpy.data.lights.new("Fill", type='SUN')
    fill_data.energy = 0.5
    fill_data.color = (0.72, 0.82, 1.0)
    fill = bpy.data.objects.new("Fill", fill_data)
    fill.rotation_euler = (math.radians(64), 0, math.radians(140))
    bpy.context.collection.objects.link(fill)


def setup_render():
    scene = bpy.context.scene
    scene.render.resolution_x = RES_X
    scene.render.resolution_y = RES_Y
    scene.render.film_transparent = False
    scene.render.image_settings.file_format = 'PNG'

    engines = []
    try:
        engines = [e.bl_idname for e in bpy.types.RenderEngine.__subclasses__()]
    except Exception:
        pass

    # 优先 EEVEE（快），失败退回 Cycles
    for name in ('CYCLES', 'BLENDER_EEVEE_NEXT', 'BLENDER_EEVEE'):
        try:
            scene.render.engine = name
            break
        except Exception:
            continue
    print("渲染引擎：%s" % scene.render.engine)

    if scene.render.engine == 'CYCLES':
        scene.cycles.samples = 128
        scene.cycles.use_denoising = True
        scene.cycles.max_bounces = 6
        scene.cycles.diffuse_bounces = 4
        scene.cycles.glossy_bounces = 3
        scene.cycles.transmission_bounces = 2
        scene.cycles.use_adaptive_sampling = True
        scene.cycles.adaptive_threshold = 0.02
        try:
            scene.cycles.use_fast_gi = True
        except Exception:
            pass
    else:
        try:
            scene.eevee.taa_render_samples = 160
        except Exception:
            pass
        # 开启光追与阴影柔化，提升画面质感
        for attr, value in (('use_raytracing', True), ('use_shadows', True), ('use_volumetric_shadows', False)):
            try:
                setattr(scene.eevee, attr, value)
            except Exception:
                pass
        try:
            scene.eevee.shadow_ray_count = 4
            scene.eevee.shadow_step_count = 8
        except Exception:
            pass
    # 用 Standard 保留模型原本的配色（Filmic/AgX 会明显去饱和）
    for tf in ('Standard', 'Filmic'):
        try:
            scene.view_settings.view_transform = tf
            break
        except Exception:
            continue
    scene.view_settings.look = 'None'
    scene.view_settings.exposure = 0.0


def add_room_lights(lights):
    """室内镜头补光：OBJ 不携带灯光，室内被屋顶遮挡后需要人工布光"""
    for i, (loc, energy, color) in enumerate(lights):
        data = bpy.data.lights.new("RoomLight_%d" % i, type='AREA')
        data.energy = energy
        data.size = 2.2
        data.color = color
        obj = bpy.data.objects.new("RoomLight_%d" % i, data)
        obj.location = Vector(loc)
        obj.rotation_euler = (0.0, 0.0, 0.0)   # 朝下照明
        bpy.context.collection.objects.link(obj)


def make_camera(name, location, target, lens=45.0):
    cam_data = bpy.data.cameras.new(name)
    cam_data.lens = lens
    cam = bpy.data.objects.new(name, cam_data)
    cam.location = Vector(location)
    bpy.context.collection.objects.link(cam)

    direction = Vector(target) - Vector(location)
    # 让相机朝向目标
    rot_quat = direction.to_track_quat('-Z', 'Y')
    cam.rotation_euler = rot_quat.to_euler()
    return cam


def render_view(cam, filename):
    bpy.context.scene.camera = cam
    path = os.path.join(OUT_DIR, filename)
    bpy.context.scene.render.filepath = path
    bpy.ops.render.render(write_still=True)
    print("已渲染：%s" % path)


def main():
    os.makedirs(OUT_DIR, exist_ok=True)

    clear_scene()
    import_obj()
    setup_world()
    setup_lights()
    setup_render()

    # 坐标系：Blender X = Unity X，Blender Y = -Unity Z，Blender Z = Unity Y
    # 坐标系换算：Blender X = Unity X，Blender Y = -Unity Z，Blender Z = Unity Y
    # 因此"住宅南侧"对应 Blender 的 +Y 方向
    # 每条：(名称, 机位, 目标, 焦距, 室内补光列表)
    warm = (1.0, 0.92, 0.8)
    cool = (0.85, 0.9, 1.0)
    views = [
        # 1. 小区鸟瞰：从南侧高处俯视整个住宅区
        ("01_小区鸟瞰", (58, 40, 44), (20, 0, 1), 40, None),
        # 2. 住宅外观：从马路一侧看临街第一排的正立面
        ("02_住宅外观", (24, 30, 6.5), (26, -2, 2.4), 42, None),
        # 3. 小区内街：沿两排楼之间的步道向东看
        ("03_小区内街", (4, -9, 2.6), (46, -9, 1.8), 38, None),
        # 4. 室内厨房：从入户门看向橱柜台面（补室内灯）
        ("04_室内厨房", (12.9, 6.5, 1.65), (9.8, 1.8, 1.05), 30,
         [((11.0, 4.0, 2.6), 105, warm), ((9.0, 5.6, 2.5), 58, warm), ((13.2, 2.4, 2.4), 52, cool)]),
        # 5. 办公区同事（补室内灯）
        ("05_办公区同事", (-15.2, 3.2, 1.7), (-17.2, -0.6, 1.1), 34,
         [((-15.5, 1.2, 2.6), 118, warm), ((-18.2, -2.2, 2.5), 62, warm)]),
        # 7. 客厅布置：沙发正对电视，茶几居中
        ("07_客厅布置", (3.15, 1.62, 2.05), (8.7, 3.2, 0.85), 21,
         [((5.6, 3.2, 2.6), 92, warm), ((8.6, 3.4, 2.5), 62, cool)]),
        # 8. 卧室布置：床头靠墙，床头柜贴床头，衣柜靠墙
        ("08_卧室布置", (13.6, -0.4, 1.95), (9.6, -3.6, 0.85), 30,
         [((11.0, -2.0, 2.55), 92, warm), ((12.6, -4.2, 2.45), 48, cool)]),
        # 9. 员工宿舍：床、衣柜、书桌（玩家住处，可睡觉）
        ("09_员工宿舍", (-26.2, 4.6, 2.05), (-28.8, -2.4, 0.9), 24,
         [((-27.5, 1.0, 2.55), 105, warm), ((-26.0, -2.0, 2.45), 58, cool)]),
        # 6. 办公区全景（补室内灯）
        ("06_办公区全景", (-14, 6.6, 3.6), (-14, -6, 1.0), 32,
         [((-14, 2.0, 2.65), 150, warm), ((-16.8, -3.2, 2.55), 78, cool), ((-11.2, -3.2, 2.55), 78, cool)]),
    ]

    # 支持只渲染指定镜头：blender ... --python render_scene.py -- 07
    ARG_FILTER = []
    if '--' in sys.argv:
        ARG_FILTER = sys.argv[sys.argv.index('--') + 1:]

    for name, loc, target, lens, room_lights in views:
        if ARG_FILTER and not any(f in name for f in ARG_FILTER):
            continue
        if room_lights:
            add_room_lights(room_lights)
        cam = make_camera(name, loc, target, lens)
        render_view(cam, name + ".png")

    print("全部完成，输出目录：%s" % OUT_DIR)


main()
