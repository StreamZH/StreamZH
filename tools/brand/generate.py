#!/usr/bin/env python3
"""Generates all StreamZH logo files (SVG, PDF, PNG, ICO) in a selectable accent colour.

    pip install shapely fonttools brotli cairosvg pillow
    python3 tools/brand/generate.py                       # Zurich red #C0202A on light and dark, to ./streamzh-logo
    python3 tools/brand/generate.py --accent "#C1121F" --out ~/Logo

Monogram: Z in front of H (height 100), identical to the CloudZH monogram (CloudZH family, see docs/brand/README.md).
Wordmark: «Stream» Outfit Medium + «ZH» Outfit Bold, built like the CloudZH wordmark.
Rules and measurements: docs/brand/README.md. The fonts are in tools/brand/fonts (SIL OFL).
"""
import argparse
import io
import math
import os
import re

import cairosvg
from fontTools.pens.basePen import BasePen
from fontTools.pens.transformPen import TransformPen
from fontTools.ttLib import TTFont
from PIL import Image
from shapely.geometry import Polygon, box
from shapely.geometry.polygon import orient
from shapely.ops import unary_union

HERE = os.path.dirname(os.path.abspath(__file__))
FONT_MEDIUM = os.path.join(HERE, "fonts", "outfit-latin-500-normal.woff2")
FONT_BOLD = os.path.join(HERE, "fonts", "outfit-latin-700-normal.woff2")

# CloudZH family colours: graphite, cream
NAVY, NAVY_2, PAPER = "#1F1E1D", "#2A2927", "#F4F2EC"
NS = 'xmlns="http://www.w3.org/2000/svg"'
# App icon: monogram 60 % of the tile (CloudZH: 54 %); a bit larger on the dark tile, chosen 17.10.2026
ICON_FRAC = 0.60
# Corners of the monogram softened with this radius (of 100), so it does not look too angular (17.10.2026)
ROUND = 1.5
# Logo side by side: cap height of the wordmark 70 % of the monogram, gap 0.24 x monogram (17.10.2026;
# CloudZH: 58 % and 0.42)
LOGO_TEXT = 0.70
LOGO_GAP = 0.24
# Corners of the wordmark letters softened as well (radius of the cap height 100), 17.10.2026
WORD_ROUND = 1.5


# ---------------------------------------------------------------- Monogram
def letter_z(w, s, x=0.0):
    th = math.atan2(100 - 2 * s, w)
    wh = s / math.sin(th)
    return unary_union([box(x, 0, x + w, s), box(x, 100 - s, x + w, 100),
                        Polygon([(x + w - wh, s), (x + w, s), (x + wh, 100 - s), (x, 100 - s)])])


def soften(geom, r=ROUND):
    """Rounds convex and concave corners with radius r; the gaps between H and Z stay unchanged."""
    if r <= 0:
        return geom
    return geom.buffer(r, quad_segs=8).buffer(-2 * r, quad_segs=8).buffer(r, quad_segs=8)


def monogram():
    """The CloudZH family monogram: stems and crossbar 17 wide, the crossbar ends parallel to the diagonal of the Z
    with a clear gap. StreamZH uses exactly the same geometry as CloudZH."""
    h = unary_union([box(0, 0, 17, 100), box(83, 0, 100, 100),
                     Polygon([(17, 41.5), (38.07, 41.5), (29.12, 58.5), (17, 58.5)]),
                     Polygon([(70.88, 41.5), (83, 41.5), (83, 58.5), (61.93, 58.5)])])
    z = Polygon([(23, 0), (77, 0), (77, 17), (42.25, 83), (77, 83), (77, 100), (23, 100), (23, 83), (57.75, 17), (23, 17)])
    return soften(h), soften(z)


def path(geom, digits=2):
    """SVG path of a (multi)polygon; holes run against the outline, so the default nonzero fill keeps them open."""
    polys = [geom] if geom.geom_type == "Polygon" else [g for g in geom.geoms if g.geom_type == "Polygon"]
    out = []
    for p in polys:
        p = orient(p, 1.0)
        for ring in [p.exterior, *p.interiors]:
            pts = list(ring.coords)[:-1]
            fmt = lambda v: f"{v:.{digits}f}".rstrip("0").rstrip(".")
            out.append("M" + " L".join(f"{fmt(x)} {fmt(y)}" for x, y in pts) + "Z")
    return " ".join(out)


# ---------------------------------------------------------------- Wordmark
class PolyPen(BasePen):
    """Collects glyph contours as point lists; curves are flattened finely enough for 8192 px renders."""

    def __init__(self, glyphset, steps=16):
        super().__init__(glyphset)
        self.rings, self.cur, self.steps = [], [], steps

    def _moveTo(self, p):
        self.cur = [p]

    def _lineTo(self, p):
        self.cur.append(p)

    def _qCurveToOne(self, p1, p2):
        p0 = self.cur[-1]
        for i in range(1, self.steps + 1):
            t = i / self.steps
            self.cur.append(((1 - t) ** 2 * p0[0] + 2 * (1 - t) * t * p1[0] + t * t * p2[0],
                             (1 - t) ** 2 * p0[1] + 2 * (1 - t) * t * p1[1] + t * t * p2[1]))

    def _curveToOne(self, p1, p2, p3):
        p0 = self.cur[-1]
        for i in range(1, self.steps + 1):
            t = i / self.steps
            a, b, c, d = (1 - t) ** 3, 3 * (1 - t) ** 2 * t, 3 * (1 - t) * t * t, t ** 3
            self.cur.append((a * p0[0] + b * p1[0] + c * p2[0] + d * p3[0], a * p0[1] + b * p1[1] + c * p2[1] + d * p3[1]))

    def _closePath(self):
        if len(self.cur) >= 3:
            self.rings.append(self.cur)
        self.cur = []

    _endPath = _closePath


def glyph_geom(rings):
    """Rings of one glyph to a polygon (nonzero rule): rings turning like the largest one are outlines, the rest holes."""
    polys = [Polygon(r).buffer(0) for r in rings]
    signs = [Polygon(r).exterior.is_ccw for r in rings]
    main = signs[max(range(len(polys)), key=lambda i: polys[i].area)]
    outer = unary_union([p for p, s in zip(polys, signs) if s == main])
    holes = unary_union([p for p, s in zip(polys, signs) if s != main])
    return outer.difference(holes)


def text_geom(text, font_file, size, x, baseline, track=-0.02, r=WORD_ROUND):
    """Text as geometry, every glyph softened on its own so neighbouring letters never merge."""
    f = TTFont(font_file)
    gs, cmap, scale = f.getGlyphSet(), f.getBestCmap(), size / f["head"].unitsPerEm
    parts, cx = [], x
    for ch in text:
        gn = cmap[ord(ch)]
        pen = PolyPen(gs)
        gs[gn].draw(TransformPen(pen, (scale, 0, 0, -scale, cx, baseline)))
        parts.append(soften(glyph_geom(pen.rings), r).simplify(0.02))
        cx += f["hmtx"][gn][0] * scale + track * size
    return unary_union(parts), cx - x - track * size


def text_path(text, font_file, size, x, baseline, track=-0.02, r=WORD_ROUND):
    g, w = text_geom(text, font_file, size, x, baseline, track, r)
    return path(g), w


def wordmark():
    f = TTFont(FONT_MEDIUM)
    size = 100 / (f["OS/2"].sCapHeight / f["head"].unitsPerEm)   # cap height = 100
    word, wa = text_path("Stream", FONT_MEDIUM, size, 0, 100)
    zh, wb = text_path("ZH", FONT_BOLD, size, wa - 0.02 * size, 100)
    return word, zh, wa - 0.02 * size + wb


# ---------------------------------------------------------------- Compositions
def build(accent, dark):
    H, Z = monogram()
    WORD, ZH, WW = wordmark()

    def mark(hc, zc):
        return f'<svg {NS} viewBox="0 0 100 100"><path d="{path(H)}" fill="{hc}"/><path d="{path(Z)}" fill="{zc}"/></svg>'

    def icon(bg, hc, zc, h=H, z=Z, frac=ICON_FRAC, radius=112, grad=True):
        s = 512 * frac / 100
        t = (512 - 100 * s) / 2
        defs = (f'<defs><linearGradient id="g" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="{NAVY_2}"/>'
                f'<stop offset="1" stop-color="{bg}"/></linearGradient></defs>') if grad else ""
        fill = "url(#g)" if grad else bg
        return (f'<svg {NS} viewBox="0 0 512 512">{defs}<rect width="512" height="512" rx="{radius}" fill="{fill}"/>'
                f'<g transform="translate({t:.2f} {t:.2f}) scale({s:.4f})"><path d="{path(h)}" fill="{hc}"/>'
                f'<path d="{path(z)}" fill="{zc}"/></g></svg>')

    def word(ink, red):
        return f'<svg {NS} viewBox="0 -3 {WW:.1f} 106"><path d="{WORD}" fill="{ink}"/><path d="{ZH}" fill="{red}"/></svg>'

    def logo(ink, red):
        # Monogram 1 / LOGO_TEXT x cap height, gap LOGO_GAP x monogram to the wordmark (its left side bearing
        # included); the monogram sits slightly below the middle of the cap height (CloudZH: 5.8 at 172)
        mh = 100 / LOGO_TEXT
        gap = LOGO_GAP * mh
        my = 49.55 + 5.8 * (mh - 100) / 72 - mh / 2
        w = mh + gap + WW
        return (f'<svg {NS} viewBox="0 {my - 2:.1f} {w:.1f} {mh + 4:.1f}">'
                f'<g transform="translate(0 {my:.1f}) scale({mh / 100})"><path d="{path(H)}" fill="{ink}"/><path d="{path(Z)}" fill="{red}"/></g>'
                f'<g transform="translate({mh + gap:.1f} 0)"><path d="{WORD}" fill="{ink}"/><path d="{ZH}" fill="{red}"/></g></svg>')

    def stacked(ink, red):
        mh = 260                     # monogram above the wordmark
        gap = 70
        w = max(WW, mh)
        mx = (w - mh) / 2
        wx = (w - WW) / 2
        top = mh + gap
        return (f'<svg {NS} viewBox="0 -3 {w:.1f} {top + 106:.1f}">'
                f'<g transform="translate({mx:.1f} 0) scale({mh / 100})"><path d="{path(H)}" fill="{ink}"/><path d="{path(Z)}" fill="{red}"/></g>'
                f'<g transform="translate({wx:.1f} {top:.1f})"><path d="{WORD}" fill="{ink}"/><path d="{ZH}" fill="{red}"/></g></svg>')

    a = accent
    return {
        # name: (SVG, PNG widths)
        "streamzh-logo": (logo(NAVY, a), (1200, 2400, 4800, 8192)),
        "streamzh-logo-dark": (logo(PAPER, dark), (1200, 2400, 4800, 8192)),
        "streamzh-logo-mono-black": (logo(NAVY, NAVY), (2400, 4800)),
        "streamzh-logo-mono-white": (logo("#FFFFFF", "#FFFFFF"), (2400, 4800)),
        "streamzh-logo-stacked": (stacked(NAVY, a), (1024, 2048, 4096)),
        "streamzh-logo-stacked-dark": (stacked(PAPER, dark), (1024, 2048, 4096)),
        "streamzh-wordmark": (word(NAVY, a), (1200, 2400, 4800, 8192)),
        "streamzh-wordmark-dark": (word(PAPER, dark), (1200, 2400, 4800, 8192)),
        "streamzh-mark": (mark(NAVY, a), (256, 512, 1024, 2048, 4096)),
        "streamzh-mark-dark": (mark(PAPER, dark), (256, 512, 1024, 2048, 4096)),
        "streamzh-mark-mono-black": (mark(NAVY, NAVY), (1024, 4096)),
        "streamzh-mark-mono-white": (mark("#FFFFFF", "#FFFFFF"), (1024, 4096)),
        "streamzh-icon": (icon(NAVY, PAPER, dark), (180, 192, 256, 512, 1024, 2048)),
        "streamzh-icon-light": (icon(PAPER, NAVY, a, grad=False), (180, 192, 512, 1024, 2048)),
        # Favicon as CloudZH: graphite tile (corner 26 of 128), monogram 100 of 128
        "streamzh-favicon": (icon(NAVY, PAPER, dark, frac=100 / 128, radius=104, grad=False), (16, 32, 48, 64, 128)),
    }


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--accent", default="#C0202A", help="accent colour #RRGGBB on light backgrounds (default: portal red #C0202A)")
    ap.add_argument("--accent-dark", default=None, help="accent colour on dark backgrounds (default: same as --accent; since 0.77.4 the logo uses the same red everywhere)")
    ap.add_argument("--out", default="streamzh-logo", help="target folder")
    args = ap.parse_args()
    args.accent_dark = args.accent_dark or args.accent
    if not re.fullmatch(r"#[0-9A-Fa-f]{6}", args.accent) or not re.fullmatch(r"#[0-9A-Fa-f]{6}", args.accent_dark):
        raise SystemExit("Please enter the color as #RRGGBB.")
    out = os.path.expanduser(args.out)
    for sub in ("svg", "pdf", "png"):
        os.makedirs(os.path.join(out, sub), exist_ok=True)
    files = build(args.accent.upper(), args.accent_dark.upper())
    for name, (svg, widths) in files.items():
        open(os.path.join(out, "svg", name + ".svg"), "w", encoding="utf-8").write(svg)
        cairosvg.svg2pdf(bytestring=svg.encode(), write_to=os.path.join(out, "pdf", name + ".pdf"))
        for w in widths:
            cairosvg.svg2png(bytestring=svg.encode(), write_to=os.path.join(out, "png", f"{name}-{w}.png"), output_width=w)
    # Favicon as ICO (16, 32, 48, 64), every size rendered separately
    fav = files["streamzh-favicon"][0].encode()
    imgs = [Image.open(io.BytesIO(cairosvg.svg2png(bytestring=fav, output_width=s))).convert("RGBA") for s in (16, 32, 48, 64)]
    imgs[-1].save(os.path.join(out, "favicon.ico"), sizes=[(i.width, i.height) for i in imgs], append_images=imgs[:-1])
    print(f"{len(files)} designs in {out} (accent {args.accent.upper()})")


if __name__ == "__main__":
    main()
