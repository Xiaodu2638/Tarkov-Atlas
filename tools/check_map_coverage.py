#!/usr/bin/env python3
"""Check that every baked map canvas is either full of artwork or declares where
the artwork actually is.

`datacheck` only sees numbers in mapdata.json, so it cannot tell whether the
bitmap matches them. This script decodes the JPEGs and compares the drawn
artwork with the declared `content` rect.

The app fits `content`, not the whole bitmap (Icebreaker's hull occupies a fifth
of its tile canvas, the Labyrinth SVG has a wide border). So an empty canvas
border is fine as long as `content` is tight around the artwork. Without a
content rect the canvas itself must be full, which is the old behaviour.

Run after touching Assets/Maps/*.jpg or mapdata.json:

    python tools/check_map_coverage.py              # all maps
    python tools/check_map_coverage.py icebreaker   # one map

Exit code is non-zero when a map looks under-baked, so it can gate a build.
"""
from __future__ import annotations

import json
import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools" / "pyext"))
sys.path.insert(0, str(ROOT / "tools" / "pylibs"))

import numpy as np
from PIL import Image

MAPS = ROOT / "TarkovAtlas" / "Assets" / "Maps"

# Artwork is anything meaningfully brighter than the empty border colour.
ART_DELTA = 12
# The dark surface every canvas is flattened onto (build_assets.BG in grayscale).
BG_GRAY = 17
# A well baked map reaches within this fraction of every canvas edge.
MAX_MARGIN = 0.10
# The declared content rect must sit this close to the detected artwork.
CONTENT_TOLERANCE = 0.03
# ...and its markers must not float in the empty border.
MIN_MARKERS_IN_ART = 0.90


def markers(map_def: dict) -> list[tuple[float, float]]:
    pts = [(p["x"], p["z"]) for p in map_def["spawns"]]
    pts += [(p["x"], p["z"]) for p in map_def["extracts"]]
    pts += [(p["x"], p["z"]) for boss in map_def["bosses"] for p in boss["points"]]
    pts += [(p["x"], p["z"]) for p in map_def["transits"]]
    return pts


def background(gray: np.ndarray) -> int:
    """Modal value of the outer ring: the colour the canvas was flattened onto.

    The whole-image mode is wrong for maps whose artwork covers most of the
    canvas (Factory's floor is the most common pixel, not the background).
    """
    ring = np.concatenate([gray[0:2].ravel(), gray[-2:].ravel(),
                           gray[:, 0:2].ravel(), gray[:, -2:].ravel()])
    return int(np.bincount(ring, minlength=256).argmax())


def art_box(gray: np.ndarray, bg: int) -> tuple[int, int, int, int] | None:
    art = gray > bg + ART_DELTA
    rows = np.flatnonzero(art.any(axis=1))
    cols = np.flatnonzero(art.any(axis=0))
    if rows.size == 0 or cols.size == 0:
        return None
    return int(cols.min()), int(rows.min()), int(cols.max()), int(rows.max())


def content_box(map_def: dict) -> tuple[float, float, float, float] | None:
    """Declared content rect, already in pixels."""
    c = map_def.get("content") or []
    if len(c) < 4:
        return None
    return min(c[0], c[2]), min(c[1], c[3]), max(c[0], c[2]), max(c[1], c[3])


def main(argv: list[str]) -> int:
    manifest = json.loads((MAPS / "mapdata.json").read_text(encoding="utf-8"))
    wanted = {a.lower() for a in argv[1:]}
    maps = [m for m in manifest["maps"] if not wanted or m["key"].lower() in wanted]
    if not maps:
        print("no matching map")
        return 2

    print(f"{'map':18}{'image':>12}{'art bbox':>24}{'margins L/T/R/B':>24}"
          f"{'in art':>9}  content")
    broken: list[str] = []
    for m in maps:
        with Image.open(MAPS / m["image"]) as im:
            gray = np.asarray(im.convert("L"))
        height, width = gray.shape
        bg = background(gray)
        if abs(bg - BG_GRAY) > ART_DELTA:
            # The border ring is artwork (terminal's sea plate), so there is no
            # empty frame to measure and the declared content covers the canvas.
            box = (0, 0, width - 1, height - 1)
        else:
            box = art_box(gray, bg)
        if box is None:
            broken.append(f"  {m['key']}: image has no artwork above the background")
            print(f"{m['key']:18}{width}x{height:<7}{'empty':>22}{'-':>20}{'-':>10}")
            continue

        left, top = box[0] / width, box[1] / height
        right, bottom = (width - 1 - box[2]) / width, (height - 1 - box[3]) / height
        bbox = f"{box[0]}..{box[2]},{box[1]}..{box[3]}"

        declared = content_box(m)
        a, b, c, d, e, f = m["affine"]
        pts = markers(m)
        inside_img = inside_art = 0
        for x, z in pts:
            px = a * x + b * z + c
            py = d * x + e * z + f
            if 0 <= px < width and 0 <= py < height:
                inside_img += 1
                if box[0] <= px <= box[2] and box[1] <= py <= box[3]:
                    inside_art += 1

        in_art_ratio = inside_art / max(len(pts), 1)
        fitted = "yes" if declared else "-"
        print(
            f"{m['key']:18}{width}x{height:<7}{bbox:>24}"
            f"{left:7.1%}/{top:6.1%}/{right:6.1%}/{bottom:7.1%}"
            f"{inside_art:>4}/{len(pts):<4}{fitted:>9}"
        )

        problems = []
        if (width, height) != (m["width"], m["height"]):
            problems.append(f"declared {m['width']}x{m['height']} but file is {width}x{height}")
        if declared:
            # The app fits this rect, so it has to be tight around the artwork.
            drift = max(abs(declared[0] - box[0]) / width, abs(declared[1] - box[1]) / height,
                        abs(declared[2] - box[2]) / width, abs(declared[3] - box[3]) / height)
            if drift > CONTENT_TOLERANCE:
                problems.append(f"content rect is {drift:.1%} off the artwork")
        else:
            wide = [n for n, v in (("left", left), ("top", top), ("right", right), ("bottom", bottom))
                    if v > MAX_MARGIN]
            if wide:
                problems.append("empty margin on " + "/".join(wide))
            if in_art_ratio < MIN_MARKERS_IN_ART:
                problems.append(f"only {inside_art}/{len(pts)} markers inside the artwork "
                                f"({inside_img} land on the canvas)")
        if problems:
            broken.append(f"  {m['key']}: " + "; ".join(problems))

    if broken:
        print("\nSUSPECT MAPS")
        for line in broken:
            print(line)
        return 1
    print("\nall canvases are fully baked")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
