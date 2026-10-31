# -*- coding: utf-8 -*-
r"""Boss 搬迁修正：把 tarkov.dev 快照里滞后于游戏版本的 Boss 挪到正确地图。

背景：上游快照更新滞后。1.3 起 Glukhar 从灯塔（Zone_Hellicopter）搬到储备站，
eftarkov 的灯塔地图页已经没有他的分类、储备站有 6 个刷点标记。快照是管线的
唯一 Boss 来源（build_data.py 直接消费），所以确认后直接改 research/api/。

用法：
    python tools/boss_relocate.py          # 有出入才写入（幂等）
    python tools/boss_relocate.py --check  # 只检查，不写

update_data.py 重拉快照会覆盖本修正；重拉后跑一次本脚本即可恢复。等上游
tarkov.dev 把 Boss 位置修对之后，删掉 RELOCATIONS 里对应条目即可。
"""
from __future__ import annotations

import json
import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parents[1]
SNAPSHOT = ROOT / "research" / "api" / "maps.json"

MAP_IDS = {
    "lighthouse": "5704e4dad2720bb55b8b4567",
    "reserve": "5704e5fad2720bc05b8b4567",
}

# mob -> (from, to, [(x, z, y), ...])
# 坐标 = eftarkov 储备站地图页的 Glukhar 标记像素，经该图像素->游戏仿射换算
# （锚点：已验证剧情点 2 / 同名地标 2 / 同任务唯一 zone 10，拟合残差中位
# 6.7 m）。落点与白车 / K楼群 / 白马 / 停机坪 / K4地堡 / 白后 六个地标吻合，
# 正是储备站 Glukhar 的巡逻圈。y 只有 K4 一个地下点，渲染与校验都不用它。
RELOCATIONS = {
    "bossGluhar": (
        "lighthouse",
        "reserve",
        [
            (167.8, -109.4, -2.0),
            (33.0, -97.0, -2.0),
            (71.7, -22.1, -2.0),
            (-132.8, 57.6, 3.0),
            (62.1, -121.6, -14.0),
            (-28.4, 168.8, 3.0),
        ],
    ),
}

ZONE_NAME = "Zone_Glukhar"


def current_state(maps: dict) -> dict[str, tuple[str, int] | None]:
    """mob -> (which map holds it now, spawn point count)"""
    out: dict[str, tuple[str, int] | None] = {}
    for key, mid in MAP_IDS.items():
        for boss in maps.get(mid, {}).get("bosses") or []:
            mob = boss.get("mob")
            if mob in RELOCATIONS:
                n = sum(len(s.get("positions") or []) for s in boss.get("spawnLocations") or [])
                out[mob] = (key, n)
    return out


def main() -> int:
    check_only = "--check" in sys.argv
    data = json.loads(SNAPSHOT.read_text(encoding="utf-8"))
    maps = data["data"]["maps"]
    state = current_state(maps)
    changed = False

    for mob, (src, dst, points) in RELOCATIONS.items():
        wanted = {(round(x, 2), round(z, 2)) for x, z, _ in points}
        held = state.get(mob)
        if held is not None:
            where, n = held
            mine = maps[MAP_IDS[where]]["bosses"]
            got = {(round(p["x"], 2), round(p["z"], 2))
                   for b in mine if b.get("mob") == mob
                   for s in b.get("spawnLocations") or [] for p in s.get("positions") or []}
            if where == dst and got == wanted:
                print(f"[ok] {mob} already on {dst} ({n} points)")
                continue

        if check_only:
            print(f"[stale] {mob}: on {held[0] if held else 'nowhere'}, expected {dst}")
            return 1

        source = None
        for mid in (MAP_IDS[src], MAP_IDS[dst]):
            bosses = maps[mid]["bosses"]
            for b in list(bosses):
                if b.get("mob") == mob:
                    source = b
                    bosses.remove(b)
        record = dict(source or {})
        record["mob"] = mob
        record.setdefault("spawnChance", 1)
        record["spawnLocations"] = [{
            "name": ZONE_NAME,
            "chance": 1,
            "spawnKey": ZONE_NAME,
            "positions": [{"x": x, "y": y, "z": z} for x, z, y in points],
        }]
        maps[MAP_IDS[dst]]["bosses"].append(record)
        changed = True
        print(f"[fixed] {mob}: {src} -> {dst} ({len(points)} points)")

    if check_only:
        print("[ok] snapshot matches the relocation table")
        return 0
    if not changed:
        return 0
    SNAPSHOT.write_text(json.dumps(data, ensure_ascii=False, separators=(",", ":")),
                        encoding="utf-8", newline="\n")
    print("snapshot updated; rerun tools/build_data.py")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
