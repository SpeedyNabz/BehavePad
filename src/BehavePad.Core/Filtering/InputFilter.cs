using System.Numerics;
using BehavePad.Core.Input;

namespace BehavePad.Core.Filtering;

/// <summary>Running totals of what the filter kept away from games.</summary>
public readonly record struct FilterStatistics(
    double LeftStickBlockedMs,
    double RightStickBlockedMs,
    double TriggerBlockedMs,
    int PhantomPressesBlocked,
    int CenterAdjustments,
    int ZoneGrowths)
{
    public double StickBlockedMs => LeftStickBlockedMs + RightStickBlockedMs;
}

/// <summary>
/// Applies a <see cref="FilterProfile"/> to live controller input. One instance keeps timing state,
/// so feed it every poll from a single thread.
/// </summary>
public sealed class InputFilter
{
    /// <summary>Raw stick travel that counts as blocked drift when the output is zero.</summary>
    public const double BlockedStickThreshold = 0.02;

    public const byte BlockedTriggerThreshold = 3;

    /// <summary>Adaptive centering only fires when the stick moves less than this.</summary>
    public const double AdaptiveStillRange = 0.004;

    public const double AdaptiveStillWindowMs = 2500;

    /// <summary>Adaptive centering never moves further than this from the tested center.</summary>
    public const double MaxAdaptiveShift = 0.3;

    private readonly StickRuntime _left;
    private readonly StickRuntime _right;
    private readonly (GamepadButtons Button, int Bit, int Milliseconds)[] _debounce;
    private readonly double[] _pressStart = new double[16];
    private double _lastTime = double.NaN;
    private double _leftBlockedMs;
    private double _rightBlockedMs;
    private double _triggerBlockedMs;
    private int _phantomBlocked;
    private int _centerAdjustments;
    private int _zoneGrowths;

    public InputFilter(FilterProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        Profile = profile.Sanitized();
        _left = CreateRuntime(Profile.LeftStick);
        _right = CreateRuntime(Profile.RightStick);
        Array.Fill(_pressStart, double.NaN);
        _debounce = Profile.Buttons.Debounce
            .Where(d => d.Milliseconds > 0 && BitOperations.PopCount((uint)d.Button) == 1)
            .Select(d => (d.Button, BitOperations.TrailingZeroCount((uint)d.Button), d.Milliseconds))
            .ToArray();
    }

    public FilterProfile Profile { get; }

    public FilterStatistics Statistics =>
        new(_leftBlockedMs, _rightBlockedMs, _triggerBlockedMs, _phantomBlocked, _centerAdjustments, _zoneGrowths);

    /// <summary>Center in use for the left stick, including any adaptive adjustment.</summary>
    public StickPoint LeftCenter => _left.Center;

    public StickPoint RightCenter => _right.Center;

    /// <summary>The fitted zone in use for a stick, including anything learned during play, or null when the stick uses a circle.</summary>
    public StickZone? Zone(StickSide side) => Runtime(side).Zone;

    /// <summary>Spots a stick's fitted zone has learned, including those saved in the profile. Safe to read from another thread.</summary>
    public IReadOnlyList<StickPoint> LearnedPoints(StickSide side) =>
        Runtime(side).Learner?.Learned ?? Profile.Stick(side).Learned;

    public GamepadState Apply(in GamepadState raw, double nowMs)
    {
        var elapsed = double.IsNaN(_lastTime) ? 0 : Math.Clamp(nowMs - _lastTime, 0, 50);
        _lastTime = nowMs;

        short leftX = raw.LeftX, leftY = raw.LeftY, rightX = raw.RightX, rightY = raw.RightY;
        var left = raw.LeftStick;
        var right = raw.RightStick;

        if (Profile.LeftStick.Enabled)
        {
            var rawLeft = left;
            left = FilterStick(rawLeft, Profile.LeftStick, _left, nowMs);
            if (left == StickPoint.Zero && rawLeft.Magnitude >= BlockedStickThreshold)
            {
                _leftBlockedMs += elapsed;
            }

            leftX = StickPoint.ToRaw(left.X);
            leftY = StickPoint.ToRaw(left.Y);
        }

        if (Profile.RightStick.Enabled)
        {
            var rawRight = right;
            right = FilterStick(rawRight, Profile.RightStick, _right, nowMs);
            if (right == StickPoint.Zero && rawRight.Magnitude >= BlockedStickThreshold)
            {
                _rightBlockedMs += elapsed;
            }

            rightX = StickPoint.ToRaw(right.X);
            rightY = StickPoint.ToRaw(right.Y);
        }

        var leftTrigger = FilterTrigger(raw.LeftTrigger, Profile.LeftTrigger);
        var rightTrigger = FilterTrigger(raw.RightTrigger, Profile.RightTrigger);
        if ((leftTrigger == 0 && raw.LeftTrigger > BlockedTriggerThreshold) ||
            (rightTrigger == 0 && raw.RightTrigger > BlockedTriggerThreshold))
        {
            _triggerBlockedMs += elapsed;
        }

        var buttons = FilterButtons(raw.Buttons, nowMs);

        if (Profile.LearnZone)
        {
            // Filtered values, so drift the filter already blocks doesn't count as someone playing.
            var controlsIdle = buttons == GamepadButtons.None && leftTrigger == 0 && rightTrigger == 0;
            Learn(_left, controlsIdle && right == StickPoint.Zero, nowMs);
            Learn(_right, controlsIdle && left == StickPoint.Zero, nowMs);
        }

        return new GamepadState(leftX, leftY, rightX, rightY, leftTrigger, rightTrigger, buttons);
    }

    internal static byte FilterTrigger(byte raw, TriggerFilterSettings settings)
    {
        if (!settings.Enabled || (settings.Deadzone <= 0 && settings.OuterDeadzone >= 1))
        {
            return raw;
        }

        var value = raw / 255.0;
        if (value <= settings.Deadzone)
        {
            return 0;
        }

        var scaled = (value - settings.Deadzone) / (settings.OuterDeadzone - settings.Deadzone);
        return (byte)Math.Round(Math.Clamp(scaled, 0, 1) * 255);
    }

    private StickRuntime CreateRuntime(StickFilterSettings settings)
    {
        var runtime = new StickRuntime(settings.Center);
        if (Profile.ZoneShape != ZoneShape.Fitted)
        {
            return runtime;
        }

        if (Profile.LearnZone && settings.Enabled)
        {
            var tested = new StickZone(settings.Outline, settings.OutlineMargin);
            runtime.Learner = new ZoneLearner(tested, settings.Learned, FilterProfileBuilder.MaxZoneArea);
            runtime.Zone = runtime.Learner.Zone;
        }
        else
        {
            // Turning learning off keeps what it learned. Forgetting is a separate choice.
            runtime.Zone = new StickZone(settings.Outline.Concat(settings.Learned), settings.OutlineMargin);
        }

        return runtime;
    }

    private StickRuntime Runtime(StickSide side) => side == StickSide.Left ? _left : _right;

    private StickPoint FilterStick(StickPoint raw, StickFilterSettings settings, StickRuntime runtime, double now)
    {
        if (runtime.Zone is { } zone)
        {
            return FilterFitted(raw, settings, runtime, zone);
        }

        if (Profile.AdaptiveCentering)
        {
            UpdateAdaptiveCenter(raw, settings, runtime, now);
        }

        var point = HoldNoise(StickMath.Recenter(raw, runtime.Center), settings, runtime);

        var threshold = runtime.Active ? settings.Deadzone : settings.Deadzone + settings.Hysteresis;
        if (point.Magnitude <= threshold)
        {
            runtime.Active = false;
            return StickPoint.Zero;
        }

        runtime.Active = true;
        return StickMath.ScaleRadial(point, settings.Deadzone, settings.OuterDeadzone);
    }

    /// <summary>Fitted zones work on raw positions. Output starts at the zone edge nearest the stick, so there is nothing to recenter.</summary>
    private static StickPoint FilterFitted(StickPoint raw, StickFilterSettings settings, StickRuntime runtime, StickZone zone)
    {
        var point = HoldNoise(raw, settings, runtime);
        var (nearest, distance) = zone.Nearest(point);
        runtime.Point = point;
        runtime.OutlineDistance = distance;

        var threshold = runtime.Active ? zone.Margin : zone.Margin + settings.Hysteresis;
        if (distance <= threshold)
        {
            runtime.Active = false;
            return StickPoint.Zero;
        }

        runtime.Active = true;
        return zone.Output(point, nearest, distance, settings.OuterDeadzone);
    }

    private static StickPoint HoldNoise(StickPoint point, StickFilterSettings settings, StickRuntime runtime)
    {
        if (settings.NoiseGate <= 0)
        {
            return point;
        }

        if (!runtime.HasHeld || point.DistanceTo(runtime.Held) >= settings.NoiseGate)
        {
            runtime.Held = point;
            runtime.HasHeld = true;
        }

        return runtime.Held;
    }

    private void Learn(StickRuntime runtime, bool othersIdle, double now)
    {
        if (runtime.Learner is { } learner && learner.Observe(runtime.Point, runtime.OutlineDistance, othersIdle, now))
        {
            runtime.Zone = learner.Zone;
            _zoneGrowths++;
        }
    }

    private void UpdateAdaptiveCenter(StickPoint raw, StickFilterSettings settings, StickRuntime runtime, double now)
    {
        var nearRadius = settings.Deadzone * 2.5 + 0.02;
        if (raw.DistanceTo(runtime.Center) > nearRadius)
        {
            runtime.Window.Clear();
            return;
        }

        if (!runtime.Window.Add(raw, now, AdaptiveStillRange) || now - runtime.Window.Start < AdaptiveStillWindowMs)
        {
            return;
        }

        var mean = runtime.Window.Mean;
        runtime.Window.Clear();

        if (mean.DistanceTo(runtime.Center) <= settings.Deadzone * 0.5 || mean.DistanceTo(settings.Center) > MaxAdaptiveShift)
        {
            return;
        }

        runtime.Center = mean;
        _centerAdjustments++;
    }

    private GamepadButtons FilterButtons(GamepadButtons raw, double now)
    {
        var output = raw & ~Profile.Buttons.Blocked;

        foreach (var (button, bit, milliseconds) in _debounce)
        {
            if ((raw & button) != 0)
            {
                if (double.IsNaN(_pressStart[bit]))
                {
                    _pressStart[bit] = now;
                }

                if (now - _pressStart[bit] < milliseconds)
                {
                    output &= ~button;
                }
            }
            else
            {
                if (!double.IsNaN(_pressStart[bit]) && now - _pressStart[bit] < milliseconds)
                {
                    _phantomBlocked++;
                }

                _pressStart[bit] = double.NaN;
            }
        }

        return output;
    }

    private sealed class StickRuntime(StickPoint center)
    {
        public StickPoint Center = center;
        public StickPoint Held;
        public bool HasHeld;
        public bool Active;
        public StillWindow Window;

        /// <summary>Replaced, never changed in place, so the UI thread can read it while the pump thread learns.</summary>
        public volatile StickZone? Zone;
        public ZoneLearner? Learner;
        public StickPoint Point;
        public double OutlineDistance;
    }

    private struct StillWindow
    {
        public int Count;
        public double Start;
        private double _minX, _maxX, _minY, _maxY, _sumX, _sumY;

        public readonly StickPoint Mean => Count == 0 ? StickPoint.Zero : new StickPoint(_sumX / Count, _sumY / Count);

        public void Clear() => Count = 0;

        /// <summary>Adds a point. Returns false and starts over when the stick moved too much.</summary>
        public bool Add(StickPoint point, double time, double range)
        {
            if (Count == 0)
            {
                Restart(point, time);
                return true;
            }

            var minX = Math.Min(_minX, point.X);
            var maxX = Math.Max(_maxX, point.X);
            var minY = Math.Min(_minY, point.Y);
            var maxY = Math.Max(_maxY, point.Y);
            if (maxX - minX > range || maxY - minY > range)
            {
                Restart(point, time);
                return false;
            }

            (_minX, _maxX, _minY, _maxY) = (minX, maxX, minY, maxY);
            _sumX += point.X;
            _sumY += point.Y;
            Count++;
            return true;
        }

        private void Restart(StickPoint point, double time)
        {
            Count = 1;
            Start = time;
            _minX = _maxX = _sumX = point.X;
            _minY = _maxY = _sumY = point.Y;
        }
    }
}
