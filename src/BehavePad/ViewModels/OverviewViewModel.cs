using BehavePad.Core.Analysis;
using BehavePad.Core.Filtering;
using BehavePad.Core.Input;
using BehavePad.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BehavePad.ViewModels;

public sealed partial class OverviewViewModel : ObservableObject, IPageViewModel
{
    private readonly ShellViewModel _shell;

    [ObservableProperty]
    private StickPoint _leftStick;

    [ObservableProperty]
    private StickPoint _rightStick;

    [ObservableProperty]
    private bool _anyPressed;

    [ObservableProperty]
    private string _blockedText = "0s";

    [ObservableProperty]
    private string _phantomText = "0";

    private bool _wasConnected;

    public OverviewViewModel(ShellViewModel shell)
    {
        _shell = shell;
        shell.Settings.Changed += (_, _) => OnPropertyChanged(string.Empty);
        shell.Filter.PropertyChanged += (_, _) => OnPropertyChanged(string.Empty);
    }

    public DriftReport? Report => _shell.Settings.LastReport;

    public bool HasReport => Report is not null;

    public DriverSetupService DriverSetup => _shell.Filter.DriverSetup;

    private FilterProfile? Profile => _shell.Settings.Profile;

    public bool IsFilterOn => _shell.Filter.IsOn;

    /// <summary>True when BehavePad will turn the filter on by itself once the controller is there.</summary>
    public bool IsArmed => _shell.Filter.IsArmed;

    public string OffPillText => IsArmed && !_shell.Controller.IsConnected ? "Waiting for your controller" : "Filter off";

    public bool MascotHappy => IsFilterOn || Report is null || Report.Overall == Severity.Healthy;

    public string Headline =>
        IsFilterOn ? "Your controller is behaving"
        : Report is null ? "Let's see how your controller behaves"
        : Describe.Verdict(Report, Profile).Title;

    public string Subtext =>
        IsFilterOn
            ? _shell.Filter.PhysicalHidden
                ? "Games now read BehavePad's clean virtual controller, and the original is hidden from them."
                : "Games can read BehavePad's clean virtual controller. Hide the original in Setup for best results."
        : Report is null
            ? "A hands-off check finds stick drift, trigger creep and phantom button presses. Then BehavePad builds a filter for this exact controller."
        : IsArmed
            ? "Your filter is ready. BehavePad turns it on by itself whenever this controller is connected."
        : Report.Overall == Severity.Healthy
            ? "Nothing needs fixing. Turn the filter on from the sidebar if you want a safety net while you play."
            : "Turn the filter on from the sidebar to keep this unintended input out of your games.";

    public string PrimaryText => HasReport ? "Open live filter" : "Start drift test";

    /// <summary>The same glyph the sidebar uses for wherever this button leads.</summary>
    public string PrimaryGlyph => HasReport ? "" : "";

    public string? LastTestedText => Report is null
        ? null
        : Profile is { } profile
            ? $"Tested {Report.CapturedAt.LocalDateTime:MMM d 'at' h:mm tt} · {Describe.ProfileSummary(profile)}"
            : $"Tested {Report.CapturedAt.LocalDateTime:MMM d 'at' h:mm tt}";

    public Severity? LeftSeverity => Report?.LeftStick.Severity;

    public string LeftTitle => Report is null ? "Not tested yet" : Describe.StickTitle(Report.LeftStick);

    public string LeftDetail => Report is null ? "Run the drift test to check it." : Describe.StickDetail(Report.LeftStick, Profile?.LeftStick.Shape ?? ZoneShape.Circle);

    public Severity? RightSeverity => Report?.RightStick.Severity;

    public string RightTitle => Report is null ? "Not tested yet" : Describe.StickTitle(Report.RightStick);

    public string RightDetail => Report is null ? "Run the drift test to check it." : Describe.StickDetail(Report.RightStick, Profile?.RightStick.Shape ?? ZoneShape.Circle);

    public Severity? TriggerSeverity => Report is null ? null : Describe.Triggers(Report).Severity;

    public string TriggerTitle => Report is null ? "Not tested yet" : Describe.Triggers(Report).Title;

    public string TriggerDetail => Report is null ? "Run the drift test to check them." : Describe.Triggers(Report).Detail;

    public Severity? ButtonSeverity => Report is null ? null : Describe.Buttons(Report).Severity;

    public string ButtonTitle => Report is null ? "Not tested yet" : Describe.Buttons(Report).Title;

    public string ButtonDetail => Report is null ? "Run the drift test to check them." : Describe.Buttons(Report).Detail;

    /// <summary>Held back until a test has been run, so the first thing BehavePad asks for is not a permission prompt.</summary>
    public bool ShowDriverCallout => HasReport && !_shell.Filter.DriversReady;

    public void OnNavigatedTo()
    {
        _shell.Controller.FrameUpdated += OnFrame;
        _shell.Controller.SetDemoScenario(DemoScenario.Resting);
        OnPropertyChanged(string.Empty);
    }

    public void OnNavigatedFrom() => _shell.Controller.FrameUpdated -= OnFrame;

    [RelayCommand]
    private void Primary()
    {
        if (HasReport)
        {
            OpenLive();
        }
        else
        {
            RunTest();
        }
    }

    [RelayCommand]
    private void RunTest() => _shell.CurrentPage = AppPage.Test;

    [RelayCommand]
    private void OpenLive() => _shell.CurrentPage = AppPage.Live;

    [RelayCommand]
    private void OpenSetup() => _shell.CurrentPage = AppPage.Setup;

    [RelayCommand]
    private async Task InstallDriversAsync() => await _shell.Filter.InstallDriversAsync();

    private void OnFrame(object? sender, EventArgs e)
    {
        var frame = _shell.Controller.Frame;
        if (frame.Connected != _wasConnected)
        {
            _wasConnected = frame.Connected;
            OnPropertyChanged(nameof(OffPillText));
            OnPropertyChanged(nameof(Subtext));
        }

        // While filtering, the mascot shows what games receive, so a protected controller looks calm.
        var state = IsFilterOn ? frame.Output : frame.Raw;
        LeftStick = state.LeftStick;
        RightStick = state.RightStick;
        AnyPressed = state.Buttons != GamepadButtons.None;
        BlockedText = Describe.Duration(frame.Statistics.StickBlockedMs);
        PhantomText = frame.Statistics.PhantomPressesBlocked.ToString();
    }
}
