using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using BehavePad.Controls;
using BehavePad.Core.Storage;
using BehavePad.Services;
using BehavePad.ViewModels;

namespace BehavePad;

public partial class App : Application
{
    private const string MutexName = @"Local\BehavePad.SingleInstance";
    private const string ActivateEventName = @"Local\BehavePad.Activate";
    private const string DemoArgument = "--demo";
    private const string PageArgument = "--page";
    private const string ScreenshotArgument = "--screenshot";
    private const string TourArgument = "--capture-tour";

    private Mutex? _mutex;
    private EventWaitHandle? _activateEvent;
    private SettingsService? _settings;
    private ControllerService? _controller;
    private FilterService? _filter;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;

        // An elevated copy of BehavePad started only to change HidHide settings.
        if (args.Length > 0 && args[0] is HidHideManager.HideArgument or HidHideManager.RestoreArgument)
        {
            Shutdown(HidHideManager.RunHelper(args));
            return;
        }

        // An elevated copy of BehavePad started only to run the driver installers.
        if (args.Length > 0 && args[0] == DriverSetupService.InstallArgument)
        {
            Shutdown(DriverSetupService.RunHelper(args));
            return;
        }

        // Register the attached properties before any template needs them.
        RuntimeHelpers.RunClassConstructor(typeof(Ui).TypeHandle);
        EnableBindingTrace();

        var screenshotPath = ArgumentValue(args, ScreenshotArgument);
        var tourDirectory = ArgumentValue(args, TourArgument);
        var capturing = screenshotPath is not null || tourDirectory is not null;
        if (!capturing && !AcquireSingleInstance(args.Contains(SetupViewModel.RelaunchArgument)))
        {
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, _) => _filter?.ShutdownBlocking();

        _settings = new SettingsService();
        var demo = _settings.Settings.UseDemoController || args.Contains(DemoArgument);
        _controller = new ControllerService(demo, _settings.Settings.PreferredSlot);
        _filter = new FilterService(_controller, _settings);
        var shell = new ShellViewModel(_controller, _filter, _settings);
        if (Enum.TryParse<AppPage>(ArgumentValue(args, PageArgument), ignoreCase: true, out var page))
        {
            shell.CurrentPage = page;
        }

        _window = new MainWindow(shell);
        _window.Closing += OnWindowClosing;
        _controller.Start();

        if (capturing)
        {
            _window.Show();
            _ = tourDirectory is not null ? CaptureTourAsync(shell, tourDirectory) : CaptureAndExitAsync(screenshotPath!);
            return;
        }

        _tray = new TrayIcon(ShowMainWindow, () => shell.ToggleFilterCommand.ExecuteAsync(null), ExitApplication);
        _filter.PropertyChanged += OnFilterPropertyChanged;

        if (!args.Contains(StartupRegistration.MinimizedArgument))
        {
            _window.Show();
        }

        _ = RunStartupTasksAsync();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        _filter?.ShutdownBlocking();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activateEvent?.Dispose();
        if (_mutex is not null)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }

            _mutex.Dispose();
        }

        base.OnExit(e);
    }

    private static string? ArgumentValue(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    /// <summary>Writes WPF binding problems to the file named by BEHAVEPAD_TRACE, for troubleshooting.</summary>
    private static void EnableBindingTrace()
    {
        if (Environment.GetEnvironmentVariable("BEHAVEPAD_TRACE") is not { Length: > 0 } tracePath)
        {
            return;
        }

        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(new TextWriterTraceListener(tracePath));
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        Trace.AutoFlush = true;
    }

    private async Task RunStartupTasksAsync()
    {
        await _filter!.RefreshDriversAsync();

        var autoStart = _settings!.Settings.StartFilterOnLaunch && _settings.Profile is not null;
        if (_filter.HidHide.HasPendingRestore && !autoStart)
        {
            // The last session ended while a controller was hidden. Put it back.
            await _filter.RestoreVisibilityAsync();
        }

        if (autoStart)
        {
            await _filter.StartAsync(installDrivers: false);
        }
    }

    private async Task CaptureAndExitAsync(string path)
    {
        await Task.Delay(2500);
        _window!.SaveScreenshot(path);
        ExitApplication();
    }

    private bool AcquireSingleInstance(bool waitForPrevious)
    {
        _mutex = new Mutex(true, MutexName, out var owned);
        if (!owned && waitForPrevious)
        {
            try
            {
                owned = _mutex.WaitOne(TimeSpan.FromSeconds(15));
            }
            catch (AbandonedMutexException)
            {
                owned = true;
            }
        }

        if (!owned)
        {
            try
            {
                using var activate = EventWaitHandle.OpenExisting(ActivateEventName);
                activate.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
            }

            _mutex.Dispose();
            _mutex = null;
            return false;
        }

        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        var listener = new Thread(() =>
        {
            try
            {
                while (_activateEvent.WaitOne())
                {
                    Dispatcher.BeginInvoke(ShowMainWindow);
                }
            }
            catch (ObjectDisposedException)
            {
            }
        })
        {
            IsBackground = true,
            Name = "BehavePad activation listener",
        };
        listener.Start();
        return true;
    }

    private void ShowMainWindow()
    {
        if (_window is null || _exiting)
        {
            return;
        }

        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
    }

    private void OnFilterPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FilterService.State))
        {
            Dispatcher.BeginInvoke(() => _tray?.Update(_filter!.IsOn));
        }
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting)
        {
            return;
        }

        e.Cancel = true;
        if (_settings!.Settings.MinimizeToTray && _filter!.IsOn && _tray is not null)
        {
            _window!.Hide();
            _tray.ShowNotice("BehavePad is still filtering", "Your games keep getting clean input. Open BehavePad from the notification area.");
            return;
        }

        // A window can't be closed again from inside its own Closing event, so exit right after it returns.
        Dispatcher.BeginInvoke(ExitApplication);
    }

    private void ExitApplication()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        _filter?.ShutdownBlocking();
        _controller?.Dispose();
        _tray?.Dispose();
        _window?.Close();
        Shutdown();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            File.AppendAllText(Path.Combine(AppPaths.DataDirectory, "error.log"), $"{DateTimeOffset.Now:u} {e.Exception}{Environment.NewLine}");
        }
        catch (Exception)
        {
        }

        Trace.TraceError(e.Exception.ToString());
        if (_window?.IsVisible == true && Environment.GetEnvironmentVariable("BEHAVEPAD_DATA_DIR") is null)
        {
            MessageBox.Show(
                $"BehavePad hit a problem and recovered.\n\n{e.Exception.Message}",
                "BehavePad",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        e.Handled = true;
    }
}
