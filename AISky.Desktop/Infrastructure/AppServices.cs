using AISky_Desktop.Core;
using AISky_Desktop.DataWorker;

namespace AISky_Desktop.Infrastructure;

public sealed class AppServices
{
    private readonly object _initializationLock = new();
    private Task? _initialization;

    private AppServices(
        AppPaths paths,
        FileLogService log,
        JsonSettingsStore settings,
        IDataWorkerClient dataWorker)
    {
        Paths = paths;
        Log = log;
        Settings = settings;
        DataWorker = dataWorker;
        Startup = new StartupService();
        Updates = new UpdateService(paths, log);
        BackgroundSync = new BackgroundSyncService(dataWorker, log);
    }

    public AppPaths Paths { get; }
    public FileLogService Log { get; }
    public JsonSettingsStore Settings { get; }
    public IDataWorkerClient DataWorker { get; }
    public StartupService Startup { get; }
    public UpdateService Updates { get; }
    public BackgroundSyncService BackgroundSync { get; }
    public AppSettings CurrentSettings { get; private set; } = new();

    public static AppServices CreateDefault()
    {
        var paths = AppPaths.CreateDefault();
        return new AppServices(
            paths,
            new FileLogService(paths),
            new JsonSettingsStore(paths),
            new PythonDataWorkerClient(paths));
    }

    public Task InitializeAsync()
    {
        lock (_initializationLock)
        {
            return _initialization ??= InitializeCoreAsync();
        }
    }

    private async Task InitializeCoreAsync()
    {
        Paths.EnsureDirectories();
        CurrentSettings = await Settings.LoadAsync();
        await Log.WriteAsync(
            "INFO",
            $"Settings loaded: autoSync={CurrentSettings.AutoSyncEnabled}, forecastHours={CurrentSettings.AutoSyncForecastHours}, firstRunComplete={CurrentSettings.FirstRunSetupCompleted}, utcOffset={CurrentSettings.DisplayUtcOffsetHours}.");
        if (CurrentSettings.StartWithWindows != Startup.IsEnabled())
        {
            CurrentSettings = CurrentSettings with
            {
                StartWithWindows = Startup.IsEnabled(),
            };
            await Settings.SaveAsync(CurrentSettings);
        }
        BackgroundSync.ApplySettings(CurrentSettings);
        await Log.WriteAsync("INFO", $"AISky Desktop initialized at {Paths.Root}");
    }

    public async Task UpdateSettingsAsync(AppSettings settings)
    {
        Startup.SetEnabled(settings.StartWithWindows);
        CurrentSettings = settings;
        await Settings.SaveAsync(settings);
        BackgroundSync.ApplySettings(settings);
    }
}
