# Tarkov Atlas · 逃离塔科夫离线地图与任务助手

用 **C# + Avalonia** 编写的 Windows 桌面地图工具，以 **.NET NativeAOT** 发布，生成的
`TarkovAtlas.exe` 不依赖目标电脑安装 .NET 运行时。

玩家在副屏或窗口模式跑游戏时，用一块不遮挡游戏、可交互的高清地图查撤离点、Boss 刷点、
危险区，并把「这一局该做什么任务」直接标在图上。**运行时地图与任务数据全部离线**：
底图、标记点、任务库、剧情、攻略图都打包在 `Assets/` 里。唯一的联网行为是任务详情里
「任务地点图」按需抓取 `www.eftarkov.com` 的攻略图，失败只是没有图。

![主界面](docs/main.png)

## 功能

### 地图

- **13 张当前战局地图**，采用游戏内官方中文名：中心区、海关、工厂、森林、立交桥、
  海岸线、储备站、实验室、灯塔、塔科夫街区、迷宫、码头、破冰船。
- **朝向与官方一致**：底图按 tarkov.dev 官方呈现方向输出（上北下南），不存在南北颠倒。
- **离线高清底图**：矢量 SVG 在构建阶段烘焙成本地 JPG（最长边 5600 px），仅迷宫
  与个别楼层（上游无矢量组）取自社区瓦片，运行时不请求任何在线资源。
- **浏览**：左侧列表 + 搜索；滚轮缩放、左键拖拽、双击放大、`0`/`Home` 复位、
  比例尺与指北针。
- **中文地名**：地标名已本地化（红厂房、宿舍、伐木场、疗养院……），商店品牌名保留原文。
- **按层显示**：地图上方下拉框可手动切层；开启「按高度自动切层」后，截图坐标的 y 值
  落到哪一层就自动切到哪一层。楼层图在构建阶段已 warp 到与底图完全相同的像素栅格，
  运行时只套同一个目标矩形，不会错位。目前 9 张地图共 52 个层条目：海关 5、工厂 4、
  储备站 6、中心区 4、立交桥 3、海岸线 4、实验室 3、塔科夫街区 6、破冰船 17。
- **危险区**：雷区（红）/ 狙击区（橙）/ 其它危险区（紫）以半透明多边形叠加。

### 显示内容（图例面板）

图例每一项都可独立开关，并带 48×48 图标；撤离点还带互斥子筛选。

| 项目 | 说明 |
| --- | --- |
| 撤离点 | 开关；并可筛选 **全部 / 仅 PMC·共享 / 仅 Scav·共享 / 仅共享** |
| Boss 刷点 | 各 Boss 的刷新位置、概率与头像 |
| 出生点 | PMC / Scav 出生位置（默认关闭，避免拥挤） |
| 转移点 | 地图间转移点（默认关闭） |
| 任务地点标记 | 已勾选「追踪」的任务目标点 |
| 物资点 | 各类容器点位，**放大到 400%（4x）及以上才渲染** |
| 埋藏点 | 地下埋藏物资 |
| 危险区 | 雷区 / 狙击区 / 其它危险多边形 |
| 地名 | 地标文字 |

设置会立即保存。

### 悬浮地图

- **无边框正方形小窗**，默认停靠屏幕**右侧中间**（多显示器会记在离开时的那块屏上），
  **中心始终跟随你最新的截图坐标**。
- 尺寸在设置里调，`Ctrl+Alt+M` 显示/隐藏、`Ctrl+Alt+L` 锁定鼠标穿透。
- 透明度与缩放比例可在设置中调整；鼠标右键拖拽移动窗口，位置即时保存。

### 任务

- **离线任务库 503 条**（tarkov.dev JSON API，2026-10 快照），任务名已官方中文化。
- 选中某张地图时，右侧**只列出该地图可做的任务**；一旦在搜索框输入，就自动放开为
  **全库搜索**。
- 勾选「追踪」后，该任务的目标点会以**派发任务的商人头像**为图标画在地图上。
- 每个有坐标的任务带「跳转到地图 ▸」按钮：单地图任务直接切图并对准目标；多地图任务
  （非击杀类）会弹出地图选择菜单。
- 「标记完成 / 取消」、全部 / 未完成 / 已完成筛选。
- 任务识别：**任务页面截图 OCR**（Windows 自带离线 OCR + 模糊匹配）。
- 「打开 Wiki」指向 `https://www.eftarkov.com/task/{id}`；「任务地点图」在应用内的图片
  查看器中显示攻略页里的标注图（滚轮缩放、拖拽平移）。

### 截图定位

- 监听 `Documents\Escape from Tarkov\Screenshots`，从文件名解析 `x, y, z` 与四元数朝向。
- 在地图上画出带编号的定位点与朝向箭头，点击可看文件名、时间、坐标。
- **可选清理**：默认关闭；打开后坐标写入完就把截图**移到回收站**
  （`SHFileOperation` + `FOF_ALLOWUNDO`，不是永久删除）。

### 权限

- 可执行文件嵌入 `requireAdministrator`，启动即请求管理员权限 —— 这样在游戏占据前台时，
  全局快捷键依然能触发。

## 系统要求

- Windows 10 1809 / Windows 11，x64。
- 运行发布版**不需要** .NET；只需系统 VC++ 运行库（Windows 10/11 自带）。
- 启动会弹 UAC 提权提示（为了全局热键）。
- OCR 需要 Windows OCR 语言包；英文系统默认可用。

## 直接运行

```
TarkovAtlas.exe
av_libglesv2.dll
libHarfBuzzSharp.dll
libSkiaSharp.dll
Assets/
  maps/         13 张地图 JPG + mapdata.json + layers/ + hazards.json
  bosses/       Boss 头像
  traders/      商人头像（任务标记用）
  legend/       图例图标
  guides/       任务地点攻略图
  quests.json   离线任务库
  story.json    剧情
LICENSE  THIRD-PARTY-NOTICES.md
```

解压后双击 `TarkovAtlas.exe`。设置与进度写入 `%APPDATA%\TarkovAtlas\`。

## 从源码构建（NativeAOT）

需要 .NET 10 SDK + Visual Studio 生成工具（“使用 C++ 的桌面开发”）。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ./build.ps1
```

`-NoAot` 跳过 NativeAOT（编译更快、产物更大）；`-OutputRoot <dir>` 换输出目录。
脚本优先尝试 NativeAOT，失败自动回退自包含发布，产物收拢到 `dist\TarkovAtlas-win-x64\`
并打包同名 zip。

> 不要裸跑 `dotnet publish`：`build.ps1` 会把 `TEMP`/`NUGET_PACKAGES`/`DOTNET_CLI_HOME`
> 指到仓库内，避免污染主机缓存。

## 自检

提交前必须三套全绿：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ./tools/check.ps1
```

单独跑（需先按 `check.ps1` 的方式导出 `TEMP`/`NUGET_PACKAGES`/`DOTNET_CLI_HOME`）：

```powershell
dotnet run --project tools/datacheck  -c Release
dotnet run --project tools/logiccheck -c Release
dotnet run --project tools/shotcheck  -c Release
```

- `datacheck`：地图几何/投影/标记/楼层/hazard/本地化一致性
- `logiccheck`：任务筛选、追踪、商人头像与跳转规则
- `shotcheck`：截图文件名解析与删除行为

底图本身的覆盖率另有：`python tools/check_map_coverage.py`（13 张必须退出码 0）。

## 数据管线

管线脚本在 `tools/`，输入在 `research/`，产物写入 `TarkovAtlas/Assets/`。

```powershell
python tools/build_all.py                  # 一次跑完整条
python tools/build_all.py --only factory   # 只重建一张地图的底图与楼层
python tools/build_all.py --skip-quests    # 只重建美术
```

分步执行时按依赖顺序：

```powershell
python tools/build_assets.py   # 底图 + maps.json 清单（需要本机 Edge/Chrome）
python tools/build_layers.py   # 楼层 PNG + layers/index.json + hazards.json
python tools/build_data.py     # mapdata.json + quests.json + 商人头像
python tools/build_story.py    # story.json
```

### 输入目录

```
research/
  api/          tarkov.dev API 快照（maps / tasks / traders，含中文表）
  upstream/     上游 data/maps.json（楼层与仿射来源）
  story/        剧情中文抓取 + 坐标标定表
  svg/<地图 key>/  矢量源，一张地图一个子目录（破冰船为 14 个单层 SVG）
```

### 依赖

```powershell
python -m pip install --target tools/pylibs pillow numpy requests
```

脚本自己把 `tools/pyext`、`tools/pylibs` 插到 `sys.path` 前面，用的是仓库内的
Pillow/numpy/requests；这两个目录不进 git。栅格化 SVG 需要本机 Edge 或 Chrome，
也可用 `EDGE_PATH` 环境变量指定可执行文件。

### 破冰船

破冰船的层与其他地图走同一条管线，但层源是 `research/svg/icebreaker/` 下的 14 个单层
SVG（共享 viewBox `1000 1500 3000 5400`）：`build_layers.py` 通过 `ICE_FLOOR_FILES`
把上游层名映射到这些文件，按与底图完全相同的目标尺寸与 `turn` 渲染，因此天然对齐。
不要为它另建入口脚本，也不要在 `build_layers.py` 里跳过它。

## 数据来源与许可

详见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

- 矢量地图：`the-hideout/tarkov-dev-svg-maps`（**CC BY-NC-SA 4.0**）
- 冰破船矢量图：`www.eftarkov.com`
- 地图几何、瓦片、中文名、撤离点、Boss、任务：`the-hideout/tarkov-dev`（MIT）
- 剧情中文：`www.eftarkov.com/storyline`
- UI：Avalonia、SkiaSharp、HarfBuzzSharp

> 与 Battlestate Games 无关；Escape from Tarkov 及相关素材归其权利人所有。
> **请勿商用，也请勿用于作弊、雷达或破坏公平性的用途。**

## 已知限制

- 任务库是离线快照，新版本任务需要重跑数据脚本。
- 底图为主楼层静态图，不随截图 `y` 值切换楼层（坐标会显示高度）。
- 破冰船 16 层的层名沿用上游英文。
- 截图文件名不含地图 ID；边界重叠时依赖日志或当前选中地图判断。
- OCR 识别率受截图分辨率与语言包影响。
- 海岸线楼层内容区偏小，待重新烘焙。
- `docs/` 中的界面截图来自旧版本，尚未按最近 UI 变更重新截取。

## 项目结构

```
TarkovAtlas/                 应用主体（C# / Avalonia）
  Program.cs / App.axaml     启动、主题、PerMonitorV2 DPI
  app.manifest               管理员权限 + DPI 感知
  AppJsonContext.cs          源生成 JSON 上下文（新类型必须登记）
  MainWindow.axaml(.cs)      地图列表、图例、任务抽屉、日志监听
  OverlayWindow.axaml(.cs)   正方形跟随式悬浮窗
  SettingsWindow.axaml(.cs)  设置
  QuestScanWindow.cs         OCR 候选
  MapPickWindow.cs           多地图任务选择
  ImageViewerWindow.cs       应用内图片查看器（任务地点图）
  Controls/MapCanvas.cs      全部绘制（性能关键路径）
  Models/                    地图、任务、设置、截图
  Services/                  数据加载、日志监听、截图、OCR、热键、持久化
  Assets/maps/               底图 + mapdata.json + layers/ + hazards.json
  Assets/{bosses,traders,legend,guides}/
  Assets/quests.json         本地化任务库
  Assets/story.json          剧情
tools/                       数据管线（Python）+ 三套自检（C#）
  build_all.py               管线入口
  build_assets.py            渲染 SVG（迷宫拼接瓦片）→ 底图 + maps.json
  build_layers.py            楼层 PNG + layers/index.json + hazards.json
  build_data.py              mapdata.json + quests.json + 商人头像
  build_story.py             剧情
  check_map_coverage.py      底图内容区覆盖率自检
  check.ps1 / datacheck / logiccheck / shotcheck
research/                    上游快照与矢量源（管线输入）
docs/                        界面截图
```
