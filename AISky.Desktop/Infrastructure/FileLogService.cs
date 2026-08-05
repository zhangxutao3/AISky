using System.Text;
using AISky_Desktop.Core;

namespace AISky_Desktop.Infrastructure;

public sealed class FileLogService(AppPaths paths)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task WriteAsync(string level, string message)
    {
        var file = Path.Combine(paths.Logs, $"aisky-{DateTime.Now:yyyyMMdd}.log");
        var line = $"{DateTimeOffset.Now:O}\t{level}\t{message}{Environment.NewLine}";

        await _gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(paths.Logs);
            await File.AppendAllTextAsync(file, line, Encoding.UTF8);
        }
        finally
        {
            _gate.Release();
        }
    }
}
