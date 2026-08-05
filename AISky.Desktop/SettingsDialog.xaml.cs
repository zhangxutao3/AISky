using AISky_Desktop.Core;
using AISky_Desktop.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AISky_Desktop;

public sealed partial class SettingsDialog : ContentDialog
{
    private readonly AppSettings _initialSettings;

    public SettingsDialog(AppSettings settings)
    {
        _initialSettings = settings;
        InitializeComponent();
        AutoSyncToggle.IsOn = settings.AutoSyncEnabled;
        KeepInTrayToggle.IsOn = settings.KeepRunningInTray;
        StartWithWindowsToggle.IsOn = settings.StartWithWindows;
        CheckUpdatesToggle.IsOn = settings.CheckUpdatesOnStartup;
        LayerOpacitySlider.Value = Math.Clamp(settings.MapLayerOpacity, 0.35, 1) * 100;
        MapGridToggle.IsOn = settings.ShowMapGrid;
        MapPlacesToggle.IsOn = settings.ShowMapPlaces;
        DataAccessPasswordInput.Password = settings.DataAccessPassword;
        ApplicationVersionText.Text = $"版本 {VersionInfo.CurrentVersion}";
        SelectByTag(ForecastHoursPicker, settings.AutoSyncForecastHours.ToString());
        SelectRetention(settings.CacheRetentionDays);
    }

    public AppSettings SelectedSettings => _initialSettings with
    {
        AutoSyncEnabled = AutoSyncToggle.IsOn,
        AutoSyncForecastHours = ReadSelectedInteger(ForecastHoursPicker, 24),
        CacheRetentionDays = ReadRetentionDays(),
        KeepRunningInTray = KeepInTrayToggle.IsOn,
        StartWithWindows = StartWithWindowsToggle.IsOn,
        CheckUpdatesOnStartup = CheckUpdatesToggle.IsOn,
        MapLayerOpacity = Math.Clamp(LayerOpacitySlider.Value / 100, 0.35, 1),
        ShowMapGrid = MapGridToggle.IsOn,
        ShowMapPlaces = MapPlacesToggle.IsOn,
        DataAccessPassword = DataAccessPasswordInput.Password,
    };

    private void SettingsDialog_Closing(
        ContentDialog sender,
        ContentDialogClosingEventArgs args)
    {
        if (args.Result == ContentDialogResult.Primary
            && AutoSyncToggle.IsOn
            && string.IsNullOrWhiteSpace(DataAccessPasswordInput.Password))
        {
            args.Cancel = true;
            CredentialInfo.IsOpen = true;
            DataAccessPasswordInput.Focus(FocusState.Programmatic);
            return;
        }

        CredentialInfo.IsOpen = false;
    }

    private async void SettingsDialog_Loaded(object sender, RoutedEventArgs e)
    {
        CacheSizeText.Text = await Task.Run(CalculateCacheSize);
    }

    private void SelectRetention(int days)
    {
        var known = new[] { 3, 7, 15, 30 };
        if (known.Contains(days))
        {
            SelectByTag(RetentionPicker, days.ToString());
            return;
        }

        RetentionPicker.SelectedIndex = RetentionPicker.Items.Count - 1;
        CustomRetentionDays.Value = Math.Clamp(days, 1, 365);
        CustomRetentionDays.Visibility = Visibility.Visible;
    }

    private void RetentionPicker_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        CustomRetentionDays.Visibility =
            (RetentionPicker.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "custom"
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private async void CleanupButton_Click(object sender, RoutedEventArgs e)
    {
        CleanupButton.IsEnabled = false;
        CleanupProgress.Visibility = Visibility.Visible;
        CleanupInfo.IsOpen = false;
        try
        {
            var progress = new Progress<DataWorker.DataWorkerProgress>(item =>
            {
                CleanupInfo.Severity = item.IsWarning
                    ? InfoBarSeverity.Warning
                    : InfoBarSeverity.Informational;
                CleanupInfo.Message = item.Message;
                CleanupInfo.IsOpen = true;
            });
            var result = await App.Services.BackgroundSync.CleanupNowAsync(
                ReadRetentionDays(),
                progress);
            if (result is null)
            {
                CleanupInfo.Severity = InfoBarSeverity.Warning;
                CleanupInfo.Message = "另一个数据任务正在运行，请稍后再试。";
            }
            else
            {
                CleanupInfo.Severity = result.Failed > 0
                    ? InfoBarSeverity.Warning
                    : InfoBarSeverity.Success;
                CleanupInfo.Message =
                    $"已移除 {result.RemovedRuns} 项，释放 {BackgroundSyncService.FormatBytes(result.ReclaimedBytes)}。";
                CacheSizeText.Text = await Task.Run(CalculateCacheSize);
            }
            CleanupInfo.IsOpen = true;
        }
        catch (Exception exception)
        {
            CleanupInfo.Severity = InfoBarSeverity.Error;
            CleanupInfo.Message = $"清理失败：{exception.Message}";
            CleanupInfo.IsOpen = true;
        }
        finally
        {
            CleanupProgress.Visibility = Visibility.Collapsed;
            CleanupButton.IsEnabled = true;
        }
    }

    private int ReadRetentionDays()
    {
        var selectedTag = (RetentionPicker.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        if (selectedTag == "custom")
        {
            return Math.Clamp(
                double.IsNaN(CustomRetentionDays.Value)
                    ? _initialSettings.CacheRetentionDays
                    : (int)CustomRetentionDays.Value,
                1,
                365);
        }
        return int.TryParse(selectedTag, out var days) ? days : 3;
    }

    private string CalculateCacheSize()
    {
        long bytes = 0;
        foreach (var root in new[] { App.Services.Paths.Data, App.Services.Paths.RenderCache })
        {
            try
            {
                bytes += Directory
                    .EnumerateFiles(root, "*", SearchOption.AllDirectories)
                    .Sum(path =>
                    {
                        try
                        {
                            return new FileInfo(path).Length;
                        }
                        catch (IOException)
                        {
                            return 0L;
                        }
                        catch (UnauthorizedAccessException)
                        {
                            return 0L;
                        }
                    });
            }
            catch (IOException)
            {
                // A concurrently downloaded file may briefly disappear from enumeration.
            }
            catch (UnauthorizedAccessException)
            {
                // A concurrently downloaded file may briefly disappear from enumeration.
            }
        }
        return $"当前 {BackgroundSyncService.FormatBytes(bytes)}";
    }

    private static void SelectByTag(ComboBox picker, string tag)
    {
        picker.SelectedItem = picker.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => item.Tag?.ToString() == tag)
            ?? picker.Items.OfType<ComboBoxItem>().FirstOrDefault();
    }

    private static int ReadSelectedInteger(ComboBox picker, int fallback) =>
        int.TryParse((picker.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var value)
            ? value
            : fallback;
}
