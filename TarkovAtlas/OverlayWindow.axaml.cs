using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using TarkovAtlas.Controls;
using TarkovAtlas.Models;

namespace TarkovAtlas;

/// <summary>
/// Borderless square mini-map that stays glued to the right edge of the screen
/// and re-centres itself on the player's latest screenshot position.
/// </summary>
public partial class OverlayWindow : Window, IOverlayState
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExToolWindow = 0x00000080L;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    [DllImport("user32.dll", EntryPoint = "SetWindowPos", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);

    private readonly MainWindow _owner;
    private bool _locked;
    private bool _allowClose;
    private bool _suppressSettings;
    private bool _resizing;
    private Point _resizeStart;
    private double _startWidth;
    private double _startHeight;
    private double _opacity = 0.9;
    private double _zoom = 2.4;
    private MapDefinition? _map;
    private ScreenshotInfo? _player;

    public OverlayWindow(MainWindow owner, AppSettings settings)
    {
        _owner = owner;
        InitializeComponent();
        var grip = ResizeGrip;
        if (grip is not null) grip.Cursor = new Cursor(StandardCursorType.BottomRightCorner);
        Root.PointerPressed += OnRootPointerPressed;
        Root.PointerMoved += OnRootPointerMoved;
        Root.PointerReleased += OnRootPointerReleased;
        Root.PointerWheelChanged += OnRootWheel;
        Closing += OnClosing;
        ApplySettings(settings);
    }

    public event EventHandler? SettingsChanged;
    public event EventHandler? VisibilityChanged;
    public bool IsLocked => _locked;
    public double OverlayOpacityValue => _opacity;
    public double OverlayZoomValue => _zoom;

    public void ApplySettings(AppSettings settings)
    {
        _suppressSettings = true;
        try
        {
            _zoom = Math.Clamp(settings.OverlayZoom, 0.5, 12);
            _opacity = Math.Clamp(settings.OverlayOpacity, 0.25, 1.0);
            Root.Opacity = _opacity;
            Width = Math.Clamp(settings.OverlaySize, MinWidth, 900);
            Height = Width;
            OverlayMap.Options = BuildOptions(settings);
            PlaceWindow(settings);
            SetLocked(settings.OverlayLocked, raiseEvent: false);
            Recenter();
        }
        finally
        {
            _suppressSettings = false;
        }
    }

    private static MapDisplayOptions BuildOptions(AppSettings settings) => new()
    {
        ShowLabels = false,
        ShowExtracts = settings.ShowExtracts,
        ExtractFilter = settings.ExtractFilter,
        ShowBosses = settings.ShowBosses,
        ShowSpawns = false,
        ShowTransits = false,
        ShowQuestMarkers = settings.ShowQuestMarkers,
        ShowHazards = settings.ShowHazards,
    };

    private void PlaceWindow(AppSettings settings)
    {
        var screen = Screens.Primary;
        var area = screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
        var scaling = screen?.Scaling ?? 1.0;
        var w = (int)Math.Round(Width * scaling);
        var h = (int)Math.Round(Height * scaling);

        var x = area.Right - w - (int)Math.Round(settings.OverlayOffsetX * scaling);
        var y = settings.OverlayAnchor switch
        {
            "top" => area.Y + (int)Math.Round(settings.OverlayOffsetY * scaling),
            "bottom" => area.Bottom - h - (int)Math.Round(settings.OverlayOffsetY * scaling),
            _ => area.Y + (area.Height - h) / 2 + (int)Math.Round(settings.OverlayOffsetY * scaling),
        };
        x = Math.Clamp(x, area.X, Math.Max(area.X, area.Right - w));
        y = Math.Clamp(y, area.Y, Math.Max(area.Y, area.Bottom - h));
        Position = new PixelPoint(x, y);
    }

    public void SetMap(MapDefinition map, Bitmap bitmap)
    {
        _map = map;
        OverlayMap.SetMap(map, bitmap);
        Recenter();
    }

    /// <summary>Show the same building level the main window picked.</summary>
    public void SetFloor(FloorLayerView? layer)
    {
        OverlayMap.Layers = layer is null ? Array.Empty<FloorLayerView>() : new[] { layer };
        OverlayMap.InvalidateVisual();
    }

    public void SetPlayer(ScreenshotInfo? pin)
    {
        _player = pin;
        OverlayMap.ActivePin = pin;
        Recenter();
    }

    /// <summary>Mirror the main window's display toggles onto the mini-map.</summary>
    public void ApplyDisplayOptions(AppSettings settings)
    {
        OverlayMap.Options = BuildOptions(settings);
        OverlayMap.InvalidateVisual();
    }

    /// <summary>Keep the player position in the middle of the cut-out.</summary>
    private void Recenter()
    {
        if (_map is null || _player is null)
        {
            OverlayMap.InvalidateVisual();
            return;
        }
        OverlayMap.FocusOn(_player.X, _player.Z, _zoom);
        HintText.Text = $"{_map.DisplayName}  ·  {_player.X:0}, {_player.Z:0}";
        OverlayMap.InvalidateVisual();
    }

    public void ShowOverlay()
    {
        if (!IsVisible) Show();
        Topmost = true;
        SetLocked(_locked, raiseEvent: false);
        Recenter();
        VisibilityChanged?.Invoke(this, EventArgs.Empty);
    }

    public void HideOverlay()
    {
        if (IsVisible) Hide();
        VisibilityChanged?.Invoke(this, EventArgs.Empty);
    }

    public void AllowClose() => _allowClose = true;

    public void SetLocked(bool locked, bool raiseEvent = true)
    {
        _locked = locked;
        Root.IsHitTestVisible = !locked;
        UpdateClickThrough(locked);
        if (raiseEvent) RaiseSettingsChanged();
    }

    private void RaiseSettingsChanged()
    {
        if (_suppressSettings) return;
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateClickThrough(bool locked)
    {
        try
        {
            var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (handle == IntPtr.Zero) return;
            var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
            style |= WsExToolWindow;
            if (locked) style |= WsExTransparent;
            else style &= ~WsExTransparent;
            SetWindowLongPtr(handle, GwlExStyle, new IntPtr(style));

            // SetWindowLongPtr alone only caches the new style; without a
            // SWP_FRAMECHANGED refresh the window keeps its old hit-testing, so
            // unlocking did not actually restore mouse input.
            SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        }
        catch
        {
            // Non-Windows platforms fall back to Avalonia hit testing.
        }
    }

    /// <summary>
    /// Mouse-wheel zoom for the floating map. Anchor the zoom on the cursor so
    /// the spot under the pointer stays put, like the main map; resizing or a
    /// fresh screenshot re-centres on the player again.
    /// </summary>
    private void OnRootWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_locked) return;
        var factor = e.Delta.Y > 0 ? 1.15 : 1 / 1.15;
        var before = OverlayMap.Zoom;
        OverlayMap.ZoomAtScreen(e.GetPosition(OverlayMap), factor);
        _zoom = Math.Clamp(OverlayMap.Zoom, 0.5, 12);
        if (Math.Abs(_zoom - before) < 0.0001) return;
        HintText.Text = _map is null ? "" : _map.DisplayName;
        RaiseSettingsChanged();
        e.Handled = true;
    }

    private void OnRootPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_locked) return;
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsRightButtonPressed) return;
        _resizing = true;
        _resizeStart = point.Position;
        _startWidth = Width;
        _startHeight = Height;
        e.Pointer.Capture(Root);
        e.Handled = true;
    }

    private void OnRootPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_resizing) return;
        var pos = e.GetPosition(this);
        var delta = Math.Max(pos.X - _resizeStart.X, pos.Y - _resizeStart.Y);
        var size = Math.Clamp(_startWidth + delta, MinWidth, 900);
        Width = size;
        Height = size;
        e.Handled = true;
    }

    private void OnRootPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_resizing) return;
        _resizing = false;
        e.Pointer.Capture(null);
        Recenter();
        RaiseSettingsChanged();
        e.Handled = true;
    }

    private void ResizeGrip_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_locked) return;
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed) return;
        _resizing = true;
        _resizeStart = point.Position;
        _startWidth = Width;
        _startHeight = Height;
        e.Pointer.Capture(Root);
        e.Handled = true;
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        HideOverlay();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }
}