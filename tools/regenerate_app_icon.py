#!/usr/bin/env python3
"""Regenerate KaliteKit's app icon assets from a single source PNG.

The artwork's own background is kept INSIDE the tile, and everything
outside a rounded-square "tile" is made fully transparent:
  * the tile is inset 7% on every side (a transparent border around it)
  * its corners are rounded at 20% of the canvas size
This is the classic Windows-app-icon look (like Edge / Chrome / Firefox).

Outputs (all derived from one 256px master):

  Assets/AppIcon.ico              multi-resolution 16..256 RGBA .ico
  Assets/StoreLogo.png                      50 x 50
  Assets/Square44x44Logo.scale-200.png      88 x 88
  Assets/Square150x150Logo.scale-200.png   300 x 300
  Assets/LockScreenLogo.scale-200.png       48 x 48
  Assets/Square44x44Logo.targetsize-24_altform-unplated.png  24 x 24

Usage:
  python tools/regenerate_app_icon.py <source.png> [--root <repo-root>]
"""

from __future__ import annotations

import argparse
import io
import sys
from pathlib import Path

from PIL import Image, ImageFilter

# The .ico frame set that Assets/AppIcon.ico currently ships.
ICO_SIZES = (256, 128, 64, 48, 40, 32, 24, 20, 16)

# Tile geometry: transparent margin + rounded-corner radius, as fractions
# of the target canvas size.
MARGIN_FRAC = 0.07   # transparent border on each side
RADIUS_FRAC = 0.20   # rounded-corner radius

TILE_TARGETS = {
    "Assets/StoreLogo.png": 50,
    "Assets/Square44x44Logo.scale-200.png": 88,
    "Assets/Square150x150Logo.scale-200.png": 300,
    "Assets/LockScreenLogo.scale-200.png": 48,
    "Assets/Square44x44Logo.targetsize-24_altform-unplated.png": 24,
}


def tile_art(master: Image.Image, size: int) -> Image.Image:
    """Render the master at `size` with the tile silhouette alpha mask.

    The mask is rasterised at 4x and downsized with LANCZOS so the
    rounded corners stay smooth at every output size.
    """
    img = master.resize((size, size), Image.LANCZOS)
    ss = 4  # supersample factor for the mask
    big = size * ss
    mask = Image.new("L", (big, big), 0)
    from PIL import ImageDraw
    d = ImageDraw.Draw(mask)
    margin = MARGIN_FRAC * big
    radius = RADIUS_FRAC * big
    d.rounded_rectangle((margin, margin, big - margin, big - margin),
                        radius=radius, fill=255)
    mask = mask.resize((size, size), Image.LANCZOS)
    out = img.copy()
    out.putalpha(mask)
    return out


def build_master(src: Image.Image) -> Image.Image:
    """Fit the source onto a square canvas and upscale to 256px.

    A 1px off-square sliver (if any) is filled by mirroring the nearest
    edge pixel so the master stays full-bleed with no transparent seams.
    """
    src = src.convert("RGBA")
    w, h = src.size
    side = max(w, h)
    canvas = Image.new("RGBA", (side, side))
    x0 = (side - w) // 2
    y0 = (side - h) // 2
    canvas.paste(src, (x0, y0))
    px = canvas.load()
    # Edge-fill the up-to-1px gutters so the pad is invisible.
    for x in range(side):
        # top / bottom gutters
        for y in (y0 - 1, y0 + h):
            if 0 <= y < side:
                px[x, y] = px[x, min(max(y - 1, 0), side - 1)]
    for y in range(side):
        for x in (x0 - 1, x0 + w):
            if 0 <= x < side:
                px[x, y] = px[min(max(x - 1, 0), side - 1), y]

    master = canvas.resize((256, 256), Image.LANCZOS)
    # Light sharpen so the upscaled artwork keeps crisp edges at 256px.
    master = master.filter(ImageFilter.UnsharpMask(radius=2, percent=90, threshold=2))
    return master


# ── ICO writer ────────────────────────────────────────────────────────
# Every frame is stored as an embedded PNG (32bpp RGBA). This is what the
# previous AppIcon.ico used (all 9 of its frames decode as PNG) and it is
# fully supported by the Windows shell / .NET on Vista+.

def build_ico(frames: dict[int, Image.Image]) -> bytes:
    buf = io.BytesIO()
    # Pillow's ICO save: pass the 256px image plus append_images so each
    # requested size uses the exact frame we already rendered (our LANCZOS
    # downscales), not Pillow's own thumbnail pass.
    sizes = [(s, s) for s in ICO_SIZES]
    master = frames[ICO_SIZES[0]]
    append = [frames[s] for s in ICO_SIZES if s != ICO_SIZES[0]]
    master.save(buf, format="ICO", sizes=sizes, append_images=append)
    return buf.getvalue()


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("source", help="Source PNG (the new icon art, background included).")
    ap.add_argument("--root", default=".", help="Repo root containing Assets/ (default: cwd).")
    args = ap.parse_args()

    src = Image.open(args.source)
    print(f"source: {src.size} {src.mode}")
    master = build_master(src)
    print("master: 256x256 RGBA")

    root = Path(args.root)

    # Multi-res master -> each ICO frame, cut to the tile silhouette.
    ico_frames = {s: tile_art(master, s) for s in ICO_SIZES}
    ico_path = root / "Assets" / "AppIcon.ico"
    ico_path.write_bytes(build_ico(ico_frames))
    print(f"wrote   {ico_path}  ({len(ico_path.read_bytes())} bytes, "
          f"frames {', '.join(str(s) for s in ICO_SIZES)})")

    for rel, size in TILE_TARGETS.items():
        target = root / rel
        tile_art(master, size).save(target, format="PNG")
        print(f"wrote   {target}  ({size}x{size})")

    return 0


if __name__ == "__main__":
    sys.exit(main())
