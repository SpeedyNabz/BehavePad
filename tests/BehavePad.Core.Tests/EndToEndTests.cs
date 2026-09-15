using BehavePad.Core.Analysis;
using BehavePad.Core.Filtering;
using BehavePad.Core.Input;

namespace BehavePad.Core.Tests;

public class EndToEndTests
{
    [Theory]
    [InlineData(ProtectionLevel.Precise, ZoneShape.Circle)]
    [InlineData(ProtectionLevel.Balanced, ZoneShape.Circle)]
    [InlineData(ProtectionLevel.Maximum, ZoneShape.Circle)]
    [InlineData(ProtectionLevel.Precise, ZoneShape.Fitted)]
    [InlineData(ProtectionLevel.Balanced, ZoneShape.Fitted)]
    [InlineData(ProtectionLevel.Maximum, ZoneShape.Fitted)]
    public void Demo_controller_is_silent_at_rest_after_testing(ProtectionLevel level, ZoneShape shape)
    {
        var clock = new TestClock();
        var pad = new SimulatedGamepad(() => clock.Now);
        var report = RunDriftTest(pad, clock);

        Assert.True(report.RightStick.Severity >= Severity.Moderate);
        Assert.Contains(report.ButtonGlitches, g => g.Button == GamepadButtons.Y);
        Assert.False(report.WasDisturbed);

        var filter = new InputFilter(FilterProfileBuilder.Build(report, level, shape: shape));
        pad.Scenario = DemoScenario.Resting;
        var leaks = 0;
        for (var end = clock.Now + 20000; clock.Now < end; clock.Now += 2)
        {
            pad.TryRead(0, out var reading);
            var output = filter.Apply(reading.State, clock.Now);
            if (output != default)
            {
                leaks++;
            }
        }

        Assert.Equal(0, leaks);
        Assert.True(filter.Statistics.RightStickBlockedMs > 15000);
        Assert.True(filter.Statistics.PhantomPressesBlocked >= 8);
    }

    [Fact]
    public void Demo_controller_movement_still_gets_through_the_filter()
    {
        var clock = new TestClock();
        var pad = new SimulatedGamepad(() => clock.Now) { PhantomButton = GamepadButtons.None };
        var profile = new FilterProfile
        {
            LeftStick = new StickFilterSettings { Deadzone = 0.1 },
            RightStick = new StickFilterSettings { CenterX = 0.034, CenterY = -0.158, Deadzone = 0.12 },
            LeftTrigger = new TriggerFilterSettings { Deadzone = 0.06 },
        };

        AssertMovementGetsThrough(pad, clock, new InputFilter(profile));
    }

    [Fact]
    public void Demo_controller_movement_still_gets_through_a_fitted_zone()
    {
        var clock = new TestClock();
        var pad = new SimulatedGamepad(() => clock.Now) { PhantomButton = GamepadButtons.None };
        var report = RunDriftTest(pad, clock);

        AssertMovementGetsThrough(pad, clock, new InputFilter(FilterProfileBuilder.Build(report, ProtectionLevel.Balanced, shape: ZoneShape.Fitted)));
    }

    private static DriftReport RunDriftTest(SimulatedGamepad pad, TestClock clock)
    {
        var rest = new RestRecorder();
        for (var end = clock.Now + 8000; clock.Now < end; clock.Now += 2)
        {
            pad.TryRead(0, out var reading);
            rest.Add(reading.State, clock.Now);
        }

        pad.Scenario = DemoScenario.SnapBack;
        var snap = new SnapBackRecorder();
        for (var end = clock.Now + 30000; !snap.IsComplete && clock.Now < end; clock.Now += 2)
        {
            pad.TryRead(0, out var reading);
            snap.Add(reading.State, clock.Now);
        }

        Assert.True(snap.IsComplete);
        return DriftAnalyzer.Analyze(rest.ToCapture(), snap.ToCapture(), pad.DisplayName);
    }

    private static void AssertMovementGetsThrough(SimulatedGamepad pad, TestClock clock, InputFilter filter)
    {
        pad.Scenario = DemoScenario.Playing;

        double maxLeft = 0, maxRight = 0;
        byte maxRightTrigger = 0;
        var sawA = false;
        for (var end = clock.Now + 9000; clock.Now < end; clock.Now += 2)
        {
            pad.TryRead(0, out var reading);
            var output = filter.Apply(reading.State, clock.Now);
            maxLeft = Math.Max(maxLeft, output.LeftStick.Magnitude);
            maxRight = Math.Max(maxRight, output.RightStick.Magnitude);
            maxRightTrigger = Math.Max(maxRightTrigger, output.RightTrigger);
            sawA |= (output.Buttons & GamepadButtons.A) != 0;
        }

        Assert.True(maxLeft > 0.8, $"left reached {maxLeft}");
        Assert.True(maxRight > 0.8, $"right reached {maxRight}");
        Assert.Equal((byte)255, maxRightTrigger);
        Assert.True(sawA);
    }

    private sealed class TestClock
    {
        public double Now;
    }
}
