#!/usr/bin/env python3
"""Rebuild TarkovAtlas/Assets/story.json from the eftarkov.com storyline pages.

Inputs (local research data, not shipped):
  research/story_zh_raw.json          Chinese chapter + objective scrape
  research/scratch/maps-ef/<key>.json eftarkov map configs (marker table)
  research/scratch/cal-<key>.json     validated pixel -> game-coordinate fits

The eftarkov objective list is the authoritative Chinese text. Marker
coordinates are only emitted for maps whose fit was independently validated
against this app's own label positions (see research/scratch/label-fit.txt).
Objectives on maps without a trustworthy fit keep their map (so the UI can
still jump to the map) but carry no coordinates, rather than inventing them.
"""
from __future__ import annotations

import json
import pathlib
import re

ROOT = pathlib.Path(__file__).resolve().parents[1]
RES = ROOT / "research"
SCRATCH = RES / "scratch"
ASSETS = ROOT / "TarkovAtlas" / "Assets"

# eftarkov's storyline id -> stable local chapter id / English title.
CHAPTERS = {
    "68cbcdc4c964ab83cc0c928e": ("story-falling-skies", "Falling Skies"),
    "68cbd33676fe74b1e80bfd91": ("story-tour", "Tour"),
    "68da33fe00868edcb6025ac4": ("story-the-ticket", "The Ticket"),
    "68da36cf7cff54fc6109874a": ("story-batya", "Batya"),
    "68e3a35002661eb2d30ce387": ("story-the-labyrinth", "The Labyrinth"),
    "68e784b7fa3f1fa3770094ba": ("story-blue-fire", "Blue Fire"),
    "6900927ab7d28358f80b9421": ("story-the-unheard", "The Unheard"),
    "6903d779fdfc4078740a4bd0": ("story-they-are-already-here", "They Are Already Here"),
    "69052e18e680c2d3e3034d3a": ("story-accidental-witness", "Accidental Witness"),
    "69d38381cea4b428690ea1d9": ("story-boreas", "Boreas"),
}

# Fits validated both by quest-marker inliers and by this app's own labels.
# Anything else is map-only: a wrong coordinate is worse than no coordinate.
TRUSTED = {
    "customs", "woods", "shoreline", "streets-of-tarkov",
    "lighthouse", "ground-zero", "interchange",
}


def apply_fit(cal: dict, px: float, py: float) -> tuple[float, float]:
    mode = cal["mode"]
    if mode == 0:
        u, v = px, py
    elif mode == 90:
        u, v = py, px
    elif mode == 180:
        u, v = -px, -py
    else:
        u, v = -py, -px
    return cal["k"][0] * u + cal["b"][0], cal["k"][1] * v + cal["b"][1]


def marker_index(key: str) -> dict[str, dict]:
    path = SCRATCH / "maps-ef" / f"{key}.json"
    if not path.exists():
        return {}
    cfg = json.loads(path.read_text(encoding="utf-8"))
    return {str(m["id"]): m for m in cfg.get("markers", [])}


def main() -> None:
    raw = json.loads((RES / "story_zh_raw.json").read_text(encoding="utf-8"))
    maps_manifest = json.loads((ASSETS / "Maps" / "maps.json").read_text(encoding="utf-8"))["maps"]
    map_names = {m["key"]: m.get("nameZh") or m["key"] for m in maps_manifest}

    # eftarkov map id -> local map key
    from_id = {}
    for key in map_names:
        # build_story stays independent from build_data; recover ids from configs
        pass
    for cfg_path in (SCRATCH / "maps-ef").glob("*.json"):
        key = cfg_path.stem
        try:
            cfg = json.loads(cfg_path.read_text(encoding="utf-8"))
            from_id[cfg["mapId"]] = key
        except Exception:
            continue

    cal = {}
    for key in TRUSTED:
        path = SCRATCH / f"cal-{key}.json"
        if path.exists():
            cal[key] = json.loads(path.read_text(encoding="utf-8"))

    out = []
    for order, chapter in enumerate(raw):
        href = chapter["href"].rstrip("/").split("/")[-1]
        cid, english = CHAPTERS.get(href, (f"story-{href}", chapter.get("name", href)))
        objectives = []
        used_maps: list[str] = []
        for obj in chapter.get("objectives", []):
            text = (obj.get("title") or "").strip()
            if not text:
                continue
            map_key = from_id.get(obj.get("map") or "", "")
            has_coords = False
            x = z = 0.0
            if map_key:
                if map_key not in used_maps:
                    used_maps.append(map_key)
                marker = marker_index(map_key).get(str(obj.get("marker") or ""))
                if marker and map_key in cal:
                    x, z = apply_fit(cal[map_key], float(marker["x"]), float(marker["y"]))
                    has_coords = True
            objectives.append({
                "text": text,
                "map": map_key,
                "mapName": map_names.get(map_key, map_key),
                "x": round(x, 2),
                "z": round(z, 2),
                "hasCoordinates": has_coords,
            })

        out.append({
            "id": cid,
            "name": english,
            "nameZh": chapter.get("name") or english,
            "order": order,
            "maps": used_maps,
            "mapNames": [map_names.get(k, k) for k in used_maps],
            "objectives": objectives,
            "objectiveCount": len(objectives),
        })

    db = {
        "generatedAt": "2026-10-28",
        "source": "https://www.eftarkov.com/storyline (Chinese objectives); "
                  "marker coordinates calibrated against this app's map labels",
        "chapters": out,
    }
    (ASSETS / "story.json").write_text(
        json.dumps(db, ensure_ascii=False, indent=1), encoding="utf-8")

    total = sum(len(c["objectives"]) for c in out)
    located = sum(o["hasCoordinates"] for c in out for o in c["objectives"])
    mapped = sum(bool(o["map"]) for c in out for o in c["objectives"])
    print(f"chapters={len(out)} objectives={total} with-map={mapped} with-coords={located}")
    for c in out:
        print(f"  {c['id']:32s} {c['nameZh']:12s} {len(c['objectives']):3d} objs")


if __name__ == "__main__":
    main()
