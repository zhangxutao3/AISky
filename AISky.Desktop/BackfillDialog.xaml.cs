using AISky_Desktop.DataWorker;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AISky_Desktop;

public sealed partial class BackfillDialog : ContentDialog
{
    private static readonly string[] FixedTimes =
    [
        "01:30", "04:30", "07:30", "10:30",
        "13:30", "16:30", "19:30", "22:30",
    ];

    private readonly CancellationTokenSource _cancellation = new();
    private bool _isRunning;

    public BackfillDialog(string initialPassword = "")
    {
        InitializeComponent();
        PasswordInput.Password = initialPassword;
        foreach (var item in FixedTimes)
        {
            StartTimePicker.Items.Add(item);
            EndTimePicker.Items.Add(item);
        }

        var latest = LatestFixedTime(DateTimeOffset.UtcNow);
        StartDatePicker.Date = latest.AddDays(-1);
        EndDatePicker.Date = latest;
        StartTimePicker.SelectedItem = latest.AddDays(-1).ToString("HH:mm");
        EndTimePicker.SelectedItem = latest.ToString("HH:mm");
        Closed += (_, _) => _cancellation.Cancel();
    }

    public Func<ForecastIndex, Task>? IndexUpdated { get; set; }

    private async void ContentDialog_PrimaryButtonClick(
        ContentDialog sender,
        ContentDialogButtonClickEventArgs args)
    {
        if (_isRunning)
        {
            args.Cancel = true;
            return;
        }

        var deferral = args.GetDeferral();
        args.Cancel = true;
        try
        {
            var request = CreateRequest();
            _isRunning = true;
            IsPrimaryButtonEnabled = false;
            PrimaryButtonText = "正在下载";
            CloseButtonText = "取消下载";
            ProgressArea.Visibility = Visibility.Visible;
            ResultInfoBar.IsOpen = false;

            var progress = new Progress<DataWorkerProgress>(UpdateProgress);
            var result = await App.Services.DataWorker.DownloadRangeAsync(
                request,
                progress,
                _cancellation.Token);

            if (IndexUpdated is not null)
            {
                await IndexUpdated(result.Index);
            }

            ResultInfoBar.Severity = result.Failed == 0
                ? InfoBarSeverity.Success
                : InfoBarSeverity.Warning;
            ResultInfoBar.Title = result.Failed == 0 ? "下载完成" : "下载完成，但有失败项";
            ResultInfoBar.Message =
                $"新增 {result.Downloaded} 个，跳过已有 {result.Skipped} 个，失败 {result.Failed} 个。";
            ResultInfoBar.IsOpen = true;
            ProgressText.Text = "本地索引已刷新";
            ProgressValueText.Text = "100%";
            DownloadProgress.Value = 100;
            PrimaryButtonText = "完成";
            IsPrimaryButtonEnabled = true;
            CloseButtonText = "";
            _isRunning = false;
            args.Cancel = false;
        }
        catch (OperationCanceledException)
        {
            ResultInfoBar.Severity = InfoBarSeverity.Warning;
            ResultInfoBar.Title = "下载已取消";
            ResultInfoBar.Message = "已完成的文件会保留，未完成的临时文件不会进入可视化索引。";
            ResultInfoBar.IsOpen = true;
        }
        catch (Exception exception)
        {
            ResultInfoBar.Severity = InfoBarSeverity.Error;
            ResultInfoBar.Title = "下载没有完成";
            ResultInfoBar.Message = FriendlyMessage(exception);
            ResultInfoBar.IsOpen = true;
            PrimaryButtonText = "重试";
            IsPrimaryButtonEnabled = true;
            CloseButtonText = "关闭";
            _isRunning = false;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private DownloadRangeRequest CreateRequest()
    {
        if (StartDatePicker.Date is null || EndDatePicker.Date is null)
        {
            throw new InvalidOperationException("请选择开始日期和截止日期。");
        }
        if (StartTimePicker.SelectedItem is not string startText
            || EndTimePicker.SelectedItem is not string endText)
        {
            throw new InvalidOperationException("请选择固定起报时刻。");
        }

        var startTime = TimeOnly.Parse(startText);
        var endTime = TimeOnly.Parse(endText);
        var startDate = DateOnly.FromDateTime(StartDatePicker.Date.Value.UtcDateTime);
        var endDate = DateOnly.FromDateTime(EndDatePicker.Date.Value.UtcDateTime);
        var start = new DateTimeOffset(startDate.ToDateTime(startTime), TimeSpan.Zero);
        var end = new DateTimeOffset(endDate.ToDateTime(endTime), TimeSpan.Zero);
        if (start > end)
        {
            throw new InvalidOperationException("开始时间不能晚于截止时间。");
        }
        if (end - start > TimeSpan.FromDays(31))
        {
            throw new InvalidOperationException("单次回溯范围最多 31 天，请分批下载。");
        }

        var model = (ModelPicker.SelectedItem as ComboBoxItem)?.Content?.ToString()
            ?? "AISky-Energy";
        return new DownloadRangeRequest(model, start, end, PasswordInput.Password);
    }

    private void UpdateProgress(DataWorkerProgress progress)
    {
        ProgressText.Text = string.IsNullOrWhiteSpace(progress.Message)
            ? "正在处理数据"
            : progress.Message;
        if (progress.Percent is { } percent)
        {
            DownloadProgress.IsIndeterminate = false;
            DownloadProgress.Value = Math.Clamp(percent, 0, 100);
            ProgressValueText.Text = $"{percent:0}%";
        }
        else
        {
            DownloadProgress.IsIndeterminate = true;
            ProgressValueText.Text = progress.BytesReceived is { } received
                ? $"{received / 1024d / 1024d:0.0} MB"
                : "处理中";
        }
    }

    private static DateTimeOffset LatestFixedTime(DateTimeOffset utcNow)
    {
        var candidates = FixedTimes
            .Select(TimeOnly.Parse)
            .Select(time => new DateTimeOffset(
                utcNow.Year,
                utcNow.Month,
                utcNow.Day,
                time.Hour,
                time.Minute,
                0,
                TimeSpan.Zero))
            .Where(candidate => candidate <= utcNow)
            .ToList();
        return candidates.Count > 0
            ? candidates.Max()
            : new DateTimeOffset(utcNow.Year, utcNow.Month, utcNow.Day, 22, 30, 0, TimeSpan.Zero).AddDays(-1);
    }

    private static string FriendlyMessage(Exception exception)
    {
        var message = exception.Message;
        if (message.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            || message.Contains("timeout", StringComparison.OrdinalIgnoreCase))
        {
            return "连接数据服务器超时。请检查网络后重试，已下载成功的文件不会重复下载。";
        }
        if (message.Contains("csrf", StringComparison.OrdinalIgnoreCase)
            || message.Contains("password", StringComparison.OrdinalIgnoreCase))
        {
            return "数据访问验证失败，请检查访问密码。";
        }
        return message;
    }
}
