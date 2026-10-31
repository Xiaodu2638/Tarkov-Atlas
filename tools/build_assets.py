#!/usr/bin/env python3
"""Build the offline map bundle for Tarkov Atlas.

Sources (all committed under research/):
  * research/svg/<key>/*.svg        vector maps (CC BY-NC-SA 4.0)
  * research/api/maps.json          tarkov.dev geometry/labels (MIT)
  * research/upstream/              upstream data/maps.json (layer + affine table)
  * tarkov.dev raster tiles         for maps that ship no public SVG (labyrinth)

Output: TarkovAtlas/Assets/maps/ (base JPGs + maps.json manifest).

The script intentionally uses the same camera/CRS maths as tarkov.dev's web map,
then normalises every output image to north-up so the desktop app needs only one
simple affine transform.
"""
from __future__ import annotations

import html
import json
import math
import os
import re
import shutil
import subprocess
import sys
import threading
import time
import uuid
import xml.etree.ElementTree as ET
from concurrent.futures import ThreadPoolExecutor, as_completed
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools" / "pyext"))
sys.path.insert(0, str(ROOT / "tools" / "pylibs"))

from PIL import Image, ImageChops, ImageOps  # noqa: E402
import requests  # noqa: E402

try:  # noqa: E402 - used for the labyrinth SVG warp; pylibs is vendored
    import numpy as np  # type: ignore
except ImportError:  # pragma: no cover
    np = None

RESEARCH = ROOT / "research"
# Vector sources: one directory per map key, so `research/svg/<key>/` is the only
# place a map's artwork can come from. Icebreaker keeps its 14 deck files there.
SVG_ROOT = RESEARCH / "svg"
DATA_MAPS = RESEARCH / "upstream" / "tarkov-dev-maps.json"
OUT = ROOT / "TarkovAtlas" / "Assets" / "maps"
CACHE = ROOT / "tools" / "tile-cache"
# Scratch space for headless Edge profiles and intermediate renders. Kept inside
# the repo (never %TEMP%) and git-ignored.
SCRATCH = ROOT / ".tmp" / "build"

# Maps whose SVG directory holds more than one deck; the base art is the ground
# deck. Every other map has exactly one SVG in its directory.
BASE_DECK = {"icebreaker": "1.svg"}


def svg_source(key: str) -> Path | None:
    """The base-art SVG for a map key, or None when the map ships no vector art.

    Single source of truth for map -> SVG: `build_layers.py` calls this instead
    of keeping its own file-name table.
    """
    folder = SVG_ROOT / key
    if not folder.is_dir():
        return None
    if key in BASE_DECK:
        deck = folder / BASE_DECK[key]
        return deck if deck.is_file() else None
    files = sorted(folder.glob("*.svg"))
    return files[0] if len(files) == 1 else None


def _find_edge() -> Path:
    """Locate the Chromium/Edge binary used to rasterise SVGs.

    Never hardcode an absolute drive path: contributors run this on machines with
    different install roots. Resolution order: EDGE_PATH env var, then the usual
    Edge/Chrome locations, then a bare name resolved through PATH.
    """
    import os
    import shutil

    override = os.environ.get("EDGE_PATH")
    if override:
        p = Path(override)
        if p.is_file():
            return p

    candidates = [
        Path(os.environ.get("PROGRAMFILES(X86)", r"C:\Program Files (x86)"))
        / "Microsoft" / "Edge" / "Application" / "msedge.exe",
        Path(os.environ.get("PROGRAMFILES", r"C:\Program Files"))
        / "Microsoft" / "Edge" / "Application" / "msedge.exe",
        Path(os.environ.get("PROGRAMFILES", r"C:\Program Files"))
        / "Google" / "Chrome" / "Application" / "chrome.exe",
        Path(os.environ.get("LOCALAPPDATA", ""))
        / "Google" / "Chrome" / "Application" / "chrome.exe",
    ]
    for c in candidates:
        if c.is_file():
            return c

    for name in ("msedge", "chrome", "chromium"):
        found = shutil.which(name)
        if found:
            return Path(found)

    raise RuntimeError("Edge/Chrome not found; set EDGE_PATH to the executable")


EDGE = _find_edge()

# Raid maps shown by the application. Order is the sidebar order.
MAP_META = {
    "ground-zero": {
        "zh": "零号区域", "subtitle": "新手区域 · 高密度任务", "difficulty": "低",
        "players": "10–12", "minutes": 20,
        "desc": "塔科夫市郊的封锁区，低等级玩家的任务起点，也常有老玩家进入刷任务。",
    },
    "customs": {
        "zh": "海关", "subtitle": "工业区 · 经典战场", "difficulty": "中",
        "players": "10–12", "minutes": 35,
        "desc": "塔科夫最具代表性的地图，宿舍、红厂房与加油站构成主要交火区。",
    },
    "factory": {
        "zh": "工厂", "subtitle": "室内近战 · 快节奏", "difficulty": "高",
        "players": "4–6", "minutes": 20,
        "desc": "狭小多层工业厂房，近距离交火和 Boss Tagilla 的主场。",
    },
    "woods": {
        "zh": "森林", "subtitle": "开阔林地 · 远距离", "difficulty": "中",
        "players": "10–14", "minutes": 40,
        "desc": "视野开阔的森林与伐木场，适合狙击，也容易遭遇 Shturman。",
    },
    "interchange": {
        "zh": "立交桥", "subtitle": "超级商场 · 室内搜刮", "difficulty": "中",
        "players": "10–14", "minutes": 40,
        "desc": "U LTRA 购物中心与地下停车场，室内近战和电力开关是核心机制。",
    },
    "shoreline": {
        "zh": "海岸线", "subtitle": "度假村 · 多区域", "difficulty": "中",
        "players": "10–13", "minutes": 40,
        "desc": "从村庄、码头到气象站和疗养院，跨度极大，任务线密集。",
    },
    "reserve": {
        "zh": "储备站", "subtitle": "军事基地 · 地下 bunker", "difficulty": "高",
        "players": "9–12", "minutes": 40,
        "desc": "废弃军事基地，地上地下多层结构，Raider 与 Glukhar 出没。",
    },
    "the-lab": {
        "zh": "实验室", "subtitle": "高危室内 · 需要门卡", "difficulty": "极高",
        "players": "6–10", "minutes": 35,
        "desc": "TerraGroup 地下研究设施，Raider 密集，入场成本和风险都很高。",
    },
    "lighthouse": {
        "zh": "灯塔", "subtitle": "海岸长图 · 水电站", "difficulty": "高",
        "players": "9–12", "minutes": 40,
        "desc": "狭长的海岸地图，Rogues 驻守豪宅，Zryachiy 守着灯塔新区半岛。",
    },
    "streets-of-tarkov": {
        "zh": "塔科夫街区", "subtitle": "城市巷战 · 超大地图", "difficulty": "高",
        "players": "12–16", "minutes": 40,
        "desc": "塔科夫市中心街区，建筑密集、任务极多，是后期主要战场。",
    },
    "the-labyrinth": {
        "zh": "迷宫", "subtitle": "地下迷宫 · 高危", "difficulty": "极高",
        "players": "?–?", "minutes": 30,
        "desc": "塔科夫下方的古老迷宫，陷阱、Boss 与高价值战利品并存。",
    },
    "terminal": {
        "zh": "航站楼", "subtitle": "机场终端 · 新区域", "difficulty": "高",
        "players": "?–?", "minutes": 40,
        "desc": "塔科夫国际机场航站楼及周边区域，最新加入的战术地图之一。",
    },
    "icebreaker": {
        "zh": "破冰船", "subtitle": "极地船只 · 多层室内", "difficulty": "极高",
        "players": "?–?", "minutes": 35,
        "desc": "冰封海域中的巨型破冰船，多层船舱与狭窄通道构成垂直战场。",
    },
}

# tarkov.dev tile paths need a browser-looking request; no login is involved.
HEADERS = {
    "User-Agent": (
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
        "(KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36"
    ),
    "Accept": "image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8",
    "Accept-Language": "en-US,en;q=0.9",
    "Referer": "https://tarkov.dev/",
    "Sec-Fetch-Dest": "image",
    "Sec-Fetch-Mode": "no-cors",
    "Sec-Fetch-Site": "cross-site",
}
BG = (13, 18, 23)

# Maps whose base art is drawn into the interactive map's tile pixel grid.
# The labyrinth SVG only lines up through the calibrated warp into that grid,
# so its size/affine are reproduced from the projection formula (tile_grid) —
# no tiles are downloaded and none are painted into the artwork.
# Icebreaker ships full vector art (one SVG per deck, same viewBox); it is built
# from those decks so the base art and the floor overlays share one render
# identity.
TILE_MAPS = {"the-labyrinth"}

# Edge renders are CPU bound; four at a time keeps the peak memory (a few
# 5600 px RGBA canvases) sane on a normal desktop.
WORKERS = min(4, os.cpu_count() or 1)

# ---------------------------------------------------------------------------
# geometry helpers


def project(mm: dict, x: float, z: float, zoom: int) -> tuple[float, float]:
    """Game (x,z) -> tarkov.dev tile world pixel coordinates."""
    sx, mx, sy, my = mm["transform"]
    rot = math.radians(mm.get("coordinateRotation", 0) or 0)
    c, s = math.cos(rot), math.sin(rot)
    xp = x * c - z * s
    yp = x * s + z * c
    return (
        (sx * xp + mx) * (2**zoom),
        (-sy * yp + my) * (2**zoom),
    )


def projected_bounds(mm: dict, zoom: int) -> tuple[float, float, float, float]:
    b0, b1 = mm["bounds"]
    xs, ys = [], []
    for x in (b0[0], b1[0]):
        for z in (b0[1], b1[1]):
            px, py = project(mm, x, z, zoom)
            xs.append(px)
            ys.append(py)
    return min(xs), min(ys), max(xs), max(ys)


def tile_range(mm: dict, zoom: int, tile_size: int) -> tuple[int, int, int, int, int, int]:
    minx, miny, maxx, maxy = projected_bounds(mm, zoom)
    tx0 = math.floor(minx / tile_size)
    ty0 = math.floor(miny / tile_size)
    tx1 = math.floor((maxx - 1e-6) / tile_size)
    ty1 = math.floor((maxy - 1e-6) / tile_size)

    return tx0, ty0, tx1, ty1, tile_size, zoom


def rotate_affine(aff: list[float], w: int, h: int, quarter_turns_cw: int) -> tuple[list[float], int, int]:
    """Rotate an image and its affine matrix together (clockwise quarter turns)."""
    a, b, c, d, e, f = aff
    q = quarter_turns_cw % 4
    if q == 0:
        return [a, b, c, d, e, f], w, h
    if q == 1:  # new is W'=h, H'=w ; nx = h - y, ny = x
        return [-d, -e, h - f, a, b, c], h, w
    if q == 2:
        return [-a, -b, w - c, -d, -e, h - f], w, h
    # q == 3: nx = y, ny = w - x
    return [d, e, f, -a, -b, w - c], h, w


def official_orientation(aff: list[float], w: int, h: int) -> int:
    """Pick the quarter-turn that matches tarkov.dev's own presentation.

    The official render of every map (SVG or tile) maps game x to a *decreasing*
    image x and game z to an *increasing* image y, i.e. a < 0 and e > 0.
    Using a generic "north up" rule instead flips tile maps vertically, which is
    exactly the north/south inversion users reported.
    """
    best_q, best_score = 0, -1e18
    for q in range(4):
        na, nb, _, nd, ne, _ = rotate_affine(aff, w, h, q)[0]
        score = (-na) + ne - 8 * (abs(nb) + abs(nd))
        if score > best_score:
            best_q, best_score = q, score
    return best_q


# ---------------------------------------------------------------------------
# SVG rendering


def _thread_profile() -> str:
    """Stable Edge profile name for the calling worker thread."""
    ident = threading.get_ident()
    return f"w{ident}"


def top_level_group_ids(text: str) -> list[str]:
    """Ids of the `<g>` children directly under the root `<svg>` element.

    These files stack every floor as a top-level group, and each floor in turn
    contains nested groups (Floor, Walls, Stairs...). Hiding by *any* matching
    id therefore hides the children of the floor we want to keep and renders a
    blank canvas, so only the direct children may be considered. Parsed with
    ElementTree because the files are minified on some maps and pretty-printed
    on others, which makes indent-anchored regexes unreliable.
    """
    try:
        root = ET.fromstring(text)
    except ET.ParseError:
        return []
    ids: list[str] = []
    for child in root:
        if child.tag.rsplit("}", 1)[-1] != "g":
            continue
        gid = child.get("id")
        if gid:
            ids.append(gid)
    return ids


def render_svg(svg: Path, width: int, height: int, out_png: Path,
               transparent: bool = False, only_layer: str | None = None) -> None:
    """Rasterise an SVG through headless Edge.

    transparent=True keeps the page background clear so per-floor overlays can
    be composited on top of the base artwork; the default paints the app's dark
    surface behind full base maps.

    only_layer hides every other top-level layer group. Factory's SVG stacks
    all four floors in one document, so rendering it as-is showed several
    overlapping wall sets at once; the base art must pick exactly one.
    """
    uri = svg.resolve().as_uri()
    body_bg = "transparent" if transparent else f"rgb({BG[0]},{BG[1]},{BG[2]})"
    hide_css = ""
    if only_layer:
        text = svg.read_text(encoding="utf-8", errors="replace")
        ids = top_level_group_ids(text)
        if not ids:
            print(f"!! {svg.name}: no top-level groups parsed, rendering all layers",
                  flush=True)
        elif only_layer not in ids:
            print(f"!! {svg.name}: {only_layer!r} is not a top-level group",
                  flush=True)
        hide = [i for i in ids if i != only_layer]
        if hide:
            hide_css = "<style>" + "".join(
                f"#{i} {{ display: none !important; }}" for i in hide) + "</style>"
            # Serve the patched copy from the scratch dir instead of the source.
            scratch_dir = SCRATCH
            scratch_dir.mkdir(parents=True, exist_ok=True)
            patched = scratch_dir / f"{svg.stem}-only-{only_layer}-{uuid.uuid4().hex[:8]}.svg"
            body = text
            close = body.find(">")
            body = body[:close + 1] + hide_css + body[close + 1:]
            patched.write_text(body, encoding="utf-8")
            uri = patched.resolve().as_uri()
    doc = f"""<!doctype html><html><head><meta charset="utf-8"><style>
html,body{{margin:0;padding:0;width:100%;height:100%;overflow:hidden;background:{body_bg}}}
img{{display:block;width:100vw;height:100vh;object-fit:fill;image-rendering:auto}}
</style></head><body><img src="{html.escape(uri)}"></body></html>"""
    scratch = SCRATCH
    scratch.mkdir(parents=True, exist_ok=True)
    stamp = f"{svg.stem}-{width}x{height}-{'clear' if transparent else 'dark'}"
    # Unique per call: concurrent renders of the same layer would otherwise
    # overwrite each other's scratch HTML.
    tmp = scratch / f"render-{stamp}-{uuid.uuid4().hex[:8]}.html"
    tmp.write_text(doc, encoding="utf-8")
    # One profile per worker thread: Edge locks its user-data-dir, so sharing a
    # single profile would serialise every concurrent render.
    prof = _thread_profile()
    profile = scratch / f"edge-profile-{prof}"
    cmd = [
        str(EDGE),
        "--headless=new",
        "--disable-gpu",
        "--no-sandbox",
        "--hide-scrollbars",
        "--no-first-run",
        "--disable-extensions",
        "--default-background-color=00000000",
        f"--user-data-dir={profile}",
        f"--window-size={width},{height}",
        "--virtual-time-budget=8000",
        "--screenshot=" + str(out_png),
        tmp.resolve().as_uri(),
    ]
    try:
        subprocess.run(cmd, check=False, stdout=subprocess.DEVNULL,
                       stderr=subprocess.DEVNULL, timeout=120)
    except subprocess.TimeoutExpired:
        print(f"!! Edge timed out on {svg.name}", flush=True)
    tmp.unlink(missing_ok=True)
    if not out_png.exists() or out_png.stat().st_size < 1000:
        raise RuntimeError(f"Edge failed to render {svg.name}")


# tile stitching


def download_tile(session: requests.Session, url: str, path: Path) -> bool:
    if path.exists():
        # An empty cache file records an upstream 404; re-requesting it turns an
        # offline rebuild into hours of timeouts.
        return path.stat().st_size > 20
    for attempt in range(3):
        try:
            r = session.get(url, headers=HEADERS, timeout=45)
            if r.status_code == 200 and len(r.content) > 50:
                path.write_bytes(r.content)
                return True
            if r.status_code == 404:
                path.write_bytes(b"")
                return False
        except Exception:
            time.sleep(0.8 + attempt)
    return False


def stitch_tiles(mm: dict, key: str, zoom: int) -> tuple[Image.Image, list[float]]:
    """Download and composite one interactive-map tile layer into a canvas.

    Used by build_layers for floors tarkov.dev ships only as tiles (reserve
    2nd-5th, customs 4th). The labyrinth base art used to come from here too,
    but its grid is now derived analytically — see tile_grid.
    """
    tile_size = int(mm.get("tileSize") or 256)
    tx0, ty0, tx1, ty1, ts, z = tile_range(mm, zoom, tile_size)
    cols, rows = tx1 - tx0 + 1, ty1 - ty0 + 1
    canvas = Image.new("RGBA", (cols * ts, rows * ts), (0, 0, 0, 0))
    cache_dir = CACHE / key / str(z)
    cache_dir.mkdir(parents=True, exist_ok=True)

    jobs = []
    for ty in range(ty0, ty1 + 1):
        for tx in range(tx0, tx1 + 1):
            url = mm["tilePath"].replace("{z}", str(z)).replace("{x}", str(tx)).replace("{y}", str(ty))
            jobs.append((tx, ty, url))

    print(f"    downloading {len(jobs)} tiles at z{z} ({cols}x{rows}) ...", flush=True)
    session = requests.Session()
    ok = 0
    with ThreadPoolExecutor(max_workers=16) as pool:
        futs = {
            pool.submit(download_tile, session, url, cache_dir / f"{tx}_{ty}.webp"): (tx, ty)
            for tx, ty, url in jobs
        }
        for fut in as_completed(futs):
            tx, ty = futs[fut]
            try:
                if fut.result():
                    ok += 1
            except Exception:
                pass
            if (ok and ok % 250 == 0):
                print(f"      {ok}/{len(jobs)}", flush=True)

    for tx, ty, _ in jobs:
        p = cache_dir / f"{tx}_{ty}.webp"
        if not p.exists() or p.stat().st_size < 50:
            continue
        try:
            tile = Image.open(p).convert("RGBA")
        except Exception:
            continue
        if tile.size != (ts, ts):
            tile = tile.resize((ts, ts), Image.Resampling.LANCZOS)
        canvas.alpha_composite(tile, ((tx - tx0) * ts, (ty - ty0) * ts))

    minx, miny, maxx, maxy = projected_bounds(mm, z)

    # Exact affine from the projection formula, before any cropping/rotation.
    sx, mx, sy, my = mm["transform"]
    rot = math.radians(mm.get("coordinateRotation", 0) or 0)
    c, s = math.cos(rot), math.sin(rot)
    scale = 2**z
    a = sx * c * scale
    b = -sx * s * scale
    d = -sy * s * scale
    e = -sy * c * scale
    # The stitched canvas starts at tile (tx0, ty0), i.e. world pixel
    # (tx0*ts, ty0*ts). Shift the affine so its origin matches the canvas.
    raw_affine = [a, b, mx * scale - tx0 * ts, d, e, my * scale - ty0 * ts]

    # Rotate the artwork (and its affine) into the official orientation the app
    # expects; the rotation must happen BEFORE cropping so the crop offsets and
    # the affine stay consistent.
    b0x, b0z = mm["bounds"][0]
    b1x, b1z = mm["bounds"][1]
    q = official_orientation(raw_affine, canvas.width, canvas.height)
    if q:
        canvas = canvas.rotate(-90 * q, expand=True)
        raw_affine, nw, nh = rotate_affine(raw_affine, cols * ts, rows * ts, q)
    # Bounds corners in the (possibly rotated) canvas frame. These have to come
    # from the canvas affine, not projected_bounds(): the canvas starts at tile
    # (tx0, ty0), so for maps whose tiles begin at a negative coordinate the
    # world-space numbers are off by that origin and the crop cut real artwork.
    rxs = [raw_affine[0] * x + raw_affine[1] * z_ + raw_affine[2]
           for x in (b0x, b1x) for z_ in (b0z, b1z)]
    rys = [raw_affine[3] * x + raw_affine[4] * z_ + raw_affine[5]
           for x in (b0x, b1x) for z_ in (b0z, b1z)]
    minx, miny, maxx, maxy = min(rxs), min(rys), max(rxs), max(rys)

    pad = 24
    left = max(0, math.floor(minx) - pad)
    top = max(0, math.floor(miny) - pad)
    right = min(canvas.width, math.ceil(maxx) + pad)
    bottom = min(canvas.height, math.ceil(maxy) + pad)
    canvas = canvas.crop((left, top, right, bottom))
    raw_affine[2] -= left
    raw_affine[5] -= top
    return canvas, raw_affine


def tile_grid(mm: dict, zoom: int) -> tuple[tuple[int, int], list[float]]:
    """The pixel grid a stitched tile canvas would have — without downloading.

    Tile stitching is pure geometry: the canvas size and its affine follow from
    the projection formula and the tile range, not from the tile pixels. The
    labyrinth needs that grid (its SVG only lines up through the calibrated
    warp into it), but it no longer needs the 240 tile downloads that used to
    provide the grid before being painted over.
    """
    tile_size = int(mm.get("tileSize") or 256)
    tx0, ty0, tx1, ty1, ts, z = tile_range(mm, zoom, tile_size)
    cols, rows = tx1 - tx0 + 1, ty1 - ty0 + 1

    sx, mx, sy, my = mm["transform"]
    rot = math.radians(mm.get("coordinateRotation", 0) or 0)
    c, s = math.cos(rot), math.sin(rot)
    scale = 2**z
    raw_affine = [sx * c * scale, -sx * s * scale, mx * scale - tx0 * ts,
                  -sy * s * scale, -sy * c * scale, my * scale - ty0 * ts]

    # Rotate the grid into the official orientation, then crop to the bounds
    # rectangle (plus pad) — the same numbers stitch_tiles crops by.
    b0x, b0z = mm["bounds"][0]
    b1x, b1z = mm["bounds"][1]
    q = official_orientation(raw_affine, cols * ts, rows * ts)
    if q:
        raw_affine, nw, nh = rotate_affine(raw_affine, cols * ts, rows * ts, q)
    gw, gh = (nw, nh) if q else (cols * ts, rows * ts)

    rxs = [raw_affine[0] * x + raw_affine[1] * z_ + raw_affine[2]
           for x in (b0x, b1x) for z_ in (b0z, b1z)]
    rys = [raw_affine[3] * x + raw_affine[4] * z_ + raw_affine[5]
           for x in (b0x, b1x) for z_ in (b0z, b1z)]
    pad = 24
    left = max(0, math.floor(min(rxs)) - pad)
    top = max(0, math.floor(min(rys)) - pad)
    right = min(gw, math.ceil(max(rxs)) + pad)
    bottom = min(gh, math.ceil(max(rys)) + pad)
    raw_affine[2] -= left
    raw_affine[5] -= top
    return (right - left, bottom - top), raw_affine


def sanitize_affine(aff: list[float]) -> list[float]:
    """Force a single scale on both axes.

    A world->image transform for these maps is a pure rotate/scale/flip: world
    units are metres and the artwork keeps its aspect ratio, so |sx| must equal
    |sy|. Icebreaker ships sx=-32 and sy=56 (it reuses Factory's placeholder
    transform), which stretched the map and threw every marker outside the
    picture. Anything anisotropic is a data bug, so keep the horizontal scale
    and let the vertical axis mirror its magnitude.
    """
    if len(aff) != 6:
        return aff
    a, b, c, d, e, f = aff
    sx, sy = abs(a), abs(e)
    if sx < 1e-9 or sy < 1e-9:
        return aff
    if abs(sx - sy) <= 1e-6 * max(sx, sy):
        return aff
    if abs(b) > 1e-9 or abs(d) > 1e-9:
        # Rotated matrices carry their scale in b/d; leave those alone.
        return aff
    sign = -1.0 if e < 0 else 1.0
    return [a, b, c, d, sign * sx, f]


def uniform_transform(mm: dict) -> dict:
    """Copy of *mm* whose tile transform scales both axes the same.

    Icebreaker reuses Factory's placeholder transform, whose y scale (3.5) is
    1.75x its x scale (2.0). Stitching with that stretches the artwork and
    requests a tile range that is 1.75x too tall. The tile layer itself is
    uniform, so keep the x scale for both axes.
    """
    t = mm.get("transform")
    if not t or len(t) != 4:
        return mm
    sx, mx, sy, my = t
    if abs(abs(sx) - abs(sy)) <= 1e-9 * max(1.0, abs(sx)):
        return mm
    out = dict(mm)
    out["transform"] = [sx, mx, abs(sx) if sy >= 0 else -abs(sx), my]
    return out


def svg_ratio(svg: Path) -> float | None:
    """Intrinsic width/height of the SVG root.

    An <img> keeps the SVG's aspect ratio and letterboxes whatever is left of
    the window. The affine below assumes the artwork fills the canvas, so a
    letterboxed render moves every marker off its room (the Labyrinth lost 4 of
    7). Render at this ratio and stretch afterwards, like tarkov.dev's overlay.
    """
    head = svg.read_text(encoding="utf-8", errors="replace")[:2000]

    def attr(name: str) -> float | None:
        m = re.search(rf'(?:^|\s){name}="([\d.]+)"', head)
        return float(m.group(1)) if m else None

    w, h = attr("width"), attr("height")
    if w and h and h > 0:
        return w / h
    vb = re.search(r'(?:^|\s)viewBox="([^"]+)"', head)
    if vb:
        parts = re.split(r"[\s,]+", vb.group(1).strip())
        if len(parts) == 4 and float(parts[3]) > 0:
            return float(parts[2]) / float(parts[3])
    return None


def ground_group(svg: Path) -> str | None:
    """The floor shown by default; these SVGs stack every floor in one file.

    Rendering the whole document drew all floors at once (doubled walls), which
    is why the base art must pick one group. The upper floors are separate
    layers in the picker.
    """
    ids = top_level_group_ids(svg.read_text(encoding="utf-8", errors="replace"))
    for name in ("Ground_Level", "Ground_Floor", "First_Level"):
        if name in ids:
            return name
    return None


def svg_turn(mm: dict, api_entry: dict | None) -> int:
    """Degrees the artwork must be rotated to match the cardinal frame.

    tarkov.dev authors the SVG in the `coordinateRotation` frame and stretches it
    over the cardinal bounds. Factory and the Labyrinth differ; the rest are 0.
    """
    cardinal = (api_entry or {}).get("coordinateToCardinalRotation")
    if cardinal is None:
        cardinal = mm.get("coordinateRotation") or 0
    return (cardinal - (mm.get("coordinateRotation") or 0)) % 360


def svg_canvas(svg: Path, width: int, height: int, turn: int,
               only_layer: str | None = None,
               transparent: bool = False) -> Image.Image:
    """Rasterise an SVG onto the pixel grid the affine assumes.

    Rendering straight into (width, height) letterboxes whenever the SVG's own
    aspect ratio differs, and the affine then puts every marker off its room.
    Render at the SVG's ratio, apply the same turn as the base art, then stretch.
    Base art and floors share this path so a layer can never drift from its base.
    """
    ratio = svg_ratio(svg)
    if ratio is None:
        rw, rh = width, height
    elif ratio >= 1:
        rw, rh = width, max(1, round(width / ratio))
    else:
        rw, rh = max(1, round(height * ratio)), height
    SCRATCH.mkdir(parents=True, exist_ok=True)
    raw = SCRATCH / f"canvas-{uuid.uuid4().hex[:8]}.png"
    render_svg(svg, rw, rh, raw, transparent=transparent, only_layer=only_layer)
    img = Image.open(raw).convert("RGBA")
    raw.unlink(missing_ok=True)
    if turn:
        # Rotate about the centre and clip back to the artwork frame, the way
        # tarkov.dev's overlay is clipped by the map bounds. Expanding without
        # clipping shrinks the map inside dark corners and desyncs the affine.
        img = img.rotate(turn, expand=True)
        left = (img.width - rw) // 2
        top = (img.height - rh) // 2
        img = img.crop((left, top, left + rw, top + rh))
    if img.size != (width, height):
        img = img.resize((width, height), Image.Resampling.LANCZOS)
    return img


def box_to_game(aff: list[float], box: tuple[int, int, int, int]) -> list[float]:
    """Game-space extent of an arbitrary pixel box, through the inverse affine."""
    a, b, c, d, e, f = aff
    det = a * e - b * d
    if abs(det) < 1e-12:
        return [0.0, 0.0, float(box[2]), float(box[3])]
    xs, zs = [], []
    for px, py in ((box[0], box[1]), (box[2], box[1]), (box[0], box[3]), (box[2], box[3])):
        u, v = px - c, py - f
        xs.append((e * u - b * v) / det)
        zs.append((-d * u + a * v) / det)
    return [min(xs), min(zs), max(xs), max(zs)]


def bounds_from_affine(aff: list[float], w: int, h: int) -> list[float]:
    """Game-space extent actually covered by the produced image.

    tarkov.dev's manifest bounds are placeholders for some tile maps (Icebreaker
    reuses Factory's numbers), so derive the bounds from the image rectangle
    through the inverse affine instead of trusting them.
    """
    return box_to_game(aff, (0, 0, w, h))


def content_box(img: Image.Image) -> tuple[int, int, int, int] | None:
    """Pixel box of the drawn artwork, ignoring the flat background.

    Several baked canvases are mostly empty (Icebreaker's hull occupies a fifth
    of its tile canvas, the Labyrinth SVG has a wide border). The app fits this
    box instead of the whole bitmap, so the empty area never fills the view.

    The mask is averaged into 16 px cells first: one JPEG ringing pixel at the
    edge must not stretch the box over the whole canvas.
    """
    small = img.resize((max(1, img.width // 16), max(1, img.height // 16)), Image.BOX)
    diff = ImageChops.difference(small, Image.new("RGB", small.size, BG)).convert("L")
    mask = diff.point(lambda v: 255 if v > 12 else 0)
    box = mask.getbbox()
    if box is None:
        return None
    return (box[0] * 16, box[1] * 16,
            min(img.width, box[2] * 16), min(img.height, box[3] * 16))


# ---------------------------------------------------------------------------
# main build


# ---------------------------------------------------------------------------
# main build


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    CACHE.mkdir(parents=True, exist_ok=True)
    data = json.loads(DATA_MAPS.read_text(encoding="utf-8"))
    by_name = {m["normalizedName"]: m for m in data}
    # Marker points live in the API snapshot, not in the repo geometry file.
    api_root = json.loads((RESEARCH / "api" / "maps.json").read_text(encoding="utf-8"))["data"]["maps"]
    api_by_name = {m.get("normalizedName"): m for m in api_root.values()}
    manifest = []

    # --only a,b limits the rebuild to those map keys; every other entry is
    # carried over from the existing manifest so a partial run stays valid.
    only = None
    for i, arg in enumerate(sys.argv):
        if arg == "--only" and i + 1 < len(sys.argv):
            only = {x.strip() for x in sys.argv[i + 1].split(",") if x.strip()}

    previous = {}
    manifest_path = OUT / "maps.json"
    if only and manifest_path.exists():
        for entry in json.loads(manifest_path.read_text(encoding="utf-8")).get("maps", []):
            previous[entry["key"]] = entry

    # Each map is an independent Edge render, so build them concurrently. Edge
    # is CPU bound and every worker gets its own profile (_thread_profile), which
    # is what keeps a full rebuild at minutes instead of tens of minutes.
    def build(idx: int, key: str, meta: dict) -> dict | None:
        if only and key not in only:
            return previous.get(key)
        m = by_name.get(key)
        if not m:
            print(f"!! map {key} missing from tarkov.dev data", file=sys.stderr)
            return None
        mm = next((x for x in m["maps"] if x.get("projection") == "interactive"), None)
        if not mm:
            print(f"!! map {key} has no interactive definition", file=sys.stderr)
            return None
        b0, b1 = mm["bounds"]
        minx, maxx = sorted((b0[0], b1[0]))
        minz, maxz = sorted((b0[1], b1[1]))
        rx, rz = maxx - minx, maxz - minz
        print(f"[{idx}/{len(MAP_META)}] {key}: {rx:.0f} x {rz:.0f} game units", flush=True)

        svg_path = svg_source(key)

        if key in TILE_MAPS:
            # The labyrinth SVG only lines up through the calibrated warp into
            # the interactive map's tile pixel grid, so that grid is reproduced
            # from the projection formula (tile_grid) instead of downloading
            # the tiles and painting over them.
            zoom = 4
            size, aff = tile_grid(uniform_transform(mm), zoom)
            # Upstream ships placeholder transforms for some maps, so the emitted
            # matrix has to be made isotropic before bounds/content are derived
            # from it.
            aff = sanitize_affine(aff)
            mode = "tile grid (vector art)"
            if key == "the-labyrinth":
                # The labyrinth tiles and Labyrinth.svg are authored in different
                # orientations, and tarkov.dev ships no svgLayer for this map, so
                # the generic SVG branch (bounds-stretch) puts every marker off
                # its room (0/7 spawns). The measured mapping below was fit by
                # anchors (5 spawns + 2 extracts transcribed into the eftarkov
                # 1300x1200 frame): warp the SVG artwork into the tile pixel
                # grid instead, keeping the tile affine. 7/7 anchors and 90/90
                # POIs land on artwork; wall overlap with the tile render 85%.
                a11, a12, a21, a22, b1, b2 = (-3.39591, 0.005, -0.00438,
                                              3.37302, 3851.105, -333.2)
                if np is None:
                    print("!! numpy missing: keeping labyrinth tiles", file=sys.stderr)
                else:
                    RENDER = 2600  # svg pixels across; eftarkov frame is 1300
                    svg_canvas_path = SCRATCH / "laby-hi.png"
                    render_svg(svg_path, RENDER, round(RENDER * 1200 / 1300),
                               svg_canvas_path)
                    # forward (render px -> tile px): ef = px/RENDER*1300, then
                    # t = M @ ef + b. PIL wants the inverse map as coefficients.
                    half = 1300 / RENDER
                    Af = np.array([[a12 * half, a11 * half],
                                   [a22 * half, a21 * half]], dtype=np.float64)
                    Minv = np.linalg.inv(Af)
                    q = Minv @ np.array([b1, b2])
                    coeffs = (float(Minv[0, 0]), float(Minv[0, 1]), float(-q[0]),
                              float(Minv[1, 0]), float(Minv[1, 1]), float(-q[1]))
                    art = Image.open(svg_canvas_path).convert("RGBA")
                    tw, th = size
                    warped = art.transform((tw, th), Image.AFFINE, coeffs,
                                           resample=Image.BICUBIC)
                    flat = Image.new("RGB", (tw, th), BG)
                    flat.paste(warped, mask=warped.getchannel("A"))
                    img = flat
                    svg_canvas_path.unlink(missing_ok=True)
                    mode = f"{mode} + svg overlay"
        elif svg_path and svg_path.exists():
            # The artwork is authored in the frame rotated by coordinateRotation,
            # and tarkov.dev stretches it over the map's bounds rectangle. North-up
            # is the *cardinal* frame (coordinateToCardinalRotation), which differs
            # from the artwork frame for Factory (90) and the Labyrinth (270 -> 180).
            # The 180-degree maps need no turn, so their output is unchanged.
            turn = svg_turn(mm, api_by_name.get(key))
            cardinal = (api_by_name.get(key) or {}).get("coordinateToCardinalRotation")
            if cardinal is None:
                cardinal = mm.get("coordinateRotation") or 0
            ct = math.cos(math.radians(cardinal))
            st = math.sin(math.radians(cardinal))
            east = [x * ct - z * st for x in (minx, maxx) for z in (minz, maxz)]
            north = [x * st + z * ct for x in (minx, maxx) for z in (minz, maxz)]
            ew, nh = max(east) - min(east), max(north) - min(north)
            aspect = ew / nh
            if aspect >= 1:
                w = 5600
                h = max(800, round(w / aspect))
            else:
                h = 5600
                w = max(800, round(h * aspect))
            # These SVGs stack every floor in one document; rendering the whole
            # file drew all of them at once (doubled walls). The base art is the
            # default floor, the rest are layers in the picker.
            base_layer = ground_group(svg_path)
            img = svg_canvas(svg_path, w, h, turn, only_layer=base_layer)
            s = w / ew
            aff = [s * ct, -s * st, -s * min(east), -s * st, -s * ct, s * max(north)]
            mode = "svg"
        else:
            print(f"!! no source for {key}", file=sys.stderr)
            return None

        # Flatten transparency onto the app's dark surface.
        bg = Image.new("RGB", img.size, BG)
        if img.mode == "RGBA":
            bg.paste(img, mask=img.getchannel("A"))
        else:
            bg.paste(img.convert("RGB"))

        # A solid canvas is what a broken layer filter looks like: Edge exits 0,
        # the PNG is big enough to pass the size check, and the map ships empty.
        # Every real map has thousands of colours, so a handful means failure.
        flat = bg.getcolors(maxcolors=8)
        if flat is not None and len(flat) == 1:
            raise RuntimeError(f"{key}: base art rendered blank (layer filter?)")

        out_img = OUT / f"{key}.jpg"
        bg.save(out_img, "JPEG", quality=90, optimize=True, progressive=True)
        print(f"    -> {out_img.name} {bg.width}x{bg.height} {out_img.stat().st_size/1e6:.2f} MB", flush=True)

        # Labels are stored in game coordinates and rendered by Avalonia, so
        # they remain crisp at every zoom level.
        labels = []
        for lab in mm.get("labels", []) or []:
            if not lab.get("position"):
                continue
            labels.append({
                "x": lab["position"][0],
                "z": lab["position"][1],
                "text": lab.get("text", ""),
                "size": lab.get("size", 100),
                "rotation": float(lab.get("rotation", 0) or 0),
            })

        return {
            "key": key,
            "name": m.get("name") or key,
            "nameZh": meta["zh"],
            "subtitle": meta["subtitle"],
            "description": meta["desc"],
            "difficulty": meta["difficulty"],
            "players": meta["players"],
            "raidMinutes": meta["minutes"],
            "image": f"{key}.jpg",
            "width": bg.width,
            "height": bg.height,
            "affine": aff,
            "bounds": bounds_from_affine(aff, bg.width, bg.height),
            "labels": labels,
            "source": "tarkov-dev-svg-maps" if mode == "svg" else "tarkov.dev tile layer",
            "order": idx,
        }

    with ThreadPoolExecutor(max_workers=WORKERS) as pool:
        built = list(pool.map(lambda j: build(j[0], *j[1]),
                              enumerate(MAP_META.items(), start=1)))
    manifest = [entry for entry in built if entry]

    # Derive the drawn-artwork rect for every entry (including the ones carried
    # over by --only) so the app can fit the artwork instead of the raw canvas.
    for entry in manifest:
        path = OUT / entry["image"]
        if not path.exists():
            entry["content"] = []
            continue
        with Image.open(path) as im:
            box = content_box(im.convert("RGB"))
        # Pixels, not game coordinates: a rotated affine cannot represent the
        # artwork rect as an axis-aligned world rect, and the app fits pixels.
        entry["content"] = [float(v) for v in box] if box else []

    # newline="" keeps LF: git strips CRLF on commit and the noise hides real
    # diffs in the generated manifest.
    (OUT / "maps.json").write_text(
        json.dumps({"generatedAt": time.strftime("%Y-%m-%d"), "maps": manifest}, ensure_ascii=False, indent=2),
        encoding="utf-8",
        newline="",
    )
    print(f"wrote {OUT/'maps.json'} with {len(manifest)} maps", flush=True)


if __name__ == "__main__":
    main()
