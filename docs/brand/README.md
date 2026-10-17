# StreamZH Logo

StreamZH is part of the CloudZH family and uses the **same logo as CloudZH**, with «Stream» in place of «Cloud».
Monogram, colours, font, proportions and clear space are identical to the CloudZH brand kit.

## Parts

| File | Use |
|---|---|
| `streamzh-mark.svg` / `-dark.svg` | Monogram (Z in front of H), square. Logo in the app, avatar |
| `streamzh-icon.svg` / `-light.svg` | App icon: monogram on a tile (dark or light) |
| `streamzh-favicon.svg`, `favicon.ico` | Favicon 16 to 48 px, graphite tile |
| `streamzh-wordmark.svg` / `-dark.svg` | Wordmark «StreamZH» (Outfit Medium + Bold) |
| `streamzh-logo.svg` / `-dark.svg` | Monogram + wordmark side by side |
| `streamzh-logo-stacked.svg` / `-dark.svg` | Monogram above the wordmark (README header, splash) |
| `streamzh-mark-mono-*.svg`, `streamzh-logo-mono-*.svg` | Single colour, black or white |
| `png/` | PNG exports (icons 16 to 1024 px, logos 2048 to 2400 px wide) |

All SVGs contain only paths, no fonts and no scripts. In the app the logo is the React component
`frontend/src/components/Logo.tsx` (same paths).

## Colour

- Red (Z in the monogram, «ZH» in the wordmark): **#C0202A** (Zurich red) on light and dark backgrounds.
- H and «Stream»: graphite **#1F1E1D** on light, cream **#F4F2EC** on dark backgrounds (in the app: the text colour).

## Monogram construction (height 100)

- H `M0 0H17V100H0Z M83 0H100V100H83Z M17 41.5H38.07L29.12 58.5H17Z M70.88 41.5H83V58.5H61.93Z`
- Z `M23 0H77V17L42.25 83H77V100H23V83L57.75 17H23Z`
- Stems, bars and diagonal 17 wide; the crossbar of the H ends parallel to the diagonal of the Z with a clear gap
- Favicon: the same monogram (100 of 128) on a graphite tile #1F1E1D with corner radius 26 of 128

## Wordmark

- Font Outfit (SIL Open Font License): «Stream» Medium, «ZH» Bold, tracking −2 %
- Side by side: wordmark height 0.616 × monogram, gap 0.418 × monogram, wordmark 5 % above the centre.
  In CSS: `gap: calc(M * .418)`, wordmark `height: calc(M * .616); transform: translateY(-5%)` (M = monogram size)

## Clear space and minimum sizes

- Free all around: half the height of the monogram
- Monogram from 16 px (below that use the favicon), wordmark from 80 px width

## Regenerating

```
pip install shapely fonttools brotli cairosvg pillow
python3 tools/brand/generate.py --accent "#C0202A" --out streamzh-logo
```

Writes SVG, PDF, PNG (up to 8192 px) and `favicon.ico`. The font Outfit (SIL OFL) is under `tools/brand/fonts/`.
