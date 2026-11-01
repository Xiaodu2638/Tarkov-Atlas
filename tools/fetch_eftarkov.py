#!/usr/bin/env python3
r"""Fetch task / storyline data from eftarkov.com (the Chinese wiki this app ships).

Everything the app displays as Chinese text — task names, objectives, prerequisites,
levels, maps, storyline chapters — is authored on eftarkov.com. tarkov.dev only ever
provided *coordinates*, and it is hard to reach from mainland China, so it is no
longer part of the default update path (see tools/update_data.py).

    python tools/fetch_eftarkov.py --tasks     # 任务快照 -> research/eftarkov/tasks.json
    python tools/fetch_eftarkov.py --story     # 剧情快照 -> research/story/zh-raw.json
    python tools/fetch_eftarkov.py --guides    # 攻略配图 -> TarkovAtlas/Assets/guides/<task-id>/
    python tools/fetch_eftarkov.py             # 以上全部

The script only writes the committed snapshots / images. Building the app assets from
them is build_data.py / build_story.py (no network there).

Raw HTML is cached in .tmp/eftarkov-cache/ so a parser fix can be re-run without
hitting the site again: pass --cache-only to parse the cache, --refresh to ignore it.
"""
from __future__ import annotations

import argparse
import concurrent.futures
import datetime
import json
import re
import shutil
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
TOOLS = ROOT / "tools"
RES = ROOT / "research"
EF = RES / "eftarkov"
STORY_RAW = RES / "story" / "zh-raw.json"
GUIDES = ROOT / "TarkovAtlas" / "Assets" / "guides"
CACHE = ROOT / ".tmp" / "eftarkov-cache"

BASE = "https://www.eftarkov.com"
UA = ("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
      "(KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36")
HEADERS = {"User-Agent": UA, "Accept-Language": "zh-CN,zh;q=0.9"}
MODES = ("pvp", "pve")
WORKERS = 6


def log(msg: str) -> None:
    print(f"[eftarkov] {msg}", flush=True)


def die(msg: str) -> None:
    log(f"!! {msg}")
    sys.exit(1)


# ------------------------------------------------------------------ http


def fetch(url: str, *, cache_name: str | None = None, refresh: bool = False,
          cache_only: bool = False) -> str:
    """GET with a local cache; retries transient failures."""
    if cache_name:
        p = CACHE / cache_name
        if p.is_file() and not refresh:
            return p.read_text(encoding="utf-8")
        if cache_only:
            die(f"缓存缺失 {cache_name}（先不带 --cache-only 跑一次）")
    last: Exception | None = None
    for attempt in range(4):
        try:
            req = urllib.request.Request(url, headers=HEADERS)
            with urllib.request.urlopen(req, timeout=90) as r:
                html = r.read().decode("utf-8", "replace")
            if cache_name:
                CACHE.mkdir(parents=True, exist_ok=True)
                (CACHE / cache_name).write_text(html, encoding="utf-8")
            return html
        except (urllib.error.URLError, TimeoutError, OSError) as exc:
            last = exc
            wait = 3 * (attempt + 1)
            log(f"  {url} 失败（{str(exc)[:80]}），{wait}s 后重试 ...")
            time.sleep(wait)
    die(f"拉取失败: {url} ({last})")


def parallel(fn, items, workers=WORKERS):
    out = []
    with concurrent.futures.ThreadPoolExecutor(max_workers=workers) as pool:
        futures = {pool.submit(fn, it): it for it in items}
        for i, fut in enumerate(concurrent.futures.as_completed(futures), 1):
            out.append(fut.result())
            if i % 50 == 0 or i == len(futures):
                log(f"  {i}/{len(futures)}")
    return out


# ------------------------------------------------------------------ text


TAG = re.compile(r"<[^>]+>")
WS = re.compile(r"[ \t\r\f\v]+")


def text_of(html: str) -> str:
    """Tags -> spaces, entities decoded, whitespace collapsed (keeps CJK intact)."""
    import html as htmllib
    s = re.sub(r"<br\s*/?>", "\n", html)
    s = TAG.sub(" ", s)
    s = htmllib.unescape(s)
    s = WS.sub(" ", s)
    return "\n".join(line.strip() for line in s.split("\n")).strip()


def block(html: str, start: int, opener: str = "<div", closer: str = "</div>") -> str:
    """Extract a balanced <div>...</div> starting at `start` (a tag position)."""
    depth = 0
    i = start
    while True:
        nxt_open = html.find("<div", i)
        nxt_close = html.find("</div>", i)
        if nxt_close == -1:
            return html[start:]
        if nxt_open != -1 and nxt_open < nxt_close:
            depth += 1
            i = nxt_open + 4
        else:
            depth -= 1
            i = nxt_close + 6
            if depth == 0:
                return html[start:i]


# ------------------------------------------------------------------ parse


def parse_task(tid: str, html: str) -> dict:
    rec: dict = {"id": tid, "source": f"{BASE}/task/{tid}"}

    m = re.search(r"<h1[^>]*>([\s\S]*?)</h1>", html)
    rec["name"] = text_of(m.group(1)) if m else ""
    m = re.search(r'class="task-title-en[^"]*"[^>]*>([\s\S]*?)</div>', html)
    rec["nameEn"] = text_of(m.group(1)) if m else ""

    # Trader: the avatar URL carries the BSG/tarkov.dev trader id, which is what
    # traders.json is keyed by — no name matching needed.
    m = re.search(r'/uploads/trader/([0-9a-f]{24})\.webp"\s+alt="([^"]*)"', html)
    rec["trader"] = m.group(1) if m else ""
    rec["traderName"] = m.group(2) if m else ""

    m = re.search(r"等级\s*(\d+)\s*\+", html)
    rec["level"] = int(m.group(1)) if m else 0

    # Map chips: "<i class='fas fa-map'></i>海关". "不限制地图" means no map.
    maps = []
    for chip in re.findall(r'<i class="fas fa-map[^"]*"></i>([\s\S]*?)</span>', html):
        name = text_of(chip)
        if not name or "不限制" in name:
            continue
        for part in re.split(r"[/、,，]", name):
            part = part.strip()
            if part and part not in maps:
                maps.append(part)
    rec["maps"] = maps

    # Objectives: the 任务目标 card's <ul>. Each <li> starts with an index badge.
    objectives = []
    head = re.search(r"fa-bullseye[\s\S]{0,400}?</h2>", html)
    if head:
        ul = re.search(r'<ul class="space-y-2">([\s\S]*?)</ul>', html[head.end():])
        if ul:
            for li in re.findall(r"<li[\s\S]*?</li>", ul.group(1)):
                t = text_of(li)
                t = re.sub(r"^\d+\s*", "", t).strip()
                if t:
                    objectives.append(t)
    rec["objectives"] = objectives

    # Prerequisites: the relation card lists "<status> <name>" per link.
    prereqs = []
    card = html.find("task-relation-card")
    if card != -1:
        for href, body in re.findall(
                r'<a href="/task/([0-9a-f]{24})"[\s\S]*?</i>([\s\S]*?)</a>',
                html[card:card + 12000]):
            t = text_of(body)
            status = ""
            sm = re.match(r"\((完成|激活|失败)\)", t)
            if sm:
                status = sm.group(1)
                t = t[sm.end():].strip()
            prereqs.append({"id": href, "name": t, "status": status})
    rec["prerequisites"] = prereqs

    # Guide pictures live inside 任务攻略: /uploads/infoImg/*. The block also
    # embeds interactive mini-maps (/assets/maps/*.svg + quest_objective.png
    # marker icons) - those are page furniture, not guide images.
    imgs = []
    gi = html.find("rich-guide-content")
    if gi != -1:
        seg = html[gi:gi + 40000]
        end = seg.find("task-detail-sidebar")
        if end != -1:
            seg = seg[:end]
        for src in re.findall(r'<img[^>]+src="([^"]+)"', seg):
            if "/uploads/infoImg/" not in src:
                continue
            if src.startswith("/"):
                src = BASE + src
            if src not in imgs:
                imgs.append(src)
    rec["guideImages"] = imgs
    rec["cover"] = f"{BASE}/uploads/task/{tid}.webp"

    # Per-objective location cards: the site draws each objective's position on
    # its own SVG grid (/assets/maps/<mapId>/<floor>.svg + data-marker-x/y).
    # "obj" is the 1-based objective index and repeats when one objective is
    # doable on several maps. Read the stage divs (one per floor, hidden ones
    # hold the other floors' points) rather than the header link: multi-point
    # objectives put a COMMA-separated marker list in it, and some cards have
    # no link at all. These pixel points are what site_calibrate.py turns into
    # game coordinates (research/eftarkov/pxaffine.json).
    locs = []
    for c in re.findall(r'<article class="task-og-card">([\s\S]*?)</article>', html):
        idx = re.search(r'task-og-index">(\d+)', c)
        link = re.search(r'href="/map/([0-9a-f]{24})\?marker=', c)
        if not idx or not link:
            continue
        map_id = link.group(1)
        for stage in re.finditer(
                r'data-floor-key="([^"]*)"[^>]*'
                r'data-map-width="([\d.]+)"[^>]*data-map-height="([\d.]+)"([\s\S]*?)(?=data-floor-key=|<button|$)', c):
            for m in re.finditer(r'data-marker-x="([\d.\-]+)"\s+data-marker-y="([\d.\-]+)"',
                                 stage.group(4)):
                locs.append({
                    "obj": int(idx.group(1)),
                    "map": map_id,
                    "floor": stage.group(1),
                    "w": float(stage.group(2)),
                    "h": float(stage.group(3)),
                    "x": float(m.group(1)),
                    "y": float(m.group(2)),
                })
    rec["locations"] = locs

    m = re.search(r'<p class="text-stone-300 leading-relaxed[^"]*">([\s\S]*?)</p>', html)
    rec["description"] = text_of(m.group(1)) if m else ""
    m = re.search(r"更新于\s*([0-9]{4}-[0-9]{2}-[0-9]{2})", html)
    rec["updated"] = m.group(1) if m else ""
    return rec


def task_list(refresh: bool, cache_only: bool) -> list[str]:
    """Every non-season task id: crawl the PVP and PVE list pages."""
    ids: list[str] = []
    seen: set[str] = set()
    for mode in MODES:
        page = 1
        while True:
            html = fetch(f"{BASE}/task?mode={mode}&page={page}",
                         cache_name=f"list-{mode}-{page}.html",
                         refresh=refresh, cache_only=cache_only)
            found = re.findall(r'href="/task/([0-9a-f]{24})"', html)
            fresh = [i for i in found if i not in seen]
            for i in fresh:
                seen.add(i)
                ids.append(i)
            pages = [int(p) for p in re.findall(rf"/task\?mode={mode}&(?:amp;)?page=(\d+)", html)]
            last = max(pages) if pages else page
            if page >= last or not found:
                break
            page += 1
        log(f"{mode}: 列表共 {len(ids)} 个任务")
    return ids


def fetch_tasks(refresh: bool, cache_only: bool) -> int:
    ids = task_list(refresh, cache_only)
    if not ids:
        die("任务列表为空，站点结构可能变了")

    def one(tid: str) -> dict:
        html = fetch(f"{BASE}/task/{tid}", cache_name=f"task-{tid}.html",
                     refresh=refresh, cache_only=cache_only)
        return parse_task(tid, html)

    recs = parallel(one, ids)
    recs.sort(key=lambda r: r["name"])
    EF.mkdir(parents=True, exist_ok=True)
    db = {
        "generatedAt": datetime.date.today().isoformat(),
        "source": f"{BASE}/task (PVP + PVE 任务列表；不含赛季任务)",
        "tasks": recs,
    }
    (EF / "tasks.json").write_text(
        json.dumps(db, ensure_ascii=False, indent=1), encoding="utf-8", newline="\n")
    no_en = [r for r in recs if not r["nameEn"]]
    log(f"tasks.json: {len(recs)} 条，缺英文名 {len(no_en)}，"
        f"有目标 {sum(1 for r in recs if r['objectives'])}，"
        f"有配图 {sum(1 for r in recs if r['guideImages'])}")
    return len(recs)


# ------------------------------------------------------------------ story


def fetch_story(refresh: bool, cache_only: bool) -> int:
    index = fetch(f"{BASE}/storyline", cache_name="storyline.html",
                  refresh=refresh, cache_only=cache_only)
    hrefs = list(dict.fromkeys(re.findall(r'href="(/storyline/[0-9a-f]{24})"', index)))
    if not hrefs:
        die("剧情列表为空，站点结构可能变了")

    story_px = {}  # story marker pixels live in map-config (see fetch_maps); kept for diff debugging only

    def one(href: str) -> dict:
        cid = href.rstrip("/").split("/")[-1]
        html = fetch(BASE + href, cache_name=f"story-{cid}.html",
                     refresh=refresh, cache_only=cache_only)
        m = re.search(r"<h1[^>]*>([\s\S]*?)</h1>", html)
        name = text_of(m.group(1)) if m else cid
        objectives = []
        # One block per objective; blocks for mapless steps ("交给我吧") have no
        # story-og-maps panel, so cut on the block start instead of requiring it.
        # Marker pixel positions are NOT scraped from here: every /map/<id> page
        # embeds them in map-config (fetch_maps -> mapmarkers.json), which is the
        # single pixel source site_calibrate.py fits against.
        for blk in re.split(r'(?=<div class="obj-block)', html):
            oid = re.search(r'data-obj-id="([0-9a-f]{24})"', blk[:400])
            title = re.search(r'class="obj-title[^"]*">([\s\S]*?)</h3>', blk)
            if not oid or not title:
                continue
            link = re.search(r'href="/map/([0-9a-f]{24})\?marker=([0-9a-f]{24})"', blk)
            objectives.append({
                "id": oid.group(1),
                "title": text_of(title.group(1)),
                "map": link.group(1) if link else "",
                "marker": link.group(2) if link else "",
            })
        return {"href": href, "name": name, "objectives": objectives}

    chapters = parallel(one, hrefs)
    # Mongo ObjectId prefix = creation time; the story's progression order is the
    # same order (68cbcdc4 Falling Skies < 68cbd336 Tour < ... < 69d38381 Boreas).
    # Site nav order is alphabetical by title, so sort by chapter id instead.
    chapters.sort(key=lambda c: c["href"].rstrip("/").split("/")[-1])
    STORY_RAW.parent.mkdir(parents=True, exist_ok=True)
    STORY_RAW.write_text(json.dumps(chapters, ensure_ascii=False, indent=1),
                         encoding="utf-8", newline="\n")
    total = sum(len(c["objectives"]) for c in chapters)
    log(f"zh-raw.json: {len(chapters)} 章 / {total} 目标")
    return total


# ------------------------------------------------------------------- maps

# Our interactive-map key -> eftarkov map id (== BSG id, same space as KEY_TO_ID
# in build_data.py; kept duplicated here so the fetcher needs no import).
EF_MAP_KEYS = {
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


def fetch_maps(refresh: bool, cache_only: bool) -> int:
    """Pull every /map/<id> page and keep its inline map-config JSON.

    research/eftarkov/mapmarkers.json: per map -> floors + all markers in site
    pixel space (categories 任务地点/地图标注/剧情地点/PMC/... ). This is the
    anchor corpus site_calibrate.py fits pixels against game coordinates with.
    """
    out: dict[str, dict] = {}
    for key, mid in EF_MAP_KEYS.items():
        html = fetch(f"{BASE}/map/{mid}", cache_name=f"map-{mid}.html",
                     refresh=refresh, cache_only=cache_only)
        i = html.find('id="map-config"')
        if i == -1:
            die(f"{key}: 页面没有 map-config，站点结构可能变了")
        j = html.find(">", i) + 1
        k = html.find("</script>", j)
        cfg = json.loads(html[j:k])
        out[key] = {
            "mapId": mid,
            "floors": [{"key": f["key"], "width": f["width"], "height": f["height"]}
                       for f in cfg.get("floors", [])],
            "markers": [{
                "id": m.get("id"), "category": m.get("category"),
                "floor": m.get("floor_key"), "title": m.get("title"),
                "task_id": m.get("task_id"),
                "x": float(m["x"]) if m.get("x") not in (None, "") else None,
                "y": float(m["y"]) if m.get("y") not in (None, "") else None,
            } for m in cfg.get("markers", [])],
        }
        log(f"  {key}: {len(out[key]['markers'])} markers / {len(out[key]['floors'])} floors")
    EF.mkdir(parents=True, exist_ok=True)
    (EF / "mapmarkers.json").write_text(
        json.dumps(out, ensure_ascii=False, indent=1), encoding="utf-8", newline="\n")
    n = sum(len(v["markers"]) for v in out.values())
    log(f"mapmarkers.json: 13 图 / {n} 标记")
    return n


# ------------------------------------------------------------------ guides


def fetch_guides(refresh: bool, prune: bool = False) -> int:
    """Download each task's guide pictures into Assets/guides/<task-id>/N.webp.

    Task ids are shared between eftarkov.com and this app (both mirror BSG ids),
    so folder names are stable. Without --prune, pictures of tasks that left the
    site are kept: the app only shows guides for tasks it ships.
    """
    db = json.loads((EF / "tasks.json").read_text(encoding="utf-8"))

    sys.path.insert(0, str(TOOLS / "pyext"))
    from PIL import Image  # noqa: E402
    import io

    jobs = []
    for t in db["tasks"]:
        for n, url in enumerate(t["guideImages"]):
            jobs.append((t["id"], n, url))

    def one(job) -> int:
        qid, n, url = job
        target = GUIDES / qid / f"{n}.webp"
        if target.is_file() and not refresh:
            return 0
        data = urllib.request.urlopen(
            urllib.request.Request(url, headers={**HEADERS, "Referer": BASE + "/"}),
            timeout=90).read()
        im = Image.open(io.BytesIO(data)).convert("RGB")
        if im.width > 1600:
            im = im.resize((1600, round(im.height * 1600 / im.width)), Image.LANCZOS)
        target.parent.mkdir(parents=True, exist_ok=True)
        buf = io.BytesIO()
        im.save(buf, "WEBP", quality=82, method=4)
        target.write_bytes(buf.getvalue())
        return 1

    done = parallel(one, jobs, workers=4)
    log(f"guides: {len(jobs)} 张引用，{sum(done)} 张新下载")

    # Never delete local pictures by default: the hand-curated toolbox set can be
    # richer than the current page (a task ships 54 local images vs 17 on the
    # site). --prune removes whole folders of tasks the site no longer lists.
    if prune:
        keep = {t["id"] for t in db["tasks"]}
        for d in GUIDES.glob("*/"):
            if d.is_dir() and d.name not in keep:
                shutil.rmtree(d)
                log(f"  pruned {d.name}")
    return len(jobs)


# ------------------------------------------------------------------ main


def main() -> int:
    ap = argparse.ArgumentParser(description="pull tasks/story/guides from eftarkov.com")
    ap.add_argument("--tasks", action="store_true", help="只拉任务快照")
    ap.add_argument("--story", action="store_true", help="只拉剧情快照")
    ap.add_argument("--maps", action="store_true", help="只拉 13 张地图页的 marker 表")
    ap.add_argument("--guides", action="store_true", help="只下载攻略配图")
    ap.add_argument("--refresh", action="store_true", help="忽略 .tmp 缓存，重新下载")
    ap.add_argument("--cache-only", action="store_true", help="只解析 .tmp 缓存，不联网")
    ap.add_argument("--prune", action="store_true",
                    help="guides：删除已下线任务的整个攻略图目录（默认保留）")
    args = ap.parse_args()

    all_steps = not (args.tasks or args.story or args.maps or args.guides)
    if all_steps or args.tasks:
        fetch_tasks(args.refresh, args.cache_only)
    if all_steps or args.story:
        fetch_story(args.refresh, args.cache_only)
    if all_steps or args.maps:
        fetch_maps(args.refresh, args.cache_only)
    if all_steps or args.guides:
        if not (EF / "tasks.json").is_file():
            die(f"缺少 {EF / 'tasks.json'}：先跑 --tasks")
        fetch_guides(args.refresh, args.prune)
    return 0


if __name__ == "__main__":
    sys.exit(main())
