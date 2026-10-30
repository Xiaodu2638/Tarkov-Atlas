#!/usr/bin/env python3
"""Rebuild the Tarkov Atlas data bundle from tarkov.dev's JSON API.

Produces:
  TarkovAtlas/Assets/Maps/mapdata.json   maps + affine + labels + markers
  TarkovAtlas/Assets/quests.json         localized tasks with objective coords
  TarkovAtlas/Assets/traders/*.png       trader avatars (quest markers)

Base JPGs already exist; they are rotated 180 degrees in place so the view
matches the in-game map orientation, and the affine matrix is adjusted to keep
every game coordinate mapping onto the same physical spot.
"""
from __future__ import annotations

import json
import math
import pathlib
import shutil
import urllib.parse
import urllib.request

ROOT = pathlib.Path(__file__).resolve().parents[1]
RES = ROOT / "research"
ASSETS = ROOT / "TarkovAtlas" / "Assets"
MAPS = ASSETS / "Maps"
TRADERS = ASSETS / "traders"
import sys

# pyext holds the working Pillow build; pylibs only contains an empty stub.
sys.path.insert(0, str(ROOT / "tools" / "pyext"))
sys.path.insert(0, str(ROOT / "tools" / "pylibs"))
sys.path.insert(0, str(ROOT / "tools"))
from PIL import Image  # noqa: E402
# One implementation of the affine correction: the producer applies it, so the
# image rect and the content rect are both derived from the fixed matrix.
from build_assets import sanitize_affine  # noqa: E402

# Interactive-map key -> API map id (regular game mode)
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

# English display names for the interactive map keys
META_EN = {
    "ground-zero": "Ground Zero", "customs": "Customs", "factory": "Factory",
    "woods": "Woods", "interchange": "Interchange", "shoreline": "Shoreline",
    "reserve": "Reserve", "the-lab": "The Lab", "lighthouse": "Lighthouse",
    "streets-of-tarkov": "Streets of Tarkov", "the-labyrinth": "The Labyrinth",
    "terminal": "Terminal", "icebreaker": "Icebreaker",
}

# Chinese meta (official map name comes from the zh translation table)
META = {
    "ground-zero": ("中心区", "新手区域 · 高密度任务", "低", "城市封锁区，1–20 级任务的主要舞台。"),
    "customs": ("海关", "工业区 · 经典战场", "中", "宿舍、红厂房与加油站构成主要交火区。"),
    "factory": ("工厂", "室内近战 · 快节奏", "高", "多层工业厂房，Tagilla 的主场。"),
    "woods": ("森林", "开阔林地 · 远距离", "中", "伐木场与开阔林地，Shturman 出没。"),
    "interchange": ("立交桥", "超级商场 · 室内搜刮", "中", "ULTRA 商场与地下车库，电力开关是核心机制。"),
    "shoreline": ("海岸线", "度假村 · 多区域", "中", "村庄、码头、气象站与疗养院，跨度极大。"),
    "reserve": ("储备站", "军事基地 · 地下掩体", "高", "废弃军事基地，Raider 与 Glukhar 出没。"),
    "the-lab": ("实验室", "高危室内 · 需要门卡", "极高", "TerraGroup 地下研究设施，入场成本高。"),
    "lighthouse": ("灯塔", "海岸长图 · 水电站", "高", "狭长海岸地图，水电站与灯塔分布着 Rogues。"),
    "streets-of-tarkov": ("塔科夫街区", "城市巷战 · 超大地图", "高", "市中心街区，任务密集，后期主战场。"),
    "the-labyrinth": ("迷宫", "地下迷宫 · 高危", "极高", "古老迷宫，陷阱与高价值战利品并存。"),
    "terminal": ("码头", "港口终端 · 新区域", "高", "港口终端站及周边区域。"),
    "icebreaker": ("破冰船", "极地船只 · 多层室内", "极高", "冰封海域的巨型破冰船，垂直战场。"),
}

# Landmark label translations (community/official Chinese usage)
LABEL_ZH = {
    "Big Red": "红厂房", "Dorms": "宿舍", "New Gas": "新加油站", "Old Gas": "老加油站",
    "Fortress": "堡垒", "Crackhouse": "毒品屋", "Streamer House": "主播房",
    "Main Bridge": "主桥", "Sniper Hill": "狙击山", "Storage": "仓库区",
    "Trailer Park": "拖车停车场", "Junk Bridge": "废弃桥", "Repair Shop": "修理厂",
    "Sniper Ridge": "狙击岭", "Old Construction": "旧工地", "Skeleton": "骨架楼",
    "Warehouse 3": "3号仓库", "Warehouse 4": "4号仓库", "Warehouse 7": "7号仓库",
    "Warehouse 17": "17号仓库", "Depot": "仓库", "Military Checkpoint": "军事检查站",
    "Bus Station": "公交站", "Powerline Tower": "电塔",
    "Sawmill": "伐木场", "Scav Town": "Scav 镇", "Old Sawmill": "老伐木场",
    "Cultist Village": "邪教徒村", "USEC Camp": "USEC 营地", "Military Camp": "医疗营地",
    "Medical Camp": "医疗营地", "Military Camp (Woods)": "医疗营地",
    "Scav Camp": "Scav 营地", "Bandit Camp": "匪徒营地",
    "Ponds": "池塘", "Crash Site": "坠机点", "Checkpoint": "检查站", "Shack": "小屋",
    "Lumber": "木材堆", "Cabins": "木屋", "Bus Stop": "公交站", "Jaeger's Camp": "猎人之营地",
    "Sniper Rock": "狙击岩", "Convoy": "车队",
    "Power Station": "发电站", "Go Kart": "卡丁车场", "Four Camp": "四号营地",
    "Cargo Containers": "货运集装箱", "Wall Break": "围墙缺口", "Ramps": "坡道",
    "Oli Tower": "OLI 塔", "Idea Tower": "IDEA 塔", "Scav Camp": "Scav 营地",
    "Highway Construction": "公路施工区", "IDEA": "IDEA", "Goshan": "Goshan", "OLI": "OLI",
    "Nortex": "Nortex", "TRend": "TRend", "Mode7": "Mode7", "TTS": "TTS",
    "Book Store": "书店", "Dino Clothes": "恐龙服装店", "EMERCOM": "EMERCOM",
    "Kostin": "Kostin", "Bizarro": "Bizarro", "Spiel": "Spiel", "Voyage": "Voyage",
    "Viking": "Viking", "Mantis": "Mantis", "German": "German", "The National": "The National",
    "Brutal": "Brutal", "Kiba": "Kiba 武器店", "Pretty Lights": "Pretty Lights",
    "Telespot": "Telespot", "Revis": "Revis", "ADIK": "ADIK", "Generic": "Generic",
    "Top Brand": "Top Brand", "Sports": "Sports", "Yushka": "Yushka",
    "Rasmussen": "Rasmussen", "Avokado": "Avokado", "Boots 4 Life": "Boots 4 Life",
    "Texho": "Texho", "Dom": "Dom", "Father & Sons": "父子店", "Tarkovstar": "Tarkovstar",
    "Eastland": "Eastland", "Arena": "Arena", "Intourist": "Intourist",
    "Burger Spot": "汉堡店", "FCK": "FCK", "McDaniels": "McDaniels", "Tarducks": "Tarducks",
    "Coffee Joy": "Coffee Joy", "Jacob & Jacob": "Jacob & Jacob",
    "Sushi Huyushi": "Sushi Huyushi", "Underway": "Underway", "Burger House": "Burger House",
    "Philly Cute": "Philly Cute", "Tarbank": "Tarbank", "School": "学校",
    "Vet Clinic": "宠物诊所", "Hive": "Hive", "Family Market": "家庭超市",
    "Marked Hotel": "标记酒店", "South Hotel": "南部酒店", "Abandon Factory": "废弃工厂",
    "Diner": "餐馆", "Primorskij 49": "滨海街 49 号", "Prestigio Cafe": "Prestigio 咖啡",
    "Bilbo Coffee": "Bilbo 咖啡", "Kilmov Shopping Mall": "Klimov 商场", "Beluga": "Beluga 餐厅",
    "Cardinal Apartments": "Cardinal 公寓", "Construction": "工地", "Lexos": "Lexos 车行",
    "Sparja Grocery": "Sparja 超市", "Cinema": "电影院", "Shestyorochka": "六号超市",
    "Pinewood Hotel": "Pinewood 酒店", "Teppakot / Koener": "Teppakot / Koener",
    "Concordia": "Concordia 公寓", "Cardinal Bank": "Cardinal 银行", "LERM Expo": "LERM 展馆",
    "Sparja Express": "Sparja 便利店", "Post Office": "邮局", "Pharmacy 1": "药房 1",
    "Pharmacy 2": "药房 2", "Pharmacy 3": "药房 3", "Office": "办公室",
    "Corner Restaurant": "街角餐厅", "Zmejskij Alley": "Zmejskij 巷",
    "Kilmov St.": "Klimov 街", "Primorsky Ave.": "滨海大道", "Nikitskaya St.": "Nikitskaya 街",
    "Verhnyaya St.": "Verhnyaya 街", "Malevecha St.": "Malevecha 街",
    "Chekannaya St.": "Chekannaya 街", "Nizhnyaya St.": "Nizhnyaya 街",
    "Razvedchikov St.": "Razvedchikov 街", "Kamchatskaya St.": "Kamchatskaya 街",
    "Primorskij": "滨海街",
    "Train Yard": "火车站场", "Drug Lab": "毒品实验室", "Water Treatment": "水处理厂",
    "Plant 1": "1号厂区", "Plant 2": "2号厂区", "Plant 3": "3号厂区", "Pipes": "管道区",
    "Gunner Nest": "机枪塔", "Island": "小岛", "Cabins": "木屋", "Cottages": "小别墅",
    "Convenience": "便利店", "Red Brick": "红砖楼", "Hillside": "山坡", "Boathouses": "船屋",
    "Dead Tree": "枯树", "Pikes Peak Resort": "派克峰度假村", "Grand Chalet": "大木屋",
    "Tennis Court": "网球场", "Lightkeeper Island": "灯塔新区",
    "Parking": "停车场", "Vestibules #1": "1号门厅", "Vestibules #2": "2号门厅",
    "Main Working Area": "主工作区", "Negotiation Room": "谈判室", "Infirmary Lvl 1": "医务室 1F",
    "Infirmary Lvl 2": "医务室 2F", "Test Room": "测试室", "Server Room": "服务器房",
    "Warehouse Tunnels": "仓库隧道", "Warehouse": "仓库", "Warehouse Gate": "仓库大门",
    "Lecture Hall": "讲堂", "Recreation Area": "休息区", "Gym": "健身房",
    "Men's Locker Room": "男更衣室", "Women's Locker Room": "女更衣室",
    "Boiler Room": "锅炉房", "Security Barracks": "安保营房", "Reception": "接待处",
    "Administrative Office": "行政办公室", "Security Office": "安保办公室",
    "Security #1": "安保室 1", "Security #2": "安保室 2", "Conference Room": "会议室",
    "Cafeteria": "食堂", "Offices #1": "办公室 1", "Offices #2": "办公室 2",
    "Sterile Laboratory": "无菌实验室", "Control Room": "控制室",
    "Corridor To Building #2": "通往 2 号楼走廊", "K Buildings": "K 楼群",
    "White Queen": "白后", "White Pawn": "白兵", "Black Bishop": "黑象",
    "White Bishop": "白象", "White King": "白王", "Black Knight": "黑马", "White Knight": "白马",
    "White Rook": "白车", "Black Pawn": "黑兵", "Train Station": "火车站",
    "Barracks": "兵营", "E1 Bunkers": "E1 掩体", "E2 Bunkers": "E2 掩体",
    "Garage": "车库", "Mechanic": "机械师", "Gas Station": "加油站",
    "Shipping Yard": "货运场", "Dome": "圆顶", "Tarmac": "停机坪",
    "Office Building": "办公楼", "Main Office": "主办公室", "Breach Room": "突破房",
    "Locked Office": "上锁办公室", "North Stairs": "北楼梯", "South Stairs": "南楼梯",
    "Sky Bridge": "空中连廊", "Connector": "连接通道", "Locker Rooms": "更衣室",
    "Sinks": "水池", "Hole": "破洞", "Platform": "站台", "Servers": "服务器",
    "Med Tent": "医疗帐篷", "Silos": "筒仓", "Heli Crash": "直升机残骸", "Pit": "深坑",
    "Underground Stash": "地下藏匿点", "Scav Bunker": "Scav 掩体",
    "Blue Containers": "蓝色集装箱", "Wood Room": "木材间", "Glass Hall": "玻璃大厅",
    "Boilers": "锅炉区", "Pumping Station": "泵站", "East Halls": "东大厅",
    "Forklifts": "叉车区", "Rafters": "横梁区",
    "TerraGroup": "TerraGroup", "Skyside": "Skyside", "Fusion": "Fusion",
    "Empire": "Empire", "Capital Insight": "Capital Insight", "Nakatani": "Nakatani",
    "Elemental Global": "Elemental Global", "Oasis": "Oasis", "ASAP Winery": "ASAP 酒庄",
    "GAGRIN Hotel": "Gagarin 酒店", "M Showroom": "M 展厅", "Science Office": "科研办公室",
    "Scav Farm": "Scav 农场", "West Wing": "西楼", "East Wing": "东楼", "Admin": "行政楼",
    "Northwest Scav Bunker": "西北 Scav 掩体", "Lumber Mill": "伐木场",
    # generic geographic / landmark words remaining in the manifest
    "Bunker": "掩体", "Crane": "吊车", "Fashion Store": "服装店", "Figaro": "Figaro 餐厅",
    "Garage A": "A 区车库", "Garage B": "B 区车库", "Garage C": "C 区车库",
    "Garage D": "D 区车库", "K1": "K1 楼", "K2": "K2 楼", "K3": "K3 楼",
    "K4": "K4 楼", "K5": "K5 楼", "K6": "K6 楼", "Papillon": "Papillon 商店",
    "Pier": "码头", "Radio Tower": "无线电塔", "Resort": "疗养院",
    "SARA": "SARA 商店", "Scav Island": "Scav 岛", "Shiccos": "Shiccos 商店",
    "Swamp": "沼泽", "TECHLIGHT": "TECHLIGHT 商店", "Tank Bridge": "坦克桥",
    "Urban Clothes": "Urban 服装店", "Village": "村庄", "Weather Station": "气象站",
}

# Trader display names. The API only exposes object ids, and the translator table
# keys nicknames as "<id> Nickname", which is what used to leak hex ids into the UI.
TRADER_ZH = {
    "prapor": ("Prapor", "大老板"),
    "therapist": ("Therapist", "医生"),
    "fence": ("Fence", "商人"),
    "skier": ("Skier", "滑雪者"),
    "peacekeeper": ("Peacekeeper", "和平卫士"),
    "mechanic": ("Mechanic", "机械师"),
    "ragman": ("Ragman", "拾荒者"),
    "jaeger": ("Jaeger", "猎人"),
    "lightkeeper": ("Lightkeeper", "灯塔守护者"),
    "ref": ("Ref", "裁判"),
    "btr-driver": ("BTR Driver", "BTR 司机"),
    "radio-station": ("Radio Station", "无线电台"),
    "taran": ("Taran", "塔兰"),
    "mr-kerman": ("Mr. Kerman", "克尔曼先生"),
    "voevoda": ("Voevoda", "沃耶沃达"),
    "survivor": ("Survivor", "幸存者"),
}

# Boss / hostile-group display names, keyed by the API's "mob" identifier.
# The API leaves normalizedName empty for bosses, which is why every marker used
# to fall back to the literal string "Boss".
BOSS_ZH = {
    "bossBully": "Reshala",
    "bossKilla": "Killa",
    "bossTagilla": "Tagilla",
    "bossTagillaAgro": "Tagilla",
    "bossKojaniy": "Shturman",
    "bossZryachiy": "Zryachiy",
    "bossSanitar": "Sanitar",
    "bossGluhar": "Glukhar",
    "bossZryachiy": "Zryachiy",
    "bossKolontay": "Kollontay",
    "bossPartisan": "Partisan",
    "bossBoar": "Kaban",
    "bossKnight": "三狗",
    "bossWedge": "Wedge",
    "bossWedgeLab": "Wedge",
    "bossScourgeEventLab": "Scourge",
    "sectantPriest": "邪教徒祭司",
    "blackDivision": "黑色军团",
    "pmcBotBlackDiv": "黑色军团",
    "bossBullyBlackDiv": "黑色军团",
    "PmcBot": "掠夺者",
    # ex-USEC units are rogues (游荡者), not "流氓" and not a USEC boss.
    # Lighthouse runs exUsecFree; Icebreaker runs ExUsec.
    "exUsecFree": "游荡者",
    "ExUsec": "游荡者",
    "Sentry": "狙击手",
    "vsRF": "俄军",
    "vsRFSniper": "俄军狙击手",
    "raider": "Raider",
    "rogue": "Rogue",
    "cultist-priest": "邪教徒祭司",
}

# The API lists one boss object per spawn variant, sometimes five times for the
# same mob (Lighthouse rogues, Reserve raiders, Icebreaker boarding party). They
# are merged into one marker per mob below; without this the map stack showed
# duplicate entries at the same spot (and Lighthouse read as a "USEC" boss).
#
# Some upstream spawnLocation tables also mix zones that are not inside the
# named boss area. The curated case is Shturman: eftarkov's Shturman marker
# calibrates to just north of the Sawmill label, and Shturman patrols that one
# compound. The upstream ZoneWoodCutter table also lists points 170-210 m to
# the south-east (USEC Camp / Scav Town side) which are not his spawn, so the
# keep-radius is tightened to 120 m.
BOSS_ZONE_FILTER = {
    "woods": {"bossKojaniy": (10.0, -3.0, 120.0)},
}


def CleanEnglishName(raw: str, zh_title: str) -> str:
    """The API sometimes ships "<24-hex-id> name" instead of a real title."""
    text = (raw or "").replace(" Name", "").strip()
    if not text:
        return zh_title
    if IsHexId(text) or (text.lower().endswith(" name") and IsHexId(text[:-5].strip())):
        return zh_title
    return text


def IsHexId(value: str) -> bool:
    return len(value) == 24 and all(c in "0123456789abcdefABCDEF" for c in value)


def load(name: str):
    return json.loads((RES / name).read_text(encoding="utf-8"))["data"]


def flip_affine(aff: list[float], w: int, h: int) -> list[float]:
    a, b, c, d, e, f = aff
    return [-a, -b, w - c, -d, -e, h - f]


def main() -> None:
    for d in (MAPS, TRADERS):
        d.mkdir(parents=True, exist_ok=True)

    manifest = json.loads((MAPS / "maps.json").read_text(encoding="utf-8"))
    # Spline calibrations were written into mapdata.json by import_splines.py;
    # build_assets.py does not know about them, so carry them across rebuilds.
    prev_mapdata = {}
    if (MAPS / "mapdata.json").exists():
        try:
            prev_mapdata = {m["key"]: m for m in json.loads(
                (MAPS / "mapdata.json").read_text(encoding="utf-8"))["maps"]}
        except Exception:
            prev_mapdata = {}
    PREV_SPLINES = {k: m.get("spline") for k, m in prev_mapdata.items()
                    if m.get("spline")}
    api_root = load("api-maps.json")
    api_maps = api_root["maps"]
    maps_zh = load("api-maps-zh.json")
    api_tasks = load("api-tasks.json")["tasks"]
    tasks_zh = load("api-tasks-zh.json")
    traders = load("api-traders.json")
    loot_containers = api_root.get("lootContainers") or {}

    id_to_key = {v: k for k, v in KEY_TO_ID.items()}

    # ---- trader avatars -------------------------------------------------
    trader_meta = []
    for tid, tr in traders.items():
        if not isinstance(tr, dict):
            continue
        slug = tr.get("normalizedName") or tid
        # Prefer the curated table: the API only gives object ids, and the
        # translator table keys nicknames as "<id> Nickname" (which used to leak
        # raw hex ids into the sidebar).
        if slug in TRADER_ZH:
            english_name, zh_name = TRADER_ZH[slug]
        else:
            raw = (tr.get("name") or "").replace(" Nickname", "").strip()
            english_name = raw if raw and not IsHexId(raw) else slug.replace("-", " ").title()
            fallback = (tasks_zh.get(tid + " Nickname") or tasks_zh.get(tid) or "")
            zh_name = fallback.replace(" Nickname", "").strip() or english_name
        target = TRADERS / f"{slug}.png"
        if not target.exists():
            url = tr.get("imageLink") or f"https://assets.tarkov.dev/{tid}.webp"
            try:
                req = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0"})
                raw = urllib.request.urlopen(req, timeout=45).read()
                tmp = TRADERS / f".{slug}.tmp"
                tmp.write_bytes(raw)
                with Image.open(tmp) as im:
                    im = im.convert("RGBA")
                    side = min(im.size)
                    im = im.crop(((im.width - side) // 2, (im.height - side) // 2,
                                  (im.width + side) // 2, (im.height + side) // 2))
                    im = im.resize((96, 96), Image.Resampling.LANCZOS)
                    im.save(target, "PNG")
                tmp.unlink(missing_ok=True)
            except Exception as exc:  # noqa: BLE001
                print(f"  avatar failed {slug}: {exc}")
        trader_meta.append({"id": tid, "slug": slug,
                            "name": english_name, "nameZh": zh_name})

    # ---- maps ----------------------------------------------------------
    # Extra hand-curated points of interest (currently the Labyrinth puzzle
    # items, which the API does not expose as a marker category).
    poi_path = ROOT / "tools" / "poi.json"
    pois = json.loads(poi_path.read_text(encoding="utf-8")) if poi_path.exists() else {}

    out_maps = []
    for entry in manifest["maps"]:
        key = entry["key"]
        mid = KEY_TO_ID.get(key)
        api = api_maps.get(mid) if mid else None
        img_path = MAPS / entry["image"]
        with Image.open(img_path) as im:
            w, h = im.size
        # build_assets.py already produced the artwork in the official in-game
        # orientation, and the manifest affine matches it. Never rotate here:
        # doing it in two places is what flipped the maps upside-down before.
        aff = sanitize_affine(entry["affine"])
        # Spline calibrations (tools/import_splines.py) live on the previous
        # mapdata entry: build_assets rewrites maps.json without them, so carry
        # them forward across rebuilds.
        spline = PREV_SPLINES.get(key)

        name_zh = META.get(key, (key, "", "", ""))[0]
        if mid and (maps_zh.get(mid + " Name")):
            name_zh = maps_zh[mid + " Name"]
        name_en = META_EN.get(key, key)

        labels = []
        for lab in entry.get("labels", []):
            text = lab["text"]
            zh = LABEL_ZH.get(text, text)
            labels.append({"x": lab["x"], "z": lab["z"], "text": zh, "en": text,
                           "size": lab.get("size", 100)})

        extracts, bosses, spawns, transits, loot = [], [], [], [], []
        caches = []
        if api:
            bx0, bz0, bx1, bz1 = (entry["bounds"] if "bounds" in entry else (0, 0, 0, 0))

            def inside(p: dict) -> bool:
                return bx0 - 60 <= p.get("x", 1e18) <= bx1 + 60 and bz0 - 60 <= p.get("z", 1e18) <= bz1 + 60

            for e in api.get("extracts", []):
                pos = e["position"]
                # The API files a few Woods extracts under Ground Zero too; drop
                # anything that does not actually fall inside this map's artwork.
                if not inside(pos):
                    continue
                zh_name = maps_zh.get(e["name"], e["name"])
                extracts.append({
                    "name": zh_name, "en": e["name"], "faction": e.get("faction", "pmc"),
                    "x": pos["x"], "z": pos["z"],
                })
            for b in api.get("bosses", []):
                mob = b.get("mob") or ""
                positions = []
                for loc in b.get("spawnLocations", []):
                    for p in loc.get("positions", []):
                        x, z = p["x"], p["z"]
                        zone = BOSS_ZONE_FILTER.get(key, {}).get(mob)
                        if zone and math.hypot(x - zone[0], z - zone[1]) > zone[2]:
                            continue
                        positions.append((x, z))
                if not positions:
                    continue

                chance = b.get("spawnChance", 0) or 0
                merged = next((x for x in bosses if x["mob"] == mob), None)
                if merged is None:
                    merged = {
                        "name": BOSS_ZH.get(mob)
                                or BOSS_ZH.get((b.get("normalizedName") or "").lower())
                                or mob
                                or "Boss",
                        "mob": mob,
                        "chance": chance,
                        "points": [],
                    }
                    bosses.append(merged)
                else:
                    merged["chance"] = max(merged["chance"], chance)

                seen = {(round(p["x"], 1), round(p["z"], 1)) for p in merged["points"]}
                for x, z in positions:
                    dedupe = (round(x, 1), round(z, 1))
                    if dedupe in seen:
                        continue
                    seen.add(dedupe)
                    merged["points"].append({"x": x, "z": z})

            for s in api.get("spawns", []):
                pos = s.get("position") or {}
                if "x" not in pos:
                    continue
                # The API also emits defender/boss/rogue positions in this list.
                # Only points flagged "player" are real insertion spawns; keeping
                # the rest painted USEC/rogue bots on the Lighthouse peninsula
                # as if they were PMC spawns.
                if "player" not in (s.get("categories") or []):
                    continue
                sides = s.get("sides") or []
                spawns.append({"x": pos["x"], "z": pos["z"],
                               "side": "scav" if "scav" in sides else "pmc"})
            for t in api.get("transits", []):
                pos = t.get("position") or {}
                if "x" not in pos:
                    continue
                dest = id_to_key.get(t.get("map", ""), "")
                transits.append({"x": pos["x"], "z": pos["z"], "dest": dest})

            # Buried caches: the lootContainer table lists every loose container
            # on the map, so keep only the two the game actually buries. The
            # entry id is looked up in the global lootContainers definition table.
            # Every other lootable container becomes a categorised loot spot
            # (weapon boxes, safes, jackets, drawers, med cases, ...) which the
            # legend exposes as its own toggle group.
            for item in api.get("lootContainers", []) or []:
                pos = item.get("position") or {}
                if "x" not in pos:
                    continue
                cid = item.get("lootContainer") or ""
                definition = loot_containers.get(cid) or {}
                kind = (definition.get("normalizedName") or "").lower()
                if not kind:
                    continue
                if kind in ("buried-barrel-cache", "ground-cache"):
                    caches.append({"x": pos["x"], "z": pos["z"], "kind": kind})
                else:
                    loot.append({"x": pos["x"], "z": pos["z"], "kind": kind})

        meta = META.get(key, (key, "", "", ""))
        players = api.get("players") if api else entry.get("players", "")

        # A marker a little outside the artwork (Gate_o sits ~1.5 m past the
        # south edge) still belongs to this map: clamp it onto the border so it
        # stays visible. Anything far outside belongs to another zone and would
        # render as a dot floating in the black margin, so drop it.
        ia, ib = aff[4], -aff[1]
        ic = aff[1] * aff[5] - aff[4] * aff[2]
        id_, ie = -aff[3], aff[0]
        iff = aff[3] * aff[2] - aff[0] * aff[5]
        det = aff[0] * aff[4] - aff[1] * aff[3]

        def place(p: dict) -> dict | None:
            if abs(det) < 1e-12:
                return p
            px = aff[0] * p["x"] + aff[1] * p["z"] + aff[2]
            py = aff[3] * p["x"] + aff[4] * p["z"] + aff[5]
            cx = min(max(px, 0), w - 2)
            cy = min(max(py, 0), h - 2)
            if math.hypot(px - cx, py - cy) > 160:
                return None
            if (cx, cy) == (px, py):
                return p
            gx = (ia * cx + ib * cy + ic) / det
            gz = (id_ * cx + ie * cy + iff) / det
            return {**p, "x": gx, "z": gz}

        dropped = 0
        keep_extracts = []
        for e in extracts:
            placed = place(e)
            if placed is None:
                dropped += 1
            else:
                keep_extracts.append(placed)
        extracts = keep_extracts

        keep_spawns = []
        for s in spawns:
            placed = place(s)
            if placed is None:
                dropped += 1
            else:
                keep_spawns.append(placed)
        spawns = keep_spawns

        for bz in bosses:
            keep_points = []
            for p in bz["points"]:
                placed = place(p)
                if placed is None:
                    dropped += 1
                else:
                    keep_points.append(placed)
            bz["points"] = keep_points
        bosses = [b for b in bosses if b["points"]]
        if dropped:
            print(f"    dropped {dropped} off-art marker(s) for {key}", flush=True)

        # Keep only loot spots that actually fall on the artwork.
        keep_loot = []
        for item in loot:
            placed = place(item)
            if placed is not None:
                keep_loot.append(placed)

        out_maps.append({
            "key": key,
            "name": name_zh,
            "nameEn": name_en or entry.get("nameEn") or (api or {}).get("name", ""),
            "subtitle": meta[1],
            "description": meta[3],
            "difficulty": meta[2],
            "players": players or "—",
            "raidMinutes": (api or {}).get("raidDuration") or entry.get("raidMinutes", 0),
            "image": entry["image"],
            "width": w,
            "height": h,
            "affine": aff,
            "spline": spline,
            "bounds": entry["bounds"],
            "content": entry.get("content") or [],
            "labels": labels,
            "extracts": extracts,
            "bosses": bosses,
            "spawns": spawns,
            "transits": transits,
            "caches": caches,
            "loot": keep_loot,
            "pois": pois.get(key, []),
            "source": entry.get("source", ""),
            "order": entry["order"],
            "flipped": True,
        })

    # ---- tasks ---------------------------------------------------------
    out_tasks = []
    for tid, q in api_tasks.items():
        name_en = q.get("name", tid)
        title = (tasks_zh.get(name_en) or "").replace(" Name", "").strip()
        if not title or IsHexId(title) or title.lower().endswith(" name"):
            title = name_en.replace(" Name", "").replace("-", " ").title()
        # The API ships "<24-hex-id> name" for every task; the original English
        # title is only in the wiki link. Without this fallback nameEn stayed a
        # copy of the Chinese title, so English search never matched.
        english_name = CleanEnglishName(name_en, title)
        if not english_name.isascii():
            wiki_slug = urllib.parse.unquote((q.get("wikiLink") or "").rsplit("/", 1)[-1])
            wiki_slug = urllib.parse.unquote(wiki_slug).replace("_", " ").strip()
            if wiki_slug and ":" not in wiki_slug:
                english_name = wiki_slug
        objectives = []
        maps_touched: list[str] = []
        for o in q.get("objectives", []):
            # Optional objectives (side conditions such as "also bring X") still
            # carry real map coordinates; skipping them made multi-location
            # tasks lose their pins, so every zoned objective is kept.
            desc_en = o.get("description", "")
            desc = (tasks_zh.get(desc_en) or "").replace(" description", "") or desc_en
            zones = []
            for z in o.get("zones", []) or []:
                zid = z.get("map", "")
                mkey = id_to_key.get(zid)
                pos = z.get("position") or {}
                if not mkey or "x" not in pos:
                    continue
                zones.append({"map": mkey, "x": pos["x"], "z": pos["z"]})
                if mkey not in maps_touched:
                    maps_touched.append(mkey)
            # findQuestItem objectives don't use zones; they name the maps the
            # item can spawn on (possibleLocations) with candidate positions.
            # Skipping them left tasks like "主钥匙" (Master Key) with a map but
            # no jump coordinates and no pins.
            for loc in o.get("possibleLocations", []) or []:
                mkey = id_to_key.get(loc.get("map", ""))
                if not mkey:
                    continue
                for pos in loc.get("positions", []) or []:
                    if "x" not in pos:
                        continue
                    zones.append({"map": mkey, "x": pos["x"], "z": pos["z"]})
                    if mkey not in maps_touched:
                        maps_touched.append(mkey)
            otype = o.get("type", "")
            if zones:
                objectives.append({"text": desc, "type": otype, "zones": zones})
        # Only maps with real objective coordinates count as "this task is done
        # here". The API's top-level task.map is often a rough guess (it puts the
        # Therapist "Private clinic" on Factory), so prefer the zones.
        map_keys = list(maps_touched)
        if not map_keys:
            qmap = id_to_key.get(q.get("map", ""))
            if qmap:
                map_keys.append(qmap)

        out_tasks.append({
            "id": tid,
            "name": title,
            "nameEn": english_name,
            "trader": q.get("trader", ""),
            "level": q.get("minPlayerLevel", 0),
            "wiki": q.get("wikiLink", ""),
            "maps": map_keys,
            "prerequisites": [r.get("taskId") for r in (q.get("taskRequirements") or [])
                              if r.get("status") in ("complete", "active") and r.get("taskId")],
            "objectives": objectives,
        })
    out_tasks.sort(key=lambda t: (t["name"]))

    # Markers live in their own file so rebaking mapdata (artwork metadata) never
    # touches task/POI points, mirroring the toolbox's split of 底图 vs 任务点位.
    markers = {m["key"]: {
        "extracts": m["extracts"],
        "bosses": m["bosses"],
        "spawns": m["spawns"],
        "transits": m["transits"],
        "caches": m["caches"],
        "loot": m["loot"],
        "pois": m["pois"],
    } for m in out_maps}
    (MAPS / "markers.json").write_text(
        json.dumps({"generatedAt": "2026-10-27", "maps": markers}, ensure_ascii=False, indent=1),
        encoding="utf-8")

    (MAPS / "mapdata.json").write_text(
        json.dumps({"generatedAt": "2026-10-27", "maps": out_maps, "traders": trader_meta},
                   ensure_ascii=False, indent=1), encoding="utf-8")
    (ASSETS / "quests.json").write_text(
        json.dumps({"generatedAt": "2026-10-27", "source": "tarkov.dev JSON API",
                    "quests": out_tasks}, ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"maps={len(out_maps)} tasks={len(out_tasks)} traders={len(trader_meta)}")


if __name__ == "__main__":
    main()