using System.Net.Http;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace TarkovAtlas;

/// <summary>
/// In-app picture viewer for task location guides. The image is shown scaled to
/// fit the window (no zoom / pan — a guide picture is read, not navigated).
/// Left/right arrows or the arrow keys page through multiple images.
/// </summary>
public sealed class ImageViewerWindow : Window
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(45) };

    private readonly Image _image;
    private readonly TextBlock _status;
    private readonly List<string> _urls;
    private readonly string _caption;
    private readonly Button _prev;
    private readonly Button _next;
    private readonly TextBlock _pager;

    private Bitmap? _bitmap;
    private int _index;

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
            // Uniform: always show the whole guide, scaled down to fit.
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _status = new TextBlock
        {
            Text = "正在加载…",
            FontSize = 11,
            Foreground = Palette.Brush(Palette.TextSecondary),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        _prev = new Button { Content = "‹", Width = 32, IsEnabled = false };
        _next = new Button { Content = "›", Width = 32, IsEnabled = false };
        _pager = new TextBlock
        {
            FontSize = 10,
            Foreground = Palette.Brush(Palette.TextSecondary),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0),
        };
        _prev.Click += (_, _) => ShowIndex(_index - 1);
        _next.Click += (_, _) => ShowIndex(_index + 1);

        Content = BuildLayout();
        Opened += async (_, _) => await LoadAsync();
        KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Escape: Close(); break;
                case Key.Left: ShowIndex(_index - 1); break;
                case Key.Right: ShowIndex(_index + 1); break;
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

        var stage = new Border
        {
            Background = Palette.Brush(Palette.CanvasBg),
            Padding = new Thickness(8),
            Child = _image,
        };
        Grid.SetRow(stage, 1);
        root.Children.Add(stage);

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
            Children = { reload, browser, _prev, _pager, _next },
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
                _status.Text = $"{_bitmap.PixelSize.Width} × {_bitmap.PixelSize.Height}";
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
            _status.Text = $"{_bitmap.PixelSize.Width} × {_bitmap.PixelSize.Height}";
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
}
