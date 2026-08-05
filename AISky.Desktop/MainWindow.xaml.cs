using AISky_Desktop.Infrastructure;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace AISky_Desktop;

/// <summary>
/// The application window. This hosts a Frame that displays pages. Add your
/// UI and logic to MainPage.xaml / MainPage.xaml.cs instead of here so you
/// can use Page features such as navigation events and the Loaded lifecycle.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly bool _startInTray;
    private TrayIconService? _trayIcon;
    private bool _allowExit;
    private bool _hiddenToTray;
    private bool _trayHintShown;
    private bool _backgroundOperationWasRunning;

    public MainWindow(bool startInTray = false)
    {
        _startInTray = startInTray;
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Resize(new SizeInt32(1440, 900));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Maximize();
        }
        AppWindow.Closing += AppWindow_Closing;
        Closed += MainWindow_Closed;

        RootFrame.Navigate(typeof(MainPage));
        _ = InitializeBackgroundExperienceAsync();
    }

    private async Task InitializeBackgroundExperienceAsync()
    {
        try
        {
            await App.Services.InitializeAsync();
            var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            _trayIcon = new TrayIconService(windowHandle);
            _trayIcon.OpenRequested += (_, _) => ShowFromTray();
            _trayIcon.SyncRequested += (_, _) => _ = App.Services.BackgroundSync.SyncNowAsync();
            _trayIcon.ToggleAutoSyncRequested += (_, _) => _ = ToggleAutoSyncFromTrayAsync();
            _trayIcon.CheckUpdatesRequested += (_, _) => _ = ShowUpdateDialogFromTrayAsync();
            _trayIcon.ExitRequested += (_, _) => ExitApplication();
            App.Services.BackgroundSync.StatusChanged += BackgroundSync_StatusChanged;
            UpdateTrayState(App.Services.BackgroundSync.CurrentStatus);
            if (_startInTray)
            {
                _hiddenToTray = true;
                AppWindow.IsShownInSwitchers = false;
                AppWindow.Hide();
            }
            if (App.Services.CurrentSettings.CheckUpdatesOnStartup
                && App.Services.Updates.Options.IsConfigured)
            {
                _ = CheckUpdatesOnStartupAsync();
            }
        }
        catch (Exception exception)
        {
            await App.Services.Log.WriteAsync(
                "ERROR",
                $"Tray initialization failed; close will exit normally: {exception}");
        }
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowExit
            || _trayIcon is null
            || !App.Services.CurrentSettings.KeepRunningInTray)
        {
            return;
        }

        args.Cancel = true;
        _hiddenToTray = true;
        AppWindow.IsShownInSwitchers = false;
        AppWindow.Hide();
        if (!_trayHintShown)
        {
            _trayHintShown = true;
            _trayIcon.ShowNotification(
                "AISky 正在后台运行",
                "自动同步和缓存维护会继续工作。双击通知区图标可重新打开。",
                TrayIconState.Normal);
        }
    }

    public void ShowFromTray()
    {
        _hiddenToTray = false;
        AppWindow.IsShownInSwitchers = true;
        AppWindow.Show();
        Activate();
    }

    public void ExitApplication()
    {
        _allowExit = true;
        _trayIcon?.Dispose();
        _trayIcon = null;
        Close();
    }

    private async Task ToggleAutoSyncFromTrayAsync()
    {
        var settings = App.Services.CurrentSettings;
        await App.Services.UpdateSettingsAsync(
            settings with { AutoSyncEnabled = !settings.AutoSyncEnabled });
        _trayIcon?.ShowNotification(
            settings.AutoSyncEnabled ? "自动同步已暂停" : "自动同步已开启",
            settings.AutoSyncEnabled
                ? "现有数据保持不变，可随时从托盘重新开启。"
                : $"AISky 正在立即检查双模型，之后每 {settings.AutoSyncIntervalHours} 小时自动同步。",
            TrayIconState.Normal);
    }

    private async Task ShowUpdateDialogFromTrayAsync()
    {
        ShowFromTray();
        if (RootFrame.Content is MainPage page)
        {
            await page.ShowUpdateDialogAsync();
        }
    }

    private async Task CheckUpdatesOnStartupAsync()
    {
        try
        {
            var result = await App.Services.Updates.CheckAsync();
            if (result.Availability == UpdateAvailability.Available
                && result.Release is not null)
            {
                DispatcherQueue.TryEnqueue(() =>
                    _trayIcon?.ShowNotification(
                        $"AISky {result.Release.Version} 可用",
                        "新版本已经发布，可从托盘菜单或主界面查看更新说明。",
                        TrayIconState.Normal));
            }
        }
        catch (Exception exception)
        {
            await App.Services.Log.WriteAsync(
                "WARN",
                $"Silent update check failed and will not interrupt startup: {exception}");
        }
    }

    private void BackgroundSync_StatusChanged(object? sender, BackgroundSyncStatus status)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var completedWhileHidden =
                _hiddenToTray
                && _backgroundOperationWasRunning
                && status.State is BackgroundSyncState.Scheduled
                    or BackgroundSyncState.Paused
                    or BackgroundSyncState.Error;
            _backgroundOperationWasRunning =
                status.State is BackgroundSyncState.Syncing
                    or BackgroundSyncState.Cleaning;
            UpdateTrayState(status);
            if (completedWhileHidden)
            {
                _trayIcon?.ShowNotification(
                    status.State == BackgroundSyncState.Error
                        ? "后台同步需要处理"
                        : "后台同步完成",
                    status.Message,
                    status.State == BackgroundSyncState.Error
                        ? TrayIconState.Error
                        : TrayIconState.Normal);
            }
        });
    }

    private void UpdateTrayState(BackgroundSyncStatus status)
    {
        var state = status.State switch
        {
            BackgroundSyncState.Syncing or BackgroundSyncState.Cleaning =>
                TrayIconState.Syncing,
            BackgroundSyncState.Error => TrayIconState.Error,
            _ => TrayIconState.Normal,
        };
        _trayIcon?.UpdateState(state, status.Message, status.AutoSyncEnabled);
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        App.Services.BackgroundSync.StatusChanged -= BackgroundSync_StatusChanged;
        _trayIcon?.Dispose();
        _trayIcon = null;
        App.Services.BackgroundSync.Dispose();
        ((App)Application.Current).ShutdownSingleInstance();
    }
}
