#!/usr/bin/env python3
"""Fit per-map-and-floor affine transforms: eftarkov.com SVG pixels -> game meters.

Fully offline. Reads the committed site snapshot (research/eftarkov/), the app's
own label/zone data, and writes research/eftarkov/pxaffine.json, which
build_data.py uses to place objective pins for tasks tarkov.dev has no
coordinates for (the 2026 halloween / PVE-zone batch).

Fitting is per (map, floor): the-lab / factory / icebreaker stack several floors
on ONE pixel grid, so two markers on different decks can share a pixel while
sitting metres apart in the world. A single flat affine over the whole map mixes
those decks and fails leave-one-out (the-lab: 52 m median), so floor is part of
the key and an unknown floor simply yields no pin.

Anchor sources per map floor (all site pixel space <-> game space we already trust):
  1. storyline markers   coords.json game points + zh-raw marker ids + the
                         marker's pixel/floor in mapmarkers.json
  2. 任务地点 markers     site task-location markers with a task_id whose zone is
                         UNIQUE on that map in the tarkov.dev overlay (one
                         candidate point -> unambiguous game position)
  3. 地图标注 labels      site label markers whose title equals a mapdata.json
                         label. Label residuals run 13-34 m on their own (our
                         labels come from approximate tarkov.dev positions), so
                         they are consensus candidates, never seeds.

Seeds (1,2) start the fit; candidates join when they agree within 10 m, then
re-fit. The result must pass leave-one-out: median error <= 5 m and max <= 10 m
(the story-calibration bar), else that map floor gets no transform.
宁可不标，也不编坐标 - a missing pin beats a wrong one.

    python tools/site_calibrate.py
"""
from __future__ import annotations

import json
import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parents[1]
TOOLS = ROOT / "tools"
RES = ROOT / "research"
EF = RES / "eftarkov"
ASSETS = ROOT / "TarkovAtlas" / "Assets"

sys.path.insert(0, str(TOOLS / "pylibs"))
sys.path.insert(0, str(TOOLS))
import numpy as np  # noqa: E402


def load(p: pathlib.Path):
    return json.loads(p.read_text(encoding="utf-8"))


def norm_title(s: str) -> str:
    return (s or "").lower().replace(" ", "").replace("（", "(").replace("）", ")")


# ---------------------------------------------------------------- fitting


def fit_affine(src, dst):
    """Least squares 6-param affine (px,py)->(x,z)."""
    A = np.array([[x, y, 1.0] for x, y in src])
    bx = np.array([p[0] for p in dst])
    bz = np.array([p[1] for p in dst])
    cx, *_ = np.linalg.lstsq(A, bx, rcond=None)
    cz, *_ = np.linalg.lstsq(A, bz, rcond=None)
    return {"a": cx[0], "b": cx[1], "c": cx[2], "d": cz[0], "e": cz[1], "f": cz[2]}


def apply_affine(aff, px, py):
    return (aff["a"] * px + aff["b"] * py + aff["c"],
            aff["d"] * px + aff["e"] * py + aff["f"])


def fit_similarity3(src, dst):
    """3 points: complex similarity w and mirrored w*; pick the exact solver."""
    z = np.array([x + 1j * y for x, y in dst])
    w = np.array([x + 1j * y for x, y in src])
    best = None
    for ww in (w, np.conj(w)):
        # z = s*ww + t through least squares on exactly 3 points (unique fit)
        M = np.stack([ww, np.ones(3)], axis=1)
        sol, res, rank, _ = np.linalg.lstsq(M, z, rcond=None)
        s, t = sol[0], sol[1]
        pred = s * ww + t
        err = max(abs(p - q) for p, q in zip(pred, z))
        if best is None or err < best[0]:
            # (x,z) = (Re, Im) of s*(px + i*py [or conj]) + t
            if ww is w:  # analytic affine coefficients from s = u+iv
                u, v = s.real, s.imag
                aff = {"a": u, "b": -v, "c": t.real, "d": v, "e": u, "f": t.imag}
            else:
                u, v = s.real, s.imag
                aff = {"a": u, "b": v, "c": t.real, "d": v, "e": -u, "f": t.imag}
            best = (err, aff)
    return best[1]


def loo_errors(fit_fn, src, dst):
    errs = []
    for i in range(len(src)):
        s2 = [p for j, p in enumerate(src) if j != i]
        d2 = [p for j, p in enumerate(dst) if j != i]
        if len(s2) < 3:
            return None
        aff = fit_affine(s2, d2) if len(s2) >= 4 else fit_similarity3(s2, d2)
        x, z = apply_affine(aff, *src[i])
        errs.append(((x - dst[i][0]) ** 2 + (z - dst[i][1]) ** 2) ** 0.5)
    return errs


def robust_fit(src, dst):
    """Fit with iterative outlier trimming (generic site labels can collide),
    then score by leave-one-out on the surviving set. Returns (affine, errs)."""
    keep = list(range(len(src)))
    aff = None
    for _ in range(6):
        if len(keep) < 3:
            return None, None
        fit_src = [src[i] for i in keep]
        fit_dst = [dst[i] for i in keep]
        aff = (fit_affine(fit_src, fit_dst) if len(keep) >= 4
               else fit_similarity3(fit_src, fit_dst))
        res = [((apply_affine(aff, *src[i])[0] - dst[i][0]) ** 2
                + (apply_affine(aff, *src[i])[1] - dst[i][1]) ** 2) ** 0.5
               for i in keep]
        med = float(np.median(res))
        thresh = max(10.0, 3.0 * med)
        new = [k for k, r in zip(keep, res) if r <= thresh]
        if len(new) == len(keep) or len(new) < 3:
            break
        keep = new
    errs = loo_errors(None, [src[i] for i in keep], [dst[i] for i in keep])
    if errs is None:
        # too few anchors for a leave-one-out round: score on in-sample
        # residuals instead (3-point similarity is exactly determined anyway)
        errs = [((apply_affine(aff, *src[i])[0] - dst[i][0]) ** 2
                 + (apply_affine(aff, *src[i])[1] - dst[i][1]) ** 2) ** 0.5
                for i in keep]
    return aff, (errs, keep)


def consensus_fit(story, zone, label, gate: float = 10.0):
    """Grow a consensus set: seed from the trusted sources (storyline points
    are calibrated once already; task-zone pairs are mostly right), then keep
    absorbing candidate anchors whose residual against the current fit is
    <= gate. Label/zone sources alone can be poisoned by mispaired names or
    objectives, so they only ever join an established fit, never seed one
    (except zone-anchored maps with no story points at all).

    Each anchor: (px, py, x, z). Returns (affine, (errs, keep_idx, all_pts))."""
    pool = story + zone + label
    n_s, n_z = len(story), len(story) + len(zone)
    src = [(p[0], p[1]) for p in pool]
    dst = [(p[2], p[3]) for p in pool]
    if len(pool) < 3:
        return None, None
    # A 3-point similarity through anchors that collapse to fewer than 3
    # distinct world positions is underdetermined (icebreaker: two story steps
    # share one game point) - reject rather than invent a transform.
    if len({(round(p[2], 1), round(p[3], 1)) for p in pool}) < 3:
        return None, None

    def fit_set(keep):
        s = [src[i] for i in keep]
        d = [dst[i] for i in keep]
        if len(keep) >= 4:
            return fit_affine(s, d)
        return fit_similarity3(s, d)

    def residual(aff, i):
        x, z = apply_affine(aff, *src[i])
        return ((x - dst[i][0]) ** 2 + (z - dst[i][1]) ** 2) ** 0.5

    if n_s >= 3:
        keep = list(range(n_s))
    elif n_z >= 3:
        keep = list(range(n_z))
    else:
        return None, None
    aff = fit_set(keep)
    while True:
        added = False
        for i in range(len(pool)):
            if i in keep:
                continue
            if residual(aff, i) <= gate:
                keep.append(i)
                added = True
        if not added:
            break
        # re-verify the set: any anchor that drifted out gets one eviction pass
        res = {i: residual(aff, i) for i in keep}
        worst = max(res, key=res.get)
        if len(keep) > 3 and res[worst] > max(gate, 3.0 * float(np.median(list(res.values())))):
            keep.remove(worst)
        aff = fit_set(keep)
    errs = loo_errors(None, [src[i] for i in keep], [dst[i] for i in keep])
    if errs is None:
        errs = [residual(aff, i) for i in keep]
    return aff, (errs, keep, pool)


def fit_group(story, zone, label):
    """One consensus fit over a group of same-space anchors. Returns
    (affine, stats) or (None, why)."""
    res = consensus_fit(story, zone, label)
    if not res or res[0] is None:
        return None, "seed <3 anchors / degenerate"
    aff, (errs, keep, pool) = res
    med, mx = float(np.median(errs)), float(max(errs))
    if med > 5.0 or mx > 10.0:
        return None, f"LOO error median {med:.1f} m max {mx:.1f} m (too large)"
    n_s, n_z = len(story), len(story) + len(zone)
    kinds = ["story" if i < n_s else "zone" if i < n_z else "label" for i in keep]
    counts = {k: kinds.count(k) for k in ("story", "zone", "label") if kinds.count(k)}
    return aff, {"anchors": len(keep), "by": counts, "pool": len(pool),
                 "looMedianM": round(med, 2), "looMaxM": round(mx, 2)}


def fit_map(name, groups):
    """groups: {floor: {"story": [...], "zone": [...], "label": [...]}}.

    Try one whole-map transform first (outdoor maps stack their floors on the
    same layout, so pooling passes and is simpler); if that misses the bar, fit
    each floor separately - the-lab/factory put different decks on identical
    pixel grids, where a flat fit is provably wrong. Returns ({"*": aff} or
    {floor: aff}, combined stats, or (None, why))."""
    allg = {"story": [], "zone": [], "label": []}
    for g in groups.values():
        for k in allg:
            allg[k].extend(g[k])
    aff, stats = fit_group(allg["story"], allg["zone"], allg["label"])
    if aff is not None:
        return {"*": aff}, stats

    per: dict[str, dict] = {}
    fails: list[str] = []
    anchors = pool = 0
    med = mx = 0.0
    counts_all: dict[str, int] = {}
    for floor, g in sorted(groups.items()):
        a, s = fit_group(g["story"], g["zone"], g["label"])
        if a is None:
            fails.append(f"f{floor}:{s}")
            continue
        per[floor] = a
        anchors += s["anchors"]
        pool += s["pool"]
        med = max(med, s["looMedianM"])
        mx = max(mx, s["looMaxM"])
        for bk, bv in s["by"].items():
            counts_all[bk] = counts_all.get(bk, 0) + bv
    if not per:
        return None, "map fit failed (" + "; ".join(fails) + ")"
    stats = {"anchors": anchors, "by": counts_all, "pool": pool,
             "looMedianM": round(med, 2), "looMaxM": round(mx, 2),
             "floors": sorted(per)}
    if fails:
        stats["floorFails"] = fails
    return per, stats


# ----------------------------------------------------------------- anchors


def main() -> int:
    markers = load(EF / "mapmarkers.json")
    zh_raw = load(RES / "story" / "zh-raw.json")
    coords = load(RES / "story" / "coords.json")
    mapdata = load(ASSETS / "maps" / "mapdata.json")["maps"]
    td_tasks = load(RES / "api" / "tasks.json")["data"]["tasks"]

    from build_data import KEY_TO_ID  # local import: id<->key table lives there
    id_to_key = {v: k for k, v in KEY_TO_ID.items()}

    # pixel lookup: marker id -> (map key, floor, px, py)
    px_by_marker: dict[str, tuple[str, str, float, float]] = {}
    for key, m in markers.items():
        for mk in m["markers"]:
            if mk.get("x") is None or mk.get("y") is None:
                continue
            px_by_marker[mk["id"]] = (key, str(mk.get("floor")), mk["x"], mk["y"])

    # unique-zone lookup: (task_id) -> {map_key: [x,z]}, only maps with 1 zone
    td_zones: dict[str, dict[str, list[tuple[float, float]]]] = {}
    for tid, task in td_tasks.items():
        per_map: dict[str, list[tuple[float, float]]] = {}
        for o in task.get("objectives") or []:
            for z in o.get("zones") or []:
                k = id_to_key.get(z.get("map") or "")
                pos = z.get("position") or {}
                if k and "x" in pos:
                    per_map.setdefault(k, []).append((pos["x"], pos.get("z", 0)))
            for loc in o.get("possibleLocations") or []:
                k = id_to_key.get(loc.get("map") or "")
                if not k:
                    continue
                for p in loc.get("positions") or []:
                    if "x" in p:
                        per_map.setdefault(k, []).append((p["x"], p.get("z", 0)))
        td_zones[tid] = {k: pts for k, pts in per_map.items()}

    # anchors grouped per (map, floor), kept SEPARATE by source: story/zone seed
    # the consensus, label is candidate-only (its own residuals run 13-34 m - our
    # labels come from approximate positions, see AGENTS).
    pools: dict[str, dict[str, dict[str, list]]] = {}
    stats = {"story": 0, "label": 0, "zone": 0}

    def add(key, floor, kind, px, py, gx, gz):
        bucket = pools.setdefault(key, {}).setdefault(
            str(floor), {"story": [], "zone": [], "label": []})[kind]
        # dedupe within a bucket by pixel (a marker feeding several sources must
        # not count twice); cross-floor duplicates stay - they anchor other floors
        if any(abs(b[0] - px) < 0.01 and abs(b[1] - py) < 0.01 for b in bucket):
            return
        bucket.append((px, py, gx, gz))
        stats[kind] += 1

    # 1. story
    obj_marker = {o["id"]: o for c in zh_raw for o in c["objectives"]}
    for oid, game in coords["objectives"].items():
        o = obj_marker.get(oid)
        if not o or not o.get("marker"):
            continue
        hit = px_by_marker.get(o["marker"])
        if not hit:
            continue
        add(hit[0], hit[1], "story", hit[2], hit[3], game["x"], game["z"])

    # 2. same-name labels. Generic names ("仓库", "村庄") repeat on both sides
    # and pair wrong, so only names unique within the map on BOTH tables count.
    labels_by_map = {}
    for m in mapdata:
        by_name: dict[str, list[tuple[float, float]]] = {}
        for l in m.get("labels") or []:
            by_name.setdefault(norm_title(l["text"]), []).append((l["x"], l["z"]))
        labels_by_map[m["key"]] = {k: v[0] for k, v in by_name.items() if len(v) == 1}
    for key, m in markers.items():
        table = labels_by_map.get(key, {})
        site_names: dict[str, list[dict]] = {}
        for mk in m["markers"]:
            if mk["category"] == "地图标注" and mk.get("x") is not None:
                site_names.setdefault(norm_title(mk["title"]), []).append(mk)
        for name, lst in site_names.items():
            if len(lst) != 1 or name not in table:
                continue
            mk = lst[0]
            add(key, mk["floor"], "label", mk["x"], mk["y"], table[name][0], table[name][1])

    # 3. task-zone cross anchors. Require the pairing to be 1:1 on this map:
    # one site marker for the task AND one unique td zone point for it.
    for key, m in markers.items():
        task_marker_count: dict[str, int] = {}
        for mk in m["markers"]:
            if mk["category"] == "任务地点" and mk.get("task_id") and mk.get("x") is not None:
                task_marker_count[mk["task_id"]] = task_marker_count.get(mk["task_id"], 0) + 1
        for mk in m["markers"]:
            if mk["category"] != "任务地点" or not mk.get("task_id"):
                continue
            if mk.get("x") is None:
                continue
            if task_marker_count.get(mk["task_id"], 0) != 1:
                continue
            zones = td_zones.get(mk["task_id"], {})
            pts = zones.get(key)
            if pts and len(set((round(p[0], 1), round(p[1], 1)) for p in pts)) == 1:
                add(key, mk["floor"], "zone", mk["x"], mk["y"], pts[0][0], pts[0][1])

    out = {"_note": "eftarkov.com interactive-map SVG pixel -> game-meter affine, keyed per "
                    "map then per floor ('*' = one transform for the whole map). Fit by "
                    "tools/site_calibrate.py (storyline points seed, task-zone joins, "
                    "same-name labels only by consensus). Leave-one-out median<=5m "
                    "max<=10m; floors/maps that miss the bar get no transform. Regenerate: "
                    "python tools/fetch_eftarkov.py --maps && python tools/site_calibrate.py",
           "maps": {}}
    missing = []
    for key in sorted(set(pools) | set(markers)):
        groups = pools.get(key, {})
        n = sum(len(v) for g in groups.values() for v in g.values())
        if n < 3:
            missing.append(f"{key}({n})")
            continue
        affs, info = fit_map(key, groups)
        if affs is None:
            missing.append(f"{key}:{info}")
            continue
        entry = {}
        for floor, aff in affs.items():
            entry[floor] = {k: round(v, 6) if isinstance(v, float) else v
                            for k, v in aff.items()}
        entry["stats"] = info
        out["maps"][key] = entry
        print(f"  {key:18} floors={info.get('floors', ['*'])} anchors={info['anchors']:3d} "
              f"{info['by']} loo med={info['looMedianM']:5.2f} m max={info['looMaxM']:5.2f} m")
    if missing:
        print("  no transform:", ", ".join(missing))
    print(f"anchors: story={stats['story']} label={stats['label']} zone={stats['zone']}")
    (EF / "pxaffine.json").write_text(
        json.dumps(out, ensure_ascii=False, indent=1), encoding="utf-8", newline="\n")
    print(f"wrote {EF / 'pxaffine.json'} ({len(out['maps'])} maps)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
