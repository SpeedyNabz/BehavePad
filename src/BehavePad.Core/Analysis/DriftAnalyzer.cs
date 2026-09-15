using BehavePad.Core.Filtering;
using BehavePad.Core.Input;

namespace BehavePad.Core.Analysis;

/// <summary>Turns raw test samples into a plain diagnosis of every unintended input.</summary>
public static class DriftAnalyzer
{
    /// <summary>Below this rest distance a stick is fine in practically every game.</summary>
    public const double HealthyStickLimit = 0.05;

    /// <summary>Below this, drift only shows in games with small or no deadzones.</summary>
    public const double MinorStickLimit = 0.12;

    /// <summary>Below this, drift shows in many games. Above it, drift shows almost everywhere.</summary>
    public const double ModerateStickLimit = 0.25;

    /// <summary>A button held down for this share of the test is treated as stuck.</summary>
    public const double StuckFraction = 0.8;

    public const int MaxPlotPoints = 320;

    public static DriftReport Analyze(RestCapture rest, SnapBackCapture? snapBack, string controllerName, DateTimeOffset? capturedAt = null)
    {
        ArgumentNullException.ThrowIfNull(rest);
        var snapIncluded = snapBack is not null && (snapBack.LeftSettles.Count > 0 || snapBack.RightSettles.Count > 0);

        return new DriftReport(
            capturedAt ?? DateTimeOffset.Now,
            controllerName,
            Math.Round(rest.DurationMs),
            snapIncluded,
            rest.WasDisturbed,
            AnalyzeStick(StickSide.Left, rest, snapBack?.LeftSettles),
            AnalyzeStick(StickSide.Right, rest, snapBack?.RightSettles),
            AnalyzeTrigger(TriggerSide.Left, rest),
            AnalyzeTrigger(TriggerSide.Right, rest),
            AnalyzeButtons(rest));
    }

    internal static StickDiagnosis AnalyzeStick(StickSide side, RestCapture rest, IReadOnlyList<StickPoint>? settles)
    {
        settles ??= [];
        var count = rest.States.Count;
        if (count == 0 && settles.Count == 0)
        {
            return new StickDiagnosis(side, StickPoint.Zero, 0, 0, 0, StickPoint.Zero, 0, null, [], [], Severity.Healthy);
        }

        var points = new StickPoint[count];
        for (var i = 0; i < count; i++)
        {
            points[i] = rest.States[i].Stick(side);
        }

        var restCenter = count > 0 ? Stats.Mean(points) : Stats.Mean(settles.ToArray());
        var jitter = Stats.Percentile(points.Select(p => p.DistanceTo(restCenter)), 0.99);
        var worst = Stats.Percentile(points.Select(p => p.Magnitude), 0.995);

        var center = restCenter;
        var residual = jitter;
        double? spread = null;

        if (settles.Count > 0)
        {
            var all = settles.Append(restCenter).ToArray();
            center = Stats.Mean(all);
            spread = settles.Max(p => p.DistanceTo(center));
            residual = all.Max(p => p.DistanceTo(center)) + jitter;
            worst = Math.Max(worst, settles.Max(p => p.Magnitude));
        }

        return new StickDiagnosis(
            side,
            Stats.Round(restCenter),
            Stats.Round(restCenter.Magnitude),
            Stats.Round(jitter),
            Stats.Round(worst),
            Stats.Round(center),
            Stats.Round(residual),
            spread is null ? null : Stats.Round(spread.Value),
            Stats.Decimate(points, MaxPlotPoints),
            settles.Select(Stats.Round).ToArray(),
            ClassifyStick(worst, jitter))
        {
            RestOutline = StickZone.ConvexHull(WithoutSpikes(points)).Select(Stats.Round).ToArray(),
        };
    }

    /// <summary>
    /// Drops single readings that jump away and straight back, which are sensor glitches rather than drift.
    /// Polls often repeat a reading, so this compares each distinct reading with its neighbors.
    /// </summary>
    internal static List<StickPoint> WithoutSpikes(IReadOnlyList<StickPoint> points, double jump = 0.05)
    {
        var distinct = new List<StickPoint>(points.Count);
        foreach (var point in points)
        {
            if (distinct.Count == 0 || distinct[^1] != point)
            {
                distinct.Add(point);
            }
        }

        var kept = new List<StickPoint>(distinct.Count);
        for (var i = 0; i < distinct.Count; i++)
        {
            var spike = i > 0 && i < distinct.Count - 1 &&
                distinct[i].DistanceTo(distinct[i - 1]) > jump &&
                distinct[i].DistanceTo(distinct[i + 1]) > jump &&
                distinct[i - 1].DistanceTo(distinct[i + 1]) <= jump;
            if (!spike)
            {
                kept.Add(distinct[i]);
            }
        }

        return kept;
    }

    internal static Severity ClassifyStick(double worstRestDistance, double jitter)
    {
        var severity = worstRestDistance switch
        {
            < HealthyStickLimit => Severity.Healthy,
            < MinorStickLimit => Severity.Minor,
            < ModerateStickLimit => Severity.Moderate,
            _ => Severity.Severe,
        };

        if (jitter >= 0.08)
        {
            severity = Max(severity, Severity.Moderate);
        }
        else if (jitter >= 0.03)
        {
            severity = Max(severity, Severity.Minor);
        }

        return severity;
    }

    internal static TriggerDiagnosis AnalyzeTrigger(TriggerSide side, RestCapture rest)
    {
        if (rest.States.Count == 0)
        {
            return new TriggerDiagnosis(side, 0, 0, Severity.Healthy);
        }

        double sum = 0;
        byte max = 0;
        foreach (var state in rest.States)
        {
            var value = state.Trigger(side);
            sum += value;
            max = Math.Max(max, value);
        }

        var severity = max switch
        {
            <= 3 => Severity.Healthy,
            <= 15 => Severity.Minor,
            <= 38 => Severity.Moderate,
            _ => Severity.Severe,
        };

        return new TriggerDiagnosis(side, Stats.Round(sum / rest.States.Count / 255.0), Stats.Round(max / 255.0), severity);
    }

    internal static IReadOnlyList<ButtonGlitch> AnalyzeButtons(RestCapture rest)
    {
        var glitches = new List<ButtonGlitch>();
        var count = rest.States.Count;
        if (count == 0)
        {
            return glitches;
        }

        var duration = Math.Max(1, rest.DurationMs);
        var sampleInterval = count > 1 ? duration / (count - 1) : 1;

        foreach (var button in GamepadButtonsExtensions.AllSingles)
        {
            var presses = 0;
            double longest = 0, total = 0;
            double start = 0;
            var wasDown = false;

            for (var i = 0; i < count; i++)
            {
                var down = (rest.States[i].Buttons & button) != 0;
                var time = rest.TimesMs[i];
                if (down && !wasDown)
                {
                    presses++;
                    start = time;
                }
                else if (!down && wasDown)
                {
                    var length = time - start;
                    total += length;
                    longest = Math.Max(longest, length);
                }

                wasDown = down;
            }

            if (wasDown)
            {
                var length = Math.Max(rest.TimesMs[count - 1] - start, sampleInterval);
                total += length;
                longest = Math.Max(longest, length);
            }

            if (presses > 0)
            {
                var fraction = Math.Clamp(total / duration, 0, 1);
                glitches.Add(new ButtonGlitch(button, presses, Math.Round(longest, 1), Stats.Round(fraction), fraction >= StuckFraction));
            }
        }

        return glitches;
    }

    private static Severity Max(Severity a, Severity b) => (Severity)Math.Max((int)a, (int)b);
}
