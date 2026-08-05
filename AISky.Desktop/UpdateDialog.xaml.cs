using AISky_Desktop.Infrastructure;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace AISky_Desktop;

public sealed partial class UpdateDialog : ContentDialog
{
    private readonly CancellationTokenSource _cancellation = new();
    private UpdateRelease? _release;
    private string? _downloadedPackage;
    private bool _installing;

    public UpdateDialog()
    {
        InitializeComponent();
        CurrentVersionText.Text = $"当前版本 {VersionInfo.CurrentVersion}";
    }

    private async void UpdateDialog_Loaded(object sender, RoutedEventArgs e) =>
        await CheckForUpdatesAsync();

    private async Task CheckForUpdatesAsync()
    {
        SetBusy("正在连接 GitHub Release…");
        ReleasePanel.Visibility = Visibility.Collapsed;
        UpdateActionButton.Visibility = Visibility.Collapsed;
        OpenReleaseButton.Visibility = Visibility.Collapsed;
        RetryButton.Visibility = Visibility.Collapsed;
        try
        {
            var result = await App.Services.Updates.CheckAsync(_cancellation.Token);
            _release = result.Release;
            UpdateProgress.Visibility = Visibility.Collapsed;
            UpdateInfo.Message = result.Message;
            UpdateInfo.Severity = result.Availability switch
            {
                UpdateAvailability.Latest => InfoBarSeverity.Success,
                UpdateAvailability.AssetMissing => InfoBarSeverity.Warning,
                _ => InfoBarSeverity.Informational,
            };

            if (result.Release is not null)
            {
                ShowRelease(result.Release);
                OpenReleaseButton.Visibility = Visibility.Visible;
            }
            if (result.Availability == UpdateAvailability.Available)
            {
                UpdateActionButton.Content = "下载更新";
                UpdateActionButton.Visibility = Visibility.Visible;
                UpdateActionButton.IsEnabled = true;
            }
        }
        catch (OperationCanceledException)
        {
            // Closing the dialog intentionally cancels the request.
        }
        catch (Exception exception)
        {
            UpdateProgress.Visibility = Visibility.Collapsed;
            UpdateInfo.Severity = InfoBarSeverity.Error;
            UpdateInfo.Message = FriendlyUpdateError(exception);
            RetryButton.Visibility = Visibility.Visible;
            await App.Services.Log.WriteAsync("ERROR", $"Update check failed: {exception}");
        }
    }

    private void ShowRelease(UpdateRelease release)
    {
        ReleasePanel.Visibility = Visibility.Visible;
        ReleaseTitleText.Text = $"{release.Title} · {release.Version}";
        var published = release.PublishedUtc is { } time
            ? time.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'")
            : "发布时间未知";
        var size = release.Asset is { Size: > 0 } asset
            ? BackgroundSyncService.FormatBytes(asset.Size)
            : "未提供 Windows 更新包";
        ReleaseMetadataText.Text = $"{published} · {size}";
        ReleaseNotesText.Text = release.Notes.Length > 6000
            ? release.Notes[..6000] + "\n\n更新说明过长，余下内容请在 GitHub 发布页查看。"
            : release.Notes;
    }

    private async void RetryButton_Click(object sender, RoutedEventArgs e) =>
        await CheckForUpdatesAsync();

    private async void OpenReleaseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_release is not null)
        {
            await Launcher.LaunchUriAsync(_release.WebUri);
        }
    }

    private async void UpdateActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_downloadedPackage is not null)
        {
            StartInstallation();
            return;
        }
        if (_release is null)
        {
            return;
        }

        UpdateActionButton.IsEnabled = false;
        RetryButton.Visibility = Visibility.Collapsed;
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateProgress.IsIndeterminate = false;
        UpdateProgress.Minimum = 0;
        UpdateProgress.Maximum = 100;
        UpdateProgress.Value = 0;
        UpdateInfo.Severity = InfoBarSeverity.Informational;
        try
        {
            var progress = new Progress<UpdateDownloadProgress>(item =>
            {
                UpdateInfo.Message = item.Message;
                UpdateProgress.IsIndeterminate = item.Percent is null;
                if (item.Percent is { } percent)
                {
                    UpdateProgress.Value = Math.Clamp(percent, 0, 100);
                }
            });
            _downloadedPackage = await App.Services.Updates.DownloadAsync(
                _release,
                progress,
                _cancellation.Token);
            UpdateProgress.Visibility = Visibility.Collapsed;
            UpdateInfo.Severity = InfoBarSeverity.Success;
            UpdateInfo.Message = "更新包已下载并校验完成，可以安装。";
            UpdateActionButton.Content = "安装并重启";
            AutomationProperties.SetName(UpdateActionButton, "安装更新并重启 AISky");
            UpdateActionButton.IsEnabled = true;
        }
        catch (OperationCanceledException)
        {
            // Closing the dialog intentionally cancels the download.
        }
        catch (Exception exception)
        {
            UpdateProgress.Visibility = Visibility.Collapsed;
            UpdateInfo.Severity = InfoBarSeverity.Error;
            UpdateInfo.Message = FriendlyUpdateError(exception);
            UpdateActionButton.IsEnabled = true;
            RetryButton.Visibility = Visibility.Visible;
            await App.Services.Log.WriteAsync("ERROR", $"Update download failed: {exception}");
        }
    }

    private void StartInstallation()
    {
        if (_downloadedPackage is null || _installing)
        {
            return;
        }
        try
        {
            _installing = true;
            UpdateActionButton.IsEnabled = false;
            UpdateInfo.Severity = InfoBarSeverity.Informational;
            UpdateInfo.Message = "正在交给更新助手，AISky 即将重新启动…";
            App.Services.Updates.StartInstaller(_downloadedPackage);
            if ((Application.Current as App)?.MainWindow is MainWindow window)
            {
                window.ExitApplication();
            }
            else
            {
                Application.Current.Exit();
            }
        }
        catch (Exception exception)
        {
            _installing = false;
            UpdateActionButton.IsEnabled = true;
            UpdateInfo.Severity = InfoBarSeverity.Error;
            UpdateInfo.Message = FriendlyUpdateError(exception);
            _ = App.Services.Log.WriteAsync("ERROR", $"Update install failed: {exception}");
        }
    }

    private void SetBusy(string message)
    {
        UpdateInfo.IsOpen = true;
        UpdateInfo.Severity = InfoBarSeverity.Informational;
        UpdateInfo.Message = message;
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateProgress.IsIndeterminate = true;
    }

    private void UpdateDialog_Closing(
        ContentDialog sender,
        ContentDialogClosingEventArgs args)
    {
        if (_installing)
        {
            args.Cancel = true;
            return;
        }
        _cancellation.Cancel();
    }

    private static string FriendlyUpdateError(Exception exception)
    {
        var message = exception.Message.Trim();
        if (exception is HttpRequestException or TaskCanceledException)
        {
            return "无法连接 GitHub，请检查网络或稍后重试。";
        }
        return string.IsNullOrWhiteSpace(message)
            ? "更新检查失败，详细原因已写入日志。"
            : message;
    }
}
