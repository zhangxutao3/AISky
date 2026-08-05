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
    }

    public DownloadRangeRequest? Request { get; private set; }

    private void ContentDialog_PrimaryButtonClick(
        ContentDialog sender,
        ContentDialogButtonClickEventArgs args)
    {
        try
        {
            Request = CreateRequest();
            ResultInfoBar.IsOpen = false;
        }
        catch (Exception exception)
        {
            ResultInfoBar.Severity = InfoBarSeverity.Error;
            ResultInfoBar.Title = "无法提交下载";
            ResultInfoBar.Message = FriendlyMessage(exception);
            ResultInfoBar.IsOpen = true;
            args.Cancel = true;
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
