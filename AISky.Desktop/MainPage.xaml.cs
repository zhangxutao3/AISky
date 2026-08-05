using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using AISky_Desktop.DataWorker;
using AISky_Desktop.Infrastructure;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using Windows.Storage.Pickers;

namespace AISky_Desktop;

public sealed partial class MainPage : Page
{
    private readonly DispatcherTimer _playbackTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(720),
    };

    private ForecastIndex _index = new();
    private List<ForecastRun> _currentForecastRuns = [];
    private ForecastRun? _selectedRun;
    private bool _mapReady;
    private bool _suppressSelection;
    private bool _suppressAutoSync;
    private bool _backgroundEventsAttached;
    private bool _isPlaying;
    private bool _firstRunBusy;

    public ObservableCollection<LayerItem> LayerItems { get; } = [];

    public MainPage()
    {
        InitializeComponent();
        _playbackTimer.Tick += PlaybackTimer_Tick;
        Unloaded += MainPage_Unloaded;
    }

    private async void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await App.Services.InitializeAsync();
            AttachBackgroundEvents();
            _suppressAutoSync = true;
            AutoSyncButton.IsChecked = App.Services.CurrentSettings.AutoSyncEnabled;
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
            if (_index.Runs.Count == 0)
            {
                ShowFirstRunOverlay();
                if (!string.IsNullOrWhiteSpace(FirstRunPasswordInput.Password))
                {
                    await ProbeFirstRunAsync();
                }
            }
        }
        catch (Exception exception)
        {
            ServiceStatusText.Text = "本地数据服务初始化失败";
            MapLoadingOverlay.Visibility = Visibility.Collapsed;
            ShowFirstRunOverlay(
                $"本地数据服务未能启动：{FriendlyDataError(exception)}",
                InfoBarSeverity.Error);
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
                SendSelectedRunToMap();
                await App.Services.Log.WriteAsync("INFO", "Map host reported ready.");
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
            if (root.TryGetProperty("type", out var type)
                && type.GetString() == "map-error")
            {
                var message = root.TryGetProperty("message", out var messageNode)
                    ? messageNode.GetString() ?? "未知错误"
                    : "未知错误";
                ServiceStatusText.Text = $"地图数据加载失败：{message}";
                await App.Services.Log.WriteAsync("ERROR", $"MapHost: {message}");
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
            IndexUpdated = async index =>
            {
                _index = index;
                ApplyIndex(index);
                ServiceStatusText.Text = $"下载完成，索引现有 {index.Runs.Count} 个预报时刻";
                HideFirstRunOverlayIfReady(index);
                await App.Services.Log.WriteAsync("INFO", "Backfill download completed and index refreshed.");
            },
        };
        await dialog.ShowAsync();
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

    private void FirstRunPasswordInput_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (!_firstRunBusy)
        {
            FirstRunProbeButton.IsEnabled =
                !string.IsNullOrWhiteSpace(FirstRunPasswordInput.Password);
        }
    }

    private async Task ProbeFirstRunAsync()
    {
        if (_firstRunBusy)
        {
            return;
        }

        var password = FirstRunPasswordInput.Password.Trim();
        if (string.IsNullOrWhiteSpace(password))
        {
            ShowFirstRunMessage("请输入数据访问密码，或导入一个已有的 NetCDF 文件。", InfoBarSeverity.Warning);
            FirstRunPasswordInput.Focus(FocusState.Programmatic);
            return;
        }

        SetFirstRunBusy(true, "正在从最近 3 天寻找可用起报");
        FirstRunInfo.IsOpen = false;
        try
        {
            await App.Services.UpdateSettingsAsync(
                App.Services.CurrentSettings with { DataAccessPassword = password });
            var result = await App.Services.BackgroundSync.SyncNowAsync();
            if (result is not null)
            {
                _index = result;
                ApplyIndex(result);
                if (result.Runs.Count > 0)
                {
                    FirstRunStatusText.Text = $"已准备 {result.Runs.Count} 个预报时次";
                    HideFirstRunOverlayIfReady(result);
                    await App.Services.Log.WriteAsync(
                        "INFO",
                        $"First-run preparation completed with {result.Runs.Count} run(s).");
                    return;
                }
            }

            var status = App.Services.BackgroundSync.CurrentStatus;
            FirstRunStatusText.Text = "暂未获得可用预报";
            ShowFirstRunMessage(
                status.IsError
                    ? status.Message
                    : "最近 3 天暂未发现可用起报。请检查网络与密码后重试，或导入本地 NetCDF。",
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
        FirstRunOverlay.Visibility = Visibility.Visible;
        try
        {
            FirstRunPasswordInput.Password = App.Services.CurrentSettings.DataAccessPassword;
        }
        catch
        {
            // The overlay must stay usable even when service construction failed.
        }
        FirstRunStatusText.Text = "等待获取首个有效预报";
        FirstRunProbeButton.IsEnabled =
            !string.IsNullOrWhiteSpace(FirstRunPasswordInput.Password);
        if (!string.IsNullOrWhiteSpace(message))
        {
            ShowFirstRunMessage(message, severity);
        }
        else
        {
            FirstRunInfo.IsOpen = false;
        }

        if (string.IsNullOrWhiteSpace(FirstRunPasswordInput.Password))
        {
            FirstRunPasswordInput.Focus(FocusState.Programmatic);
        }
        else
        {
            FirstRunProbeButton.Focus(FocusState.Programmatic);
        }
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
        FirstRunPasswordInput.IsEnabled = !isBusy;
        FirstRunProbeButton.IsEnabled =
            !isBusy && !string.IsNullOrWhiteSpace(FirstRunPasswordInput.Password);
        FirstRunImportButton.IsEnabled = !isBusy;
        if (!string.IsNullOrWhiteSpace(status))
        {
            FirstRunStatusText.Text = status;
        }
    }

    private void HideFirstRunOverlayIfReady(ForecastIndex index)
    {
        if (index.Runs.Count == 0)
        {
            return;
        }

        FirstRunOverlay.Visibility = Visibility.Collapsed;
        FirstRunInfo.IsOpen = false;
        FirstRunProgressRing.IsActive = false;
    }

    private async void SettingsButton_Click(object sender, RoutedEventArgs e)
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
            _suppressAutoSync = true;
            AutoSyncButton.IsChecked = dialog.SelectedSettings.AutoSyncEnabled;
            _suppressAutoSync = false;
            ApplyBackgroundStatus(App.Services.BackgroundSync.CurrentStatus);
            SendDisplayOptionsToMap();
            await App.Services.Log.WriteAsync(
                "INFO",
                $"Settings updated: autoSync={dialog.SelectedSettings.AutoSyncEnabled}, retentionDays={dialog.SelectedSettings.CacheRetentionDays}, keepInTray={dialog.SelectedSettings.KeepRunningInTray}, startWithWindows={dialog.SelectedSettings.StartWithWindows}, mapOpacity={dialog.SelectedSettings.MapLayerOpacity:F2}.");
        }
        catch (Exception exception)
        {
            ServiceStatusTitle.Text = "设置未保存";
            ServiceStatusText.Text = exception.Message;
            SetStatusDot("AISkyErrorBrush");
            await App.Services.Log.WriteAsync("ERROR", $"Settings update failed: {exception}");
        }
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
        ServiceStatusTitle.Text = "本地数据服务";
        ServiceStatusText.Text = App.Services.BackgroundSync.CurrentStatus.Message;
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
        _suppressAutoSync = true;
        AutoSyncButton.IsChecked = status.AutoSyncEnabled;
        _suppressAutoSync = false;
        SyncNowButton.IsEnabled =
            status.State is not BackgroundSyncState.Syncing
                and not BackgroundSyncState.Cleaning;
        ServiceStatusTitle.Text = status.State switch
        {
            BackgroundSyncState.Syncing => "正在同步",
            BackgroundSyncState.Cleaning => "缓存维护",
            BackgroundSyncState.Error => "需要处理",
            BackgroundSyncState.Scheduled => "后台同步",
            _ => "本地数据服务",
        };
        SetStatusDot(status.State switch
        {
            BackgroundSyncState.Syncing or BackgroundSyncState.Cleaning => "AISkyWarningBrush",
            BackgroundSyncState.Error => "AISkyErrorBrush",
            _ => "AISkySuccessBrush",
        });
        ServiceStatusText.Text = status.Message;
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
        PopulateInitPicker(GetSelectedModel());
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
        PostMapMessage(new { type = "set-layer", layer = layer.Id });
    }

    private void TimelineSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressSelection || _currentForecastRuns.Count == 0)
        {
            return;
        }
        var index = Math.Clamp((int)Math.Round(e.NewValue), 0, _currentForecastRuns.Count - 1);
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
        var next = ((int)TimelineSlider.Value + 1) % _currentForecastRuns.Count;
        TimelineSlider.Value = next;
    }

    private void ApplyIndex(
        ForecastIndex index,
        string? preferredModel = null,
        string? preferredInit = null)
    {
        _index = index;
        preferredModel ??= GetSelectedModel();
        var availableModels = index.Runs.Select(run => run.Model).Distinct().ToHashSet();
        if (!availableModels.Contains(preferredModel) && availableModels.Count > 0)
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
                    Content = FormatUtcKey(key, "yyyy-MM-dd HH:mm"),
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

        _suppressSelection = true;
        try
        {
            ForecastPicker.Items.Clear();
            foreach (var run in _currentForecastRuns)
            {
                ForecastPicker.Items.Add(new ComboBoxItem
                {
                    Content = $"{FormatLead(run.LeadHours)} · {FormatUtcKey(run.ForecastKey, "MM-dd HH:mm")}",
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

    private void SelectRun(ForecastRun? run, bool updateForecastPicker)
    {
        _selectedRun = run;
        if (run is null)
        {
            LayerItems.Clear();
            LayerCountText.Text = "0";
            LeadText.Text = "--";
            TimelineSummaryText.Text = "暂无可用时次";
            ForecastTimeText.Text = "暂无预报数据";
            FirstForecastText.Text = "--";
            LastForecastText.Text = "--";
            TimelineSlider.Maximum = 0;
            CompactRunButton.Content = $"{GetSelectedModel()} · 暂无数据";
            UpdateEmptyColorBar();
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

        var previousLayer = (LayerList.SelectedItem as LayerItem)?.Id;
        LayerItems.Clear();
        foreach (var layer in run.Layers)
        {
            LayerItems.Add(LayerItem.FromForecastLayer(layer));
        }
        LayerCountText.Text = LayerItems.Count.ToString(CultureInfo.InvariantCulture);
        var layerIndex = LayerItems
            .Select((layer, index) => new { layer.Id, Index = index })
            .FirstOrDefault(item => item.Id == previousLayer)?.Index ?? 0;
        LayerList.SelectedIndex = LayerItems.Count == 0 ? -1 : layerIndex;

        var forecastIndex = Math.Max(0, _currentForecastRuns.FindIndex(item => item.Id == run.Id));
        _suppressSelection = true;
        try
        {
            TimelineSlider.Maximum = Math.Max(0, _currentForecastRuns.Count - 1);
            TimelineSlider.Value = forecastIndex;
        }
        finally
        {
            _suppressSelection = false;
        }
        LeadText.Text = FormatLead(run.LeadHours);
        TimelineSummaryText.Text = _currentForecastRuns.Count == 1
            ? "1 个预报时刻"
            : $"{_currentForecastRuns.Count} 个预报时刻 · 3 小时间隔";
        ForecastTimeText.Text =
            $"{FormatUtcKey(run.ForecastKey, "MM-dd HH:mm")} · {FormatLead(run.LeadHours)} UTC";
        FirstForecastText.Text = _currentForecastRuns.Count == 0
            ? "--"
            : FormatUtcKey(_currentForecastRuns[0].ForecastKey, "MM-dd HH:mm");
        LastForecastText.Text = _currentForecastRuns.Count == 0
            ? "--"
            : FormatUtcKey(_currentForecastRuns[^1].ForecastKey, "MM-dd HH:mm");
        CompactRunButton.Content = $"{run.Model} · {FormatLead(run.LeadHours)}";
        ServiceStatusText.Text =
            $"{run.Model} · 起报 {FormatUtcKey(run.InitKey, "MM-dd HH:mm")} UTC · {run.Version}";
        SendSelectedRunToMap();
    }

    private void SendSelectedRunToMap()
    {
        if (!_mapReady)
        {
            return;
        }
        if (_selectedRun is null)
        {
            PostMapMessage(new { type = "set-data", run = (object?)null });
            return;
        }

        var run = _selectedRun;
        var selectedLayer = (LayerList.SelectedItem as LayerItem)?.Id
            ?? run.Layers.FirstOrDefault()?.Id;
        PostMapMessage(new
        {
            type = "set-data",
            layer = selectedLayer,
            run = new
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
                layers = run.Layers.Select(layer => new
                {
                    id = layer.Id,
                    label = layer.Label,
                    cn = layer.Name,
                    unit = layer.Unit,
                    range = layer.Range,
                    palette = layer.Palette,
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
                }),
            },
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
                layers = seriesRun.Layers.Select(layer => new
                {
                    id = layer.Id,
                    label = layer.Label,
                    cn = layer.Name,
                    unit = layer.Unit,
                    palette = layer.Palette,
                    sampleUrl = string.IsNullOrWhiteSpace(layer.Sample)
                        ? null
                        : BuildDataUrl(layer.Sample),
                }),
            }),
        });
    }

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
        });
    }

    private void UpdateColorBar(LayerItem layer)
    {
        ColorMinimumText.Text = FormatValue(layer.Range.FirstOrDefault(), layer.Unit);
        ColorMaximumText.Text = FormatValue(layer.Range.LastOrDefault(), layer.Unit);
        ColorLayerText.Text = layer.Name;
        ColorUnitText.Text = string.IsNullOrWhiteSpace(layer.Unit)
            ? layer.Code
            : $"{layer.Code} · {layer.Unit}";
        ColorBarGradient.GradientStops.Clear();
        for (var index = 0; index < layer.Palette.Count; index++)
        {
            ColorBarGradient.GradientStops.Add(new GradientStop
            {
                Color = LayerItem.ParseColor(layer.Palette[index]),
                Offset = layer.Palette.Count == 1 ? 0 : index / (double)(layer.Palette.Count - 1),
            });
        }
    }

    private void UpdateEmptyColorBar()
    {
        ColorMinimumText.Text = "--";
        ColorMaximumText.Text = "--";
        ColorLayerText.Text = "等待数据";
        ColorUnitText.Text = "";
    }

    private void SetPlaybackState(bool isPlaying)
    {
        _isPlaying = isPlaying;
        PlayButton.Content = isPlaying ? "\uE769" : "\uE768";
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

    private static string FormatUtcKey(string key, string format) =>
        DateTimeOffset.TryParseExact(
            key,
            "yyyyMMdd_HHmm",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var value)
            ? value.ToString(format, CultureInfo.InvariantCulture)
            : key;

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
}

public sealed class LayerItem
{
    public string Id { get; set; } = "";
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Unit { get; set; } = "";
    public List<double> Range { get; set; } = [];
    public List<string> Palette { get; set; } = [];
    public Brush Brush { get; set; } = new SolidColorBrush(ParseColor("#4682B4"));

    public static LayerItem FromForecastLayer(ForecastLayer layer)
    {
        var palette = layer.Palette.Count > 0
            ? layer.Palette
            : ["#0B78B6", "#2DB6C8"];
        return new LayerItem
        {
            Id = layer.Id,
            Code = layer.Label,
            Name = layer.Name,
            Unit = layer.Unit,
            Range = layer.Range,
            Palette = palette,
            Brush = CreateBrush(palette),
        };
    }

    private static Brush CreateBrush(IReadOnlyList<string> colors)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 1),
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
