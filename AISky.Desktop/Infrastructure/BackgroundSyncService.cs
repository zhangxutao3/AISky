using AISky_Desktop.Core;
using AISky_Desktop.DataWorker;
using System.Globalization;

namespace AISky_Desktop.Infrastructure;

public enum BackgroundSyncState
{
    Paused,
    Scheduled,
    Syncing,
    Cleaning,
    Error,
}

public sealed record BackgroundSyncStatus(
    BackgroundSyncState State,
    string Message,
    bool AutoSyncEnabled,
    DateTimeOffset? NextRunUtc,
    DateTimeOffset UpdatedUtc,
    bool IsError = false);

public sealed class BackgroundSyncService(
    IDataWorkerClient dataWorker,
    FileLogService log) : IDisposable
{
    private static readonly string[] Models = ["AISky-Energy", "AISky-SDS"];
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _timerLock = new();
    private AppSettings _settings = new();
    private Timer? _timer;
    private bool _disposed;

    public event EventHandler<BackgroundSyncStatus>? StatusChanged;
    public event EventHandler<ForecastIndex>? IndexUpdated;

    public BackgroundSyncStatus CurrentStatus { get; private set; } = new(
        BackgroundSyncState.Paused,
        "自动同步已暂停",
        false,
        null,
        DateTimeOffset.UtcNow);

    public void ApplySettings(AppSettings settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _settings = settings;
        lock (_timerLock)
        {
            _timer?.Dispose();
            _timer = null;
            if (settings.AutoSyncEnabled)
            {
                var interval = TimeSpan.FromHours(Math.Clamp(settings.AutoSyncIntervalHours, 1, 24));
                _timer = new Timer(
                    _ => _ = SyncNowAsync(),
                    null,
                    interval,
                    Timeout.InfiniteTimeSpan);
                Publish(
                    BackgroundSyncState.Scheduled,
                    $"自动同步已开启，下次检查约在 {DateTimeOffset.UtcNow.Add(interval):MM-dd HH:mm} UTC",
                    DateTimeOffset.UtcNow.Add(interval));
            }
            else
            {
                Publish(BackgroundSyncState.Paused, "自动同步已暂停");
            }
        }
    }

    public async Task<ForecastIndex?> SyncNowAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!await _operationLock.WaitAsync(0, cancellationToken))
        {
            Publish(
                BackgroundSyncState.Syncing,
                "同步任务已经在运行，请稍候",
                CurrentStatus.NextRunUtc);
            return null;
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _shutdown.Token);
        try
        {
            Publish(BackgroundSyncState.Syncing, "正在寻找两个模型的最新起报数据");
            var index = await dataWorker.GetIndexAsync(linkedCancellation.Token);
            var foundModels = 0;
            var downloaded = 0;
            var skipped = 0;
            var failures = 0;
            Exception? firstFailure = null;
            foreach (var model in Models)
            {
                var progress = new Progress<DataWorkerProgress>(item =>
                    Publish(
                        BackgroundSyncState.Syncing,
                        item.IsWarning ? $"同步提醒：{item.Message}" : item.Message));
                try
                {
                    var result = await dataWorker.SyncLatestAsync(
                        new SyncLatestRequest(
                            model,
                            _settings.DataAccessPassword,
                            Math.Clamp(_settings.LatestProbeDays, 1, 14),
                            Math.Clamp(_settings.AutoSyncForecastHours, 0, 360)),
                        progress,
                        linkedCancellation.Token);
                    index = result.Index;
                    foundModels += result.Found ? 1 : 0;
                    downloaded += result.Downloaded;
                    skipped += result.Skipped;
                    failures += result.Failed;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    failures++;
                    firstFailure ??= exception;
                    await log.WriteAsync("ERROR", $"{model} automatic sync failed: {exception}");
                }
            }

            Publish(BackgroundSyncState.Cleaning, "同步完成，正在执行缓存保留策略");
            var cleanup = await dataWorker.CleanupAsync(
                new CleanupRequest(Math.Clamp(_settings.CacheRetentionDays, 1, 365)),
                null,
                linkedCancellation.Token);
            index = cleanup.Index;
            IndexUpdated?.Invoke(this, index);

            var reclaimed = FormatBytes(cleanup.ReclaimedBytes);
            var message = downloaded > 0
                ? $"同步完成：新增 {downloaded} 个时次，清理 {cleanup.RemovedRuns} 项（{reclaimed}）"
                : foundModels > 0
                    ? $"已是最新数据，{skipped} 个时次无需重复下载"
                    : "暂未发现新起报，继续使用现有本地数据";
            if (failures > 0)
            {
                message += $"；{failures} 项失败，稍后会重试";
            }
            var hasNoUsableData = index.Runs.Count == 0
                && foundModels == 0
                && downloaded == 0;
            if (hasNoUsableData && firstFailure is not null)
            {
                message = $"同步失败：{FriendlyError(firstFailure)}";
            }
            await log.WriteAsync(
                failures > 0 ? "WARN" : "INFO",
                $"Automatic sync finished. foundModels={foundModels}, downloaded={downloaded}, skipped={skipped}, failures={failures}, removed={cleanup.RemovedRuns}, reclaimed={cleanup.ReclaimedBytes}.");
            ScheduleAfterCompletion(message, failures > 0);
            return index;
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception)
        {
            await log.WriteAsync("ERROR", $"Automatic sync crashed safely: {exception}");
            ScheduleAfterCompletion(
                $"自动同步失败：{FriendlyError(exception)}",
                isError: true);
            return null;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<ForecastIndex?> FillForecastSeriesAsync(
        string model,
        string initKey,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!DateTimeOffset.TryParseExact(
                initKey,
                "yyyyMMdd_HHmm",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var initTime))
        {
            ScheduleAfterCompletion("序列补齐失败：起报时间格式无效", true);
            return null;
        }
        if (!await _operationLock.WaitAsync(0, cancellationToken))
        {
            Publish(BackgroundSyncState.Syncing, "已有数据任务正在运行，请稍候");
            return null;
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _shutdown.Token);
        try
        {
            var hours = Math.Clamp(_settings.AutoSyncForecastHours, 24, 360);
            Publish(
                BackgroundSyncState.Syncing,
                $"正在补齐 {model} 当前起报的 0–{hours} 小时序列");
            var progress = new Progress<DataWorkerProgress>(item =>
                Publish(
                    BackgroundSyncState.Syncing,
                    item.IsWarning ? $"补齐提醒：{item.Message}" : item.Message));
            var result = await dataWorker.DownloadRangeAsync(
                new DownloadRangeRequest(
                    model,
                    initTime,
                    initTime,
                    _settings.DataAccessPassword,
                    hours),
                progress,
                linkedCancellation.Token);
            IndexUpdated?.Invoke(this, result.Index);
            var count = result.Index.Runs.Count(item =>
                item.Model == model && item.InitKey == initKey);
            var message = result.Downloaded > 0
                ? $"序列已补齐：新增 {result.Downloaded} 个时次，当前共 {count} 个"
                : count > 1
                    ? $"当前起报已有 {count} 个时次，无需重复下载"
                    : "未能从数据服务器取得更多时次，请检查网络后重试";
            await log.WriteAsync(
                result.Failed > 0 ? "WARN" : "INFO",
                $"Forecast series fill finished. model={model}, init={initKey}, downloaded={result.Downloaded}, skipped={result.Skipped}, failed={result.Failed}.");
            ScheduleAfterCompletion(message, result.Failed > 0 || count <= 1);
            return result.Index;
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception)
        {
            await log.WriteAsync("ERROR", $"Forecast series fill failed: {exception}");
            ScheduleAfterCompletion(
                $"序列补齐失败：{FriendlyError(exception)}",
                isError: true);
            return null;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<CleanupResult?> CleanupNowAsync(
        int retentionDays,
        IProgress<DataWorkerProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!await _operationLock.WaitAsync(0, cancellationToken))
        {
            return null;
        }
        try
        {
            Publish(BackgroundSyncState.Cleaning, "正在安全清理过期缓存");
            var result = await dataWorker.CleanupAsync(
                new CleanupRequest(Math.Clamp(retentionDays, 1, 365)),
                progress,
                cancellationToken);
            IndexUpdated?.Invoke(this, result.Index);
            await log.WriteAsync(
                result.Failed > 0 ? "WARN" : "INFO",
                $"Manual cleanup finished. removed={result.RemovedRuns}, failed={result.Failed}, reclaimed={result.ReclaimedBytes}.");
            ScheduleAfterCompletion(
                $"缓存清理完成：移除 {result.RemovedRuns} 项，释放 {FormatBytes(result.ReclaimedBytes)}",
                result.Failed > 0);
            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await log.WriteAsync("ERROR", $"Manual cleanup failed: {exception}");
            ScheduleAfterCompletion($"缓存清理失败：{FriendlyError(exception)}", true);
            return null;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private void ScheduleAfterCompletion(string message, bool isError)
    {
        DateTimeOffset? nextRun = null;
        lock (_timerLock)
        {
            _timer?.Dispose();
            _timer = null;
            if (_settings.AutoSyncEnabled && !_shutdown.IsCancellationRequested)
            {
                var interval = TimeSpan.FromHours(Math.Clamp(_settings.AutoSyncIntervalHours, 1, 24));
                nextRun = DateTimeOffset.UtcNow.Add(interval);
                _timer = new Timer(
                    _ => _ = SyncNowAsync(),
                    null,
                    interval,
                    Timeout.InfiniteTimeSpan);
            }
        }
        Publish(
            isError ? BackgroundSyncState.Error
                : nextRun is null ? BackgroundSyncState.Paused : BackgroundSyncState.Scheduled,
            message,
            nextRun,
            isError);
    }

    private void Publish(
        BackgroundSyncState state,
        string message,
        DateTimeOffset? nextRunUtc = null,
        bool isError = false)
    {
        CurrentStatus = new BackgroundSyncStatus(
            state,
            message,
            _settings.AutoSyncEnabled,
            nextRunUtc,
            DateTimeOffset.UtcNow,
            isError);
        StatusChanged?.Invoke(this, CurrentStatus);
    }

    private static string FriendlyError(Exception exception)
    {
        var message = exception.Message.Trim();
        if (message.Contains("网络", StringComparison.OrdinalIgnoreCase)
            || message.Contains("timed out", StringComparison.OrdinalIgnoreCase))
        {
            return "数据服务器连接超时（尚未进入密码验证），请检查网络；后台稍后会自动重试";
        }
        return string.IsNullOrWhiteSpace(message) ? "未知错误，详情已写入日志" : message;
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }
        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024d:0.0} KB";
        }
        if (bytes < 1024L * 1024 * 1024)
        {
            return $"{bytes / 1024d / 1024:0.0} MB";
        }
        return $"{bytes / 1024d / 1024 / 1024:0.00} GB";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _shutdown.Cancel();
        lock (_timerLock)
        {
            _timer?.Dispose();
            _timer = null;
        }
        _shutdown.Dispose();
        _operationLock.Dispose();
    }
}
