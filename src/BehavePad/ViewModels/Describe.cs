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

    public static (string Title, string Body) Verdict(DriftReport report, ZoneShape shape)
    {
        var issues = Issues(report);
        if (issues.Count == 0)
        {
            return ("Your controller is well-behaved", "Nothing moved on its own while it rested. You can still save a light filter as a safety net.");
        }

        var title = issues.Count == 1 ? $"{issues[0]} detected" : $"{issues.Count} issues detected";

        var beyondFilter = new[] { report.LeftStick, report.RightStick }.Where(s => FilterProfileBuilder.ExceedsFilterRange(s, shape)).ToList();
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

    public static string ZoneSentence(StickFilterSettings settings, ZoneShape shape) =>
        shape == ZoneShape.Fitted ? $"Ignores {Percent(IgnoredShare(settings, shape))} of the stick, in an outline shaped to where it drifted."
        : settings.Deadzone <= 0 ? "No deadzone needed."
        : $"Ignores {Percent(settings.Deadzone)} of travel around its real center.";

    public static StickZone FittedZone(StickFilterSettings settings) => new(settings.Outline.Concat(settings.Learned), settings.OutlineMargin);

    /// <summary>Share of a stick's area that the zone ignores, which compares fairly across shapes.</summary>
    public static double IgnoredShare(StickFilterSettings settings, ZoneShape shape) =>
        shape == ZoneShape.Fitted ? FittedZone(settings).AreaShare : Math.Min(settings.Deadzone * settings.Deadzone, 1);

    public static string ShapeCost(FilterProfile profile, ZoneShape shape) =>
        $"Ignores {Percent(IgnoredShare(profile.LeftStick, shape))} of the left stick, {Percent(IgnoredShare(profile.RightStick, shape))} of the right";

    public static string ShapeDescription(DriftReport report, ZoneShape shape)
    {
        if (shape == ZoneShape.Fitted)
        {
            return "Hugs every spot the sticks drifted or sprang back to, so less of each stick is ignored. Diagonal pushes that run along a long drift can bend slightly.";
        }

        var onlyShapedCovers = new[] { report.LeftStick, report.RightStick }
            .Any(s => FilterProfileBuilder.ExceedsFilterRange(s, ZoneShape.Circle) && !FilterProfileBuilder.ExceedsFilterRange(s, ZoneShape.Fitted));
        return onlyShapedCovers
            ? "A circle around each stick's real center, the same size in every direction. A safe circle can't cover this drift, but a shaped zone can."
            : "A circle around each stick's real center, the same size in every direction.";
    }

    /// <summary>Zooms a plot so the drift and the ignore zone fill a good part of it.</summary>
    public static double PlotZoom(StickDiagnosis diagnosis, StickFilterSettings? settings, ZoneShape shape)
    {
        var reach = settings is null ? 0
            : shape == ZoneShape.Fitted ? settings.Outline.Concat(settings.Learned).Select(p => p.Magnitude).DefaultIfEmpty(0).Max() + settings.OutlineMargin
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
