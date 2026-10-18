import sys
from PIL import Image, ImageDraw, ImageFilter
src, out = sys.argv[1], sys.argv[2]
im = Image.open(src).convert('RGB')
names = ['MeteorResonatorPrime','MeteorResonatorHumble','MeteorResonatorSelenea','MeteorResonatorToxicity',
         'MeteorResonatorAqualis','MeteorResonatorSkeo','MeteorSpaceLink']
# Cartridge outline measured on the concept art (crop coordinates, crop top at y=198).
body = [(28,6),(198,6),(198,20),(212,24),(217,40),(217,370),(212,378),(192,380),(192,430),(30,430),(30,380),
        (8,378),(2,370),(2,40),(6,24),(28,20)]
for i, name in enumerate(names):
    x0 = 38 + 246 * i
    top = 95 if i == 6 else 198
    dy = 198 - top
    crop = im.crop((x0, top, x0 + 226, 648))
    mask = Image.new('L', crop.size, 0)
    d = ImageDraw.Draw(mask)
    d.polygon([(x, y + dy) for x, y in body], fill=255)
    if i == 6:
        # Space Link: antennas, knob and the top box above the casing.
        d.rectangle((84, 8, 116, 118), fill=255)
        d.rectangle((118, 80, 170, 118), fill=255)
        d.rectangle((58, 100, 195, 132), fill=255)
    mask = mask.filter(ImageFilter.GaussianBlur(0.8))
    tile = crop.convert('RGBA')
    tile.putalpha(mask)
    tile = tile.crop(mask.getbbox())
    scale = 240 / max(tile.size)
    tile = tile.resize((round(tile.width * scale), round(tile.height * scale)), Image.LANCZOS)
    canvas = Image.new('RGBA', (256, 256), (0, 0, 0, 0))
    canvas.paste(tile, ((256 - tile.width) // 2, (256 - tile.height) // 2), tile)
    canvas.save(f'{out}/{name}.png')
