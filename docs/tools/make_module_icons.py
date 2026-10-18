"""Icons for the advanced modules, the meteor core and the meteor beacon.

Modules: the Prime cartridge recoloured (stripe and LED) with a glowing symbol in its window,
the same look as the cartridges cut from the concept art. Core and beacon: drawn from scratch.
Usage: python3 make_module_icons.py <MeteorMagnet dir>
"""
import colorsys
import math
import sys
from PIL import Image, ImageDraw, ImageFilter

out = sys.argv[1]
S = 256
WIN = (98, 62, 146, 156)


def recolor(im, hue):
    """Moves the cyan accents (stripe, LED) of the Prime cartridge to another hue."""
    px = im.load()
    for y in range(im.height):
        for x in range(im.width):
            r, g, b, a = px[x, y]
            h, s, v = colorsys.rgb_to_hsv(r / 255, g / 255, b / 255)
            if a > 0 and s > 0.35 and 0.42 < h < 0.6 and not (WIN[0] <= x <= WIN[2] and WIN[1] <= y <= WIN[3]):
                nr, ng, nb = colorsys.hsv_to_rgb(hue, s, v)
                px[x, y] = (int(nr * 255), int(ng * 255), int(nb * 255), a)
    return im


def cartridge(symbol, color, hue):
    im = recolor(Image.open(f"{out}/MeteorResonatorPrime.png").convert("RGBA"), hue)
    d = ImageDraw.Draw(im)
    d.rounded_rectangle(WIN, radius=10, fill=(12, 22, 30, 255), outline=(70, 90, 100, 255), width=2)
    glow = Image.new("RGBA", im.size, (0, 0, 0, 0))
    symbol(ImageDraw.Draw(glow), (WIN[0] + WIN[2]) // 2, (WIN[1] + WIN[3]) // 2, color)
    im = Image.alpha_composite(im, glow.filter(ImageFilter.GaussianBlur(4)))
    return Image.alpha_composite(im, glow)


def bolt(g, cx, cy, c):
    g.polygon([(cx + 4, cy - 34), (cx - 14, cy + 4), (cx - 1, cy + 4), (cx - 6, cy + 34), (cx + 14, cy - 6), (cx + 1, cy - 6)], fill=c)


def plus(g, cx, cy, c):
    g.rounded_rectangle((cx - 4, cy - 22, cx + 4, cy + 22), radius=3, fill=c)
    g.rounded_rectangle((cx - 18, cy - 4, cx + 18, cy + 4), radius=3, fill=c)
    g.rectangle((cx - 16, cy - 34, cx + 16, cy - 30), fill=c)
    g.rectangle((cx - 16, cy + 30, cx + 16, cy + 34), fill=c)


def crosshair(g, cx, cy, c):
    g.ellipse((cx - 17, cy - 17, cx + 17, cy + 17), outline=c, width=3)
    g.ellipse((cx - 4, cy - 4, cx + 4, cy + 4), fill=c)
    for dx, dy in ((0, -1), (0, 1), (-1, 0), (1, 0)):
        g.line((cx + dx * 10, cy + dy * 10, cx + dx * 24, cy + dy * 24), fill=c, width=3)


cartridge(bolt, (150, 255, 110, 255), 0.30).save(f"{out}/MeteorPowerSaver.png")
cartridge(plus, (255, 215, 90, 255), 0.13).save(f"{out}/MeteorExpansion.png")
cartridge(crosshair, (255, 110, 100, 255), 0.0).save(f"{out}/MeteorGuidance.png")

# Meteor core: a dark faceted stone with a glowing orange heart and cracks.
im = Image.new("RGBA", (S, S), (0, 0, 0, 0))
glow = Image.new("RGBA", (S, S), (0, 0, 0, 0))
d = ImageDraw.Draw(im)
pts = [(128 + 92 * math.cos(a) * (1 + 0.12 * math.sin(3 * a)), 128 + 92 * math.sin(a) * (1 + 0.1 * math.cos(2 * a)))
       for a in [i * math.pi / 5 for i in range(10)]]
d.polygon(pts, fill=(58, 50, 52, 255), outline=(30, 26, 28, 255))
for i in range(0, 10, 2):
    d.line([pts[i], (128, 128)], fill=(80, 70, 70, 255), width=3)
g = ImageDraw.Draw(glow)
g.ellipse((96, 96, 160, 160), fill=(255, 140, 40, 255))
g.ellipse((112, 112, 144, 144), fill=(255, 230, 160, 255))
for i in range(1, 10, 3):
    g.line([pts[i], (128, 128)], fill=(255, 120, 30, 255), width=4)
im = Image.alpha_composite(im, glow.filter(ImageFilter.GaussianBlur(10)))
im = Image.alpha_composite(im, glow.filter(ImageFilter.GaussianBlur(2)))
im.save(f"{out}/MeteorCore.png")

# Meteor beacon: flat icon like the other buildings - a mast sending rings up, meteors coming down.
im = Image.new("RGBA", (S, S), (0, 0, 0, 0))
d = ImageDraw.Draw(im)
orange = (255, 140, 60, 255)
light = (255, 200, 150, 255)
d.line((128, 236, 128, 140), fill=orange, width=10)
d.polygon([(96, 236), (160, 236), (128, 200)], fill=orange)
d.ellipse((114, 124, 142, 152), fill=light)
for r, w in ((40, 7), (66, 6), (92, 5)):
    d.arc((128 - r, 138 - r, 128 + r, 138 + r), 215, 325, fill=orange, width=w)
for x, y in ((40, 34), (206, 20), (186, 92)):
    d.line((x - 26, y - 26, x, y), fill=light, width=5)
    d.ellipse((x - 9, y - 9, x + 9, y + 9), fill=orange)
im.save(f"{out}/MeteorBeacon.png")
