using System.Text.Json.Serialization;
using BehavePad.Core.Input;

namespace BehavePad.Core.Analysis;

public enum Severity
{
    Healthy,
    Minor,
    Moderate,
    Severe,
}

/// <summary>What the test learned about one thumbstick. Distances use normalized units where 1 is full deflection.</summary>
/// <param name="RestCenter">Average position while the controller lay untouched.</param>
/// <param name="RestOffset">Distance from the true center to <paramref name="RestCenter"/>.</param>
/// <param name="Jitter">How far the stick wobbles around its rest center (99th percentile).</param>
/// <param name="WorstRestDistance">Farthest an untouched stick sat from true center. This is what a game without a filter sees.</param>
/// <param name="EstimatedCenter">Best estimate of where the stick rests, using both test steps.</param>
/// <param name="ResidualRadius">How far from <paramref name="EstimatedCenter"/> an untouched stick may still sit.</param>
/// <param name="SettleSpread">Spread of snap-back rest positions, or null when that step was skipped.</param>
public sealed record StickDiagnosis(
    StickSide Side,
    StickPoint RestCenter,
    double RestOffset,
    double Jitter,
    double WorstRestDistance,
    StickPoint EstimatedCenter,
    double ResidualRadius,
    double? SettleSpread,
    IReadOnlyList<StickPoint> RestPoints,
    IReadOnlyList<StickPoint> SettlePoints,
    Severity Severity)
{
    /// <summary>Corners of the outline around every untouched position, taken before plots thin the points out. Empty in reports saved before fitted zones.</summary>
    public IReadOnlyList<StickPoint> RestOutline { get; init; } = [];
}

/// <summary>What the test learned about one analog trigger. Values span 0 (released) to 1 (fully pulled).</summary>
public sealed record TriggerDiagnosis(TriggerSide Side, double RestMean, double RestMax, Severity Severity);

/// <summary>A button that registered presses while nobody touched the controller.</summary>
public sealed record ButtonGlitch(GamepadButtons Button, int PressCount, double LongestPressMs, double PressedFraction, bool IsStuck);

public sealed record DriftReport(
    DateTimeOffset CapturedAt,
    string ControllerName,
    double RestDurationMs,
    bool SnapBackIncluded,
    bool WasDisturbed,
    StickDiagnosis LeftStick,
    StickDiagnosis RightStick,
    TriggerDiagnosis LeftTrigger,
    TriggerDiagnosis RightTrigger,
    IReadOnlyList<ButtonGlitch> ButtonGlitches)
{
    [JsonIgnore]
    public Severity ButtonSeverity =>
        ButtonGlitches.Count == 0 ? Severity.Healthy
        : ButtonGlitches.Any(g => g.IsStuck) ? Severity.Severe
        : ButtonGlitches.All(g => g.PressCount == 1 && g.LongestPressMs <= 30) ? Severity.Minor
        : Severity.Moderate;

    [JsonIgnore]
    public Severity Overall => new[]
    {
        LeftStick.Severity, RightStick.Severity, LeftTrigger.Severity, RightTrigger.Severity, ButtonSeverity,
    }.Max();

    public StickDiagnosis Stick(StickSide side) => side == StickSide.Left ? LeftStick : RightStick;

    public TriggerDiagnosis Trigger(TriggerSide side) => side == TriggerSide.Left ? LeftTrigger : RightTrigger;
}
