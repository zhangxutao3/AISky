using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AISky_Desktop.Core;

namespace AISky_Desktop.DataWorker;

public interface IDataWorkerClient
{
    Task<DataWorkerStatus> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<ForecastIndex> GetIndexAsync(CancellationToken cancellationToken = default);
    Task<ForecastIndex> ImportAsync(
        string sourcePath,
        bool copySource,
        IProgress<DataWorkerProgress>? progress = null,
        CancellationToken cancellationToken = default);
    Task<DownloadRangeResult> DownloadRangeAsync(
        DownloadRangeRequest request,
        IProgress<DataWorkerProgress>? progress = null,
        CancellationToken cancellationToken = default);
    Task<SyncLatestResult> SyncLatestAsync(
        SyncLatestRequest request,
        IProgress<DataWorkerProgress>? progress = null,
        CancellationToken cancellationToken = default);
    Task<CleanupResult> CleanupAsync(
        CleanupRequest request,
        IProgress<DataWorkerProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed record DataWorkerStatus(
    bool IsAvailable,
    string Message,
    string PythonVersion = "",
    string NumpyVersion = "");

public sealed record DataWorkerProgress(
    string Operation,
    string Stage,
    string Message,
    double? Percent,
    long? BytesReceived,
    long? TotalBytes,
    bool IsWarning = false);

public sealed record DownloadRangeRequest(
    string Model,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string Password = "",
    int MaxLeadHours = 360);

public sealed record DownloadRangeResult(
    int InitCount,
    int Requested,
    int Downloaded,
    int Skipped,
    int Failed,
    ForecastIndex Index);

public sealed record SyncLatestRequest(
    string Model,
    string Password = "",
    int ProbeDays = 3,
    int MaxLeadHours = 360,
    int MaxVersion = 9,
    DateTimeOffset? NowUtc = null,
    string? BaseUrl = null);

public sealed record SyncLatestResult(
    string Model,
    bool Found,
    string? InitKey,
    int Requested,
    int Downloaded,
    int Skipped,
    int Failed,
    ForecastIndex Index);

public sealed record CleanupRequest(
    int RetentionDays,
    DateTimeOffset? NowUtc = null);

public sealed record CleanupResult(
    int RemovedRuns,
    long ReclaimedBytes,
    int Failed,
    string CutoffUtc,
    ForecastIndex Index);

public sealed record ForecastIndex
{
    [JsonPropertyName("generatedAtUtc")]
    public string GeneratedAtUtc { get; init; } = "";

    [JsonPropertyName("models")]
    public List<string> Models { get; init; } = [];

    [JsonPropertyName("runs")]
    public List<ForecastRun> Runs { get; init; } = [];
}

public sealed record ForecastRun
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("model")]
    public string Model { get; init; } = "";

    [JsonPropertyName("initKey")]
    public string InitKey { get; init; } = "";

    [JsonPropertyName("forecastKey")]
    public string ForecastKey { get; init; } = "";

    [JsonPropertyName("leadHours")]
    public int LeadHours { get; init; }

    [JsonPropertyName("version")]
    public string Version { get; init; } = "";

    [JsonPropertyName("sourceFile")]
    public string SourceFile { get; init; } = "";

    [JsonPropertyName("fileSize")]
    public long FileSize { get; init; }

    [JsonPropertyName("grid")]
    public ForecastGrid Grid { get; init; } = new();

    [JsonPropertyName("layers")]
    public List<ForecastLayer> Layers { get; init; } = [];
}

public sealed record ForecastGrid
{
    [JsonPropertyName("lat")]
    public List<double> Latitude { get; init; } = [];

    [JsonPropertyName("lon")]
    public List<double> Longitude { get; init; } = [];

    [JsonPropertyName("rows")]
    public int Rows { get; init; }

    [JsonPropertyName("cols")]
    public int Columns { get; init; }
}

public sealed record ForecastLayer
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("label")]
    public string Label { get; init; } = "";

    [JsonPropertyName("cn")]
    public string Name { get; init; } = "";

    [JsonPropertyName("unit")]
    public string Unit { get; init; } = "";

    [JsonPropertyName("range")]
    public List<double> Range { get; init; } = [];

    [JsonPropertyName("palette")]
    public List<string> Palette { get; init; } = [];

    [JsonPropertyName("field")]
    public string Field { get; init; } = "";

    [JsonPropertyName("fieldInfo")]
    public ForecastFieldInfo FieldInfo { get; init; } = new();

    [JsonPropertyName("sample")]
    public string Sample { get; init; } = "";

    [JsonPropertyName("stats")]
    public ForecastStats Stats { get; init; } = new();

    [JsonPropertyName("vector")]
    public ForecastVectorField? Vector { get; init; }
}

public sealed record ForecastVectorField
{
    [JsonPropertyName("u")]
    public string U { get; init; } = "";

    [JsonPropertyName("v")]
    public string V { get; init; } = "";

    [JsonPropertyName("fieldInfo")]
    public ForecastFieldInfo FieldInfo { get; init; } = new();
}

public sealed record ForecastFieldInfo
{
    [JsonPropertyName("encoding")]
    public string Encoding { get; init; } = "";

    [JsonPropertyName("missing")]
    public int Missing { get; init; } = 65535;

    [JsonPropertyName("rows")]
    public int Rows { get; init; }

    [JsonPropertyName("cols")]
    public int Columns { get; init; }

    [JsonPropertyName("range")]
    public List<double> Range { get; init; } = [];
}

public sealed record ForecastStats
{
    [JsonPropertyName("min")]
    public double Minimum { get; init; }

    [JsonPropertyName("mean")]
    public double Mean { get; init; }

    [JsonPropertyName("max")]
    public double Maximum { get; init; }

    [JsonPropertyName("p02")]
    public double Percentile02 { get; init; }

    [JsonPropertyName("p50")]
    public double Median { get; init; }

    [JsonPropertyName("p98")]
    public double Percentile98 { get; init; }
}

public sealed class PythonDataWorkerClient(AppPaths paths) : IDataWorkerClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<DataWorkerStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await RunAsync("status", [], null, cancellationToken);
            return new DataWorkerStatus(
                result.GetProperty("available").GetBoolean(),
                result.GetProperty("integrity").GetString() == "ok"
                    ? "NetCDF 工作进程与本地索引正常"
                    : "本地索引完整性检查失败",
                result.GetProperty("python").GetString() ?? "",
                result.GetProperty("numpy").GetString() ?? "");
        }
        catch (Exception exception)
        {
            return new DataWorkerStatus(false, FriendlyError(exception));
        }
    }

    public async Task<ForecastIndex> GetIndexAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync("index", [], null, cancellationToken);
        return result.Deserialize<ForecastIndex>(JsonOptions) ?? new ForecastIndex();
    }

    public async Task<ForecastIndex> ImportAsync(
        string sourcePath,
        bool copySource,
        IProgress<DataWorkerProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var arguments = new List<string>
        {
            "--source", sourcePath,
            "--data-root", paths.Data,
            "--render-root", paths.RenderCache,
        };
        if (copySource)
        {
            arguments.Add("--copy-source");
        }

        var result = await RunAsync("import", arguments, progress, cancellationToken);
        return result.GetProperty("index").Deserialize<ForecastIndex>(JsonOptions) ?? new ForecastIndex();
    }

    public async Task<DownloadRangeResult> DownloadRangeAsync(
        DownloadRangeRequest request,
        IProgress<DataWorkerProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var arguments = new List<string>
        {
            "--model", request.Model,
            "--start", request.StartUtc.UtcDateTime.ToString("yyyyMMdd_HHmm", CultureInfo.InvariantCulture),
            "--end", request.EndUtc.UtcDateTime.ToString("yyyyMMdd_HHmm", CultureInfo.InvariantCulture),
            "--password", request.Password,
            "--max-lead-hours", request.MaxLeadHours.ToString(CultureInfo.InvariantCulture),
            "--data-root", paths.Data,
            "--render-root", paths.RenderCache,
        };
        var result = await RunAsync("download-range", arguments, progress, cancellationToken);
        return new DownloadRangeResult(
            result.GetProperty("initCount").GetInt32(),
            result.GetProperty("requested").GetInt32(),
            result.GetProperty("downloaded").GetInt32(),
            result.GetProperty("skipped").GetInt32(),
            result.GetProperty("failed").GetInt32(),
            result.GetProperty("index").Deserialize<ForecastIndex>(JsonOptions) ?? new ForecastIndex());
    }

    public async Task<SyncLatestResult> SyncLatestAsync(
        SyncLatestRequest request,
        IProgress<DataWorkerProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var arguments = new List<string>
        {
            "--model", request.Model,
            "--password", request.Password,
            "--probe-days", request.ProbeDays.ToString(CultureInfo.InvariantCulture),
            "--max-lead-hours", request.MaxLeadHours.ToString(CultureInfo.InvariantCulture),
            "--max-version", request.MaxVersion.ToString(CultureInfo.InvariantCulture),
            "--data-root", paths.Data,
            "--render-root", paths.RenderCache,
        };
        if (request.NowUtc is { } now)
        {
            arguments.Add("--now");
            arguments.Add(now.UtcDateTime.ToString("yyyyMMdd_HHmm", CultureInfo.InvariantCulture));
        }
        if (!string.IsNullOrWhiteSpace(request.BaseUrl))
        {
            arguments.Add("--base-url");
            arguments.Add(request.BaseUrl);
        }
        var result = await RunAsync("sync-latest", arguments, progress, cancellationToken);
        return new SyncLatestResult(
            result.GetProperty("model").GetString() ?? request.Model,
            result.GetProperty("found").GetBoolean(),
            result.TryGetProperty("initKey", out var initNode) && initNode.ValueKind == JsonValueKind.String
                ? initNode.GetString()
                : null,
            result.GetProperty("requested").GetInt32(),
            result.GetProperty("downloaded").GetInt32(),
            result.GetProperty("skipped").GetInt32(),
            result.GetProperty("failed").GetInt32(),
            result.GetProperty("index").Deserialize<ForecastIndex>(JsonOptions) ?? new ForecastIndex());
    }

    public async Task<CleanupResult> CleanupAsync(
        CleanupRequest request,
        IProgress<DataWorkerProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var arguments = new List<string>
        {
            "--retention-days", request.RetentionDays.ToString(CultureInfo.InvariantCulture),
            "--data-root", paths.Data,
            "--render-root", paths.RenderCache,
        };
        if (request.NowUtc is { } now)
        {
            arguments.Add("--now");
            arguments.Add(now.UtcDateTime.ToString("yyyyMMdd_HHmm", CultureInfo.InvariantCulture));
        }
        var result = await RunAsync("cleanup", arguments, progress, cancellationToken);
        return new CleanupResult(
            result.GetProperty("removedRuns").GetInt32(),
            result.GetProperty("reclaimedBytes").GetInt64(),
            result.GetProperty("failed").GetInt32(),
            result.GetProperty("cutoffUtc").GetString() ?? "",
            result.GetProperty("index").Deserialize<ForecastIndex>(JsonOptions) ?? new ForecastIndex());
    }

    private async Task<JsonElement> RunAsync(
        string command,
        IReadOnlyList<string> arguments,
        IProgress<DataWorkerProgress>? progress,
        CancellationToken cancellationToken)
    {
        var workerPath = Path.Combine(AppContext.BaseDirectory, "DataWorker", "worker.py");
        var schemaPath = Path.Combine(AppContext.BaseDirectory, "Infrastructure", "Database", "schema.sql");
        if (!File.Exists(workerPath) || !File.Exists(schemaPath))
        {
            throw new InvalidOperationException("数据工作进程文件不完整，请重新安装 AISky。");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = ResolvePythonExecutable(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        startInfo.ArgumentList.Add(workerPath);
        startInfo.ArgumentList.Add("--database");
        startInfo.ArgumentList.Add(paths.CacheDatabase);
        startInfo.ArgumentList.Add("--schema");
        startInfo.ArgumentList.Add(schemaPath);
        startInfo.ArgumentList.Add("--command");
        startInfo.ArgumentList.Add(command);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("无法启动 NetCDF 数据工作进程。");
        }

        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Cancellation is best effort; the caller still receives cancellation.
            }
        });

        JsonElement? result = null;
        string? workerError = null;
        while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var type = root.TryGetProperty("type", out var typeNode) ? typeNode.GetString() : "";
            if (type is "progress" or "warning")
            {
                progress?.Report(new DataWorkerProgress(
                    ReadString(root, "operation"),
                    ReadString(root, "stage"),
                    ReadString(root, "message"),
                    ReadDouble(root, "percent"),
                    ReadLong(root, "bytesReceived"),
                    ReadLong(root, "totalBytes"),
                    type == "warning"));
            }
            else if (type == "error")
            {
                workerError = ReadString(root, "message");
            }
            else if (type == "result")
            {
                result = root.Clone();
            }
        }

        var standardError = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (process.ExitCode != 0 || result is null)
        {
            throw new InvalidOperationException(
                workerError
                ?? (string.IsNullOrWhiteSpace(standardError)
                    ? "NetCDF 工作进程没有返回有效结果。"
                    : standardError.Trim()));
        }
        return result.Value;
    }

    private static string ResolvePythonExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("AISKY_PYTHON");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var bundled = Path.Combine(AppContext.BaseDirectory, "Python", "python.exe");
        return File.Exists(bundled) ? bundled : "python.exe";
    }

    private static string FriendlyError(Exception exception)
    {
        var text = exception.Message;
        if (text.Contains("No module named", StringComparison.OrdinalIgnoreCase))
        {
            return "Python 缺少 NetCDF 组件，请安装 DataWorker/requirements.txt 中的依赖。";
        }
        if (text.Contains("cannot find", StringComparison.OrdinalIgnoreCase)
            || text.Contains("找不到", StringComparison.OrdinalIgnoreCase))
        {
            return "数据运行时缺失或损坏，请重新解压完整的 AISky 发布包。";
        }
        return text;
    }

    private static string ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.String
            ? node.GetString() ?? ""
            : "";

    private static double? ReadDouble(JsonElement root, string name) =>
        root.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.Number
            ? node.GetDouble()
            : null;

    private static long? ReadLong(JsonElement root, string name) =>
        root.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.Number
            ? node.GetInt64()
            : null;
}
