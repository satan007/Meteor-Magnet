"""Icons of the third and fourth module rows and the meteor blueprint.

Cartridges: the outline from cut_module_icons_2.py, scaled to each cartridge's box.
Usage: python3 cut_module_icons_3.py <docs/concept dir> <MeteorMagnet dir> [preview png]
"""
import sys
from PIL import Image, ImageDraw, ImageFilter

concept, out = sys.argv[1], sys.argv[2]
preview = sys.argv[3] if len(sys.argv) > 3 else None

# Outline of cut_module_icons_2.py, normalised to the cartridge box (0..1).
BODY = [(45, 8), (305, 8), (305, 20), (318, 28), (330, 50), (330, 500), (318, 510), (290, 515), (290, 588),
        (50, 588), (50, 515), (22, 510), (10, 500), (10, 50), (20, 28), (45, 20)]
NORM = [((x - 10) / 320, (y - 8) / 580) for x, y in BODY]

ROWS = [
    ("modules-cartridges-3-chatgpt.webp", 197, 727, 300, [40, 389, 732, 1074, 1418],
     ["MeteorRoverSimulation", "MeteorGearSimulation", "MeteorEmergencyExpansion", "MeteorBeaconFrequency", "MeteorBeaconCommon"]),
    ("modules-cartridges-4-chatgpt.webp", 215, 805, 330, [45, 418, 783, 1153],
     ["MeteorDestroyer", "MeteorDominant", "MeteorRecessive", "MeteorAchievementHunter"]),
]


def save_icon(tile, mask, name):
    tile = tile.convert("RGBA")
    tile.putalpha(mask)
    tile = tile.crop(mask.getbbox())
    scale = 240 / max(tile.size)
    tile = tile.resize((round(tile.width * scale), round(tile.height * scale)), Image.LANCZOS)
    canvas = Image.new("RGBA", (256, 256), (0, 0, 0, 0))
    canvas.paste(tile, ((256 - tile.width) // 2, (256 - tile.height) // 2), tile)
    canvas.save(f"{out}/{name}.png")
    return canvas


icons = []
for file, top, bottom, width, lefts, names in ROWS:
    im = Image.open(f"{concept}/{file}").convert("RGB")
    for left, name in zip(lefts, names):
        box = (left - 4, top - 4, left + width + 4, bottom + 4)
        crop = im.crop(box)
        mask = Image.new("L", crop.size, 0)
        ImageDraw.Draw(mask).polygon([(4 + x * width, 4 + y * (bottom - top)) for x, y in NORM], fill=255)
        icons.append(save_icon(crop, mask.filter(ImageFilter.GaussianBlur(0.8)), name))

# Meteor blueprint: the background (and the soft shadow, a smooth gradient) is flood-filled from the edges,
# spreading only between neighbours of nearly the same colour; the stone and the metal have sharp edges.
im = Image.open(f"{concept}/blueprint-chatgpt.webp").convert("RGB")
crop = im.crop((200, 100, 1400, 930))
w, h = crop.size
px = crop.load()
bgmask = [[False] * w for _ in range(h)]
stack = [(x, y) for x in range(w) for y in (0, h - 1)] + [(x, y) for y in range(h) for x in (0, w - 1)]
for x, y in stack:
    bgmask[y][x] = True
while stack:
    x, y = stack.pop()
    r, g, b = px[x, y]
    for nx, ny in ((x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1)):
        if 0 <= nx < w and 0 <= ny < h and not bgmask[ny][nx]:
            r2, g2, b2 = px[nx, ny]
            if abs(r - r2) + abs(g - g2) + abs(b - b2) <= 9 and max(r2, g2, b2) - min(r2, g2, b2) < 30 and r2 + g2 + b2 >= 200:
                bgmask[ny][nx] = True
                stack.append((nx, ny))
mask = Image.new("L", crop.size, 0)
mp = mask.load()
for y in range(h):
    for x in range(w):
        if not bgmask[y][x]:
            mp[x, y] = 255
mask = mask.filter(ImageFilter.MinFilter(3)).filter(ImageFilter.MaxFilter(3)).filter(ImageFilter.GaussianBlur(1))
icons.append(save_icon(crop, mask, "MeteorBlueprint"))

if preview:
    sheet = Image.new("RGBA", (256 * len(icons), 256), (40, 90, 40, 255))
    for i, ic in enumerate(icons):
        sheet.alpha_composite(ic, (256 * i, 0))
    sheet.save(preview)
