using System.Text.Json;

namespace AISky_Desktop.Core;

public sealed record DataLocationConfiguration
{
    public string DataRoot { get; init; } = "";
    public string PreviousRootPendingDeletion { get; init; } = "";
}

public static class DataLocationStore
{
    public const string MarkerFileName = ".aisky-data-root";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static string DefaultDataRoot =>
        Path.Combine(AppContext.BaseDirectory, "AISkyData");

    public static string ResolveDataRoot()
    {
        var overrideRoot = Environment.GetEnvironmentVariable("AISKY_DATA_ROOT");
        if (!string.IsNullOrWhiteSpace(overrideRoot))
        {
            return Normalize(overrideRoot);
        }

        var configuration = Load();
        return string.IsNullOrWhiteSpace(configuration.DataRoot)
            ? Path.GetFullPath(DefaultDataRoot)
            : Normalize(configuration.DataRoot);
    }

    public static async Task PrepareMigrationAsync(
        string sourceRoot,
        string targetRoot,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var source = Normalize(sourceRoot);
        var target = Normalize(targetRoot);
        if (PathsEqual(source, target))
        {
            return;
        }
        EnsureIndependentRoots(source, target);

        Directory.CreateDirectory(target);
        var targetEntries = Directory.EnumerateFileSystemEntries(target).ToList();
        if (targetEntries.Count > 0)
        {
            throw new InvalidOperationException(
                "为避免读取旧缓存，目标数据文件夹必须为空。请选择空文件夹后重试。");
        }

        var sourceFiles = Directory.Exists(source)
            ? Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).ToList()
            : [];
        for (var index = 0; index < sourceFiles.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceFile = sourceFiles[index];
            var relativePath = Path.GetRelativePath(source, sourceFile);
            var destination = Path.GetFullPath(Path.Combine(target, relativePath));
            EnsurePathInside(target, destination);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var input = new FileStream(
                sourceFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var output = new FileStream(
                destination,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await input.CopyToAsync(output, 1024 * 1024, cancellationToken);
            progress?.Report($"正在迁移数据 · {index + 1}/{Math.Max(1, sourceFiles.Count)}");
        }

        await File.WriteAllTextAsync(
            Path.Combine(target, MarkerFileName),
            "AISky managed data root",
            cancellationToken);
        Save(new DataLocationConfiguration
        {
            DataRoot = target,
            PreviousRootPendingDeletion = source,
        });
    }

    public static string? TryDeletePreviousRoot(string activeRoot)
    {
        var configuration = Load();
        if (string.IsNullOrWhiteSpace(configuration.PreviousRootPendingDeletion))
        {
            return null;
        }

        var active = Normalize(activeRoot);
        var previous = Normalize(configuration.PreviousRootPendingDeletion);
        if (PathsEqual(active, previous))
        {
            Save(configuration with { PreviousRootPendingDeletion = "" });
            return null;
        }
        EnsureIndependentRoots(active, previous);
        if (!Directory.Exists(previous))
        {
            Save(configuration with { PreviousRootPendingDeletion = "" });
            return null;
        }
        if (!File.Exists(Path.Combine(previous, MarkerFileName)))
        {
            return "旧数据目录缺少 AISky 标记，为安全起见没有自动删除。";
        }

        Directory.Delete(previous, recursive: true);
        Save(configuration with { PreviousRootPendingDeletion = "" });
        return $"旧数据目录已迁移并移除：{previous}";
    }

    public static void EnsureMarker(string root)
    {
        var marker = Path.Combine(Normalize(root), MarkerFileName);
        if (!File.Exists(marker))
        {
            File.WriteAllText(marker, "AISky managed data root");
        }
    }

    private static DataLocationConfiguration Load()
    {
        try
        {
            var path = ConfigurationFile;
            if (!File.Exists(path))
            {
                return new DataLocationConfiguration();
            }
            return JsonSerializer.Deserialize<DataLocationConfiguration>(
                File.ReadAllText(path),
                JsonOptions) ?? new DataLocationConfiguration();
        }
        catch
        {
            return new DataLocationConfiguration();
        }
    }

    private static void Save(DataLocationConfiguration configuration)
    {
        Directory.CreateDirectory(ConfigurationDirectory);
        var temporary = ConfigurationFile + ".tmp";
        File.WriteAllText(
            temporary,
            JsonSerializer.Serialize(configuration, JsonOptions));
        File.Move(temporary, ConfigurationFile, overwrite: true);
    }

    private static string ConfigurationDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AISky",
            "Launcher");

    private static string ConfigurationFile =>
        Path.Combine(ConfigurationDirectory, "data-location.json");

    private static string Normalize(string path) =>
        Path.GetFullPath(Environment.ExpandEnvironmentVariables(path))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool PathsEqual(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static void EnsureIndependentRoots(string first, string second)
    {
        var firstPrefix = first + Path.DirectorySeparatorChar;
        var secondPrefix = second + Path.DirectorySeparatorChar;
        if (firstPrefix.StartsWith(secondPrefix, StringComparison.OrdinalIgnoreCase)
            || secondPrefix.StartsWith(firstPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "新旧数据目录不能互相包含，请选择另一个独立文件夹。");
        }
    }

    private static void EnsurePathInside(string root, string candidate)
    {
        var rootPrefix = Normalize(root) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("数据目录包含不安全的文件路径。");
        }
    }
}
