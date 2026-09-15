using BehavePad.Core.Analysis;
using BehavePad.Core.Input;

namespace BehavePad.Core.Filtering;

/// <summary>Designs a filter that removes exactly what the drift test measured, plus a safety margin.</summary>
public static class FilterProfileBuilder
{
    public const double MaxStickDeadzone = 0.45;
    public const double MaxTriggerDeadzone = 0.35;

    /// <summary>No fitted zone ignores more of the stick than the largest circle does.</summary>
    public const double MaxZoneArea = MaxStickDeadzone * MaxStickDeadzone;

    public static FilterProfile Build(DriftReport report, ProtectionLevel level, bool adaptiveCentering = false, ZoneShape shape = ZoneShape.Circle)
    {
        ArgumentNullException.ThrowIfNull(report);
        return new FilterProfile
        {
            Name = report.ControllerName,
            CreatedAt = report.CapturedAt,
            Level = level,
            LeftStick = BuildStick(report.LeftStick, level),
            RightStick = BuildStick(report.RightStick, level),
            LeftTrigger = BuildTrigger(report.LeftTrigger, level),
            RightTrigger = BuildTrigger(report.RightTrigger, level),
            Buttons = BuildButtons(report.ButtonGlitches, level),
            AdaptiveCentering = adaptiveCentering,
            ZoneShape = shape,
        };
    }

    /// <summary>The deadzone that would cover the measured drift, before BehavePad caps it at <see cref="MaxStickDeadzone"/>.</summary>
    public static double RequiredStickDeadzone(StickDiagnosis diagnosis, ProtectionLevel level)
    {
        var (multiplier, margin, minimum) = level switch
        {
            ProtectionLevel.Precise => (1.15, 0.012, 0.015),
            ProtectionLevel.Maximum => (1.6, 0.05, 0.06),
            _ => (1.35, 0.025, 0.03),
        };

        var residual = diagnosis.ResidualRadius;
        if (diagnosis.SettlePoints.Count == 0)
        {
            // Without the snap-back step we cannot see how far the rest spot wanders after use.
            // Worn sticks that rest far off center usually wander more, so assume half the offset.
            residual = Math.Max(residual, diagnosis.RestOffset * 0.5);
        }

        return Math.Max(residual * multiplier + margin, minimum);
    }

    /// <summary>
    /// True when even the smallest safe zone of this shape can't cover this stick's drift. Beyond this point the zone
    /// would swallow too much of the stick's travel, so some drift gets through and the thumbstick needs repair.
    /// </summary>
    public static bool ExceedsFilterRange(StickDiagnosis diagnosis, ZoneShape shape = ZoneShape.Circle)
    {
        if (shape == ZoneShape.Circle)
        {
            return RequiredStickDeadzone(diagnosis, ProtectionLevel.Precise) > MaxStickDeadzone;
        }

        var (outline, margin) = RequiredOutline(diagnosis, ProtectionLevel.Precise);
        return StickZone.Area(outline, margin) / Math.PI > MaxZoneArea ||
            outline.Any(p => p.Magnitude > StickFilterSettings.MaxOutlineOffset);
    }

    /// <summary>Margin a fitted zone adds around every spot the test saw. The outline already holds the measured wobble.</summary>
    public static double OutlineMargin(ProtectionLevel level) => level switch
    {
        ProtectionLevel.Precise => 0.02,
        ProtectionLevel.Maximum => 0.06,
        _ => 0.035,
    };

    /// <summary>The fitted zone that would cover every spot the test saw, before BehavePad caps its size.</summary>
    public static (StickPoint[] Outline, double Margin) RequiredOutline(StickDiagnosis diagnosis, ProtectionLevel level)
    {
        ArgumentNullException.ThrowIfNull(diagnosis);
        var rest = diagnosis.RestOutline.Count > 0 ? diagnosis.RestOutline : diagnosis.RestPoints;
        var points = rest.Concat(diagnosis.SettlePoints).DefaultIfEmpty(diagnosis.EstimatedCenter);

        var margin = OutlineMargin(level);
        if (diagnosis.SettlePoints.Count == 0)
        {
            // Without the snap-back step the outline only shows where the stick sat once. As with the circle,
            // assume a stick that rests far off center wanders further.
            margin += Math.Min(diagnosis.RestOffset * 0.25, 0.08);
        }

        var outline = StickZone.Simplify(StickZone.ConvexHull(points), StickFilterSettings.MaxOutlineCorners, out var growth);
        return (outline, margin + growth);
    }

    /// <summary>
    /// Fits a zone to everything the test saw. When that would ignore more than <see cref="MaxZoneArea"/>, the margin
    /// shrinks toward the Precise one first and then the outline pulls in, so some drift gets through as with a capped circle.
    /// </summary>
    public static (StickPoint[] Outline, double Margin) FitOutline(StickDiagnosis diagnosis, ProtectionLevel level)
    {
        var (outline, margin) = RequiredOutline(diagnosis, level);
        if (StickZone.Area(outline, margin) / Math.PI <= MaxZoneArea)
        {
            return (outline, margin);
        }

        // Solve polygon + perimeter * m + pi * m^2 = MaxZoneArea * pi for the largest margin m that fits.
        var minimum = OutlineMargin(ProtectionLevel.Precise);
        var (polygon, perimeter) = StickZone.Measure(outline);
        var discriminant = perimeter * perimeter - 4 * Math.PI * (polygon - MaxZoneArea * Math.PI);
        var largest = discriminant < 0 ? -1 : (-perimeter + Math.Sqrt(discriminant)) / (2 * Math.PI);

        return largest >= minimum
            ? (outline, largest)
            : (StickZone.ShrinkToArea(outline, minimum, MaxZoneArea), minimum);
    }

    public static StickFilterSettings BuildStick(StickDiagnosis diagnosis, ProtectionLevel level)
    {
        var deadzone = Math.Min(RequiredStickDeadzone(diagnosis, level), MaxStickDeadzone);
        var noiseGate = diagnosis.Jitter >= 0.006 ? Math.Min(diagnosis.Jitter * 0.75, 0.02) : 0;
        var hysteresis = Math.Clamp(0.006 + diagnosis.Jitter * 0.5, 0.006, 0.03);
        var (outline, margin) = FitOutline(diagnosis, level);

        return new StickFilterSettings
        {
            CenterX = Math.Round(diagnosis.EstimatedCenter.X, 5),
            CenterY = Math.Round(diagnosis.EstimatedCenter.Y, 5),
            Deadzone = Math.Round(deadzone, 4),
            NoiseGate = Math.Round(noiseGate, 4),
            Hysteresis = Math.Round(hysteresis, 4),
            Outline = outline.Select(p => new StickPoint(Math.Round(p.X, 5), Math.Round(p.Y, 5))).ToArray(),

            // Round up so rounding the corners never uncovers a measured spot.
            OutlineMargin = Math.Round(margin, 4, MidpointRounding.ToPositiveInfinity),
        }.Sanitized();
    }

    public static TriggerFilterSettings BuildTrigger(TriggerDiagnosis diagnosis, ProtectionLevel level)
    {
        var (multiplier, margin, floor) = level switch
        {
            ProtectionLevel.Precise => (1.1, 0.01, 0.0),
            ProtectionLevel.Maximum => (1.5, 0.04, 0.02),
            _ => (1.25, 0.02, 0.0),
        };

        var deadzone = diagnosis.RestMax <= 1.0 / 255
            ? floor
            : Math.Clamp(diagnosis.RestMax * multiplier + margin, floor, MaxTriggerDeadzone);

        return new TriggerFilterSettings { Deadzone = Math.Round(deadzone, 4) }.Sanitized();
    }

    public static ButtonFilterSettings BuildButtons(IReadOnlyList<ButtonGlitch> glitches, ProtectionLevel level)
    {
        var debounce = new List<ButtonDebounce>();
        var blocked = GamepadButtons.None;

        foreach (var glitch in glitches)
        {
            if (glitch.IsStuck)
            {
                blocked |= glitch.Button;
                continue;
            }

            var milliseconds = level switch
            {
                ProtectionLevel.Precise => Math.Clamp(glitch.LongestPressMs + 4, 8, 50),
                ProtectionLevel.Maximum => Math.Clamp(glitch.LongestPressMs * 2 + 16, 16, 120),
                _ => Math.Clamp(glitch.LongestPressMs * 1.5 + 8, 12, 80),
            };

            debounce.Add(new ButtonDebounce(glitch.Button, (int)Math.Ceiling(milliseconds)));
        }

        return new ButtonFilterSettings { Debounce = debounce, Blocked = blocked };
    }
}
