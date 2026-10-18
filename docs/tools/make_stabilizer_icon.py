"""Field Stabilizer icon: the Prime cartridge with a gyroscope of glowing rings in its window."""
import sys
from PIL import Image, ImageDraw, ImageFilter

src, dst = sys.argv[1], sys.argv[2]
im = Image.open(src).convert("RGBA")
w, h = im.size

# Window of the cartridge (the glass capsule), measured on the 256 px Prime icon.
win = (98, 62, 146, 156)
d = ImageDraw.Draw(im)
d.rounded_rectangle(win, radius=10, fill=(12, 26, 34, 255), outline=(70, 90, 100, 255), width=2)

glow = Image.new("RGBA", im.size, (0, 0, 0, 0))
g = ImageDraw.Draw(glow)
cx, cy = (win[0] + win[2]) // 2, (win[1] + win[3]) // 2
cyan = (90, 230, 255, 255)
# Three rings at different tilts, like a gyroscope.
g.ellipse((cx - 19, cy - 32, cx + 19, cy + 32), outline=cyan, width=3)
g.ellipse((cx - 21, cy - 10, cx + 21, cy + 10), outline=cyan, width=3)
g.ellipse((cx - 12, cy - 26, cx + 12, cy + 26), outline=(170, 245, 255, 255), width=2)
g.ellipse((cx - 5, cy - 5, cx + 5, cy + 5), fill=(220, 255, 255, 255))
blur = glow.filter(ImageFilter.GaussianBlur(4))
im = Image.alpha_composite(im, blur)
im = Image.alpha_composite(im, glow)
im.save(dst)
