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

/// <summary>The shape of the area around a stick's rest spot that outputs nothing.</summary>
public enum ZoneShape
{
    /// <summary>A circle around the measured rest center.</summary>
    Circle,

    /// <summary>An outline around every spot the stick drifted or sprang back to during the test, grown by a margin.</summary>
    Fitted,
}

/// <summary>The zone shape and preset picked for one stick.</summary>
public readonly record struct StickChoice(ProtectionLevel Level, ZoneShape Shape);

public sealed record StickFilterSettings
{
    public const double MaxCenterOffset = 0.5;
    public const double MaxDeadzone = 0.9;

    /// <summary>Fitted outlines never reach further from true center than this, so every push keeps some room to ramp up.</summary>
    public const double MaxOutlineOffset = 0.8;

    public const double MaxOutlineMargin = 0.45;

    public const int MaxOutlineCorners = 32;

    public bool Enabled { get; init; } = true;

    /// <summary>Preset this stick's zone was built with.</summary>
    public ProtectionLevel Level { get; init; } = ProtectionLevel.Balanced;

    /// <summary>Which ignore zone this stick uses.</summary>
    public ZoneShape Shape { get; init; }

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

    /// <summary>Corners of the fitted zone's outline from the drift test. Used when <see cref="Shape"/> is <see cref="ZoneShape.Fitted"/>.</summary>
    public IReadOnlyList<StickPoint> Outline { get; init; } = [];

    /// <summary>How far past its outline the fitted zone reaches.</summary>
    public double OutlineMargin { get; init; }

    /// <summary>Spots the fitted zone learned during play, on top of the tested outline.</summary>
    public IReadOnlyList<StickPoint> Learned { get; init; } = [];

    [JsonIgnore]
    public StickPoint Center => new(CenterX, CenterY);

    [JsonIgnore]
    public StickChoice Choice => new(Level, Shape);

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
            Level = Enum.IsDefined(Level) ? Level : ProtectionLevel.Balanced,
            Shape = Enum.IsDefined(Shape) ? Shape : ZoneShape.Circle,
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
        Level == other.Level &&
        Shape == other.Shape &&
        CenterX.Equals(other.CenterX) &&
        CenterY.Equals(other.CenterY) &&
        Deadzone.Equals(other.Deadzone) &&
        OuterDeadzone.Equals(other.OuterDeadzone) &&
        NoiseGate.Equals(other.NoiseGate) &&
        Hysteresis.Equals(other.Hysteresis) &&
        OutlineMargin.Equals(other.OutlineMargin) &&
        (Outline ?? []).SequenceEqual(other.Outline ?? []) &&
        (Learned ?? []).SequenceEqual(other.Learned ?? []);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Enabled);
        hash.Add(Level);
        hash.Add(Shape);
        hash.Add(CenterX);
        hash.Add(CenterY);
        hash.Add(Deadzone);
        hash.Add(OuterDeadzone);
        hash.Add(NoiseGate);
        hash.Add(Hysteresis);
        hash.Add(OutlineMargin);
        return hash.ToHashCode();
    }

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
    /// <summary>
    /// Version 2 added fitted zones. Version 3 gave each stick its own shape and preset. Older profiles are upgraded
    /// when they are sanitized after loading.
    /// </summary>
    public const int CurrentSchemaVersion = 3;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public string Name { get; init; } = "My controller";

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    /// <summary>Preset for the triggers and buttons. Profiles before version 3 used it for the sticks too.</summary>
    public ProtectionLevel Level { get; init; } = ProtectionLevel.Balanced;

    public StickFilterSettings LeftStick { get; init; } = new();

    public StickFilterSettings RightStick { get; init; } = new();

    public TriggerFilterSettings LeftTrigger { get; init; } = new();

    public TriggerFilterSettings RightTrigger { get; init; } = new();

    public ButtonFilterSettings Buttons { get; init; } = new();

    /// <summary>Follows the stick's rest position if it moves during play. Off by default. Applies to sticks with a circle.</summary>
    public bool AdaptiveCentering { get; init; }

    /// <summary>The shape both sticks shared in version 2 profiles. Read only to upgrade them, and never saved again.</summary>
    [JsonPropertyName("zoneShape")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ZoneShape? LegacyZoneShape { get; init; }

    /// <summary>Lets fitted zones grow during play to cover drift that creeps further. Off by default. Applies to sticks with a fitted zone.</summary>
    public bool LearnZone { get; init; }

    public StickFilterSettings Stick(StickSide side) => side == StickSide.Left ? LeftStick : RightStick;

    public TriggerFilterSettings Trigger(TriggerSide side) => side == TriggerSide.Left ? LeftTrigger : RightTrigger;

    public bool AnyStickUses(ZoneShape shape) => LeftStick.Shape == shape || RightStick.Shape == shape;

    public FilterProfile WithStick(StickSide side, StickFilterSettings settings) =>
        side == StickSide.Left ? this with { LeftStick = settings } : this with { RightStick = settings };

    public FilterProfile WithTrigger(TriggerSide side, TriggerFilterSettings settings) =>
        side == TriggerSide.Left ? this with { LeftTrigger = settings } : this with { RightTrigger = settings };

    public FilterProfile WithLearned(IReadOnlyList<StickPoint> left, IReadOnlyList<StickPoint> right) => this with
    {
        LeftStick = LeftStick with { Learned = left },
        RightStick = RightStick with { Learned = right },
    };

    public FilterProfile Sanitized()
    {
        var left = LeftStick ?? new StickFilterSettings();
        var right = RightStick ?? new StickFilterSettings();
        if (SchemaVersion < 3)
        {
            // Older profiles used one shape and one preset for everything.
            var shape = LegacyZoneShape ?? ZoneShape.Circle;
            left = left with { Level = Level, Shape = shape };
            right = right with { Level = Level, Shape = shape };
        }

        return this with
        {
            SchemaVersion = Math.Max(SchemaVersion, CurrentSchemaVersion),
            LegacyZoneShape = null,
            Level = Enum.IsDefined(Level) ? Level : ProtectionLevel.Balanced,
            LeftStick = left.Sanitized(),
            RightStick = right.Sanitized(),
            LeftTrigger = (LeftTrigger ?? new TriggerFilterSettings()).Sanitized(),
            RightTrigger = (RightTrigger ?? new TriggerFilterSettings()).Sanitized(),
            Buttons = Buttons ?? new ButtonFilterSettings(),
        };
    }
}
