using BehavePad.Core.Analysis;
using BehavePad.Core.Filtering;
using BehavePad.Core.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BehavePad.ViewModels;

public enum TestStep
{
    Intro,
    Countdown,
    Resting,
    SnapBack,
    Results,
}

public enum RestLength
{
    Quick,
    Standard,
    Thorough,
}

/// <summary>Guides the user through the hands-off rest check, the snap-back check, and the results.</summary>
public sealed partial class TestViewModel : ObservableObject, IPageViewModel
{
    public const int TargetReleases = 6;
    private const double CountdownMs = 3000;
    private const int MaxCloudPoints = 700;

    /// <summary>Margin drawn around the growing outline while the test runs, before a protection level is picked.</summary>
    private static readonly double PreviewMargin = FilterProfileBuilder.OutlineMargin(ProtectionLevel.Balanced);

    private readonly ShellViewModel _shell;
    private readonly List<StickPoint> _leftCloud = [];
    private readonly List<StickPoint> _rightCloud = [];
    private readonly HashSet<(int, int)> _leftSeen = [];
    private readonly HashSet<(int, int)> _rightSeen = [];
    private RestRecorder? _rest;
    private SnapBackRecorder? _snap;
    private RestCapture? _restCapture;
    private StickZone? _leftLiveZone;
    private StickZone? _rightLiveZone;
    private double _phaseStart;
    private int _frameCount;

    [ObservableProperty]
    private TestStep _step;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RestSeconds))]
    private RestLength _length = RestLength.Standard;

    [ObservableProperty]
    private int _countdown = 3;

    [ObservableProperty]
    private string? _notice;

    [ObservableProperty]
    private double _restProgress;

    [ObservableProperty]
    private string _restRemaining = "";

    [ObservableProperty]
    private int _phantomPresses;

    [ObservableProperty]
    private StickPoint _leftRaw;

    [ObservableProperty]
    private StickPoint _rightRaw;

    [ObservableProperty]
    private double _leftTrigger;

    [ObservableProperty]
    private double _rightTrigger;

    [ObservableProperty]
    private IReadOnlyList<StickPoint> _leftCloudPoints = [];

    [ObservableProperty]
    private IReadOnlyList<StickPoint> _rightCloudPoints = [];

    [ObservableProperty]
    private IReadOnlyList<StickPoint>? _leftLiveOutline;

    [ObservableProperty]
    private IReadOnlyList<StickPoint>? _rightLiveOutline;

    [ObservableProperty]
    private double _leftLiveZoom = 0.3;

    [ObservableProperty]
    private double _rightLiveZoom = 0.3;

    [ObservableProperty]
    private int _leftReleases;

    [ObservableProperty]
    private int _rightReleases;

    [ObservableProperty]
    private string _leftHint = "Push to the edge";

    [ObservableProperty]
    private string _rightHint = "Push to the edge";

    [ObservableProperty]
    private IReadOnlyList<StickPoint> _leftSettles = [];

    [ObservableProperty]
    private IReadOnlyList<StickPoint> _rightSettles = [];

    [ObservableProperty]
    private DriftReport? _report;

    [ObservableProperty]
    private ProtectionLevel _level = ProtectionLevel.Balanced;

    [ObservableProperty]
    private ZoneShape _zoneShape;

    [ObservableProperty]
    private FilterProfile? _proposed;

    public TestViewModel(ShellViewModel shell)
    {
        _shell = shell;
    }

    public double RestSeconds => Length switch
    {
        RestLength.Quick => 6,
        RestLength.Thorough => 20,
        _ => 10,
    };

    public int ReleaseTarget => TargetReleases;

    public bool CanBegin => _shell.Controller.IsConnected;

    public double LiveOutlineMargin => PreviewMargin;

    public bool IsFitted => ZoneShape == ZoneShape.Fitted;

    public string VerdictTitle => Report is null ? "" : Describe.Verdict(Report, ZoneShape).Title;

    public string VerdictBody => Report is null ? "" : Describe.Verdict(Report, ZoneShape).Body;

    public Severity OverallSeverity => Report?.Overall ?? Severity.Healthy;

    public string LeftTitle => Report is null ? "" : Describe.StickTitle(Report.LeftStick);

    public string LeftDetail => Report is null ? "" : Describe.StickDetail(Report.LeftStick, ZoneShape);

    public Severity LeftSeverity => Report?.LeftStick.Severity ?? Severity.Healthy;

    public string RightTitle => Report is null ? "" : Describe.StickTitle(Report.RightStick);

    public string RightDetail => Report is null ? "" : Describe.StickDetail(Report.RightStick, ZoneShape);

    public Severity RightSeverity => Report?.RightStick.Severity ?? Severity.Healthy;

    public double LeftDeadzone => Proposed?.LeftStick.Deadzone ?? 0;

    public double RightDeadzone => Proposed?.RightStick.Deadzone ?? 0;

    public StickPoint LeftCenter => Proposed?.LeftStick.Center ?? StickPoint.Zero;

    public StickPoint RightCenter => Proposed?.RightStick.Center ?? StickPoint.Zero;

    public IReadOnlyList<StickPoint>? LeftOutline => IsFitted ? Proposed?.LeftStick.Outline : null;

    public IReadOnlyList<StickPoint>? RightOutline => IsFitted ? Proposed?.RightStick.Outline : null;

    public double LeftOutlineMargin => Proposed?.LeftStick.OutlineMargin ?? 0;

    public double RightOutlineMargin => Proposed?.RightStick.OutlineMargin ?? 0;

    public string LeftFilterText => Proposed is null ? "" : Describe.ZoneSentence(Proposed.LeftStick, ZoneShape);

    public string RightFilterText => Proposed is null ? "" : Describe.ZoneSentence(Proposed.RightStick, ZoneShape);

    public double LeftResultZoom => Report is null ? 1 : Describe.PlotZoom(Report.LeftStick, Proposed?.LeftStick, ZoneShape);

    public double RightResultZoom => Report is null ? 1 : Describe.PlotZoom(Report.RightStick, Proposed?.RightStick, ZoneShape);

    public string CircleShapeCost => Proposed is null ? "" : Describe.ShapeCost(Proposed, ZoneShape.Circle);

    public string FittedShapeCost => Proposed is null ? "" : Describe.ShapeCost(Proposed, ZoneShape.Fitted);

    public string ShapeDescription => Report is null ? "" : Describe.ShapeDescription(Report, ZoneShape);

    public Severity TriggerSeverity => Report is null ? Severity.Healthy : Describe.Triggers(Report).Severity;

    public string TriggerTitle => Report is null ? "" : Describe.Triggers(Report).Title;

    public string TriggerDetail => Report is null ? "" : Describe.Triggers(Report).Detail;

    public string TriggerFilterText => Proposed is null
        ? ""
        : Proposed.LeftTrigger.Deadzone <= 0 && Proposed.RightTrigger.Deadzone <= 0
            ? "Triggers keep their full range."
            : $"Ignores the first {Describe.Percent(Math.Max(Proposed.LeftTrigger.Deadzone, Proposed.RightTrigger.Deadzone))} of trigger pull.";

    public Severity ButtonSeverity => Report is null ? Severity.Healthy : Describe.Buttons(Report).Severity;

    public string ButtonTitle => Report is null ? "" : Describe.Buttons(Report).Title;

    public string ButtonDetail => Report is null ? "" : Describe.Buttons(Report).Detail;

    public string ButtonFilterText
    {
        get
        {
            if (Proposed is null)
            {
                return "";
            }

            var buttons = Proposed.Buttons;
            var parts = new List<string>();
            if (buttons.Blocked != GamepadButtons.None)
            {
                parts.Add($"Blocks {string.Join(", ", buttons.Blocked.Each().Select(b => b.DisplayName()))}.");
            }

            parts.AddRange(buttons.Debounce.Select(d => $"{d.Button.DisplayName()} must be held {d.Milliseconds} ms to count."));
            return parts.Count == 0 ? "Buttons pass straight through." : string.Join(" ", parts);
        }
    }

    public string LevelDescription => Describe.LevelDescription(Level);

    public string? SnapSkippedNote => Report is { SnapBackIncluded: false }
        ? "You skipped the snap-back check, so BehavePad added extra margin around the sticks."
        : null;

    public void OnNavigatedTo()
    {
        _shell.Controller.FrameUpdated += OnFrame;
        if (Step == TestStep.Intro)
        {
            _shell.Controller.SetDemoScenario(DemoScenario.Resting);
        }
    }

    public void OnNavigatedFrom()
    {
        _shell.Controller.FrameUpdated -= OnFrame;
        if (Step is TestStep.Countdown or TestStep.Resting or TestStep.SnapBack)
        {
            Abort(null);
        }
    }

    partial void OnLevelChanged(ProtectionLevel value) => RebuildProposal();

    partial void OnZoneShapeChanged(ZoneShape value) => RebuildProposal();

    partial void OnReportChanged(DriftReport? value) => RebuildProposal();

    [RelayCommand]
    private void Begin()
    {
        if (!_shell.Controller.IsConnected)
        {
            Notice = "Connect a controller first, or turn on the demo controller in Setup.";
            return;
        }

        StartCountdown(_shell.Controller.Frame.TimestampMs, null);
    }

    [RelayCommand]
    private void Cancel() => Abort(null);

    [RelayCommand]
    private void SkipSnapBack() => Finish(includeSnapBack: _snap is { } snap && (snap.LeftCount > 0 || snap.RightCount > 0));

    [RelayCommand]
    private void RunAgain()
    {
        Report = null;
        Step = TestStep.Intro;
    }

    [RelayCommand]
    private async Task SaveAndEnableAsync()
    {
        if (!Save())
        {
            return;
        }

        _shell.CurrentPage = AppPage.Live;
        if (!_shell.Filter.IsOn)
        {
            await _shell.Filter.StartAsync();
        }
    }

    [RelayCommand]
    private void SaveOnly()
    {
        if (Save())
        {
            _shell.CurrentPage = AppPage.Overview;
        }
    }

    private bool Save()
    {
        if (Report is null || Proposed is null)
        {
            return false;
        }

        var saved = _shell.Settings.Profile;
        var profile = Proposed with
        {
            AdaptiveCentering = saved?.AdaptiveCentering ?? false,
            LearnZone = saved?.LearnZone ?? false,
        };
        _shell.Settings.SaveTest(Report, profile);
        _shell.Filter.ApplyProfile(profile);
        Step = TestStep.Intro;
        Report = null;
        return true;
    }

    private void RebuildProposal()
    {
        Proposed = Report is null ? null : FilterProfileBuilder.Build(Report, Level, shape: ZoneShape);
        OnPropertyChanged(string.Empty);
    }

    private void StartCountdown(double now, string? notice)
    {
        _shell.Controller.Pump.SetObserver(null);
        _shell.Controller.SetDemoScenario(DemoScenario.Resting);
        _rest = new RestRecorder();
        _snap = null;
        _restCapture = null;
        ClearClouds();
        Notice = notice;
        Countdown = 3;
        RestProgress = 0;
        PhantomPresses = 0;
        _phaseStart = now;
        Step = TestStep.Countdown;
    }

    private void BeginResting()
    {
        var rest = _rest ??= new RestRecorder();
        rest.Reset();
        ClearClouds();
        _shell.Controller.Pump.SetObserver((state, time) => rest.Add(state, time));
        Notice = null;
        Step = TestStep.Resting;
    }

    private void FinishResting()
    {
        _shell.Controller.Pump.SetObserver(null);
        var capture = _restCapture = _rest!.ToCapture();
        _shell.Controller.Pulse();

        // The preview only saw about 60 readings a second. Redraw the outline from every reading before snap-back adds to it.
        _leftLiveZone = Grow(null, capture.States.Select(s => s.LeftStick));
        _rightLiveZone = Grow(null, capture.States.Select(s => s.RightStick));
        LeftLiveOutline = _leftLiveZone?.Hull;
        RightLiveOutline = _rightLiveZone?.Hull;

        var snap = _snap = new SnapBackRecorder(TargetReleases);
        LeftReleases = RightReleases = 0;
        LeftSettles = RightSettles = [];
        LeftHint = RightHint = "Push to the edge";
        _shell.Controller.SetDemoScenario(DemoScenario.SnapBack);
        _shell.Controller.Pump.SetObserver((state, time) => snap.Add(state, time));
        Step = TestStep.SnapBack;
    }

    private void Finish(bool includeSnapBack)
    {
        _shell.Controller.Pump.SetObserver(null);
        _shell.Controller.SetDemoScenario(DemoScenario.Resting);
        if (_restCapture is null)
        {
            Abort(null);
            return;
        }

        Level = ProtectionLevel.Balanced;
        ZoneShape = _shell.Settings.Profile?.ZoneShape ?? ZoneShape.Circle;
        Report = DriftAnalyzer.Analyze(_restCapture, includeSnapBack ? _snap?.ToCapture() : null, _shell.Controller.ControllerName);
        Step = TestStep.Results;
        _shell.Controller.Pulse(0.3, 120);
    }

    private void Abort(string? notice)
    {
        _shell.Controller.Pump.SetObserver(null);
        _shell.Controller.SetDemoScenario(DemoScenario.Resting);
        _rest = null;
        _snap = null;
        Notice = notice;
        Step = TestStep.Intro;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var frame = _shell.Controller.Frame;
        LeftRaw = frame.Raw.LeftStick;
        RightRaw = frame.Raw.RightStick;
        LeftTrigger = frame.Raw.LeftTrigger / 255.0;
        RightTrigger = frame.Raw.RightTrigger / 255.0;
        OnPropertyChanged(nameof(CanBegin));
        _frameCount++;

        var now = frame.TimestampMs;
        switch (Step)
        {
            case TestStep.Countdown:
            {
                if (!frame.Connected)
                {
                    Abort("The controller disconnected. Reconnect it and try again.");
                    return;
                }

                var remaining = CountdownMs - (now - _phaseStart);
                Countdown = Math.Max(1, (int)Math.Ceiling(remaining / 1000));
                if (remaining <= 0)
                {
                    BeginResting();
                }

                break;
            }

            case TestStep.Resting:
            {
                if (!frame.Connected)
                {
                    Abort("The controller disconnected. Reconnect it and try again.");
                    return;
                }

                var rest = _rest!;
                if (rest.IsDisturbed)
                {
                    StartCountdown(now, "The controller moved, so the check started over. Leave it flat and don't touch it.");
                    return;
                }

                AddCloudPoint(frame.Raw);
                var elapsed = rest.ElapsedMs;
                RestProgress = Math.Clamp(elapsed / (RestSeconds * 1000), 0, 1);
                RestRemaining = $"{Math.Max(0, Math.Ceiling(RestSeconds - elapsed / 1000)):0}";
                PhantomPresses = rest.PressEvents;
                if (elapsed >= RestSeconds * 1000)
                {
                    FinishResting();
                }

                break;
            }

            case TestStep.SnapBack:
            {
                var snap = _snap!;
                if (snap.LeftCount != LeftReleases)
                {
                    LeftReleases = snap.LeftCount;
                    LeftSettles = snap.Settles(StickSide.Left);
                    _leftLiveZone = Grow(_leftLiveZone, LeftSettles);
                    LeftLiveOutline = _leftLiveZone?.Hull;
                }

                if (snap.RightCount != RightReleases)
                {
                    RightReleases = snap.RightCount;
                    RightSettles = snap.Settles(StickSide.Right);
                    _rightLiveZone = Grow(_rightLiveZone, RightSettles);
                    RightLiveOutline = _rightLiveZone?.Hull;
                }

                LeftHint = Hint(snap.LeftPhase, snap.LeftCount);
                RightHint = Hint(snap.RightPhase, snap.RightCount);
                if (snap.IsComplete)
                {
                    Finish(includeSnapBack: true);
                }

                break;
            }
        }
    }

    private static string Hint(SnapPhase phase, int count) =>
        count >= TargetReleases ? "Done"
        : phase switch
        {
            SnapPhase.Pushed => "Now let go",
            SnapPhase.Settling => "Settling",
            _ => count == 0 ? "Push to the edge" : "Again, another direction",
        };

    /// <summary>Stretches a preview outline to cover more points, starting one when there is none yet.</summary>
    private static StickZone? Grow(StickZone? zone, IEnumerable<StickPoint> points)
    {
        foreach (var point in points)
        {
            zone = zone?.With(point) ?? new StickZone([point], PreviewMargin);
        }

        return zone;
    }

    private void ClearClouds()
    {
        _leftCloud.Clear();
        _rightCloud.Clear();
        _leftSeen.Clear();
        _rightSeen.Clear();
        _leftLiveZone = _rightLiveZone = null;
        LeftCloudPoints = [];
        RightCloudPoints = [];
        LeftLiveOutline = RightLiveOutline = null;
        LeftLiveZoom = RightLiveZoom = 0.3;
    }

    private void AddCloudPoint(GamepadState state)
    {
        Add(state.LeftStick, _leftCloud, _leftSeen);
        Add(state.RightStick, _rightCloud, _rightSeen);
        _leftLiveZone = Grow(_leftLiveZone, [state.LeftStick]);
        _rightLiveZone = Grow(_rightLiveZone, [state.RightStick]);

        if (_frameCount % 6 == 0)
        {
            LeftCloudPoints = _leftCloud.ToArray();
            RightCloudPoints = _rightCloud.ToArray();
            LeftLiveOutline = _leftLiveZone?.Hull;
            RightLiveOutline = _rightLiveZone?.Hull;
            LeftLiveZoom = ZoomFor(_leftCloud);
            RightLiveZoom = ZoomFor(_rightCloud);
        }

        static void Add(StickPoint point, List<StickPoint> cloud, HashSet<(int, int)> seen)
        {
            if (cloud.Count < MaxCloudPoints && seen.Add(((int)Math.Round(point.X / 0.002), (int)Math.Round(point.Y / 0.002))))
            {
                cloud.Add(point);
            }
        }

        static double ZoomFor(List<StickPoint> cloud) =>
            cloud.Count == 0 ? 0.3 : Math.Clamp((cloud.Max(p => p.Magnitude) + PreviewMargin) * 2.2, 0.15, 1.0);
    }
}
