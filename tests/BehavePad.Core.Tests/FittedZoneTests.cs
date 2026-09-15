using BehavePad.Core.Analysis;
using BehavePad.Core.Filtering;
using BehavePad.Core.Input;

namespace BehavePad.Core.Tests;

/// <summary>Uses the right stick from docs/ignore-zone-study.md, which springs back below center and creeps far upward along one line.</summary>
public class FittedZoneTests
{
    private static readonly StickPoint[] StudyRest =
    [
        new(0.047, 0.686), new(0.047, 0.682), new(0.047, 0.681), new(0.047, 0.678), new(0.047, 0.676), new(0.047, 0.673),
        new(0.047, 0.670), new(0.047, 0.667), new(0.047, 0.666), new(0.047, 0.664), new(0.047, 0.661),
    ];

    private static readonly StickPoint[] StudySettles =
    [
        new(0.006, -0.115), new(-0.007, -0.010), new(0.011, -0.121), new(0.001, -0.018), new(0.011, -0.103), new(0.012, -0.126),
    ];

    private static DriftReport StudyReport()
    {
        var recorder = new RestRecorder();
        const int samples = 2200;
        for (var i = 0; i < samples; i++)
        {
            recorder.Add(default(GamepadState).WithStick(StickSide.Right, StudyRest[i * StudyRest.Length / samples]), i);
        }

        return DriftAnalyzer.Analyze(recorder.ToCapture(), new SnapBackCapture([], StudySettles), "Study pad");
    }

    private static GamepadState Right(double x, double y) => default(GamepadState).WithStick(StickSide.Right, new StickPoint(x, y));

    [Fact]
    public void Fitted_zone_covers_every_spot_the_test_saw()
    {
        var report = StudyReport();

        foreach (var level in Enum.GetValues<ProtectionLevel>())
        {
            var settings = FilterProfileBuilder.BuildStick(report.RightStick, level);
            var zone = new StickZone(settings.Outline, settings.OutlineMargin);
            Assert.All(StudyRest.Concat(StudySettles), p => Assert.True(zone.Contains(p), $"{level} misses {p}"));
        }
    }

    [Fact]
    public void Line_drift_fits_a_small_zone_where_the_circle_hits_its_cap()
    {
        var right = StudyReport().RightStick;
        var settings = FilterProfileBuilder.BuildStick(right, ProtectionLevel.Balanced);

        Assert.True(FilterProfileBuilder.ExceedsFilterRange(right, ZoneShape.Circle));
        Assert.False(FilterProfileBuilder.ExceedsFilterRange(right, ZoneShape.Fitted));
        Assert.Equal(FilterProfileBuilder.MaxStickDeadzone, settings.Deadzone, 4);
        Assert.InRange(new StickZone(settings.Outline, settings.OutlineMargin).AreaShare, 0.01, 0.05);
    }

    [Fact]
    public void Fitted_filter_blocks_rest_drift_that_leaks_through_the_capped_circle()
    {
        var profile = FilterProfileBuilder.Build(StudyReport(), ProtectionLevel.Balanced);
        var circle = new InputFilter(profile);
        var fitted = new InputFilter(profile with { ZoneShape = ZoneShape.Fitted });

        // Where the stick sat untouched at 23:55, 13 minutes after the test.
        var resting = Right(0.047, 0.685);

        Assert.NotEqual(StickPoint.Zero, circle.Apply(resting, 0).RightStick);
        Assert.Equal(StickPoint.Zero, fitted.Apply(resting, 0).RightStick);
    }

    [Fact]
    public void Pushes_away_from_the_drift_keep_their_direction_and_reach_full()
    {
        var filter = new InputFilter(FilterProfileBuilder.Build(StudyReport(), ProtectionLevel.Balanced, shape: ZoneShape.Fitted));

        var partway = filter.Apply(Right(0.6, 0.3), 0).RightStick;
        var full = filter.Apply(Right(0.95, 0.3), 1).RightStick;

        Assert.InRange(Math.Atan2(partway.Y, partway.X) * 180 / Math.PI, -5, 5);
        Assert.InRange(partway.Magnitude, 0.5, 0.75);
        Assert.True(full.X >= 0.99, $"full push right reads {full}");
    }

    [Fact]
    public void Higher_protection_levels_use_wider_margins()
    {
        var right = StudyReport().RightStick;

        var margins = Enum.GetValues<ProtectionLevel>()
            .Select(level => FilterProfileBuilder.BuildStick(right, level).OutlineMargin)
            .ToArray();

        Assert.True(margins[0] < margins[1] && margins[1] < margins[2], string.Join(", ", margins));
    }

    [Fact]
    public void Fitted_zone_never_ignores_more_than_the_largest_circle()
    {
        var recorder = new RestRecorder();
        recorder.Add(default, 0);
        recorder.Add(default, 1);
        var wandering = Enumerable.Range(0, 12)
            .Select(i => new StickPoint(0.6 * Math.Cos(i * Math.Tau / 12), 0.6 * Math.Sin(i * Math.Tau / 12)))
            .ToArray();
        var diagnosis = DriftAnalyzer.Analyze(recorder.ToCapture(), new SnapBackCapture(wandering, []), "Worn pad").LeftStick;

        var settings = FilterProfileBuilder.BuildStick(diagnosis, ProtectionLevel.Maximum);

        Assert.True(FilterProfileBuilder.ExceedsFilterRange(diagnosis, ZoneShape.Fitted));
        Assert.True(new StickZone(settings.Outline, settings.OutlineMargin).AreaShare <= FilterProfileBuilder.MaxZoneArea + 1e-3);
    }

    [Fact]
    public void Profiles_from_before_fitted_zones_get_a_zone_the_size_of_their_circle()
    {
        var settings = new StickFilterSettings { CenterX = 0.03, CenterY = -0.11, Deadzone = 0.12 }.Sanitized();

        Assert.Equal([new StickPoint(0.03, -0.11)], settings.Outline);
        Assert.Equal(0.12, settings.OutlineMargin, 6);
    }

    [Fact]
    public void A_single_glitch_reading_does_not_stretch_the_outline()
    {
        var recorder = new RestRecorder();
        for (var i = 0; i < 1000; i++)
        {
            var point = i == 500 ? new StickPoint(0.3, 0.2) : new StickPoint(0.03, -0.11 + 0.002 * (i % 3));
            recorder.Add(default(GamepadState).WithStick(StickSide.Right, point), i);
        }

        var outline = DriftAnalyzer.Analyze(recorder.ToCapture(), null, "Pad").RightStick.RestOutline;

        Assert.All(outline, p => Assert.True(p.DistanceTo(new StickPoint(0.03, -0.11)) < 0.01, $"{p} came from the glitch"));
    }
}
