using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using BehavePad.Agent;
using BehavePad.Controls;
using BehavePad.Core.Storage;
using BehavePad.Ipc;
using BehavePad.Services;
using BehavePad.Setup;
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
    private Mutex? _agentMutex;
    private EventWaitHandle? _activateEvent;
    private SettingsService? _settings;
    private ControllerService? _controller;
    private FilterService? _filter;
    private UpdateService? _update;
    private AgentLink? _link;
    private AgentHost? _agent;
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

        // A downloaded release started only to replace the copy of BehavePad that launched it.
        if (args.Length > 0 && args[0] == UpdateService.ApplyArgument)
        {
            Shutdown(UpdateService.RunApplyHelper(args));
            return;
        }

        // Register the attached properties before any template needs them.
        RuntimeHelpers.RunClassConstructor(typeof(Ui).TypeHandle);
        EnableBindingTrace();

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // Setup and removal. BehavePad is one self-contained file, so it installs itself rather than
        // shipping a second program that would only duplicate its look.
        if (args.Contains(AppInstaller.UninstallArgument))
        {
            ShowInstaller(uninstalling: true);
            return;
        }

        if (AppInstaller.ShouldRunInstaller(args))
        {
            ShowInstaller(uninstalling: false);
            return;
        }

        // The background agent: no window, just the controller, the filter and the tray icon.
        // --minimized is what a sign-in entry written before 1.5 says, and it meant the same thing:
        // start without showing a window. The agent rewrites that entry once it is up.
        if (args.Contains(AgentContract.AgentArgument) || args.Contains(StartupRegistration.MinimizedArgument))
        {
            StartAgent(args.Contains(DemoArgument));
            return;
        }

        var screenshotPath = ArgumentValue(args, ScreenshotArgument);
        var tourDirectory = ArgumentValue(args, TourArgument);
        var capturing = screenshotPath is not null || tourDirectory is not null;
        if (!capturing && !AcquireSingleInstance(args.Contains(SetupViewModel.RelaunchArgument)))
        {
            Shutdown();
            return;
        }

        _ = StartWindowAsync(args, capturing, screenshotPath, tourDirectory);
    }

    /// <summary>Shows the setup window, which shares BehavePad's own theme so it cannot drift from it.</summary>
    private void ShowInstaller(bool uninstalling)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var window = new InstallerWindow(new InstallerViewModel(uninstalling));
        window.Closed += (_, _) => Shutdown();
        window.Show();
    }

    /// <summary>Runs the background half. Nothing here needs a window, so none is created.</summary>
    private void StartAgent(bool demo)
    {
        _agentMutex = new Mutex(true, AgentContract.AgentMutexName, out var owned);
        if (!owned)
        {
            // An agent is already looking after this session.
            _agentMutex.Dispose();
            _agentMutex = null;
            Shutdown();
            return;
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        AppDomain.CurrentDomain.UnhandledException += (_, _) => _agent?.ShutdownBlocking();

        _agent = new AgentHost(demo);
        _agent.Exiting += (_, _) =>
        {
            _agent?.Dispose();
            _agent = null;
            Shutdown();
        };
        _agent.Start();
    }

    /// <summary>Runs the window, starting the agent first if nothing is looking after this session yet.</summary>
    private async Task StartWindowAsync(string[] args, bool capturing, string? screenshotPath, string? tourDirectory)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // A capture run hosts its own agent, so the screenshots never depend on what is already running.
        if (capturing)
        {
            _agent = new AgentHost(args.Contains(DemoArgument));
            _agent.Start();
        }
        else if (!await EnsureAgentAsync(args.Contains(DemoArgument)))
        {
            MessageBox.Show(
                "BehavePad could not start its background service, so there is nothing to show.",
                "BehavePad",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
            return;
        }

        _link = new AgentLink(Dispatcher);
        if (!await _link.ConnectAsync(TimeSpan.FromSeconds(10)))
        {
            MessageBox.Show(
                "BehavePad's background service is running but did not answer.",
                "BehavePad",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
            return;
        }

        _settings = SettingsService.Remote(_link);
        _controller = ControllerService.Remote();
        _filter = FilterService.Remote(_link, _controller, _settings);
        _update = UpdateService.Remote(_link, _settings);

        var shell = new ShellViewModel(_controller, _filter, _settings, _update, _link);
        shell.ActivateRequested += (_, _) => ShowMainWindow();
        if (Enum.TryParse<AppPage>(ArgumentValue(args, PageArgument), ignoreCase: true, out var page))
        {
            shell.CurrentPage = page;
        }

        _window = new MainWindow(shell);
        _window.Closing += OnWindowClosing;
        _window.Show();

        if (capturing)
        {
            _ = tourDirectory is not null ? CaptureTourAsync(shell, tourDirectory) : CaptureAndExitAsync(screenshotPath!);
        }
    }

    /// <summary>Connects to the agent, starting one and waiting for it when there is none.</summary>
    private async Task<bool> EnsureAgentAsync(bool demo)
    {
        using var probe = new AgentLink(Dispatcher);
        if (await probe.ConnectAsync(TimeSpan.FromMilliseconds(400)))
        {
            return true;
        }

        if (Environment.ProcessPath is not { } path)
        {
            return false;
        }

        try
        {
            var arguments = AgentContract.AgentArgument + (demo ? $" {DemoArgument}" : "");
            Process.Start(new ProcessStartInfo(path, arguments) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Trace.TraceError($"BehavePad could not start its agent: {ex.Message}");
            return false;
        }

        for (var attempt = 0; attempt < 20; attempt++)
        {
            using var retry = new AgentLink(Dispatcher);
            if (await retry.ConnectAsync(TimeSpan.FromMilliseconds(500)))
            {
                return true;
            }
        }

        return false;
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        _agent?.ShutdownBlocking();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activateEvent?.Dispose();
        _link?.Dispose();
        _agent?.Dispose();
        if (_agentMutex is not null)
        {
            try
            {
                _agentMutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }

            _agentMutex.Dispose();
        }

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

    /// <summary>
    /// Closing the window never stops the filter. The agent keeps running with its tray icon, and
    /// "Exit" on that icon is what actually stops BehavePad.
    /// </summary>
    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting)
        {
            return;
        }

        e.Cancel = true;
        Dispatcher.BeginInvoke(ExitApplication);
    }

    private void ExitApplication()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;

        // A capture run owns its agent, so it has to take it down as well.
        _agent?.ShutdownBlocking();
        _controller?.Dispose();
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
