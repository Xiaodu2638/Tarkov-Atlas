#!/usr/bin/env python3
"""Bake per-floor overlay images and hazard polygons for Tarkov Atlas.

Where the data lives
--------------------
* Geometry (transform / bounds / coordinateRotation) and the per-level tile layers
  come from the tarkov.dev repo snapshot: research/upstream/tarkov-dev-maps.json.
* Hazards (minefields / sniper zones) come from the JSON API snapshot:
  research/api/maps.json, keyed by map object id.

Floors
------
Stitching a level produces an image with its own affine (game -> layer pixels),
which does not share the base JPG's pixel grid (SVG maps are rendered by a browser,
tile maps are stitched). To make the overlay trivial and always aligned, each level
is warped into the base image's pixel grid at build time:

    layer_pixel = layer_affine o inverse(base_affine) (base_pixel)

so at runtime the app only draws the PNG over the base rectangle.

Outputs:
  TarkovAtlas/Assets/maps/layers/<key>/<n>_<slug>.png
  TarkovAtlas/Assets/maps/layers/index.json
  TarkovAtlas/Assets/maps/hazards.json
"""
from __future__ import annotations

import json
import os
import re
import sys
from concurrent.futures import ThreadPoolExecutor, as_completed
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
MAPS = ROOT / "TarkovAtlas" / "Assets" / "maps"
RESEARCH = ROOT / "research"
DATA_MAPS = RESEARCH / "upstream" / "tarkov-dev-maps.json"
API_MAPS = RESEARCH / "api" / "maps.json"

sys.path.insert(0, str(Path(__file__).resolve().parent))

# Edge rendering is the bottleneck; a handful of parallel workers is a big
# win and keeps memory sane (each instance is ~100 MB).
WORKERS = max(1, min(8, (os.cpu_count() or 2) // 2))
import build_assets as base  # noqa: E402


# The interactive map's base tiles are themselves a level. With only the extra
# layers in the picker, The Lab showed "Second Level / Technical" but no First
# Level, and Interchange showed no ground floor at all. An entry with an empty
# image means "draw the base artwork only".
BASE_LAYERS = {
    "factory": ("Ground Floor", -1.0, 3.0),
    "the-lab": ("First Level", -0.9, 3.0),
    "interchange": ("Ground Floor", -1000.0, 25.0),
    # Outdoor maps whose layer list starts at an upper deck. Without a ground
    # entry the default pick (first layer) dimmed the whole outdoor base to 35%,
    # which read as "the outdoors are darker than they should be".
    "reserve": ("Ground", -1000.0, 18.0),
    "customs": ("Ground", -1000.0, 2.7),
    "shoreline": ("Ground", -0.9, 2.0),
    "ground-zero": ("Ground", -1000.0, 26.0),
    "streets-of-tarkov": ("Ground", -1000.0, 10.0),
    "icebreaker": ("Deck", -10000.0, 18.92),
}

# Floors tarkov.dev only ships as raster tiles but whose SVG has the same group.
# The Lab's tiles are the pre-0.16 "photo" render, which looked like a different
# map on top of the vector base; the SVG group is the same artwork as the base.
SVG_FLOOR_ALIASES = {
    "the-lab": {"Second Level": "Second_Level", "Technical": "Technical_Level"},
}

# Icebreaker's vector art ships as one SVG per deck, all sharing the same viewBox
# (1000 1500 3000 5400) and each with only that deck's group set to display:block.
# Baking every floor from these keeps all levels on the same pixel grid as the
# base art; the old raster tiles used a different grid, which is why decks never
# lined up and why their art style differed from the vector base.
# Mapping was verified by reading each SVG's visible group id.
_ICE_SVG_DIR = base.SVG_ROOT / "icebreaker"

# Deck svg per in-game level, in the same order as the upstream layer list so the
# floor picker keeps its familiar order. Levels that share deck art point at the
# same file.
ICE_FLOOR_FILES = {
    "Infirmary": "1.svg",
    "Helipad": "2.svg",
    "Gym/Canteen": "3.svg",
    "Accommodation (lower)": "4.svg",
    "Accommodation (mid)": "5.svg",
    "Accommodation (upper)": "6.svg",
    "Officers' Deck": "7.svg",
    "Stairs (blocked)": "8.svg",
    "Bridge": "9.svg",
    "Bridge Roof": "10.svg",
    "Control Room": "-4.svg",
    "Engine Room": "-3.svg",
    "Engine Room (upper)": "-3.svg",
    "Fuel Pumps (lower)": "-2.svg",
    "Fuel Pumps": "-2.svg",
    "Storage/Security": "-1.svg",
}


KEY_TO_ID = {
    "ground-zero": "653e6760052c01c1c805532f",
    "customs": "56f40101d2720b2a4d8b45d6",
    "factory": "55f2d3fd4bdc2d5f408b4567",
    "woods": "5704e3c2d2720bac5b8b4567",
    "interchange": "5714dbc024597771384a510d",
    "shoreline": "5704e554d2720bac5b8b456e",
    "reserve": "5704e5fad2720bc05b8b4567",
    "the-lab": "5b0fc42d86f7744a585f9105",
    "lighthouse": "5704e4dad2720bb55b8b4567",
    "streets-of-tarkov": "5714dc692459777137212e12",
    "the-labyrinth": "6733700029c367a3d40b02af",
    "terminal": "65cc8f81a9aac3e77d0cfd3e",
    "icebreaker": "69af492a4819ea4ba10a69c5",
}


def slug(name: str) -> str:
    return re.sub(r"[^a-z0-9]+", "-", name.lower()).strip("-") or "level"


def choose_zoom(mm: dict) -> int:
    zoom = 4
    minx, miny, maxx, maxy = base.projected_bounds(mm, zoom)
    if max(maxx - minx, maxy - miny) < 2400:
        zoom = 5
    return zoom


def invert(aff: list[float]) -> tuple[float, float, float, float, float, float]:
    a, b, c, d, e, f = aff
    det = a * e - b * d
    if abs(det) < 1e-12:
        raise ValueError("degenerate affine")
    ia, ib = e / det, -b / det
    id_, ie = -d / det, a / det
    return ia, ib, -(ia * c + ib * f), id_, ie, -(id_ * c + ie * f)


def bake_layer(mm: dict, key: str, index: int, layer: dict,
               base_aff: list[float], base_w: int, base_h: int) -> dict | None:
    from PIL import Image

    name = (layer.get("name") or f"Level {index}").strip()
    out_dir = MAPS / "layers" / key
    out_dir.mkdir(parents=True, exist_ok=True)
    file_name = f"{index}_{slug(name)}.png"
    target = out_dir / file_name

    if target.exists() and target.stat().st_size > 1000:
        try:
            with Image.open(target) as old:
                if old.size == (base_w, base_h):
                    print(f"    {name} -> {file_name} (cached)", flush=True)
                    return _layer_meta(key, index, name, file_name, layer)
        except Exception:
            pass

    layer_cfg = base.uniform_transform(dict(mm))
    layer_cfg["tilePath"] = layer["tilePath"]

    try:
        img, aff = base.stitch_tiles(layer_cfg, f"{key}__layer{index}", choose_zoom(mm))
    except Exception as exc:  # noqa: BLE001
        print(f"    bake failed for {layer.get('name')}: {exc}", flush=True)
        return None
    if img is None or img.getbbox() is None:
        return None
    # The base affine is isotropic (see build_assets.sanitize_affine); the layer
    # has to use the same convention or the warp squashes it vertically.
    aff = base.sanitize_affine(aff)

    # base_pixel -> game -> layer_pixel
    na, nb, nc, nd, ne, nf = invert(base_aff)
    a, b, c, d, e, f = aff
    m = (a * na + b * nd,
         a * nb + b * ne,
         a * nc + b * nf + c,
         d * na + e * nd,
         d * nb + e * ne,
         d * nc + e * nf + f)

    try:
        warped = img.transform((base_w, base_h), Image.AFFINE, m,
                               resample=Image.BILINEAR)
    except Exception as exc:  # noqa: BLE001
        print(f"    warp failed for {layer.get('name')}: {exc}", flush=True)
        return None
    if warped.getbbox() is None:
        return None

    warped.save(target, "PNG", optimize=True)
    return _layer_meta(key, index, name, file_name, layer)


def bake_svg_layer(key: str, index: int, layer: dict,
                   base_w: int, base_h: int, turn: int) -> dict | None:
    """Bake an SVG-group floor into the base image's pixel grid.

    The group is rasterised through build_assets.svg_canvas, the same path the
    base art took (own aspect ratio, same turn, same stretch), so the overlay
    lands exactly where the base drew that floor.

    Two flavours are supported:
    * `svgLayer` picks one named group out of the map's single SVG (The Lab,
      Factory, Interchange...).
    * `svgFile` points at a dedicated per-floor SVG (Icebreaker ships one file
      per deck, each already showing only that deck), so no group hiding is
      needed.
    """
    from PIL import Image

    layer_id = layer.get("svgLayer")
    svg_file = layer.get("svgFile")
    name = (layer.get("name") or f"Level {index}").strip()
    if svg_file:
        svg = _ICE_SVG_DIR / svg_file
    else:
        svg = base.svg_source(key)
        if svg is None:
            print(f"!! no SVG source registered for {key}", flush=True)
            return None
    if not svg.is_file():
        print(f"!! SVG source missing for {key}: {svg}", flush=True)
        return None
    if not layer_id and not svg_file:
        return None
    if layer_id and f'id="{layer_id}"' not in svg.read_text(encoding="utf-8", errors="replace"):
        # render_svg would silently fall back to the whole document (all floors).
        print(f"!! svg layer {layer_id!r} missing from {svg.name}", flush=True)
        return None

    out_dir = MAPS / "layers" / key
    out_dir.mkdir(parents=True, exist_ok=True)
    file_name = f"{index}_{slug(name)}.png"
    target = out_dir / file_name

    # A baked floor is only valid at the base image's resolution. When the base
    # art was re-rendered a stale overlay is silently misaligned, so rebuild;
    # when it already matches, skip the Edge launch entirely.
    if target.exists() and target.stat().st_size > 1000:
        try:
            with Image.open(target) as old:
                if old.size == (base_w, base_h):
                    print(f"    {name} -> {file_name} (cached)", flush=True)
                    return _layer_meta(key, index, name, file_name, layer)
        except Exception:
            pass

    # Icebreaker's per-deck files go through the exact same render path as the
    # base art: same svg_canvas call, same output size, same turn. That identity
    # is what keeps every deck aligned with the hull. Do not add special-case
    # cropping or resizing here; any deviation shifts the deck off the base.
    warped = base.svg_canvas(svg, base_w, base_h, turn,
                             only_layer=layer_id, transparent=True)
    # Same failure mode as the base art: a layer filter that hides too much
    # yields a fully transparent PNG, which would overlay nothing.
    if warped.getbbox() is None:
        print(f"!! {key}/{name}: SVG layer rendered empty", flush=True)
        return None
    warped.save(target, "PNG", optimize=True)
    return _layer_meta(key, index, name, file_name, layer)


def _layer_meta(key: str, index: int, name: str, file_name: str,
                layer: dict) -> dict:
    lo = hi = None
    for extent in layer.get("extents") or []:
        height = extent.get("height")
        if isinstance(height, (list, tuple)) and len(height) >= 2:
            lo = height[0] if lo is None else min(lo, height[0])
            hi = height[1] if hi is None else max(hi, height[1])

    return {
        "name": name,
        "image": f"layers/{key}/{file_name}",
        "order": index,
        "minHeight": lo,
        "maxHeight": hi,
    }


def collect_hazards(api_entry: dict | None) -> list[dict]:
    if not api_entry:
        return []
    out = []
    for hz in api_entry.get("hazards") or []:
        pts = [{"x": p["x"], "z": p["z"]}
               for p in (hz.get("outline") or [])
               if isinstance(p, dict) and "x" in p and "z" in p]
        if len(pts) < 3:
            continue
        out.append({
            "type": (hz.get("hazardType") or "hazard").lower(),
            "name": hz.get("name") or "",
            "points": pts,
        })
    return out


def main() -> None:
    manifest = json.loads((MAPS / "maps.json").read_text(encoding="utf-8"))
    data = json.loads(DATA_MAPS.read_text(encoding="utf-8"))
    api = json.loads(API_MAPS.read_text(encoding="utf-8"))["data"]["maps"]
    by_name = {m["normalizedName"]: m for m in data}

    only = None
    for i, arg in enumerate(sys.argv):
        if arg == "--only" and i + 1 < len(sys.argv):
            only = {x.strip() for x in sys.argv[i + 1].split(",") if x.strip()}

    hazards: dict[str, list] = {}
    hz_path = MAPS / "hazards.json"
    if only and hz_path.exists():
        hazards = json.loads(hz_path.read_text(encoding="utf-8"))

    # Collect a flat job list first, then bake concurrently. Every job launches
    # its own headless Edge, and Edge is CPU bound, so serial baking is what made
    # this script look stuck.
    jobs = []
    order = {}
    for entry in manifest["maps"]:
        key = entry["key"]
        if only and key not in only:
            continue
        src = by_name.get(key)
        if not src:
            continue
        interactive = next((x for x in src["maps"] if x.get("projection") == "interactive"), None)
        if not interactive:
            continue

        api_entry = api.get(KEY_TO_ID.get(key, ""))
        hz = collect_hazards(api_entry)
        if hz:
            hazards[key] = hz

        # Layers come in two flavours: named groups inside the map's SVG
        # (`svgLayer`) and raster tile sets (`tilePath`). The bundled base art is
        # the SVG render, so prefer the SVG group: mixing the crisp vector base
        # with an old brown tile overlay looked like two maps stacked.
        raw_layers = [l for l in (interactive.get("layers") or [])
                      if l.get("tilePath") or l.get("svgLayer")]
        for layer in raw_layers:
            alias = SVG_FLOOR_ALIASES.get(key, {}).get((layer.get("name") or "").strip())
            if alias:
                layer["svgLayer"] = alias
        # Icebreaker ships its vector art as one SVG per deck (each file already
        # shows a single floor). Baking those keeps every level on the same pixel
        # grid as the base artwork; the old raster tiles used a different grid,
        # which is why the decks never lined up.
        if key == "icebreaker":
            for layer in raw_layers:
                fname = ICE_FLOOR_FILES.get((layer.get("name") or "").strip())
                if fname:
                    layer.pop("tilePath", None)
                    layer["svgFile"] = fname
        turn = base.svg_turn(interactive, api_entry)
        print(f"[{key}] {len(raw_layers)} layer(s), {len(hz)} hazard(s)", flush=True)
        baked = []
        if key in BASE_LAYERS:
            name, lo, hi = BASE_LAYERS[key]
            baked.append({"name": name, "image": "", "order": 0,
                          "minHeight": lo, "maxHeight": hi})
        order[key] = baked
        for i, layer in enumerate(raw_layers):
            jobs.append((interactive, key, i, layer, entry["affine"],
                         entry["width"], entry["height"], turn))

    def _run(job):
        interactive, key, i, layer, aff, w, h, turn = job
        if layer.get("svgFile") or layer.get("svgLayer"):
            return key, i, layer, bake_svg_layer(key, i, layer, w, h, turn)
        if layer.get("tilePath"):
            return key, i, layer, bake_layer(interactive, key, i, layer, aff, w, h)
        return key, i, layer, None

    results = []
    with ThreadPoolExecutor(max_workers=WORKERS) as pool:
        futures = [pool.submit(_run, job) for job in jobs]
        for fut in as_completed(futures):
            key, i, layer, meta = fut.result()
            results.append((key, i, layer, meta))

    for key, i, layer, meta in sorted(results, key=lambda r: (list(order).index(r[0]), r[1])):
        if meta:
            meta["order"] = len(order[key])
            order[key].append(meta)
            print(f"    {meta['name']} -> {meta['image']}", flush=True)
        else:
            print(f"!! layer {layer.get('name')!r} of {key} failed to bake", flush=True)

    index = {}
    out = MAPS / "layers" / "index.json"
    if only and out.exists():
        # --only rebuilds a subset; the other maps' entries must survive.
        index = json.loads(out.read_text(encoding="utf-8"))
    index.update({k: v for k, v in order.items() if v})

    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(index, ensure_ascii=False, indent=1), encoding="utf-8", newline="")
    hz_out = MAPS / "hazards.json"
    hz_out.write_text(json.dumps(hazards, ensure_ascii=False, indent=1), encoding="utf-8", newline="")
    print(f"wrote {out} ({sum(len(v) for v in index.values())} layers)")
    print(f"wrote {hz_out} ({sum(len(v) for v in hazards.values())} hazards)")


if __name__ == "__main__":
    sys.exit(main())
