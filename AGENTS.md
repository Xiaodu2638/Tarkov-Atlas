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

# 联网更新数据（重拉快照 -> 重放 Boss 搬迁修正 -> build_all -> 门禁；从不自动提交，失败自动回滚快照）
python tools/update_data.py            # 全流程
python tools/update_data.py --dry-run  # 只 diff 快照并回滚
python tools/update_data.py --selftest # 离线自测快照重塑/翻译展平
python tools/boss_relocate.py          # update_data 重拉后必须补跑一次（快照会冲掉修正，datacheck 随即报红）
```

改了代码要做任何运行时验证之前**先重跑 `build.ps1`**：验证脚本打的是 `dist/` 里的 exe，代码提交了但 dist 没重发，测到的就是旧版本（曾把「功能正常」误判成「左键拖拽没反应」）。

重发布 `dist` 前先确认没有 TarkovAtlas 实例在运行：提权实例会锁住 `TarkovAtlas.exe` / `av_libglesv2.dll` / zip，`build.ps1` 打包到一半才报 `PermissionDenied`。

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
    ScreenshotService.cs        截图定位：600ms 轮询目录 + 台账 diff（见 4.5）
    UiFx.cs                     微动效统一入口（150-220ms ease-out 淡入/宽度动画）
    GameLogParsing.cs           战局日志纯解析（无注册表/UI 依赖，logiccheck 直接链接）
    GameLogService.cs           游戏日志监听、地图自动识别（兼容 1.3 会话目录与旧扁平布局）
    HotkeyService.cs            全局热键（Win32 低级键盘钩子）
    OcrService.cs               截图 OCR
  Assets/
    maps/mapdata.json           运行时地图清单（唯一可信来源）
    maps/markers.json           运行时标记点（撤离/Boss/出生/转移/BTR/埋藏/物资/道具；管线拆分产物，mapdata 里不再有内联标记）
    maps/*.jpg                  13 张底图（最长边 5600 px）
    maps/layers/index.json      楼层图索引
    maps/layers/<key>/*.png     楼层叠加图（与底图共用像素网格）
    maps/hazards.json           雷区 / 狙击区 / 其它危险区多边形
    quests.json                 任务库（468 条）
    story.json                  剧情（10 章 / 254 目标）
    bosses/*.png                Boss 头像
    traders/*.png               商人头像
    legend/*.png                图例图标（48×48）
    guides/<questId>/*.webp     任务地点攻略图
  Scripts/ocr.ps1               内嵌资源（OCR 调用）
tools/
  check.ps1                     三套自检入口
  datacheck/                    数据层断言（地图几何、标记、任务、楼层、hazard）
  logiccheck/                   服务层断言（QuestService、剧情、商人、跳转、战局日志解析）
  shotcheck/                    截图解析、导入台账、轮询 watcher、latest-wins pin
  build_all.py                  管线入口（按依赖顺序跑完下面四步）
  build_assets.py               渲染 SVG（迷宫 warp 进解析栅格）→ 生成底图 + maps.json
  build_layers.py               烘焙楼层 PNG + layers/index.json + hazards.json
  build_data.py                 合并 API → mapdata.json / markers.json / quests.json
  build_story.py                剧情 → story.json
  update_data.py                联网更新入口：重拉 research/api 快照 -> build_all -> 门禁（可回滚）
  boss_relocate.py              快照滞后于游戏版本的 Boss 搬迁修正表（幂等；update_data 重拉后重跑，见第 6 节）
  check_map_coverage.py         底图内容区覆盖率自检（第 7 节验收标准用它）
  capture_docs.py               驱动 dist 产物重拍 docs/ 四张界面截图
  poi.json                      手工维护的 POI 表（迷宫解谜道具）
  btr.json                      手工维护的 BTR 站点表（灯塔上游 btrStops 为空；API 有数据的地图不覆盖）
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
- **房间级标记统一走 `MapDefinition.LootGate`**：容器点与物资埋藏点只在放大到门槛以上才渲染（图例里也都归在「物资」组）。门槛按图取值：室外图 4.0（400%），小图 2.0（200%）——世界面积 < `SmallMapArea = 100_000 m²` 即迷宫（12k）、破冰船（19k）、工厂（19k）、实验室（59k），再往上一张是地面零区（170k），所以分界不会含混。加新类别时别再写死 4.0，直接用 `LootGate`/`_map.LootGate`；`datacheck` 的「small maps use the 2x loot gate」会盯住这个分类不被 bounds 改动悄悄挪走。
- **图标尺寸用 `MarkerIcon(base, max, min)`**：纯 `sqrt(zoom)` 在整个 0.35x–32x 区间只覆盖约 1:1.6 的变化，加上较宽的下限后，概览缩放下撤离点/转移点/BTR 会比建筑还大。该helper按 `zoom^0.45` 缩放并保留原上限，概览时明显缩小、贴近时回到原来的大小。
- **悬浮窗图标再乘 `CompactShrink`**：Compact 画布把整张图塞进 ~390px 小窗，按主窗口地图区（~700px）调好的图标仍然偏大；helper 内部已按 `min(w,h)/700` 自动缩小（0.30–1.00），主窗口不受影响。新增标记类别直接复用 `MarkerIcon`。
- **悬浮窗与主图共用同一份标注数据**：`MainWindow.SyncQuestMarkers()` 把追踪任务标记同时推给 `MapView.QuestMarkers` 和 `_overlay.SetQuestMarkers(...)`；悬浮窗是懒创建的，`ShowOverlay` 里还要补推一次，否则先开图后开窗时标记是空的。新增任何「只在主图出现」的标记层时，记得一起推给悬浮窗（`OverlayWindow.BuildOptions` 的开关也要跟着补）。
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

### 4.5 战局识别与截图定位
- **截图文件名就是全部数据**：`2026-10-30[19-51]_x, y, z_qx, qy, qz, qw_游戏内秒.png`，坐标与朝向在文件**创建**那刻就完整；PNG 字节还在流式写入（几 MB、可停顿 >1 s）。所以定位**不要**依赖写入完成信号：`ScreenshotService` 用 600 ms 目录轮询 + `ScreenshotImportLedger` 对上次快照做 diff，只解析文件名。任何「等文件稳定」的做法都被证明会误判（半成品导入 → pin 跳；事件被吞 → 看着像没反应）。
- **台账身份 = 路径 + 创建时间**（`ScreenshotInfo.ImportToken`）：创建时间在写入全程不变，故一次截图恰好回调一次；同名重建（分钟精度 + 删除后复用）算新截图。FileTime 不能当键，它在写入期间一直变。
- **pin 语义是「我现在在哪」**：`ScreenshotService.RememberLatest` 每张地图只保留最新一个点，扫描时按时间从旧到新导入，留下的是最新的。
- **1.3.0.0 日志布局**：`Logs/log_<时间戳>_<版本>/` 每局一个子目录，application 通道是目录内 `<时间戳> application_NNN.log`（顶层不再有扁平 `application*.log`）。`GameLogParsing.FindApplicationLog` 同时认新旧两种布局；文件名里的通道名是**最后一个空格后**的 token。
- **1.3 的战局标记行**只剩 `scene preset path:... rcid:<id>.scenespreset.asset`（每次进战局都打，rcid 是 bundle 名如 `factory_day` / `Shopping_Mall`）与 `[Transit] ... Locations:<id> ->`；旧的 `Load location:` 已消失。新增 id 一律加进 `MapRepository.LocationToMap`，未知 rcid（大厅/菜单场景）必须跳过——这是唯一的防误触发手段。

### 4.6 生命周期与动效
- **× 是最小化到托盘，不是退出**：`ShutdownMode = OnExplicitShutdown`（App.axaml.cs），MainWindow.OnClosing 在非退出路径取消关闭并 `Hide()`；真正的退出只有托盘菜单「退出」（`ExitApplication` → 置 `_shuttingDown = true` → `desktop.Shutdown()`），它复用 OnClosing 的 teardown。托盘由 `InitTrayIcon` 在 OnOpened 里挂，失败静默降级。别把 ShutdownMode 改回去——主窗口一关进程就没了。
- **动效统一走 `Services/UiFx.cs`**：`FadeIn` / `FadeOutAndCollapse` / `AnimateWidth`，150–220ms ease-out。已应用：任务抽屉（滑出+淡入）、图例面板、toast、地图切换画布淡入、悬浮窗开关、标题栏三键（App.axaml 里 CaptionButtons 的 BrushTransition）。新增过渡复用它，不要在每个 call site 手写 Animation；也别把时长调长——超过 ~250ms 就会显得拖沓。

---

## 5. 约束与已知的坑

1. **一切留在工作区内。** 不写 `%APPDATA%`、`%TEMP%`、全局 git config、全局 pip。测试项目和非提权构建用 `TARKOVATLAS_DATA_DIR` 重定向应用状态（提权版见下一条）。
2. **管理员清单**：`app.manifest` 内嵌 `requireAdministrator`（全局热键需要）。副作用是**非交互 shell 里启动 exe 会卡在 UAC**；自动化验证请用非提权构建，并按 PID 定位窗口后再截图。非提权验证构建（在同一 shell 里先做好第 2 节的环境变量重定向）：`dotnet publish TarkovAtlas\TarkovAtlas.csproj -c Release -p:ApplicationManifest= -p:PublishAot=false -o .tmp/verify/app`——注意 `--no-incremental` 不是 publish 的合法开关（MSB1001），manifest 改动要强制重建得先删 `TarkovAtlas/obj`（apphost 会增量缓存旧 manifest）。同时务必先清掉残留实例——否则截到的是旧窗口，而且哈希会完全一致。另外 UAC 提权**不继承父进程环境变量**，所以发布版 exe 永远读 `%APPDATA%\TarkovAtlas\settings.json`，`TARKOVATLAS_DATA_DIR` 对它无效；要用干净配置验证，先备份挪走那份 settings.json，验完还原。
3. **构建**必须设 `AVALONIA_TELEMETRY_OPTOUT=1`，否则 Avalonia 遥测写日志失败会让整个 build 以误导性的 MSB4018 崩掉。`build.ps1` / `tools/check.ps1` 已经设好，只有绕过脚本直接跑 `dotnet build|publish` 时才需要自己设。
4. **JSON 走源生成**（`JsonSerializerIsReflectionEnabledByDefault=false`）：新增要序列化/反序列化的类型必须加进 `AppJsonContext.cs` 的 `[JsonSerializable]` 列表，否则 NativeAOT 下要等到运行时才炸。
5. **`dotnet publish` 的输出目录被占用会锁住 `bin\Release\...`**；用暂存目录或 `-p:OutputPath=`。
6. **不要加 NuGet 外网依赖**：只能用 `.nuget-packages` 里的离线缓存。
7. **别 `git clean -xdf`**：会连 `research/` 的矢量源一起删掉（`tools/pyext`、`tools/pylibs`、`tools/tile-cache` 也会没）。
8. **文件写入偶发 `file no longer exists — re-read`** 是沙箱常态，重读再写即可，不是逻辑错误。往 `.tmp/` 丢临时脚本时**别用标准库模块名**（`struct.py` 会盖住 stdlib 的 `struct`，因为 CWD 在 `sys.path` 里）。
9. **上游端点现状（实测）**：`assets.tarkov.dev` 403；`api.tarkov.dev/graphql` 可用但间歇 422 "GraphQL server unavailable"（可连发多次，重试也未必好；update_data.py 按退避重试处理）——**请求必须带浏览器 UA + Content-Type/Accept，否则 Cloudflare 直接 403**；`data.tarkov.dev` 在本机连 DNS 都不解析，tarkov-api 的 GitHub 仓库也不随仓分发数据 JSON。GitHub 与 fandom wiki、eftarkov.com 可访问。真实拉取若持续失败，换网络环境重跑，不要改脚本硬编码假数据。
10. **`render_svg()` 用 `<img>` 渲染，窗口比例和 SVG viewBox 不一致时会 letterbox**：画面被等比缩小居中，而 affine 假设画面铺满画布，于是所有标记整体偏移。所以 SVG 一律经 `svg_canvas()` 出图（按自身比例渲染 → 同一 `turn` 旋转 → 拉伸到目标尺寸），底图和楼层共用这条路径；不要再直接 `render_svg()` 到目标画布尺寸。
11. **冰破船的层必须用与底图相同的渲染身份**：`research/svg/icebreaker/*.svg` 共享同一 viewBox（`1000 1500 3000 5400`），每个文件里只有 `display:block` 的那个组是有效内容。必须按**与底图完全相同的目标尺寸与 `turn`** 渲染（`bake_svg_layer` 已如此），任何裁剪/二次缩放都会重新引入错位。
12. **`render_svg()` 的 `only_layer` 只允许隐藏顶层组**：这些 SVG 把每层作为根 `<svg>` 的直接子 `<g>`，层内还有 `Floor` / `Wall` / `Stairs` 等嵌套组。早期用 `^  <g` 缩进正则匹配顶层组，文件被压成单行后匹配数为 0，「隐藏其它层」静默失效、整份文档全被渲染；后来改成匹配任意 `<g id>` 又把**层内**的嵌套组一起隐藏，底图直接变成纯背景色。现在统一走 `top_level_group_ids()`（`ElementTree` 解析，只取根节点的直接子组）。新增地图的层过滤必须复用它，不要再用正则猜缩进。
13. **重建底图后必须目视/数值确认不是纯色**：`render_svg()` 只检查 PNG 大于 1 KB，空背景也能过。改完渲染路径先跑 `python tools/build_assets.py --only <key>`，用 PIL 数一下 distinct colors（正常底图上万，纯背景是 1），再跑 `check_map_coverage.py`。
14. **UI 自动化验证的三个连环坑**（`tools/capture_docs.py` 已把这些都绕开，改它或写新脚本时注意）：
    - 单实例互斥（`TarkovAtlas_SingleInstance`）：已有实例在跑时，新启动的进程只会激活旧窗口就退出——测到的行为是**旧那个进程**的。
    - 低权限 shell 给提权实例合成的鼠标事件会被 UIPI 静默吞掉（画面零像素变化 ≠ 功能坏了）；判据：`Get-Process TarkovAtlas` 的 `Path` 读不到即提权实例，先让用户手动退出再测。
    - 验证脚本要与 app 同 DPI 感知（`SetProcessDpiAwareness(2)`），否则 `mouse_event` 的绝对坐标被虚拟化，跟手率永远算不满。
    - UIA 细节：CheckBox 要点 `TogglePattern`（对它 `InvokePattern` 会抛 Unsupported Pattern）；按 `BoundingRectangle` 坐标点击前先 `SetForegroundWindow`，否则事件落到别的窗口；窗口矩形用 `DwmGetWindowAttribute(EXTENDED_FRAME_BOUNDS)`。
15. **悬浮窗拖窗不用 DIP 增量**：`e.GetPosition` 是相对已移动的窗口的，负反馈导致窗口只跟到光标位移的 40–73%；`GetCursorPos`（物理像素，与 `Window.Position` 同系）+ 按下时抓取偏移才 100% 跟手。`BeginMoveDrag` 只认左键，右键无效。
16. **数据管线缩进陷阱**：`build_data.py` 里曾有一段 12 空格的代码被 8 空格的 `for` 头吞成循环体，12 张地图的 loot/caches 静默清零而三套自检照样全绿（当时没有非空断言）。改完 `build_*.py` 重跑后，必须 diff 产物：`git diff` 之外，用 Python 按 `maps.<key>.<field>` 逐字段对比 HEAD 版本，确认**只有预期字段变化**。datacheck 现已有 BTR/loot/caches 逐图非空断言兜底，但只覆盖已知类别。
17. **推送 / 发布的网络路径**：本机 `github.com:443` 直连不通（`api.github.com` 通）。git push 走 v2rayN 混合端口的 HTTP 代理 `-c http.proxy=http://127.0.0.1:10808`；`socks5h://` scheme 会 Connection reset，别用。代理没开时 push 秒败，先探端口再推。
18. **历史已用 git filter-repo 清理过**（第三方工具箱、含个人路径的 HANDOVER.md），`.git` 约 474 MB。绝不能再把 `工具箱*/` 或任何本机路径提交进来；`.gitignore` 已挡住，改 ignore 规则时留意。

两个大文件：`MainWindow.axaml.cs`（约 2k 行，所有 UI 交互：地图切换、楼层、图例、任务抽屉、日志监听）和 `Controls/MapCanvas.cs`（约 1.6k 行，全部绘制 + 指针手势）。改行为先在这两处找，不要去 Models 里猜。悬浮窗的手势在 `OverlayWindow.axaml.cs`（画布 `IsHitTestVisible=false`，事件由窗口层转发给 `MapCanvas.Begin/Move/EndHostPan`）。

运行 `tools/*.py` 前不要 `pip install` 到全局：脚本自己把 `tools/pyext`、`tools/pylibs` 插到 `sys.path` 前面，用的是仓库内的 Pillow/numpy/requests。缺依赖就装到 `tools/pylibs`（见 4.4 节命令）。

---

## 6. 数据出处（不要凭印象改）

- **Boss / 刷点**：上游同一个 `mob` 会按刷点变体重复出现，必须按 `mob` 合并（`chance` 取最大，`points` 0.1 m 去重取并集）。显示名见 `BOSS_ZH`：`exUsecFree/ExUsec → 游荡者`（**不是「流氓」**）、`PmcBot → 掠夺者`、`vsRF/vsRFSniper → 俄军/俄军狙击手`、`blackDivision/pmcBotBlackDiv/bossBullyBlackDiv → 黑色军团`、`sectantPriest → 邪教徒祭司`。不要出现 `PMC` / `Boss` 这类占位名。
- **Boss 搬迁 / 钉位修正（`tools/boss_relocate.py`）**：tarkov.dev 快照滞后于游戏版本，该脚本在重拉后把快照修回正确状态，三张表各管一类：`RELOCATIONS`（换图：`bossGluhar` lighthouse → reserve，6 个点取 eftarkov 储备站标记，14 锚点拟合、残差中位 6.7 m，落点与白车/K楼群/白马/停机坪/K4地堡/白后吻合）；`POSITION_FIXES`（同图钉位：`bossZryachiy` 上游 `Zone_Island` positions 为空数组、build_data 会整条丢弃，按 eftarkov 灯塔页标记钉到「灯塔新区」半岛 (346.3, 471.9)，21 锚点、LOO 中位 9.7 m）；`REMOVALS`（整条删除：`bossKnight` 三狗——豪宅自 Rogue 重做后由游荡者驻守，上游还把 Zone_Chalet 的一点画在新区半岛）。三张表幂等，`--check` 只报告。**`update_data.py` 重拉后必须跑 `boss_relocate.py` 再 `build_data.py`**（datacheck 随即报红兜底）。灯塔现行口径：**新区 = Zryachiy、豪宅 = 游荡者（Rogue）、无三狗、Glukhar 在储备站**——datacheck 有对应断言。上游修好后删表内条目、退回纯上游数据。
- **出生点**：只保留 `categories` 含 `player` 的点。
- **BTR 站点**：森林 8 / 街区 6 取上游 `btrStops`（站名查 `maps.zh` 的 `Trading/Dialog/PlayerTaxi/...` 键）；灯塔上游为空，用人工表 `tools/btr.json`（10-29 交互地图标定）。人工表只在该图 API 无数据处补位。共 22 站，图例行按数据存在与否显示，数据丢了整行就没了。
- **Shturman**：唯一的人工筛选 `BOSS_ZONE_FILTER["woods"]["bossKojaniy"] = (10, -3, 250)`；上游 `ZoneWoodCutter` 混进了 USEC Camp / Scav Town 方向的点。**只删远点，不新增任何点。**
- **剧情**：中文标题/目标来自 `https://www.eftarkov.com/storyline/<id>` 的抓取（`research/story/zh-raw.json`），不是机器翻译；章节映射在 `tools/build_story.py`。
- **剧情勾选**：`剧情任务` 页有**章级**和**目标级**两套勾选框，都写同一份进度（`storyDone` = 章 id，`storyObjectiveDone` = eftarkov objective id，后者由 `build_story.py` 从 `zh-raw.json` 带进 `story.json`）。规则：勾章 = 全选该章所有子目标；取消章 = 全部清空；子目标全勾 → 章自动变完成，取消任意一个 → 章自动回到未完成。视觉上两者一致（整行淡化 45%/50% + 删除线），`Border.story.done` / `Border.story-obj.done`。旧的只有 `storyDone` 的进度文件会在加载时把整章子目标点亮（一次性迁移）。「隐藏已完成」同时过滤章（`StoryView`）和子目标（`StoryObjective.IsVisible`），`StoryRows` 与进度计数永远是全量。**别把它当任务完成状态**，它不参与地图/任务/悬浮窗的任何逻辑。
- **剧情坐标**：`research/story/coords.json` 是唯一来源（69 条，`datacheck` 断言「有地图就必须有坐标」，少一条就红）。像素 -> 游戏坐标的分辨率按图不同，**不要用固定比例换算**（直接按 `data-map-width/height` 换算的已知点误差中位 43 m、最大 83 m）。正确做法是逐图拟合，锚点来自：① 已人工验证的点；② 站点「地图标注」与本图 `mapdata.labels` 同名；③ 站点「任务地点」（带 `task_id`）与 `quests.json` 同任务唯一 zone。锚点 ≥ 4 用通用仿射（工厂/实验室的站点图相对游戏坐标是旋转的，轴对齐模型会把残差拉到 40 m+），只有 3 个点时用反射相似。迷宫可用本图 5 个出生点与站点 7 个 `PMC` 标记做形状配准（rms 0.8 m），破冰船用「和平原子」的 3 个任务点配准（rms 0.3 m）。全部过留一法，残差 > ~10 m 的图不输出。**宁可不标，也不编坐标。**
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
- **发布到 GitHub**（remote 未配置；`.tmp/gh_push.py` / `gh_tag_move.py` / `gh_release.py` 在 gitignored 的 `.tmp/`，fresh clone 需按此说明重写）：`build.ps1` → 确认无运行实例 → `python .tmp/gh_push.py`（token 从 Windows 凭据管理器条目 `GitHub for Visual Studio - https://Xiaodu2638@github.com/` 读取、UTF-16 解码，不落盘不打印；master→main，走第 17 条的 HTTP 代理）→ `python .tmp/gh_tag_move.py`（PATCH 移动 `v1.0.0` tag，幂等）→ `python .tmp/gh_release.py` 上传 `dist/TarkovAtlas-win-x64.zip`（脚本幂等：同名资产先删再传）。**`gh_release.py` 必须用后台方式跑**：232 MB 走代理可超过任何前台超时，被中途杀掉会留下「旧资产已删、新资产没传」的空档（Release 一度 0 资产）；传完用 API 的 `assets[].digest` 与本地 `sha256` 对账。仓库：`github.com/Xiaodu2638/Tarkov-Atlas`。
- **官方已下线任务的剔除**：tarkov.dev 快照滞后于游戏版本，判断以 eftarkov.com 为准——`/task?q=<名>` 返回 0 且详情页被退回列表页 = 已下线（对照一个已知存在的任务 id 验行为）。确认后直接改快照 `research/api/tasks.json` + `tasks.zh.json`（删任务对象与其专属 zh 键，共享键保留）→ `build_data.py` 重建 → 核对 `git grep <id>` 无残留。
