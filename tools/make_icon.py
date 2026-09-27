"""Draws the tray/app icon: a white thermometer, centred, on an orange rounded square.

Usage (needs Pillow: pip install pillow):
    python tools/make_icon.py AntecCaseDisplay/Assets/AppIcon.ico

Shapes are laid out on a 32-unit grid with even coordinates, so at 16 px every
edge lands on a pixel boundary and the icon stays crisp in the tray.
"""
import io
import struct
import sys
from PIL import Image, ImageDraw

BG_TOP, BG_BOTTOM = (0xF9, 0x73, 0x16), (0xC2, 0x41, 0x0C)   # orange 500 -> 700
WHITE = (255, 255, 255)
MERCURY = (0xB9, 0x1C, 0x1C)                                 # deep red
SS = 8                                                       # supersampling

def render(size):
    s = size * SS
    u = s / 32.0                     # one design unit in supersampled px
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))

    # background: vertical gradient clipped to a rounded square
    grad = Image.new("RGBA", (s, s))
    gd = ImageDraw.Draw(grad)
    for y in range(s):
        t = y / (s - 1)
        gd.line([(0, y), (s, y)], fill=tuple(round(a + (b - a) * t) for a, b in zip(BG_TOP, BG_BOTTOM)) + (255,))
    mask = Image.new("L", (s, s), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, s - 1, s - 1], radius=7 * u, fill=255)
    img.paste(grad, (0, 0), mask)

    d = ImageDraw.Draw(img)
    R = lambda *v: [c * u for c in v]
    # thermometer body, centred: tube with a round top, and the bulb
    d.rounded_rectangle(R(12, 3, 20, 24), radius=4 * u, fill=WHITE)
    d.ellipse(R(9, 16, 23, 30), fill=WHITE)
    # mercury: channel up the tube into the bulb
    d.rounded_rectangle(R(14, 10, 18, 24), radius=1 * u, fill=MERCURY)
    d.ellipse(R(11.5, 18.5, 20.5, 27.5), fill=MERCURY)

    return img.resize((size, size), Image.LANCZOS)

def dib(img):
    """32-bit BMP icon entry: header, bottom-up BGRA pixels, then an AND mask."""
    n = img.width
    px = img.load()
    header = struct.pack("<IiiHHIIiiII", 40, n, n * 2, 1, 32, 0, 0, 0, 0, 0, 0)
    xor = bytearray()
    for y in reversed(range(n)):
        for x in range(n):
            r, g, b, a = px[x, y]
            xor += bytes((b, g, r, a))
    row = ((n + 31) // 32) * 4
    mask = bytearray()
    for y in reversed(range(n)):
        bits = bytearray(row)
        for x in range(n):
            if px[x, y][3] == 0:
                bits[x // 8] |= 0x80 >> (x % 8)
        mask += bits
    return header + bytes(xor) + bytes(mask)

def png(img):
    buf = io.BytesIO()
    img.save(buf, format="PNG", optimize=True)
    return buf.getvalue()

# Classic layout every icon loader understands: bitmaps for the small sizes,
# PNG only for 256 px.
sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
entries = [(n, png(render(n)) if n == 256 else dib(render(n))) for n in sizes]
out = bytearray(struct.pack("<HHH", 0, 1, len(entries)))
offset = 6 + 16 * len(entries)
for n, data in entries:
    out += struct.pack("<BBBBHHII", n % 256, n % 256, 0, 0, 1, 32, len(data), offset)
    offset += len(data)
for _, data in entries:
    out += data
with open(sys.argv[1], "wb") as f:
    f.write(out)
print("wrote", sys.argv[1])
