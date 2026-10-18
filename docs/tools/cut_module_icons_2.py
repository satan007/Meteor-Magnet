"""Icons of the second module row (docs/concept/modules-cartridges-2-chatgpt.png):
Field Stabilizer, Power Saver, Expansion, Guidance. Same geometric mask approach as cut_module_icons.py."""
import sys
from PIL import Image, ImageDraw, ImageFilter
src, out = sys.argv[1], sys.argv[2]
im = Image.open(src).convert('RGB')
names = ['MeteorFieldStabilizer', 'MeteorPowerSaver', 'MeteorExpansion', 'MeteorGuidance']
# Cartridge outline in crop coordinates (crop 350x600, top at y=210), measured on the first cartridge.
body = [(45, 8), (305, 8), (305, 20), (318, 28), (330, 50), (330, 500), (318, 510), (290, 515), (290, 588),
        (50, 588), (50, 515), (22, 510), (10, 500), (10, 50), (20, 28), (45, 20)]
for i, name in enumerate(names):
    x0 = 40 + 365 * i
    crop = im.crop((x0, 210, x0 + 350, 810))
    mask = Image.new('L', crop.size, 0)
    ImageDraw.Draw(mask).polygon(body, fill=255)
    mask = mask.filter(ImageFilter.GaussianBlur(0.8))
    tile = crop.convert('RGBA')
    tile.putalpha(mask)
    tile = tile.crop(mask.getbbox())
    scale = 240 / max(tile.size)
    tile = tile.resize((round(tile.width * scale), round(tile.height * scale)), Image.LANCZOS)
    canvas = Image.new('RGBA', (256, 256), (0, 0, 0, 0))
    canvas.paste(tile, ((256 - tile.width) // 2, (256 - tile.height) // 2), tile)
    canvas.save(f'{out}/{name}.png')
