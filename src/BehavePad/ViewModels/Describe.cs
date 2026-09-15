using BehavePad.Core.Analysis;
using BehavePad.Core.Filtering;
using BehavePad.Core.Input;

namespace BehavePad.ViewModels;

/// <summary>Plain-language descriptions of test results, so no one needs to know what a deadzone is.</summary>
internal static class Describe
{
    public static string Percent(double value) => $"{value * 100:0.#}%";

    public static string Duration(double milliseconds)
    {
        var span = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        if (span.TotalSeconds < 60)
        {
            return $"{span.TotalSeconds:0}s";
        }

        return span.TotalMinutes < 60
            ? $"{(int)span.TotalMinutes}m {span.Seconds:00}s"
            : $"{(int)span.TotalHours}h {span.Minutes:00}m";
    }

    public static string StickTitle(StickDiagnosis diagnosis) => diagnosis.Severity switch
    {
        Severity.Healthy => "Rests centered",
        Severity.Minor => "Slight drift",
        Severity.Moderate => "Noticeable drift",
        _ => "Severe drift",
    };

    public static string StickDetail(StickDiagnosis diagnosis, ZoneShape shape)
    {
        var parts = new List<string> { $"Rests {Percent(diagnosis.WorstRestDistance)} from center" };
        if (diagnosis.SettleSpread is double spread)
        {
            parts.Add($"springs back within {Percent(spread)}");
        }

        if (diagnosis.Jitter >= 0.006)
        {
            parts.Add($"wobbles {Percent(diagnosis.Jitter)}");
        }

        var detail = string.Join(", ", parts) + ".";
        return FilterProfileBuilder.ExceedsFilterRange(diagnosis, shape)
            ? detail + " That is more than a filter can hide without making the stick unusable."
            : detail;
    }

    public static (Severity Severity, string Title, string Detail) Triggers(DriftReport report)
    {
        var worst = report.LeftTrigger.Severity >= report.RightTrigger.Severity ? report.LeftTrigger : report.RightTrigger;
        if (worst.Severity == Severity.Healthy)
        {
            return (Severity.Healthy, "Fully released", "Both triggers read zero while untouched.");
        }

        var name = worst.Side == TriggerSide.Left ? "Left trigger" : "Right trigger";
        return (worst.Severity, "Trigger creep", $"{name} reads {Percent(worst.RestMax)} pressed while untouched.");
    }

    public static (Severity Severity, string Title, string Detail) Buttons(DriftReport report)
    {
        if (report.ButtonGlitches.Count == 0)
        {
            return (Severity.Healthy, "No phantom presses", "No button pressed itself during the test.");
        }

        var stuck = report.ButtonGlitches.Where(g => g.IsStuck).ToList();
        if (stuck.Count > 0)
        {
            var names = JoinNames(stuck.Select(g => g.Button.DisplayName()));
            return (Severity.Severe, stuck.Count == 1 ? "Stuck button" : "Stuck buttons", $"{names} stayed pressed on its own.");
        }

        var detail = string.Join(" ", report.ButtonGlitches.Select(g => $"{g.Button.DisplayName()} pressed itself {Times(g.PressCount)}."));
        return (report.ButtonSeverity, "Phantom presses", detail);
    }

    public static IReadOnlyList<string> Issues(DriftReport report)
    {
        var issues = new List<string>();
        if (report.LeftStick.Severity > Severity.Healthy)
        {
            issues.Add("Left stick drift");
        }

        if (report.RightStick.Severity > Severity.Healthy)
        {
            issues.Add("Right stick drift");
        }

        if (report.LeftTrigger.Severity > Severity.Healthy || report.RightTrigger.Severity > Severity.Healthy)
        {
            issues.Add("Trigger creep");
        }

        if (report.ButtonGlitches.Any(g => g.IsStuck))
        {
            issues.Add("Stuck button");
        }
        else if (report.ButtonGlitches.Count > 0)
        {
            issues.Add("Phantom presses");
        }

        return issues;
    }

    /// <param name="profile">The filter to judge against, so each stick is checked with the shape it uses. Null checks circles.</param>
    public static (string Title, string Body) Verdict(DriftReport report, FilterProfile? profile)
    {
        var issues = Issues(report);
        if (issues.Count == 0)
        {
            return ("Your controller is well-behaved", "Nothing moved on its own while it rested. You can still save a light filter as a safety net.");
        }

        var title = issues.Count == 1 ? $"{issues[0]} detected" : $"{issues.Count} issues detected";

        var beyondFilter = new[] { report.LeftStick, report.RightStick }
            .Where(s => FilterProfileBuilder.ExceedsFilterRange(s, profile?.Stick(s.Side).Shape ?? ZoneShape.Circle))
            .ToList();
        if (beyondFilter.Count > 0)
        {
            var sticks = beyondFilter.Count == 2 ? "Both sticks drift" : beyondFilter[0].Side == StickSide.Left ? "The left stick drifts" : "The right stick drifts";
            return (title, $"{sticks} too far for any filter to hide completely. BehavePad built the strongest safe filter, but some drift can still get through. Replacing that thumbstick is the lasting fix.");
        }

        return (title, "BehavePad built a filter that removes this unintended input and keeps your real movements.");
    }

    public static string LevelDescription(ProtectionLevel level) => level switch
    {
        ProtectionLevel.Precise => "Smallest deadzones for the sharpest aim. A badly worn stick may still creep now and then.",
        ProtectionLevel.Maximum => "Extra margin for controllers whose drift grows as they warm up or wear further.",
        _ => "Covers everything the test measured with a comfortable margin. Recommended for most people.",
    };

    /// <summary>What a stick's zone ignores, in the shape it uses.</summary>
    public static string ZoneSentence(StickFilterSettings settings) =>
        settings.Shape == ZoneShape.Fitted ? $"Ignores {Percent(IgnoredShare(settings, ZoneShape.Fitted))} of the stick, in an outline shaped to where it drifted."
        : settings.Deadzone <= 0 ? "No deadzone needed."
        : $"Ignores {Percent(settings.Deadzone)} of travel around its real center.";

    public static StickZone FittedZone(StickFilterSettings settings) => new(settings.Outline.Concat(settings.Learned), settings.OutlineMargin);

    /// <summary>Share of a stick's area that a zone of <paramref name="shape"/> ignores, which compares fairly across shapes.</summary>
    public static double IgnoredShare(StickFilterSettings settings, ZoneShape shape) =>
        shape == ZoneShape.Fitted ? FittedZone(settings).AreaShare : Math.Min(settings.Deadzone * settings.Deadzone, 1);

    /// <summary>A shape choice with the area it would ignore, such as "Shaped · 2.3%".</summary>
    public static string ShapeLabel(StickFilterSettings settings, ZoneShape shape) =>
        $"{(shape == ZoneShape.Fitted ? "Shaped" : "Round")} · {Percent(IgnoredShare(settings, shape))}";

    /// <summary>Points out a stick that still uses a circle when a safe circle can't cover its drift but a shaped zone can.</summary>
    public static string? ShapeHint(DriftReport report, FilterProfile profile)
    {
        var sticks = new[] { report.LeftStick, report.RightStick }
            .Where(s => profile.Stick(s.Side).Shape == ZoneShape.Circle
                        && FilterProfileBuilder.ExceedsFilterRange(s, ZoneShape.Circle)
                        && !FilterProfileBuilder.ExceedsFilterRange(s, ZoneShape.Fitted))
            .ToList();

        return sticks.Count switch
        {
            0 => null,
            2 => "Both sticks drift too far for a safe circle, but shaped zones can cover them.",
            _ => $"The {(sticks[0].Side == StickSide.Left ? "left" : "right")} stick drifts too far for a safe circle, but a shaped zone can cover it.",
        };
    }

    /// <summary>A short summary of a profile's presets and shapes, such as "Balanced protection" or "Mixed presets · right stick shaped".</summary>
    public static string ProfileSummary(FilterProfile profile)
    {
        var uniform = profile.LeftStick.Level == profile.Level && profile.RightStick.Level == profile.Level;
        var shapes = (profile.LeftStick.Shape, profile.RightStick.Shape) switch
        {
            (ZoneShape.Fitted, ZoneShape.Fitted) => "shaped zones",
            (ZoneShape.Fitted, _) => "left stick shaped",
            (_, ZoneShape.Fitted) => "right stick shaped",
            _ => null,
        };

        var levels = uniform ? profile.Level.ToString() : "Mixed presets";
        return shapes is not null ? $"{levels} · {shapes}"
            : uniform ? $"{levels} protection"
            : levels;
    }

    /// <summary>Zooms a plot so the drift and the ignore zone fill a good part of it.</summary>
    public static double PlotZoom(StickDiagnosis diagnosis, StickFilterSettings? settings)
    {
        var reach = settings is null ? 0
            : settings.Shape == ZoneShape.Fitted ? settings.Outline.Concat(settings.Learned).Select(p => p.Magnitude).DefaultIfEmpty(0).Max() + settings.OutlineMargin
            : diagnosis.EstimatedCenter.Magnitude + settings.Deadzone;
        return Math.Clamp(Math.Max(diagnosis.WorstRestDistance, reach) * 1.7, 0.18, 1.0);
    }

    private static string Times(int count) => count switch
    {
        1 => "once",
        2 => "twice",
        _ => $"{count} times",
    };

    private static string JoinNames(IEnumerable<string> names)
    {
        var list = names.ToList();
        return list.Count <= 1 ? string.Concat(list) : string.Join(", ", list.Take(list.Count - 1)) + " and " + list[^1];
    }
}
