# 第三方素材与数据声明

Tarkov Atlas 是玩家社区的非官方、非商业工具，与 Battlestate Games Limited
无隶属、背书或赞助关系。《逃离塔科夫》(Escape from Tarkov) 及其地图、名称与
游戏素材的版权归各自权利人所有。

本仓库的**代码**采用 MIT 许可（见 `LICENSE`）。下列素材与数据来自第三方，
遵循各自许可。

## 地图美术

### tarkov-dev-svg-maps

- 项目：https://github.com/the-hideout/tarkov-dev-svg-maps
- 作者：Shebuka 及贡献者
- 许可：**Creative Commons Attribution-NonCommercial-ShareAlike 4.0
  International (CC BY-NC-SA 4.0)**
- 用途：Customs、Ground Zero、Interchange、Lighthouse、Reserve、Shoreline、
  Streets of Tarkov、Terminal、Woods、The Lab、The Labyrinth 的矢量源与烘焙位图。
- 修改：SVG 源文件经栅格化、旋转为上北下南、压平到深色底并保存为 JPEG；
  楼层叠加图同样由这些 SVG 的分组烘焙而来。署名、非商业与相同方式共享条款
  继续适用于这些衍生图像。
- 源文件保留在 `research/svg/<地图 key>/`，许可全文见 `research/svg/LICENSE.md`。
- 上游明确禁止将其素材用于作弊软件、游戏内雷达/ESP 叠加或自动化像素脚本。
  本项目是地图阅读器与任务/截图助手，不是雷达或外挂；请勿把素材挪用至此类软件。

### eftarkov.com 地图矢量图

- 来源：https://www.eftarkov.com（冰破船 14 个单层 SVG，共享同一 viewBox）
- 用途：冰破船底图与 16 层楼层叠加图（`Assets/maps/icebreaker.jpg`、
  `Assets/maps/layers/icebreaker/*.png`）
- 源文件保留在 `research/svg/icebreaker/`。

### tarkov.dev 地图数据与瓦片

- 项目：https://github.com/the-hideout/tarkov-dev
- 代码许可：MIT
- 用途：地图清单、仿射/坐标元数据、地名标签，以及迷宫（The Labyrinth）的
  栅格瓦片底图。
- 瓦片/地图影像描绘的是《逃离塔科夫》中的地点，权利仍属游戏权利人；
  仅以个人、非商业用途随包分发。

## 任务与剧情数据

- 任务：https://github.com/the-hideout/tarkov-data-manager
  （`quest-data.json`，MIT）→ `Assets/quests.json`
- 剧情中文标题与目标：https://www.eftarkov.com/storyline → `Assets/story.json`
- 任务名、目标与链接为社区维护数据，新任务可能缺失；更新方式见 README 的
  数据管线章节，不要手改 JSON。

## 头像与攻略图

- 商人 / Boss 头像：`https://www.eftarkov.com/uploads/{trader,boss}/<id>.webp`
  → `Assets/traders/*.png`、`Assets/bosses/*.png`（构建期一次性抓取，已随包内置）
- 任务地点攻略图：本机整理的攻略页标注图 → `Assets/guides/<questId>/*.webp`
  （259 个任务随包内置，运行时不联网）

## 图例图标

`Assets/legend/*.png` 由本机素材整理与 PIL 几何绘制生成；`hazard`、`label`
两类为程序绘制。

## 运行时库

- **Avalonia UI** — MIT — https://avaloniaui.net/
- **SkiaSharp** — MIT — https://github.com/mono/SkiaSharp
- **HarfBuzzSharp** — MIT — https://github.com/mono/SkiaSharp
- **ANGLE / libEGL / libGLESv2** — BSD-style — https://chromium.googlesource.com/angle/angle

## Windows OCR

可选的截图任务识别通过 Windows PowerShell 调用本机 `Windows.Media.Ocr`。
截图与识别文本不会上传到任何服务器。
