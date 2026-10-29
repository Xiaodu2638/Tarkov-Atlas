using System.Net.Http;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace TarkovAtlas;

/// <summary>
/// In-app picture viewer. Used for the wiki task location maps so the image is
/// shown inside the application instead of being handed off to a browser.
/// Supports wheel zoom, drag to pan and a browser fallback when the download
/// fails (offline machine, site outage, ...).
/// </summary>
public sealed class ImageViewerWindow : Window
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(45) };

    private readonly Image _image;
    private readonly TextBlock _status;
    private readonly ScrollViewer _scroll;
    private readonly List<string> _urls;
    private readonly string _caption;
    private readonly Button _prev;
    private readonly Button _next;
    private readonly TextBlock _pager;

    private Bitmap? _bitmap;
    private double _zoom = 1;
    private int _index;
    private Point _dragStart;
    private Vector _dragOffset;

    public ImageViewerWindow(string caption, string url)
        : this(caption, new[] { url })
    {
    }

    public ImageViewerWindow(string caption, IReadOnlyList<string> urls)
    {
        _caption = caption;
        _urls = urls.Where(u => !string.IsNullOrWhiteSpace(u)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (_urls.Count == 0) _urls.Add("");

        Title = caption;
        Width = 980;
        Height = 720;
        MinWidth = 480;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Palette.Brush(Palette.AppBg);

        _image = new Image
        {
            // Size is driven by ApplyZoom(); Stretch=Fill with explicit pixel
            // dimensions keeps the bitmap 1:1 sharp at zoom=1 regardless of the
            // control's own measure pass.
            Stretch = Stretch.Fill,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };

        _status = new TextBlock
        {
            Text = "正在加载…",
            FontSize = 11,
            Foreground = Palette.Brush(Palette.TextSecondary),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        _prev = new Button { Content = "‹", Width = 30, IsEnabled = false };
        _next = new Button { Content = "›", Width = 30, IsEnabled = false };
        _pager = new TextBlock
        {
            FontSize = 10,
            Foreground = Palette.Brush(Palette.TextSecondary),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0),
        };
        _prev.Click += (_, _) => ShowIndex(_index - 1);
        _next.Click += (_, _) => ShowIndex(_index + 1);

        _scroll = new ScrollViewer
        {
            Content = _image,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Background = Palette.Brush(Palette.CanvasBg),
        };
        _scroll.PointerWheelChanged += OnWheel;
        _scroll.PointerPressed += OnPointerPressed;
        _scroll.PointerMoved += OnPointerMoved;
        _scroll.PointerReleased += OnPointerReleased;

        Content = BuildLayout();
        Opened += async (_, _) => await LoadAsync();
        KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Escape: Close(); break;
                case Key.Add or Key.OemPlus: SetZoom(_zoom * 1.25); break;
                case Key.Subtract or Key.OemMinus: SetZoom(_zoom / 1.25); break;
                case Key.D0 or Key.Home: SetZoom(1); break;
            }
        };
    }

    private Control BuildLayout()
    {
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };

        var header = new Border
        {
            Background = Palette.Brush(Palette.CardBg),
            BorderBrush = Palette.Brush(Palette.BorderPanel),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(16, 10),
            Child = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock
                    {
                        Text = _caption,
                        FontSize = 14,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = Palette.Brush(Palette.TextPrimary),
                    },
                    new TextBlock
                    {
                        Text = _urls.Count > 0 ? _urls[0] : "",
                        FontSize = 9.5,
                        Foreground = Palette.Brush(Palette.TextMuted),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    },
                },
            },
        };
        root.Children.Add(header);

        Grid.SetRow(_scroll, 1);
        root.Children.Add(_scroll);

        var zoomOut = new Button { Content = "−", Width = 32, Margin = new Thickness(0, 0, 4, 0) };
        zoomOut.Click += (_, _) => SetZoom(_zoom / 1.25);
        var zoomIn = new Button { Content = "+", Width = 32, Margin = new Thickness(0, 0, 8, 0) };
        zoomIn.Click += (_, _) => SetZoom(_zoom * 1.25);
        var reset = new Button { Content = "复位", Margin = new Thickness(0, 0, 8, 0) };
        reset.Click += (_, _) => SetZoom(1);
        var reload = new Button { Content = "重新加载" };
        reload.Click += async (_, _) => await LoadAsync();
        var browser = new Button { Content = "在浏览器打开" };
        browser.Click += (_, _) => OpenInBrowser();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(16, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Children = { zoomOut, zoomIn, reset, reload, browser, _prev, _pager, _next },
        };

        var footerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Margin = new Thickness(0, 8, 16, 8),
        };
        footerGrid.Children.Add(buttons);
        Grid.SetColumn(_status, 1);
        footerGrid.Children.Add(_status);

        var close = new Button { Content = "关闭" };
        close.Click += (_, _) => Close();
        Grid.SetColumn(close, 2);
        footerGrid.Children.Add(close);

        var footer = new Border
        {
            Background = Palette.Brush(Palette.PanelBg),
            BorderBrush = Palette.Brush(Palette.BorderPanel),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = footerGrid,
        };
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        return root;
    }

    private async Task LoadAsync()
    {
        if (_urls.Count == 0 || string.IsNullOrWhiteSpace(_urls[_index]))
        {
            ShowError("没有可显示的图片");
            return;
        }

        var source = _urls[_index];

        // Local guide files load straight from disk; remote urls keep the
        // https-only rule (the list used to be scraped from a web page).
        if (File.Exists(source))
        {
            try
            {
                _status.Text = "正在加载…";
                _status.Foreground = Palette.Brush(Palette.TextSecondary);
                _prev.IsEnabled = _index > 0;
                _next.IsEnabled = _index < _urls.Count - 1;
                _pager.Text = _urls.Count > 1 ? $"{_index + 1} / {_urls.Count}" : "";

                await using var stream = File.OpenRead(source);
                _bitmap = new Bitmap(stream);
                _image.Source = _bitmap;
                _zoom = 1;
                ApplyZoom();
                FitToViewport();
                _status.Text = $"{_bitmap.PixelSize.Width} × {_bitmap.PixelSize.Height}  ·  滚轮缩放  ·  拖拽平移";
            }
            catch (Exception ex)
            {
                ShowError("加载失败：" + ex.Message);
            }
            return;
        }

        if (!IsHttps(source))
        {
            ShowError("只支持 https 图片地址或本地文件");
            return;
        }

        try
        {
            _status.Text = "正在加载…";
            _status.Foreground = Palette.Brush(Palette.TextSecondary);
            _prev.IsEnabled = _index > 0;
            _next.IsEnabled = _index < _urls.Count - 1;
            _pager.Text = _urls.Count > 1 ? $"{_index + 1} / {_urls.Count}" : "";

            using var response = await Http.GetAsync(source);
            if (!response.IsSuccessStatusCode)
            {
                ShowError($"加载失败：HTTP {(int)response.StatusCode}");
                return;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync();
            using var stream = new MemoryStream(bytes);
            _bitmap = new Bitmap(stream);
            _image.Source = _bitmap;

            // Guides are dense annotated maps: at zoom=1 a 3000 px wide image
            // on a ~1000 px viewport only shows a quarter of it, and at the old
            // Uniform stretch a small cover art filled the window blurry. Fit
            // the image to the viewport width instead (never upscale beyond 1),
            // then centre the scroll on the top-left where titles usually sit.
            _zoom = 1;
            ApplyZoom();
            FitToViewport();

            _status.Text = $"{_bitmap.PixelSize.Width} × {_bitmap.PixelSize.Height}  ·  滚轮缩放  ·  拖拽平移";
        }
        catch (Exception ex)
        {
            ShowError("加载失败：" + ex.Message);
        }
    }

    private void ShowIndex(int index)
    {
        if (index < 0 || index >= _urls.Count || index == _index) return;
        _index = index;
        _ = LoadAsync();
    }

    private void ShowError(string message)
    {
        _status.Text = message;
        _status.Foreground = Palette.Brush(Palette.DangerText);
    }

    private static bool IsHttps(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    private void OpenInBrowser()
    {
        var source = _urls[_index];
        try
        {
            // Local files open in the system image viewer, remote ones in the
            // default browser.
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo { FileName = source, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowError("无法打开：" + ex.Message);
        }
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_bitmap is null) return;
        SetZoom(e.Delta.Y > 0 ? _zoom * 1.15 : _zoom / 1.15);
        e.Handled = true;
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_scroll).Properties.IsLeftButtonPressed) return;
        _dragStart = e.GetPosition(_scroll);
        _dragOffset = _scroll.Offset;
        e.Pointer.Capture(_scroll);
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!e.GetCurrentPoint(_scroll).Properties.IsLeftButtonPressed) return;
        var pos = e.GetPosition(_scroll);
        var delta = pos - _dragStart;
        _scroll.Offset = new Vector(
            Math.Max(0, _dragOffset.X - delta.X),
            Math.Max(0, _dragOffset.Y - delta.Y));
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e) =>
        e.Pointer.Capture(null);

    private void SetZoom(double zoom)
    {
        if (_bitmap is null) return;
        _zoom = Math.Clamp(zoom, 0.2, 8);
        ApplyZoom();
    }

    private void ApplyZoom()
    {
        if (_bitmap is null) return;
        _image.Width = Math.Max(1, _bitmap.PixelSize.Width * _zoom);
        _image.Height = Math.Max(1, _bitmap.PixelSize.Height * _zoom);
    }

    /// <summary>
    /// One-time fit after load: shrink wide location maps to the viewport so
    /// the whole sketch is visible on open. Only ever down-scales — a small
    /// cover art stays at 1:1 instead of being blown up blurry. Runs after the
    /// layout pass (the Opened event fires before the ScrollViewer is measured).
    /// </summary>
    private void FitToViewport()
    {
        if (_bitmap is null) return;
        _scroll.UpdateLayout();
        var viewport = _scroll.Bounds;
        if (viewport.Width <= 10 || viewport.Height <= 10)
        {
            // Window not measured yet (first load before Show): retry on the
            // next layout pass.
            _scroll.LayoutUpdated += OnFirstLayout;
            return;
        }
        ApplyFit(viewport);
    }

    private void OnFirstLayout(object? sender, EventArgs e)
    {
        _scroll.LayoutUpdated -= OnFirstLayout;
        if (_bitmap is null) return;
        ApplyFit(_scroll.Bounds);
    }

    private void ApplyFit(Rect viewport)
    {
        if (_bitmap is null) return;
        if (viewport.Width <= 10 || viewport.Height <= 10) return;
        var fit = Math.Min(viewport.Width / _bitmap.PixelSize.Width,
                           viewport.Height / _bitmap.PixelSize.Height);
        if (fit >= 1) return;
        _zoom = Math.Round(Math.Clamp(fit, 0.2, 1.0), 2);
        ApplyZoom();
        _scroll.Offset = new Vector(0, 0);
    }
}