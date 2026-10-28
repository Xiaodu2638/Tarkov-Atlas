using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using TarkovAtlas.Models;

namespace TarkovAtlas;

/// <summary>Small chooser shown when a task spans several raid maps.</summary>
public sealed class MapPickWindow : Window
{
    private MapDefinition? _result;

    public MapPickWindow(string questName, IReadOnlyList<MapDefinition> maps)
    {
        Title = "选择地图";
        Width = 440;
        Height = Math.Min(560, 170 + maps.Count * 54);
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Palette.Brush(Palette.InputBg);
        Foreground = Palette.Brush(Palette.TextPrimary);
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
        SystemDecorations = SystemDecorations.BorderOnly;

        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            Margin = new Thickness(18, 14),
        };

        var header = new StackPanel { Spacing = 3 };
        header.Children.Add(new TextBlock
        {
            Text = "这个任务涉及多张地图",
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
        });
        header.Children.Add(new TextBlock
        {
            Text = questName,
            FontSize = 11,
            Foreground = Palette.Brush(Palette.TextSecondary),
            TextWrapping = TextWrapping.Wrap,
        });
        root.Children.Add(header);

        var list = new StackPanel { Spacing = 6, Margin = new Thickness(0, 12, 0, 12) };
        foreach (var map in maps)
        {
            var button = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(12, 9),
                Content = new StackPanel
                {
                    Spacing = 1,
                    Children =
                    {
                        new TextBlock { Text = map.DisplayName, FontSize = 12.5, FontWeight = FontWeight.SemiBold },
                        new TextBlock
                        {
                            Text = $"{map.NameEn}  ·  {map.Subtitle}",
                            FontSize = 9.5,
                            Foreground = Palette.Brush(Palette.TextDim),
                        },
                    },
                },
            };
            button.Click += (_, _) =>
            {
                _result = map;
                Close(map);
            };
            list.Children.Add(button);
        }

        var scroll = new ScrollViewer
        {
            Content = list,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        var cancel = new Button
        {
            Content = "取消",
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        cancel.Click += (_, _) => Close(null);
        Grid.SetRow(cancel, 2);
        root.Children.Add(cancel);

        Content = root;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close(null);
        };
    }
}
