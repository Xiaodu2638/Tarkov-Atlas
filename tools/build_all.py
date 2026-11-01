#!/usr/bin/env python3
"""Rebuild everything the app ships - the one entry point for the data pipeline.

    python tools/build_all.py                 # full rebuild (offline)
    python tools/build_all.py --only factory  # one map's art + floors
    python tools/build_all.py --skip-quests   # art only (no task/story data)
    python tools/build_all.py --check         # rebuild + all gates (check.ps1, coverage)

Every step here is OFFLINE: inputs are the committed snapshots under research/
(eftarkov.com tasks/story, tarkov.dev coordinate overlay, SVG vector sources).
Nothing in this script needs the network or an agent. To refresh the snapshots
from eftarkov.com, run tools/fetch_eftarkov.py first (see tools/update_data.py
for the fetch -> rebuild -> gate -> rollback orchestration).

Steps run in dependency order:
  1. build_assets.py   base JPGs + maps.json (headless Edge rasterises research/svg)
  2. build_layers.py   floor PNGs + layers/index.json + hazards.json
  3. boss_relocate.py  apply curated Boss fixes to the API snapshot (idempotent;
                       a re-fetched snapshot would otherwise wipe them)
  4. build_data.py     mapdata.json / markers.json / quests.json
  5. build_story.py    story.json

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
    check = "--check" in argv
    for i, a in enumerate(argv):
        if a == "--only" and i + 1 < len(argv):
            only = ["--only", argv[i + 1]]
        elif a == "--skip-quests":
            skip_quests = True

    # 1. Base artwork + per-map manifest.
    run("build_assets.py", only)

    # 2. Floor overlays + hazards (icebreaker's floors come from the deck SVGs
    #    handled inside this same script).
    run("build_layers.py", only)

    # 3. Quest / trader data (independent of the artwork).
    if not skip_quests:
        # The relocation table edits research/api/ and must land before
        # build_data.py consumes the snapshot; it is idempotent, so running it
        # on every rebuild is free even when the snapshot was not re-fetched.
        run("boss_relocate.py", [])
        run("build_data.py", [])
        run("build_story.py", [])

    # 4. Optional gates: the three self-check suites + map coverage.
    if check:
        print("\n=== check.ps1 ===", flush=True)
        r = subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass",
                            "-File", str(TOOLS / "check.ps1")], cwd=ROOT)
        if r.returncode != 0:
            raise SystemExit("check.ps1 failed")
        run("check_map_coverage.py", [])

    print("\nall build steps finished", flush=True)


if __name__ == "__main__":
    main()
