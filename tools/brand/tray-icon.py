#!/usr/bin/env python3
"""Writes src/StreamZH.Tray/streamzh.ico, the icon of StreamZH.Tray.exe and of the tray icon, from docs/brand.

    pip install cairosvg pillow
    python3 tools/brand/tray-icon.py

Small sizes (16 to 48 px, the notification area at 100 to 300 % scaling) use the favicon (graphite tile, large
monogram), 64 and 256 px (Explorer, Alt+Tab) the app icon. Every size is rendered from the SVG, not scaled down.
Sizes below 256 are stored as 32-bit BMP, 256 as PNG (what Windows and System.Drawing.Icon read everywhere).
The status dot (green, orange, red) is drawn by the tray app at runtime.
"""
import io
import os
import struct

import cairosvg
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
FAVICON = os.path.join(REPO, "docs", "brand", "streamzh-favicon.svg")
APP_ICON = os.path.join(REPO, "docs", "brand", "streamzh-icon.svg")
OUT = os.path.join(REPO, "src", "StreamZH.Tray", "streamzh.ico")
SMALL = (16, 20, 24, 32, 40, 48)
LARGE = (64, 256)


def render(svg_path, size):
    png = cairosvg.svg2png(url=svg_path, output_width=size, output_height=size)
    return Image.open(io.BytesIO(png)).convert("RGBA")


def bmp_entry(image):
    """BITMAPINFOHEADER (height doubled for the AND mask), BGRA pixels bottom-up, then a 1-bit AND mask."""
    w, h = image.size
    header = struct.pack("<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0, 0, 0, 0, 0, 0)
    pixels = bytearray()
    for y in range(h - 1, -1, -1):
        for x in range(w):
            r, g, b, a = image.getpixel((x, y))
            pixels += bytes((b, g, r, a))
    row = ((w + 31) // 32) * 4
    mask = bytearray()
    for y in range(h - 1, -1, -1):
        bits = bytearray(row)
        for x in range(w):
            if image.getpixel((x, y))[3] == 0:
                bits[x // 8] |= 0x80 >> (x % 8)
        mask += bits
    return header + bytes(pixels) + bytes(mask)


def png_entry(image):
    buffer = io.BytesIO()
    image.save(buffer, format="PNG", optimize=True)
    return buffer.getvalue()


def main():
    images = [render(FAVICON, s) for s in SMALL] + [render(APP_ICON, s) for s in LARGE]
    entries = [png_entry(i) if i.width >= 256 else bmp_entry(i) for i in images]
    header = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    directory = b""
    for image, data in zip(images, entries):
        size = image.width if image.width < 256 else 0
        directory += struct.pack("<BBBBHHII", size, size, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    with open(OUT, "wb") as f:
        f.write(header + directory + b"".join(entries))
    print(f"{OUT}: {', '.join(str(i.width) for i in images)} px")


if __name__ == "__main__":
    main()
