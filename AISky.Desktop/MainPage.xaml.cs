using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using AISky_Desktop.Core;
using AISky_Desktop.DataWorker;
using AISky_Desktop.Infrastructure;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.Web.WebView2.Core;
using Windows.Storage.Pickers;

namespace AISky_Desktop;

public sealed partial class MainPage : Page
{
    private readonly DispatcherTimer _playbackTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(720),
    };
    private readonly DispatcherTimer _modelStatusTicker = new()
    {
        Interval = TimeSpan.FromMilliseconds(3500),
    };

    private ForecastIndex _index = new();
    private List<ForecastRun> _currentForecastRuns = [];
    private ForecastRun? _selectedRun;
    private bool _mapReady;
    private bool _suppressSelection;
    private bool _suppressAutoSync = true;
    private bool _suppressDisplaySettings = true;
    private bool _suppressPaletteSelection;
    private bool _backgroundEventsAttached;
    private bool _isPlaying;
    private bool _firstRunBusy;
    private bool _startupDataReady;
    private bool _modelSelectionMadeByUser;
    private bool _tutorialOpen;
    private bool _tutorialQueued;
    private bool _trayRevealPrepared;
    private int _firstRunCurrentItem;
    private int _currentForecastIndex;
    private int _lastTimelineSelectionIndex = -1;
    private int _displayUtcOffsetHours;
    private int _modelStatusDisplayIndex;
    private BackgroundSyncStatus? _lastBackgroundStatus;
    private string? _lastLayerSetKey;
    private string? _lastMapSeriesKey;
    private readonly List<Button> _timelineSlotButtons = [];
    private readonly List<LayerItem> _allLayerItems = [];
    private readonly SemaphoreSlim _paletteSettingsGate = new(1, 1);

    public ObservableCollection<LayerItem> LayerItems { get; } = [];
    public ObservableCollection<ColorPaletteOption> PaletteOptions { get; } = [];
    public ObservableCollection<SyncModelStatusItem> SyncModelItems { get; } = [];
    public bool CanShowUpdatePrompt =>
        StartupCover.Visibility == Visibility.Collapsed
        && FirstRunOverlay.Visibility == Visibility.Collapsed
        && XamlRoot is not null;

    public MainPage()
    {
        InitializeComponent();
        _playbackTimer.Tick += PlaybackTimer_Tick;
        _modelStatusTicker.Tick += ModelStatusTicker_Tick;
        Unloaded += MainPage_Unloaded;
    }

    private async void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await App.Services.InitializeAsync();
            StartupStatusText.Text = "正在启动本地数据服务";
            _displayUtcOffsetHours = Math.Clamp(
                App.Services.CurrentSettings.DisplayUtcOffsetHours,
                -12,
                14);
            TimeZoneButton.Label = $"显示时区 · {DisplayTimeZoneLabel}";
            AttachBackgroundEvents();
            _suppressAutoSync = true;
            AutoSyncButton.IsChecked = App.Services.CurrentSettings.AutoSyncEnabled;
            _suppressDisplaySettings = true;
            WindAnimationToggle.IsOn = App.Services.CurrentSettings.ShowWindAnimation;
            TyphoonPathToggle.IsOn = App.Services.CurrentSettings.ShowTyphoonPaths;
            _suppressDisplaySettings = false;
            _suppressAutoSync = false;
            DarkThemeToggle.IsChecked = RootLayout.ActualTheme == ElementTheme.Dark;
            ServiceStatusText.Text = "正在连接本地 NetCDF 工作进程";

            await MapWebView.EnsureCoreWebView2Async();
            MapWebView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
            MapWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "aisky.local",
                Path.Combine(AppContext.BaseDirectory, "MapHost"),
                CoreWebView2HostResourceAccessKind.Allow);
            MapWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "aisky-data.local",
                App.Services.Paths.RenderCache,
                CoreWebView2HostResourceAccessKind.Allow);
            MapWebView.Source = new Uri("https://aisky.local/index.html");

            var workerStatus = await App.Services.DataWorker.GetStatusAsync();
            if (!workerStatus.IsAvailable)
            {
                ServiceStatusText.Text = workerStatus.Message;
                MapLoadingOverlay.Visibility = Visibility.Collapsed;
                ShowFirstRunOverlay(workerStatus.Message, InfoBarSeverity.Error);
                _startupDataReady = true;
                StartupCover.Visibility = Visibility.Collapsed;
                FirstRunProbeButton.IsEnabled = false;
                await App.Services.Log.WriteAsync("ERROR", workerStatus.Message);
                return;
            }

            _index = await App.Services.DataWorker.GetIndexAsync();
            ApplyIndex(_index);
            ServiceStatusText.Text = _index.Runs.Count == 0
                ? "暂无本地数据，请使用下载或导入本地 NetCDF"
                : $"已加载 {_index.Runs.Count} 个本地预报时刻";
            await App.Services.Log.WriteAsync(
                "INFO",
                $"Data worker ready: Python {workerStatus.PythonVersion}, NumPy {workerStatus.NumpyVersion}; {_index.Runs.Count} run(s).");
            ApplyBackgroundStatus(App.Services.BackgroundSync.CurrentStatus);
            if (!App.Services.CurrentSettings.FirstRunSetupCompleted
                || !HasCompleteForecastSeries(_index))
            {
                ShowFirstRunOverlay();
            }
            _startupDataReady = true;
            TryCompleteStartupCover();
        }
        catch (Exception exception)
        {
            ServiceStatusText.Text = "本地数据服务初始化失败";
            MapLoadingOverlay.Visibility = Visibility.Collapsed;
            ShowFirstRunOverlay(
                $"本地数据服务未能启动：{FriendlyDataError(exception)}",
                InfoBarSeverity.Error);
            _startupDataReady = true;
            StartupCover.Visibility = Visibility.Collapsed;
            try
            {
                await App.Services.Log.WriteAsync("ERROR", exception.ToString());
            }
            catch
            {
                WriteFallbackStartupError(exception);
            }
        }
    }

    private async void TutorialButton_Click(object sender, RoutedEventArgs e) =>
        await ShowTutorialAsync(automatic: false);

    private async void CoreWebView2_WebMessageReceived(
        object? sender,
        CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            if (e.TryGetWebMessageAsString() == "map-ready")
            {
                _mapReady = true;
                MapLoadingOverlay.Visibility = Visibility.Collapsed;
                ServiceStatusText.Text = _selectedRun is null
                    ? "地图已就绪，等待本地预报数据"
                    : "本地地图与 NetCDF 栅格已连接";
                SendThemeToMap();
                SendDisplayOptionsToMap();
                SendSelectedRunToMap(forceFull: true);
                await App.Services.Log.WriteAsync("INFO", "Map host reported ready.");
                TryCompleteStartupCover();
                return;
            }
        }
        catch (ArgumentException)
        {
            // Structured web messages are parsed below.
        }

        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var type))
            {
                return;
            }

            switch (type.GetString())
            {
                case "map-error":
                    var message = root.TryGetProperty("message", out var messageNode)
                        ? messageNode.GetString() ?? "未知错误"
                        : "未知错误";
                    ServiceStatusText.Text = $"地图数据加载失败：{message}";
                    await App.Services.Log.WriteAsync("ERROR", $"MapHost: {message}");
                    break;
                case "step-frame":
                    var delta = root.TryGetProperty("delta", out var deltaNode)
                        ? Math.Sign(deltaNode.GetInt32())
                        : 0;
                    if (delta != 0)
                    {
                        StepForecast(delta, wrap: false);
                    }
                    break;
                case "toggle-playback":
                    if (_currentForecastRuns.Count > 1)
                    {
                        SetPlaybackState(!_isPlaying);
                    }
                    break;
                case "select-typhoon-time":
                    SelectTyphoonForecast(root);
                    break;
                case "select-point-time":
                    SelectPointForecast(root);
                    break;
                case "typhoon-status":
                    var status = root.TryGetProperty("status", out var statusNode)
                        ? statusNode.GetString()
                        : null;
                    var trackCount = root.TryGetProperty("trackCount", out var countNode)
                        ? countNode.GetInt32()
                        : 0;
                    TyphoonPathToggle.OnContent = status switch
                    {
                        "loading" => "正在识别",
                        "ready" when trackCount > 0 => $"{trackCount} 条路径",
                        "ready" => "未识别到",
                        "error" => "识别失败",
                        _ => "分析本地模式",
                    };
                    break;
            }
        }
        catch (JsonException)
        {
            // Ignore unrecognized diagnostic messages.
        }
    }

    private void MapWebView_NavigationCompleted(
        WebView2 sender,
        CoreWebView2NavigationCompletedEventArgs args)
    {
        if (!args.IsSuccess)
        {
            MapLoadingOverlay.Visibility = Visibility.Collapsed;
            ServiceStatusText.Text = $"地图页面加载失败：{args.WebErrorStatus}";
            _startupDataReady = true;
            StartupCover.Visibility = Visibility.Collapsed;
            ShowFirstRunOverlay(
                $"本地地图加载失败：{args.WebErrorStatus}",
                InfoBarSeverity.Error);
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ServiceStatusText.Text = "正在刷新本地索引";
            var preferredModel = GetSelectedModel();
            var preferredInit = _selectedRun?.InitKey;
            _index = await App.Services.DataWorker.GetIndexAsync();
            ApplyIndex(_index, preferredModel, preferredInit);
            ServiceStatusText.Text = _index.Runs.Count == 0
                ? "本地索引为空，可下载或导入 NetCDF"
                : $"索引已刷新，共 {_index.Runs.Count} 个预报时刻";
            if (_index.Runs.Count == 0)
            {
                ShowFirstRunOverlay();
            }
        }
        catch (Exception exception)
        {
            ServiceStatusText.Text = $"索引刷新失败：{exception.Message}";
            await App.Services.Log.WriteAsync("ERROR", exception.ToString());
        }
    }

    private async void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new BackfillDialog(App.Services.CurrentSettings.DataAccessPassword)
        {
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary
            || dialog.Request is null)
        {
            return;
        }

        var started = await App.Services.BackgroundSync.StartBackfillAsync(dialog.Request);
        if (!started)
        {
            ServiceStatusTitle.Text = "后台任务";
            ServiceStatusText.Text = "已有数据任务正在运行，请稍后重新提交回溯下载";
            return;
        }
        await App.Services.Log.WriteAsync(
            "INFO",
            $"Backfill queued in background. model={dialog.Request.Model}, start={dialog.Request.StartUtc:O}, end={dialog.Request.EndUtc:O}.");
    }

    private void CancelBackgroundTask_Click(object sender, RoutedEventArgs e)
    {
        if (App.Services.BackgroundSync.CancelActiveOperation())
        {
            CancelBackgroundTaskButton.IsEnabled = false;
            CancelBackgroundTaskCommand.IsEnabled = false;
            ServiceStatusText.Text = "正在取消回溯下载";
        }
    }

    private async void ImportButton_Click(object sender, RoutedEventArgs e) =>
        await ImportLocalNetCdfAsync();

    private async Task<bool> ImportLocalNetCdfAsync()
    {
        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.Downloads,
                ViewMode = PickerViewMode.List,
            };
            picker.FileTypeFilter.Add(".nc");
            var app = Application.Current as App;
            if (app?.MainWindow is null)
            {
                throw new InvalidOperationException("主窗口尚未就绪。");
            }

            var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(app.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);
            var file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                return false;
            }

            ServiceStatusText.Text = $"正在验证 {file.Name}";
            var progress = new Progress<DataWorkerProgress>(item =>
            {
                ServiceStatusText.Text = item.Message;
                if (FirstRunOverlay.Visibility == Visibility.Visible)
                {
                    FirstRunStatusText.Text = item.Message;
                }
            });
            _index = await App.Services.DataWorker.ImportAsync(
                file.Path,
                copySource: true,
                progress);
            ApplyIndex(_index);
            ServiceStatusText.Text = $"{file.Name} 已解析并加入本地索引";
            HideFirstRunOverlayIfReady(_index);
            await App.Services.Log.WriteAsync("INFO", $"Imported NetCDF: {file.Path}");
            return true;
        }
        catch (Exception exception)
        {
            var message = FriendlyDataError(exception);
            ServiceStatusText.Text = $"导入失败：{message}";
            if (FirstRunOverlay.Visibility == Visibility.Visible)
            {
                ShowFirstRunMessage(message, InfoBarSeverity.Error);
                FirstRunStatusText.Text = "文件未通过校验";
            }
            await App.Services.Log.WriteAsync("ERROR", exception.ToString());
            return false;
        }
    }

    private async void FirstRunProbeButton_Click(object sender, RoutedEventArgs e) =>
        await ProbeFirstRunAsync();

    private async void FirstRunImportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_firstRunBusy)
        {
            return;
        }

        SetFirstRunBusy(true, "正在等待选择本地 NetCDF");
        try
        {
            await ImportLocalNetCdfAsync();
        }
        finally
        {
            SetFirstRunBusy(false);
        }
    }

    private async Task ProbeFirstRunAsync()
    {
        if (_firstRunBusy)
        {
            return;
        }

        var password = FirstRunPasswordInput.Password.Trim();
        var model = GetFirstRunModel();
        SetFirstRunBusy(true, $"正在寻找 {model} 最新起报");
        FirstRunInfo.IsOpen = false;
        try
        {
            var progress = new Progress<DataWorkerProgress>(UpdateFirstRunProgress);
            var result = await App.Services.BackgroundSync.PrepareFirstForecastAsync(
                model,
                password,
                progress);
            _index = result.Index;
            ApplyIndex(result.Index, result.Model, result.InitKey);
            if (result.IsComplete)
            {
                await App.Services.UpdateSettingsAsync(
                    App.Services.CurrentSettings with
                    {
                        DataAccessPassword = password,
                        FirstRunSetupCompleted = true,
                    });
                FirstRunStatusText.Text =
                    $"{result.Model} 已准备 {result.AvailableRuns}/{result.ExpectedRuns} 个预报时次";
                FirstRunProgressBar.Value = 100;
                FirstRunProgressValueText.Text = "100%";
                FirstRunProgressCountText.Text =
                    $"{result.AvailableRuns} / {result.ExpectedRuns} 个时次";
                FirstRunTransferText.Text = "本地校验完成，正在进入预报地图";
                HideFirstRunOverlayIfReady(result.Index);
                await App.Services.Log.WriteAsync(
                    "INFO",
                    $"First-run setup completed. model={result.Model}, init={result.InitKey}, runs={result.AvailableRuns}.");
                return;
            }

            var status = App.Services.BackgroundSync.CurrentStatus;
            FirstRunStatusText.Text =
                $"当前起报仅有 {result.AvailableRuns}/{result.ExpectedRuns} 个预报时次";
            FirstRunProgressCountText.Text =
                $"{result.AvailableRuns} / {result.ExpectedRuns} 个时次可用";
            ShowFirstRunMessage(
                status.IsError
                    ? status.Message
                    : result.InitKey is null
                        ? $"最近 3 天暂未发现 {result.Model} 可用起报，请检查网络后重试；若服务器要求验证，再填写访问密码。"
                        : "完整序列尚未下载完成，请保持网络连接后重试；已有文件不会重复下载。",
                status.IsError ? InfoBarSeverity.Error : InfoBarSeverity.Warning);
        }
        catch (Exception exception)
        {
            var message = FriendlyDataError(exception);
            FirstRunStatusText.Text = "准备数据时遇到问题";
            ShowFirstRunMessage(message, InfoBarSeverity.Error);
            await App.Services.Log.WriteAsync("ERROR", $"First-run preparation failed: {exception}");
        }
        finally
        {
            SetFirstRunBusy(false);
        }
    }

    private void ShowFirstRunOverlay(
        string? message = null,
        InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        StartupCover.Visibility = Visibility.Collapsed;
        FirstRunOverlay.Visibility = Visibility.Visible;
        FirstRunStatusText.Text = "等待开始初始化";
        FirstRunProgressBar.Value = 0;
        FirstRunProgressValueText.Text = "0%";
        FirstRunProgressCountText.Text = "0 / 120 个时次";
        FirstRunTransferText.Text = "选择模型后即可开始";
        FirstRunProbeButton.IsEnabled = true;
        if (!string.IsNullOrWhiteSpace(message))
        {
            ShowFirstRunMessage(message, severity);
        }
        else
        {
            FirstRunInfo.IsOpen = false;
        }

        FirstRunProbeButton.Focus(FocusState.Programmatic);
    }

    private void ShowFirstRunMessage(string message, InfoBarSeverity severity)
    {
        FirstRunInfo.Severity = severity;
        FirstRunInfo.Title = severity switch
        {
            InfoBarSeverity.Error => "未能准备数据",
            InfoBarSeverity.Warning => "需要确认",
            InfoBarSeverity.Success => "准备完成",
            _ => "提示",
        };
        FirstRunInfo.Message = message;
        FirstRunInfo.IsOpen = true;
    }

    private void SetFirstRunBusy(bool isBusy, string? status = null)
    {
        _firstRunBusy = isBusy;
        FirstRunProgressRing.IsActive = isBusy;
        FirstRunModelPicker.IsEnabled = !isBusy;
        FirstRunPasswordInput.IsEnabled = !isBusy;
        FirstRunProbeButton.IsEnabled = !isBusy;
        FirstRunImportButton.IsEnabled = !isBusy;
        if (isBusy)
        {
            _firstRunCurrentItem = 0;
            FirstRunProgressBar.Value = 0;
            FirstRunProgressValueText.Text = "0%";
            FirstRunProgressCountText.Text = "0 / 120 个时次";
            FirstRunTransferText.Text = "正在连接 AISky 数据服务";
        }
        if (!string.IsNullOrWhiteSpace(status))
        {
            FirstRunStatusText.Text = status;
        }
    }

    private void UpdateFirstRunProgress(DataWorkerProgress progress)
    {
        if (progress.Stage == "transfer")
        {
            var received = progress.BytesReceived is { } bytes
                ? $"{bytes / 1024d / 1024d:0.0} MB"
                : progress.Message;
            var total = progress.TotalBytes is { } totalBytes
                ? $" / {totalBytes / 1024d / 1024d:0.0} MB"
                : "";
            FirstRunTransferText.Text = $"当前文件 {received}{total}";
            return;
        }

        if (!string.IsNullOrWhiteSpace(progress.Message))
        {
            FirstRunStatusText.Text = progress.Message;
        }

        if (progress.Stage == "probe" && progress.Percent is { } probePercent)
        {
            var value = Math.Clamp(probePercent, 0, 35);
            FirstRunProgressBar.Value = value;
            FirstRunProgressValueText.Text = $"{value:0}%";
            FirstRunProgressCountText.Text = "正在寻找最新起报";
            FirstRunTransferText.Text = "自动跳过尚未发布的候选时刻";
            return;
        }

        if (progress.Stage == "download")
        {
            FirstRunProgressBar.Value = 40;
            FirstRunProgressValueText.Text = "40%";
            FirstRunProgressCountText.Text = "已找到最新起报";
            FirstRunTransferText.Text = "开始准备 15 天完整预报";
            return;
        }

        if (progress.Stage is "file" or "file-complete")
        {
            _firstRunCurrentItem = progress.CurrentItem ?? _firstRunCurrentItem;
            var totalItems = progress.TotalItems ?? 120;
            var filePercent = Math.Clamp(progress.Percent ?? 0, 0, 100);
            var overallPercent = 40 + filePercent * 0.6;
            FirstRunProgressBar.Value = overallPercent;
            FirstRunProgressValueText.Text = $"{overallPercent:0}%";
            FirstRunProgressCountText.Text =
                $"{_firstRunCurrentItem} / {totalItems} 个时次";
            FirstRunTransferText.Text = progress.Stage == "file"
                ? "正在下载并解析当前预报文件"
                : "当前预报文件已完成本地校验";
        }
    }

    private void HideFirstRunOverlayIfReady(ForecastIndex index)
    {
        if (!App.Services.CurrentSettings.FirstRunSetupCompleted
            || !HasCompleteForecastSeries(index))
        {
            return;
        }

        FirstRunOverlay.Visibility = Visibility.Collapsed;
        FirstRunInfo.IsOpen = false;
        FirstRunProgressRing.IsActive = false;
        TryCompleteStartupCover();
        ((Application.Current as App)?.MainWindow as MainWindow)?.TryShowPendingUpdate();
    }

    private string GetFirstRunModel() =>
        (FirstRunModelPicker.SelectedItem as ComboBoxItem)?.Content?.ToString()
        ?? "AISky-Energy";

    private static bool HasCompleteForecastSeries(ForecastIndex index)
    {
        const int expectedRuns = 120;
        return index.Runs
            .GroupBy(run => new { run.Model, run.InitKey })
            .Any(group =>
            {
                var leads = group.Select(run => run.LeadHours).ToHashSet();
                return Enumerable.Range(1, expectedRuns)
                    .All(item => leads.Contains(item * 3));
            });
    }

    private async void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        await ShowSettingsDialogAsync();
    }

    private async Task ShowSettingsDialogAsync()
    {
        var dialog = new SettingsDialog(App.Services.CurrentSettings)
        {
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            await App.Services.UpdateSettingsAsync(dialog.SelectedSettings);
            if ((Application.Current as App)?.MainWindow is MainWindow mainWindow)
            {
                mainWindow.ApplyUpdateCheckSchedule();
            }
            if (dialog.DataRootChanged)
            {
                FirstRunOverlay.Visibility = Visibility.Collapsed;
                StartupStatusText.Text = "正在准备迁移预报数据";
                StartupProgressBar.IsIndeterminate = true;
                StartupCover.Visibility = Visibility.Visible;
                var migrationProgress = new Progress<string>(message =>
                    StartupStatusText.Text = message);
                await App.Services.PrepareDataRootMigrationAsync(
                    dialog.SelectedDataRoot,
                    migrationProgress);
                StartupStatusText.Text = "迁移完成，正在重新启动 AISky";
                if ((Application.Current as App)?.MainWindow is MainWindow restartWindow)
                {
                    restartWindow.RestartAfterDataMove();
                    return;
                }
                throw new InvalidOperationException("数据迁移完成，但主窗口无法重新启动。");
            }
            _suppressAutoSync = true;
            AutoSyncButton.IsChecked = dialog.SelectedSettings.AutoSyncEnabled;
            _suppressAutoSync = false;
            _suppressDisplaySettings = true;
            WindAnimationToggle.IsOn = dialog.SelectedSettings.ShowWindAnimation;
            _suppressDisplaySettings = false;
            ApplyBackgroundStatus(App.Services.BackgroundSync.CurrentStatus);
            SendDisplayOptionsToMap();
            await App.Services.Log.WriteAsync(
                "INFO",
                $"Settings updated: autoSync={dialog.SelectedSettings.AutoSyncEnabled}, retentionDays={dialog.SelectedSettings.CacheRetentionDays}, keepInTray={dialog.SelectedSettings.KeepRunningInTray}, startWithWindows={dialog.SelectedSettings.StartWithWindows}, mapOpacity={dialog.SelectedSettings.MapLayerOpacity:F2}, dataRoot={App.Services.Paths.Root}.");
        }
        catch (Exception exception)
        {
            ServiceStatusTitle.Text = "设置未保存";
            ServiceStatusText.Text = exception.Message;
            SetStatusDot("AISkyErrorBrush");
            StartupCover.Visibility = Visibility.Collapsed;
            await App.Services.Log.WriteAsync("ERROR", $"Settings update failed: {exception}");
        }
    }

    private void RootLayout_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (RightRail is null || LayerPanel is null || LayerList is null || ColorBarPanel is null)
        {
            return;
        }

        var width = e.NewSize.Width;
        var height = e.NewSize.Height;
        var colorBarVisible = height >= 640 && width >= 760;
        ColorBarPanel.Visibility = colorBarVisible
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async void AutoSyncButton_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressAutoSync)
        {
            return;
        }

        var enabled = AutoSyncButton.IsChecked == true;
        await App.Services.UpdateSettingsAsync(
            App.Services.CurrentSettings with { AutoSyncEnabled = enabled });
        ApplyBackgroundStatus(App.Services.BackgroundSync.CurrentStatus);
        await App.Services.Log.WriteAsync(
            "INFO",
            enabled ? "Automatic sync enabled from command bar." : "Automatic sync paused from command bar.");
    }

    private async void WindAnimationToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressDisplaySettings)
        {
            return;
        }

        await App.Services.UpdateSettingsAsync(
            App.Services.CurrentSettings with { ShowWindAnimation = WindAnimationToggle.IsOn });
        SendDisplayOptionsToMap();
    }

    private async void TyphoonPathToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressDisplaySettings)
        {
            return;
        }

        await App.Services.UpdateSettingsAsync(
            App.Services.CurrentSettings with { ShowTyphoonPaths = TyphoonPathToggle.IsOn });
        TyphoonPathToggle.OnContent = TyphoonPathToggle.IsOn
            ? "正在识别"
            : "分析本地模式";
        SendDisplayOptionsToMap();
    }

    private async void SyncNowButton_Click(object sender, RoutedEventArgs e)
    {
        var result = await App.Services.BackgroundSync.SyncNowAsync();
        if (result is null
            && App.Services.BackgroundSync.CurrentStatus.State == BackgroundSyncState.Syncing)
        {
            ServiceStatusText.Text = "同步任务已经在运行，请稍候";
        }
    }

    private async void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        await ShowUpdateDialogAsync();
    }

    public async Task ShowUpdateDialogAsync()
    {
        ServiceStatusTitle.Text = "软件更新";
        ServiceStatusText.Text = $"正在检查 AISky {VersionInfo.CurrentVersion} 的软件更新";
        SetStatusDot("AISkySuccessBrush");
        var dialog = new UpdateDialog
        {
            XamlRoot = XamlRoot,
        };
        await dialog.ShowAsync();
        ApplyBackgroundStatus(App.Services.BackgroundSync.CurrentStatus);
    }

    private void CompactRunButton_Click(object sender, RoutedEventArgs e)
    {
        var nextModel = GetSelectedModel() == "AISky-Energy" ? "AISky-SDS" : "AISky-Energy";
        var index = ModelPicker.Items
            .OfType<ComboBoxItem>()
            .Select((item, itemIndex) => new { Item = item, Index = itemIndex })
            .FirstOrDefault(item => item.Item.Content?.ToString() == nextModel)?.Index;
        if (index is { } selectedIndex)
        {
            ModelPicker.SelectedIndex = selectedIndex;
        }
    }

    private void GlobeButton_Click(object sender, RoutedEventArgs e) =>
        PostMapMessage(new { type = "reset-view" });

    private async void TimeZoneButton_Click(object sender, RoutedEventArgs e)
    {
        var selector = new ComboBox
        {
            Header = "软件显示时区",
            MinWidth = 320,
        };
        for (var offset = -12; offset <= 14; offset++)
        {
            var zone = offset == 0 ? "UTC" : $"UTC{offset:+#;-#}";
            var note = offset switch
            {
                0 => "（默认）",
                8 => "（中国标准时间）",
                _ => "",
            };
            selector.Items.Add(new ComboBoxItem
            {
                Content = $"{zone} {note}".TrimEnd(),
                Tag = offset,
            });
        }
        selector.SelectedIndex = _displayUtcOffsetHours + 12;

        var content = new StackPanel
        {
            Spacing = 10,
        };
        content.Children.Add(selector);
        content.Children.Add(new TextBlock
        {
            Text = "起报、预报、时间轴与格点序列会统一使用所选时区；原始数据仍按 UTC 保存。",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.72,
        });
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "显示时区",
            Content = content,
            PrimaryButtonText = "应用",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary
            || selector.SelectedItem is not ComboBoxItem { Tag: int offsetHours })
        {
            return;
        }

        var selectedRunId = _selectedRun?.Id;
        var selectedModel = GetSelectedModel();
        var selectedInit = _selectedRun?.InitKey;
        _displayUtcOffsetHours = offsetHours;
        await App.Services.UpdateSettingsAsync(
            App.Services.CurrentSettings with { DisplayUtcOffsetHours = offsetHours });
        TimeZoneButton.Label = $"显示时区 · {DisplayTimeZoneLabel}";
        ApplyIndex(_index, selectedModel, selectedInit);
        var selectedRun = _currentForecastRuns.FirstOrDefault(run => run.Id == selectedRunId);
        if (selectedRun is not null)
        {
            SelectRun(selectedRun, updateForecastPicker: true);
        }
        SendDisplayOptionsToMap();
        ApplyBackgroundStatus(App.Services.BackgroundSync.CurrentStatus);
    }

    private async void FillSeriesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedRun is null)
        {
            return;
        }

        await App.Services.BackgroundSync.FillForecastSeriesAsync(
            _selectedRun.Model,
            _selectedRun.InitKey);
    }

    private void ThemeToggle_Changed(object sender, RoutedEventArgs e)
    {
        var isDark = sender is ToggleButton { IsChecked: true };
        RootLayout.RequestedTheme = isDark ? ElementTheme.Dark : ElementTheme.Light;
        SendThemeToMap();
        ServiceStatusText.Text = isDark ? "已切换为深色观测界面" : "已切换为明亮观测界面";
    }

    private void AttachBackgroundEvents()
    {
        if (_backgroundEventsAttached)
        {
            return;
        }

        _backgroundEventsAttached = true;
        App.Services.BackgroundSync.StatusChanged += BackgroundSync_StatusChanged;
        App.Services.BackgroundSync.IndexUpdated += BackgroundSync_IndexUpdated;
    }

    private void MainPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _playbackTimer.Stop();
        _modelStatusTicker.Stop();
        if (!_backgroundEventsAttached)
        {
            return;
        }

        _backgroundEventsAttached = false;
        App.Services.BackgroundSync.StatusChanged -= BackgroundSync_StatusChanged;
        App.Services.BackgroundSync.IndexUpdated -= BackgroundSync_IndexUpdated;
    }

    private void BackgroundSync_StatusChanged(object? sender, BackgroundSyncStatus status)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ApplyBackgroundStatus(status);
            if (FirstRunOverlay.Visibility == Visibility.Visible)
            {
                FirstRunStatusText.Text = status.Message;
            }
        });
    }

    private void BackgroundSync_IndexUpdated(object? sender, ForecastIndex index)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var preferredModel = GetSelectedModel();
            var preferredInit = _selectedRun?.InitKey;
            ApplyIndex(index, preferredModel, preferredInit);
            ApplyBackgroundStatus(App.Services.BackgroundSync.CurrentStatus);
            HideFirstRunOverlayIfReady(index);
        });
    }

    private void ApplyBackgroundStatus(BackgroundSyncStatus status)
    {
        _lastBackgroundStatus = status;
        _suppressAutoSync = true;
        AutoSyncButton.IsChecked = status.AutoSyncEnabled;
        _suppressAutoSync = false;
        SyncNowButton.IsEnabled =
            status.State is not BackgroundSyncState.Syncing
                and not BackgroundSyncState.Cleaning;
        RenderBackgroundStatusLine(status);
        if (!status.CanCancel && status.ModelStatuses is { Count: > 1 })
        {
            _modelStatusTicker.Start();
        }
        else
        {
            _modelStatusTicker.Stop();
        }
        SetStatusDot(status.State switch
        {
            BackgroundSyncState.Syncing or BackgroundSyncState.Cleaning => "AISkyWarningBrush",
            BackgroundSyncState.Error => "AISkyErrorBrush",
            _ => "AISkySuccessBrush",
        });
        var isBusy = status.State is BackgroundSyncState.Syncing
            or BackgroundSyncState.Cleaning;
        BackgroundSyncProgressBar.Visibility = isBusy
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (isBusy)
        {
            BackgroundSyncProgressBar.IsIndeterminate = status.ProgressPercent is null;
            if (status.ProgressPercent is { } progress)
            {
                BackgroundSyncProgressBar.Value = Math.Clamp(progress, 0, 100);
            }
        }
        CancelBackgroundTaskButton.Visibility = status.CanCancel
            ? Visibility.Visible
            : Visibility.Collapsed;
        CancelBackgroundTaskCommand.Visibility = status.CanCancel
            ? Visibility.Visible
            : Visibility.Collapsed;
        CancelBackgroundTaskButton.IsEnabled = status.CanCancel;
        CancelBackgroundTaskCommand.IsEnabled = status.CanCancel;
        UpdateSyncModelItems(status);
    }

    private void SyncStatusFlyout_Opening(object? sender, object e) =>
        UpdateSyncModelItems(
            _lastBackgroundStatus ?? App.Services.BackgroundSync.CurrentStatus);

    private void UpdateSyncModelItems(BackgroundSyncStatus status)
    {
        SyncModelItems.Clear();
        var statuses = status.ModelStatuses is { Count: > 0 }
            ? status.ModelStatuses
            : new[]
            {
                new ModelSyncProgress(
                    "AISky-Energy",
                    ModelSyncState.Queued,
                    "等待下一次同步"),
                new ModelSyncProgress(
                    "AISky-SDS",
                    ModelSyncState.Queued,
                    "等待下一次同步"),
            };
        foreach (var item in statuses)
        {
            var stateText = item.State switch
            {
                ModelSyncState.Queued => "等待",
                ModelSyncState.Checking => "正在检查",
                ModelSyncState.Downloading => "正在下载",
                ModelSyncState.Complete => "已完成",
                ModelSyncState.Skipped => "已缓存",
                ModelSyncState.NoData => "暂无数据",
                ModelSyncState.Error => "失败",
                _ => "状态",
            };
            var resourceKey = item.State switch
            {
                ModelSyncState.Downloading or ModelSyncState.Checking => "AISkyWarningBrush",
                ModelSyncState.Error => "AISkyErrorBrush",
                ModelSyncState.Complete or ModelSyncState.Skipped => "AISkySuccessBrush",
                _ => "AISkyChromeBorderBrush",
            };
            var brush = Application.Current.Resources[resourceKey] as Brush
                ?? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 100, 128, 137));
            var progress = Math.Clamp(item.ProgressPercent ?? 0, 0, 100);
            var detail = item.CurrentItem is { } current && item.TotalItems is { } total
                ? $"{item.Message} · {current}/{total}"
                : item.Message;
            var speed = item.BytesPerSecond > 1024
                ? $"{BackgroundSyncService.FormatBytes((long)item.BytesPerSecond)}/s"
                : "";
            SyncModelItems.Add(new SyncModelStatusItem(
                item.Model.Replace("AISky-", "", StringComparison.Ordinal),
                stateText,
                detail,
                speed,
                progress,
                item.ProgressPercent is null
                    && item.State is ModelSyncState.Checking or ModelSyncState.Downloading,
                brush));
        }

        SyncFlyoutSummaryText.Text = FormatBackgroundStatusMessage(status);
    }

    private void ModelStatusTicker_Tick(object? sender, object e)
    {
        if (_lastBackgroundStatus?.ModelStatuses is not { Count: > 1 } statuses)
        {
            _modelStatusTicker.Stop();
            return;
        }
        _modelStatusDisplayIndex = (_modelStatusDisplayIndex + 1) % statuses.Count;
        RenderBackgroundStatusLine(_lastBackgroundStatus);
        AnimateModelStatusLine();
    }

    private void AnimateModelStatusLine()
    {
        var storyboard = new Storyboard();
        var slide = new DoubleAnimation
        {
            From = 9,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(360),
            EasingFunction = new QuadraticEase
            {
                EasingMode = EasingMode.EaseOut,
            },
        };
        Storyboard.SetTarget(slide, ModelStatusLine);
        Storyboard.SetTargetProperty(
            slide,
            "(UIElement.RenderTransform).(TranslateTransform.Y)");

        var fade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(280),
        };
        Storyboard.SetTarget(fade, ModelStatusLine);
        Storyboard.SetTargetProperty(fade, "Opacity");

        storyboard.Children.Add(slide);
        storyboard.Children.Add(fade);
        storyboard.Begin();
    }

    private void RenderBackgroundStatusLine(BackgroundSyncStatus status)
    {
        if (!status.CanCancel && status.ModelStatuses is { Count: > 0 } statuses)
        {
            var item = statuses[_modelStatusDisplayIndex % statuses.Count];
            ServiceStatusTitle.Text = item.Model.Replace("AISky-", "", StringComparison.Ordinal);
            var state = item.State switch
            {
                ModelSyncState.Queued => "等待",
                ModelSyncState.Checking => "检查",
                ModelSyncState.Downloading => "下载",
                ModelSyncState.Complete => "完成",
                ModelSyncState.Skipped => "已缓存",
                ModelSyncState.NoData => "暂无数据",
                ModelSyncState.Error => "失败",
                _ => "状态",
            };
            var speed = item.BytesPerSecond > 1024
                ? $" · {BackgroundSyncService.FormatBytes((long)item.BytesPerSecond)}/s"
                : "";
            ServiceStatusText.Text = $"{state} · {item.Message}{speed}";
            return;
        }

        ServiceStatusTitle.Text = status.OperationLabel
            ?? (status.State switch
            {
                BackgroundSyncState.Syncing => "正在同步",
                BackgroundSyncState.Cleaning => "缓存维护",
                BackgroundSyncState.Error => "需要处理",
                BackgroundSyncState.Scheduled => "后台同步",
                _ => "本地数据服务",
            });
        ServiceStatusText.Text = FormatBackgroundStatusMessage(status);
    }

    private void SetStatusDot(string resourceKey)
    {
        if (Application.Current.Resources[resourceKey] is Brush brush)
        {
            ServiceStatusDot.Fill = brush;
        }
    }

    private void ModelPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection || InitPicker is null || ForecastPicker is null)
        {
            return;
        }
        _modelSelectionMadeByUser = true;
        PopulateInitPicker(GetSelectedModel());
    }

    private void SelectTyphoonForecast(JsonElement message)
    {
        var model = message.TryGetProperty("model", out var modelNode)
            ? modelNode.GetString()
            : null;
        var initKey = message.TryGetProperty("initKey", out var initNode)
            ? initNode.GetString()
            : null;
        var forecastKey = message.TryGetProperty("forecastKey", out var forecastNode)
            ? forecastNode.GetString()
            : null;
        var leadHours = message.TryGetProperty("leadHours", out var leadNode)
            ? leadNode.GetInt32()
            : int.MinValue;
        if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(initKey))
        {
            return;
        }

        var target = _index.Runs.FirstOrDefault(run =>
            string.Equals(run.Model, model, StringComparison.Ordinal)
            && string.Equals(run.InitKey, initKey, StringComparison.Ordinal)
            && (string.Equals(run.ForecastKey, forecastKey, StringComparison.Ordinal)
                || run.LeadHours == leadHours));
        if (target is null)
        {
            ServiceStatusText.Text = $"{model} 暂无 +{leadHours}h 本地预报";
            return;
        }

        _modelSelectionMadeByUser = true;
        ApplyIndex(_index, model, initKey);
        var selected = _currentForecastRuns.FirstOrDefault(run => run.Id == target.Id);
        if (selected is not null)
        {
            SelectRun(selected, updateForecastPicker: true);
        }
    }

    private void SelectPointForecast(JsonElement message)
    {
        var forecastKey = message.TryGetProperty("forecastKey", out var forecastNode)
            ? forecastNode.GetString()
            : null;
        var leadHours = message.TryGetProperty("leadHours", out var leadNode)
            ? leadNode.GetInt32()
            : int.MinValue;
        var selected = _currentForecastRuns.FirstOrDefault(run =>
            string.Equals(run.ForecastKey, forecastKey, StringComparison.Ordinal)
            || run.LeadHours == leadHours);
        if (selected is null)
        {
            ServiceStatusText.Text = $"当前起报暂无 +{leadHours}h 本地预报";
            return;
        }

        SelectRun(selected, updateForecastPicker: true);
    }

    private void InitPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection)
        {
            return;
        }
        var initKey = (InitPicker.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        PopulateForecastPicker(GetSelectedModel(), initKey);
    }

    private void ForecastPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection)
        {
            return;
        }
        if ((ForecastPicker.SelectedItem as ComboBoxItem)?.Tag is ForecastRun run)
        {
            SelectRun(run, updateForecastPicker: false);
        }
    }

    private void LayerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LayerList.SelectedItem is not LayerItem layer)
        {
            return;
        }
        UpdateColorBar(layer);
        UpdateProductContext();
        PostMapMessage(new { type = "set-layer", layer = layer.Id });
    }

    private void ColorPaletteFlyout_Opening(object? sender, object e)
    {
        if (LayerList.SelectedItem is not LayerItem layer)
        {
            ColorPaletteFlyout.Hide();
            return;
        }

        PaletteFlyoutTitle.Text = $"{layer.Code} · {layer.Name}";
        PaletteFlyoutHint.Text =
            $"仅应用于当前变量，并自动保存 · {ColorPaletteCatalog.All.Count} 种可选色带";
        PaletteSearchBox.Text = "";
        var preference = GetLayerPalettePreference(layer.Id);
        PopulatePaletteOptions(layer, preference);
    }

    private void PaletteSearchBox_TextChanged(
        AutoSuggestBox sender,
        AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput
            || LayerList.SelectedItem is not LayerItem layer)
        {
            return;
        }

        PopulatePaletteOptions(layer, GetLayerPalettePreference(layer.Id));
    }

    private async void PaletteList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPaletteSelection
            || LayerList.SelectedItem is not LayerItem layer
            || PaletteList.SelectedItem is not ColorPaletteOption option)
        {
            return;
        }

        await SaveLayerPaletteAsync(
            layer,
            option.IsAutomatic ? "" : option.Id,
            ReversePaletteToggle.IsOn);
    }

    private async void ReversePaletteToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressPaletteSelection
            || LayerList.SelectedItem is not LayerItem layer)
        {
            return;
        }

        var preference = GetLayerPalettePreference(layer.Id);
        var selectedId = (PaletteList.SelectedItem as ColorPaletteOption)?.Id
            ?? preference.PaletteId;
        await SaveLayerPaletteAsync(
            layer,
            selectedId,
            ReversePaletteToggle.IsOn);
        PopulatePaletteOptions(layer, GetLayerPalettePreference(layer.Id));
    }

    private void PopulatePaletteOptions(
        LayerItem layer,
        LayerPalettePreference preference)
    {
        _suppressPaletteSelection = true;
        try
        {
            var searchText = PaletteSearchBox.Text.Trim();
            PaletteOptions.Clear();
            if (string.IsNullOrEmpty(searchText)
                || "跟随变量默认".Contains(searchText, StringComparison.OrdinalIgnoreCase)
                || "default".Contains(searchText, StringComparison.OrdinalIgnoreCase))
            {
                PaletteOptions.Add(new ColorPaletteOption(
                    "",
                    "跟随变量默认",
                    ApplyPaletteDirection(layer.Palette, preference.Reversed),
                    isAutomatic: true));
            }

            foreach (var palette in ColorPaletteCatalog.All.Where(palette =>
                         string.IsNullOrEmpty(searchText)
                         || palette.Name.Contains(searchText, StringComparison.OrdinalIgnoreCase)
                         || palette.Id.Contains(searchText, StringComparison.OrdinalIgnoreCase)))
            {
                PaletteOptions.Add(new ColorPaletteOption(
                    palette.Id,
                    palette.Name,
                    ApplyPaletteDirection(palette.Colors, preference.Reversed)));
            }

            PaletteList.SelectedItem = PaletteOptions.FirstOrDefault(option =>
                string.Equals(
                    option.Id,
                    preference.PaletteId,
                    StringComparison.OrdinalIgnoreCase))
                ?? (string.IsNullOrEmpty(searchText)
                    ? PaletteOptions.FirstOrDefault()
                    : null);
            ReversePaletteToggle.IsOn = preference.Reversed;
        }
        finally
        {
            _suppressPaletteSelection = false;
        }
    }

    private async Task SaveLayerPaletteAsync(
        LayerItem layer,
        string paletteId,
        bool reversed)
    {
        await _paletteSettingsGate.WaitAsync();
        try
        {
            var settings = App.Services.CurrentSettings;
            var preferences = new Dictionary<string, LayerPalettePreference>(
                settings.LayerPalettes ?? [],
                StringComparer.OrdinalIgnoreCase);
            var key = layer.Id.ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(paletteId) && !reversed)
            {
                preferences.Remove(key);
            }
            else
            {
                preferences[key] = new LayerPalettePreference
                {
                    PaletteId = paletteId,
                    Reversed = reversed,
                };
            }

            await App.Services.UpdateSettingsAsync(
                settings with { LayerPalettes = preferences });
            ApplyLayerPalette(layer);
        }
        catch (Exception exception)
        {
            ServiceStatusText.Text = "色带设置保存失败";
            await App.Services.Log.WriteAsync("ERROR", $"Palette update failed: {exception}");
        }
        finally
        {
            _paletteSettingsGate.Release();
        }
    }

    private void ApplyLayerPalette(LayerItem layer)
    {
        var palette = ResolveLayerPalette(layer.Id, layer.Palette);
        UpdateColorBar(layer);
        _lastMapSeriesKey = null;
        PostMapMessage(new
        {
            type = "set-palette",
            layer = layer.Id,
            palette,
            paletteOverride = HasLayerPaletteOverride(layer.Id),
        });
        ServiceStatusText.Text =
            $"{layer.Code} 已使用 {ColorPaletteModeText.Text} 色带";
    }

    private void LayerSearchBox_TextChanged(
        AutoSuggestBox sender,
        AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            ApplyLayerFilter();
        }
    }

    private void LayerCategoryPicker_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e) =>
        ApplyLayerFilter();

    private void ApplyLayerFilter(string? preferredLayerId = null)
    {
        if (LayerList is null
            || LayerSearchBox is null
            || LayerCategoryPicker is null
            || LayerCountText is null
            || LayerHint is null)
        {
            return;
        }

        var selectedLayerId = preferredLayerId
            ?? (LayerList.SelectedItem as LayerItem)?.Id;
        var search = LayerSearchBox.Text.Trim();
        var category =
            (LayerCategoryPicker.SelectedItem as ComboBoxItem)?.Tag as string
            ?? "全部";
        var filtered = _allLayerItems.Where(layer =>
            (category == "全部" || layer.Category == category)
            && (search.Length == 0
                || layer.Code.Contains(search, StringComparison.OrdinalIgnoreCase)
                || layer.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                || layer.Category.Contains(search, StringComparison.CurrentCultureIgnoreCase)))
            .OrderBy(layer => layer.Code, StringComparer.OrdinalIgnoreCase)
            .ToList();

        LayerItems.Clear();
        foreach (var layer in filtered)
        {
            LayerItems.Add(layer);
        }

        var filterActive = category != "全部" || search.Length > 0;
        LayerCountText.Text = filterActive
            ? $"{filtered.Count}/{_allLayerItems.Count}"
            : _allLayerItems.Count.ToString(CultureInfo.InvariantCulture);
        LayerHint.Text = filtered.Count == 0
            ? "没有匹配的变量，请调整关键词或分类"
            : "颜色和值域随产品自动更新";
        var selectedIndex = LayerItems
            .Select((layer, index) => new { layer.Id, Index = index })
            .FirstOrDefault(item => item.Id == selectedLayerId)?.Index ?? 0;
        LayerList.SelectedIndex = LayerItems.Count == 0 ? -1 : selectedIndex;
    }

    private void TimelineSlot_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int index }
            || index < 0
            || index >= _currentForecastRuns.Count)
        {
            return;
        }
        SelectRun(_currentForecastRuns[index], updateForecastPicker: true);
    }

    private void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentForecastRuns.Count < 2)
        {
            ServiceStatusText.Text = "当前起报只有一个预报时刻，无法播放";
            return;
        }
        SetPlaybackState(!_isPlaying);
    }

    private void PlaybackTimer_Tick(object? sender, object e)
    {
        if (_currentForecastRuns.Count < 2)
        {
            SetPlaybackState(false);
            return;
        }
        StepForecast(1, wrap: true);
    }

    private void StepForecast(int delta, bool wrap)
    {
        if (_currentForecastRuns.Count == 0 || delta == 0)
        {
            return;
        }

        var next = _currentForecastIndex + Math.Sign(delta);
        next = wrap
            ? (next + _currentForecastRuns.Count) % _currentForecastRuns.Count
            : Math.Clamp(next, 0, _currentForecastRuns.Count - 1);
        if (next == _currentForecastIndex)
        {
            ServiceStatusText.Text = delta < 0
                ? "已经是第一个预报时刻"
                : "已经是最后一个预报时刻";
            return;
        }

        SelectRun(_currentForecastRuns[next], updateForecastPicker: true);
    }

    private void ApplyIndex(
        ForecastIndex index,
        string? preferredModel = null,
        string? preferredInit = null)
    {
        _index = index;
        preferredModel ??= GetSelectedModel();
        var availableModels = index.Runs.Select(run => run.Model).Distinct().ToHashSet();
        if (!_modelSelectionMadeByUser
            && !availableModels.Contains(preferredModel)
            && availableModels.Count > 0)
        {
            preferredModel = index.Runs[0].Model;
        }

        _suppressSelection = true;
        try
        {
            var modelIndex = ModelPicker.Items
                .OfType<ComboBoxItem>()
                .Select((item, itemIndex) => new { Item = item, Index = itemIndex })
                .FirstOrDefault(item => item.Item.Content?.ToString() == preferredModel)?.Index ?? 0;
            ModelPicker.SelectedIndex = modelIndex;
        }
        finally
        {
            _suppressSelection = false;
        }
        PopulateInitPicker(preferredModel, preferredInit);
    }

    private void PopulateInitPicker(string model, string? preferredInit = null)
    {
        var initKeys = _index.Runs
            .Where(run => run.Model == model)
            .Select(run => run.InitKey)
            .Distinct()
            .OrderByDescending(key => key)
            .ToList();

        _suppressSelection = true;
        try
        {
            InitPicker.Items.Clear();
            foreach (var key in initKeys)
            {
                InitPicker.Items.Add(new ComboBoxItem
                {
                    Content = FormatTimeKey(key, "yyyy-MM-dd HH:mm"),
                    Tag = key,
                });
            }
            var selection = Math.Max(0, initKeys.FindIndex(key => key == preferredInit));
            InitPicker.SelectedIndex = initKeys.Count == 0 ? -1 : selection;
        }
        finally
        {
            _suppressSelection = false;
        }

        PopulateForecastPicker(
            model,
            (InitPicker.SelectedItem as ComboBoxItem)?.Tag?.ToString());
    }

    private void PopulateForecastPicker(string model, string? initKey)
    {
        _currentForecastRuns = _index.Runs
            .Where(run => run.Model == model && run.InitKey == initKey)
            .OrderBy(run => run.LeadHours)
            .ToList();
        BuildTimelineSlots();

        _suppressSelection = true;
        try
        {
            ForecastPicker.Items.Clear();
            foreach (var run in _currentForecastRuns)
            {
                ForecastPicker.Items.Add(new ComboBoxItem
                {
                    Content = $"{FormatLead(run.LeadHours)} · {FormatTimeKey(run.ForecastKey, "MM-dd HH:mm")}",
                    Tag = run,
                });
            }
            ForecastPicker.SelectedIndex = _currentForecastRuns.Count == 0 ? -1 : 0;
        }
        finally
        {
            _suppressSelection = false;
        }

        if (_currentForecastRuns.Count > 0)
        {
            SelectRun(_currentForecastRuns[0], updateForecastPicker: false);
        }
        else
        {
            SelectRun(null, updateForecastPicker: false);
        }
    }

    private void BuildTimelineSlots()
    {
        if (TimelineDaysHost is null)
        {
            return;
        }

        TimelineDaysHost.Children.Clear();
        _timelineSlotButtons.Clear();
        _lastTimelineSelectionIndex = -1;
        foreach (var dayGroup in _currentForecastRuns
                     .Select((run, index) => new { Run = run, Index = index })
                     .GroupBy(item => item.Index / 8))
        {
            var dayPanel = new StackPanel
            {
                Spacing = 3,
            };
            dayPanel.Children.Add(new TextBlock
            {
                Text = $"第 {dayGroup.Key + 1} 组 · {FormatTimeKey(dayGroup.First().Run.ForecastKey, "MM-dd")}",
                FontSize = 10,
                Opacity = 0.72,
            });

            var slots = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 3,
            };
            foreach (var item in dayGroup)
            {
                var hour = FormatTimeKey(item.Run.ForecastKey, "HH");
                var button = new Button
                {
                    Tag = item.Index,
                    Content = hour,
                    FontSize = 10,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Style = (Style)Resources["TimelineSlotButtonStyle"],
                };
                button.Click += TimelineSlot_Click;
                ToolTipService.SetToolTip(
                    button,
                    $"{FormatTimeKey(item.Run.ForecastKey, "MM-dd HH:mm")} {DisplayTimeZoneLabel} · {FormatLead(item.Run.LeadHours)}");
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                    button,
                    $"选择 {FormatTimeKey(item.Run.ForecastKey, "MM-dd HH:mm")} {DisplayTimeZoneLabel}");
                slots.Children.Add(button);
                _timelineSlotButtons.Add(button);
            }
            dayPanel.Children.Add(slots);
            TimelineDaysHost.Children.Add(dayPanel);
        }
    }

    private void UpdateTimelineSelection()
    {
        if (_lastTimelineSelectionIndex >= 0
            && _lastTimelineSelectionIndex < _timelineSlotButtons.Count
            && _lastTimelineSelectionIndex != _currentForecastIndex)
        {
            _timelineSlotButtons[_lastTimelineSelectionIndex].Style =
                (Style)Resources["TimelineSlotButtonStyle"];
        }

        if (_currentForecastIndex < 0
            || _currentForecastIndex >= _timelineSlotButtons.Count)
        {
            _lastTimelineSelectionIndex = -1;
            return;
        }

        var selectedButton = _timelineSlotButtons[_currentForecastIndex];
        selectedButton.Style = (Style)Resources["TimelineSlotButtonSelectedStyle"];
        selectedButton.StartBringIntoView();
        _lastTimelineSelectionIndex = _currentForecastIndex;
    }

    private void SelectRun(ForecastRun? run, bool updateForecastPicker)
    {
        _selectedRun = run;
        if (run is null)
        {
            _allLayerItems.Clear();
            LayerItems.Clear();
            LayerCountText.Text = "0";
            LeadText.Text = "--";
            TimelineSummaryText.Text = "暂无可用时次";
            ForecastTimeText.Text = "暂无预报数据";
            FirstForecastText.Text = "--";
            LastForecastText.Text = "--";
            FillSeriesButton.Visibility = Visibility.Collapsed;
            CompactRunButton.Content = $"{GetSelectedModel()} · 暂无数据";
            _lastLayerSetKey = null;
            _lastMapSeriesKey = null;
            UpdateEmptyColorBar();
            UpdateProductContext();
            SendSelectedRunToMap();
            return;
        }

        if (updateForecastPicker)
        {
            _suppressSelection = true;
            try
            {
                ForecastPicker.SelectedIndex = _currentForecastRuns.FindIndex(item => item.Id == run.Id);
            }
            finally
            {
                _suppressSelection = false;
            }
        }

        var layerSetKey =
            $"{run.Model}|{run.InitKey}|{string.Join(',', run.Layers.Select(layer => layer.Id))}";
        if (!string.Equals(_lastLayerSetKey, layerSetKey, StringComparison.Ordinal))
        {
            var previousLayer = (LayerList.SelectedItem as LayerItem)?.Id;
            _allLayerItems.Clear();
            foreach (var layer in run.Layers
                         .OrderBy(layer => layer.Label, StringComparer.OrdinalIgnoreCase))
            {
                _allLayerItems.Add(LayerItem.FromForecastLayer(layer));
            }
            ApplyLayerFilter(previousLayer);
            _lastLayerSetKey = layerSetKey;
        }

        var forecastIndex = Math.Max(0, _currentForecastRuns.FindIndex(item => item.Id == run.Id));
        _currentForecastIndex = forecastIndex;
        UpdateTimelineSelection();
        LeadText.Text = FormatLead(run.LeadHours);
        TimelineSummaryText.Text = _currentForecastRuns.Count == 1
            ? "仅 1 个时次 · 可补齐序列"
            : $"{_currentForecastRuns.Count} 个预报时刻 · 3 小时间隔";
        FillSeriesButton.Visibility = _currentForecastRuns.Count == 1
            ? Visibility.Visible
            : Visibility.Collapsed;
        ForecastTimeText.Text =
            $"{FormatTimeKey(run.ForecastKey, "MM-dd HH:mm")} · {FormatLead(run.LeadHours)} {DisplayTimeZoneLabel}";
        FirstForecastText.Text = _currentForecastRuns.Count == 0
            ? "--"
            : FormatTimeKey(_currentForecastRuns[0].ForecastKey, "MM-dd HH:mm");
        LastForecastText.Text = _currentForecastRuns.Count == 0
            ? "--"
            : FormatTimeKey(_currentForecastRuns[^1].ForecastKey, "MM-dd HH:mm");
        CompactRunButton.Content = $"{run.Model} · {FormatLead(run.LeadHours)}";
        ServiceStatusText.Text =
            $"{run.Model} · 起报 {FormatTimeKey(run.InitKey, "MM-dd HH:mm")} {DisplayTimeZoneLabel} · {run.Version}";
        UpdateProductContext();
        SendSelectedRunToMap();
    }

    private void UpdateProductContext()
    {
        if (_selectedRun is null)
        {
            ProductStatusText.Text = $"{GetSelectedModel()} · 暂无数据";
            LayerContextText.Text = "暂无可用时次";
            ToolTipService.SetToolTip(UpdateStatus, null);
            return;
        }

        var layer = LayerList.SelectedItem as LayerItem;
        var code = layer?.Code ?? _selectedRun.Layers.FirstOrDefault()?.Label ?? "预报产品";
        ProductStatusText.Text = $"{code} · {FormatLead(_selectedRun.LeadHours)}";
        LayerContextText.Text =
            $"{_selectedRun.Model} · {FormatLead(_selectedRun.LeadHours)}";
        ToolTipService.SetToolTip(
            UpdateStatus,
            $"{code} {layer?.Name} · {_selectedRun.Model} · 起报 {FormatTimeKey(_selectedRun.InitKey, "MM-dd HH:mm")} {DisplayTimeZoneLabel}");
    }

    private void SendSelectedRunToMap(bool forceFull = false)
    {
        if (!_mapReady)
        {
            return;
        }
        if (_selectedRun is null)
        {
            _lastMapSeriesKey = null;
            PostMapMessage(new { type = "set-data", run = (object?)null });
            return;
        }

        var run = _selectedRun;
        var selectedLayer = (LayerList.SelectedItem as LayerItem)?.Id
            ?? run.Layers.FirstOrDefault()?.Id;
        var nextRun = _currentForecastRuns.Count > 1
            ? _currentForecastRuns[(_currentForecastIndex + 1) % _currentForecastRuns.Count]
            : null;
        var seriesKey =
            $"{run.Model}|{run.InitKey}|{_currentForecastRuns.Count}|{_currentForecastRuns.FirstOrDefault()?.Id}|{_currentForecastRuns.LastOrDefault()?.Id}|{TyphoonSeriesKey()}";
        if (!forceFull && string.Equals(_lastMapSeriesKey, seriesKey, StringComparison.Ordinal))
        {
            PostMapMessage(new
            {
                type = "set-frame",
                layer = selectedLayer,
                run = CreateMapRun(run),
                nextRun = nextRun is null ? null : CreateMapRun(nextRun),
            });
            return;
        }

        _lastMapSeriesKey = seriesKey;
        PostMapMessage(new
        {
            type = "set-data",
            layer = selectedLayer,
            run = CreateMapRun(run),
            nextRun = nextRun is null ? null : CreateMapRun(nextRun),
            typhoonModels = CreateTyphoonModelSeries(),
            series = _currentForecastRuns.Select(seriesRun => new
            {
                id = seriesRun.Id,
                model = seriesRun.Model,
                version = seriesRun.Version,
                initKey = seriesRun.InitKey,
                forecastKey = seriesRun.ForecastKey,
                leadHours = seriesRun.LeadHours,
                grid = new
                {
                    lat = GridExtent(seriesRun.Grid.Latitude),
                    lon = GridExtent(seriesRun.Grid.Longitude),
                    rows = seriesRun.Grid.Rows,
                    cols = seriesRun.Grid.Columns,
                },
                layers = seriesRun.Layers.Select(CreateMapSeriesLayer),
            }),
        });
    }

    private object CreateMapSeriesLayer(ForecastLayer layer)
    {
        var display = LayerDisplayCatalog.Resolve(layer);
        return new
        {
            id = layer.Id,
            label = layer.Label,
            cn = layer.Name,
            unit = display.Unit,
            range = new[] { display.Minimum, display.Maximum },
            displayScale = display.Scale,
            displayOffset = display.Offset,
            palette = ResolveLayerPalette(layer.Id, layer.Palette),
            paletteOverride = HasLayerPaletteOverride(layer.Id),
            sampleUrl = string.IsNullOrWhiteSpace(layer.Sample)
                ? null
                : BuildDataUrl(layer.Sample),
        };
    }

    private object[] CreateTyphoonModelSeries()
    {
        var result = new List<object>();
        var selectedInitKey = _selectedRun?.InitKey;
        if (string.IsNullOrWhiteSpace(selectedInitKey))
        {
            return result.ToArray();
        }

        foreach (var model in new[] { "AISky-Energy", "AISky-SDS" })
        {
            var runs = _index.Runs
                .Where(run =>
                    string.Equals(run.Model, model, StringComparison.Ordinal)
                    && string.Equals(run.InitKey, selectedInitKey, StringComparison.Ordinal))
                .OrderBy(run => run.LeadHours)
                .Select(run =>
                {
                    var slp = run.Layers.FirstOrDefault(layer => layer.Id == "slp");
                    var wind = run.Layers.FirstOrDefault(layer => layer.Id == "wind10");
                    if (slp is null || wind is null
                        || string.IsNullOrWhiteSpace(slp.Sample)
                        || string.IsNullOrWhiteSpace(wind.Sample))
                    {
                        return null;
                    }
                    return (object)new
                    {
                        id = run.Id,
                        forecastKey = run.ForecastKey,
                        leadHours = run.LeadHours,
                        grid = new
                        {
                            lat = GridExtent(run.Grid.Latitude),
                            lon = GridExtent(run.Grid.Longitude),
                        },
                        slpSampleUrl = BuildDataUrl(slp.Sample),
                        windSampleUrl = BuildDataUrl(wind.Sample),
                    };
                })
                .Where(run => run is not null)
                .Cast<object>()
                .ToArray();
            if (runs.Length < 3)
            {
                continue;
            }
            result.Add(new { model, initKey = selectedInitKey, runs });
        }
        return result.ToArray();
    }

    private string TyphoonSeriesKey()
    {
        var selectedInitKey = _selectedRun?.InitKey ?? "";
        return string.Join(
            "|",
            new[] { "AISky-Energy", "AISky-SDS" }.Select(model =>
            {
                var runs = _index.Runs
                    .Where(run =>
                        string.Equals(run.Model, model, StringComparison.Ordinal)
                        && string.Equals(run.InitKey, selectedInitKey, StringComparison.Ordinal))
                    .OrderBy(run => run.LeadHours)
                    .ToList();
                return
                    $"{model}:{selectedInitKey}:{runs.Count}:{runs.FirstOrDefault()?.Id}:{runs.LastOrDefault()?.Id}";
            }));
    }

    private object CreateMapRun(ForecastRun run) =>
        new
        {
            id = run.Id,
            model = run.Model,
            version = run.Version,
            initKey = run.InitKey,
            forecastKey = run.ForecastKey,
            leadHours = run.LeadHours,
            grid = new
            {
                lat = GridExtent(run.Grid.Latitude),
                lon = GridExtent(run.Grid.Longitude),
                rows = run.Grid.Rows,
                cols = run.Grid.Columns,
            },
            layers = run.Layers.Select(layer =>
            {
                var display = LayerDisplayCatalog.Resolve(layer);
                return new
                {
                id = layer.Id,
                label = layer.Label,
                cn = layer.Name,
                unit = display.Unit,
                range = new[] { display.Minimum, display.Maximum },
                displayScale = display.Scale,
                displayOffset = display.Offset,
                palette = ResolveLayerPalette(layer.Id, layer.Palette),
                paletteOverride = HasLayerPaletteOverride(layer.Id),
                fieldUrl = BuildDataUrl(layer.Field),
                sampleUrl = string.IsNullOrWhiteSpace(layer.Sample)
                    ? null
                    : BuildDataUrl(layer.Sample),
                fieldInfo = new
                {
                    rows = layer.FieldInfo.Rows,
                    cols = layer.FieldInfo.Columns,
                    missing = layer.FieldInfo.Missing,
                    range = layer.FieldInfo.Range,
                },
                vector = layer.Vector is null
                    ? null
                    : new
                    {
                        uUrl = BuildDataUrl(layer.Vector.U),
                        vUrl = BuildDataUrl(layer.Vector.V),
                        fieldInfo = new
                        {
                            rows = layer.Vector.FieldInfo.Rows,
                            cols = layer.Vector.FieldInfo.Columns,
                            missing = layer.Vector.FieldInfo.Missing,
                            range = layer.Vector.FieldInfo.Range,
                        },
                    },
                };
            }),
        };

    private void SendThemeToMap() =>
        PostMapMessage(new
        {
            type = "set-theme",
            theme = DarkThemeToggle.IsChecked == true ? "dark" : "light",
        });

    private void SendDisplayOptionsToMap()
    {
        if (!_mapReady)
        {
            return;
        }
        var settings = App.Services.CurrentSettings;
        PostMapMessage(new
        {
            type = "set-display",
            opacity = settings.MapLayerOpacity,
            showGrid = settings.ShowMapGrid,
            showPlaces = settings.ShowMapPlaces,
            windAnimation = settings.ShowWindAnimation,
            typhoonPaths = settings.ShowTyphoonPaths,
            utcOffsetHours = _displayUtcOffsetHours,
        });
    }

    private void UpdateColorBar(LayerItem layer)
    {
        var preference = GetLayerPalettePreference(layer.Id);
        var palette = ResolveLayerPalette(layer.Id, layer.Palette);
        ColorMinimumText.Text = FormatValue(layer.Range.FirstOrDefault(), layer.Unit);
        ColorMaximumText.Text = FormatValue(layer.Range.LastOrDefault(), layer.Unit);
        ColorLayerText.Text = layer.Name;
        ColorUnitText.Text = string.IsNullOrWhiteSpace(layer.Unit)
            ? layer.Code
            : $"{layer.Code} · {layer.Unit}";
        var definition = ColorPaletteCatalog.Find(preference.PaletteId);
        ColorPaletteModeText.Text =
            $"{definition?.Name ?? "变量默认"}{(preference.Reversed ? " · 反转" : "")}";
        ColorBarButton.IsEnabled = true;
        ColorBarGradient.GradientStops.Clear();
        for (var index = 0; index < palette.Count; index++)
        {
            ColorBarGradient.GradientStops.Add(new GradientStop
            {
                Color = LayerItem.ParseColor(palette[index]),
                Offset = palette.Count == 1 ? 0 : index / (double)(palette.Count - 1),
            });
        }
    }

    private void UpdateEmptyColorBar()
    {
        ColorMinimumText.Text = "--";
        ColorMaximumText.Text = "--";
        ColorLayerText.Text = "等待数据";
        ColorUnitText.Text = "";
        ColorPaletteModeText.Text = "变量默认";
        ColorBarButton.IsEnabled = false;
    }

    private static List<string> ApplyPaletteDirection(
        IReadOnlyList<string> colors,
        bool reversed) =>
        reversed ? colors.Reverse().ToList() : colors.ToList();

    private LayerPalettePreference GetLayerPalettePreference(string layerId)
    {
        var preferences = App.Services.CurrentSettings.LayerPalettes;
        if (preferences is not null
            && preferences.TryGetValue(layerId.ToLowerInvariant(), out var preference))
        {
            return preference;
        }
        if (preferences is not null)
        {
            var pair = preferences.FirstOrDefault(item =>
                string.Equals(item.Key, layerId, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(pair.Key))
            {
                return pair.Value;
            }
        }
        return new LayerPalettePreference();
    }

    private bool HasLayerPaletteOverride(string layerId)
    {
        var preference = GetLayerPalettePreference(layerId);
        return preference.Reversed
            || ColorPaletteCatalog.Find(preference.PaletteId) is not null;
    }

    private List<string> ResolveLayerPalette(
        string layerId,
        IReadOnlyList<string> defaultPalette)
    {
        var preference = GetLayerPalettePreference(layerId);
        var selected = ColorPaletteCatalog.Find(preference.PaletteId);
        return ApplyPaletteDirection(
            selected?.Colors ?? defaultPalette,
            preference.Reversed);
    }

    private void SetPlaybackState(bool isPlaying)
    {
        _isPlaying = isPlaying;
        PlaybackPlayIcon.Visibility = isPlaying
            ? Visibility.Collapsed
            : Visibility.Visible;
        PlaybackPauseIcon.Visibility = isPlaying
            ? Visibility.Visible
            : Visibility.Collapsed;
        ToolTipService.SetToolTip(PlayButton, isPlaying ? "暂停" : "播放");
        if (isPlaying)
        {
            _playbackTimer.Start();
            ServiceStatusText.Text = "正在播放本地预报时间序列";
        }
        else
        {
            _playbackTimer.Stop();
            ServiceStatusText.Text = "时间播放已暂停";
        }
    }

    private string GetSelectedModel() =>
        (ModelPicker.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "AISky-Energy";

    private static string BuildDataUrl(string relativePath) =>
        "https://aisky-data.local/" + string.Join(
            "/",
            relativePath.Replace('\\', '/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.EscapeDataString));

    private static double[] GridExtent(IReadOnlyList<double> values) =>
        values.Count == 0 ? [] : [values[0], values[^1]];

    private string FormatTimeKey(string key, string format) =>
        DateTimeOffset.TryParseExact(
            key,
            "yyyyMMdd_HHmm",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var value)
            ? value.ToOffset(TimeSpan.FromHours(_displayUtcOffsetHours))
                .ToString(format, CultureInfo.InvariantCulture)
            : key;

    private string DisplayTimeZoneLabel =>
        _displayUtcOffsetHours == 0
            ? "UTC"
            : $"UTC{_displayUtcOffsetHours:+#;-#}";

    private string FormatBackgroundStatusMessage(BackgroundSyncStatus status)
    {
        if (status.State != BackgroundSyncState.Scheduled
            || status.NextRunUtc is not { } nextRunUtc)
        {
            return status.Message;
        }

        var nextRun = nextRunUtc
            .ToUniversalTime()
            .ToOffset(TimeSpan.FromHours(_displayUtcOffsetHours))
            .ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
        return $"{status.Message} · 下次检查 {nextRun} {DisplayTimeZoneLabel}";
    }

    private static string FormatLead(int leadHours) =>
        leadHours >= 0 ? $"+{leadHours}h" : $"{leadHours}h";

    private static string FormatValue(double value, string unit) =>
        string.IsNullOrWhiteSpace(unit)
            ? $"{value:0.##}"
            : $"{value:0.##} {unit}";

    private static string FriendlyDataError(Exception exception)
    {
        if (exception.Message.Contains("NetCDF 无法读取", StringComparison.OrdinalIgnoreCase))
        {
            return "文件已经损坏或不完整，请重新下载后再试。";
        }
        if (exception.Message.Contains("缺少", StringComparison.OrdinalIgnoreCase))
        {
            return $"{exception.Message} 请检查 Python 数据组件。";
        }
        return exception.Message;
    }

    private static void WriteFallbackStartupError(Exception exception)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "AISky-startup-diagnostic.log"),
                $"{DateTimeOffset.Now:O}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // The visible first-run error remains the final fallback.
        }
    }

    private void PostMapMessage(object payload)
    {
        if (!_mapReady || MapWebView.CoreWebView2 is null)
        {
            return;
        }
        MapWebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload));
    }

    private void TryCompleteStartupCover()
    {
        if (!_startupDataReady || !_mapReady)
        {
            return;
        }

        if (((Application.Current as App)?.MainWindow as MainWindow)?.IsHiddenToTray == true)
        {
            return;
        }

        AnimateStartupCoverAway();
    }

    public bool PrepareTrayReveal()
    {
        if (!_mapReady)
        {
            return false;
        }

        _trayRevealPrepared = true;
        StartupProgressBar.Visibility = Visibility.Collapsed;
        StartupStatusText.Text = "正在恢复预报工作区";
        StartupCover.Opacity = 1;
        StartupContent.Opacity = 1;
        StartupContentTransform.ScaleX = 0.97;
        StartupContentTransform.ScaleY = 0.97;
        StartupCover.Visibility = Visibility.Visible;
        return true;
    }

    public bool HasPreparedTrayReveal =>
        _mapReady
        && (_trayRevealPrepared || StartupCover.Visibility == Visibility.Visible);

    public void PlayTrayReveal()
    {
        _trayRevealPrepared = false;
        AnimateStartupCoverAway(420);
    }

    public void RequestAutomaticTutorial(int delayMilliseconds = 180)
    {
        if (_tutorialQueued || _tutorialOpen)
        {
            return;
        }

        _tutorialQueued = true;
        _ = ShowAutomaticTutorialAfterDelayAsync(delayMilliseconds);
    }

    private async Task ShowAutomaticTutorialAfterDelayAsync(int delayMilliseconds)
    {
        try
        {
            await Task.Delay(Math.Max(0, delayMilliseconds));
            if (XamlRoot is null || StartupCover.Visibility == Visibility.Visible)
            {
                return;
            }
            await ShowTutorialAsync(automatic: true);
        }
        finally
        {
            _tutorialQueued = false;
        }
    }

    private async Task ShowTutorialAsync(bool automatic)
    {
        await App.Services.InitializeAsync();
        var settings = App.Services.CurrentSettings;
        if (_tutorialOpen || (automatic && !settings.ShowTutorialOnOpen))
        {
            ((Application.Current as App)?.MainWindow as MainWindow)
                ?.TryShowPendingUpdate();
            return;
        }

        _tutorialOpen = true;
        try
        {
            var dialog = new TutorialDialog(settings.ShowTutorialOnOpen)
            {
                XamlRoot = XamlRoot,
                RequestedTheme = RootLayout.ActualTheme,
            };
            await dialog.ShowAsync();
            var showOnOpen = !dialog.SuppressAutomaticOpening;
            if (showOnOpen != App.Services.CurrentSettings.ShowTutorialOnOpen)
            {
                await App.Services.UpdateSettingsAsync(
                    App.Services.CurrentSettings with
                    {
                        ShowTutorialOnOpen = showOnOpen,
                    });
            }
        }
        catch (InvalidOperationException exception)
        {
            await App.Services.Log.WriteAsync(
                "WARN",
                $"Tutorial dialog was deferred because another dialog is open: {exception.Message}");
        }
        finally
        {
            _tutorialOpen = false;
            ((Application.Current as App)?.MainWindow as MainWindow)
                ?.TryShowPendingUpdate();
        }
    }

    private void AnimateStartupCoverAway(int beginDelayMilliseconds = 100)
    {
        if (StartupCover.Visibility != Visibility.Visible)
        {
            return;
        }

        var storyboard = new Storyboard();
        var easing = new ExponentialEase
        {
            Exponent = 6,
            EasingMode = EasingMode.EaseOut,
        };
        var coverFade = new DoubleAnimation
        {
            From = 1,
            To = 0,
            BeginTime = TimeSpan.FromMilliseconds(beginDelayMilliseconds),
            Duration = TimeSpan.FromMilliseconds(240),
            EasingFunction = easing,
        };
        Storyboard.SetTarget(coverFade, StartupCover);
        Storyboard.SetTargetProperty(coverFade, "Opacity");
        storyboard.Children.Add(coverFade);

        foreach (var property in new[] { "ScaleX", "ScaleY" })
        {
            var scale = new DoubleAnimation
            {
                From = 0.97,
                To = 1.015,
                BeginTime = TimeSpan.FromMilliseconds(beginDelayMilliseconds),
                Duration = TimeSpan.FromMilliseconds(280),
                EasingFunction = easing,
            };
            Storyboard.SetTarget(scale, StartupContentTransform);
            Storyboard.SetTargetProperty(scale, property);
            storyboard.Children.Add(scale);
        }

        storyboard.Completed += (_, _) =>
        {
            StartupCover.Visibility = Visibility.Collapsed;
            StartupCover.Opacity = 1;
            StartupContent.Opacity = 1;
            StartupContentTransform.ScaleX = 1;
            StartupContentTransform.ScaleY = 1;
            StartupProgressBar.Visibility = Visibility.Visible;
            StartupStatusText.Text = "正在准备本地预报环境";
            RequestAutomaticTutorial();
        };
        storyboard.Begin();
    }
}

public sealed class LayerItem
{
    public string Id { get; set; } = "";
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Unit { get; set; } = "";
    public string Category { get; set; } = "基础场";
    public List<double> Range { get; set; } = [];
    public List<string> Palette { get; set; } = [];
    public string Thumbnail { get; set; } = "";
    public Brush Brush { get; set; } = new SolidColorBrush(ParseColor("#4682B4"));

    public static LayerItem FromForecastLayer(ForecastLayer layer)
    {
        var palette = layer.Palette.Count > 0
            ? layer.Palette
            : ["#0B78B6", "#2DB6C8"];
        var display = LayerDisplayCatalog.Resolve(layer);
        return new LayerItem
        {
            Id = layer.Id,
            Code = layer.Label,
            Name = layer.Name,
            Unit = display.Unit,
            Category = Categorize(layer.Label),
            Range = [display.Minimum, display.Maximum],
            Palette = palette,
            Thumbnail = $"ms-appx:///Assets/Layers/{layer.Id}.png",
            Brush = CreatePaletteBrush(palette, diagonal: true),
        };
    }

    private static string Categorize(string code)
    {
        var normalized = code.ToUpperInvariant();
        if (normalized is "QV2M" or "QV10M" or "TQV" or "PRECTOT")
        {
            return "水分降水";
        }
        if (normalized is "CLDTOT" or "CLDLOW" or "CLDMID" or "CLDHGH"
            or "SWGDN" or "SWGDNCLR" or "SWTDN" or "SWGNT" or "ALBEDO")
        {
            return "云与辐射";
        }
        if (normalized.StartsWith("DU", StringComparison.Ordinal)
            || normalized is "TAUTOT" or "TOTEXTTAU")
        {
            return "沙尘气溶胶";
        }
        if (normalized is "GWETTOP" or "FRSNO" or "LAI" or "Z0M")
        {
            return "陆面";
        }
        return "基础场";
    }

    public static Brush CreatePaletteBrush(
        IReadOnlyList<string> colors,
        bool diagonal = false)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = diagonal
                ? new Windows.Foundation.Point(1, 1)
                : new Windows.Foundation.Point(1, 0),
        };
        for (var index = 0; index < colors.Count; index++)
        {
            brush.GradientStops.Add(new GradientStop
            {
                Color = ParseColor(colors[index]),
                Offset = colors.Count == 1 ? 0 : index / (double)(colors.Count - 1),
            });
        }
        return brush;
    }

    public static Windows.UI.Color ParseColor(string value) =>
        Windows.UI.Color.FromArgb(
            255,
            Convert.ToByte(value[1..3], 16),
            Convert.ToByte(value[3..5], 16),
            Convert.ToByte(value[5..7], 16));
}

public sealed class ColorPaletteOption
{
    public ColorPaletteOption(
        string id,
        string name,
        IReadOnlyList<string> colors,
        bool isAutomatic = false)
    {
        Id = id;
        Name = name;
        IsAutomatic = isAutomatic;
        PreviewBrush = LayerItem.CreatePaletteBrush(colors);
    }

    public string Id { get; }
    public string Name { get; }
    public bool IsAutomatic { get; }
    public Brush PreviewBrush { get; }
}

public sealed class SyncModelStatusItem(
    string modelName,
    string stateText,
    string detailText,
    string speedText,
    double progress,
    bool isIndeterminate,
    Brush statusBrush)
{
    public string ModelName { get; } = modelName;
    public string StateText { get; } = stateText;
    public string DetailText { get; } = detailText;
    public string SpeedText { get; } = speedText;
    public double Progress { get; } = progress;
    public bool IsIndeterminate { get; } = isIndeterminate;
    public Brush StatusBrush { get; } = statusBrush;
}
