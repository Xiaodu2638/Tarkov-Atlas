using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using TarkovAtlas.Models;
using TarkovAtlas.Services;

namespace TarkovAtlas;

public partial class SettingsWindow : Window
{
    private bool _suppress;
    private readonly string _hotkeyError;

    public SettingsWindow(AppSettings settings, string hotkeyError = "")
    {
        InitializeComponent();
        Settings = settings;
        _hotkeyError = hotkeyError;
        LoadValues();
    }

    public AppSettings Settings { get; }

    private void LoadValues()
    {
        _suppress = true;
        try
        {
            LogsPathBox.Text = Settings.LogsPath ?? "";
            ScreenshotsPathBox.Text = Settings.ScreenshotsPath ?? "";
            OverlayHotkeyBox.Text = Settings.OverlayHotkey;
            LockHotkeyBox.Text = Settings.OverlayLockHotkey;
            OpacitySlider.Value = Math.Clamp(Settings.OverlayOpacity, 0.25, 1.0);
            OpacityValueText.Text = $"{OpacitySlider.Value * 100:0}%";
            SizeSlider.Value = Math.Clamp(Settings.OverlaySize, 120, 600);
            SizeValueText.Text = $"{SizeSlider.Value:0}";
            ZoomSlider.Value = Math.Clamp(Settings.OverlayZoom, 0.5, 8);
            ZoomValueText.Text = $"{ZoomSlider.Value:0.0}x";
            AnchorBox.SelectedIndex = Settings.OverlayAnchor switch
            {
                "top" => 0,
                "bottom" => 2,
                _ => 1,
            };
            LockedCheck.IsChecked = Settings.OverlayLocked;
            OverlayVisibleCheck.IsChecked = Settings.OverlayVisibleOnStartup;
            ExtractsCheck.IsChecked = Settings.ShowExtracts;
            HazardsCheck.IsChecked = Settings.ShowHazards;
            AutoLayerCheck.IsChecked = Settings.AutoLayer;
            BossesCheck.IsChecked = Settings.ShowBosses;
            SpawnsCheck.IsChecked = Settings.ShowSpawns;
            TransitsCheck.IsChecked = Settings.ShowTransits;
            QuestMarkersCheck.IsChecked = Settings.ShowQuestMarkers;
            QuestFollowMapCheck.IsChecked = Settings.QuestFollowsMap;
            AutoDetectCheck.IsChecked = Settings.AutoDetect;
            AutoSwitchCheck.IsChecked = Settings.AutoSwitchMap;
            ScanScreenshotsCheck.IsChecked = Settings.ScanScreenshots;
            ScanOnStartupCheck.IsChecked = Settings.ScanOnStartup;
            DeleteScreenshotsCheck.IsChecked = Settings.DeleteScreenshotsAfterScan;
            LabelsCheck.IsChecked = Settings.ShowLabels;
            HotkeyErrorText.Text = _hotkeyError;
            HotkeyErrorText.IsVisible = !string.IsNullOrWhiteSpace(_hotkeyError);
            PathStatusText.Text = "日志目录: " + (GameLogService.ResolveLogDirectory(Settings.LogsPath) ?? "未找到");
        }
        finally
        {
            _suppress = false;
        }
    }

    private async void BrowseLogsButton_Click(object? sender, RoutedEventArgs e)
    {
        var dir = await PickFolder("选择塔科夫 logs 目录", LogsPathBox.Text);
        if (dir is not null) LogsPathBox.Text = dir;
    }

    private async void BrowseScreenshotsButton_Click(object? sender, RoutedEventArgs e)
    {
        var dir = await PickFolder("选择截图目录", ScreenshotsPathBox.Text);
        if (dir is not null) ScreenshotsPathBox.Text = dir;
    }

    private async Task<string?> PickFolder(string title, string? current)
    {
        var start = current is not null && Directory.Exists(current)
            ? await StorageProvider.TryGetFolderFromPathAsync(current)
            : null;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    private void AutoDetectPathsButton_Click(object? sender, RoutedEventArgs e)
    {
        var logs = GameLogService.ResolveLogDirectory(null);
        var screens = AppPaths.DefaultScreenshotsDir;
        if (logs is not null) LogsPathBox.Text = logs;
        if (Directory.Exists(screens)) ScreenshotsPathBox.Text = screens;
        PathStatusText.Text = logs is null
            ? "未自动找到日志目录；请手动选择游戏安装目录下的 logs。"
            : "已自动填写检测到的路径。";
    }

    private void HotkeyBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers == KeyModifiers.None)
        {
            if (sender is TextBox box) box.Text = "";
            e.Handled = true;
            return;
        }

        var text = HotkeyFormatter.Format(e.Key, e.KeyModifiers);
        if (!HotkeyFormatter.IsValid(text))
        {
            if (sender is TextBox invalidBox) invalidBox.Text = "无效组合，请重试";
            e.Handled = true;
            return;
        }

        if (sender is TextBox textBox)
        {
            textBox.Text = text;
            textBox.CaretIndex = text.Length;
        }
        e.Handled = true;
    }

    private void OpacitySlider_ValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppress) return;
        OpacityValueText.Text = $"{e.NewValue * 100:0}%";
    }

    private void SizeSlider_ValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppress) return;
        SizeValueText.Text = $"{e.NewValue:0}";
    }

    private void ZoomSlider_ValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppress) return;
        ZoomValueText.Text = $"{e.NewValue:0.0}x";
    }

    private void SaveButton_Click(object? sender, RoutedEventArgs e)
    {
        var overlay = (OverlayHotkeyBox.Text ?? "").Trim();
        var lockKey = (LockHotkeyBox.Text ?? "").Trim();
        if (!HotkeyFormatter.IsValid(overlay) || !HotkeyFormatter.IsValid(lockKey))
        {
            HotkeyErrorText.Text = "快捷键无效：需要至少一个修饰键（Ctrl / Alt / Shift / Win）和一个主键。";
            HotkeyErrorText.IsVisible = true;
            return;
        }
        if (string.Equals(overlay, lockKey, StringComparison.OrdinalIgnoreCase))
        {
            HotkeyErrorText.Text = "显示悬浮和锁定悬浮不能使用同一个快捷键。";
            HotkeyErrorText.IsVisible = true;
            return;
        }

        var logs = (LogsPathBox.Text ?? "").Trim();
        var shots = (ScreenshotsPathBox.Text ?? "").Trim();
        if (logs.Length > 0 && !Directory.Exists(logs))
        {
            HotkeyErrorText.Text = "日志目录不存在：" + logs;
            HotkeyErrorText.IsVisible = true;
            return;
        }
        if (shots.Length > 0 && !Directory.Exists(shots))
        {
            HotkeyErrorText.Text = "截图目录不存在：" + shots;
            HotkeyErrorText.IsVisible = true;
            return;
        }

        Settings.LogsPath = logs.Length == 0 ? null : logs;
        Settings.ScreenshotsPath = shots.Length == 0 ? null : shots;
        Settings.OverlayHotkey = overlay;
        Settings.OverlayLockHotkey = lockKey;
        Settings.OverlayOpacity = Math.Clamp(OpacitySlider.Value, 0.25, 1.0);
        Settings.OverlaySize = Math.Clamp(SizeSlider.Value, 120, 600);
        Settings.OverlayZoom = Math.Clamp(ZoomSlider.Value, 0.5, 8);
        var anchor = AnchorBox.SelectedIndex switch
        {
            0 => "top",
            2 => "bottom",
            _ => "middle",
        };
        if (anchor != Settings.OverlayAnchor)
        {
            // A new anchor only takes effect if the pinned position is dropped.
            Settings.OverlayScreenX = null;
            Settings.OverlayScreenY = null;
        }
        Settings.OverlayAnchor = anchor;
        Settings.OverlayLocked = LockedCheck.IsChecked == true;
        Settings.OverlayVisibleOnStartup = OverlayVisibleCheck.IsChecked == true;
        Settings.ShowExtracts = ExtractsCheck.IsChecked == true;
        Settings.ShowHazards = HazardsCheck.IsChecked == true;
        Settings.AutoLayer = AutoLayerCheck.IsChecked == true;
        Settings.ShowBosses = BossesCheck.IsChecked == true;
        Settings.ShowSpawns = SpawnsCheck.IsChecked == true;
        Settings.ShowTransits = TransitsCheck.IsChecked == true;
        Settings.ShowQuestMarkers = QuestMarkersCheck.IsChecked == true;
        Settings.QuestFollowsMap = QuestFollowMapCheck.IsChecked == true;
        Settings.AutoDetect = AutoDetectCheck.IsChecked == true;
        Settings.AutoSwitchMap = AutoSwitchCheck.IsChecked == true;
        Settings.ScanScreenshots = ScanScreenshotsCheck.IsChecked == true;
        Settings.ScanOnStartup = ScanOnStartupCheck.IsChecked == true;
        Settings.DeleteScreenshotsAfterScan = DeleteScreenshotsCheck.IsChecked == true;
        Settings.ShowLabels = LabelsCheck.IsChecked == true;
        Close(true);
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Close(false);

    private void Header_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Avalonia.Visual v && v.FindAncestorOfType<Button>() is not null) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        BeginMoveDrag(e);
    }
}