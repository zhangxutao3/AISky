using System.Text.Json;
using AISky_Desktop.Core;

namespace AISky_Desktop.Infrastructure;

public sealed class JsonSettingsStore(AppPaths paths)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public async Task<AppSettings> LoadAsync()
    {
        await _gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(paths.Config);
            if (!File.Exists(paths.SettingsFile))
            {
                var defaults = new AppSettings();
                await SaveCoreAsync(defaults);
                return defaults;
            }

            await using var stream = File.OpenRead(paths.SettingsFile);
            return await JsonSerializer.DeserializeAsync<AppSettings>(stream, SerializerOptions)
                ?? new AppSettings();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(AppSettings settings)
    {
        await _gate.WaitAsync();
        try
        {
            await SaveCoreAsync(settings);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SaveCoreAsync(AppSettings settings)
    {
        Directory.CreateDirectory(paths.Config);
        var tempFile = $"{paths.SettingsFile}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = File.Create(tempFile))
            {
                await JsonSerializer.SerializeAsync(stream, settings, SerializerOptions);
            }

            File.Move(tempFile, paths.SettingsFile, true);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
