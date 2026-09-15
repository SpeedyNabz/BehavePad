using BehavePad.Core.Analysis;
using BehavePad.Core.Input;

namespace BehavePad.Core.Tests;

public class DriftAnalyzerTests
{
    private static RestCapture Rest(Func<int, GamepadState> stateAt, int samples = 2000)
    {
        var recorder = new RestRecorder();
        for (var i = 0; i < samples; i++)
        {
            recorder.Add(stateAt(i), i);
        }

        return recorder.ToCapture();
    }

    [Fact]
    public void Healthy_controller_is_reported_healthy()
    {
        var capture = Rest(_ => default(GamepadState).WithStick(StickSide.Left, new StickPoint(0.01, -0.01)));

        var report = DriftAnalyzer.Analyze(capture, null, "Test pad");

        Assert.Equal(Severity.Healthy, report.Overall);
        Assert.Empty(report.ButtonGlitches);
        Assert.False(report.WasDisturbed);
    }

    [Fact]
    public void Off_center_right_stick_is_reported_as_drift()
    {
        // Rest values measured from a real wired controller while building BehavePad.
        var capture = Rest(_ => new GamepadState(575, -467, -181, -3638, 0, 0, GamepadButtons.None));

        var report = DriftAnalyzer.Analyze(capture, null, "Test pad");

        Assert.Equal(Severity.Healthy, report.LeftStick.Severity);
        Assert.Equal(Severity.Minor, report.RightStick.Severity);
        Assert.Equal(-0.111, report.RightStick.RestCenter.Y, 3);
        Assert.Null(report.RightStick.SettleSpread);
    }

    [Fact]
    public void Large_offset_is_severe()
    {
        var capture = Rest(_ => default(GamepadState).WithStick(StickSide.Right, new StickPoint(0, -0.3)));

        Assert.Equal(Severity.Severe, DriftAnalyzer.Analyze(capture, null, "Test pad").RightStick.Severity);
    }

    [Fact]
    public void Wobbling_stick_measures_jitter_around_its_center()
    {
        var capture = Rest(i => default(GamepadState).WithStick(StickSide.Left, new StickPoint(0.02 * Math.Sin(i / 10.0), 0)));

        var left = DriftAnalyzer.Analyze(capture, null, "Test pad").LeftStick;

        Assert.InRange(left.Jitter, 0.019, 0.021);
        Assert.InRange(left.RestCenter.X, -0.002, 0.002);
    }

    [Fact]
    public void Short_phantom_presses_are_detected()
    {
        var capture = Rest(i => new GamepadState(0, 0, 0, 0, 0, 0, i % 500 is >= 250 and < 262 ? GamepadButtons.Y : GamepadButtons.None), samples: 3000);

        var report = DriftAnalyzer.Analyze(capture, null, "Test pad");

        var glitch = Assert.Single(report.ButtonGlitches);
        Assert.Equal(GamepadButtons.Y, glitch.Button);
        Assert.Equal(6, glitch.PressCount);
        Assert.Equal(12, glitch.LongestPressMs, 1);
        Assert.False(glitch.IsStuck);
        Assert.Equal(Severity.Moderate, report.ButtonSeverity);
    }

    [Fact]
    public void Button_held_the_whole_time_is_stuck()
    {
        var capture = Rest(_ => new GamepadState(0, 0, 0, 0, 0, 0, GamepadButtons.LeftShoulder));

        var report = DriftAnalyzer.Analyze(capture, null, "Test pad");

        Assert.True(Assert.Single(report.ButtonGlitches).IsStuck);
        Assert.Equal(Severity.Severe, report.Overall);
    }

    [Fact]
    public void Trigger_rest_pressure_is_detected()
    {
        var capture = Rest(_ => new GamepadState(0, 0, 0, 0, 20, 0, GamepadButtons.None));

        var report = DriftAnalyzer.Analyze(capture, null, "Test pad");

        Assert.Equal(Severity.Moderate, report.LeftTrigger.Severity);
        Assert.Equal(20 / 255.0, report.LeftTrigger.RestMax, 4);
        Assert.Equal(Severity.Healthy, report.RightTrigger.Severity);
    }

    [Fact]
    public void Moving_the_controller_marks_the_capture_disturbed()
    {
        var capture = Rest(i => default(GamepadState).WithStick(StickSide.Left, i > 1000 ? new StickPoint(0.8, 0) : StickPoint.Zero));

        Assert.True(DriftAnalyzer.Analyze(capture, null, "Test pad").WasDisturbed);
    }

    [Fact]
    public void Snap_back_positions_widen_the_residual_radius()
    {
        var capture = Rest(_ => default(GamepadState).WithStick(StickSide.Right, new StickPoint(0, -0.1)));
        var snap = new SnapBackCapture(
            [],
            [new StickPoint(0.04, -0.1), new StickPoint(-0.04, -0.1), new StickPoint(0, -0.06), new StickPoint(0, -0.14)]);

        var right = DriftAnalyzer.Analyze(capture, snap, "Test pad").RightStick;

        Assert.Equal(0, right.EstimatedCenter.X, 3);
        Assert.Equal(-0.1, right.EstimatedCenter.Y, 3);
        Assert.Equal(0.04, right.ResidualRadius, 3);
        Assert.Equal(0.04, right.SettleSpread!.Value, 3);
        Assert.Equal(4, right.SettlePoints.Count);
    }
}
