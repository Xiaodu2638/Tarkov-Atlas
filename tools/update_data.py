#!/usr/bin/env python3
r"""Data refresh entry point: pull from eftarkov.com, rebuild, gate.

    python tools/update_data.py               # eftarkov 任务+剧情 -> build_all -> 门禁
    python tools/update_data.py --dry-run     # 拉完只展示快照 diff 并回滚，不构建
    python tools/update_data.py --no-fetch    # 跳过联网，只跑 build_all + 门禁
    python tools/update_data.py --with-guides # 同时下载任务攻略配图（增量）
    python tools/update_data.py --td          # 额外重拉 tarkov.dev 坐标快照（慢，可能 422）
    python tools/update_data.py --selftest    # 离线自测 tarkov.dev 重塑/展平逻辑

数据源分工（以 eftarkov.com 为准，tarkov.dev 只在拿不到的数据上做覆盖层）：
* research/eftarkov/tasks.json   任务名册 + 中文名 + 中文目标 + 前置 + 攻略图链接
                                 （tools/fetch_eftarkov.py，国内直连）
* research/story/zh-raw.json     剧情章节/目标中文文本（同上）
* research/api/tasks.json        tarkov.dev 快照：只为任务目标提供**坐标**
                                 （--td 时重拉；eftarkov 没有这个数据）
* research/api/maps.json 等      撤离点/Boss/出生点/雷区等标记数据 + 楼层表，
                                 同样只有 tarkov.dev 有（--td 时重拉）

流水线设计（AGENTS.md 第 8 节约定）：
* 快照进 git、产物 diff 可审：拉完后打印 research/ 的 git diff --stat，
  **脚本从不自动提交**；数据异常由门禁（check.ps1 + coverage）拦住。
* 门禁失败：拉取过的快照回滚到拉取前，中止，不留半成品。
* boss_relocate 已内置于 build_all.py（幂等），重拉 --td 快照后无需手动补跑。
* 依赖：eftarkov.com 可直连；--td 需要 api.tarkov.dev 可用（间歇 422，
  脚本内退避重试）；构建需要本机 Edge（SVG 栅格化）与仓库内
  tools/pyext|pylibs（Pillow/numpy/requests）。

tarkov.dev 快照格式与 build_data.py 的读法保持一致：
  {name}.json     {"data": {...id 键控段落...}, "translations": [<jsonpath>...]}
  {name}.zh.json  {"data": {"<id> Name": 中文, "<英文值>": 中文, ...}}

GraphQL 选择集从**旧快照自身的字段结构**派生（上游加字段无需改代码）。
中文表的键格式历史上由一个已删除的外部工具决定（"<id> Name" / "<id> name" /
裸英文值 多套并存），无法从路径清单可靠还原，所以展平采取「学习旧表」策略：
对每个翻译叶子生成候选键（<id> Field 两种大小写 + 裸英文值），命中旧表格式
就沿用该格式；旧表里没被本次覆盖的条目原样保留。宁可留下过期词条，也绝不
静默丢翻译——新增/漂移在 diff 里可见，由人裁决。
"""
from __future__ import annotations

import argparse
import json
import shutil
import subprocess
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
TOOLS = ROOT / "tools"
API = ROOT / "research" / "api"
EF = ROOT / "research" / "eftarkov"
STORY = ROOT / "research" / "story" / "zh-raw.json"
BACKUP = ROOT / ".tmp" / "api-backup"
GRAPHQL = "https://api.tarkov.dev/graphql"
DATASETS = ["maps", "tasks", "traders"]   # build_data.py 消费的三对快照
ID_FIELD = "id"


def log(msg: str) -> None:
    print(f"[update_data] {msg}", flush=True)


def die(msg: str) -> None:
    log(f"!! {msg}")
    sys.exit(1)


def load_json(path: Path):
    return json.loads(path.read_text(encoding="utf-8"))


def dump_json(path: Path, obj) -> None:
    # Match the committed snapshots byte-for-byte (compact, one line, no
    # spaces): a re-pull must show a *data* diff, not a whitespace reshuffle
    # of a 3 MB file that hides the real change.
    path.write_text(json.dumps(obj, ensure_ascii=False, separators=(',', ':')),
                    encoding="utf-8", newline="\n")


# ------------------------------------------------------------ eftarkov.com


def fetch_eftarkov(with_guides: bool) -> None:
    args = [sys.executable, str(TOOLS / "fetch_eftarkov.py"), "--tasks", "--story"]
    if with_guides:
        args.append("--guides")
    log("$ " + " ".join(args))
    if subprocess.run(args, cwd=ROOT).returncode != 0:
        die("fetch_eftarkov.py 失败")


# ------------------------------------------------------------------- fetch


def gql(query: str, language: str) -> dict:
    """POST one query with $language; retry transient failures, abort on schema errors."""
    body = json.dumps({"query": query, "variables": {"language": language}}).encode("utf-8")
    hdrs = {
        "Content-Type": "application/json",
        "Accept": "application/json",
        "User-Agent": ("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
                       "(KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36"),
    }
    last = None
    for attempt in range(4):
        try:
            req = urllib.request.Request(GRAPHQL, data=body, headers=hdrs)
            with urllib.request.urlopen(req, timeout=180) as r:
                payload = json.loads(r.read().decode("utf-8"))
        except urllib.error.HTTPError as exc:
            # 422/5xx 多半是 "GraphQL server unavailable"（Cloudflare 端抖动），
            # 重试即可；但响应体里若是字段/查询错误，重试没有意义，直接报出来。
            detail = ""
            try:
                detail = exc.read().decode("utf-8", "replace")[:300]
            except Exception:
                pass
            if "unavailable" in detail.lower() or exc.code in (422, 429, 500, 502, 503, 504):
                last = exc
                wait = 5 * (attempt + 1)
                log(f"  {language} HTTP {exc.code}（服务端抖动），{wait}s 后重试 ...")
                time.sleep(wait)
                continue
            die(f"GraphQL HTTP {exc.code}（{language}）: {detail or exc.reason}")
        except (urllib.error.URLError, TimeoutError, json.JSONDecodeError) as exc:
            last = exc
            wait = 5 * (attempt + 1)
            log(f"  {language} 拉取失败（{str(exc)[:90]}），{wait}s 后重试 ...")
            time.sleep(wait)
            continue
        if payload.get("errors"):
            msgs = " | ".join(e.get("message", "?") for e in payload["errors"])
            if "unavailable" in msgs.lower() or "rate" in msgs.lower():
                last = urllib.error.URLError(msgs)
                wait = 5 * (attempt + 1)
                log(f"  {language} 服务端忙（{msgs[:80]}），{wait}s 后重试 ...")
                time.sleep(wait)
                continue
            die(f"GraphQL 报错（{language}）: {msgs}")
        return payload["data"]
    die(f"GraphQL 不可达（重试 4 次）: {last}\n"
        f"  该端点偶发不可用；可在网络正常的机器上重跑，或去掉 --td 跳过坐标快照")


def selection_for(value) -> str | None:
    """GraphQL sub-selection derived from one example dict of the old snapshot."""
    if not isinstance(value, dict):
        return None
    subs = [ID_FIELD]
    for k, v in value.items():
        if isinstance(v, dict):
            inner = selection_for(v)
            if inner:
                subs.append(f"{k} {{ {inner} }}")
        elif isinstance(v, list) and v and isinstance(v[0], dict):
            inner = selection_for(v[0])
            if inner:
                subs.append(f"{k} {{ {inner} }}")
        else:
            subs.append(k)   # scalar / scalar list
    return " ".join(subs)


# traders.json 的 data 直接是 id->对象 的平铺 map（GraphQL 根字段就叫 traders），
# 没有 maps/tasks 那种 data.<section>.<id> 的中间层；拉取与回写都要按这层映射。
FLAT_MAP_SECTIONS = {"traders": "traders"}


def build_query(name: str, snapshot: dict) -> str:
    """query($language:String){ section(language:$language){...} } from old shape."""
    sections = snapshot["data"]
    if name in FLAT_MAP_SECTIONS:
        sections = {FLAT_MAP_SECTIONS[name]: sections}
    parts = []
    for section, value in sections.items():
        sample = None
        if isinstance(value, dict) and value:
            sample = next(iter(value.values()))
        elif isinstance(value, list) and value:
            sample = value[0]
        if isinstance(sample, dict):
            inner = selection_for(sample)
            parts.append(f"{section}(language: $language) {{ {inner} }}")
        else:
            log(f"  跳过 {name}.{section}（非对象段，无法派生选择集）")
    # 空选择集 = 快照结构与预期不符。此时继续拉取只会把空 data 写回快照，
    # 静默毁掉整个文件；宁可在这里停下来让人看。
    if not parts:
        die(f"{name}: 无法从现有快照派生任何选择段，中止（不写回）")
    return "query($language: String) { " + " ".join(parts) + " }"


def key_section(section_value, want_dict: bool):
    """List-of-objects -> id-keyed dict only if the committed snapshot keys it.

    prestige stays a list, mobs/tasks become dicts: the old file's own shape is
    the contract, otherwise a re-pull would rewrite sections into a different
    structure and bury the real data diff under a formatting diff.
    """
    if want_dict and isinstance(section_value, list) and section_value \
            and all(isinstance(x, dict) and ID_FIELD in x for x in section_value):
        return {x[ID_FIELD]: x for x in section_value}
    return section_value


# ---------------------------------------------------- translations flatten


def leaves(node):
    """(path_tuple, en_id, value) for every string leaf; path entries are str
    keys or int indices. A dict keyed directly by its object id (traders) is
    treated as having that id."""
    out = []

    def walk(n, path, ident):
        if isinstance(n, dict):
            if n.get(ID_FIELD):
                ident = n[ID_FIELD]
            elif len(path) == 1 and isinstance(path[0], str) \
                    and all(isinstance(v, dict) for v in node.values()):
                ident = path[0]     # {id: {...}} map without an inner id field
            for k, v in n.items():
                if k == ID_FIELD:
                    continue
                walk(v, path + (k,), ident)
        elif isinstance(n, list):
            for i, v in enumerate(n):
                walk(v, path + (i,), ident)
        elif isinstance(n, str):
            out.append((path, ident, n))

    walk(node, (), None)
    return out


def flatten_zh(section_en, section_zh, old_table: dict) -> dict:
    """Merge re-pulled zh into a copy of the committed table, learning key format.

    For each translated string pair (E -> Z at the same position):
      candidates = ["<id> Field", "<id> field", "E" with Field-cased variants...]
    The first candidate that already exists in old_table wins (that is the
    format build_data.py reads); otherwise the plain English value is used,
    which is what name/description lookups fall back to. Old entries whose
    source string disappeared upstream are kept, so a transient API hiccup can
    never silently drop translations.
    """
    merged = dict(old_table)
    en_leaves = {(p, i): v for p, i, v in leaves(section_en)}
    for path, ident, zval in leaves(section_zh):
        eval_ = en_leaves.get((path, ident))
        if eval_ is None or eval_ == zval:
            continue
        field = next((t for t in reversed(path) if isinstance(t, str)), "")
        cap = field[:1].upper() + field[1:]
        cands = []
        if ident:
            cands += [f"{ident} {cap}", f"{ident} {field}"]
        cands.append(eval_)
        key = next((c for c in cands if c in old_table), cands[-1])
        merged[key] = zval
    return merged


# ------------------------------------------------------------------ gates


def run(cmd: list[str]) -> int:
    log("$ " + " ".join(str(c) for c in cmd))
    return subprocess.run(cmd, cwd=ROOT).returncode


def git_diff_stat(paths: list[str]) -> str:
    r = subprocess.run(["git", "diff", "--stat", "--", *paths],
                       capture_output=True, text=True, encoding="utf-8",
                       errors="replace", cwd=ROOT)
    return r.stdout.strip()


def snapshot_backup() -> None:
    """Copy every snapshot the fetches may rewrite, mirroring the research/ tree
    (api/tasks.json and eftarkov/tasks.json must not collide in a flat backup)."""
    if BACKUP.exists():
        shutil.rmtree(BACKUP)
    for src_dir in (API, EF):
        if src_dir.is_dir():
            dst = BACKUP / src_dir.relative_to(ROOT)
            dst.mkdir(parents=True, exist_ok=True)
            for f in src_dir.glob("*.json"):
                (dst / f.name).write_bytes(f.read_bytes())
    if STORY.is_file():
        dst = BACKUP / STORY.parent.relative_to(ROOT)
        dst.mkdir(parents=True, exist_ok=True)
        (dst / STORY.name).write_bytes(STORY.read_bytes())


def rollback() -> None:
    if not BACKUP.is_dir():
        return
    for f in BACKUP.rglob("*.json"):
        target = ROOT / f.relative_to(BACKUP)
        if target.parent.is_dir():
            target.write_bytes(f.read_bytes())
    log("research 快照已回滚到拉取前")


# ------------------------------------------------------------------- main


def refresh_dataset(name: str, en_raw: dict, zh_raw: dict) -> tuple[int, int]:
    """Reshape one fetched dataset and write both files. Returns (sections, zh rows)."""
    snap = load_json(API / f"{name}.json")
    old_zh = load_json(API / f"{name}.zh.json")["data"]
    old_sections = snap["data"]

    data = {sec: key_section(val, isinstance(old_sections.get(sec), dict))
            for sec, val in en_raw.items()}
    flat = dict(old_zh)
    for section, en_sec in data.items():
        zh_sec = zh_raw.get(section)
        if zh_sec is None:
            continue
        flat = flatten_zh(en_sec, zh_sec, flat)

    # build_query 把平铺 map（traders 的 data 就是 id->对象）当成一个虚拟
    # section 发查询；回写前解包回 data 顶层，保持提交快照的原有形状。
    virtual = FLAT_MAP_SECTIONS.get(name)
    if virtual and set(data) == {virtual}:
        data = data[virtual]

    dump_json(API / f"{name}.json",
              {"data": data, "translations": snap.get("translations", [])})
    # flat starts as dict(old_zh) so committed keys keep their committed
    # positions; only genuinely new rows are appended (in doc order). Sorting
    # here would reshuffle the whole table on the first real pull and bury the
    # data diff under key-order noise.
    dump_json(API / f"{name}.zh.json", {"data": flat})
    return len(data), len(flat)


def selftest() -> int:
    """Feed the committed snapshots through the reshape/flatten code.

    The en doc doubles as the zh doc with one field deliberately changed, so we
    verify: (1) key_section round-trips, (2) flatten_zh picks the existing
    "<id> Field" formats from the old table (never invents a new spelling),
    (3) old rows survive.
    """
    ok = True
    for name in DATASETS:
        snap = load_json(API / f"{name}.json")
        old_zh = load_json(API / f"{name}.zh.json")["data"]
        sections = {}
        for sec, val in snap["data"].items():
            sections[sec] = val
        # key_section must keep each section's committed shape: dicts stay keyed
        # dicts, lists that were lists stay lists
        for sec, val in sections.items():
            shaped = key_section(val, isinstance(val, dict))
            if isinstance(val, dict) and not isinstance(shaped, dict):
                print(f"  FAIL {name}.{sec}: dict 段被改成了非 dict")
                ok = False
            if isinstance(val, list) and not isinstance(shaped, dict) and shaped is not val:
                print(f"  FAIL {name}.{sec}: list 段意外变形")
                ok = False
        # synthetic "translation": append a marker to each string of the first dict section
        target_sec = next((s for s, v in sections.items()
                           if isinstance(v, dict) and v
                           and isinstance(next(iter(v.values())), dict)), None)
        if target_sec is None:
            continue
        en_sec = sections[target_sec]
        zh_sec = json.loads(json.dumps(en_sec))
        changed_keys = []
        for oid, obj in zh_sec.items():
            if isinstance(obj, dict) and isinstance(obj.get("name"), str):
                obj["name"] += "·译"
                changed_keys.append(oid)
        flat = flatten_zh(en_sec, zh_sec, old_zh)
        # every changed row must land on a key that exists in old_zh when one
        # matches its "<id> Field" pattern (either casing)
        bad = []
        for oid in changed_keys:
            obj = en_sec[oid]
            field = "name"
            cap = "Name"
            cands = [k for k in old_zh if k in (f"{oid} {field}", f"{oid} {cap}")]
            used = [k for k in (f"{oid} {cap}", f"{oid} {field}") if k in flat]
            if cands and not used:
                bad.append(oid)
        print(f"  {name}: section={target_sec} changed={len(changed_keys)} "
              f"格式漂移={len(bad)} 旧行保留={all(k in flat for k in old_zh)}")
        if bad:
            ok = False
    print("SELFTEST", "PASSED" if ok else "FAILED")
    return 0 if ok else 1


def fetch_td() -> None:
    for name in DATASETS:
        snap = load_json(API / f"{name}.json")
        query = build_query(name, snap)
        log(f"{name}: en+zh 拉取（query {len(query)} 字符）")
        en_raw = gql(query, "en")
        zh_raw = gql(query, "zh")
        secs, rows = refresh_dataset(name, en_raw, zh_raw)
        log(f"{name}: 写回 {secs} 段 / 翻译 {rows} 条")


def main() -> int:
    ap = argparse.ArgumentParser(
        description="eftarkov.com 快照刷新 -> build_all -> 门禁（tarkov.dev 坐标层可选 --td）")
    ap.add_argument("--dry-run", action="store_true", help="只展示快照 diff，回滚且不构建")
    ap.add_argument("--no-fetch", action="store_true", help="跳过联网，只跑构建与门禁")
    ap.add_argument("--skip-build", action="store_true", help="跳过 build_all（只跑门禁）")
    ap.add_argument("--with-guides", action="store_true",
                    help="顺带下载新增任务的攻略配图（增量，不动已有图片）")
    ap.add_argument("--td", action="store_true",
                    help="重拉 tarkov.dev 坐标/标记快照（间歇 422，慢；仅在需要同步坐标数据时用）")
    ap.add_argument("--selftest", action="store_true", help="离线自测 tarkov.dev 重塑/展平逻辑")
    args = ap.parse_args()

    if args.selftest:
        return selftest()

    for d in ("tools/pyext", "tools/pylibs"):
        if not (ROOT / d).is_dir():
            die(f"缺少 {d}：python -m pip install --target tools/pylibs pillow numpy requests")

    if not args.no_fetch:
        snapshot_backup()
        try:
            fetch_eftarkov(args.with_guides)
            if args.td:
                fetch_td()
        except SystemExit:
            rollback()
            raise
        print("\n=== research 变更 ===")
        print(git_diff_stat(["research"]) or "（无变化）")
        if args.dry_run:
            rollback()
            log("dry-run 完成：快照已回滚，未构建")
            return 0

    if not args.skip_build:
        if run([sys.executable, str(TOOLS / "build_all.py")]) != 0:
            if not args.no_fetch:
                rollback()
            die("build_all.py 失败")

    if run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass",
            "-File", str(TOOLS / "check.ps1")]) != 0:
        if not args.no_fetch:
            rollback()
        die("check.ps1 失败：数据被门禁拦下")

    if run([sys.executable, str(TOOLS / "check_map_coverage.py")]) != 0:
        if not args.no_fetch:
            rollback()
        die("check_map_coverage.py 失败")

    print("\n=== 产物变更 ===")
    print(git_diff_stat(["TarkovAtlas/Assets", "research"]) or "（无变化）")
    log("门禁全部通过。git diff 人工审查后再自行 add/commit。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
