using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AISky_Desktop.Core;

namespace AISky_Desktop.Infrastructure;

public enum UpdateAvailability
{
    NotConfigured,
    Latest,
    Available,
    AssetMissing,
}

public sealed record UpdateAsset(
    string Name,
    long Size,
    Uri DownloadUri,
    string? Digest);

public sealed record UpdateRelease(
    string Version,
    string Title,
    string Notes,
    DateTimeOffset? PublishedUtc,
    Uri WebUri,
    UpdateAsset? Asset);

public sealed record UpdateCheckResult(
    UpdateAvailability Availability,
    string CurrentVersion,
    UpdateRelease? Release,
    string Message);

public sealed record UpdateDownloadProgress(
    string Message,
    long BytesReceived,
    long? TotalBytes,
    double? Percent);

public sealed record UpdateOptions
{
    [JsonPropertyName("repositoryOwner")]
    public string RepositoryOwner { get; init; } = "";

    [JsonPropertyName("repositoryName")]
    public string RepositoryName { get; init; } = "";

    [JsonPropertyName("assetNamePattern")]
    public string AssetNamePattern { get; init; } = "AISky-Desktop-win-x64.zip";

    [JsonPropertyName("apiBaseUrl")]
    public string ApiBaseUrl { get; init; } = "https://api.github.com";

    public bool IsConfigured =>
        IsSafeRepositoryPart(RepositoryOwner)
        && IsSafeRepositoryPart(RepositoryName)
        && Uri.TryCreate(ApiBaseUrl, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https";

    private static bool IsSafeRepositoryPart(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && Regex.IsMatch(value, "^[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant);
}

public sealed class UpdateService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly AppPaths _paths;
    private readonly FileLogService _log;
    private readonly HttpClient _httpClient;
    private readonly UpdateOptions _options;

    public UpdateService(
        AppPaths paths,
        FileLogService log,
        HttpClient? httpClient = null,
        UpdateOptions? options = null)
    {
        _paths = paths;
        _log = log;
        _options = options ?? LoadOptions();
        _httpClient = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _httpClient.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("AISky-Desktop", VersionInfo.CurrentVersion));
        }
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation(
            "X-GitHub-Api-Version",
            "2022-11-28");
    }

    public UpdateOptions Options => _options;

    public async Task<UpdateCheckResult> CheckAsync(
        CancellationToken cancellationToken = default)
    {
        var currentVersion = VersionInfo.CurrentVersion;
        if (!_options.IsConfigured)
        {
            return new UpdateCheckResult(
                UpdateAvailability.NotConfigured,
                currentVersion,
                null,
                "更新组件已经就绪，等待在 update-config.json 中填写 GitHub 仓库地址。");
        }

        var baseUri = _options.ApiBaseUrl.TrimEnd('/');
        var requestUri =
            $"{baseUri}/repos/{Uri.EscapeDataString(_options.RepositoryOwner)}/" +
            $"{Uri.EscapeDataString(_options.RepositoryName)}/releases/latest";
        using var response = await _httpClient.GetAsync(requestUri, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException(
                "没有找到公开的正式版本，请检查仓库地址或先发布一个 GitHub Release。");
        }
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException(
                "GitHub 暂时拒绝了更新请求，可能已达到访问频率限制，请稍后重试。");
        }
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync<GitHubReleasePayload>(
            stream,
            JsonOptions,
            cancellationToken)
            ?? throw new InvalidOperationException("GitHub 返回的版本信息为空。");
        var releaseVersion = NormalizeVersion(payload.TagName);
        if (!SemanticVersion.TryParse(releaseVersion, out var parsedRelease))
        {
            throw new InvalidOperationException(
                $"最新 Release 的版本号“{payload.TagName}”无法识别，请使用 v1.2.3 格式。");
        }

        var release = new UpdateRelease(
            releaseVersion,
            string.IsNullOrWhiteSpace(payload.Name)
                ? $"AISky {releaseVersion}"
                : payload.Name.Trim(),
            string.IsNullOrWhiteSpace(payload.Body)
                ? "本次发布没有填写更新说明。"
                : payload.Body.Trim(),
            payload.PublishedAt,
            RequireHttpUri(payload.HtmlUrl, "Release 页面"),
            SelectAsset(payload.Assets));
        var currentParsed = SemanticVersion.ParseOrDefault(currentVersion);
        if (parsedRelease <= currentParsed)
        {
            await _log.WriteAsync(
                "INFO",
                $"Update check: current={currentVersion}, latest={releaseVersion}; already current.");
            return new UpdateCheckResult(
                UpdateAvailability.Latest,
                currentVersion,
                release,
                $"当前已是最新版本（{currentVersion}）。");
        }

        if (release.Asset is null)
        {
            await _log.WriteAsync(
                "WARN",
                $"Update {releaseVersion} is available but asset '{_options.AssetNamePattern}' is missing.");
            return new UpdateCheckResult(
                UpdateAvailability.AssetMissing,
                currentVersion,
                release,
                $"发现 {releaseVersion}，但 Release 中缺少 {_options.AssetNamePattern}。");
        }

        await _log.WriteAsync(
            "INFO",
            $"Update available: current={currentVersion}, latest={releaseVersion}, asset={release.Asset.Name}.");
        return new UpdateCheckResult(
            UpdateAvailability.Available,
            currentVersion,
            release,
            $"发现新版本 {releaseVersion}。");
    }

    public async Task<string> DownloadAsync(
        UpdateRelease release,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var asset = release.Asset
            ?? throw new InvalidOperationException("这个版本没有可下载的 Windows 更新包。");
        Directory.CreateDirectory(_paths.Updates);
        var destination = Path.Combine(_paths.Updates, Path.GetFileName(asset.Name));
        var temporary = destination + ".part";
        if (File.Exists(temporary))
        {
            File.Delete(temporary);
        }

        using var response = await _httpClient.GetAsync(
            asset.DownloadUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength > 0
            ? response.Content.Headers.ContentLength
            : asset.Size > 0 ? asset.Size : null;
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var output = File.Create(temporary))
        {
            var buffer = new byte[1024 * 1024];
            long received = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                received += read;
                progress?.Report(new UpdateDownloadProgress(
                    $"正在下载 {asset.Name} · {BackgroundSyncService.FormatBytes(received)}",
                    received,
                    total,
                    total > 0 ? received / (double)total.Value * 100 : null));
            }
        }

        var actualSize = new FileInfo(temporary).Length;
        if (asset.Size > 0 && actualSize != asset.Size)
        {
            File.Delete(temporary);
            throw new InvalidOperationException(
                "更新包大小与 GitHub 记录不一致，已取消安装，请重新下载。");
        }
        await VerifyDigestAsync(temporary, asset.Digest, cancellationToken);
        File.Move(temporary, destination, true);
        await _log.WriteAsync(
            "INFO",
            $"Update package downloaded: {destination}, bytes={actualSize}.");
        return destination;
    }

    public Process StartInstaller(string packagePath)
    {
        var bundledUpdater = Path.Combine(AppContext.BaseDirectory, "AISky.Updater.exe");
        if (!File.Exists(bundledUpdater))
        {
            throw new InvalidOperationException(
                "当前开发版本未包含安装助手。更新包已下载，可解压后手动覆盖程序目录。");
        }

        Directory.CreateDirectory(_paths.Temp);
        var temporaryUpdater = Path.Combine(
            _paths.Temp,
            $"AISky.Updater-{Guid.NewGuid():N}.exe");
        File.Copy(bundledUpdater, temporaryUpdater, true);
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法定位当前 AISky 主程序。");
        var startInfo = new ProcessStartInfo
        {
            FileName = temporaryUpdater,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--wait-pid");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--package");
        startInfo.ArgumentList.Add(Path.GetFullPath(packagePath));
        startInfo.ArgumentList.Add("--target");
        startInfo.ArgumentList.Add(Path.GetFullPath(AppContext.BaseDirectory));
        startInfo.ArgumentList.Add("--executable");
        startInfo.ArgumentList.Add(Path.GetFileName(executable));
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动更新安装助手。");
    }

    private UpdateOptions LoadOptions()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Config", "update-config.json");
        if (!File.Exists(path))
        {
            return new UpdateOptions();
        }
        try
        {
            return JsonSerializer.Deserialize<UpdateOptions>(
                File.ReadAllText(path),
                JsonOptions) ?? new UpdateOptions();
        }
        catch (JsonException exception)
        {
            _ = _log.WriteAsync("ERROR", $"Update configuration is invalid: {exception}");
            return new UpdateOptions();
        }
    }

    private UpdateAsset? SelectAsset(IReadOnlyList<GitHubAssetPayload> assets)
    {
        var selected = assets.FirstOrDefault(asset =>
            string.Equals(
                asset.Name,
                _options.AssetNamePattern,
                StringComparison.OrdinalIgnoreCase));
        if (selected is null)
        {
            return null;
        }
        return new UpdateAsset(
            selected.Name,
            selected.Size,
            RequireHttpUri(selected.BrowserDownloadUrl, "更新包下载地址"),
            selected.Digest);
    }

    private static async Task VerifyDigestAsync(
        string path,
        string? digest,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(digest)
            || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        await using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken));
        var expected = digest["sha256:".Length..].Trim();
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            stream.Close();
            File.Delete(path);
            throw new InvalidOperationException(
                "更新包的 SHA-256 校验失败，文件可能不完整，已取消安装。");
        }
    }

    private static Uri RequireHttpUri(string value, string label)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException($"{label}无效。");
        }
        return uri;
    }

    private static string NormalizeVersion(string value) =>
        value.Trim().TrimStart('v', 'V').Split('+', 2)[0];

    private sealed record GitHubReleasePayload
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; init; } = "";

        [JsonPropertyName("name")]
        public string Name { get; init; } = "";

        [JsonPropertyName("body")]
        public string Body { get; init; } = "";

        [JsonPropertyName("published_at")]
        public DateTimeOffset? PublishedAt { get; init; }

        [JsonPropertyName("html_url")]
        public string HtmlUrl { get; init; } = "";

        [JsonPropertyName("assets")]
        public List<GitHubAssetPayload> Assets { get; init; } = [];
    }

    private sealed record GitHubAssetPayload
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = "";

        [JsonPropertyName("size")]
        public long Size { get; init; }

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; init; } = "";

        [JsonPropertyName("digest")]
        public string? Digest { get; init; }
    }
}

public readonly record struct SemanticVersion(int Major, int Minor, int Patch)
    : IComparable<SemanticVersion>
{
    public static bool TryParse(string value, out SemanticVersion version)
    {
        version = default;
        var core = value.Trim().Split('-', 2)[0];
        var parts = core.Split('.');
        var minor = 0;
        var patch = 0;
        if (parts.Length is < 1 or > 4
            || !int.TryParse(parts[0], out var major)
            || (parts.Length > 1 && !int.TryParse(parts[1], out minor))
            || (parts.Length > 2 && !int.TryParse(parts[2], out patch)))
        {
            return false;
        }
        version = new SemanticVersion(
            major,
            parts.Length > 1 ? minor : 0,
            parts.Length > 2 ? patch : 0);
        return true;
    }

    public static SemanticVersion ParseOrDefault(string value) =>
        TryParse(value, out var parsed) ? parsed : default;

    public int CompareTo(SemanticVersion other)
    {
        var major = Major.CompareTo(other.Major);
        if (major != 0)
        {
            return major;
        }
        var minor = Minor.CompareTo(other.Minor);
        return minor != 0 ? minor : Patch.CompareTo(other.Patch);
    }

    public static bool operator <=(SemanticVersion left, SemanticVersion right) =>
        left.CompareTo(right) <= 0;

    public static bool operator >=(SemanticVersion left, SemanticVersion right) =>
        left.CompareTo(right) >= 0;
}

public static class VersionInfo
{
    public static string CurrentVersion { get; } =
        typeof(VersionInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            .Split('+', 2)[0]
        ?? typeof(VersionInfo).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";
}
