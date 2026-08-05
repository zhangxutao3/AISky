using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace AISky.Updater;

internal static class Program
{
    private const uint ErrorIcon = 0x00000010;
    private static readonly string LogPath =
        Path.Combine(Path.GetTempPath(), "AISky-Updater.log");

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var options = ParseArguments(args);
            var processId = ReadInteger(options, "--wait-pid");
            var package = RequireFile(options, "--package", ".zip");
            var target = RequireDirectory(options, "--target");
            var executableName = RequireFileName(options, "--executable");
            ApplyUpdate(processId, package, target, executableName);
            return 0;
        }
        catch (Exception exception)
        {
            WriteLog($"更新失败：{exception}");
            MessageBox(
                0,
                $"AISky 更新失败，原版本会尽量保持不变。\n\n{exception.Message}\n\n详情：{LogPath}",
                "AISky 更新助手",
                ErrorIcon);
            return 1;
        }
    }

    private static void ApplyUpdate(
        int processId,
        string package,
        string target,
        string executableName)
    {
        WriteLog($"开始更新。package={package}; target={target}; pid={processId}");
        WaitForProcess(processId);
        var targetDirectory = new DirectoryInfo(target);
        var parent = targetDirectory.Parent?.FullName
            ?? throw new InvalidOperationException("程序目录没有有效的父目录。");
        var staging = Path.Combine(parent, $".aisky-staging-{Guid.NewGuid():N}");
        var backup = Path.Combine(
            parent,
            $"{targetDirectory.Name}.backup-{DateTime.UtcNow:yyyyMMddHHmmss}");
        ExtractSafely(package, staging);
        if (!File.Exists(Path.Combine(staging, executableName)))
        {
            throw new InvalidOperationException(
                $"更新包中缺少主程序 {executableName}，没有修改现有安装。");
        }

        var targetMoved = false;
        try
        {
            MoveDirectoryWithRetry(target, backup);
            targetMoved = true;
            Directory.Move(staging, target);
            var updatedExecutable = Path.Combine(target, executableName);
            _ = Process.Start(new ProcessStartInfo
            {
                FileName = updatedExecutable,
                UseShellExecute = true,
                WorkingDirectory = target,
            }) ?? throw new InvalidOperationException("新版本安装完成，但无法重新启动。");
            WriteLog($"更新完成。备份保留在 {backup}");
        }
        catch
        {
            if (targetMoved && !Directory.Exists(target) && Directory.Exists(backup))
            {
                Directory.Move(backup, target);
                WriteLog("安装失败，已恢复原版本。");
            }
            throw;
        }
    }

    private static void WaitForProcess(int processId)
    {
        if (processId <= 0)
        {
            return;
        }
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.WaitForExit(60_000))
            {
                throw new TimeoutException("AISky 主程序没有在 60 秒内退出。");
            }
        }
        catch (ArgumentException)
        {
            // The application already exited.
        }
    }

    private static void ExtractSafely(string package, string staging)
    {
        Directory.CreateDirectory(staging);
        var stagingRoot = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(package);
        foreach (var entry in archive.Entries)
        {
            var destination = Path.GetFullPath(Path.Combine(staging, entry.FullName));
            if (!destination.StartsWith(stagingRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("更新包包含不安全的文件路径。");
            }
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destination);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }
    }

    private static void MoveDirectoryWithRetry(string source, string destination)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= 20; attempt++)
        {
            try
            {
                Directory.Move(source, destination);
                return;
            }
            catch (IOException exception)
            {
                lastError = exception;
                Thread.Sleep(500);
            }
            catch (UnauthorizedAccessException exception)
            {
                lastError = exception;
                Thread.Sleep(500);
            }
        }
        throw new IOException("程序文件仍被占用，无法开始覆盖更新。", lastError);
    }

    private static Dictionary<string, string> ParseArguments(string[] args)
    {
        if (args.Length % 2 != 0)
        {
            throw new ArgumentException("更新助手参数不完整。");
        }
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index += 2)
        {
            values[args[index]] = args[index + 1];
        }
        return values;
    }

    private static int ReadInteger(
        IReadOnlyDictionary<string, string> values,
        string name) =>
        values.TryGetValue(name, out var value) && int.TryParse(value, out var result)
            ? result
            : throw new ArgumentException($"缺少参数 {name}。");

    private static string RequireFile(
        IReadOnlyDictionary<string, string> values,
        string name,
        string extension)
    {
        if (!values.TryGetValue(name, out var value)
            || !File.Exists(value)
            || !Path.GetExtension(value).Equals(extension, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"{name} 指向的更新包无效。");
        }
        return Path.GetFullPath(value);
    }

    private static string RequireDirectory(
        IReadOnlyDictionary<string, string> values,
        string name)
    {
        if (!values.TryGetValue(name, out var value) || !Directory.Exists(value))
        {
            throw new ArgumentException($"{name} 指向的程序目录无效。");
        }
        var path = Path.GetFullPath(value).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        if (Path.GetPathRoot(path)?.Equals(
                path,
                StringComparison.OrdinalIgnoreCase) == true)
        {
            throw new ArgumentException("拒绝更新磁盘根目录。");
        }
        return path;
    }

    private static string RequireFileName(
        IReadOnlyDictionary<string, string> values,
        string name)
    {
        if (!values.TryGetValue(name, out var value)
            || string.IsNullOrWhiteSpace(value)
            || value != Path.GetFileName(value))
        {
            throw new ArgumentException($"{name} 必须是主程序文件名。");
        }
        return value;
    }

    private static void WriteLog(string message)
    {
        try
        {
            File.AppendAllText(
                LogPath,
                $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch
        {
            // Updating must not fail only because diagnostic logging is unavailable.
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBox(
        nint window,
        string text,
        string caption,
        uint type);
}
