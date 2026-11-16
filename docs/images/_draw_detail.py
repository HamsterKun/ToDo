"""
用 Pillow 手绘 detail.png，风格与 groups.png 保持一致：
深蓝渐变背景 + 左侧文字区 + 右侧双卡片（主面板 + 详情浮窗）+ 标注线
"""
from PIL import Image, ImageDraw, ImageFont
import os, math

PY = r"C:\Users\jiangchengrui\.workbuddy\binaries\python\envs\default\Scripts\python.exe"
OUT_DIR = r"C:\Users\jiangchengrui\Desktop\DesktopNote\docs\images"

W, H = 1080, 648
img = Image.new("RGB", (W, H), (13, 22, 56))
draw = ImageDraw.Draw(img)

# ── 背景渐变 ──────────────────────────────────────────────
# 用多个半透明椭圆叠加模拟光晕
glow = Image.new("RGB", (W, H), (0, 0, 0))
gd = ImageDraw.Draw(glow)
for r in range(300, 0, -6):
    alpha = int(55 * (1 - r / 300))
    gd.ellipse([(-40 - r, H - 160 - r), (-40 + r, H - 160 + r)],
               fill=(58 + alpha, 13 + alpha//3, 110 + alpha//2))
for r in range(220, 0, -6):
    alpha = int(45 * (1 - r / 220))
    gd.ellipse([(W - 60 - r, -60 - r), (W - 60 + r, -60 + r)],
               fill=(13 + alpha//2, 42 + alpha, 110 + alpha//2))
img = Image.blend(img, glow, 0.55)
draw = ImageDraw.Draw(img)

# ── 字体 ──────────────────────────────────────────────────
def font(size, bold=False):
    names = [
        r"C:\Windows\Fonts\msyhbd.ttc" if bold else r"C:\Windows\Fonts\msyh.ttc",
        r"C:\Windows\Fonts\simhei.ttf",
        r"C:\Windows\Fonts\simsun.ttc",
    ]
    for n in names:
        if os.path.exists(n):
            try:
                return ImageFont.truetype(n, size)
            except:
                pass
    return ImageFont.load_default()

F_TAG   = font(13)
F_H1    = font(44, bold=True)
F_DESC  = font(16)
F_POINT = font(15)
F_SMALL = font(12)
F_UI_S  = font(13)
F_UI_M  = font(14, bold=True)
F_UI_L  = font(18, bold=True)
F_UI_XS = font(12)

C_BG1   = (13, 22, 56)
C_PANEL = (19, 28, 56)
C_HEAD  = (24, 32, 64)
C_ADD   = (26, 34, 68)
C_NOTES = (22, 36, 64)
C_BLUE  = (106, 166, 255)
C_WHITE = (233, 238, 246)
C_GRAY  = (108, 119, 135)
C_MUTED = (61, 79, 110)
C_BORD  = (255, 255, 255)
C_RED   = (255, 123, 114)

def rgba_over(base, color, alpha):
    return tuple(int(b * (1 - alpha) + c * alpha) for b, c in zip(base, color))

def rounded_rect(draw, xy, r, fill, border=None, border_alpha=0.08):
    x0, y0, x1, y1 = xy
    draw.rounded_rectangle([x0, y0, x1, y1], radius=r, fill=fill)
    if border:
        draw.rounded_rectangle([x0, y0, x1, y1], radius=r, outline=border, width=1)

# ══════════════════════════════════════════════════════════
# 左侧文字区
# ══════════════════════════════════════════════════════════
LX = 56

# 标签
draw.text((LX, 86), "DETAIL  &  NOTES", font=F_TAG, fill=(106, 166, 255))

# 大标题（两行）
draw.text((LX, 118), "单击查看详情，", font=F_H1, fill=C_WHITE)
# 第二行"备注"用蓝色
line2_y = 118 + 54
draw.text((LX, line2_y), "备注", font=F_H1, fill=C_BLUE)
w_bz = draw.textlength("备注", font=F_H1)
draw.text((LX + w_bz, line2_y), "随手可写", font=F_H1, fill=C_WHITE)

# 说明文字
dy = line2_y + 60
for line in [
    "点击任意待办，右侧弹出备注浮窗。",
    "多行输入，停止编辑后自动保存，",
    "失焦即关闭，内容永久保留。",
]:
    draw.text((LX, dy), line, font=F_DESC, fill=(139, 150, 168))
    dy += 26

# 要点列表
dy += 14
points = [
    "停止输入 0.8 秒后自动写盘",
    "浮窗贴主面板右侧弹出，不遮内容",
    "点击空白处关闭，备注不会丢失",
    "单击看备注，双击编辑标题时间",
]
for pt in points:
    # 蓝色圆点
    draw.ellipse([(LX, dy + 5), (LX + 7, dy + 12)], fill=C_BLUE)
    draw.text((LX + 16, dy), pt, font=F_POINT, fill=(201, 209, 220))
    dy += 32

# ══════════════════════════════════════════════════════════
# 右侧界面卡片
# ══════════════════════════════════════════════════════════
# 主面板：右侧，宽248，高548
MX0 = 793; MY0 = 50; MW = 248; MH = 548
MX1 = MX0 + MW; MY1 = MY0 + MH

rounded_rect(draw, [MX0, MY0, MX1, MY1], 14, C_PANEL,
             border=rgba_over(C_BG1, C_BORD, 0.08))

# 顶部header
rounded_rect(draw, [MX0, MY0, MX1, MY0 + 56], 14, C_HEAD)
draw.rectangle([MX0, MY0 + 42, MX1, MY0 + 56], fill=C_HEAD)

# 日期块
db_x, db_y = MX0 + 12, MY0 + 10
rounded_rect(draw, [db_x, db_y, db_x + 38, db_y + 42], 7,
             rgba_over(C_HEAD, C_BLUE, 0.12))
draw.text((db_x + 5, db_y + 4), "11月", font=font(10), fill=C_BLUE)
draw.text((db_x + 2, db_y + 18), "16", font=font(20, bold=True), fill=C_WHITE)

draw.text((MX0 + 60, MY0 + 16), "未来日程", font=F_UI_M, fill=C_WHITE)
draw.text((MX0 + 60, MY0 + 36), "共 4 项待办", font=font(11), fill=C_GRAY)
draw.text((MX1 - 20, MY0 + 16), "−", font=font(16), fill=C_MUTED)

# 分割线
draw.line([(MX0, MY0 + 56), (MX1, MY0 + 56)], fill=(255, 255, 255, 13), width=1)

# 添加框
rounded_rect(draw, [MX0 + 10, MY0 + 66, MX1 - 10, MY0 + 90], 7, C_ADD)
draw.text((MX0 + 22, MY0 + 72), "+ 添加新待办…", font=F_UI_XS, fill=C_MUTED)

def section(y, label, count):
    draw.text((MX0 + 14, y), label, font=font(10, bold=True), fill=C_GRAY)
    cw = draw.textlength(label, font=font(10, bold=True))
    draw.line([(MX0 + 14 + cw + 8, y + 6), (MX1 - 24, y + 6)],
              fill=(255, 255, 255), width=1)
    draw.text((MX1 - 20, y), str(count), font=font(10), fill=C_MUTED)
    return y + 22

def row(y, title, chip="", chip_color=None, active=False):
    if active:
        rounded_rect(draw, [MX0 + 4, y - 4, MX1 - 4, y + 24], 7,
                     rgba_over(C_PANEL, C_BORD, 0.055))
    rounded_rect(draw, [MX0 + 14, y + 1, MX0 + 28, y + 15], 4, (0, 0, 0, 0),
                 border=(74, 86, 104))
    draw.text((MX0 + 34, y), title, font=F_UI_S, fill=C_WHITE)
    if chip:
        cc = chip_color or C_GRAY
        draw.text((MX1 - 14 - int(draw.textlength(chip, font=font(11))), y + 1),
                  chip, font=font(11), fill=cc)
    return y + 30

y = MY0 + 100
y = section(y, "长期任务", 1)
y = row(y, "学完吉他 C 大调指法")
y = section(y + 4, "今天", 1)
y = row(y, "去超市买咖啡豆", "18:00", C_BLUE, active=True)
y = section(y + 4, "接下来", 1)
y = row(y, "和朋友打羽毛球", "周六")
y = section(y + 4, "更远", 1)
y = row(y, "写年终总结初稿", "12月1日")

# 底栏
draw.line([(MX0, MY1 - 30), (MX1, MY1 - 30)], fill=(255, 255, 255), width=1)
draw.text((MX0 + 14, MY1 - 22), "4 项待办", font=font(11), fill=C_MUTED)
draw.text((MX1 - 70, MY1 - 22), "已完成 2", font=font(11), fill=C_MUTED)

# ── 详情浮窗 ──────────────────────────────────────────────
DX0 = 530; DY0 = 108; DW = 246; DH = 310
DX1 = DX0 + DW; DY1 = DY0 + DH

# 轻微投影（偏移黑色矩形）
shadow = Image.new("RGB", (W, H), (0, 0, 0))
sd = ImageDraw.Draw(shadow)
rounded_rect(sd, [DX0 + 6, DY0 + 12, DX1 + 6, DY1 + 12], 13, (0, 0, 0))
img = Image.blend(img, shadow, 0.35)
draw = ImageDraw.Draw(img)

rounded_rect(draw, [DX0, DY0, DX1, DY1], 13, C_PANEL,
             border=rgba_over(C_PANEL, C_BLUE, 0.14))

# 浮窗标题栏
draw.text((DX0 + 16, DY0 + 16), "去超市买咖啡豆", font=F_UI_M, fill=C_WHITE)
draw.text((DX0 + 16, DY0 + 36), "今天    18:00", font=font(12), fill=C_BLUE)
# 关闭按钮
rounded_rect(draw, [DX1 - 30, DY0 + 12, DX1 - 12, DY0 + 30], 5,
             rgba_over(C_PANEL, C_BORD, 0.07))
draw.text((DX1 - 24, DY0 + 13), "×", font=font(13), fill=C_GRAY)

# 分割线
draw.line([(DX0 + 14, DY0 + 54), (DX1 - 14, DY0 + 54)],
          fill=(255, 255, 255), width=1)

# 备注区标签
draw.text((DX0 + 16, DY0 + 62), "备注", font=font(11), fill=C_GRAY)

# 备注文本框
rounded_rect(draw, [DX0 + 12, DY0 + 80, DX1 - 12, DY1 - 24], 8, C_NOTES)
note_lines = [
    "埃塞俄比亚耶加雪菲，买 250g 浅烘。",
    "顺便看看有没有手冲滤纸，快用完了。",
    "",
    "如果没货可以去旁边那家精品咖啡，",
    "他家也有同款，价格差不多。",
]
ny = DY0 + 92
for line in note_lines:
    if line:
        draw.text((DX0 + 20, ny), line, font=F_UI_XS, fill=(201, 209, 220))
    ny += 22

# 光标
draw.rectangle([(DX0 + 20, ny - 4), (DX0 + 22, ny + 10)], fill=C_BLUE)

# 已保存提示
saved_w = int(draw.textlength("已保存", font=font(11)))
draw.text((DX1 - 16 - saved_w, DY1 - 20), "已保存", font=font(11),
          fill=rgba_over(C_PANEL, C_BLUE, 0.75))

# ── 连接虚线（高亮行 → 浮窗标题）────────────────────────
# 主面板"去超市"行中点 → 浮窗右边中点
arrow_sx = MX0 - 2       # 从主面板左边出发
arrow_sy = MY0 + 192     # "去超市"行的 y
arrow_ex = DX1 + 2       # 到浮窗右边
arrow_ey = DY0 + 36      # 浮窗标题行 y

# 二阶贝塞尔折线（水平出发，水平到达）
steps = 40
pts = []
for i in range(steps + 1):
    t = i / steps
    # 控制点
    cx1, cy1 = (arrow_sx + arrow_ex) / 2, arrow_sy
    cx2, cy2 = (arrow_sx + arrow_ex) / 2, arrow_ey
    x = (1-t)**3*arrow_sx + 3*(1-t)**2*t*cx1 + 3*(1-t)*t**2*cx2 + t**3*arrow_ex
    y = (1-t)**3*arrow_sy + 3*(1-t)**2*t*cy1 + 3*(1-t)*t**2*cy2 + t**3*arrow_ey
    pts.append((x, y))

# 只画奇数段（虚线效果）
for i in range(0, len(pts) - 1, 2):
    draw.line([pts[i], pts[i+1]], fill=rgba_over(C_BG1, C_BLUE, 0.5), width=2)

# 箭头头部
ex, ey = pts[-1]
draw.polygon([(ex, ey), (ex - 8, ey - 5), (ex - 8, ey + 5)],
             fill=rgba_over(C_BG1, C_BLUE, 0.5))

# ── 存储 ─────────────────────────────────────────────────
out_png = os.path.join(OUT_DIR, "detail.png")
out_jpg = os.path.join(OUT_DIR, "detail.jpg")
img.save(out_png, "PNG")
img.save(out_jpg, "JPEG", quality=95)
print(f"saved: {out_png}")
print(f"saved: {out_jpg}")
print(f"size: {img.size}")
