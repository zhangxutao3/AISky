using AISky_Desktop.Infrastructure;
using Microsoft.UI.Xaml;

namespace AISky_Desktop;

public partial class App : Application
{
    private const string InstanceMutexName = "Local\\AISky.Desktop.SingleInstance";
    private const string ActivationEventName = "Local\\AISky.Desktop.Activate";
    private Window? _window;
    private Mutex? _instanceMutex;
    private EventWaitHandle? _activationEvent;
    private CancellationTokenSource? _activationListenerCancellation;
    private Task? _activationListenerTask;

    public static AppServices Services { get; } = AppServices.CreateDefault();
    public Window? MainWindow => _window;

    public App()
    {
        UnhandledException += App_UnhandledException;
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            using var activationEvent = new EventWaitHandle(
                initialState: false,
                EventResetMode.AutoReset,
                ActivationEventName);
            activationEvent.Set();
            _instanceMutex.Dispose();
            _instanceMutex = null;
            Exit();
            return;
        }

        StartActivationListener();
        var startInTray = Environment.GetCommandLineArgs()
            .Any(argument => argument.Equals("--background", StringComparison.OrdinalIgnoreCase));
        _window = new MainWindow(startInTray);
        _window.Activate();
    }

    private void StartActivationListener()
    {
        _activationEvent = new EventWaitHandle(
            initialState: false,
            EventResetMode.AutoReset,
            ActivationEventName);
        _activationListenerCancellation = new CancellationTokenSource();
        var cancellation = _activationListenerCancellation;
        var activationEvent = _activationEvent;
        _activationListenerTask = Task.Run(() =>
        {
            var handles = new WaitHandle[]
            {
                activationEvent,
                cancellation.Token.WaitHandle,
            };
            while (WaitHandle.WaitAny(handles) == 0)
            {
                _window?.DispatcherQueue.TryEnqueue(() =>
                {
                    if (_window is MainWindow mainWindow)
                    {
                        mainWindow.ShowFromTray();
                    }
                });
            }
        });
    }

    public void ShutdownSingleInstance()
    {
        var cancellation = _activationListenerCancellation;
        var activationEvent = _activationEvent;
        var listenerTask = _activationListenerTask;
        cancellation?.Cancel();
        activationEvent?.Set();
        _activationEvent = null;
        _activationListenerCancellation = null;
        _activationListenerTask = null;
        if (listenerTask is not null)
        {
            _ = listenerTask.ContinueWith(
                _ =>
                {
                    activationEvent?.Dispose();
                    cancellation?.Dispose();
                },
                TaskScheduler.Default);
        }
        else
        {
            activationEvent?.Dispose();
            cancellation?.Dispose();
        }
        if (_instanceMutex is not null)
        {
            _instanceMutex.ReleaseMutex();
            _instanceMutex.Dispose();
            _instanceMutex = null;
        }
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
