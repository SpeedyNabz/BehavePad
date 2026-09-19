using BehavePad.Core.Engine;
using BehavePad.Core.Filtering;
using BehavePad.Core.Input;
using BehavePad.Core.Setup;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BehavePad.Services;

public enum FilterState
{
    Off,
    Starting,
    On,
    Stopping,
}

/// <summary>Who asked for the filter, which decides whether a problem is worth interrupting anyone about.</summary>
public enum FilterStart
{
    /// <summary>A person pressed something, so tell them when it does not work.</summary>
    User,

    /// <summary>BehavePad started it on its own. Stay quiet, and never ask Windows for permission unprompted.</summary>
    Automatic,
}

/// <summary>
/// Turns in-game filtering on and off. The filter always runs as a preview so the app can show what games
/// would receive. Turning it on adds a virtual controller for games and hides the physical one.
/// </summary>
public sealed partial class FilterService : ObservableObject
{
    private static readonly TimeSpan VirtualSlotTimeout = TimeSpan.FromSeconds(3);

    /// <summary>How long a connection has to hold before BehavePad acts, so a flaky wireless link cannot flap the filter.</summary>
    private const double ConnectSettleMs = 1000;

    /// <summary>How long a controller can stay away before BehavePad packs the filter away and waits for it again.</summary>
    private const double AbsenceTeardownMs = 5 * 60 * 1000;

    private readonly ControllerService _controller;
    private readonly SettingsService _settings;
    private VirtualXboxPad? _pad;
    private int _savedGrowths;
    private bool _wasConnected;
    private double _connectedSinceMs = double.NegativeInfinity;
    private double _absentSinceMs = double.NegativeInfinity;
    private bool _newArrival;
    private bool _autoStartFailed;
    private bool _autoBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOn), nameof(IsBusy))]
    private FilterState _state;

    [ObservableProperty]
    private bool _physicalHidden;

    [ObservableProperty]
    private int? _virtualSlot;

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private bool _messageIsError;

    /// <summary>True when BehavePad will turn the filter on by itself as soon as a tested controller is connected.</summary>
    [ObservableProperty]
    private bool _isArmed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DriversReady))]
    private DriverInfo _vigem = new(false, null, null);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DriversReady))]
    private DriverInfo _hidHideDriver = new(false, null, null);

    public FilterService(ControllerService controller, SettingsService settings)
    {
        _controller = controller;
        _settings = settings;
        _controller.ForwardingFailed += OnForwardingFailed;
        _controller.FrameUpdated += OnFrameUpdated;
        _settings.Changed += OnSettingsChanged;
        ApplyProfile(settings.Profile);
        RefreshArmed();
    }

    public HidHideManager HidHide { get; } = new();

    public DriverSetupService DriverSetup { get; } = new();

    public bool IsOn => State == FilterState.On;

    public bool IsBusy => State is FilterState.Starting or FilterState.Stopping;

    public bool DriversReady => Vigem.Ready && HidHideDriver.Ready;

    public async Task RefreshDriversAsync()
    {
        var (vigem, hidHide) = await Task.Run(() => (DriverStatus.CheckVigem(), DriverStatus.CheckHidHide()));
        Vigem = vigem;
        HidHideDriver = hidHide;
    }

    /// <summary>Uses a new profile right away, for both the preview and games. Returns the profile now in use.</summary>
    /// <param name="keepLearned">Carries over what the running filter learned during play, for edits to the same test.</param>
    public FilterProfile? ApplyProfile(FilterProfile? profile, bool keepLearned = false)
    {
        if (profile is not null && keepLearned)
        {
            profile = WithLearned(profile);
        }

        _savedGrowths = 0;
        _controller.Pump.Filter = profile is null ? null : new InputFilter(profile);
        return profile;
    }

    /// <summary>
    /// Copies the spots the running filter learned during play into a profile, for each stick whose tested outline
    /// matches. Returns the same profile when nothing differs.
    /// </summary>
    public FilterProfile WithLearned(FilterProfile profile)
    {
        if (_controller.Pump.Filter is not { } filter)
        {
            return profile;
        }

        IReadOnlyList<StickPoint> LearnedFor(StickSide side) =>
            filter.Profile.Stick(side).Outline.SequenceEqual(profile.Stick(side).Sanitized().Outline)
                ? filter.LearnedPoints(side)
                : profile.Stick(side).Learned;

        var left = LearnedFor(StickSide.Left);
        var right = LearnedFor(StickSide.Right);
        return left.SequenceEqual(profile.LeftStick.Learned) && right.SequenceEqual(profile.RightStick.Learned)
            ? profile
            : profile.WithLearned(left, right);
    }

    /// <summary>Installs whichever drivers are missing, then checks them again.</summary>
    public async Task<DriverSetupOutcome> InstallDriversAsync()
    {
        await RefreshDriversAsync();
        var packages = new List<DriverPackage>();
        if (!Vigem.Ready)
        {
            packages.Add(DriverPackages.ViGEmBus);
        }

        if (!HidHideDriver.Installed)
        {
            packages.Add(DriverPackages.HidHide);
        }

        var outcome = await DriverSetup.InstallAsync(packages);
        await RefreshDriversAsync();

        // Windows can need a restart before it loads a new driver, even when setup reports success.
        if ((outcome.Result is DriverSetupResult.Installed or DriverSetupResult.NothingToInstall) && !DriversReady)
        {
            outcome = DriverSetup.Show(new DriverSetupOutcome(
                DriverSetupResult.RestartRequired,
                Vigem.Problem ?? HidHideDriver.Problem ?? "Restart your PC to finish setting up the drivers."));
        }

        return outcome;
    }

    /// <param name="trigger">
    /// An automatic start keeps quiet about anything a person did not ask for, and never installs drivers,
    /// so Windows is never asked for permission out of the blue.
    /// </param>
    public async Task<bool> StartAsync(FilterStart trigger = FilterStart.User)
    {
        // A problem only earns a banner when somebody was waiting for the filter to come on.
        void Report(string message, bool isError = true)
        {
            if (trigger == FilterStart.User)
            {
                SetMessage(message, isError);
            }
        }

        if (State != FilterState.Off)
        {
            return IsOn;
        }

        if (_settings.Profile is null)
        {
            Report("Run the drift test first so BehavePad knows what to filter.");
            return false;
        }

        var pump = _controller.Pump;
        var demo = _controller.IsDemo;
        var physicalSlot = pump.Latest.Connected ? pump.Latest.Slot : -1;
        if (!demo && physicalSlot < 0)
        {
            Report("Connect your controller before turning the filter on.");
            return false;
        }

        State = FilterState.Starting;
        SetMessage(null);

        try
        {
            await RefreshDriversAsync();
            if (!Vigem.Ready && trigger == FilterStart.User && !DriverSetup.IsBusy)
            {
                SetMessage("Installing the drivers BehavePad needs to filter inside games. Windows will ask for permission.");
                var outcome = await InstallDriversAsync();
                if (!Vigem.Ready)
                {
                    var restart = outcome.Result == DriverSetupResult.RestartRequired;
                    Report(restart ? $"{outcome.Message} Then turn the filter on again." : outcome.Message, isError: !restart);
                    State = FilterState.Off;
                    return false;
                }

                SetMessage(null);
            }

            if (!Vigem.Ready)
            {
                Report(Vigem.Problem ?? "BehavePad needs ViGEmBus to filter inside games. Choose Install drivers on the Setup page.");
                State = FilterState.Off;
                return false;
            }

            if (!demo)
            {
                // Stay on the real controller while the virtual one appears.
                pump.PreferredSlot = physicalSlot;
            }

            var pad = _pad = await Task.Run(VirtualXboxPad.Create);
            VirtualSlot = _controller.XInput is { } xinput
                ? await VirtualSlotLocator.FindAsync(xinput, state => pad.Submit(state), VirtualSlotTimeout)
                : null;

            if (!demo && VirtualSlot is int virtualSlot)
            {
                pump.ExcludedSlot = virtualSlot;
                var preferred = _settings.Settings.PreferredSlot;
                pump.PreferredSlot = preferred == virtualSlot ? InputPump.AutoSlot : preferred;
            }

            // If the virtual slot could not be found, the pump stays locked to the real controller's slot,
            // so it can never end up reading BehavePad's own output.
            pump.Sink = pad;

            if (_settings.Settings.HidePhysicalController && !demo)
            {
                await HidePhysicalAsync();
            }

            State = FilterState.On;
            return true;
        }
        catch (Exception ex)
        {
            await TearDownAsync();
            Report($"Could not start the filter. {ex.Message}");
            State = FilterState.Off;
            return false;
        }
    }

    public async Task StopAsync()
    {
        if (State != FilterState.On)
        {
            return;
        }

        State = FilterState.Stopping;
        await TearDownAsync();
        State = FilterState.Off;
    }

    public async Task RestoreVisibilityAsync()
    {
        var outcome = await HidHide.RestoreAsync();
        switch (outcome.Kind)
        {
            case HideResultKind.Done:
            case HideResultKind.NotInstalled:
                PhysicalHidden = false;
                SetMessage("Your controller is visible to games again.");
                break;
            default:
                SetMessage(outcome.Message ?? "BehavePad could not restore the controller.", isError: true);
                break;
        }
    }

    /// <summary>Stops filtering during app exit and waits until the controller is visible to games again.</summary>
    public void ShutdownBlocking()
    {
        try
        {
            Task.Run(TearDownAsync).Wait(TimeSpan.FromSeconds(30));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"Filter shutdown failed: {ex}");
        }
    }

    private async Task HidePhysicalAsync()
    {
        await RefreshDriversAsync();
        if (!HidHideDriver.Ready)
        {
            SetMessage(HidHideDriver.Problem ?? "Filter is on. Choose Install drivers on the Setup page to add HidHide, so games stop seeing the original controller too.");
            return;
        }

        var devices = await Task.Run(ControllerDevices.FindPhysicalControllerNodes);
        var outcome = await HidHide.HideAsync(devices);
        PhysicalHidden = outcome.Kind == HideResultKind.Done && outcome.FilterActive;

        if (outcome.Kind != HideResultKind.Done)
        {
            SetMessage($"Filter is on, but games can still see the original controller. {outcome.Message}");
        }
        else if (!outcome.FilterActive)
        {
            SetMessage("Filter is on, but HidHide isn't active on your controller yet, so games can still see it. Unplug the controller, plug it back in, then turn the filter off and on again.");
        }
        else if (outcome.ReplugRecommended)
        {
            // Hiding only takes effect the next time something opens the controller, and BehavePad never forces
            // that, so say what the user can do instead of quietly leaving a game reading the drift.
            SetMessage("Filter is on. If a game was already open, unplug your controller and plug it back in so it stops reading the original, then restart the game.");
        }
        else
        {
            SetMessage("Filter is on. Restart any game that was already open so it picks up the clean controller.");
        }
    }

    private async Task TearDownAsync()
    {
        var pump = _controller.Pump;
        pump.Sink = null;
        _pad?.Dispose();
        _pad = null;
        VirtualSlot = null;
        pump.ExcludedSlot = -1;
        pump.PreferredSlot = _settings.Settings.PreferredSlot;

        var slot = pump.Latest.Slot;
        if (slot >= 0)
        {
            try
            {
                pump.Source.SetVibration(slot, 0, 0);
            }
            catch (Exception)
            {
            }
        }

        if (PhysicalHidden || HidHide.HasPendingRestore)
        {
            var outcome = await HidHide.RestoreAsync().ConfigureAwait(false);
            if (outcome.Kind is HideResultKind.Done or HideResultKind.NotInstalled)
            {
                PhysicalHidden = false;
            }
            else
            {
                SetMessage("Your controller is still hidden from games. Open Setup and choose Restore controller visibility.", isError: true);
            }
        }
    }

    private async void OnForwardingFailed(object? sender, string error)
    {
        if (State != FilterState.On)
        {
            return;
        }

        State = FilterState.Stopping;
        await TearDownAsync();
        State = FilterState.Off;
        SetMessage($"The filter turned off because the virtual controller stopped responding. {error}", isError: true);
    }

    private void OnFrameUpdated(object? sender, EventArgs e)
    {
        TrackConnection();
        SaveLearnedZones();
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        RefreshArmed();

        // Saving a test or turning the setting back on is a fresh chance for a start that failed before.
        _autoStartFailed = false;
    }

    private void RefreshArmed() =>
        IsArmed = _settings.Settings.AutoFilterWhenConnected && _settings.Profile is not null;

    /// <summary>
    /// Follows the controller coming and going. A tested controller gets its filter without anyone asking,
    /// a controller swapped in mid-session still gets hidden from games, and one that is put away for the
    /// evening does not leave an idle virtual controller behind.
    /// </summary>
    private void TrackConnection()
    {
        var frame = _controller.Frame;
        var now = frame.TimestampMs;
        var connected = frame.Connected;

        if (connected != _wasConnected)
        {
            _wasConnected = connected;
            if (connected)
            {
                _connectedSinceMs = now;
                _newArrival = true;
            }
            else
            {
                _absentSinceMs = now;
                _newArrival = false;

                // Unplugging and plugging back in is the natural way to retry, so let it.
                _autoStartFailed = false;
            }
        }

        if (_autoBusy)
        {
            return;
        }

        if (connected)
        {
            if (now - _connectedSinceMs < ConnectSettleMs)
            {
                return;
            }

            var arrived = _newArrival;
            _newArrival = false;

            if (State == FilterState.Off && IsArmed && !_autoStartFailed)
            {
                _autoBusy = true;
                _ = RunAutoAsync(AutoStartAsync());
            }
            else if (arrived && State == FilterState.On && PhysicalHidden)
            {
                _autoBusy = true;
                _ = RunAutoAsync(HideNewControllersAsync());
            }
        }
        else if (State == FilterState.On && IsArmed && now - _absentSinceMs >= AbsenceTeardownMs)
        {
            _autoBusy = true;
            _ = RunAutoAsync(StandDownAsync());
        }
    }

    /// <summary>Runs one piece of automatic work at a time, so the 60 Hz frame tick cannot pile them up.</summary>
    private async Task RunAutoAsync(Task work)
    {
        try
        {
            await work;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"Automatic filter work failed: {ex}");
        }
        finally
        {
            _autoBusy = false;
        }
    }

    private async Task AutoStartAsync()
    {
        if (!await StartAsync(FilterStart.Automatic))
        {
            // Stop trying until something changes, rather than rebuilding a virtual controller every second.
            _autoStartFailed = true;
        }
    }

    /// <summary>
    /// Hides a controller that arrived after the filter started. The hidden list is checked first, because
    /// hiding asks Windows for permission and a controller that is already hidden does not need asking again.
    /// </summary>
    private async Task HideNewControllersAsync()
    {
        if (!_settings.Settings.HidePhysicalController || _controller.IsDemo)
        {
            return;
        }

        var devices = await Task.Run(ControllerDevices.FindPhysicalControllerNodes);
        var hidden = HidHide.HiddenInstanceIds;
        if (devices.Count == 0 || devices.All(d => hidden.Contains(d.InstanceId)))
        {
            return;
        }

        await HidePhysicalAsync();
    }

    private async Task StandDownAsync()
    {
        await StopAsync();
        SetMessage("Your controller has been away for a while, so BehavePad put the filter away. It comes back on when the controller does.");
    }

    /// <summary>Saves spots the filter learns during play, so they survive a restart.</summary>
    private void SaveLearnedZones()
    {
        var growths = _controller.Frame.Statistics.ZoneGrowths;
        if (growths == _savedGrowths)
        {
            return;
        }

        _savedGrowths = growths;
        if (_settings.Profile is { } saved && WithLearned(saved) is var updated && !ReferenceEquals(updated, saved))
        {
            _settings.SaveProfile(updated);
        }
    }

    private void SetMessage(string? message, bool isError = false)
    {
        Message = message;
        MessageIsError = isError && message is not null;
    }
}
