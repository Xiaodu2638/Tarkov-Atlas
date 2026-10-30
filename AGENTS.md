# AGENTS.md

Tarkov Atlas —— 《逃离塔科夫》离线战术地图与任务助手（Windows / Avalonia / NativeAOT）。

本文件面向在本仓库工作的 agent。**动手前先读第 5 节的约束**，那里记录了几处会让构建或验证静默出错的坑。

---

## 1. 项目定位

桌面端离线地图工具。玩家在副屏或窗口模式下跑游戏时，用一块不遮挡游戏、可交互的高清地图查撤离点、Boss 刷点、危险区，并把「这一局该做什么任务」直接标在图上。

运行时**完全离线**：底图、标记点、任务库、剧情、地点攻略图全部打包进 `Assets/`，不请求任何网络；「打开 Wiki」只是用系统浏览器打开 eftarkov.com 的网页。

---

## 2. 常用命令

```powershell
# 构建（Release；NativeAOT 优先，失败自动回退自包含）
powershell -NoProfile -ExecutionPolicy Bypass -File ./build.ps1

# 三套自检（提交前必跑，全绿才是基线）
powershell -NoProfile -ExecutionPolicy Bypass -File ./tools/check.ps1

# 单独跑某套
dotnet run --project tools/datacheck  -c Release
dotnet run --project tools/logiccheck -c Release
dotnet run --project tools/shotcheck  -c Release

# 联网更新数据（重拉快照 -> build_all -> 门禁；从不自动提交，失败自动回滚快照）
python tools/update_data.py            # 全流程
python tools/update_data.py --dry-run  # 只 diff 快照并回滚
python tools/update_data.py --selftest # 离线自测快照重塑/翻译展平
```

改了代码要做任何运行时验证之前**先重跑 `build.ps1`**：验证脚本打的是 `dist/` 里的 exe，代码提交了但 dist 没重发，测到的就是旧版本（曾把「功能正常」误判成「左键拖拽没反应」）。

产物：`dist/TarkovAtlas-win-x64/TarkovAtlas.exe`（约 20 MB 原生 exe，整个目录约 263 MB，需同目录的 `av_libglesv2.dll`、`libSkiaSharp.dll`、`libHarfBuzzSharp.dll` 与 `Assets/`；`Scripts/ocr.ps1` 已内嵌进 exe，不随包分发）。脚本同时打一个同名 zip；`-NoAot` 跳过 AOT，`-OutputRoot <dir>` 换输出目录。

构建与自检会强制把 `TEMP`/`TMP`、`NUGET_PACKAGES`、`DOTNET_CLI_HOME` 指到仓库内（`.tmp/`、`.nuget-packages/`、`.dotnet-home/`），不写主机。**不要裸跑 `dotnet publish`**，会污染 `%TEMP%` 且可能锁住 `bin/`。上面「单独跑某套」的 `dotnet run` **不做**这套重定向（只有 `check.ps1` 会）：要么直接用 `check.ps1`，要么在同一个 shell 里先导出这几个变量。

---

## 3. 目录结构

```
TarkovAtlas/                    应用主体（C# / Avalonia）
  Program.cs                    入口（STAThread + Avalonia AppBuilder）
  App.axaml(.cs)                应用与样式
  AppJsonContext.cs             源生成的 JSON 上下文（反射已禁用，新类型必须登记）
  MainWindow.axaml(.cs)         主窗口：地图列表、任务抽屉、设置、日志监听
  OverlayWindow.axaml(.cs)      悬浮小窗（置顶；左键拖图平移、右键拖窗、滚轮以光标为锚缩放）
  SettingsWindow.axaml(.cs)     设置界面
  Controls/MapCanvas.cs         自绘地图控件（全部要素手绘，性能关键路径）
  Models/
    MapModels.cs                地图/标记/危险区模型 + HazardRules
    AppSettings.cs              持久化设置
    QuestModels.cs              任务与剧情模型
    ScreenshotInfo.cs           截图坐标与朝向
    MapNames.cs                 地图显示名
  Services/
    MapRepository.cs            加载 mapdata.json、按需加载楼层位图
    QuestService.cs             任务库、剧情、进度读写
    SettingsService.cs          AppPaths（含 TARKOVATLAS_DATA_DIR 重定向）
    ScreenshotService.cs        截图目录监听与解析
    GameLogService.cs           游戏日志监听、地图自动识别
    HotkeyService.cs            全局热键（Win32 低级键盘钩子）
    OcrService.cs               截图 OCR
  Assets/
    maps/mapdata.json           运行时地图清单（唯一可信来源）
    maps/*.jpg                  13 张底图（最长边 5600 px）
    maps/layers/index.json      楼层图索引
    maps/layers/<key>/*.png     楼层叠加图（与底图共用像素网格）
    maps/hazards.json           雷区 / 狙击区 / 其它危险区多边形
    quests.json                 任务库（502 条）
    story.json                  剧情（10 章 / 254 目标）
    bosses/*.png                Boss 头像
    traders/*.png               商人头像
    legend/*.png                图例图标（48×48）
    guides/<questId>/*.webp     任务地点攻略图
  Scripts/ocr.ps1               内嵌资源（OCR 调用）
tools/
  check.ps1                     三套自检入口
  datacheck/                    数据层断言（地图几何、标记、任务、楼层、hazard）
  logiccheck/                   服务层断言（QuestService、剧情、商人、跳转）
  shotcheck/                    截图解析与删除行为
  build_all.py                  管线入口（按依赖顺序跑完下面四步）
  build_assets.py               渲染 SVG（迷宫 warp 进解析栅格）→ 生成底图 + maps.json
  build_layers.py               烘焙楼层 PNG + layers/index.json + hazards.json
  build_data.py                 合并 API → mapdata.json / markers.json / quests.json
  build_story.py                剧情 → story.json
  update_data.py                联网更新入口：重拉 research/api 快照 -> build_all -> 门禁（可回滚）
  check_map_coverage.py         底图内容区覆盖率自检（第 7 节验收标准用它）
  capture_docs.py               驱动 dist 产物重拍 docs/ 四张界面截图
  poi.json                      手工维护的 POI 表（迷宫解谜道具）
  tile-cache/                   瓦片下载缓存（git 忽略）
research/                       管线输入，全部进 git
  api/                          tarkov.dev API 快照（maps/tasks/traders + *.zh.json）
  upstream/tarkov-dev-maps.json 上游楼层与仿射表
  story/zh-raw.json             剧情中文抓取
  story/coords.json             剧情坐标标定表（只含双源验证过的点）
  svg/<地图 key>/*.svg          矢量源，一张地图一个子目录
  svg/LICENSE.md                上游矢量图许可全文
docs/                           界面截图（README 引用）
```

`tools/pyext/`、`tools/pylibs/`、`tools/tile-cache/` 不进 git（见 `.gitignore`），其余源码与 `research/` 输入都随仓库分发。`app` 的构建和运行都不碰 Python。

---

## 4. 架构要点

### 4.1 坐标与投影
- 每个 `MapDefinition` 带一个仿射矩阵：`px = a*x + b*z + c; py = d*x + e*z + f`，把游戏坐标映射到图片像素。
- `MapCanvas.Project()` 是唯一投影入口，所有标记都走它。
- `maps.json`（`build_assets.py` 产物）里的 affine **未经人工校正**，运行时以 `mapdata.json` 为准。两者不一致时先查原因，**不要直接覆盖**。
- **底图必须上北下南**：图片帧 = 游戏坐标按 `coordinateToCardinalRotation` 旋转后，`px ∝ east`、`py ∝ -north`；SVG 再按 `(cardinal − coordinateRotation) mod 360` 转正（`svg_turn()`），等比缩放后拉伸进 cardinal bounds 矩形（与 tarkov.dev 的 `L.svgOverlay` 一致）。11 张图 `turn=0`，reserve 是 15.2°（`svg_canvas()` 绕中心转完再裁回原帧，否则地图会缩在黑角里），其余都在 90° 的整数倍上。
- **SVG 底图只渲染地面层组**：这些 SVG 把各层叠在一个文件里，整份渲染会重影（工厂四层、海关/立交桥/地面零区/街区/实验室都一样）。`ground_group()` 取 `Ground_Level` → `Ground_Floor` → `First_Level`，其余层是楼层选择器里的 `svgLayer`。工厂竖版 5183×5600。
- **`content` 是像素矩形，不是游戏坐标**：`build_assets.py` 的 `content_box()` 用「边框环众数色 + 12 灰阶」找出实际画出来的区域，直接把像素框写进 `mapdata.json` 的 `content`；`MapCanvas.EnsureLayout()` 按它做 fit + 居中，所以画布边缘的空白不会把地图缩成一小块。像素是因为旋转后的仿射无法把轴对齐的图画框映射成轴对齐的世界矩形。`bounds` 仍是整张图片的世界范围（`datacheck` 用它做一致性断言），两者别混。
- **仿射必须各向同性**：世界单位是米、图片保持比例，所以 `|a| == |e|`。`build_assets.py` 里 `uniform_transform()` 会在拼瓦片前把上游占位 `transform`（如工厂的 `sx=2.0, sy=3.5`）统一成水平尺度，`sanitize_affine()` 在产物侧再兜一次底。数据里出现各向异性矩阵一律按 bug 处理。

### 4.2 渲染
- `MapCanvas.Render()` 每次整帧重绘。性能红线：
  - `DrawBitmapCropped()` 只把视口内的源矩形交给 Skia，禁止整图重采样。
  - 文本走 `_textCache`，画笔走 `_penCache`，菱形/多边形几何走 `_diamondCache`。
  - `Compact`（悬浮窗）跳过标签、比例尺、指北针。
  - 任务标记在 `ShowQuestMarkers=false` 时直接跳过生成与绘制。
- **房间级标记统一走 `MapCanvas.LootZoomGate = 4.0`**：容器点与物资埋藏点只有放大到 400% 及以上才渲染（图例里也都归在「物资」组）。加新类别时别再各写一套门槛。
- **图标尺寸用 `MarkerIcon(base, max, min)`**：纯 `sqrt(zoom)` 在整个 0.35x–32x 区间只覆盖约 1:1.6 的变化，加上较宽的下限后，概览缩放下撤离点/转移点/BTR 会比建筑还大。该helper按 `zoom^0.45` 缩放并保留原上限，概览时明显缩小、贴近时回到原来的大小。
- **悬浮窗图标再乘 `CompactShrink`**：Compact 画布把整张图塞进 ~390px 小窗，按主窗口地图区（~700px）调好的图标仍然偏大；helper 内部已按 `min(w,h)/700` 自动缩小（0.30–1.00），主窗口不受影响。新增标记类别直接复用 `MarkerIcon`。
- 楼层图与底图共用同一目标矩形，运行时直接叠加，不做单独缩放。

### 4.3 危险区
- 规则集中在 `HazardRules`（`Models/MapModels.cs`）。
- `minefield` / `sniper` 始终按多边形填充。
- 其它 generic `hazard` 若面积超过 `AreaLevelFraction = 0.02`（地图 bounds 的 2%），只画质心 `!` 标记，避免一块 50 m 级区域盖住整张图。
- 数据本身不做删改，只在展示层过滤。

### 4.4 数据管线（输入进 git，产物由脚本生成）
- 入口：`python tools/build_all.py`（等价于依次跑下面四步）。顺序：`build_assets.py` → `build_layers.py` → `build_data.py` → `build_story.py`。所有地图（含冰破船）都在同一条管线里。
- 局部重建：`build_all.py --only factory`、`build_assets.py --only factory`、`build_layers.py --only factory`。其余地图的 manifest 条目和楼层索引会合并保留，不会清空。
- **冰破船在同一管线内**：`build_layers.py` 用 `ICE_FLOOR_FILES` 把上游的层名映射到 `research/svg/icebreaker/*.svg`，逐层走 `bake_svg_layer`。不要为它另建入口脚本，也不要在 `build_layers.py` 里跳过它。
- **迷宫底图不下载瓦片**：美术本来就是 `Labyrinth.svg` 经锚点矩阵 warp 进「瓦片像素栅格」；栅格的尺寸和仿射是纯几何（`build_assets.tile_grid()` 按投影公式直接算），不需要 240 张瓦片提供。`stitch_tiles()` 只剩 reserve 2–5 层、customs 4 层在用。
- **地图 → SVG 只有一处映射**：`build_assets.svg_source(key)` 按 `research/svg/<key>/` 目录查找（单文件地图取唯一的 `*.svg`，破冰船取 `BASE_DECK`）。`build_layers.py` 复用它，不要再写第二张文件名表。
- build 脚本靠本机 headless Edge 栅格化 SVG（可用 `EDGE_PATH` 覆盖）；瓦片只有 reserve 2–5 层、customs 4 层在用，缓存在 `tools/tile-cache/`（git 忽略，可离线重跑）。渲染中间产物写到 `.tmp/build/`（git 忽略，不写 `%TEMP%`）。
- **构建期零联网**：API 快照、矢量源、剧情、头像、地点图都已提交进 `research/` 或 `Assets/`；`build_data.py` 不再下载头像（缺 PNG 只提示，应用回退色点）。
- 外部依赖：`tools/pyext`（Pillow）、`tools/pylibs`（numpy、requests）。重建：
  `python -m pip install --target tools/pylibs pillow numpy requests`
- 上游快照放在 `research/`：`api/`（tarkov.dev API）、`upstream/tarkov-dev-maps.json`（楼层与仿射）、`svg/<key>/`（矢量图）、`story/`（剧情中文 + 坐标标定表）。

---

## 5. 约束与已知的坑

1. **一切留在工作区内。** 不写 `%APPDATA%`、`%TEMP%`、全局 git config、全局 pip。测试项目和非提权构建用 `TARKOVATLAS_DATA_DIR` 重定向应用状态（提权版见下一条）。
2. **管理员清单**：`app.manifest` 内嵌 `requireAdministrator`（全局热键需要）。副作用是**非交互 shell 里启动 exe 会卡在 UAC**；自动化验证请用非提权构建（`-p:ApplicationManifest=`，改完须先删 `TarkovAtlas/obj`，apphost 会增量缓存旧 manifest），并按 PID 定位窗口后再截图。同时务必先清掉残留实例——否则截到的是旧窗口，而且哈希会完全一致。另外 UAC 提权**不继承父进程环境变量**，所以发布版 exe 永远读 `%APPDATA%\TarkovAtlas\settings.json`，`TARKOVATLAS_DATA_DIR` 对它无效；要用干净配置验证，先备份挪走那份 settings.json，验完还原。
3. **构建**必须设 `AVALONIA_TELEMETRY_OPTOUT=1`，否则 Avalonia 遥测写日志失败会让整个 build 以误导性的 MSB4018 崩掉。`build.ps1` / `tools/check.ps1` 已经设好，只有绕过脚本直接跑 `dotnet build|publish` 时才需要自己设。
4. **JSON 走源生成**（`JsonSerializerIsReflectionEnabledByDefault=false`）：新增要序列化/反序列化的类型必须加进 `AppJsonContext.cs` 的 `[JsonSerializable]` 列表，否则 NativeAOT 下要等到运行时才炸。
5. **`dotnet publish` 的输出目录被占用会锁住 `bin\Release\...`**；用暂存目录或 `-p:OutputPath=`。
6. **不要加 NuGet 外网依赖**：只能用 `.nuget-packages` 里的离线缓存。
7. **别 `git clean -xdf`**：会连 `research/` 的矢量源一起删掉（`tools/pyext`、`tools/pylibs`、`tools/tile-cache` 也会没）。
8. **文件写入偶发 `file no longer exists — re-read`** 是沙箱常态，重读再写即可，不是逻辑错误。往 `.tmp/` 丢临时脚本时**别用标准库模块名**（`struct.py` 会盖住 stdlib 的 `struct`，因为 CWD 在 `sys.path` 里）。
9. **上游端点现状（实测）**：`assets.tarkov.dev` 403；`api.tarkov.dev/graphql` 可用但间歇 422 "GraphQL server unavailable"（update_data.py 按退避重试处理）；`data.tarkov.dev` 在本机连 DNS 都不解析。GitHub 与 fandom wiki 可访问。真实拉取若持续失败，换网络环境重跑，不要改脚本硬编码假数据。
10. **`render_svg()` 用 `<img>` 渲染，窗口比例和 SVG viewBox 不一致时会 letterbox**：画面被等比缩小居中，而 affine 假设画面铺满画布，于是所有标记整体偏移。所以 SVG 一律经 `svg_canvas()` 出图（按自身比例渲染 → 同一 `turn` 旋转 → 拉伸到目标尺寸），底图和楼层共用这条路径；不要再直接 `render_svg()` 到目标画布尺寸。
11. **冰破船的层必须用与底图相同的渲染身份**：`research/svg/icebreaker/*.svg` 共享同一 viewBox（`1000 1500 3000 5400`），每个文件里只有 `display:block` 的那个组是有效内容。必须按**与底图完全相同的目标尺寸与 `turn`** 渲染（`bake_svg_layer` 已如此），任何裁剪/二次缩放都会重新引入错位。
12. **`render_svg()` 的 `only_layer` 只允许隐藏顶层组**：这些 SVG 把每层作为根 `<svg>` 的直接子 `<g>`，层内还有 `Floor` / `Wall` / `Stairs` 等嵌套组。早期用 `^  <g` 缩进正则匹配顶层组，文件被压成单行后匹配数为 0，「隐藏其它层」静默失效、整份文档全被渲染；后来改成匹配任意 `<g id>` 又把**层内**的嵌套组一起隐藏，底图直接变成纯背景色。现在统一走 `top_level_group_ids()`（`ElementTree` 解析，只取根节点的直接子组）。新增地图的层过滤必须复用它，不要再用正则猜缩进。
13. **重建底图后必须目视/数值确认不是纯色**：`render_svg()` 只检查 PNG 大于 1 KB，空背景也能过。改完渲染路径先跑 `python tools/build_assets.py --only <key>`，用 PIL 数一下 distinct colors（正常底图上万，纯背景是 1），再跑 `check_map_coverage.py`。
14. **UI 自动化验证的三个连环坑**（`tools/capture_docs.py` 已把这些都绕开，改它或写新脚本时注意）：
    - 单实例互斥（`TarkovAtlas_SingleInstance`）：已有实例在跑时，新启动的进程只会激活旧窗口就退出——测到的行为是**旧那个进程**的。
    - 低权限 shell 给提权实例合成的鼠标事件会被 UIPI 静默吞掉（画面零像素变化 ≠ 功能坏了）；判据：`Get-Process TarkovAtlas` 的 `Path` 读不到即提权实例，先让用户手动退出再测。
    - 验证脚本要与 app 同 DPI 感知（`SetProcessDpiAwareness(2)`），否则 `mouse_event` 的绝对坐标被虚拟化，跟手率永远算不满。
15. **悬浮窗拖窗不用 DIP 增量**：`e.GetPosition` 是相对已移动的窗口的，负反馈导致窗口只跟到光标位移的 40–73%；`GetCursorPos`（物理像素，与 `Window.Position` 同系）+ 按下时抓取偏移才 100% 跟手。`BeginMoveDrag` 只认左键，右键无效。

两个大文件：`MainWindow.axaml.cs`（约 1.9k 行，所有 UI 交互：地图切换、楼层、图例、任务抽屉、日志监听）和 `Controls/MapCanvas.cs`（约 1.6k 行，全部绘制 + 指针手势）。改行为先在这两处找，不要去 Models 里猜。悬浮窗的手势在 `OverlayWindow.axaml.cs`（画布 `IsHitTestVisible=false`，事件由窗口层转发给 `MapCanvas.Begin/Move/EndHostPan`）。

运行 `tools/*.py` 前不要 `pip install` 到全局：脚本自己把 `tools/pyext`、`tools/pylibs` 插到 `sys.path` 前面，用的是仓库内的 Pillow/numpy/requests。缺依赖就装到 `tools/pylibs`（见 4.4 节命令）。

---

## 6. 数据出处（不要凭印象改）

- **Boss / 刷点**：上游同一个 `mob` 会按刷点变体重复出现，必须按 `mob` 合并（`chance` 取最大，`points` 0.1 m 去重取并集）。显示名见 `BOSS_ZH`：`exUsecFree/ExUsec → 游荡者`（**不是「流氓」**）、`PmcBot → 掠夺者`、`vsRF/vsRFSniper → 俄军/俄军狙击手`、`blackDivision/pmcBotBlackDiv/bossBullyBlackDiv → 黑色军团`、`sectantPriest → 邪教徒祭司`。不要出现 `PMC` / `Boss` 这类占位名。
- **出生点**：只保留 `categories` 含 `player` 的点。
- **Shturman**：唯一的人工筛选 `BOSS_ZONE_FILTER["woods"]["bossKojaniy"] = (10, -3, 250)`；上游 `ZoneWoodCutter` 混进了 USEC Camp / Scav Town 方向的点。**只删远点，不新增任何点。**
- **剧情**：中文标题/目标来自 `https://www.eftarkov.com/storyline/<id>` 的抓取（`research/story/zh-raw.json`），不是机器翻译；章节映射在 `tools/build_story.py`。
- **剧情坐标**：只对双重验证通过的地图输出 —— `customs`、`woods`、`shoreline`、`streets-of-tarkov`、`lighthouse`、`ground-zero`、`interchange`。其余地图只给「跳转到地图」，`hasCoordinates=false`。**宁可不标，也不编坐标。**
- **商人**：任务列表显示英文原名（`TraderInfo.Name`），中文名只用于其它展示。`QuestService.ResolveTraderNames()` 同时填 `TraderDisplay` 和 `TraderSlug`，不出现 24 位十六进制 id。
- **头像**：`Assets/bosses/`、`Assets/traders/`，来源 `https://www.eftarkov.com/uploads/{boss,trader}/<id>.webp`。`BossSpawn.AvatarSlug` 是唯一映射入口，`MapCanvas` 不得保留第二份表。
- **图例图标**：`Assets/legend/*.png`（48×48）。图标 stem 的唯一映射在 `MainWindow.axaml.cs`（`LootKindIcon` / `PoiKindIcon` + 各图例行的 `Icon =` 字面量）；`LoadLegendIcon` 找不到文件时回退成色点，所以新增图标类别必须同步补 PNG。图标美术来自本机工具箱资源（不进 git 的 `工具箱*/资源素材/用户自定义`），`hazard`/`label` 是 PIL 内置几何绘制（`.tmp/make_fallback_icons.py` 思路），别用外部素材覆盖这两个。
- **楼层**：`layers/index.json`。空 `image` 是合法的「仅底图」层——室外图（reserve / customs / shoreline / ground-zero / streets / icebreaker）**必须**有这个条目，否则默认选中第一层时整个室外底图会被压暗成 35%。楼层大部分来自 SVG 分组（`svgLayer`）；仅 reserve 2–5 层与 customs 4 层（上游无对应 SVG 组）仍走瓦片拼合分支（`bake_layer`/`stitch_tiles`，缓存在 `tools/tile-cache`）。
- **许可**：矢量图 CC BY-NC-SA 4.0（the-hideout/tarkov-dev-svg-maps），几何与任务 MIT（the-hideout/tarkov-dev）。与 Battlestate Games 无关，禁止商用，禁止作弊/雷达用途。

---

## 7. 已知缺陷（当前不接受回归）

- **海岸线楼层**：3 层已生成，内容区仍偏小，待重烘焙。
- 历史缺陷（破冰船空背景、实验室/迷宫硬拉伸）已修复，**不要再退回**：
  - 破冰船：已改为纯矢量管线，不再用上游栅格瓦片。成品 5600×5183，16 层全部由 `build_layers.py` 从 `research/svg/icebreaker` 渲染，与底图共用同一 viewBox 渲染身份，逐层对齐。旧的 `uniform_transform()` 1.75× 拉伸修正仍适用于其他瓦片地图。
  - 实验室 / 迷宫：实验室底图 = `First_Level` 组（5600×4082），两层楼（`Second Level` / `Technical`）走 `SVG_FLOOR_ALIASES` 映射到 SVG 组 `Second_Level` / `Technical_Level`——上游只给瓦片，那套瓦片是旧版照片风底图，叠在矢量底图上像两张地图；**迷宫必须走「瓦片栅格 + SVG warp」分支**（3602×3873，`TILE_MAPS`）：`Labyrinth.svg` 的 viewBox 比画面本身大，按 bounds 拉伸后每个标记都偏出房间（3/7 落在画面内），只有经锚点矩阵 warp 进瓦片栅格才对齐——但栅格由 `tile_grid()` 纯几何算出，不下载瓦片。
  - 验收口径：`python tools/check_map_coverage.py` 全 13 张退出码 0。有 `content` 的图按「content 与实测画面偏差 ≤ 3%」判，没有 `content` 的图仍按四边边距 < 10% + 标记落在画面内 ≥ 90% 判；背景色取**图片边框环**的众数（整图众数会把工厂判成误报），边框环本身不是深色底（码头的水面铺满画布）时按「画布已铺满」处理。`datacheck` 的 `knownGeometryDefects` 已清空，并新增「content 矩形必须落在画布内」断言；旋转仿射（reserve）跳过「bounds 角点必须在画面内」那条——那是轴对齐前提。

---

## 8. 改动规范

- 改数据先跑对应 `tools/build_*.py`，再跑 `tools/check.ps1`，三套全绿才提交。
- 冰破船的层和其它地图一样由 `build_layers.py` 生成；它的层源在 `ICE_FLOOR_FILES`（`research/svg/icebreaker`），改完直接重跑该脚本即可。
- 新增断言优先加到 `tools/datacheck`（数据层）或 `tools/logiccheck`（服务层），不要只靠肉眼。
- `MapCanvas` 是性能关键路径：新增绘制要素必须复用画笔/文本缓存，且不得在平移时触发整图重采样。
- 同一份映射只保留一处（例如头像 slug），避免两处表漂移。
- 提交前 `git status --short` 必须干净。
