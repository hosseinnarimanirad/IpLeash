"""Draws the IpLeash app icon (white shield on the accent blue) at 1024 px and writes an .icns.

The Windows .ico tops out at 64 px, which is too small for a Dock icon, so the same design is
redrawn here. Run: python3 make-icon.py <output.icns>
"""
import os, subprocess, sys, tempfile
from PIL import Image, ImageDraw

S = 1024
ACCENT = (37, 99, 235, 255)

def draw(size):
    scale = 4  # supersample, then downsize for smooth edges
    n = size * scale
    img = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    inset = n * 100 // 1024          # macOS icon grid: 824/1024 body
    d.rounded_rectangle([inset, inset, n - inset, n - inset], radius=n * 185 // 1024, fill=ACCENT)
    cx, top, w, h = n / 2, n * 0.30, n * 0.36, n * 0.44
    pts = []
    # Flat top with rounded shoulders, straight sides, curving to a point at the bottom.
    for i in range(0, 101):
        t = i / 100
        x = cx - w / 2 + w * t
        y = top + (n * 0.025) * (1 - (2 * t - 1) ** 2) * -1
        pts.append((x, y))
    for i in range(0, 101):
        t = i / 100
        x = cx + (w / 2) * (1 - t ** 1.8)
        y = top + h * 0.38 + (h * 0.62) * t
        pts.append((x, y))
    for i in range(100, -1, -1):
        t = i / 100
        x = cx - (w / 2) * (1 - t ** 1.8)
        y = top + h * 0.38 + (h * 0.62) * t
        pts.append((x, y))
    pts.insert(101, (cx + w / 2, top + h * 0.38))
    pts.append((cx - w / 2, top + h * 0.38))
    d.polygon(pts, fill=(255, 255, 255, 255))
    return img.resize((size, size), Image.LANCZOS)

out = sys.argv[1]
with tempfile.TemporaryDirectory() as tmp:
    iconset = os.path.join(tmp, "IpLeash.iconset")
    os.mkdir(iconset)
    for base in (16, 32, 128, 256, 512):
        draw(base).save(os.path.join(iconset, f"icon_{base}x{base}.png"))
        draw(base * 2).save(os.path.join(iconset, f"icon_{base}x{base}@2x.png"))
    subprocess.run(["/usr/bin/iconutil", "-c", "icns", iconset, "-o", out], check=True)
