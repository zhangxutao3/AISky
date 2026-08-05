using System.Text.Json;
using AISky_Desktop.Core;

namespace AISky_Desktop.Infrastructure;

public sealed class JsonSettingsStore(AppPaths paths)
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public async Task<AppSettings> LoadAsync()
    {
        Directory.CreateDirectory(paths.Config);
        if (!File.Exists(paths.SettingsFile))
        {
            var defaults = new AppSettings();
            await SaveAsync(defaults);
            return defaults;
        }

        await using var stream = File.OpenRead(paths.SettingsFile);
        return await JsonSerializer.DeserializeAsync<AppSettings>(stream, SerializerOptions) ?? new AppSettings();
    }

    public async Task SaveAsync(AppSettings settings)
    {
        Directory.CreateDirectory(paths.Config);
        var tempFile = paths.SettingsFile + ".tmp";
        await using (var stream = File.Create(tempFile))
        {
            await JsonSerializer.SerializeAsync(stream, settings, SerializerOptions);
        }

        File.Move(tempFile, paths.SettingsFile, true);
    }
}
