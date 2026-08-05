namespace AISky_Desktop.Core;

public sealed record AppSettings
{
    public string Theme { get; init; } = "System";
    public bool AutoSyncEnabled { get; init; }
    public int AutoSyncIntervalHours { get; init; } = 3;
    public int AutoSyncForecastHours { get; init; } = 360;
    public int LatestProbeDays { get; init; } = 3;
    public string DataAccessPassword { get; init; } = "";
    public bool FirstRunSetupCompleted { get; init; }
    public int CacheRetentionDays { get; init; } = 3;
    public int MaxConcurrentDownloads { get; init; } = 2;
    public bool StartWithWindows { get; init; }
    public bool KeepRunningInTray { get; init; } = true;
    public bool CheckUpdatesOnStartup { get; init; } = true;
    public double MapLayerOpacity { get; init; } = 0.93;
    public bool ShowMapGrid { get; init; } = true;
    public bool ShowMapPlaces { get; init; } = true;
    public bool ShowWindAnimation { get; init; } = true;
    public int DisplayUtcOffsetHours { get; init; }
}
