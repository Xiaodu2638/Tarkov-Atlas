# AGENTS.md

This file provides guidance to coding agents (DSH, Claude Code, Cursor, Codex, …) when working with code in this repository.

Tarkov Atlas —— 《逃离塔科夫》离线战术地图与任务助手。Windows / Avalonia 11 / .NET 10 / NativeAOT，运行时零联网。

## 命令

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ./build.ps1        # 发布到 dist/，AOT 优先、失败回退自包含
powershell -NoProfile -ExecutionPolicy Bypass -File ./tools/check.ps1  # 三套自检，提交前必须全绿
python tools/build_all.py            # 离线全量重建（底图/楼层/数据/剧情），加 --check 连门禁一起跑
python tools/update_data.py          # 唯一联网入口：抓 eftarkov.com -> build_all -> 门禁，失败回滚快照
python tools/check_map_coverage.py   # 13 张底图覆盖率自检，必须退出码 0
```

`tools/*.py` 会自己把 `tools/pyext`、`tools/pylibs` 插到 `sys.path` 前面，用仓库内的 Pillow/numpy，不要往全局 pip 装。

## 硬性约束

- **不许裸跑 `dotnet build|publish|run`**：脚本把 `TEMP`/`TMP`/`NUGET_PACKAGES`/`DOTNET_CLI_HOME` 重定向进仓库（`.tmp/`、`.nuget-packages/`、`.dotnet-home/`）。必须绕过脚本时自己先导出这几个变量，并设 `AVALONIA_TELEMETRY_OPTOUT=1`（否则遥测写日志失败会让 build 以误导性的 MSB4018 崩掉）。
- **新增可序列化类型必须登记 `TarkovAtlas/AppJsonContext.cs`**：`JsonSerializerIsReflectionEnabledByDefault=false`，漏登记在 NativeAOT 下要到运行时才炸。
- **改 XAML 后要确认真的重编译了**：增量 build 可能 1 秒就报「已成功」而 XAML 没进程序集；判据是耗时明显 >5 秒，不放心就删 `TarkovAtlas/obj`、`bin` 再 build。改前先 `Copy-Item` 备份，别用编辑器批量替换（写成非 UTF-8 立刻 `AVLN1001`）。
- **改了 C# 后先重跑 `build.ps1` 再验证**：验证脚本打的是 `dist/` 里的 exe，否则测到的是旧版本。
- **`Controls/MapCanvas.cs` 是性能关键路径**：每次整帧重绘，`DrawBitmapCropped` 只把视口内源矩形交给 Skia。楼层位图必须与底图共用同一 mip 阈值，不许给它加「缩得更小时绕过 mip」的豁免——5600² 的层是 113 MB 纹理，全尺寸采样就是「非地面层平移巨卡」的根因。
- **重跑 `build_*.py` 后按字段 diff 产物**（`maps.<key>.<field>`）：曾有 12 空格的代码被 8 空格的 `for` 吞成循环体，静默清零 12 张图的 loot/caches，三套自检照样全绿。
- **任务按模式隔离，改任务代码时容易漏一处**：`quests.json` 每条带 `modes`（pvp/pve/season），进度按模式分文件；`Apply()`/`BuildMarkers()`/`TotalCount` 全部要按 `QuestService.CurrentMode` 过滤，漏一处就是「勾了 PVE 的任务显示在 PVP」。
- **个人状态绝不进仓库或发布包**：进度与 `settings.json` 写在 `%APPDATA%\TarkovAtlas\`；`build.ps1` 打包前会扫 `dist`，命中直接 throw。
- 地图数据以 `TarkovAtlas/Assets/maps/mapdata.json` 为准；`maps.json` 里的 affine 未经人工校正，两者不一致先查原因、不要直接覆盖。

## 验证 UI 时的两个坑

- `app.manifest` 内嵌 `requireAdministrator`（全局热键需要），非交互 shell 启动 exe 会卡在 UAC。UI 验证用非提权构建：`dotnet publish TarkovAtlas\TarkovAtlas.csproj -c Release -p:ApplicationManifest= -p:PublishAot=false -o .tmp/verify/app`（`--no-incremental` 不是 publish 的合法开关，改 manifest 要先删 `TarkovAtlas/obj`），并先清掉残留实例，否则截到的是旧窗口。
- 单实例互斥 + UIPI 会静默吞掉低权限 shell 合成的输入：已有实例在跑时新进程只会激活旧窗口；提权实例收不到低权限鼠标事件（画面零变化 ≠ 功能坏了）。判据：`Get-Process TarkovAtlas` 的 `Path` 读不到即提权实例。

## 提交

历史用中文 conventional commits（`fix(data): …`、`docs(agents): …`）。提交身份是本地 git config 的 `Xiaodu <89345327+Xiaodu2638@users.noreply.github.com>`，别改回 `atlas@local`（GitHub 按邮箱归属，那个地址只会渲染成灰头像）。仓库未配置 remote，推送走 `.tmp/` 里的一次性脚本。

## 发布（`.github/workflows/monthly-data-refresh.yml`）

两条链路，别混：

1. **每月 15 日 09:00（Asia/Shanghai，cron `0 1 15 * *` UTC）刷新数据 → 开 PR**。CI 跑 `update_data.py --with-guides`（自带失败回滚 + 门禁），然后只把变更推到 `data/monthly-<yyyy-MM>` 分支并开 PR，**从不直接改 main**。数据 diff 由人审阅后合并。
2. **数据 PR 合进 main 后自动构建并发布**：`build.ps1`（NativeAOT 优先、失败回退自包含）→ 取现有最大 `v*` tag 末位 +1（`v1.0.0` → `v1.0.1` → …）→ `gh release create` 挂 `dist/TarkovAtlas-win-x64.zip`。

注意几点，改这个 workflow 时别踩：

- **必须 `windows-latest`**：check 项目 target `net10.0-windows`，`build_assets.py` 还要本机 Edge 栅格化 SVG（缺失会明确 throw，不静默降级）。
- **`tools/pyext`、`tools/pylibs`、`tools/tile-cache` 都被 .gitignore 排除**，CI 每次要自己 `pip install --target`；瓦片缓存用 `actions/cache` 兜住（缺失会现场下载 tarkov.dev 瓦片，该端点间歇 422）。
- **PowerShell 的 `if (<native cmd>)` 判断的是输出不是退出码**。`git diff --quiet` 无输出会让 `if` 永远为假——必须写成 `git diff --quiet; if ($LASTEXITCODE -eq 0)`。
- **不能用 here-string**：`@"…"@` 的结束符必须在行首，缩进后会整段解析失败；多行文本用数组 `-join "`n"`。
- tag 排序要用 `[version]` 比较，否则 `v1.0.10` 会排在 `v1.0.9` 前面。
