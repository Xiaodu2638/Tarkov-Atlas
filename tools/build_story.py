#!/usr/bin/env python3
"""Rebuild TarkovAtlas/Assets/story.json from the eftarkov.com storyline.

Inputs (committed under research/, not shipped):
  research/story/zh-raw.json   Chinese chapter + objective scrape
  research/story/coords.json   validated objective coordinates + map-id table

The eftarkov objective list is the authoritative Chinese text. Coordinates are
only emitted for objectives listed in `coords.json`, which holds the points that
were validated against this app's own tarkov.dev label positions. Everything
else keeps its map (so the UI can still jump to it) but carries no coordinate:
a wrong marker is worse than no marker.
"""
from __future__ import annotations

import json
import pathlib

ROOT = pathlib.Path(__file__).resolve().parents[1]
RES = ROOT / "research"
ASSETS = ROOT / "TarkovAtlas" / "Assets"
MAPS = ASSETS / "maps"
RAW = RES / "story" / "zh-raw.json"
COORDS = RES / "story" / "coords.json"

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


def main() -> None:
    raw = json.loads(RAW.read_text(encoding="utf-8"))
    table = json.loads(COORDS.read_text(encoding="utf-8"))
    map_ids = table["maps"]
    points = table["objectives"]

    manifest = json.loads((MAPS / "maps.json").read_text(encoding="utf-8"))["maps"]
    map_names = {m["key"]: m.get("nameZh") or m["key"] for m in manifest}

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
            map_key = map_ids.get(obj.get("map") or "", "")
            if map_key and map_key not in used_maps:
                used_maps.append(map_key)
            point = points.get(str(obj.get("id") or ""))
            x, z = (point["x"], point["z"]) if point else (0.0, 0.0)
            objectives.append({
                "id": str(obj.get("id") or ""),
                "text": text,
                "map": map_key,
                "mapName": map_names.get(map_key, map_key),
                "x": round(float(x), 2),
                "z": round(float(z), 2),
                "hasCoordinates": point is not None,
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
        json.dumps(db, ensure_ascii=False, indent=1), encoding="utf-8", newline="")

    total = sum(len(c["objectives"]) for c in out)
    located = sum(o["hasCoordinates"] for c in out for o in c["objectives"])
    mapped = sum(bool(o["map"]) for c in out for o in c["objectives"])
    print(f"chapters={len(out)} objectives={total} with-map={mapped} with-coords={located}")
    for c in out:
        print(f"  {c['id']:32s} {c['nameZh']:12s} {len(c['objectives']):3d} objs")


if __name__ == "__main__":
    main()
