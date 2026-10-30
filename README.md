# Tarkov Atlas

《逃离塔科夫》离线战术地图与任务助手 —— Windows 桌面端，Avalonia + NativeAOT，运行时**完全离线**。

玩家在副屏或窗口模式跑游戏时，用一块不遮挡游戏、可交互的高清地图查撤离点、Boss 刷点、危险区，并把「这一局该做什么任务」直接标在图上。

## 功能

- **13 张高清地图**：全部矢量底图，最长边 5600 px
- **多层楼层**：工厂、实验室、立交桥、储备站、破冰船等支持逐层切换
- **标记分类**：撤离点（PMC / Scav / 共享）、Boss 刷点与巡逻范围、出生点、转移点、资源埋藏点、物资容器、解谜道具
- **任务追踪**：503 条任务，勾选后在图上显示商人头像与目标点；支持多地点任务跳转
- **危险区**：雷区、狙击区、警报区多边形
- **悬浮小窗**：置顶、可拖动、滚轮缩放，可跟随玩家位置

## 构建

需要 Windows 10/11、.NET 10 SDK，以及本机已安装 Microsoft Edge（构建期用于栅格化 SVG）。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ./build.ps1
```

产物：`dist\TarkovAtlas-win-x64\TarkovAtlas.exe` 及同目录依赖 DLL 与 `Assets\`。

`-NoAot` 跳过 NativeAOT（编译更快，产物更大）；`-OutputRoot <dir>` 换输出目录。

## 自检

提交前必须三套全绿：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ./tools/check.ps1
```

单独跑：

```powershell
dotnet run --project tools/datacheck  -c Release
dotnet run --project tools/logiccheck -c Release
dotnet run --project tools/shotcheck  -c Release
```

- `datacheck`：地图几何一致性、标记落在画面内、楼层网格对齐、任务数据完整性
- `logiccheck`：`QuestService`、剧情、商人解析、跳转逻辑
- `shotcheck`：截图文件名解析与删除行为

## 数据管线

管线脚本在 `tools/`，产物写入 `TarkovAtlas/Assets/`。按顺序执行：

```powershell
python tools/build_assets.py      # 底图 + maps.json（需要本机 Edge）
python tools/build_layers.py      # 楼层图 + layers/index.json + hazards.json
python tools/build_data.py        # 任务与商人数据
python tools/build_story.py       # story.json
```

或一次跑完整条：`python tools/build_all.py`。

局部重建：`python tools/build_assets.py --only factory`、`python tools/build_layers.py --only factory`。

### 依赖

```powershell
python -m pip install --target tools/pylibs pillow numpy requests
```

`tools/pyext`（Pillow）与 `tools/pylibs` 都不进 git。

### 破冰船

破冰船在主管线内处理，但层图来源独立于上游瓦片：

- 14 个单层 SVG 放在 `research/ice-floors-svg/`，共享同一 viewBox（`1000 1500 3000 5400`）
- `build_layers.py` 通过 `ICE_FLOOR_FILES` 把上游层名映射到这些 SVG，逐层走 `bake_slayer`（与底图同一渲染尺寸与 `turn`，因此天然对齐）
- 因此 **不要** 在 `build_layers.py` 里为 `icebreaker` 加 `continue` 或另建入口脚本

## 数据来源与许可

- 地图几何与任务数据：`the-hideout/tarkov-dev`（MIT）
- 矢量地图：`the-hideout/tarkov-dev-svg-maps`（CC BY-NC-SA 4.0）
- 商人 / Boss 头像：`https://www.eftarkov.com/uploads/{boss,trader}/<id>.webp`
- 任务地点攻略图：按需抓取 `www.eftarkov.com`（仅 https）

与 Battlestate Games 无关。禁止商用，禁止用于作弊或雷达类工具。

## 目录结构

```
TarkovAtlas/           应用主体（C# / Avalonia）
  Program.cs           入口
  AppJsonContext.cs    源生成 JSON 上下文（新增类型必须登记）
  MainWindow.axaml(.cs) 主窗口与全部 UI 交互
  Controls/MapCanvas.cs 自绘地图控件（性能关键路径）
  Models/              地图、任务、设置模型
  Services/            数据加载、监听、OCR、热键
  Assets/              Maps/（底图、楼层、hazard）、quests.json、story.json、legend/
tools/                 数据管线（Python）与三套自检（C#）
research/              上游快照与原始素材
```
