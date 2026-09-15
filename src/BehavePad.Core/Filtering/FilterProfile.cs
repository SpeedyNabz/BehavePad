using System.Text.Json.Serialization;
using BehavePad.Core.Input;

namespace BehavePad.Core.Filtering;

/// <summary>How aggressively the filter trades precision for drift protection.</summary>
public enum ProtectionLevel
{
    /// <summary>Smallest deadzones. Best for aiming, but a badly worn stick may still creep.</summary>
    Precise,

    /// <summary>Covers everything the test measured with a comfortable margin.</summary>
    Balanced,

    /// <summary>Extra margin for controllers whose drift gets worse over time.</summary>
    Maximum,
}

/// <summary>The shape of the area around each stick's rest spot that outputs nothing.</summary>
public enum ZoneShape
{
    /// <summary>A circle around the measured rest center.</summary>
    Circle,

    /// <summary>An outline around every spot the stick drifted or sprang back to during the test, grown by a margin.</summary>
    Fitted,
}

public sealed record StickFilterSettings
{
    public const double MaxCenterOffset = 0.5;
    public const double MaxDeadzone = 0.9;

    /// <summary>Fitted outlines never reach further from true center than this, so every push keeps some room to ramp up.</summary>
    public const double MaxOutlineOffset = 0.8;

    public const double MaxOutlineMargin = 0.45;

    public const int MaxOutlineCorners = 32;

    public bool Enabled { get; init; } = true;

    /// <summary>Measured rest position. The filter treats this point as the new center.</summary>
    public double CenterX { get; init; }

    public double CenterY { get; init; }

    /// <summary>Radius around the center that outputs nothing.</summary>
    public double Deadzone { get; init; }

    /// <summary>Stick travel that already counts as full deflection.</summary>
    public double OuterDeadzone { get; init; } = 0.97;

    /// <summary>Movements smaller than this are treated as sensor noise.</summary>
    public double NoiseGate { get; init; }

    /// <summary>Extra travel needed to leave the deadzone, which stops flicker right at its edge.</summary>
    public double Hysteresis { get; init; } = 0.006;

    /// <summary>Corners of the fitted zone's outline from the drift test. Used when the profile's shape is <see cref="ZoneShape.Fitted"/>.</summary>
    public IReadOnlyList<StickPoint> Outline { get; init; } = [];

    /// <summary>How far past its outline the fitted zone reaches.</summary>
    public double OutlineMargin { get; init; }

    /// <summary>Spots the fitted zone learned during play, on top of the tested outline.</summary>
    public IReadOnlyList<StickPoint> Learned { get; init; } = [];

    [JsonIgnore]
    public StickPoint Center => new(CenterX, CenterY);

    public StickFilterSettings Sanitized()
    {
        var deadzone = Math.Clamp(Finite(Deadzone), 0, MaxDeadzone);
        var center = new StickPoint(
            Math.Clamp(Finite(CenterX), -MaxCenterOffset, MaxCenterOffset),
            Math.Clamp(Finite(CenterY), -MaxCenterOffset, MaxCenterOffset));

        var margin = Math.Clamp(Finite(OutlineMargin), 0, MaxOutlineMargin);
        var outline = StickZone.ConvexHull(WithinReach(Outline));
        if (outline.Length == 0)
        {
            // Profiles saved before fitted zones existed. A zone around the center matches their circle.
            outline = [center];
            margin = Math.Min(deadzone, MaxOutlineMargin);
        }
        else if (outline.Length > MaxOutlineCorners)
        {
            outline = StickZone.Simplify(outline, MaxOutlineCorners, out var growth);
            margin = Math.Min(margin + growth, MaxOutlineMargin);
        }

        return this with
        {
            CenterX = center.X,
            CenterY = center.Y,
            Deadzone = deadzone,
            OuterDeadzone = Math.Clamp(Finite(OuterDeadzone, 1), deadzone + 0.05, 1.0),
            NoiseGate = Math.Clamp(Finite(NoiseGate), 0, 0.05),
            Hysteresis = Math.Clamp(Finite(Hysteresis), 0, 0.05),
            Outline = outline,
            OutlineMargin = margin,
            Learned = WithinReach(Learned).Take(MaxOutlineCorners).ToArray(),
        };
    }

    /// <summary>Compares the outlines point by point, so a profile loaded from disk equals the one that was saved.</summary>
    public bool Equals(StickFilterSettings? other) =>
        other is not null &&
        Enabled == other.Enabled &&
        CenterX.Equals(other.CenterX) &&
        CenterY.Equals(other.CenterY) &&
        Deadzone.Equals(other.Deadzone) &&
        OuterDeadzone.Equals(other.OuterDeadzone) &&
        NoiseGate.Equals(other.NoiseGate) &&
        Hysteresis.Equals(other.Hysteresis) &&
        OutlineMargin.Equals(other.OutlineMargin) &&
        (Outline ?? []).SequenceEqual(other.Outline ?? []) &&
        (Learned ?? []).SequenceEqual(other.Learned ?? []);

    public override int GetHashCode() =>
        HashCode.Combine(Enabled, CenterX, CenterY, Deadzone, OuterDeadzone, NoiseGate, Hysteresis, OutlineMargin);

    internal static double Finite(double value, double fallback = 0) => double.IsFinite(value) ? value : fallback;

    /// <summary>Drops unusable points and pulls any beyond <see cref="MaxOutlineOffset"/> back to it.</summary>
    private static IEnumerable<StickPoint> WithinReach(IReadOnlyList<StickPoint>? points) =>
        (points ?? [])
            .Where(p => double.IsFinite(p.X) && double.IsFinite(p.Y))
            .Select(p => p.Magnitude > MaxOutlineOffset ? p * (MaxOutlineOffset / p.Magnitude) : p);
}

public sealed record TriggerFilterSettings
{
    public const double MaxDeadzone = 0.9;

    public bool Enabled { get; init; } = true;

    /// <summary>Trigger travel, from 0 to 1, that outputs nothing.</summary>
    public double Deadzone { get; init; }

    public double OuterDeadzone { get; init; } = 1.0;

    public TriggerFilterSettings Sanitized()
    {
        var deadzone = Math.Clamp(StickFilterSettings.Finite(Deadzone), 0, MaxDeadzone);
        return this with
        {
            Deadzone = deadzone,
            OuterDeadzone = Math.Clamp(StickFilterSettings.Finite(OuterDeadzone, 1), deadzone + 0.05, 1.0),
        };
    }
}

/// <summary>A press must last this long before it reaches games.</summary>
public sealed record ButtonDebounce(GamepadButtons Button, int Milliseconds);

public sealed record ButtonFilterSettings
{
    public IReadOnlyList<ButtonDebounce> Debounce { get; init; } = [];

    /// <summary>Buttons that never reach games, for switches that are stuck down.</summary>
    public GamepadButtons Blocked { get; init; }

    public int DebounceFor(GamepadButtons button) =>
        Debounce.FirstOrDefault(d => d.Button == button)?.Milliseconds ?? 0;
}

/// <summary>Everything needed to clean up one controller's input.</summary>
public sealed record FilterProfile
{
    /// <summary>Version 2 added fitted zones. Older profiles load with a circle-sized zone until the drift test runs again.</summary>
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public string Name { get; init; } = "My controller";

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    public ProtectionLevel Level { get; init; } = ProtectionLevel.Balanced;

    public StickFilterSettings LeftStick { get; init; } = new();

    public StickFilterSettings RightStick { get; init; } = new();

    public TriggerFilterSettings LeftTrigger { get; init; } = new();

    public TriggerFilterSettings RightTrigger { get; init; } = new();

    public ButtonFilterSettings Buttons { get; init; } = new();

    /// <summary>Follows the stick's rest position if it moves during play. Off by default. Applies to circles only.</summary>
    public bool AdaptiveCentering { get; init; }

    /// <summary>Shape of both sticks' ignore zones.</summary>
    public ZoneShape ZoneShape { get; init; }

    /// <summary>Lets fitted zones grow during play to cover drift that creeps further. Off by default.</summary>
    public bool LearnZone { get; init; }

    public StickFilterSettings Stick(StickSide side) => side == StickSide.Left ? LeftStick : RightStick;

    public TriggerFilterSettings Trigger(TriggerSide side) => side == TriggerSide.Left ? LeftTrigger : RightTrigger;

    public FilterProfile WithStick(StickSide side, StickFilterSettings settings) =>
        side == StickSide.Left ? this with { LeftStick = settings } : this with { RightStick = settings };

    public FilterProfile WithTrigger(TriggerSide side, TriggerFilterSettings settings) =>
        side == TriggerSide.Left ? this with { LeftTrigger = settings } : this with { RightTrigger = settings };

    public FilterProfile WithLearned(IReadOnlyList<StickPoint> left, IReadOnlyList<StickPoint> right) => this with
    {
        LeftStick = LeftStick with { Learned = left },
        RightStick = RightStick with { Learned = right },
    };

    public FilterProfile Sanitized() => this with
    {
        LeftStick = (LeftStick ?? new StickFilterSettings()).Sanitized(),
        RightStick = (RightStick ?? new StickFilterSettings()).Sanitized(),
        LeftTrigger = (LeftTrigger ?? new TriggerFilterSettings()).Sanitized(),
        RightTrigger = (RightTrigger ?? new TriggerFilterSettings()).Sanitized(),
        Buttons = Buttons ?? new ButtonFilterSettings(),
        ZoneShape = Enum.IsDefined(ZoneShape) ? ZoneShape : ZoneShape.Circle,
    };
}
