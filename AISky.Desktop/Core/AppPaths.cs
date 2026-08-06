namespace AISky_Desktop.Core;

public sealed class AppPaths
{
    private AppPaths(string root)
    {
        Root = root;
        Cache = Path.Combine(root, "cache");
        Config = Path.Combine(root, "config");
        Logs = Path.Combine(root, "logs");
        Data = Path.Combine(root, "data");
        Temp = Path.Combine(root, "temp");
        Updates = Path.Combine(root, "updates");
        RenderCache = Path.Combine(Cache, "render");
        CacheDatabase = Path.Combine(Cache, "aisky-cache.db");
        SettingsFile = Path.Combine(Config, "settings.json");
    }

    public string Root { get; }
    public string Cache { get; }
    public string Config { get; }
    public string Logs { get; }
    public string Data { get; }
    public string Temp { get; }
    public string Updates { get; }
    public string RenderCache { get; }
    public string CacheDatabase { get; }
    public string SettingsFile { get; }

    public static AppPaths CreateDefault()
    {
        return new AppPaths(DataLocationStore.ResolveDataRoot());
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Cache);
        Directory.CreateDirectory(Config);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Data);
        Directory.CreateDirectory(Temp);
        Directory.CreateDirectory(Updates);
        Directory.CreateDirectory(RenderCache);
        DataLocationStore.EnsureMarker(Root);
    }
}
