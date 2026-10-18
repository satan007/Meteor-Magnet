"""Meteor beacon icon, flat like the other building icons, drawn after docs/concept/beacon-chatgpt.png.

Module and core icons are cut from the concept art: cut_module_icons_2.py and cut_core_icon.py.
Usage: python3 make_beacon_icon.py <MeteorMagnet dir>
"""
import sys
from PIL import Image, ImageDraw

out = sys.argv[1]
S = 256

# Meteor beacon (after docs/concept/beacon-chatgpt.png), flat like the other building icons:
# a mast on splayed legs, two dishes, stacked emitter rings and a beam into the sky.
im = Image.new("RGBA", (S, S), (0, 0, 0, 0))
d = ImageDraw.Draw(im)
orange = (255, 140, 60, 255)
light = (255, 205, 160, 255)
d.line((128, 4, 128, 46), fill=light, width=5)                      # beam
for y in (52, 66, 80):                                               # emitter rings
    d.ellipse((108, y - 5, 148, y + 5), outline=orange, width=4)
d.line((128, 48, 128, 92), fill=orange, width=6)
d.line((128, 100, 98, 92), fill=orange, width=5)                    # dishes on arms
d.line((128, 100, 158, 92), fill=orange, width=5)
d.chord((64, 66, 112, 98), 0, 180, fill=light)
d.chord((144, 66, 192, 98), 0, 180, fill=light)
d.rounded_rectangle((114, 92, 142, 206), radius=6, fill=orange)     # mast
d.rectangle((120, 150, 136, 162), fill=light)                       # timer screen
for x0, x1 in ((114, 72), (142, 184)):                              # legs
    d.line((x0, 186, x1, 236), fill=orange, width=9)
d.rectangle((60, 232, 196, 244), fill=orange)                       # foundation
im.save(f"{out}/MeteorBeacon.png")
