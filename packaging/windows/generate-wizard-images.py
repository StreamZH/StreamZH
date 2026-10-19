#!/usr/bin/env python3
# StreamZH - one app for your whole media pipeline.
# Copyright (c) 2026 The StreamZH Authors.
# SPDX-License-Identifier: GPL-3.0-only
"""Generates the branded images of the Windows installer (packaging/windows/images).

    pip install cairosvg fonttools brotli pillow
    python3 packaging/windows/generate-wizard-images.py

wizard-<w>x<h>.png   the banner on the left of the Welcome and Finish pages (WizardImageFile): the stacked StreamZH logo
                     on the dark CloudZH graphite, "a CloudZH product" below. One file per size Inno Setup 6.7 picks
                     from (100 % to 250 % DPI, aspect 164:314).
wizard-small-<n>.png the app icon in the top right corner of the other pages (WizardSmallImageFile), square, transparent.

The logo comes from docs/brand (only paths, no fonts); the font for the small line is Outfit from tools/brand/fonts.
"""
import io
import os

import cairosvg
from fontTools.ttLib import TTFont
from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.normpath(os.path.join(HERE, "..", ".."))
BRAND = os.path.join(REPO, "docs", "brand")
FONT = os.path.join(REPO, "tools", "brand", "fonts", "outfit-latin-500-normal.woff2")
OUT = os.path.join(HERE, "images")

# CloudZH dark palette
TOP, BOTTOM = (0x26, 0x26, 0x24), (0x1F, 0x1E, 0x1D)
CREAM = (0xF4, 0xF2, 0xEC)
MUTED = (0x9F, 0x9E, 0x9A)  # cream at 60 % on the graphite
RED = (0xC0, 0x20, 0x2A)

# Image areas of Inno Setup 6.7 (default font and WizardSizePercent) at 100, 125, 150, 175, 200, 225 and 250 % DPI.
BANNERS = [(202, 386), (269, 515), (336, 643), (403, 772), (430, 824), (498, 953), (534, 1022)]
SMALL = [58, 77, 97, 116, 124, 143, 159]


def svg(name, width):
    """A brand SVG rendered to an RGBA image of the given width."""
    data = cairosvg.svg2png(url=os.path.join(BRAND, name), output_width=width)
    return Image.open(io.BytesIO(data)).convert("RGBA")


def font(size):
    """Outfit Medium (woff2 in the repository) as a Pillow font."""
    ttf = io.BytesIO()
    woff = TTFont(FONT)
    woff.flavor = None
    woff.save(ttf)
    ttf.seek(0)
    return ImageFont.truetype(ttf, size)


def banner(w, h):
    s = w / 202
    image = Image.new("RGBA", (w, h))
    draw = ImageDraw.Draw(image)
    for y in range(h):
        t = y / (h - 1)
        draw.line([(0, y), (w, y)], fill=tuple(round(a + (b - a) * t) for a, b in zip(TOP, BOTTOM)) + (255,))

    # The stacked logo (monogram above the wordmark), cream and red, a little above the middle.
    logo = svg("streamzh-logo-stacked-dark.svg", round(w * 0.62))
    image.alpha_composite(logo, ((w - logo.width) // 2, round(h * 0.40 - logo.height / 2)))

    # A short red rule and the family line at the bottom.
    rule_w, rule_h = round(24 * s), max(2, round(2 * s))
    rule_y = round(h - 58 * s)
    draw.rectangle([(w - rule_w) // 2, rule_y, (w - rule_w) // 2 + rule_w - 1, rule_y + rule_h - 1], fill=RED + (255,))
    text = "a CloudZH product"
    f = font(round(11 * s))
    box = draw.textbbox((0, 0), text, font=f)
    draw.text(((w - (box[2] - box[0])) / 2 - box[0], rule_y + rule_h + round(12 * s) - box[1]), text, font=f,
              fill=MUTED + (255,))
    return image.convert("RGB")


def small(n):
    """The app icon (monogram on the graphite tile) with a small margin, on a transparent square."""
    margin = round(n * 0.06)
    icon = svg("streamzh-icon.svg", n - 2 * margin)
    image = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    image.alpha_composite(icon, (margin, margin))
    return image


def main():
    os.makedirs(OUT, exist_ok=True)
    for w, h in BANNERS:
        banner(w, h).save(os.path.join(OUT, f"wizard-{w}x{h}.png"), optimize=True)
    for n in SMALL:
        small(n).save(os.path.join(OUT, f"wizard-small-{n}.png"), optimize=True)
    print(f"{len(BANNERS) + len(SMALL)} images in {os.path.relpath(OUT, REPO)}")


if __name__ == "__main__":
    main()
