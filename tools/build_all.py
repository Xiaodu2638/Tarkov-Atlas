#!/usr/bin/env python3
"""Rebuild every map asset (base art, floors, hazards, quest data).

One entry point for the whole data pipeline so nobody has to remember the
order. Steps run in dependency order; pass --only to rebuild a single map's
base art and floors (other maps keep their current output), or --skip-quests
when only map art changed.

    python tools/build_all.py                 # full rebuild
    python tools/build_all.py --only factory  # one map's art + floors
    python tools/build_all.py --skip-quests   # art only (no quests.json)

Requires the vendored Pillow/numpy in tools/pyext and tools/pylibs, and a
headless Edge install for SVG rasterisation (see AGENTS.md).
"""
from __future__ import annotations

import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
TOOLS = ROOT / "tools"


def run(script: str, args: list[str]) -> None:
    cmd = [sys.executable, str(TOOLS / script), *args]
    print(f"\n=== {script} {' '.join(args)} ===", flush=True)
    r = subprocess.run(cmd)
    if r.returncode != 0:
        raise SystemExit(f"{script} failed with exit code {r.returncode}")


def main() -> None:
    argv = sys.argv[1:]
    only: list[str] = []
    skip_quests = False
    for i, a in enumerate(argv):
        if a == "--only" and i + 1 < len(argv):
            only = ["--only", argv[i + 1]]
        elif a == "--skip-quests":
            skip_quests = True

    # 1. Base artwork + per-map manifest.
    run("build_assets.py", only)

    # 2. Floor overlays + hazards (skips icebreaker internally; its floors come
    #    from the deck SVGs handled inside this same script).
    run("build_layers.py", only)

    # 3. Quest / trader data (independent of the artwork).
    if not skip_quests:
        run("build_data.py", [])
        run("build_story.py", [])

    print("\nall build steps finished", flush=True)


if __name__ == "__main__":
    main()
