# -*- coding: utf-8 -*-
"""生成任务命题文档用的流程图（宋体/黑体，2倍图，可直接插入 Word）。
坐标一律用「未缩放单位」，绘制时统一乘 S，避免混用导致折行错位。"""
from PIL import Image, ImageDraw, ImageFont

FONT = "C:/Windows/Fonts/simhei.ttf"
S = 2
INK = (30, 36, 44)
LINE = (88, 98, 110)
MAIN = (230, 239, 248)
ACC = (210, 231, 218)
WARN = (251, 231, 214)
RED = (245, 212, 212)
GREY = (238, 238, 241)
BLUE = (90, 120, 150)
LSZ = 1.5          # 行高倍数


def F(sz, big=True):
    return ImageFont.truetype(FONT, int(sz * (S if big else 1)))


def wrap(d, text, font, maxw):
    out, cur = [], ""
    for ch in text:
        if d.textlength(cur + ch, font=font) <= maxw:
            cur += ch
        else:
            out.append(cur)
            cur = ch
    if cur:
        out.append(cur)
    return out or [""]


_probe = ImageDraw.Draw(Image.new("RGB", (1, 1)))


def text_h(text, fsz, maxw):
    """该文本在给定宽度下需要的高度（未缩放单位）。"""
    n = len(wrap(_probe, text, F(fsz, False), maxw))
    return max(56, int(n * fsz * LSZ + 26))


class Fig:
    def __init__(self, w, h):
        self.W, self.H = w, h
        self.img = Image.new("RGB", (int(w * S), int(h * S)), "white")
        self.d = ImageDraw.Draw(self.img)

    def box(self, x, y, w, h, text, fill=MAIN, fsz=17):
        d = self.d
        d.rounded_rectangle([x * S, y * S, (x + w) * S, (y + h) * S],
                            radius=7 * S, fill=fill, outline=LINE, width=2 * S)
        lines = wrap(d, text, F(fsz, False), (w - 30))
        big = F(fsz)
        lh = fsz * LSZ
        ty = y + (h - len(lines) * lh) / 2
        for ln in lines:
            tw = d.textlength(ln, font=big)
            d.text(((x + w / 2) * S - tw / 2, ty * S), ln, font=big, fill=INK)
            ty += lh

    def hline(self, x1, x2, y, color=LINE):
        self.d.line([x1 * S, y * S, x2 * S, y * S], fill=color, width=2 * S)

    def vline(self, x, y1, y2, color=LINE):
        self.d.line([x * S, y1 * S, x * S, y2 * S], fill=color, width=2 * S)

    def head(self, x, y, dirn, color=LINE):
        k = 7
        pts = {"down": [(x - k, y - k), (x + k, y - k), (x, y)],
               "up": [(x - k, y + k), (x + k, y + k), (x, y)],
               "right": [(x - k, y - k), (x - k, y + k), (x, y)],
               "left": [(x + k, y - k), (x + k, y + k), (x, y)]}[dirn]
        self.d.polygon([(a * S, b * S) for a, b in pts], fill=color)

    def arrow_v(self, x, y1, y2, color=LINE):
        self.vline(x, y1, y2, color)
        self.head(x, y2, "down", color)

    def arrow_h(self, x1, x2, y, color=LINE):
        self.hline(x1, x2, y, color)
        self.head(x2, y, "right", color)

    def label(self, x, y, text, fsz=14, color=(170, 60, 50)):
        self.d.text((x * S, y * S), text, font=F(fsz), fill=color)

    def save(self, path):
        self.img.save(path)
        print("saved", path)


def vertical(steps, bw, x0=24, top=28, gap=40, fsz=17, pad_r=0):
    """竖直主流程；返回 (fig, 每格中心y, 每格高度, 底部y)"""
    hs = [text_h(t, fsz, bw - 30) for t, _ in steps]
    total = top + sum(hs) + gap * (len(hs) - 1)
    fig = Fig(x0 + bw + pad_r, total + 34)
    y = top
    cy = []
    for i, ((t, fl), h) in enumerate(zip(steps, hs)):
        fig.box(x0, y, bw, h, t, fl, fsz)
        cy.append(y + h / 2)
        y += h
        if i < len(hs) - 1:
            fig.arrow_v(x0 + bw / 2, y, y + gap)
            y += gap
    return fig, cy, hs, y


# ══ 图1 数据闭环 ══
steps1 = [("传感器实时采集各点位指标", MAIN),
          ("比对报警阈值，连续超限才确认报警", GREY),
          ("生成报警工单，定位隐患点位", MAIN),
          ("上门处置：判断成因，按序执行工序", ACC),
          ("读数回落至正常区间", MAIN),
          ("持续正常达规定观察期", GREY),
          ("判定闭环验收，写入整改简报", ACC)]
BW = 470
fig, cy, hs, _ = vertical(steps1, BW, pad_r=250)
sy = cy[1] - 26
fig.box(24 + BW + 44, sy, 176, 52, "未超限：持续监测", GREY, 14)
fig.arrow_h(24 + BW, 24 + BW + 44, sy + 26)
fig.hline(24 + BW + 44 + 176, 24 + BW + 232, sy + 26)
fig.vline(24 + BW + 232, sy + 26, cy[0])
fig.arrow_h(24 + BW + 232, 24 + BW, cy[0])
fig.label(24 + BW + 50, sy - 22, "未超限", 13, BLUE)
fig.save("doc/figures/fig1_数据闭环.png")

# ══ 图2 处置交互 ══
steps2 = [("走到隐患点位，查看现场现象与实时读数", MAIN),
          ("从候选中判断隐患成因", ACC),
          ("按正确作业顺序选取处置工序", ACC),
          ("全部工序按序完成", GREY),
          ("完工结算，记入已掌握清单", ACC)]
BW2 = 430
fig, cy, hs, _ = vertical(steps2, BW2, pad_r=300, gap=44)
# 负反馈分支：必须对齐到其「来源格」的中心，避免指错方框
neg = [("成因判断错误：进度回退，重新判断", WARN, 1, 0),
       ("顺序错误：返工，回退最近一步", WARN, 2, -26),
       ("危险操作：触发事故，赔付并重做", RED, 2, 26)]
for txt, fl, src, dy in neg:
    bh = 46
    fig.box(24 + BW2 + 46, cy[src] + dy - bh / 2, 244, bh, txt, fl, 13)
    fig.arrow_h(24 + BW2, 24 + BW2 + 46, cy[src] + dy)
fig.save("doc/figures/fig2_处置交互.png")

# ══ 图3 工单状态流转 ══
sts = [("待维修", GREY), ("维修中", MAIN), ("已完工", MAIN), ("观察期", ACC), ("已闭环", ACC)]
BW3, BH3, G3, X0, Y0 = 124, 58, 34, 28, 40
wid = X0 * 2 + len(sts) * BW3 + G3 * (len(sts) - 1)
fig = Fig(wid, 250)
x = X0
cxs = []
for i, (t, fl) in enumerate(sts):
    fig.box(x, Y0, BW3, BH3, t, fl, 15)
    cxs.append(x + BW3 / 2)
    x += BW3
    if i < len(sts) - 1:
        fig.arrow_h(x, x + G3, Y0 + BH3 / 2)
        x += G3
fb = Y0 + BH3 + 56
fig.hline(cxs[0], cxs[3], fb, LINE)
fig.vline(cxs[3], Y0 + BH3, fb, LINE)
fig.vline(cxs[0], fb, Y0 + BH3 + 2, LINE)
fig.head(cxs[0], Y0 + BH3, "up", LINE)
fig.label(cxs[0] + 34, fb + 6, "复测不合格：重新打开", 13, (170, 60, 50))
# 说明放在回流线下方，避免被竖线穿过
fig.label(X0, fb + 34, "读数回落并持续正常方可判定闭环；未达复测条件的工单保持待处理", 13, INK)
fig.save("doc/figures/fig3_工单状态流转.png")
