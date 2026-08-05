using AISky_Desktop.Infrastructure;
using Microsoft.UI.Xaml;

namespace AISky_Desktop;

public partial class App : Application
{
    private Window? _window;

    public static AppServices Services { get; } = AppServices.CreateDefault();
    public Window? MainWindow => _window;

    public App()
    {
        UnhandledException += App_UnhandledException;
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var startInTray = Environment.GetCommandLineArgs()
            .Any(argument => argument.Equals("--background", StringComparison.OrdinalIgnoreCase));
        _window = new MainWindow(startInTray);
        _window.Activate();
    }

    private static void App_UnhandledException(
        object sender,
        Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        try
        {
            var paths = Services.Paths;
            paths.EnsureDirectories();
            File.AppendAllText(
                Path.Combine(paths.Logs, "startup-errors.log"),
                $"{DateTimeOffset.Now:O}{Environment.NewLine}{e.Exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Preserve the original crash if diagnostic logging also fails.
        }
    }
}
