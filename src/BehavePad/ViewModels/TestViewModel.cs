using BehavePad.Core.Analysis;
using BehavePad.Ipc;
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
    public const int TargetReleases = 4;

    /// <summary>Margin drawn around the growing outline while the test runs, before a preset is picked.</summary>
    private static readonly double PreviewMargin = FilterProfileBuilder.OutlineMargin(ProtectionLevel.Balanced);

    private readonly ShellViewModel _shell;

    /// <summary>Set while several choices change together, so the proposal is rebuilt once at the end.</summary>
    private bool _choosing;

    /// <summary>Set while the agent's snapshot is being copied in, so it cannot look like a user choice.</summary>
    private bool _applying;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
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
    private double _liveOutlineMargin;

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

    /// <summary>Preset for the triggers and buttons. Picking it on the protection card also sets both sticks.</summary>
    [ObservableProperty]
    private ProtectionLevel _level = ProtectionLevel.Balanced;

    [ObservableProperty]
    private ProtectionLevel _leftLevel = ProtectionLevel.Balanced;

    [ObservableProperty]
    private ProtectionLevel _rightLevel = ProtectionLevel.Balanced;

    [ObservableProperty]
    private ZoneShape _leftShape;

    [ObservableProperty]
    private ZoneShape _rightShape;

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

    /// <summary>True while a measurement is in progress, which nothing should interrupt by accident.</summary>
    public bool IsRunning => Step is TestStep.Countdown or TestStep.Resting or TestStep.SnapBack;

    public string LengthText => $"Takes about {RestSeconds:0} seconds, plus a few stick flicks.";

    public int ReleaseTarget => TargetReleases;

    public bool CanBegin => _shell.Controller.IsConnected;

    public string VerdictTitle => Report is null ? "" : Describe.Verdict(Report, Proposed).Title;

    public string VerdictBody => Report is null ? "" : Describe.Verdict(Report, Proposed).Body;

    public Severity OverallSeverity => Report?.Overall ?? Severity.Healthy;

    public string LeftTitle => Report is null ? "" : Describe.StickTitle(Report.LeftStick);

    public string LeftDetail => Report is null ? "" : Describe.StickDetail(Report.LeftStick, LeftShape);

    public Severity LeftSeverity => Report?.LeftStick.Severity ?? Severity.Healthy;

    public string RightTitle => Report is null ? "" : Describe.StickTitle(Report.RightStick);

    public string RightDetail => Report is null ? "" : Describe.StickDetail(Report.RightStick, RightShape);

    public Severity RightSeverity => Report?.RightStick.Severity ?? Severity.Healthy;

    public double LeftDeadzone => Proposed?.LeftStick.Deadzone ?? 0;

    public double RightDeadzone => Proposed?.RightStick.Deadzone ?? 0;

    public StickPoint LeftCenter => Proposed?.LeftStick.Center ?? StickPoint.Zero;

    public StickPoint RightCenter => Proposed?.RightStick.Center ?? StickPoint.Zero;

    public IReadOnlyList<StickPoint>? LeftOutline => LeftShape == ZoneShape.Fitted ? Proposed?.LeftStick.Outline : null;

    public IReadOnlyList<StickPoint>? RightOutline => RightShape == ZoneShape.Fitted ? Proposed?.RightStick.Outline : null;

    public double LeftOutlineMargin => Proposed?.LeftStick.OutlineMargin ?? 0;

    public double RightOutlineMargin => Proposed?.RightStick.OutlineMargin ?? 0;

    public string LeftFilterText => Proposed is null ? "" : Describe.ZoneSentence(Proposed.LeftStick);

    public string RightFilterText => Proposed is null ? "" : Describe.ZoneSentence(Proposed.RightStick);

    public double LeftResultZoom => Report is null ? 1 : Describe.PlotZoom(Report.LeftStick, Proposed?.LeftStick);

    public double RightResultZoom => Report is null ? 1 : Describe.PlotZoom(Report.RightStick, Proposed?.RightStick);

    public string? ShapeHint =>
        Report is null || Proposed is null || Describe.ShapeHint(Report, Proposed) is not { } hint
            ? null
            : $"{hint} Open Live filter to switch that stick to a shaped zone.";

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
            Send(AgentCommand.SetDemoScenario, new DemoScenarioRequest(DemoScenario.Resting));
        }
    }

    public void OnNavigatedFrom()
    {
        _shell.Controller.FrameUpdated -= OnFrame;
        if (IsRunning)
        {
            Notice = "The test stopped when you left the page, so nothing was measured. Start it again when you are ready.";
            Send(AgentCommand.CancelTest);
        }
    }

    /// <summary>Copies the agent's measurement into the window. Everything below the results is ours.</summary>
    public void ApplySnapshot(TestSnapshot snapshot)
    {
        _applying = true;
        try
        {
            Countdown = snapshot.Countdown;
            RestProgress = snapshot.RestProgress;
            RestRemaining = snapshot.RestRemaining.ToString();
            PhantomPresses = snapshot.PhantomPresses;
            LeftReleases = snapshot.LeftReleases;
            RightReleases = snapshot.RightReleases;
            LeftHint = snapshot.LeftHint;
            RightHint = snapshot.RightHint;
            Notice = snapshot.Notice;
            LeftCloudPoints = snapshot.LeftCloud;
            RightCloudPoints = snapshot.RightCloud;
            LeftLiveOutline = snapshot.LeftOutline.Count == 0 ? null : snapshot.LeftOutline;
            RightLiveOutline = snapshot.RightOutline.Count == 0 ? null : snapshot.RightOutline;
            LeftSettles = snapshot.LeftSettles;
            RightSettles = snapshot.RightSettles;
            LeftLiveZoom = snapshot.LeftZoom;
            RightLiveZoom = snapshot.RightZoom;
            LiveOutlineMargin = snapshot.OutlineMargin > 0 ? snapshot.OutlineMargin : PreviewMargin;

            // Arriving at the results is when the saved filter's choices seed the protection cards.
            if (snapshot.Step == TestStep.Results && Step != TestStep.Results && snapshot.Report is not null)
            {
                SeedChoices();
            }

            Step = snapshot.Step;
            Report = snapshot.Report;
        }
        finally
        {
            _applying = false;
        }

        RebuildProposal();
    }

    private void SeedChoices()
    {
        // Start from the choices in the saved filter, so a new test keeps each stick's shape and preset.
        var saved = _shell.Settings.Profile;
        _choosing = true;
        Level = saved?.Level ?? ProtectionLevel.Balanced;
        LeftLevel = saved?.LeftStick.Level ?? ProtectionLevel.Balanced;
        RightLevel = saved?.RightStick.Level ?? ProtectionLevel.Balanced;
        LeftShape = saved?.LeftStick.Shape ?? ZoneShape.Circle;
        RightShape = saved?.RightStick.Shape ?? ZoneShape.Circle;
        _choosing = false;
    }

    private void Send(AgentCommand command, object? payload = null) => _shell.Link?.Send(command, payload);

    partial void OnLevelChanged(ProtectionLevel value)
    {
        if (!_choosing)
        {
            _choosing = true;
            LeftLevel = value;
            RightLevel = value;
            _choosing = false;
        }

        RebuildProposal();
    }

    partial void OnLeftLevelChanged(ProtectionLevel value) => RebuildProposal();

    partial void OnRightLevelChanged(ProtectionLevel value) => RebuildProposal();

    partial void OnLeftShapeChanged(ZoneShape value) => RebuildProposal();

    partial void OnRightShapeChanged(ZoneShape value) => RebuildProposal();

    partial void OnReportChanged(DriftReport? value) => RebuildProposal();

    [RelayCommand]
    private void Begin()
    {
        if (!_shell.Controller.IsConnected)
        {
            Notice = "Connect a controller first, or turn on the demo controller in Setup.";
            return;
        }

        Send(AgentCommand.BeginTest, new BeginTestRequest(Length));
    }

    [RelayCommand]
    private void Cancel() => Send(AgentCommand.CancelTest);

    [RelayCommand]
    private void SkipSnapBack() => Send(AgentCommand.SkipSnapBack);

    [RelayCommand]
    private void RunAgain()
    {
        Report = null;
        Step = TestStep.Intro;
        Send(AgentCommand.CancelTest);
    }

    /// <summary>Offered on the results, where a borderline reading is the reason to sit still for longer.</summary>
    [RelayCommand]
    private void TestForLonger()
    {
        Length = RestLength.Thorough;
        RunAgain();
    }

    [RelayCommand]
    private void SaveAndEnable()
    {
        if (Save(turnOn: true))
        {
            _shell.CurrentPage = AppPage.Live;
        }
    }

    [RelayCommand]
    private void SaveOnly()
    {
        if (Save(turnOn: false))
        {
            _shell.CurrentPage = AppPage.Overview;
        }
    }

    /// <summary>Hands the result to the agent, which saves it, applies it and optionally turns the filter on.</summary>
    private bool Save(bool turnOn)
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

        Send(AgentCommand.SaveTest, new SaveTestRequest(Report, profile, turnOn));
        _shell.Settings.SaveTest(Report, profile);
        _shell.Filter.ApplyProfile(profile);
        Step = TestStep.Intro;
        Report = null;
        return true;
    }

    private void RebuildProposal()
    {
        if (_choosing || _applying)
        {
            return;
        }

        Proposed = Report is null
            ? null
            : FilterProfileBuilder.Build(Report, new StickChoice(LeftLevel, LeftShape), new StickChoice(RightLevel, RightShape), Level);
        OnPropertyChanged(string.Empty);
    }

    /// <summary>Only the live readouts. Every measurement now happens in the agent.</summary>
    private void OnFrame(object? sender, EventArgs e)
    {
        var frame = _shell.Controller.Frame;
        LeftRaw = frame.Raw.LeftStick;
        RightRaw = frame.Raw.RightStick;
        LeftTrigger = frame.Raw.LeftTrigger / 255.0;
        RightTrigger = frame.Raw.RightTrigger / 255.0;
        OnPropertyChanged(nameof(CanBegin));
    }

}
