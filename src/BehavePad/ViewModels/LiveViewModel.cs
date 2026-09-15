using System.Collections.ObjectModel;
using System.Windows.Threading;
using BehavePad.Core.Filtering;
using BehavePad.Core.Input;
using BehavePad.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BehavePad.ViewModels;

/// <summary>One button chip on the live page.</summary>
public sealed partial class ButtonLight : ObservableObject
{
    [ObservableProperty]
    private bool _isDown;

    [ObservableProperty]
    private bool _isBlocked;

    public ButtonLight(GamepadButtons button, string label)
    {
        Button = button;
        Label = label;
    }

    public GamepadButtons Button { get; }

    public string Label { get; }
}

/// <summary>Shows raw input next to what games receive, and lets the user fine-tune the filter.</summary>
public sealed partial class LiveViewModel : ObservableObject, IPageViewModel
{
    private readonly ShellViewModel _shell;
    private readonly DispatcherTimer _saveTimer;
    private FilterProfile? _pending;
    private bool _syncing;
    private InputFilter? _shownFilter;
    private int _shownGrowths;

    [ObservableProperty]
    private StickPoint _leftRaw;

    [ObservableProperty]
    private StickPoint _rightRaw;

    [ObservableProperty]
    private StickPoint _leftOutput;

    [ObservableProperty]
    private StickPoint _rightOutput;

    [ObservableProperty]
    private StickPoint _leftCenter;

    [ObservableProperty]
    private StickPoint _rightCenter;

    [ObservableProperty]
    private double _leftTriggerRaw;

    [ObservableProperty]
    private double _leftTriggerOutput;

    [ObservableProperty]
    private double _rightTriggerRaw;

    [ObservableProperty]
    private double _rightTriggerOutput;

    [ObservableProperty]
    private string _leftBlockedText = "0s";

    [ObservableProperty]
    private string _rightBlockedText = "0s";

    [ObservableProperty]
    private string _phantomBlockedText = "0";

    [ObservableProperty]
    private string _pollRateText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlotZoom))]
    private bool _zoomIn;

    [ObservableProperty]
    private double _leftDeadzone;

    [ObservableProperty]
    private double _rightDeadzone;

    [ObservableProperty]
    private double _leftTriggerDeadzone;

    [ObservableProperty]
    private double _rightTriggerDeadzone;

    [ObservableProperty]
    private bool _adaptiveCentering;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFitted), nameof(IsCircle))]
    private ZoneShape _zoneShape;

    [ObservableProperty]
    private double _leftOutlineMargin;

    [ObservableProperty]
    private double _rightOutlineMargin;

    [ObservableProperty]
    private bool _learnZone;

    [ObservableProperty]
    private IReadOnlyList<StickPoint>? _leftOutline;

    [ObservableProperty]
    private IReadOnlyList<StickPoint>? _rightOutline;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLearned))]
    private IReadOnlyList<StickPoint>? _leftGrownOutline;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLearned))]
    private IReadOnlyList<StickPoint>? _rightGrownOutline;

    [ObservableProperty]
    private string _shapeCaption = "";

    [ObservableProperty]
    private string _learnedStatusText = "";

    public LiveViewModel(ShellViewModel shell)
    {
        _shell = shell;
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveTimer.Tick += (_, _) => FlushSave();

        Buttons =
        [
            new(GamepadButtons.A, "A"), new(GamepadButtons.B, "B"), new(GamepadButtons.X, "X"), new(GamepadButtons.Y, "Y"),
            new(GamepadButtons.LeftShoulder, "LB"), new(GamepadButtons.RightShoulder, "RB"),
            new(GamepadButtons.Back, "View"), new(GamepadButtons.Start, "Menu"), new(GamepadButtons.Guide, "Guide"),
            new(GamepadButtons.LeftThumb, "LS"), new(GamepadButtons.RightThumb, "RS"),
            new(GamepadButtons.DPadUp, "Up"), new(GamepadButtons.DPadDown, "Down"),
            new(GamepadButtons.DPadLeft, "Left"), new(GamepadButtons.DPadRight, "Right"),
        ];

        shell.Filter.PropertyChanged += (_, _) => RaiseStatus();
        shell.Settings.Changed += (_, _) =>
        {
            SyncFromProfile();
            RaiseStatus();
        };
        SyncFromProfile();
    }

    public ObservableCollection<ButtonLight> Buttons { get; }

    public IAsyncRelayCommand ToggleFilterCommand => _shell.ToggleFilterCommand;

    public FilterService Filter => _shell.Filter;

    public double PlotZoom => ZoomIn ? 0.35 : 1.0;

    public bool HasProfile => _shell.Settings.Profile is not null;

    public bool IsFilterOn => Filter.IsOn;

    public bool IsFitted => ZoneShape == ZoneShape.Fitted;

    public bool IsCircle => !IsFitted;

    public bool HasLearned => LeftGrownOutline is not null || RightGrownOutline is not null;

    public string StatusTitle => Filter.State switch
    {
        FilterState.On => "Filter is on",
        FilterState.Starting => "Turning on",
        FilterState.Stopping => "Turning off",
        _ => HasProfile ? "Preview mode" : "No filter yet",
    };

    public string StatusDetail =>
        Filter.IsOn
            ? Filter.VirtualSlot is int slot
                ? $"Games read BehavePad's clean controller as player {slot + 1}."
                : "Games read BehavePad's clean controller."
        : HasProfile
            ? "Games still read your controller as it is. The mint dots show what the filter would send instead."
            : "Run the drift test so BehavePad can build a filter for this controller.";

    public string PhysicalStatusText => Filter.IsOn && Filter.PhysicalHidden ? "Hidden from games" : "Visible to games";

    public bool PhysicalHidden => Filter.IsOn && Filter.PhysicalHidden;

    public string VirtualStatusText => Filter.IsOn
        ? Filter.VirtualSlot is int slot ? $"Player {slot + 1}" : "Connected"
        : "Off";

    public string ProfileStatusText => _shell.Settings.Profile switch
    {
        null => "Not built",
        { ZoneShape: ZoneShape.Fitted } profile => $"{profile.Level} · shaped zones",
        var profile => $"{profile.Level} protection",
    };

    public void OnNavigatedTo()
    {
        _shell.Controller.FrameUpdated += OnFrame;
        _shell.Controller.SetDemoScenario(DemoScenario.Playing);
        SyncFromProfile();
        RaiseStatus();
    }

    public void OnNavigatedFrom()
    {
        _shell.Controller.FrameUpdated -= OnFrame;
        _shell.Controller.SetDemoScenario(DemoScenario.Resting);
        FlushSave();
    }

    partial void OnLeftDeadzoneChanged(double value) =>
        UpdateProfile(p => p.WithStick(StickSide.Left, p.LeftStick with { Deadzone = value }));

    partial void OnRightDeadzoneChanged(double value) =>
        UpdateProfile(p => p.WithStick(StickSide.Right, p.RightStick with { Deadzone = value }));

    partial void OnLeftTriggerDeadzoneChanged(double value) =>
        UpdateProfile(p => p.WithTrigger(TriggerSide.Left, p.LeftTrigger with { Deadzone = value }));

    partial void OnRightTriggerDeadzoneChanged(double value) =>
        UpdateProfile(p => p.WithTrigger(TriggerSide.Right, p.RightTrigger with { Deadzone = value }));

    partial void OnAdaptiveCenteringChanged(bool value) =>
        UpdateProfile(p => p with { AdaptiveCentering = value });

    partial void OnZoneShapeChanged(ZoneShape value) =>
        UpdateProfile(p => p with { ZoneShape = value });

    partial void OnLeftOutlineMarginChanged(double value) =>
        UpdateProfile(p => p.WithStick(StickSide.Left, p.LeftStick with { OutlineMargin = value }));

    partial void OnRightOutlineMarginChanged(double value) =>
        UpdateProfile(p => p.WithStick(StickSide.Right, p.RightStick with { OutlineMargin = value }));

    partial void OnLearnZoneChanged(bool value) =>
        UpdateProfile(p => p with { LearnZone = value });

    [RelayCommand]
    private void RunTest() => _shell.CurrentPage = AppPage.Test;

    [RelayCommand]
    private void ResetToTested()
    {
        if (_shell.Settings.LastReport is not { } report)
        {
            return;
        }

        var level = _shell.Settings.Profile?.Level ?? ProtectionLevel.Balanced;
        var profile = FilterProfileBuilder.Build(report, level, AdaptiveCentering, ZoneShape) with { LearnZone = LearnZone };
        Replace(profile);
    }

    [RelayCommand]
    private void ForgetLearned()
    {
        if ((_pending ?? _shell.Settings.Profile) is { } profile)
        {
            Replace(profile.WithLearned([], []));
        }
    }

    /// <summary>Saves and applies a profile as it is, dropping anything the running filter learned.</summary>
    private void Replace(FilterProfile profile)
    {
        _pending = null;
        _saveTimer.Stop();
        _shell.Settings.SaveProfile(profile);
        _shell.Filter.ApplyProfile(profile);
        RefreshZones();
    }

    private void UpdateProfile(Func<FilterProfile, FilterProfile> change)
    {
        if (_syncing || (_pending ?? _shell.Settings.Profile) is not { } baseline)
        {
            return;
        }

        _pending = _shell.Filter.ApplyProfile(change(baseline).Sanitized(), keepLearned: true);
        RefreshZones();
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void FlushSave()
    {
        _saveTimer.Stop();
        if (_pending is { } profile)
        {
            _pending = null;
            _syncing = true;
            try
            {
                _shell.Settings.SaveProfile(_shell.Filter.WithLearned(profile));
            }
            finally
            {
                _syncing = false;
            }
        }
    }

    private void SyncFromProfile()
    {
        if (_pending is not null || _shell.Settings.Profile is not { } profile)
        {
            return;
        }

        _syncing = true;
        try
        {
            LeftDeadzone = profile.LeftStick.Deadzone;
            RightDeadzone = profile.RightStick.Deadzone;
            LeftTriggerDeadzone = profile.LeftTrigger.Deadzone;
            RightTriggerDeadzone = profile.RightTrigger.Deadzone;
            AdaptiveCentering = profile.AdaptiveCentering;
            ZoneShape = profile.ZoneShape;
            LeftOutlineMargin = profile.LeftStick.OutlineMargin;
            RightOutlineMargin = profile.RightStick.OutlineMargin;
            LearnZone = profile.LearnZone;
        }
        finally
        {
            _syncing = false;
        }

        RefreshZones();
    }

    /// <summary>Shows the zones the running filter uses, including anything it learned during play.</summary>
    private void RefreshZones()
    {
        var filter = _shell.Controller.Pump.Filter;
        _shownFilter = filter;
        _shownGrowths = filter?.Statistics.ZoneGrowths ?? 0;

        if (filter?.Profile is not { ZoneShape: ZoneShape.Fitted } profile)
        {
            LeftOutline = RightOutline = LeftGrownOutline = RightGrownOutline = null;
            ShapeCaption = "A circle around each stick's real center. Set its size below.";
            LearnedStatusText = "";
            return;
        }

        LeftOutline = profile.LeftStick.Outline;
        RightOutline = profile.RightStick.Outline;
        LeftGrownOutline = Grown(StickSide.Left, out var leftGain);
        RightGrownOutline = Grown(StickSide.Right, out var rightGain);

        var caption = $"An outline around the drift the test measured, plus the margin below. It ignores {Share(Describe.IgnoredShare(profile.LeftStick, ZoneShape.Fitted))} of the left stick and {Share(Describe.IgnoredShare(profile.RightStick, ZoneShape.Fitted))} of the right.";
        ShapeCaption = profile.SchemaVersion < 2
            ? caption + " This filter was built before shaped zones existed, so run the drift test again to shape it."
            : caption;

        var gains = new List<string>();
        if (LeftGrownOutline is not null)
        {
            gains.Add($"{Share(leftGain)} of the left stick");
        }

        if (RightGrownOutline is not null)
        {
            gains.Add($"{Share(rightGain)} of the right stick");
        }

        LearnedStatusText = gains.Count == 0 ? "Nothing learned yet." : $"Learned {string.Join(" and ", gains)} beyond the tested zone.";

        IReadOnlyList<StickPoint>? Grown(StickSide side, out double gain)
        {
            gain = 0;
            if (filter.LearnedPoints(side).Count == 0 || filter.Zone(side) is not { } zone)
            {
                return null;
            }

            var settings = profile.Stick(side);
            gain = Math.Max(zone.AreaShare - new StickZone(settings.Outline, settings.OutlineMargin).AreaShare, 0);
            return zone.Hull;
        }

        static string Share(double share) => share is > 0 and < 0.001 ? "under 0.1%" : Describe.Percent(share);
    }

    private void RaiseStatus()
    {
        OnPropertyChanged(nameof(HasProfile));
        OnPropertyChanged(nameof(IsFilterOn));
        OnPropertyChanged(nameof(StatusTitle));
        OnPropertyChanged(nameof(StatusDetail));
        OnPropertyChanged(nameof(PhysicalStatusText));
        OnPropertyChanged(nameof(PhysicalHidden));
        OnPropertyChanged(nameof(VirtualStatusText));
        OnPropertyChanged(nameof(ProfileStatusText));
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var frame = _shell.Controller.Frame;
        LeftRaw = frame.Raw.LeftStick;
        RightRaw = frame.Raw.RightStick;
        LeftOutput = frame.Output.LeftStick;
        RightOutput = frame.Output.RightStick;
        LeftCenter = frame.Filtering ? frame.LeftCenter : StickPoint.Zero;
        RightCenter = frame.Filtering ? frame.RightCenter : StickPoint.Zero;
        LeftTriggerRaw = frame.Raw.LeftTrigger / 255.0;
        RightTriggerRaw = frame.Raw.RightTrigger / 255.0;
        LeftTriggerOutput = frame.Output.LeftTrigger / 255.0;
        RightTriggerOutput = frame.Output.RightTrigger / 255.0;
        LeftBlockedText = Describe.Duration(frame.Statistics.LeftStickBlockedMs);
        RightBlockedText = Describe.Duration(frame.Statistics.RightStickBlockedMs);
        PhantomBlockedText = frame.Statistics.PhantomPressesBlocked.ToString();
        PollRateText = frame.Connected && frame.PollRateHz > 0 ? $"{frame.PollRateHz:0} polls per second" : "";

        var filter = _shell.Controller.Pump.Filter;
        if (!ReferenceEquals(filter, _shownFilter) || (filter?.Statistics.ZoneGrowths ?? 0) != _shownGrowths)
        {
            RefreshZones();
        }

        foreach (var light in Buttons)
        {
            var physical = (frame.Raw.Buttons & light.Button) != 0;
            var output = (frame.Output.Buttons & light.Button) != 0;
            light.IsDown = output;
            light.IsBlocked = physical && !output;
        }
    }
}
