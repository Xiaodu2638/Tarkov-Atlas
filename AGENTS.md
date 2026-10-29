# AGENTS.md

Tarkov Atlas —— 《逃离塔科夫》离线战术地图与任务助手（Windows / Avalonia / NativeAOT）。

本文件面向在本仓库工作的 agent。**动手前先读第 5 节的约束**，那里记录了几处会让构建或验证静默出错的坑。

---

## 1. 项目定位

桌面端离线地图工具。玩家在副屏或窗口模式下跑游戏时，用一块不遮挡游戏、可交互的高清地图查撤离点、Boss 刷点、危险区，并把「这一局该做什么任务」直接标在图上。

运行时**地图与任务数据全部离线**：底图、标记点、任务库打包进 `Assets/`。唯一的联网行为是任务详情里「任务地点图」按需抓取 `www.eftarkov.com` 的攻略图（`TaskGuideService`，只接受 https，失败只是没有图）。

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
```

产物：`dist/TarkovAtlas-win-x64/TarkovAtlas.exe`（约 21.6 MB 原生 exe，整个目录约 87 MB，需同目录的 `av_libglesv2.dll`、`libSkiaSharp.dll`、`libHarfBuzzSharp.dll` 与 `Assets/`、`Scripts/`）。脚本同时打一个同名 zip；`-NoAot` 跳过 AOT，`-OutputRoot <dir>` 换输出目录。

构建与自检会强制把 `TEMP`/`TMP`、`NUGET_PACKAGES`、`DOTNET_CLI_HOME` 指到仓库内（`.tmp/`、`.nuget-packages/`、`.dotnet-home/`），不写主机。**不要裸跑 `dotnet publish`**，会污染 `%TEMP%` 且可能锁住 `bin/`。上面「单独跑某套」的 `dotnet run` **不做**这套重定向（只有 `check.ps1` 会）：要么直接用 `check.ps1`，要么在同一个 shell 里先导出这几个变量。

---

## 3. 目录结构

```
TarkovAtlas/                    应用主体（C# / Avalonia）
  Program.cs                    入口（STAThread + Avalonia AppBuilder）
  App.axaml(.cs)                应用与样式
  AppJsonContext.cs             源生成的 JSON 上下文（反射已禁用，新类型必须登记）
  MainWindow.axaml(.cs)         主窗口：地图列表、任务抽屉、设置、日志监听
  OverlayWindow.axaml(.cs)      悬浮小窗（置顶、可拖动、滚轮缩放）
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
    TaskGuideService.cs         任务指引
  Assets/
    Maps/mapdata.json           运行时地图清单（唯一可信来源）
    Maps/*.jpg                  13 张底图（最长边 5600 px）
    Maps/layers/index.json      楼层图索引
    Maps/layers/<key>/*.png     楼层叠加图（与底图共用像素网格）
    Maps/hazards.json           雷区 / 狙击区 / 其它危险区多边形
    quests.json                 任务库（503 条）
    story.json                  剧情（10 章 / 254 目标）
    bosses/*.png                商人 / Boss 头像
tools/
  check.ps1                     三套自检入口
  datacheck/                    数据层断言（地图几何、标记、任务、楼层、hazard）
  logiccheck/                   服务层断言（QuestService、剧情、商人、跳转）
  shotcheck/                    截图解析与删除行为
  build_assets.py               下载瓦片 / 渲染 SVG → 生成底图 + maps.json
  build_layers.py               烘焙楼层 PNG + layers/index.json + hazards.json
  build_data.py                 合并 API → quests.json / mapdata.json
  build_story.py               剧情 → story.json
  check_map_coverage.py        底图内容区覆盖率自检（第 7 节验收标准用它）
```

`tools/*.py`、`tools/pyext/`、`tools/pylibs/`、`research/` 全部**不进 git**（见 `.gitignore`），只存在于开发机。`app` 的构建和运行都不碰 Python。

---

## 4. 架构要点

### 4.1 坐标与投影
- 每个 `MapDefinition` 带一个仿射矩阵：`px = a*x + b*z + c; py = d*x + e*z + f`，把游戏坐标映射到图片像素。
- `MapCanvas.Project()` 是唯一投影入口，所有标记都走它。
- `maps.json`（`build_assets.py` 产物）里的 affine **未经人工校正**，运行时以 `mapdata.json` 为准。两者不一致时先查原因，**不要直接覆盖**。
- **底图必须上北下南**：图片帧 = 游戏坐标按 `coordinateToCardinalRotation` 旋转后，`px ∝ east`、`py ∝ -north`；SVG 再按 `(cardinal − coordinateRotation) mod 360` 转正，等比缩放后拉伸进 cardinal bounds 矩形（与 tarkov.dev 的 `L.svgOverlay` 一致）。11 张 `rot=180` 的图是恒等变换，工厂是唯一真正被转过的：竖版 5183×5600，底图只渲染 `Ground_Floor` 组（整张 SVG 四层叠在一起会看着像重影）。
- **`content` 决定取景，不是 `bounds`**：`build_assets.py` 的 `content_box()` 用「边框环众数色 + 12 灰阶」找出实际画出来的区域，经 `box_to_game()` 反算成游戏坐标写进 `mapdata.json` 的 `content`；`MapCanvas.EnsureLayout()` 按它做 fit + 居中，所以画布边缘的空白不会把地图缩成一小块。`bounds` 仍是整张图片的世界范围（`datacheck` 用它做一致性断言），两者别混。
- **仿射必须各向同性**：世界单位是米、图片保持比例，所以 `|a| == |e|`。破冰船的上游 tile `transform` 是工厂的占位值（`sx=2.0, sy=3.5`），`uniform_transform()` 会在拼瓦片前统一成水平尺度；`sanitize_affine()` 在产物侧再兜一次底。数据里出现各向异性矩阵一律按 bug 处理。

### 4.2 渲染
- `MapCanvas.Render()` 每次整帧重绘。性能红线：
  - `DrawBitmapCropped()` 只把视口内的源矩形交给 Skia，禁止整图重采样。
  - 文本走 `_textCache`，画笔走 `_penCache`，菱形/多边形几何走 `_diamondCache`。
  - `Compact`（悬浮窗）跳过标签、比例尺、指北针。
  - 任务标记在 `ShowQuestMarkers=false` 时直接跳过生成与绘制。
- 楼层图与底图共用同一目标矩形，运行时直接叠加，不做单独缩放。

### 4.3 危险区
- 规则集中在 `HazardRules`（`Models/MapModels.cs`）。
- `minefield` / `sniper` 始终按多边形填充。
- 其它 generic `hazard` 若面积超过 `AreaLevelFraction = 0.02`（地图 bounds 的 2%），只画质心 `!` 标记，避免一块 50 m 级区域盖住整张图。
- 数据本身不做删改，只在展示层过滤。

### 4.4 数据管线（本机，不进 git）
- 顺序：`build_assets.py` → `build_layers.py` → `build_data.py` → `build_story.py`。
- 局部重建：`build_assets.py --only factory`、`build_layers.py --only factory`。其余地图的 manifest 条目和楼层索引会合并保留，不会清空。
- 两个 build 脚本都靠本机 headless Edge 栅格化 SVG；已下载瓦片在 `tools/tile-cache/`，瓦片类地图可离线重跑。
- 外部依赖：`tools/pyext`（Pillow）、`tools/pylibs`（numpy、requests）。重建：
  `python -m pip install --target tools/pylibs pillow numpy requests`
- 上游快照放在 `research/`：`api-maps.json`（tarkov.dev）、`svg-maps/tarkov-dev-svg-maps-main`（矢量图）、`orient-ref`（朝向参考）。

---

## 5. 约束与已知的坑

1. **一切留在工作区内。** 不写 `%APPDATA%`、`%TEMP%`、全局 git config、全局 pip。测试项目和非提权构建用 `TARKOVATLAS_DATA_DIR` 重定向应用状态（提权版见下一条）。
2. **管理员清单**：`app.manifest` 内嵌 `requireAdministrator`（全局热键需要）。副作用是**非交互 shell 里启动 exe 会卡在 UAC**；自动化验证请用非提权构建（`-p:ApplicationManifest=`），并按 PID 定位窗口后再截图。同时务必先清掉残留实例——否则截到的是旧窗口，而且哈希会完全一致。另外 UAC 提权**不继承父进程环境变量**，所以发布版 exe 永远读 `%APPDATA%\TarkovAtlas\settings.json`，`TARKOVATLAS_DATA_DIR` 对它无效；要用干净配置验证，先备份挪走那份 settings.json，验完还原。
3. **构建**必须设 `AVALONIA_TELEMETRY_OPTOUT=1`，否则 Avalonia 遥测写日志失败会让整个 build 以误导性的 MSB4018 崩掉。`build.ps1` / `tools/check.ps1` 已经设好，只有绕过脚本直接跑 `dotnet build|publish` 时才需要自己设。
4. **JSON 走源生成**（`JsonSerializerIsReflectionEnabledByDefault=false`）：新增要序列化/反序列化的类型必须加进 `AppJsonContext.cs` 的 `[JsonSerializable]` 列表，否则 NativeAOT 下要等到运行时才炸。
5. **`dotnet publish` 的输出目录被占用会锁住 `bin\Release\...`**；用暂存目录或 `-p:OutputPath=`。
6. **不要加 NuGet 外网依赖**：只能用 `.nuget-packages` 里的离线缓存。
7. **别 `git clean -xdf`**：会连 `research/` 和 `tools/*.py` 管线一起删掉。
8. **文件写入偶发 `file no longer exists — re-read`** 是沙箱常态，重读再写即可，不是逻辑错误。往 `.tmp/` 丢临时脚本时**别用标准库模块名**（`struct.py` 会盖住 stdlib 的 `struct`，因为 CWD 在 `sys.path` 里）。
9. **`assets.tarkov.dev` 返回 403**；GitHub 与 fandom wiki 可访问。
10. **`render_svg()` 用 `<img>` 渲染，窗口比例和 SVG viewBox 不一致时会 letterbox**：画面被等比缩小居中，而 affine 假设画面铺满画布，于是所有标记整体偏移。给某张图加 SVG 分支前，先对比 `viewBox` 的宽高比和 cardinal bounds 的宽高比；不一致就走瓦片分支。

两个大文件：`MainWindow.axaml.cs`（约 1.2k 行，所有 UI 交互：地图切换、楼层、任务抽屉、日志监听）和 `Controls/MapCanvas.cs`（约 1k 行，全部绘制）。改行为先在这两处找，不要去 Models 里猜。

---

## 6. 数据出处（不要凭印象改）

- **Boss / 刷点**：上游同一个 `mob` 会按刷点变体重复出现，必须按 `mob` 合并（`chance` 取最大，`points` 0.1 m 去重取并集）。显示名见 `BOSS_ZH`：`exUsecFree/ExUsec → 游荡者`（**不是「流氓」**）、`PmcBot → 掠夺者`、`vsRF/vsRFSniper → 俄军/俄军狙击手`、`blackDivision/pmcBotBlackDiv/bossBullyBlackDiv → 黑色军团`、`sectantPriest → 邪教徒祭司`。不要出现 `PMC` / `Boss` 这类占位名。
- **出生点**：只保留 `categories` 含 `player` 的点。
- **Shturman**：唯一的人工筛选 `BOSS_ZONE_FILTER["woods"]["bossKojaniy"] = (10, -3, 250)`；上游 `ZoneWoodCutter` 混进了 USEC Camp / Scav Town 方向的点。**只删远点，不新增任何点。**
- **剧情**：中文标题/目标来自 `https://www.eftarkov.com/storyline/<id>` 的抓取（`research/story_zh_raw.json`），不是机器翻译；章节映射在 `tools/build_story.py`。
- **剧情坐标**：只对双重验证通过的地图输出 —— `customs`、`woods`、`shoreline`、`streets-of-tarkov`、`lighthouse`、`ground-zero`、`interchange`。其余地图只给「跳转到地图」，`hasCoordinates=false`。**宁可不标，也不编坐标。**
- **商人**：任务列表显示英文原名（`TraderInfo.Name`），中文名只用于其它展示。`QuestService.ResolveTraderNames()` 同时填 `TraderDisplay` 和 `TraderSlug`，不出现 24 位十六进制 id。
- **头像**：`Assets/bosses/`、`Assets/traders/`，来源 `https://www.eftarkov.com/uploads/{boss,trader}/<id>.webp`。`BossSpawn.AvatarSlug` 是唯一映射入口，`MapCanvas` 不得保留第二份表。
- **楼层**：`layers/index.json`。空 `image` 是合法的「仅底图」层。部分楼层来自 SVG 分组（`svgLayer`）而非瓦片，修改 `build_layers.py` 时必须两种都支持。
- **许可**：矢量图 CC BY-NC-SA 4.0（the-hideout/tarkov-dev-svg-maps），几何与任务 MIT（the-hideout/tarkov-dev）。与 Battlestate Games 无关，禁止商用，禁止作弊/雷达用途。

---

## 7. 已知缺陷（当前不接受回归）

- **海岸线楼层**：3 层已生成，内容区仍偏小，待重烘焙。
- 历史缺陷（破冰船空背景、实验室/迷宫硬拉伸）已修复，**不要再退回**：
  - 破冰船：`uniform_transform()` 修掉 1.75× 纵向拉伸，crop 原点改按画布帧计算，成品 4585×4270，船体占满画布。
  - 实验室 / 迷宫：实验室改走 SVG 分支（cardinal 帧 + 等比缩放 + 按内容裁切），5600×4082；**迷宫必须走瓦片分支**（3602×3873，`TILE_MAPS`）。`Labyrinth.svg` 的 viewBox 比画面本身大，按 bounds 拉伸后每个标记都偏出房间，试过就知道（3/7 落在画面内）。
  - 验收口径：`python tools/check_map_coverage.py` 全 13 张退出码 0。有 `content` 的图按「content 与实测画面偏差 ≤ 3%」判，没有 `content` 的图仍按四边边距 < 10% + 标记落在画面内 ≥ 90% 判；背景色取**图片边框环**的众数（整图众数会把工厂判成误报）。`datacheck` 的 `knownGeometryDefects` 已清空，并新增「content 矩形必须落在画布内」断言。

---

## 8. 改动规范

- 改数据先跑对应 `tools/build_*.py`，再跑 `tools/check.ps1`，三套全绿才提交。
- 新增断言优先加到 `tools/datacheck`（数据层）或 `tools/logiccheck`（服务层），不要只靠肉眼。
- `MapCanvas` 是性能关键路径：新增绘制要素必须复用画笔/文本缓存，且不得在平移时触发整图重采样。
- 同一份映射只保留一处（例如头像 slug），避免两处表漂移。
- 提交前 `git status --short` 必须干净。
