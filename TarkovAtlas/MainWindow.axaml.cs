using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TarkovAtlas.Controls;
using TarkovAtlas.Models;
using TarkovAtlas.Services;

namespace TarkovAtlas;

public partial class MainWindow : Window
{
    private readonly MapRepository _repo = new();
    private readonly SettingsService _settingsService = new();
    private readonly QuestService _quests = new();
    private readonly ScreenshotService _screenshots = new();
    private readonly GameLogService _logs = new();
    private readonly GlobalHotkeyService _hotkeys = new();
    private readonly OcrService _ocr = new();
    private readonly LocalGuideService _localGuides = new();
    // ObservableCollection, not List<T>: the ListBox is bound once and only sees
    // the items that exist at binding time, so a plain List left the sidebar
    // empty (the filter filled it afterwards without any change notification).
    private readonly ObservableCollection<MapDefinition> _visibleMaps = new();
    private readonly Dictionary<string, List<ScreenshotInfo>> _shotsByMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _knownShotPaths = new(StringComparer.OrdinalIgnoreCase);
    private List<FloorLayerView> _floorLayers = new();
    private double _lastPlayerY;
    private bool _suppressLayerSelection;
    private bool _hasPlayerHeight;
    private bool _manualLayerPick;

    private AppSettings _settings = new();
    private MapDefinition? _currentMap;
    private OverlayWindow? _overlay;
    private Quest? _selectedQuest;
    private string? _detectedMapKey;
    private bool _initialized;
    private bool _shuttingDown;
    private bool _suppressMapSelection;
    private bool _suppressQuestEvents;
    private DispatcherTimer? _persistTimer;
    private DispatcherTimer? _toastTimer;
    private readonly string _defaultHint = "滚轮缩放  ·  左键拖拽  ·  双击放大  ·  0 复位";

    public MainWindow()
    {
        InitializeComponent();
        try
        {
            var icon = Path.Combine(AppPaths.AssetsDir, "TarkovAtlas.ico");
            if (File.Exists(icon)) Icon = new WindowIcon(icon);
        }
        catch { }

        Opened += OnOpened;
        Closing += OnClosing;
        KeyDown += OnWindowKeyDown;
        MapView.CursorGameMoved += OnCursorGameMoved;
        MapView.PinClicked += OnPinClicked;
        MapView.MarkerClicked += OnMarkerClicked;
        MapView.ViewChanged += (_, _) => UpdateZoomText();
        _quests.ProgressChanged += (_, _) => Dispatcher.UIThread.Post(RefreshQuestRowStates);
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (_initialized) return;
        _initialized = true;

        try
        {
            _settings = _settingsService.Load();
            _settings.Normalize();
            _repo.Load();
            _quests.Load();

            MapCountText.Text = $"{_repo.Maps.Count} 张";
            MapList.ItemsSource = _visibleMaps;
            ApplyMapFilter("");
            MapView.PoiKindsHidden = _settings.HiddenPoiKinds;
            ApplyDisplayOptions();

            _suppressQuestEvents = true;
            try
            {
                QuestFilterBox.SelectedIndex = Math.Clamp(_settings.QuestFilter, 0, 2);
                QuestFollowMapCheck.IsChecked = _settings.QuestFollowsMap;
            }
            finally
            {
                _suppressQuestEvents = false;
            }

            QuestList.ItemsSource = _quests.VisibleQuests;
            StoryList.ItemsSource = _quests.StoryRows;
            RefreshQuestCounts();
            _quests.SetFilter(_settings.QuestFilter);
            _quests.SetQuery("");
            _quests.SetMap(_settings.SelectedMap, _settings.QuestFollowsMap);

            var initial = _repo.FindByKey(_settings.SelectedMap) ?? _repo.Maps.FirstOrDefault();
            if (initial is not null)
            {
                SelectMapListItem(initial);
                await SelectMapAsync(initial, remember: false);
            }

            StartServices();

            if (_quests.LoadWarning is not null) ShowToast(_quests.LoadWarning);

            if (_settings.OverlayVisibleOnStartup) ShowOverlay(true);
        }
        catch (Exception ex)
        {
            AppPaths.LogCrash(ex);
            ShowToast("启动时出错: " + ex.Message);
        }
    }

    private void StartServices()
    {
        _hotkeys.Update(_settings.OverlayHotkey, _settings.OverlayLockHotkey);
        _hotkeys.OverlayToggleRequested += (_, _) => ToggleOverlay();
        _hotkeys.LockToggleRequested += (_, _) => ToggleOverlayLock();
        _hotkeys.Install();

        if (_settings.AutoDetect)
        {
            var logDir = GameLogService.ResolveLogDirectory(_settings.LogsPath);
            if (logDir is not null)
            {
                _settings.LogsPath = logDir;
                _logs.MapDetected += OnMapDetected;
                _logs.Start(logDir);
                LogStatusText.Text = "日志：" + logDir;
            }
            else
            {
                LogStatusText.Text = "日志：未找到，请在设置中指定";
                RaidStatusText.Text = "日志未连接";
                RaidStatusDot.Fill = Palette.Brush(Palette.StatusErrorText);
            }
        }
        else
        {
            RaidStatusText.Text = "自动识别已关闭";
            RaidStatusDot.Fill = Palette.Brush(Palette.TextDim);
        }

        var shotDir = ResolveScreenshotDir();
        if (shotDir is not null)
        {
            _screenshots.StartWatching(shotDir, info => Dispatcher.UIThread.Post(() => _ = OnNewScreenshotAsync(info)));
            ScreenshotStatusText.Text = "截图：" + shotDir;
        }
        else
        {
            ScreenshotStatusText.Text = "截图：目录不存在";
        }

        if (_settings.ScanOnStartup) _ = ScanScreenshotsAsync(quiet: true);
    }

    private string? ResolveScreenshotDir()
    {
        if (!string.IsNullOrWhiteSpace(_settings.ScreenshotsPath) && Directory.Exists(_settings.ScreenshotsPath))
            return _settings.ScreenshotsPath;
        if (Directory.Exists(AppPaths.DefaultScreenshotsDir))
        {
            _settings.ScreenshotsPath = AppPaths.DefaultScreenshotsDir;
            return _settings.ScreenshotsPath;
        }
        return null;
    }

    private void OnMapDetected(object? sender, MapDetectedEventArgs e)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            _detectedMapKey = e.MapKey;
            var map = _repo.FindByKey(e.MapKey);
            if (map is null) return;

            RaidStatusText.Text = "战局 · " + map.DisplayName;
            RaidStatusPill.Background = Palette.Brush(Palette.StatusLiveBg);
            RaidStatusPill.BorderBrush = Palette.Brush(Palette.StatusLiveBorder);
            RaidStatusDot.Fill = Palette.Brush(Palette.Success);
            LogStatusText.Text = "日志：已识别 " + e.LocationId;

            if (_settings.AutoSwitchMap)
            {
                SelectMapListItem(map);
                await SelectMapAsync(map, remember: true);
            }
        });
    }

    // ------------------------------------------------------------------
    // map selection

    private void ApplyMapFilter(string query)
    {
        query = (query ?? "").Trim();
        _visibleMaps.Clear();
        foreach (var map in _repo.Maps)
        {
            if (query.Length == 0 ||
                map.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                map.NameEn.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                map.Subtitle.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                _visibleMaps.Add(map);
            }
        }
        MapCountText.Text = query.Length == 0 ? $"{_repo.Maps.Count} 张" : $"{_visibleMaps.Count} / {_repo.Maps.Count}";
    }

    private void SelectMapListItem(MapDefinition map)
    {
        if (!_visibleMaps.Contains(map))
        {
            ApplyMapFilter("");
        }
        _suppressMapSelection = true;
        MapList.SelectedItem = map;
        MapList.ScrollIntoView(map);
        _suppressMapSelection = false;
    }

    private async void MapList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _suppressMapSelection) return;
        if (MapList.SelectedItem is MapDefinition map)
            await SelectMapAsync(map, remember: true);
    }

    private async Task SelectMapAsync(MapDefinition map, bool remember)
    {
        var mapChanged = _currentMap?.Key != map.Key;
        _currentMap = map;

        // The screenshot height is only meaningful for the map it was captured
        // on; carrying it across maps could pick a floor from another building.
        if (mapChanged)
        {
            _hasPlayerHeight = false;
            _lastPlayerY = 0;
            _manualLayerPick = false;
        }

        MapTitle.Text = map.DisplayName;
        MapSubtitle.Text = map.NameEn.ToUpperInvariant();
        MapDescription.Text = map.Description;
        MapSourceText.Text = $"{map.Extracts.Count} 撤离点  ·  {map.Bosses.Count} Boss";

        if (mapChanged)
        {
            try
            {
                var bitmap = await Task.Run(() => _repo.GetBitmap(map));
                if (_currentMap?.Key != map.Key) return;
                MapView.SetMap(map, bitmap);
                MapView.ClearFocusPin();
                MapView.PoiKindsHidden = _settings.HiddenPoiKinds;
                LoadFloorLayers(map);
                _overlay?.SetMap(map, bitmap);
                SyncOverlayFloor();
                UpdateZoomText();
            }
            catch (Exception ex)
            {
                AppPaths.LogCrash(ex);
                ShowToast("地图图片加载失败: " + ex.Message);
            }
        }

        UpdatePinsForCurrentMap();
        RefreshQuestScope();
        SyncOverlayPlayer();
        RebuildLegend();

        if (remember) RememberMap(map);
    }

    private void RememberMap(MapDefinition map)
    {
        _settings.SelectedMap = map.Key;
        Persist();
    }

    /// <summary>
    /// Resolve which building levels are available for the map and hand the
    /// selected one to the canvas. Levels are baked into the base image's pixel
    /// grid, so no extra transform is needed.
    /// </summary>
    private void LoadFloorLayers(MapDefinition map)
    {
        try
        {
            if (map.Layers.Count == 0)
            {
                _floorLayers = new List<FloorLayerView>();
                MapView.Layers = Array.Empty<FloorLayerView>();
                if (LayerComboBox is not null)
                {
                    LayerComboBox.ItemsSource = null;
                    LayerComboBox.IsVisible = false;
                }
                return;
            }

            // Decoding every floor PNG up-front is what made switching to a
            // multi-deck map (Icebreaker has 16 x 4120x5255 images) feel frozen.
            // Only the label + height range is needed for the combo box; the
            // bitmap is fetched on demand by ApplySelectedFloor().
            var views = map.Layers
                .OrderBy(l => l.Order)
                .Select(l => new FloorLayerView
                {
                    Name = l.Name,
                    Order = l.Order,
                    MinHeight = l.MinHeight,
                    MaxHeight = l.MaxHeight,
                    Source = l,
                })
                .ToList();

            _floorLayers = views;
            if (LayerComboBox is null) return;

            LayerComboBox.ItemsSource = views.Select(v => v.Name).ToList();
            LayerComboBox.IsVisible = views.Count > 1;

            // Auto-layer (on by default) wins over a remembered pick, so a fresh
            // screenshot immediately shows the floor the player is actually on.
            FloorLayerView? preferred = null;
            if (_settings.AutoLayer && _hasPlayerHeight)
                preferred = views.FirstOrDefault(v => v.MatchesHeight(_lastPlayerY));

            if (preferred is null && _settings.SelectedLayer.TryGetValue(map.Key, out var saved))
            {
                // A stale saved pick ("2nd Floor" remembered before the Ground
                // entry existed) must not shadow the real ground floor.
                var savedView = views.FirstOrDefault(v => v.Name == saved);
                if (savedView is not null && savedView.Source?.Image is not "")
                    preferred = savedView;
            }

            preferred ??= views.FirstOrDefault();

            _suppressLayerSelection = true;
            try
            {
                LayerComboBox.SelectedIndex = preferred is null ? -1 : views.IndexOf(preferred);
            }
            finally
            {
                _suppressLayerSelection = false;
            }
            ApplySelectedFloor();
        }
        catch (Exception ex)
        {
            AppPaths.LogCrash(ex);
        }
    }

    /// <summary>Manual floor switch: remember the choice for this map.</summary>
    private void LayerComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressLayerSelection) return;

        // A deliberate pick disables auto-layer for this map until the user
        // switches map (or re-enables it), otherwise the next screenshot would
        // yank the view back to another floor.
        _manualLayerPick = true;
        ApplySelectedFloor();

        if (_currentMap is null || LayerComboBox.SelectedIndex < 0) return;
        var name = LayerComboBox.SelectedItem as string;
        if (!string.IsNullOrWhiteSpace(name))
        {
            _settings.SelectedLayer[_currentMap.Key] = name;
            Persist();
        }
    }

    /// <summary>
    /// Pick the floor that matches a fresh player height (auto layer mode).
    /// Only engages when the map actually has layered artwork.
    /// </summary>
    private void AutoSelectLayerForHeight(double y)
    {
        _lastPlayerY = y;
        _hasPlayerHeight = true;

        if (!_settings.AutoLayer || _manualLayerPick || _currentMap is null || _floorLayers.Count < 2) return;
        if (LayerComboBox is null) return;

        var match = _floorLayers.FirstOrDefault(l => l.MatchesHeight(y));
        if (match is null) return;

        var index = _floorLayers.IndexOf(match);
        if (index < 0 || index == LayerComboBox.SelectedIndex) return;

        _suppressLayerSelection = true;
        try
        {
            LayerComboBox.SelectedIndex = index;
        }
        finally
        {
            _suppressLayerSelection = false;
        }
        ApplySelectedFloor();
        SyncOverlayFloor();
    }

    /// <summary>Keep the little overlay window on the same building level.</summary>
    private void SyncOverlayFloor()
    {
        if (_overlay is null) return;
        var index = LayerComboBox?.SelectedIndex ?? -1;
        _overlay.SetFloor(index >= 0 && index < _floorLayers.Count ? _floorLayers[index] : null);
    }

    /// <summary>Push the currently selected floor (or all of them) to the canvas.</summary>
    private async void ApplySelectedFloor()
    {
        if (_currentMap is null) { MapView.Layers = Array.Empty<FloorLayerView>(); return; }

        var index = LayerComboBox.SelectedIndex;
        var view = index >= 0 && index < _floorLayers.Count ? _floorLayers[index] : null;
        if (view is null)
        {
            MapView.Layers = Array.Empty<FloorLayerView>();
            MapView.InvalidateVisual();
            return;
        }

        // Decode off the UI thread: icebreaker floors are 60-175 MB PNGs and
        // synchronous decoding is what made rapid floor switching freeze.
        if (view.Bitmap is null && view.Source is not null)
        {
            var request = view;
            try
            {
                var bmp = await _repo.GetLayerBitmapAsync(request.Source);
                if (view.Bitmap is null) view.Bitmap = bmp;
            }
            catch (Exception ex)
            {
                AppPaths.LogCrash(ex);
            }
        }
        MapView.Layers = new[] { view };
        MapView.InvalidateVisual();
    }

    private void UpdatePinsForCurrentMap()
    {
        if (_currentMap is null)
        {
            MapView.Pins = Array.Empty<ScreenshotInfo>();
            PinCountText.Text = "0 个截图点";
            LocateLatestButton.IsVisible = false;
            return;
        }

        var pins = GetPins(_currentMap.Key);
        MapView.Pins = pins;
        MapView.ActivePin = GetLatestPin(_currentMap.Key);
        UpdatePinLabels();
        MapView.InvalidateVisual();
    }

    private List<ScreenshotInfo> GetPins(string mapKey)
    {
        if (!_shotsByMap.TryGetValue(mapKey, out var list)) return new List<ScreenshotInfo>();
        return list.OrderBy(p => p.CapturedAt == default ? p.FileTime : p.CapturedAt).ToList();
    }

    private ScreenshotInfo? GetLatestPin(string mapKey)
    {
        if (!_shotsByMap.TryGetValue(mapKey, out var list) || list.Count == 0) return null;
        return list.OrderByDescending(p => p.CapturedAt == default ? p.FileTime : p.CapturedAt).First();
    }

    private void OnCursorGameMoved(object? sender, (double X, double Z) point)
    {
        var text = $"X {point.X,8:0.0}   Z {point.Z,8:0.0}";
        HoverCoordText.Text = "游戏坐标  " + text;
        CursorCoordText.Text = text;
        HoverExtraText.Text = _currentMap is null ? "" : $"{_currentMap.DisplayName} · {_currentMap.Difficulty}难度";
    }

    private void OnPinClicked(object? sender, ScreenshotInfo pin)
    {
        HoverExtraText.Text = $"{pin.FileName}  ·  {pin.TimeText}  ·  朝向 {pin.HeadingDegrees:0}°";
        HoverCoordText.Text = $"X {pin.X:0.0}   Y {pin.Y:0.0}   Z {pin.Z:0.0}";
        MapView.ActivePin = pin;
        MapView.InvalidateVisual();
        SyncOverlayPlayer();
    }

    // ------------------------------------------------------------------
    // display options

    private void ApplyDisplayOptions()
    {
        MapView.Options = new MapDisplayOptions
        {
            ShowLabels = _settings.ShowLabels,
            ShowExtracts = _settings.ShowExtracts,
            ExtractFilter = _settings.ExtractFilter,
            ShowBosses = _settings.ShowBosses,
            ShowSpawns = _settings.ShowSpawns,
            ShowTransits = _settings.ShowTransits,
            ShowCaches = _settings.ShowCaches,
            ShowQuestMarkers = _settings.ShowQuestMarkers,
            ShowHazards = _settings.ShowHazards,
            ShowBtrs = _settings.ShowBtrs,
        };
        MapView.Hazards = _currentMap?.Hazards ?? new List<HazardZone>();
        MapView.InvalidateVisual();
        _overlay?.ApplyDisplayOptions(_settings);
        RebuildLegend();
    }

    // ------------------------------------------------------------------
    // legend panel

    /// <summary>One checkable row in the legend: swatch + label + count.</summary>
    private sealed class LegendRow
    {
        public required string Label { get; init; }
        public required IBrush Swatch { get; init; }
        /// <summary>Toolbox artwork icon (Assets/legend/{Icon}.png); wins over the swatch dot when set.</summary>
        public string? Icon { get; init; }
        public required Func<bool> Get { get; init; }
        public required Action<bool> Set { get; init; }
        public int Count { get; init; }
        /// <summary>Group heading the row renders under (点位 / 环境 / 道具).</summary>
        public required string Group { get; init; }
    }

    /// <summary>POI kind → display name (kept in sync with build_data's kinds).</summary>
    private static readonly Dictionary<string, string> PoiKindLabels = new()
    {
        ["钥匙"] = "钥匙",
        ["解除陷阱"] = "密室陷阱",
        ["喷灯"] = "BBQ-S43 喷灯",
        ["阀门手轮"] = "阀门手轮",
    };

    /// <summary>Collect the legend rows for the current map's display state.</summary>
    private List<LegendRow> CollectLegendRows()
    {
        var map = _currentMap;
        const string points = "点位";
        const string env = "环境";
        const string items = "道具";
        // Only offer toggles for marker sets this map actually has — a BTR
        // toggle on The Lab or a cache toggle on Factory is dead weight.
        var rows = new List<LegendRow>();
        if (map is null || map.Extracts.Count > 0)
        {
            rows.Add(new()
            {
                Label = "撤离点", Swatch = MapCanvas.PaletteBrush(MapCanvas.SwatchKind.ExtractPmc),
                Icon = "extract-pmc", Group = points,
                Get = () => _settings.ShowExtracts, Set = v => _settings.ShowExtracts = v, Count = map?.Extracts.Count ?? 0,
            });
        }
        if (map is null || map.Bosses.Count > 0)
        {
            rows.Add(new()
            {
                Label = "Boss 刷点", Swatch = MapCanvas.PaletteBrush(MapCanvas.SwatchKind.Boss),
                Group = points,
                Get = () => _settings.ShowBosses, Set = v => _settings.ShowBosses = v, Count = map?.Bosses.Sum(b => b.Points.Count) ?? 0,
            });
        }
        if (map is null || map.Spawns.Count > 0)
        {
            rows.Add(new()
            {
                Label = "出生点", Swatch = MapCanvas.PaletteBrush(MapCanvas.SwatchKind.SpawnPmc),
                Group = points,
                Get = () => _settings.ShowSpawns, Set = v => _settings.ShowSpawns = v, Count = map?.Spawns.Count ?? 0,
            });
        }
        if (map is null || map.Transits.Count > 0)
        {
            rows.Add(new()
            {
                Label = "转移点", Swatch = MapCanvas.PaletteBrush(MapCanvas.SwatchKind.Transit),
                Icon = "transit", Group = points,
                Get = () => _settings.ShowTransits, Set = v => _settings.ShowTransits = v, Count = map?.Transits.Count ?? 0,
            });
        }
        if (map is null || map.Btrs.Count > 0)
        {
            rows.Add(new()
            {
                Label = "BTR 站点", Swatch = MapCanvas.PaletteBrush(MapCanvas.SwatchKind.Transit),
                Group = points,
                Get = () => _settings.ShowBtrs, Set = v => _settings.ShowBtrs = v, Count = map?.Btrs.Count ?? 0,
            });
        }
        if (map is null || map.Caches.Count > 0)
        {
            rows.Add(new()
            {
                Label = "资源埋藏点", Swatch = MapCanvas.PaletteBrush(MapCanvas.SwatchKind.CacheStroke),
                Group = points,
                Get = () => _settings.ShowCaches, Set = v => _settings.ShowCaches = v, Count = map?.Caches.Count ?? 0,
            });
        }
        if (map is null || map.Hazards.Count > 0)
        {
            rows.Add(new()
            {
                Label = "危险区", Swatch = MapCanvas.PaletteBrush(MapCanvas.SwatchKind.MinefieldStroke),
                Group = env,
                Get = () => _settings.ShowHazards, Set = v => _settings.ShowHazards = v, Count = map?.Hazards.Count ?? 0,
            });
        }
        if (map is null || map.Labels.Count > 0)
        {
            rows.Add(new()
            {
                Label = "地名", Swatch = MapCanvas.PaletteBrush(MapCanvas.SwatchKind.Label),
                Group = env,
                Get = () => _settings.ShowLabels, Set = v => _settings.ShowLabels = v, Count = map?.Labels.Count ?? 0,
            });
        }

        if (map is not null && map.Pois.Count > 0)
        {
            foreach (var group in map.Pois.GroupBy(p => p.Kind).OrderBy(g => g.Key, StringComparer.CurrentCulture))
            {
                var kind = group.Key;
                var label = PoiKindLabels.GetValueOrDefault(kind, kind);
                rows.Add(new LegendRow
                {
                    Label = label,
                    Swatch = MapCanvas.PaletteBrush(MapCanvas.SwatchKind.QuestMarkerStroke),
                    Group = items,
                    Get = () => !_settings.HiddenPoiKinds.Contains(kind),
                    Set = v =>
                    {
                        if (v) _settings.HiddenPoiKinds.Remove(kind);
                        else if (!_settings.HiddenPoiKinds.Contains(kind)) _settings.HiddenPoiKinds.Add(kind);
                        MapView.PoiKindsHidden = _settings.HiddenPoiKinds;
                    },
                    Count = group.Count(),
                });
            }
        }
        return rows;
    }

    /// <summary>Re-render the docked legend from the current display state.
    /// Card rows (whole-row click target, icon left, count pill right) under
    /// group headings; closed categories fade to 40% opacity instead of an
    /// empty checkbox.</summary>
    private void RebuildLegend()
    {
        LegendPanel.IsVisible = _settings.ShowLegend && _currentMap is not null;
        if (!LegendPanel.IsVisible) { LegendItems.Children.Clear(); return; }

        var rows = CollectLegendRows();
        LegendItems.Children.Clear();
        var lastGroup = "";
        foreach (var row in rows)
        {
            if (row.Group != lastGroup)
            {
                lastGroup = row.Group;
                LegendItems.Children.Add(new TextBlock
                {
                    Text = lastGroup,
                    FontSize = 9.5,
                    FontWeight = FontWeight.SemiBold,
                    LetterSpacing = 1.2,
                    Foreground = Palette.Brush(Palette.TextMuted),
                    Margin = new Thickness(2, 8, 0, 3),
                });
            }

            var card = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 6),
                Background = Palette.Brush(Palette.CardAltBg),
                MinHeight = 32,
            };
            var on = row.Get();
            card.Opacity = on ? 1.0 : 0.4;

            var panel = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            var iconBitmap = row.Icon is null ? null : LoadLegendIcon(row.Icon);
            Control icon;
            if (iconBitmap is not null)
            {
                icon = new Border
                {
                    // thin outline around the artwork so mixed icon styles sit
                    // on one even baseline
                    BorderBrush = Palette.Brush(Palette.BorderStrong),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(1),
                    Child = new Image
                    {
                        Source = iconBitmap, Width = 17, Height = 17,
                        Stretch = Stretch.Uniform,
                    },
                };
            }
            else
            {
                icon = new Border
                {
                    Width = 12, Height = 12, CornerRadius = new CornerRadius(6),
                    Background = row.Swatch,
                    BorderBrush = Palette.Brush(Palette.BorderStrong),
                    BorderThickness = new Thickness(1),
                    VerticalAlignment = VerticalAlignment.Center,
                };
            }
            Grid.SetColumn(icon, 0);
            icon.VerticalAlignment = VerticalAlignment.Center;
            panel.Children.Add(icon);

            var label = new TextBlock
            {
                Text = row.Label, FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(label, 1);
            panel.Children.Add(label);

            if (row.Count > 0)
            {
                var badge = new Border
                {
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(7, 1),
                    Background = Palette.Brush(Palette.ButtonBg),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = row.Count.ToString(), FontSize = 9.5,
                        Foreground = Palette.Brush(Palette.TextMuted),
                    },
                };
                Grid.SetColumn(badge, 2);
                panel.Children.Add(badge);
            }

            card.Child = panel;
            card.Cursor = new Cursor(StandardCursorType.Hand);
            card.PointerEntered += (_, _) => card.Background = Palette.Brush(Palette.ListHoverBg);
            card.PointerExited += (_, _) => card.Background = Palette.Brush(Palette.CardAltBg);
            card.PointerReleased += (_, _) =>
            {
                row.Set(!row.Get());
                RebuildLegend();   // re-render: opacity + badge states refresh
                ApplyDisplayOptions();
                Persist();
            };
            LegendItems.Children.Add(card);
        }
    }

    private Dictionary<string, Bitmap?> _legendIconCache = new(StringComparer.OrdinalIgnoreCase);

    private Bitmap? LoadLegendIcon(string stem)
    {
        if (_legendIconCache.TryGetValue(stem, out var cached)) return cached;
        Bitmap? bmp = null;
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "legend", stem + ".png");
            if (File.Exists(path))
            {
                using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                bmp = new Bitmap(stream);
            }
        }
        catch
        {
            bmp = null;
        }
        _legendIconCache[stem] = bmp;
        return bmp;
    }

    private void DisplayMenuButton_Click(object? sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        menu.Items.Add(ToggleItem("撤离点", _settings.ShowExtracts, v =>
        {
            _settings.ShowExtracts = v;
            ApplyDisplayOptions();
            Persist();
        }));

        var faction = new MenuItem { Header = "撤离点类型" };
        foreach (var (key, label) in new[]
                 {
                     ("all", "全部（PMC + Scav）"), ("pmc", "仅 PMC / 共享"),
                     ("scav", "仅 Scav / 共享"), ("shared", "仅共享撤离"),
                 })
        {
            var item = new MenuItem
            {
                Header = label,
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = _settings.ExtractFilter == key,
            };
            item.Click += (_, _) =>
            {
                _settings.ExtractFilter = key;
                ApplyDisplayOptions();
                Persist();
            };
            faction.Items.Add(item);
        }
        menu.Items.Add(faction);

        menu.Items.Add(ToggleItem("Boss 刷点", _settings.ShowBosses, v =>
        {
            _settings.ShowBosses = v;
            ApplyDisplayOptions();
            Persist();
        }));
        menu.Items.Add(ToggleItem("出生点", _settings.ShowSpawns, v =>
        {
            _settings.ShowSpawns = v;
            ApplyDisplayOptions();
            Persist();
        }));
        menu.Items.Add(ToggleItem("转移点", _settings.ShowTransits, v =>
        {
            _settings.ShowTransits = v;
            ApplyDisplayOptions();
            Persist();
        }));
        menu.Items.Add(ToggleItem("物资埋藏点", _settings.ShowCaches, v =>
        {
            _settings.ShowCaches = v;
            ApplyDisplayOptions();
            Persist();
        }));
        menu.Items.Add(ToggleItem("任务地点标记", _settings.ShowQuestMarkers, v =>
        {
            _settings.ShowQuestMarkers = v;
            ApplyDisplayOptions();
            SyncQuestMarkers();
            Persist();
        }));
        menu.Items.Add(ToggleMenuItem("危险区（雷区 / 狙击区 / 其他）", _settings.ShowHazards, v =>
        {
            _settings.ShowHazards = v;
            ApplyDisplayOptions();
            Persist();
        }));
        menu.Items.Add(new Separator());
        menu.Items.Add(ToggleItem("地名", _settings.ShowLabels, v =>
        {
            _settings.ShowLabels = v;
            ApplyDisplayOptions();
            Persist();
        }));
        menu.Items.Add(ToggleItem("BTR 站点", _settings.ShowBtrs, v =>
        {
            _settings.ShowBtrs = v;
            ApplyDisplayOptions();
            Persist();
        }));
        menu.Items.Add(ToggleItem("图例面板", _settings.ShowLegend, v =>
        {
            _settings.ShowLegend = v;
            ApplyDisplayOptions();
            Persist();
        }));

        if (sender is Control anchor)
            menu.Open(anchor);
    }

    /// <summary>Same as <see cref="ToggleItem"/> but keeps the menu open on click.</summary>
    private static MenuItem ToggleMenuItem(string header, bool isChecked, Action<bool> onChange)
    {
        var item = ToggleItem(header, isChecked, onChange);
        item.StaysOpenOnClick = true;
        return item;
    }

    private static MenuItem ToggleItem(string header, bool isChecked, Action<bool> onChange)
    {
        var item = new MenuItem
        {
            Header = header,
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = isChecked,
        };
        item.Click += (_, _) => onChange(item.IsChecked);
        return item;
    }

    /// <summary>
    /// Debounced save: the overlay raises this on every wheel notch and every
    /// drag release, and a synchronous JSON write per notch stuttered.
    /// </summary>
    private void Persist()
    {
        if (_persistTimer is null)
        {
            _persistTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _persistTimer.Tick += (_, _) => PersistNow();
        }
        _persistTimer.Stop();
        _persistTimer.Start();
    }

    /// <summary>Write immediately (shutdown, and anything that must not be lost).</summary>
    private void PersistNow()
    {
        _persistTimer?.Stop();
        _settingsService.Save(_settings);
    }

    // ------------------------------------------------------------------
    // screenshots

    private async Task ScanScreenshotsAsync(bool quiet = false)
    {
        var dir = ResolveScreenshotDir();
        if (dir is null)
        {
            ScreenshotStatusText.Text = "截图：目录不存在";
            if (!quiet) ShowToast("未找到截图目录，请在设置中指定");
            return;
        }

        if (!quiet) ShowToast("正在扫描截图目录…");
        var files = await Task.Run(() => _screenshots.Scan(dir));
        var imported = 0;
        var deleted = 0;
        MapDefinition? latestMap = null;
        ScreenshotInfo? latestInfo = null;
        var latestTime = DateTime.MinValue;

        foreach (var info in files)
        {
            if (!_knownShotPaths.Add(info.FilePath)) continue;
            AssignScreenshotMap(info);
            AddScreenshotInternal(info);
            imported++;

            if (_settings.DeleteScreenshotsAfterScan && _screenshots.TryDeleteAfterImport(info.FilePath))
                deleted++;

            var time = info.CapturedAt == default ? info.FileTime : info.CapturedAt;
            if (time > latestTime)
            {
                latestTime = time;
                latestInfo = info;
                latestMap = _repo.FindByKey(info.MapKey);
            }
        }

        UpdatePinsForCurrentMap();
        UpdateScreenshotCount();

        if (!quiet)
        {
            var msg = imported == 0
                ? "没有发现新的截图坐标"
                : $"已导入 {imported} 个坐标" + (deleted > 0 ? $"，清理 {deleted} 张截图" : "");
            ShowToast(msg);
        }

        if (latestInfo is not null) AutoSelectLayerForHeight(latestInfo.Y);

        if (latestMap is not null && _settings.AutoSwitchMap && latestMap.Key != _currentMap?.Key)
        {
            SelectMapListItem(latestMap);
            await SelectMapAsync(latestMap, remember: true);
        }
        SyncOverlayPlayer();
    }

    private void AssignScreenshotMap(ScreenshotInfo info)
    {
        var candidates = _repo.Maps.Where(m => m.Contains(info.X, info.Z, 25)).ToList();
        if (_detectedMapKey is not null && candidates.Any(m => m.Key == _detectedMapKey))
        {
            info.MapKey = _detectedMapKey;
            return;
        }
        if (candidates.Count == 1)
        {
            info.MapKey = candidates[0].Key;
            return;
        }
        if (_currentMap is not null && candidates.Any(m => m.Key == _currentMap.Key))
        {
            info.MapKey = _currentMap.Key;
            info.MapWasGuessed = true;
            return;
        }
        info.MapKey = _currentMap?.Key ?? _settings.SelectedMap;
        info.MapWasGuessed = true;
    }

    private void AddScreenshotInternal(ScreenshotInfo info)
    {
        if (!_shotsByMap.TryGetValue(info.MapKey, out var list))
        {
            list = new List<ScreenshotInfo>();
            _shotsByMap[info.MapKey] = list;
        }
        if (!list.Any(p => p.FilePath.Equals(info.FilePath, StringComparison.OrdinalIgnoreCase)))
            list.Add(info);
    }

    private async Task OnNewScreenshotAsync(ScreenshotInfo info)
    {
        if (!_settings.ScanScreenshots) return;
        if (!_knownShotPaths.Add(info.FilePath)) return;

        AssignScreenshotMap(info);
        AddScreenshotInternal(info);
        UpdateScreenshotCount();

        if (_settings.DeleteScreenshotsAfterScan)
            _screenshots.TryDeleteAfterImport(info.FilePath);

        if (_settings.AutoSwitchMap && info.MapKey != _currentMap?.Key)
        {
            var map = _repo.FindByKey(info.MapKey);
            if (map is not null)
            {
                SelectMapListItem(map);
                await SelectMapAsync(map, remember: true);
            }
        }
        else if (_currentMap?.Key == info.MapKey)
        {
            UpdatePinsForCurrentMap();
        }

        AutoSelectLayerForHeight(info.Y);

        SyncOverlayPlayer();
        ShowToast($"新截图已定位: X {info.X:0.0}  Z {info.Z:0.0}");
    }

    private void UpdateScreenshotCount()
    {
        var count = _shotsByMap.Values.Sum(v => v.Count);
        ScreenshotStatusText.Text = $"截图：{count} 个坐标点";
    }

    private void ScanScreenshotsButton_Click(object? sender, RoutedEventArgs e) => _ = ScanScreenshotsAsync();

    private void LocateLatestButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_currentMap is null) return;
        var latest = GetLatestPin(_currentMap.Key);
        if (latest is null) return;
        MapView.ActivePin = latest;
        MapView.FocusOn(latest.X, latest.Z, 4.0);
        MapView.InvalidateVisual();
    }

    // ------------------------------------------------------------------
    // overlay

    private void OverlayButton_Click(object? sender, RoutedEventArgs e) => ToggleOverlay();

    private void ToggleOverlay() => ShowOverlay(_overlay is not { IsVisible: true });

    private void ShowOverlay(bool visible)
    {
        try
        {
            if (_overlay is null)
            {
                _overlay = new OverlayWindow(this, _settings.Clone());
                _overlay.SettingsChanged += (_, _) =>
                {
                    if (_overlay is null) return;
                    _settings.OverlayWidthFrom(_overlay);
                    Persist();
                };
                _overlay.VisibilityChanged += (_, _) =>
                {
                    if (_overlay is { IsVisible: false })
                    {
                        _settings.OverlayVisibleOnStartup = false;
                        Persist();
                    }
                    UpdateOverlayUi();
                };
                _overlay.Closed += (_, _) =>
                {
                    if (!_shuttingDown) _overlay = null;
                    UpdateOverlayUi();
                };
            }

            if (_currentMap is not null)
            {
                _overlay.SetMap(_currentMap, _repo.GetBitmap(_currentMap));
                _overlay.SetPlayer(GetLatestPin(_currentMap.Key));
                SyncOverlayFloor();
            }

            if (visible)
            {
                _overlay.ShowOverlay();
                _settings.OverlayVisibleOnStartup = true;
            }
            else
            {
                _overlay.HideOverlay();
                _settings.OverlayVisibleOnStartup = false;
            }
            Persist();
            UpdateOverlayUi();
        }
        catch (Exception ex)
        {
            AppPaths.LogCrash(ex);
            ShowToast("悬浮地图打开失败: " + ex.Message);
        }
    }

    private void ToggleOverlayLock()
    {
        if (_overlay is null || !_overlay.IsVisible) return;
        _overlay.SetLocked(!_overlay.IsLocked);
        _settings.OverlayLocked = _overlay.IsLocked;
        Persist();
    }

    private void SyncOverlayPlayer()
    {
        if (_overlay is null || _currentMap is null) return;
        try
        {
            _overlay.SetMap(_currentMap, _repo.GetBitmap(_currentMap));
            _overlay.SetPlayer(GetLatestPin(_currentMap.Key));
        }
        catch (Exception ex)
        {
            AppPaths.LogCrash(ex);
        }
    }

    private void UpdateOverlayUi()
    {
        var visible = _overlay is { IsVisible: true };
        TitleOverlayButton.Content = visible ? "隐藏悬浮" : "悬浮地图";
        MapOverlayButton.Content = visible ? "隐藏悬浮" : "悬浮";
    }

    // ------------------------------------------------------------------
    // quests

    private void QuestToggleButton_Click(object? sender, RoutedEventArgs e) => ToggleQuestDrawer();

    private void ToggleQuestDrawer()
    {
        var open = !QuestDrawer.IsVisible;
        QuestDrawer.IsVisible = open;
        QuestDrawer.Width = open ? 400 : 0;
        if (open)
        {
            _deferQuestSort = false;
            RefreshQuestScope();
            QuestSearchBox.Focus();
        }
    }

    private void CloseQuestDrawer_Click(object? sender, RoutedEventArgs e)
    {
        QuestDrawer.IsVisible = false;
        QuestDrawer.Width = 0;
    }

    private void QuestSearchBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_initialized || _suppressQuestEvents) return;
        _deferQuestSort = false;
        RefreshQuestScope();
    }

    private void QuestFilterBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _suppressQuestEvents) return;
        _settings.QuestFilter = Math.Clamp(QuestFilterBox.SelectedIndex, 0, 2);
        Persist();
        _deferQuestSort = false;
        RefreshQuestScope();
    }

    private void QuestFollowMapCheck_Click(object? sender, RoutedEventArgs e)
    {
        if (!_initialized || _suppressQuestEvents) return;
        _settings.QuestFollowsMap = QuestFollowMapCheck.IsChecked == true;
        Persist();
        _deferQuestSort = false;
        RefreshQuestScope();
    }

    /// <summary>Recompute the quest list for the current map + search + filter.
    /// A jump sets <see cref="_deferQuestSort"/> so every refresh caused by the
    /// map switch keeps the current row order; the next user-driven refresh
    /// (filter, search, drawer open) resets it and re-sorts normally.</summary>
    private bool _deferQuestSort;

    private void RefreshQuestScope()
    {
        if (_currentMap is null) return;
        var query = QuestSearchBox.Text ?? "";
        _quests.SetMap(_currentMap.Key, _settings.QuestFollowsMap);
        _quests.SetQuery(query);
        _quests.SetFilter(Math.Clamp(QuestFilterBox.SelectedIndex, 0, 2));
        if (_deferQuestSort)
        {
            // Keep row order: just re-bind tracking/completion state.
            foreach (var row in _quests.VisibleQuests)
                row.RefreshState(_quests.IsCompleted(row.Id), _quests.IsTracked(row.Id));
            RefreshQuestCounts();
            SyncQuestMarkers();
            return;
        }
        _quests.Apply();
        RefreshQuestCounts();
        SyncQuestMarkers();
    }

    /// <summary>主轴章节勾选：写回进度文件并刷新计数。</summary>
    private void StoryCheckBox_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: StoryRow row } box) return;
        _quests.SetStoryDone(row.Id, box.IsChecked == true);
        RefreshQuestCounts();
    }

    private void RefreshQuestCounts()
    {
        if (QuestEmptyText is not null)
            QuestEmptyText.IsVisible = _quests.VisibleQuests.Count == 0;
        var total = _quests.TotalCount;
        var completed = _quests.CompletedCount;
        var shown = _quests.VisibleQuests.Count;
        var scope = _quests.Searching || !_settings.QuestFollowsMap
            ? "全部地图"
            : _currentMap?.DisplayName ?? "全部地图";
        QuestCountText.Text = $"{scope} · 显示 {shown} · 已完成 {completed}/{total}";
        QuestStatusText.Text = $"任务 {completed}/{total} · 追踪 {_quests.TrackedCount}";
        QuestDatabaseText.Text = $"{total} 个任务 · 本地化 · 2026-10-27";
        if (StoryCountText is not null)
            StoryCountText.Text = $"剧情进度 {_quests.StoryDoneCount}/{_quests.Chapters.Count}";
    }

    private void SyncQuestMarkers()
    {
        if (_currentMap is null)
        {
            MapView.QuestMarkers = Array.Empty<QuestMarker>();
            return;
        }
        MapView.QuestMarkers = _settings.ShowQuestMarkers
            ? _quests.BuildMarkers(_currentMap.Key)
            : Array.Empty<QuestMarker>();
        MapView.InvalidateVisual();
    }

    /// <summary>Row-level refresh after a progress change so the checkbox
    /// states stay truthful without re-sorting the list out from under the
    /// user's cursor. The full sort re-runs on the next map/filter/search
    /// change (see <see cref="RefreshQuestScope"/>).</summary>
    private void RefreshQuestRowStates()
    {
        foreach (var row in _quests.VisibleQuests)
            row.RefreshState(_quests.IsCompleted(row.Id), _quests.IsTracked(row.Id));
        RefreshQuestCounts();
        SyncQuestMarkers();
    }

    private void QuestList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (QuestList.SelectedItem is not QuestRow row)
        {
            _selectedQuest = null;
            QuestDetailTitle.Text = "选择一个任务";
            QuestDetailMeta.Text = "";
            QuestObjectiveList.ItemsSource = null;
            SetQuestMapButtonState(null);
            return;
        }

        _selectedQuest = row.Quest;
        QuestDetailTitle.Text = row.Title;
        QuestDetailMeta.Text = $"{row.Giver}  ·  {row.LevelText}  ·  {row.MapsText}";
        SetQuestMapButtonState(row.Quest);
        QuestObjectiveList.ItemsSource = row.Quest.Objectives
            .Select(o =>
            {
                var maps = o.Zones.Select(z => z.Map).Distinct().ToList();
                var prefix = maps.Count == 0 ? "" : $"[{string.Join("/", maps.Select(MapName))}] ";
                return prefix + o.Text;
            })
            .ToList();
    }

    /// <summary>Enable the 任务地点图 button only when the quest ships local
    /// artwork; the tooltip says why it is disabled (or how many pages). No
    /// click-to-discover.</summary>
    private void SetQuestMapButtonState(Quest? quest)
    {
        if (OpenQuestMapImageButton is null) return;
        var has = quest is not null && _localGuides.HasLocalImages(quest.Id);
        OpenQuestMapImageButton.IsEnabled = has;
        ToolTip.SetTip(OpenQuestMapImageButton, quest is null
            ? "先选择一个任务"
            : has
                ? "查看该任务的地点标注图"
                : "该任务没有本地地点图");
    }

    private string MapName(string key) => _repo.FindByKey(key)?.DisplayName ?? key;

    private void ToggleSelectedQuestButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedQuest is null) return;
        var row = _quests.VisibleQuests.FirstOrDefault(r => r.Id == _selectedQuest.Id);
        if (row is not null)
            row.IsCompleted = !row.IsCompleted;
        else
            _quests.SetCompleted(_selectedQuest.Id, !_quests.IsCompleted(_selectedQuest.Id));

        // Re-run the active filter/sort: otherwise a task completed while the
        // "未完成" filter is on stays in the list and the click looks ignored.
        _deferQuestSort = false;
        RefreshQuestScope();
    }

    /// <summary>Jump to the task's map (prompt when it spans several maps).</summary>
    private async void QuestJumpButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: string questId }) return;
        var quest = _quests.FindById(questId);
        if (quest is null) return;
        var maps = quest.ZoneMaps
            .Select(_repo.FindByKey)
            .Where(m => m is not null)
            .Select(m => m!)
            .ToList();

        // Objectives without coordinates still name the raid(s) they happen on;
        // fall back to that list so hand-in/visit tasks keep a jump button.
        if (maps.Count == 0)
        {
            maps = quest.Maps
                .Select(_repo.FindByKey)
                .Where(m => m is not null)
                .Select(m => m!)
                .DistinctBy(m => m.Key)
                .ToList();
        }

        if (maps.Count == 0)
        {
            ShowToast("该任务没有关联地图（击杀/交付类任务）");
            return;
        }

        MapDefinition? target;
        if (maps.Count == 1)
        {
            target = maps[0];
        }
        else
        {
            target = await PickMapAsync(quest, maps);
            if (target is null) return;
        }

        // The map switch inside NavigateToAsync refreshes the quest list; keep
        // the current sort until the next user-driven refresh, otherwise the
        // promoted tracked task scrolls the list and the next jump click
        // lands on the wrong row.
        _deferQuestSort = true;
        await NavigateToAsync(target, remember: true);
        if (!_quests.IsTracked(quest.Id)) _quests.SetTracked(quest.Id, true);

        // ProgressChanged already re-synced the row states without re-sorting
        // (see RefreshQuestRowStates), so the list keeps its scroll position.

        // A task often scatters several zones across one map; mark every one of
        // them and centre on the group so nothing needs a second search.
        var zones = quest.ObjectivesFor(target.Key)
            .SelectMany(o => o.Zones)
            .Where(z => string.Equals(z.Map, target.Key, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (zones.Count > 0)
        {
            var seen = new HashSet<(int, int)>();
            var pins = zones
                .Where(z => seen.Add(((int)Math.Round(z.X), (int)Math.Round(z.Z))))
                .Select((z, i) => (z.X, z.Z, i == 0 ? quest.Title : ""))
                .ToList();
            MapView.SetFocusPins(pins);
            ShowToast($"已跳转到 {target.DisplayName}" + (pins.Count > 1 ? $"（{pins.Count} 个任务点）" : ""));
        }
        else
        {
            // Task names the map but carries no coordinates (hand-in / kill
            // tasks) — say so instead of silently doing nothing.
            MapView.ClearFocusPin();
            ShowToast($"已跳转到 {target.DisplayName}（该任务没有具体地点坐标）");
        }
    }

    /// <summary>Jump to the map (and calibrated marker) of one story objective.</summary>
    private async void StoryJumpButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: StoryObjective objective }) return;
        var target = _repo.FindByKey(objective.Map);
        if (target is null)
        {
            ShowToast("该目标没有关联地图");
            return;
        }

        await NavigateToAsync(target, remember: true);

        if (objective.HasCoordinates)
        {
            MapView.SetFocusPin(objective.X, objective.Z, objective.Text, focus: true);
            ShowToast($"已跳转到 {target.DisplayName} 的目标位置");
        }
        else
        {
            MapView.ClearFocusPin();
            ShowToast($"已跳转到 {target.DisplayName}（该目标无精确坐标）");
        }
    }

    /// <summary>
    /// Switch the sidebar selection and load a map. Every jump action (task,
    /// story objective, list click) goes through here so the selection state,
    /// the list highlight and the async load cannot drift apart.
    /// </summary>
    private async Task NavigateToAsync(MapDefinition map, bool remember)
    {
        SelectMapListItem(map);
        await SelectMapAsync(map, remember);
    }


    private async Task<MapDefinition?> PickMapAsync(Quest quest, IReadOnlyList<MapDefinition> maps)
    {
        var dialog = new MapPickWindow(quest.Name, maps);
        return await dialog.ShowDialog<MapDefinition?>(this);
    }

    /// <summary>大傻哥 (eftarkov.com) task page: /task/{id}.</summary>
    private const string WikiBase = "https://www.eftarkov.com/task/";

    /// <summary>Task location artwork lives under /uploads/task/{id}.webp.</summary>
    private const string WikiImageBase = "https://www.eftarkov.com/uploads/task/";
    private const string WikiPageBase = "https://www.eftarkov.com/task/";

    private void OpenWikiButton_Click(object? sender, RoutedEventArgs e) => OpenQuestPage();

    /// <summary>Show the task's annotated location map inside the app.</summary>
    private void OpenQuestMapImageButton_Click(object? sender, RoutedEventArgs e) => OpenQuestMapImage();

    private void OpenQuestPage()
    {
        var id = _selectedQuest?.Id;
        if (string.IsNullOrWhiteSpace(id))
        {
            ShowToast("请先选择一个任务");
            return;
        }

        OpenExternalUrl(WikiPageBase + id);
    }

    /// <summary>
    /// Show the task's guide pictures (the annotated location shots), not the
    /// wiki cover art. Falls back to the cover when the page has no guide image.
    /// </summary>
    private async void OpenQuestMapImage()
    {
        var quest = _selectedQuest;
        if (quest is null || string.IsNullOrWhiteSpace(quest.Id))
        {
            ShowToast("请先选择一个任务");
            return;
        }

        var caption = string.IsNullOrWhiteSpace(quest.Name) ? quest.Id : quest.Name;

        // Bundled local guides only: the toolbox assets are hand-verified and
        // the wiki scrape has proven unreliable (wrong/cover images), so no
        // network fallback when the quest ships with local artwork.
        var local = _localGuides.GetLocalImages(quest.Id);
        if (local.Count > 0)
        {
            var viewer = new ImageViewerWindow("任务地点图 · " + caption, local);
            viewer.Show(this);
            return;
        }

        ShowToast("这个任务没有本地地点图");
    }

    /// <summary>Clicking a quest marker opens that task's guide pictures.</summary>
    private void OnMarkerClicked(object? sender, QuestMarker marker)
    {
        if (marker is null || string.IsNullOrWhiteSpace(marker.QuestId)) return;

        var quest = _quests.FindById(marker.QuestId);
        if (quest is null) return;

        _selectedQuest = quest;
        QuestDetailTitle.Text = quest.Title;
        QuestDetailMeta.Text = $"{quest.Trader}  ·  {quest.LevelText}  ·  {quest.MapsText}";
        SetQuestMapButtonState(quest);

        // Local guides only — same policy as the task-detail button.
        var local = _localGuides.GetLocalImages(quest.Id);
        if (local.Count == 0)
        {
            ShowToast("这个任务没有本地地点图");
            return;
        }

        var viewer = new ImageViewerWindow("任务地点图 · " + quest.Title, local);
        viewer.Show(this);
    }

    private void OpenExternalUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowToast("无法打开浏览器: " + ex.Message);
        }
    }

    private async void OcrQuestButton_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择任务页面截图",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("图片")
                {
                    Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp" },
                },
            },
        });
        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (string.IsNullOrWhiteSpace(path)) return;

        OcrStatusText.IsVisible = true;
        OcrStatusText.Text = "正在用 Windows OCR 识别截图…";
        try
        {
            var result = await _ocr.RecognizeAsync(path);
            if (!result.Ok)
            {
                OcrStatusText.Text = "OCR 失败：" + result.Error;
                return;
            }

            var candidates = _quests.MatchText(result.Text);
            OcrStatusText.Text = candidates.Count == 0
                ? "未匹配到任务名；可在下方列表手动搜索勾选。"
                : $"识别到 {candidates.Count} 个候选任务。";

            var dialog = new QuestScanWindow("截图任务识别",
                "请核对 OCR 候选；未收录的任务可稍后在任务库更新后使用。",
                candidates, result.Text);
            await dialog.ShowDialog(this);
        }
        catch (Exception ex)
        {
            AppPaths.LogCrash(ex);
            OcrStatusText.Text = "OCR 出错：" + ex.Message;
        }
    }

    // ------------------------------------------------------------------
    // settings

    private async void SettingsButton_Click(object? sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(_settings.Clone(), _hotkeys.LastError);
        var saved = await window.ShowDialog<bool>(this);
        if (!saved) return;
        _settings = window.Settings;
        _settings.Normalize();
        Persist();
        ApplySettings();
    }

    private async void ApplySettings()
    {
        ApplyDisplayOptions();
        _hotkeys.Update(_settings.OverlayHotkey, _settings.OverlayLockHotkey);
        _hotkeys.Install();

        _logs.Stop();
        _logs.MapDetected -= OnMapDetected;
        _logs.MapDetected += OnMapDetected;
        if (_settings.AutoDetect) _logs.Start(_settings.LogsPath);
        else
        {
            RaidStatusText.Text = "自动识别已关闭";
            RaidStatusDot.Fill = Palette.Brush(Palette.TextDim);
        }

        _screenshots.StopWatching();
        var shotDir = ResolveScreenshotDir();
        if (shotDir is not null)
        {
            _screenshots.StartWatching(shotDir, info => Dispatcher.UIThread.Post(() => _ = OnNewScreenshotAsync(info)));
            _settings.ScreenshotsPath = shotDir;
        }

        _suppressQuestEvents = true;
        try
        {
            QuestFollowMapCheck.IsChecked = _settings.QuestFollowsMap;
        }
        finally
        {
            _suppressQuestEvents = false;
        }

        _overlay?.ApplySettings(_settings);
        SyncOverlayPlayer();
        RefreshQuestScope();
        await ScanScreenshotsAsync(quiet: true);
        ShowToast("设置已保存");
    }

    // ------------------------------------------------------------------
    // window chrome

    private void HeaderBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual visual && visual.FindAncestorOfType<Button>() is not null) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }
        BeginMoveDrag(e);
    }

    private void MinimizeButton_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();

    private void MapSearchBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_initialized) return;
        ApplyMapFilter(MapSearchBox.Text ?? "");
    }

    private void ResetViewButton_Click(object? sender, RoutedEventArgs e) => MapView.ResetView();

    private void ZoomInButton_Click(object? sender, RoutedEventArgs e) => MapView.ZoomIn();

    private void ZoomOutButton_Click(object? sender, RoutedEventArgs e) => MapView.ZoomOut();

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.F)
        {
            MapSearchBox.Focus();
            MapSearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && QuestDrawer.IsVisible)
        {
            QuestDrawer.IsVisible = false;
            QuestDrawer.Width = 0;
            e.Handled = true;
        }
    }

    private void UpdateZoomText() => ZoomText.Text = $"{MapView.Zoom * 100:0}%";

    private void UpdatePinLabels()
    {
        if (_currentMap is null) return;
        LocateLatestButton.IsVisible = GetLatestPin(_currentMap.Key) is not null;
        PinCountText.Text = $"{GetPins(_currentMap.Key).Count} 个截图点";
    }

    private void ShowToast(string message)
    {
        HintText.Text = message;
        if (_toastTimer is null)
        {
            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4.5) };
            _toastTimer.Tick += (_, _) =>
            {
                _toastTimer?.Stop();
                HintText.Text = _defaultHint;
            };
        }
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        _shuttingDown = true;
        _settings.SelectedMap = _currentMap?.Key ?? _settings.SelectedMap;
        if (_overlay is not null)
        {
            _settings.OverlayWidthFrom(_overlay);
            _settings.OverlayLocked = _overlay.IsLocked;
            _settings.OverlayOpacity = _overlay.OverlayOpacityValue;
            _overlay.AllowClose();
            _overlay.Close();
        }
        PersistNow();
        _hotkeys.Dispose();
        _logs.Dispose();
        _screenshots.Dispose();
        _quests.FlushProgress();
    }
}