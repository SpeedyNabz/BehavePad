using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Threading;
using BehavePad.Core.Engine;
using BehavePad.Ipc;
using BehavePad.Services;
using BehavePad.ViewModels;

namespace BehavePad.Agent;

/// <summary>
/// The background half of BehavePad. It owns the controller, the filter, the drivers and the tray icon,
/// and it keeps running whether or not a window is open. Windows attach over a named pipe, show what the
/// agent reports and ask it to do things, so the filter never drops just because someone closed a window.
/// </summary>
public sealed class AgentHost : IDisposable
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly SettingsService _settings;
    private readonly ControllerService _controller;
    private readonly FilterService _filter;
    private readonly UpdateService _update;
    private readonly TestRunner _test;
    private readonly AgentServer _server;
    private readonly TrayIcon _tray;
    private readonly DispatcherTimer _frameTimer;
    private AgentState _lastState = new();
    private bool _exiting;

    public AgentHost(bool demo)
    {
        _settings = new SettingsService();
        _controller = new ControllerService(_settings.Settings.UseDemoController || demo, _settings.Settings.PreferredSlot);
        _filter = new FilterService(_controller, _settings);
        _update = new UpdateService(_settings, () => _filter is { IsOn: false, IsBusy: false });
        _test = new TestRunner(_controller);

        _server = new AgentServer(action => _dispatcher.BeginInvoke(action));
        _server.CommandReceived += OnCommand;
        _server.ClientsChanged += (_, _) => PushState(force: true);

        _tray = new TrayIcon(OpenWindow, ToggleFilterAsync, ExitAll);

        _filter.PropertyChanged += OnFilterChanged;
        _settings.Changed += (_, _) => PushState();
        _update.PropertyChanged += OnUpdateChanged;
        _test.Changed += (_, _) => _server.Broadcast(AgentEvent.Test, _test.Snapshot);
        _controller.PropertyChanged += (_, _) => PushState();

        // Frames only go out while a window is watching; nothing is serialised for an idle agent.
        _frameTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(16) };
        _frameTimer.Tick += (_, _) =>
        {
            if (_server.HasClients)
            {
                _server.Broadcast(AgentEvent.Frame, _controller.Frame);
            }
        };
    }

    public event EventHandler? Exiting;

    public void Start()
    {
        _controller.Start();
        _server.Start();
        _frameTimer.Start();
        _tray.Update(_filter.IsOn);

        // A sign-in entry written before 1.5 starts the whole app. Point it at the agent instead.
        if (_settings.Settings.LaunchAtSignIn && !StartupRegistration.IsRegistered())
        {
            StartupRegistration.Apply(true);
        }

        _ = RunStartupTasksAsync();
    }

    /// <summary>Brings an open window forward, or starts one when there is none.</summary>
    public void OpenWindow()
    {
        if (_server.HasClients)
        {
            _server.Broadcast(AgentEvent.Activate);
            return;
        }

        try
        {
            if (Environment.ProcessPath is { } path)
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            Trace.TraceError($"BehavePad could not open its window: {ex.Message}");
        }
    }

    public void ShutdownBlocking()
    {
        _filter.ShutdownBlocking();
        _update.TryApply(relaunch: false);
    }

    public void Dispose()
    {
        _frameTimer.Stop();
        _server.Dispose();
        _tray.Dispose();
        _controller.Dispose();
    }

    private async Task ToggleFilterAsync()
    {
        if (_filter.IsOn)
        {
            await _filter.StopAsync();
        }
        else if (!_filter.IsBusy)
        {
            await _filter.StartAsync();
        }
    }

    private void ExitAll()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        ShutdownBlocking();
        Exiting?.Invoke(this, EventArgs.Empty);
    }

    private async Task RunStartupTasksAsync()
    {
        if (_update.TakeAppliedUpdate())
        {
            _tray.ShowNotice("BehavePad updated", $"BehavePad is now version {UpdateService.CurrentVersionText}.");
        }

        await _filter.RefreshDriversAsync();

        // Nothing starts the filter here. FilterService watches for the controller and turns it on once
        // it is actually there, which also covers a controller plugged in minutes after sign-in.
        if (_filter.HidHide.HasPendingRestore && !_filter.IsArmed)
        {
            await _filter.RestoreVisibilityAsync();
        }

        await _update.CheckAsync(automatic: true);

        var updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        updateTimer.Tick += async (_, _) => await _update.CheckAsync(automatic: true);
        updateTimer.Start();
    }

    private void OnFilterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FilterService.State))
        {
            _tray.Update(_filter.IsOn);
        }

        PushState();
    }

    private void OnUpdateChanged(object? sender, PropertyChangedEventArgs e)
    {
        PushState();
        if (e.PropertyName == nameof(UpdateService.State) && _update is { IsReady: true, StagedVersion: { } version })
        {
            _tray.ShowNotice(
                $"BehavePad {version} is ready",
                "It installs the next time BehavePad exits. To update now, choose Restart and install on the Setup page.");
        }
    }

    private void OnCommand(object? sender, IpcEnvelope envelope)
    {
        if (!Enum.TryParse<AgentCommand>(envelope.Kind, out var command))
        {
            return;
        }

        switch (command)
        {
            case AgentCommand.Hello:
                PushState(force: true);
                _server.Broadcast(AgentEvent.Test, _test.Snapshot);
                break;
            case AgentCommand.StartFilter:
                _ = _filter.StartAsync();
                break;
            case AgentCommand.StopFilter:
                _ = _filter.StopAsync();
                break;
            case AgentCommand.ToggleFilter:
                _ = ToggleFilterAsync();
                break;
            case AgentCommand.RefreshDrivers:
                _ = _filter.RefreshDriversAsync();
                break;
            case AgentCommand.InstallDrivers:
                _ = _filter.InstallDriversAsync();
                break;
            case AgentCommand.RestoreVisibility:
                _ = _filter.RestoreVisibilityAsync();
                break;
            case AgentCommand.DismissMessage:
                _filter.Message = null;
                break;
            case AgentCommand.UpdateSettings:
                ApplySettings(envelope.Read<AppSettings>());
                break;
            case AgentCommand.SaveProfile:
                if (envelope.Read<Core.Filtering.FilterProfile>() is { } profile)
                {
                    _settings.SaveProfile(profile);
                    _filter.ApplyProfile(_settings.Profile, keepLearned: true);
                }

                break;
            case AgentCommand.SaveTest:
                if (envelope.Read<SaveTestRequest>() is { } save)
                {
                    _settings.SaveTest(save.Report, save.Profile);
                    _filter.ApplyProfile(_settings.Profile);
                    _test.Reset();
                    if (save.TurnOn && !_filter.IsOn)
                    {
                        _ = _filter.StartAsync();
                    }
                }

                break;
            case AgentCommand.ResetProfile:
                _settings.ResetAll();
                _filter.ApplyProfile(null);
                _test.Reset();
                break;
            case AgentCommand.ForgetLearned:
                ForgetLearned();
                break;
            case AgentCommand.CheckForUpdates:
                _ = _update.CheckAsync(automatic: false);
                break;
            case AgentCommand.InstallUpdate:
                _ = InstallUpdateAsync();
                break;
            case AgentCommand.SetDemoScenario:
                if (envelope.Read<DemoScenarioRequest>() is { } scenario)
                {
                    _controller.SetDemoScenario(scenario.Scenario);
                }

                break;
            case AgentCommand.BeginTest:
                _test.Begin(envelope.Read<BeginTestRequest>()?.Length ?? RestLength.Standard);
                break;
            case AgentCommand.CancelTest:
                _test.Cancel();
                break;
            case AgentCommand.SkipSnapBack:
                _test.SkipSnapBack();
                break;
            case AgentCommand.ExitAgent:
                ExitAll();
                break;
        }
    }

    private void ApplySettings(AppSettings? next)
    {
        if (next is null)
        {
            return;
        }

        var previous = _settings.Settings;
        _settings.Update(_ => next);

        if (next.UseDemoController != previous.UseDemoController)
        {
            _controller.UseDemo(next.UseDemoController);
        }

        if (next.PreferredSlot != previous.PreferredSlot && _controller.Pump is { } pump)
        {
            pump.PreferredSlot = next.PreferredSlot;
        }

        if (next.LaunchAtSignIn != previous.LaunchAtSignIn)
        {
            StartupRegistration.Apply(next.LaunchAtSignIn);
        }
    }

    private void ForgetLearned()
    {
        if (_settings.Profile is not { } profile)
        {
            return;
        }

        var cleared = profile.WithLearned([], []);
        _settings.SaveProfile(cleared);
        _filter.ApplyProfile(_settings.Profile);
    }

    private async Task InstallUpdateAsync()
    {
        await _filter.StopAsync();
        if (_update.TryApply(relaunch: true))
        {
            ExitAll();
        }
    }

    /// <summary>Sends the state when anything in it moved, so an idle agent stays quiet on the wire.</summary>
    private void PushState(bool force = false)
    {
        if (!_server.HasClients && !force)
        {
            return;
        }

        var state = Capture();
        if (!force && state == _lastState)
        {
            return;
        }

        _lastState = state;
        _server.Broadcast(AgentEvent.State, state);
    }

    private AgentState Capture() => new()
    {
        Connected = _controller.IsConnected,
        Slot = _controller.Slot,
        IsDemo = _controller.IsDemo,
        ControllerName = _controller.ControllerName,
        ControllerStatusText = _controller.StatusText,
        BatteryText = _controller.BatteryText,
        HasXInput = _controller.HasXInput,

        FilterState = _filter.State,
        PhysicalHidden = _filter.PhysicalHidden,
        VirtualSlot = _filter.VirtualSlot,
        Message = _filter.Message,
        MessageIsError = _filter.MessageIsError,
        IsArmed = _filter.IsArmed,
        HasPendingRestore = _filter.HidHide.HasPendingRestore,

        Vigem = _filter.Vigem,
        HidHide = _filter.HidHideDriver,
        DriverStatus = _filter.DriverSetup.Status,
        DriverStatusIsError = _filter.DriverSetup.StatusIsError,
        DriverBusy = _filter.DriverSetup.IsBusy,
        DriverProgress = _filter.DriverSetup.Progress,

        Settings = _settings.Settings,
        Profile = _settings.Profile,
        Report = _settings.LastReport,

        UpdateState = _update.State,
        UpdateStatus = _update.Status,
        UpdateStatusIsError = _update.StatusIsError,
        UpdateProgress = _update.Progress,
        StagedVersion = _update.StagedVersion,
        IsElevated = Elevation.IsElevated,
    };
}
