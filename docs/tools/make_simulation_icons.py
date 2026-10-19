"""Placeholder icons of the simulation modules until there is concept art:
the Prime cartridge recoloured, with a glowing rover / helmet symbol in its window.
Usage: python3 make_simulation_icons.py <MeteorMagnet dir>
"""
import colorsys
import sys
from PIL import Image, ImageDraw, ImageFilter

out = sys.argv[1]
WIN = (98, 62, 146, 156)


def recolor(im, hue, sat_scale=1.0):
    """Moves the cyan accents (stripe, LED) of the Prime cartridge to another hue."""
    px = im.load()
    for y in range(im.height):
        for x in range(im.width):
            r, g, b, a = px[x, y]
            h, s, v = colorsys.rgb_to_hsv(r / 255, g / 255, b / 255)
            if a > 0 and s > 0.35 and 0.42 < h < 0.6 and not (WIN[0] <= x <= WIN[2] and WIN[1] <= y <= WIN[3]):
                nr, ng, nb = colorsys.hsv_to_rgb(hue, s * sat_scale, v)
                px[x, y] = (int(nr * 255), int(ng * 255), int(nb * 255), a)
    return im


def cartridge(symbol, color, hue, sat_scale=1.0):
    im = recolor(Image.open(f"{out}/MeteorResonatorPrime.png").convert("RGBA"), hue, sat_scale)
    d = ImageDraw.Draw(im)
    d.rounded_rectangle(WIN, radius=10, fill=(12, 22, 30, 255), outline=(70, 90, 100, 255), width=2)
    glow = Image.new("RGBA", im.size, (0, 0, 0, 0))
    symbol(ImageDraw.Draw(glow), (WIN[0] + WIN[2]) // 2, (WIN[1] + WIN[3]) // 2, color)
    im = Image.alpha_composite(im, glow.filter(ImageFilter.GaussianBlur(4)))
    return Image.alpha_composite(im, glow)


def rover(g, cx, cy, c):
    g.rounded_rectangle((cx - 18, cy - 14, cx + 18, cy + 4), radius=3, outline=c, width=3)
    g.rectangle((cx - 8, cy - 22, cx + 10, cy - 14), outline=c, width=3)
    for x in (cx - 12, cx, cx + 12):
        g.ellipse((x - 5, cy + 6, x + 5, cy + 16), outline=c, width=3)


def helmet(g, cx, cy, c):
    g.ellipse((cx - 19, cy - 22, cx + 19, cy + 16), outline=c, width=3)
    g.rounded_rectangle((cx - 13, cy - 12, cx + 13, cy + 4), radius=6, fill=c)
    g.rectangle((cx - 16, cy + 16, cx + 16, cy + 22), fill=c)


cartridge(rover, (255, 170, 80, 255), 0.07).save(f"{out}/MeteorRoverSimulation.png")
cartridge(helmet, (225, 230, 255, 255), 0.65, 0.2).save(f"{out}/MeteorGearSimulation.png")


def split(g, cx, cy, c):
    """Emergency expansion: one slot splitting into two, with a warning bar."""
    g.rectangle((cx - 7, cy - 30, cx + 7, cy - 12), outline=c, width=3)
    g.line((cx, cy - 12, cx, cy - 2), fill=c, width=3)
    g.line((cx - 12, cy - 2, cx + 12, cy - 2), fill=c, width=3)
    g.rectangle((cx - 19, cy + 2, cx - 5, cy + 20), outline=c, width=3)
    g.rectangle((cx + 5, cy + 2, cx + 19, cy + 20), outline=c, width=3)
    for x in range(cx - 18, cx + 18, 8):
        g.polygon([(x, cy + 34), (x + 4, cy + 26), (x + 8, cy + 26), (x + 4, cy + 34)], fill=c)


cartridge(split, (255, 80, 70, 255), 0.0).save(f"{out}/MeteorEmergencyExpansion.png")


def waves(g, cx, cy, c):
    """Beacon frequency: a mast sending faster and faster waves."""
    g.line((cx, cy + 30, cx, cy - 6), fill=c, width=4)
    for r in (8, 15, 22):
        g.arc((cx - r, cy - 10 - r, cx + r, cy - 10 + r), 200, 340, fill=c, width=3)


def dial(g, cx, cy, c):
    """Beacon tuning: a tuning dial with a needle and a locked range."""
    g.ellipse((cx - 20, cy - 20, cx + 20, cy + 20), outline=c, width=3)
    g.arc((cx - 14, cy - 14, cx + 14, cy + 14), 200, 290, fill=c, width=4)
    g.line((cx, cy, cx - 9, cy - 12), fill=c, width=3)
    g.rectangle((cx - 10, cy + 26, cx + 10, cy + 34), fill=c)


cartridge(waves, (255, 160, 60, 255), 0.08).save(f"{out}/MeteorBeaconFrequency.png")
cartridge(dial, (230, 205, 150, 255), 0.11, 0.5).save(f"{out}/MeteorBeaconCommon.png")


def burst(g, cx, cy, c):
    """Destroyer: a meteor breaking into shards."""
    g.ellipse((cx - 9, cy - 9, cx + 9, cy + 9), outline=c, width=3)
    for dx, dy in ((0, -1), (1, -1), (1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1)):
        g.line((cx + dx * 14, cy + dy * 14, cx + dx * 24, cy + dy * 24), fill=c, width=3)


def arrow(up):
    def draw(g, cx, cy, c):
        s = -1 if up else 1
        g.line((cx, cy - 26 * s, cx, cy + 24 * s), fill=c, width=5)
        g.polygon([(cx, cy + 32 * s), (cx - 14, cy + 14 * s), (cx + 14, cy + 14 * s)], fill=c)
        g.rectangle((cx - 16, cy - 32 * s - 3, cx + 16, cy - 32 * s + 3), fill=c)
    return draw


cartridge(burst, (255, 120, 40, 255), 0.06).save(f"{out}/MeteorDestroyer.png")
cartridge(arrow(True), (110, 255, 140, 255), 0.33).save(f"{out}/MeteorDominant.png")
cartridge(arrow(False), (140, 150, 255, 255), 0.66).save(f"{out}/MeteorRecessive.png")


def egg(g, cx, cy, c):
    """Achievement hunter: Halliday's egg with a key."""
    g.ellipse((cx - 15, cy - 24, cx + 15, cy + 18), outline=c, width=3)
    g.line((cx - 8, cy - 2, cx + 8, cy - 2), fill=c, width=3)
    g.ellipse((cx - 6, cy + 22, cx + 2, cy + 30), outline=c, width=2)
    g.line((cx + 2, cy + 26, cx + 16, cy + 26), fill=c, width=2)
    g.line((cx + 12, cy + 26, cx + 12, cy + 31), fill=c, width=2)


cartridge(egg, (255, 215, 60, 255), 0.13).save(f"{out}/MeteorAchievementHunter.png")
