using AISky_Desktop.Core;
using AISky_Desktop.DataWorker;
using System.Diagnostics;
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

public enum ModelSyncState
{
    Queued,
    Checking,
    Downloading,
    Complete,
    Skipped,
    NoData,
    Error,
}

public sealed record ModelSyncProgress(
    string Model,
    ModelSyncState State,
    string Message,
    double? ProgressPercent = null,
    int? CurrentItem = null,
    int? TotalItems = null,
    double BytesPerSecond = 0,
    DateTimeOffset? UpdatedUtc = null);

public sealed record BackgroundSyncStatus(
    BackgroundSyncState State,
    string Message,
    bool AutoSyncEnabled,
    DateTimeOffset? NextRunUtc,
    DateTimeOffset UpdatedUtc,
    bool IsError = false,
    double? ProgressPercent = null,
    string? ActiveModel = null,
    int? CurrentItem = null,
    int? TotalItems = null,
    string? OperationLabel = null,
    bool CanCancel = false,
    IReadOnlyList<ModelSyncProgress>? ModelStatuses = null,
    double DownloadBytesPerSecond = 0);

public sealed record FirstForecastPreparationResult(
    ForecastIndex Index,
    string Model,
    string? InitKey,
    int ExpectedRuns,
    int AvailableRuns,
    bool IsComplete,
    int Downloaded,
    int Skipped,
    int Failed);

public sealed class BackgroundSyncService(
    IDataWorkerClient dataWorker,
    FileLogService log) : IDisposable
{
    private static readonly string[] Models = ["AISky-Energy", "AISky-SDS"];
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _timerLock = new();
    private readonly object _activeCancellationLock = new();
    private readonly object _modelStatusLock = new();
    private readonly object _statusLock = new();
    private readonly Dictionary<string, ModelSyncProgress> _modelStatuses =
        new(StringComparer.Ordinal);
    private AppSettings _settings = new();
    private Timer? _timer;
    private CancellationTokenSource? _activeBackfillCancellation;
    private bool _syncImmediatelyAfterCurrentOperation;
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
        var startImmediately = settings.AutoSyncEnabled && !_settings.AutoSyncEnabled;
        var operationRunning = _operationLock.CurrentCount == 0;
        _settings = settings;
        lock (_timerLock)
        {
            _timer?.Dispose();
            _timer = null;
            if (operationRunning)
            {
                _syncImmediatelyAfterCurrentOperation =
                    settings.AutoSyncEnabled
                    && (_syncImmediatelyAfterCurrentOperation || startImmediately);
                Publish(
                    CurrentStatus.State,
                    CurrentStatus.Message,
                    isError: CurrentStatus.IsError,
                    progressPercent: CurrentStatus.ProgressPercent,
                    activeModel: CurrentStatus.ActiveModel,
                    currentItem: CurrentStatus.CurrentItem,
                    totalItems: CurrentStatus.TotalItems,
                    operationLabel: CurrentStatus.OperationLabel,
                    canCancel: CurrentStatus.CanCancel);
            }
            else if (settings.AutoSyncEnabled)
            {
                var interval = TimeSpan.FromHours(Math.Clamp(settings.AutoSyncIntervalHours, 1, 24));
                _timer = new Timer(
                    _ => _ = SyncNowAsync(),
                    null,
                    interval,
                    Timeout.InfiniteTimeSpan);
                Publish(
                    BackgroundSyncState.Scheduled,
                    "自动同步已开启",
                    DateTimeOffset.UtcNow.Add(interval));
            }
            else
            {
                Publish(BackgroundSyncState.Paused, "自动同步已暂停");
            }
        }
        if (startImmediately && !operationRunning)
        {
            _ = SyncNowAsync(skipRecentCompleteModels: true);
        }
    }

    public async Task<ForecastIndex?> SyncNowAsync(
        CancellationToken cancellationToken = default,
        bool skipRecentCompleteModels = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!await _operationLock.WaitAsync(0, cancellationToken))
        {
            return null;
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _shutdown.Token);
        try
        {
            var forecastHours = Math.Clamp(_settings.AutoSyncForecastHours, 3, 360);
            var expectedRuns = forecastHours / 3;
            ResetModelStatuses(expectedRuns);
            Publish(
                BackgroundSyncState.Syncing,
                "Energy 与 SDS 正在并行检查最新起报",
                progressPercent: 0,
                operationLabel: "双模型并行同步");
            var index = await dataWorker.GetIndexAsync(linkedCancellation.Token);
            var nowUtc = DateTimeOffset.UtcNow;
            var tasks = Models.Select(model => SyncModelAsync(
                model,
                index,
                skipRecentCompleteModels,
                forecastHours,
                expectedRuns,
                nowUtc,
                linkedCancellation.Token));
            var outcomes = await Task.WhenAll(tasks);
            var foundModels = outcomes.Count(item => item.Found);
            var downloaded = outcomes.Sum(item => item.Downloaded);
            var skipped = outcomes.Sum(item => item.Skipped);
            var failures = outcomes.Sum(item => item.Failed);
            var firstFailure = outcomes
                .Select(item => item.Exception)
                .FirstOrDefault(item => item is not null);
            index = await dataWorker.GetIndexAsync(linkedCancellation.Token);

            Publish(
                BackgroundSyncState.Cleaning,
                "双模型检查完成，正在清理过期缓存",
                progressPercent: 100,
                operationLabel: "双模型并行同步");
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

    private async Task<ModelSyncOutcome> SyncModelAsync(
        string model,
        ForecastIndex index,
        bool skipRecentCompleteModels,
        int forecastHours,
        int expectedRuns,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        if (skipRecentCompleteModels
            && HasRecentCompleteRun(index, model, forecastHours, nowUtc))
        {
            UpdateModelStatus(
                model,
                ModelSyncState.Skipped,
                "最近完整预报已在本地",
                100,
                expectedRuns,
                expectedRuns);
            return new ModelSyncOutcome(true, 0, expectedRuns, 0, null);
        }

        var speedMeter = new DownloadSpeedMeter();
        var lastPercent = 0d;
        var progress = new DirectProgress<DataWorkerProgress>(item =>
        {
            lastPercent = ResolveModelPercent(item, lastPercent);
            var speed = item.Stage == "transfer"
                ? speedMeter.Observe(item.BytesReceived)
                : speedMeter.CurrentBytesPerSecond;
            var state = item.Stage is "transfer" or "download" or "file" or "file-complete"
                ? ModelSyncState.Downloading
                : ModelSyncState.Checking;
            UpdateModelStatus(
                model,
                state,
                item.IsWarning ? $"提醒：{item.Message}" : item.Message,
                lastPercent,
                item.CurrentItem,
                item.TotalItems,
                speed);
        });

        try
        {
            UpdateModelStatus(model, ModelSyncState.Checking, "正在检查最新起报", 0);
            var result = await dataWorker.SyncLatestAsync(
                new SyncLatestRequest(
                    model,
                    _settings.DataAccessPassword,
                    Math.Clamp(_settings.LatestProbeDays, 1, 14),
                    forecastHours),
                progress,
                cancellationToken);
            UpdateModelStatus(
                model,
                result.Found ? ModelSyncState.Complete : ModelSyncState.NoData,
                result.Found
                    ? result.Downloaded > 0
                        ? $"完成 · 新增 {result.Downloaded} 个时次"
                        : "已是最新"
                    : "暂未发现可用起报",
                100,
                expectedRuns,
                expectedRuns);
            return new ModelSyncOutcome(
                result.Found,
                result.Downloaded,
                result.Skipped,
                result.Failed,
                null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            UpdateModelStatus(
                model,
                ModelSyncState.Error,
                FriendlyError(exception),
                lastPercent);
            await log.WriteAsync("ERROR", $"{model} automatic sync failed: {exception}");
            return new ModelSyncOutcome(false, 0, 0, 1, exception);
        }
    }

    private void ResetModelStatuses(int expectedRuns)
    {
        lock (_modelStatusLock)
        {
            _modelStatuses.Clear();
            foreach (var model in Models)
            {
                _modelStatuses[model] = new ModelSyncProgress(
                    model,
                    ModelSyncState.Queued,
                    "等待同步",
                    0,
                    0,
                    expectedRuns,
                    0,
                    DateTimeOffset.UtcNow);
            }
        }
    }

    private void UpdateModelStatus(
        string model,
        ModelSyncState state,
        string message,
        double? progressPercent,
        int? currentItem = null,
        int? totalItems = null,
        double bytesPerSecond = 0)
    {
        lock (_modelStatusLock)
        {
            _modelStatuses[model] = new ModelSyncProgress(
                model,
                state,
                message,
                progressPercent,
                currentItem,
                totalItems,
                bytesPerSecond,
                DateTimeOffset.UtcNow);
        }
        var snapshot = SnapshotModelStatuses();
        Publish(
            BackgroundSyncState.Syncing,
            "Energy 与 SDS 正在并行同步",
            progressPercent: snapshot
                .Where(item => item.ProgressPercent is not null)
                .Select(item => item.ProgressPercent!.Value)
                .DefaultIfEmpty(0)
                .Average(),
            activeModel: model,
            currentItem: currentItem,
            totalItems: totalItems,
            operationLabel: "双模型并行同步");
    }

    private IReadOnlyList<ModelSyncProgress> SnapshotModelStatuses()
    {
        lock (_modelStatusLock)
        {
            return Models
                .Where(_modelStatuses.ContainsKey)
                .Select(model => _modelStatuses[model])
                .ToArray();
        }
    }

    private static double ResolveModelPercent(
        DataWorkerProgress progress,
        double fallback)
    {
        if (progress.Stage == "probe")
        {
            return Math.Clamp(progress.Percent ?? fallback, 0, 35);
        }
        if (progress.Stage == "download")
        {
            return 40;
        }
        if (progress.Stage is "file" or "file-complete")
        {
            var filePercent = progress.Percent
                ?? (progress.CurrentItem is { } current
                    && progress.TotalItems is > 0
                        ? current / (double)progress.TotalItems.Value * 100
                        : 0);
            return 40 + Math.Clamp(filePercent, 0, 100) * 0.6;
        }
        return Math.Clamp(progress.Percent ?? fallback, 0, 100);
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
            var progress = new DirectProgress<DataWorkerProgress>(item =>
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

    public async Task<bool> StartBackfillAsync(
        DownloadRangeRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Models.Contains(request.Model, StringComparer.Ordinal))
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                request.Model,
                "不支持的预报模型。");
        }
        if (!await _operationLock.WaitAsync(0, cancellationToken))
        {
            return false;
        }

        var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _shutdown.Token);
        lock (_activeCancellationLock)
        {
            _activeBackfillCancellation = operationCancellation;
        }
        _ = RunBackfillAsync(request, operationCancellation);
        return true;
    }

    public bool CancelActiveOperation()
    {
        lock (_activeCancellationLock)
        {
            if (_activeBackfillCancellation is null
                || _activeBackfillCancellation.IsCancellationRequested)
            {
                return false;
            }
            _activeBackfillCancellation.Cancel();
            return true;
        }
    }

    private async Task RunBackfillAsync(
        DownloadRangeRequest request,
        CancellationTokenSource operationCancellation)
    {
        var initCount = Math.Max(
            1,
            (int)Math.Floor((request.EndUtc - request.StartUtc).TotalHours / 3) + 1);
        var runBasePercent = 0d;
        try
        {
            Publish(
                BackgroundSyncState.Syncing,
                $"{request.Model} · 正在准备回溯下载",
                progressPercent: 0,
                activeModel: request.Model,
                operationLabel: "回溯下载",
                canCancel: true);
            var progress = new DirectProgress<DataWorkerProgress>(item =>
            {
                if (item.Stage == "run" && item.Percent is { } runPercent)
                {
                    runBasePercent = Math.Clamp(runPercent, 0, 100);
                }
                var overallPercent = item.Stage == "file"
                    && item.CurrentItem is { } current
                    && item.TotalItems is > 0
                        ? Math.Clamp(
                            runBasePercent
                            + current / (double)item.TotalItems.Value * 100 / initCount,
                            0,
                            100)
                        : item.Percent is { } percent
                            ? Math.Clamp(percent, 0, 100)
                            : runBasePercent;
                Publish(
                    BackgroundSyncState.Syncing,
                    item.IsWarning
                        ? $"{request.Model} · 下载提醒：{item.Message}"
                        : $"{request.Model} · {item.Message}",
                    progressPercent: overallPercent,
                    activeModel: request.Model,
                    currentItem: item.CurrentItem,
                    totalItems: item.TotalItems,
                    operationLabel: "回溯下载",
                    canCancel: true);
            });
            var result = await dataWorker.DownloadRangeAsync(
                request,
                progress,
                operationCancellation.Token);
            IndexUpdated?.Invoke(this, result.Index);
            var message =
                $"回溯下载完成：新增 {result.Downloaded} 个，跳过 {result.Skipped} 个";
            if (result.Failed > 0)
            {
                message += $"，失败 {result.Failed} 个";
            }
            await log.WriteAsync(
                result.Failed > 0 ? "WARN" : "INFO",
                $"Backfill finished. model={request.Model}, start={request.StartUtc:O}, end={request.EndUtc:O}, downloaded={result.Downloaded}, skipped={result.Skipped}, failed={result.Failed}.");
            ScheduleAfterCompletion(message, result.Failed > 0);
        }
        catch (OperationCanceledException)
        {
            try
            {
                var index = await dataWorker.GetIndexAsync(_shutdown.Token);
                IndexUpdated?.Invoke(this, index);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await log.WriteAsync(
                    "WARN",
                    $"Refreshing index after backfill cancellation failed: {exception}");
            }
            await log.WriteAsync("INFO", "Backfill download cancelled by the user.");
            ScheduleAfterCompletion(
                "回溯下载已取消；已完成文件保留，未完成文件不会进入索引",
                isError: false);
        }
        catch (Exception exception)
        {
            await log.WriteAsync("ERROR", $"Backfill download failed: {exception}");
            ScheduleAfterCompletion(
                $"回溯下载失败：{FriendlyError(exception)}",
                isError: true);
        }
        finally
        {
            lock (_activeCancellationLock)
            {
                if (ReferenceEquals(_activeBackfillCancellation, operationCancellation))
                {
                    _activeBackfillCancellation = null;
                }
            }
            operationCancellation.Dispose();
            _operationLock.Release();
        }
    }

    public async Task<FirstForecastPreparationResult> PrepareFirstForecastAsync(
        string model,
        string password,
        IProgress<DataWorkerProgress>? preparationProgress = null,
        CancellationToken cancellationToken = default)
    {
        const int forecastHours = 360;
        const int expectedRuns = forecastHours / 3;
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Models.Contains(model, StringComparer.Ordinal))
        {
            throw new ArgumentOutOfRangeException(nameof(model), model, "不支持的预报模型。");
        }
        if (!await _operationLock.WaitAsync(0, cancellationToken))
        {
            throw new InvalidOperationException("已有数据任务正在运行，请稍候再试。");
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _shutdown.Token);
        try
        {
            Publish(
                BackgroundSyncState.Syncing,
                $"正在寻找 {model} 最新起报并下载完整 15 天预报");
            var progress = new DirectProgress<DataWorkerProgress>(item =>
            {
                Publish(
                    BackgroundSyncState.Syncing,
                    item.IsWarning ? $"首次准备提醒：{item.Message}" : item.Message);
                preparationProgress?.Report(item);
            });
            var result = await dataWorker.SyncLatestAsync(
                new SyncLatestRequest(
                    model,
                    password,
                    Math.Clamp(_settings.LatestProbeDays, 1, 14),
                    forecastHours),
                progress,
                linkedCancellation.Token);

            var availableLeads = result.InitKey is null
                ? new HashSet<int>()
                : result.Index.Runs
                    .Where(item =>
                        item.Model == model
                        && item.InitKey == result.InitKey)
                    .Select(item => item.LeadHours)
                    .ToHashSet();
            var isComplete = result.Found
                && Enumerable.Range(1, expectedRuns)
                    .All(index => availableLeads.Contains(index * 3));
            var availableRuns = availableLeads.Count(lead =>
                lead >= 3 && lead <= forecastHours && lead % 3 == 0);
            IndexUpdated?.Invoke(this, result.Index);

            var message = isComplete
                ? $"{model} 首次数据准备完成：{availableRuns}/{expectedRuns} 个预报时次"
                : result.Found
                    ? $"{model} 当前起报尚未完整：{availableRuns}/{expectedRuns} 个预报时次"
                    : $"最近 {_settings.LatestProbeDays} 天未找到 {model} 可用起报";
            await log.WriteAsync(
                isComplete ? "INFO" : "WARN",
                $"First forecast preparation finished. model={model}, init={result.InitKey}, complete={isComplete}, available={availableRuns}, expected={expectedRuns}, downloaded={result.Downloaded}, skipped={result.Skipped}, failed={result.Failed}.");
            ScheduleAfterCompletion(message, !isComplete);
            return new FirstForecastPreparationResult(
                result.Index,
                model,
                result.InitKey,
                expectedRuns,
                availableRuns,
                isComplete,
                result.Downloaded,
                result.Skipped,
                result.Failed);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await log.WriteAsync("ERROR", $"First forecast preparation failed: {exception}");
            ScheduleAfterCompletion(
                $"首次数据准备失败：{FriendlyError(exception)}",
                isError: true);
            throw;
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
                var startImmediately = _syncImmediatelyAfterCurrentOperation;
                _syncImmediatelyAfterCurrentOperation = false;
                var dueTime = startImmediately
                    ? TimeSpan.FromMilliseconds(250)
                    : interval;
                nextRun = DateTimeOffset.UtcNow.Add(dueTime);
                _timer = new Timer(
                    _ => _ = SyncNowAsync(
                        skipRecentCompleteModels: startImmediately),
                    null,
                    dueTime,
                    Timeout.InfiniteTimeSpan);
            }
            else
            {
                _syncImmediatelyAfterCurrentOperation = false;
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
        bool isError = false,
        double? progressPercent = null,
        string? activeModel = null,
        int? currentItem = null,
        int? totalItems = null,
        string? operationLabel = null,
        bool canCancel = false)
    {
        var modelStatuses = SnapshotModelStatuses();
        var status = new BackgroundSyncStatus(
            state,
            message,
            _settings.AutoSyncEnabled,
            nextRunUtc,
            DateTimeOffset.UtcNow,
            isError,
            progressPercent,
            activeModel,
            currentItem,
            totalItems,
            operationLabel,
            canCancel,
            modelStatuses,
            modelStatuses.Sum(item => item.BytesPerSecond));
        lock (_statusLock)
        {
            CurrentStatus = status;
        }
        StatusChanged?.Invoke(this, status);
    }

    private bool HasRecentCompleteRun(
        ForecastIndex index,
        string model,
        int forecastHours,
        DateTimeOffset nowUtc)
    {
        var cutoff = nowUtc.AddDays(-Math.Clamp(_settings.LatestProbeDays, 1, 14));
        var requiredLeads = Enumerable.Range(1, forecastHours / 3)
            .Select(item => item * 3)
            .ToHashSet();
        return index.Runs
            .Where(item => item.Model == model)
            .GroupBy(item => item.InitKey)
            .OrderByDescending(group => group.Key)
            .Any(group =>
                DateTimeOffset.TryParseExact(
                    group.Key,
                    "yyyyMMdd_HHmm",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var initUtc)
                && initUtc >= cutoff
                && requiredLeads.IsSubsetOf(group.Select(item => item.LeadHours)));
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

    public async Task StopForMigrationAsync(
        CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return;
        }
        Dispose();
        await _operationLock.WaitAsync(cancellationToken);
        _operationLock.Release();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        CancelActiveOperation();
        _shutdown.Cancel();
        lock (_timerLock)
        {
            _timer?.Dispose();
            _timer = null;
        }
        // Active workers observe the cancellation asynchronously and release the
        // semaphore in their finally blocks. The process is exiting, so leaving
        // these tiny primitives undisposed avoids racing that cleanup path.
    }

    private sealed class DirectProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    private sealed record ModelSyncOutcome(
        bool Found,
        int Downloaded,
        int Skipped,
        int Failed,
        Exception? Exception);

    private sealed class DownloadSpeedMeter
    {
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private long _lastBytes;
        private long _lastTimestamp;

        public double CurrentBytesPerSecond { get; private set; }

        public double Observe(long? receivedBytes)
        {
            if (receivedBytes is not { } bytes)
            {
                return CurrentBytesPerSecond;
            }
            var timestamp = _stopwatch.ElapsedMilliseconds;
            if (bytes < _lastBytes)
            {
                _lastBytes = 0;
                _lastTimestamp = timestamp;
            }
            var elapsed = timestamp - _lastTimestamp;
            if (elapsed < 180)
            {
                return CurrentBytesPerSecond;
            }
            var sample = Math.Max(0, bytes - _lastBytes) * 1000d / elapsed;
            CurrentBytesPerSecond = CurrentBytesPerSecond <= 0
                ? sample
                : CurrentBytesPerSecond * 0.62 + sample * 0.38;
            _lastBytes = bytes;
            _lastTimestamp = timestamp;
            return CurrentBytesPerSecond;
        }
    }
}
