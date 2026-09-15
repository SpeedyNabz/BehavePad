using BehavePad.Core.Filtering;
using BehavePad.Core.Input;

namespace BehavePad.Core.Tests;

public class InputFilterTests
{
    private static GamepadState WithRight(StickPoint right) => default(GamepadState).WithStick(StickSide.Right, right);

    private static FilterProfile DriftProfile() => new()
    {
        RightStick = new StickFilterSettings { CenterX = 0.03, CenterY = -0.11, Deadzone = 0.1 },
    };

    [Fact]
    public void Resting_drift_inside_the_deadzone_is_removed()
    {
        var filter = new InputFilter(DriftProfile());

        var atRest = filter.Apply(WithRight(new StickPoint(0.03, -0.11)), 0);
        var nearby = filter.Apply(WithRight(new StickPoint(0.08, -0.09)), 1);

        Assert.Equal((short)0, atRest.RightX);
        Assert.Equal((short)0, atRest.RightY);
        Assert.Equal((short)0, nearby.RightX);
        Assert.Equal((short)0, nearby.RightY);
    }

    [Fact]
    public void Full_deflection_still_reaches_full_output()
    {
        var filter = new InputFilter(DriftProfile());
        var raw = new GamepadState(-32767, 0, 0, 32767, 0, 0, GamepadButtons.None);

        var output = filter.Apply(raw, 0);

        Assert.Equal((short)32767, output.RightY);
        Assert.Equal((short)-32767, output.LeftX);
    }

    [Fact]
    public void Hysteresis_prevents_flicker_at_the_deadzone_edge()
    {
        var filter = new InputFilter(new FilterProfile
        {
            RightStick = new StickFilterSettings { Deadzone = 0.1, Hysteresis = 0.02, OuterDeadzone = 1.0 },
        });

        Assert.Equal((short)0, filter.Apply(WithRight(new StickPoint(0.11, 0)), 0).RightX);
        Assert.NotEqual((short)0, filter.Apply(WithRight(new StickPoint(0.13, 0)), 1).RightX);
        Assert.NotEqual((short)0, filter.Apply(WithRight(new StickPoint(0.105, 0)), 2).RightX);
        Assert.Equal((short)0, filter.Apply(WithRight(new StickPoint(0.095, 0)), 3).RightX);
    }

    [Fact]
    public void Trigger_rest_pressure_is_removed_and_full_pull_is_kept()
    {
        var filter = new InputFilter(new FilterProfile { LeftTrigger = new TriggerFilterSettings { Deadzone = 0.05 } });

        Assert.Equal((byte)0, filter.Apply(new GamepadState(0, 0, 0, 0, 10, 0, GamepadButtons.None), 0).LeftTrigger);
        Assert.Equal((byte)255, filter.Apply(new GamepadState(0, 0, 0, 0, 255, 0, GamepadButtons.None), 1).LeftTrigger);
        Assert.Equal((byte)121, filter.Apply(new GamepadState(0, 0, 0, 0, 128, 0, GamepadButtons.None), 2).LeftTrigger);
    }

    [Fact]
    public void Debounce_blocks_a_short_phantom_press()
    {
        var filter = new InputFilter(new FilterProfile
        {
            Buttons = new ButtonFilterSettings { Debounce = [new ButtonDebounce(GamepadButtons.Y, 30)] },
        });
        var pressed = new GamepadState(0, 0, 0, 0, 0, 0, GamepadButtons.Y);

        Assert.Equal(GamepadButtons.None, filter.Apply(pressed, 0).Buttons);
        Assert.Equal(GamepadButtons.None, filter.Apply(pressed, 10).Buttons);
        Assert.Equal(GamepadButtons.None, filter.Apply(default, 15).Buttons);
        Assert.Equal(1, filter.Statistics.PhantomPressesBlocked);
    }

    [Fact]
    public void Debounce_lets_a_real_press_through_after_the_delay()
    {
        var filter = new InputFilter(new FilterProfile
        {
            Buttons = new ButtonFilterSettings { Debounce = [new ButtonDebounce(GamepadButtons.Y, 30)] },
        });
        var pressed = new GamepadState(0, 0, 0, 0, 0, 0, GamepadButtons.Y | GamepadButtons.A);

        Assert.Equal(GamepadButtons.A, filter.Apply(pressed, 0).Buttons);
        Assert.Equal(GamepadButtons.A, filter.Apply(pressed, 29).Buttons);
        Assert.Equal(GamepadButtons.Y | GamepadButtons.A, filter.Apply(pressed, 30).Buttons);
        Assert.Equal(GamepadButtons.Y | GamepadButtons.A, filter.Apply(pressed, 100).Buttons);
        Assert.Equal(GamepadButtons.None, filter.Apply(default, 101).Buttons);
        Assert.Equal(0, filter.Statistics.PhantomPressesBlocked);
    }

    [Fact]
    public void Blocked_buttons_never_reach_the_output()
    {
        var filter = new InputFilter(new FilterProfile { Buttons = new ButtonFilterSettings { Blocked = GamepadButtons.LeftShoulder } });
        var raw = new GamepadState(0, 0, 0, 0, 0, 0, GamepadButtons.LeftShoulder | GamepadButtons.A);

        Assert.Equal(GamepadButtons.A, filter.Apply(raw, 0).Buttons);
    }

    [Fact]
    public void Disabled_stick_passes_raw_values_through_exactly()
    {
        var filter = new InputFilter(new FilterProfile { LeftStick = new StickFilterSettings { Enabled = false } });
        var raw = new GamepadState(-32768, 123, 0, 0, 0, 0, GamepadButtons.None);

        var output = filter.Apply(raw, 0);

        Assert.Equal((short)-32768, output.LeftX);
        Assert.Equal((short)123, output.LeftY);
    }

    [Fact]
    public void Time_spent_blocking_drift_is_counted()
    {
        var filter = new InputFilter(DriftProfile());
        for (var t = 0; t <= 1000; t += 10)
        {
            filter.Apply(WithRight(new StickPoint(0.03, -0.11)), t);
        }

        Assert.Equal(1000, filter.Statistics.RightStickBlockedMs, 3);
        Assert.Equal(0, filter.Statistics.LeftStickBlockedMs, 3);
    }

    [Fact]
    public void Adaptive_centering_follows_a_new_rest_position()
    {
        var filter = new InputFilter(new FilterProfile
        {
            AdaptiveCentering = true,
            RightStick = new StickFilterSettings { Deadzone = 0.05, Hysteresis = 0 },
        });
        var drifted = WithRight(new StickPoint(0.09, 0));

        Assert.NotEqual((short)0, filter.Apply(drifted, 0).RightX);

        GamepadState output = default;
        for (var t = 5; t < 3000; t += 5)
        {
            output = filter.Apply(drifted, t);
        }

        Assert.Equal((short)0, output.RightX);
        Assert.Equal(0.09, filter.RightCenter.X, 3);
        Assert.Equal(1, filter.Statistics.CenterAdjustments);
    }

    [Fact]
    public void Adaptive_centering_ignores_a_moving_stick()
    {
        var filter = new InputFilter(new FilterProfile
        {
            AdaptiveCentering = true,
            RightStick = new StickFilterSettings { Deadzone = 0.05 },
        });

        for (var t = 0; t < 8000; t += 5)
        {
            filter.Apply(WithRight(new StickPoint(0.09 + 0.02 * Math.Sin(t / 50.0), 0)), t);
        }

        Assert.Equal(0, filter.Statistics.CenterAdjustments);
        Assert.Equal(0, filter.RightCenter.X, 6);
    }
}
