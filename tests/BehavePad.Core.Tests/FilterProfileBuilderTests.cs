using BehavePad.Core.Analysis;
using BehavePad.Core.Filtering;
using BehavePad.Core.Input;

namespace BehavePad.Core.Tests;

public class FilterProfileBuilderTests
{
    private static StickDiagnosis Stick(double offset, double residual, bool withSettles) => new(
        StickSide.Right,
        new StickPoint(0, -offset),
        offset,
        0,
        offset,
        new StickPoint(0.01, -offset),
        residual,
        withSettles ? residual : null,
        [],
        withSettles ? [new StickPoint(0, -offset)] : [],
        Severity.Minor);

    [Fact]
    public void Higher_protection_levels_use_larger_deadzones()
    {
        var diagnosis = Stick(0.1, 0.05, withSettles: true);

        var precise = FilterProfileBuilder.BuildStick(diagnosis, ProtectionLevel.Precise).Deadzone;
        var balanced = FilterProfileBuilder.BuildStick(diagnosis, ProtectionLevel.Balanced).Deadzone;
        var maximum = FilterProfileBuilder.BuildStick(diagnosis, ProtectionLevel.Maximum).Deadzone;

        Assert.True(precise < balanced, $"{precise} < {balanced}");
        Assert.True(balanced < maximum, $"{balanced} < {maximum}");
    }

    [Fact]
    public void Drift_beyond_the_largest_safe_deadzone_is_flagged_and_capped()
    {
        var wrecked = Stick(0.45, 0.5, withSettles: true);
        var worn = Stick(0.11, 0.05, withSettles: true);

        Assert.True(FilterProfileBuilder.ExceedsFilterRange(wrecked));
        Assert.False(FilterProfileBuilder.ExceedsFilterRange(worn));
        Assert.Equal(FilterProfileBuilder.MaxStickDeadzone, FilterProfileBuilder.BuildStick(wrecked, ProtectionLevel.Maximum).Deadzone, 4);
    }

    [Fact]
    public void Skipping_the_snap_back_step_adds_a_safety_margin()
    {
        var without = FilterProfileBuilder.BuildStick(Stick(0.11, 0, withSettles: false), ProtectionLevel.Balanced);
        var with = FilterProfileBuilder.BuildStick(Stick(0.11, 0, withSettles: true), ProtectionLevel.Balanced);

        Assert.True(without.Deadzone > with.Deadzone);
        Assert.Equal(0.03, with.Deadzone, 4);
    }

    [Fact]
    public void Filter_center_is_the_estimated_rest_position()
    {
        var settings = FilterProfileBuilder.BuildStick(Stick(0.11, 0.02, withSettles: true), ProtectionLevel.Balanced);

        Assert.Equal(0.01, settings.CenterX, 5);
        Assert.Equal(-0.11, settings.CenterY, 5);
    }

    [Fact]
    public void Healthy_triggers_keep_full_range_unless_maximum_protection()
    {
        var healthy = new TriggerDiagnosis(TriggerSide.Left, 0, 0, Severity.Healthy);

        Assert.Equal(0, FilterProfileBuilder.BuildTrigger(healthy, ProtectionLevel.Precise).Deadzone);
        Assert.Equal(0, FilterProfileBuilder.BuildTrigger(healthy, ProtectionLevel.Balanced).Deadzone);
        Assert.Equal(0.02, FilterProfileBuilder.BuildTrigger(healthy, ProtectionLevel.Maximum).Deadzone, 4);
    }

    [Fact]
    public void Worn_trigger_deadzone_covers_its_rest_pressure()
    {
        var worn = new TriggerDiagnosis(TriggerSide.Left, 0.05, 0.08, Severity.Moderate);

        Assert.True(FilterProfileBuilder.BuildTrigger(worn, ProtectionLevel.Precise).Deadzone > 0.08);
    }

    [Fact]
    public void Phantom_buttons_are_debounced_and_stuck_buttons_are_blocked()
    {
        var settings = FilterProfileBuilder.BuildButtons(
            [
                new ButtonGlitch(GamepadButtons.Y, 3, 16, 0.01, false),
                new ButtonGlitch(GamepadButtons.Back, 1, 9000, 0.95, true),
            ],
            ProtectionLevel.Balanced);

        Assert.Equal(32, settings.DebounceFor(GamepadButtons.Y));
        Assert.Equal(0, settings.DebounceFor(GamepadButtons.Back));
        Assert.Equal(GamepadButtons.Back, settings.Blocked);
    }
}
