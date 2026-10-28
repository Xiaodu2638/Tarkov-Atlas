using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using TarkovAtlas.Models;

namespace TarkovAtlas;

public sealed class QuestScanWindow : Window
{
    private readonly List<(QuestCandidate Candidate, CheckBox Box)> _rows = new();
    private readonly TextBlock _status;

    public QuestScanWindow(string title, string subtitle, IReadOnlyList<QuestCandidate> candidates, string rawText)
    {
        Title = title;
        Width = 820;
        Height = 650;
        MinWidth = 620;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#090E14"));
        Foreground = new SolidColorBrush(Color.Parse("#E8EDF2"));
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
        SystemDecorations = SystemDecorations.BorderOnly;
        CanResize = true;

        _status = new TextBlock
        {
            Text = $"{subtitle}   候选：{candidates.Count}",
            FontSize = 10.5,
            Foreground = new SolidColorBrush(Color.Parse("#8FA0AE")),
            TextWrapping = TextWrapping.Wrap,
        };

        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("54,*,Auto"),
            Background = new SolidColorBrush(Color.Parse("#090E14")),
        };

        root.Children.Add(BuildHeader(title));
        root.Children.Add(BuildBody(candidates, rawText));
        root.Children.Add(BuildFooter());
        Content = root;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
    }

    private Control BuildHeader(string title)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(18, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var stack = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
        });
        stack.Children.Add(_status);
        grid.Children.Add(stack);
        var close = new Button
        {
            Content = "✕",
            Classes = { "ghost" },
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        close.Click += (_, _) => Close();
        Grid.SetColumn(close, 1);
        grid.Children.Add(close);

        var border = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#0D141C")),
            BorderBrush = new SolidColorBrush(Color.Parse("#26333F")),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = grid,
        };
        return border;
    }

    private Control BuildBody(IReadOnlyList<QuestCandidate> candidates, string rawText)
    {
        var main = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto"),
            Margin = new Thickness(16, 12, 16, 0),
        };

        var listPanel = new StackPanel { Spacing = 6 };
        if (candidates.Count == 0)
        {
            listPanel.Children.Add(new TextBlock
            {
                Text = "没有匹配到任务名。\n\n可以：\n· 在任务列表里手动搜索任务名并勾选\n· 换一张更清晰的任务页面截图再试一次",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.Parse("#9EAEBB")),
                TextWrapping = TextWrapping.Wrap,
            });
        }
        else
        {
            foreach (var candidate in candidates)
            {
                var check = new CheckBox
                {
                    IsChecked = candidate.Selected,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 2, 6, 0),
                };
                var titleBlock = new TextBlock
                {
                    Text = candidate.Quest.Title,
                    FontSize = 12,
                    FontWeight = FontWeight.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                };
                var meta = new TextBlock
                {
                    Text = $"{candidate.Quest.Trader}  ·  {candidate.Quest.LevelText}  ·  {candidate.Quest.MapsText}  ·  {candidate.Evidence}",
                    FontSize = 9.5,
                    Foreground = new SolidColorBrush(Color.Parse("#8FA0AE")),
                    TextWrapping = TextWrapping.Wrap,
                };
                var textStack = new StackPanel { Spacing = 2 };
                textStack.Children.Add(titleBlock);
                textStack.Children.Add(meta);

                var score = new TextBlock
                {
                    Text = candidate.ScoreText,
                    FontSize = 10,
                    FontWeight = FontWeight.Bold,
                    Foreground = new SolidColorBrush(candidate.Score >= 0.9
                        ? Color.Parse("#6FBF8B")
                        : Color.Parse("#D6A84B")),
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(8, 1, 0, 0),
                };

                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
                row.Children.Add(check);
                Grid.SetColumn(textStack, 1);
                row.Children.Add(textStack);
                Grid.SetColumn(score, 2);
                row.Children.Add(score);

                var border = new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#111922")),
                    BorderBrush = new SolidColorBrush(Color.Parse("#25313D")),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10, 8),
                    Child = row,
                };
                listPanel.Children.Add(border);
                _rows.Add((candidate, check));
            }
        }

        var scroll = new ScrollViewer
        {
            Content = listPanel,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 0, 0, 8),
        };
        main.Children.Add(scroll);

        var rawBox = new TextBox
        {
            Text = rawText,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 120,
            FontSize = 10,
            Background = new SolidColorBrush(Color.Parse("#0A0F15")),
        };
        var expander = new Expander
        {
            Header = "OCR 原始文本（用于排查识别错误）",
            Content = rawBox,
            IsExpanded = false,
            Margin = new Thickness(0, 6, 0, 0),
            Foreground = new SolidColorBrush(Color.Parse("#9EAEBB")),
        };
        Grid.SetRow(expander, 1);
        main.Children.Add(expander);
        return main;
    }

    private Control BuildFooter()
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            ColumnSpacing = 8,
            Margin = new Thickness(16, 8, 16, 12),
        };
        grid.Children.Add(new TextBlock
        {
            Text = "提示：勾选后可以复制任务名，在右侧任务列表中搜索并标记完成。",
            FontSize = 9.5,
            Foreground = new SolidColorBrush(Color.Parse("#6D7D8B")),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        });
        var copy = new Button { Content = "复制勾选任务名" };
        copy.Click += async (_, _) =>
        {
            var names = _rows.Where(r => r.Box.IsChecked == true)
                .Select(r => r.Candidate.Quest.Title)
                .ToList();
            if (names.Count == 0) return;
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null) await clipboard.SetTextAsync(string.Join("\n", names));
        };
        Grid.SetColumn(copy, 1);
        grid.Children.Add(copy);

        var close = new Button { Content = "关闭", Classes = { "accent" } };
        close.Click += (_, _) => Close();
        Grid.SetColumn(close, 2);
        grid.Children.Add(close);
        return grid;
    }
}