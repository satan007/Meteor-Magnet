"""Meteor core icon: the big stone of docs/concept/core-chatgpt.png, background removed.

The background is a smooth grey gradient: pixels close to the local background colour (sampled
along the image border) are dropped, then the largest blob is kept and its holes are filled.
Usage: python3 cut_core_icon.py <concept png> <icon png>
"""
import sys
import numpy as np
from PIL import Image, ImageFilter
from scipy import ndimage

src, dst = sys.argv[1], sys.argv[2]
im = Image.open(src).convert("RGB")
# The big stone only.
crop = im.crop((120, 90, 910, 905))
a = np.asarray(crop).astype(np.float32)
h, w, _ = a.shape

# Background estimate per row: average of the left and right border columns.
left = a[:, :12].mean(axis=1)
right = a[:, -12:].mean(axis=1)
xs = np.linspace(0, 1, w)[None, :, None]
bg = left[:, None, :] * (1 - xs) + right[:, None, :] * xs

diff = np.abs(a - bg).sum(axis=2)
sat = a.max(axis=2) - a.min(axis=2)
mask = (diff > 45) | (sat > 40)
# The soft shadow under the stone is only a little darker than the floor: be stricter near the bottom.
bottom = int(h * 0.75)
mask[bottom:] = (diff[bottom:] > 90) | (sat[bottom:] > 40)
mask = ndimage.binary_opening(mask, iterations=2)
labels, n = ndimage.label(mask)
sizes = ndimage.sum(mask, labels, range(1, n + 1))
mask = labels == (np.argmax(sizes) + 1)
mask = ndimage.binary_fill_holes(mask)
mask = ndimage.binary_closing(mask, iterations=4)

alpha = Image.fromarray((mask * 255).astype(np.uint8)).filter(ImageFilter.GaussianBlur(1.5))
rgba = crop.convert("RGBA")
rgba.putalpha(alpha)
bbox = rgba.getbbox()
rgba = rgba.crop(bbox)
side = max(rgba.size)
canvas = Image.new("RGBA", (side, side), (0, 0, 0, 0))
canvas.alpha_composite(rgba, ((side - rgba.width) // 2, (side - rgba.height) // 2))
canvas.resize((256, 256), Image.LANCZOS).save(dst)
