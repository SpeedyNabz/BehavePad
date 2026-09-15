using System.Diagnostics;

namespace BehavePad.Core.Input;

public enum DemoScenario
{
    /// <summary>Controller lies untouched: stick drift, trigger creep, and a phantom button press.</summary>
    Resting,

    /// <summary>Someone repeatedly pushes both sticks to the edge and lets go.</summary>
    SnapBack,

    /// <summary>A loop of idle time and normal gameplay movement.</summary>
    Playing,
}

/// <summary>
/// A worn-out virtual controller for trying BehavePad without hardware. It has right stick drift,
/// a creeping left trigger, and a Y button that registers presses on its own.
/// </summary>
public sealed class SimulatedGamepad : IGamepadSource
{
    private readonly object _gate = new();
    private readonly Func<double> _clock;
    private DemoScenario _scenario = DemoScenario.Resting;
    private double _scenarioStart;
    private GamepadState _last;
    private bool _hasLast;
    private uint _packet;

    public SimulatedGamepad()
        : this(null)
    {
    }

    public SimulatedGamepad(Func<double>? clock)
    {
        if (clock is null)
        {
            var stopwatch = Stopwatch.StartNew();
            _clock = () => stopwatch.Elapsed.TotalMilliseconds;
        }
        else
        {
            _clock = clock;
        }

        _scenarioStart = _clock();
    }

    public string DisplayName => "Demo controller";

    public bool IsSimulated => true;

    public StickPoint LeftRest { get; init; } = new(0.016, -0.011);

    public StickPoint RightRest { get; init; } = new(0.034, -0.158);

    public double Noise { get; init; } = 0.0035;

    public byte LeftTriggerRest { get; init; } = 6;

    public GamepadButtons PhantomButton { get; init; } = GamepadButtons.Y;

    public double PhantomIntervalMs { get; init; } = 2300;

    public double PhantomDurationMs { get; init; } = 16;

    public DemoScenario Scenario
    {
        get
        {
            lock (_gate)
            {
                return _scenario;
            }
        }

        set
        {
            lock (_gate)
            {
                if (_scenario == value)
                {
                    return;
                }

                _scenario = value;
                _scenarioStart = _clock();
            }
        }
    }

    public bool TryRead(int slot, out GamepadReading reading)
    {
        if (slot != 0)
        {
            reading = default;
            return false;
        }

        lock (_gate)
        {
            var now = _clock();
            var state = Compute(now, now - _scenarioStart);
            if (!_hasLast || state != _last)
            {
                _packet++;
                _last = state;
                _hasLast = true;
            }

            reading = new GamepadReading(_last, _packet);
            return true;
        }
    }

    public void SetVibration(int slot, double lowFrequency, double highFrequency)
    {
    }

    public BatteryStatus? GetBattery(int slot) =>
        slot == 0 ? new BatteryStatus(BatteryKind.Wired, BatteryLevel.Full) : null;

    private GamepadState Compute(double now, double elapsed)
    {
        // Sensor noise changes in small time buckets, like hardware that only reports when a value moves.
        var bucket = (long)(now / 12);
        var left = LeftRest + new StickPoint(Hash(bucket, 1), Hash(bucket, 2)) * Noise;
        var wander = new StickPoint(0.006 * Math.Sin(now / 1900.0), 0.012 * Math.Sin(now / 2600.0));
        var right = RightRest + wander + new StickPoint(Hash(bucket, 3), Hash(bucket, 4)) * Noise;
        double leftTrigger = LeftTriggerRest + Math.Round(Hash(bucket / 4, 5) * 2);
        double rightTrigger = 0;
        var buttons = GamepadButtons.None;

        if (PhantomButton != GamepadButtons.None && now % PhantomIntervalMs < PhantomDurationMs)
        {
            buttons |= PhantomButton;
        }

        switch (_scenario)
        {
            case DemoScenario.SnapBack:
            {
                const double period = 1300;
                var cycle = (long)(elapsed / period);
                var phase = elapsed % period;
                if (phase < 380)
                {
                    var leftAngle = cycle * 2.4 + 0.3;
                    var rightAngle = cycle * 1.7 + 2.1;
                    left = new StickPoint(Math.Cos(leftAngle), Math.Sin(leftAngle)) * 0.98;
                    right = new StickPoint(Math.Cos(rightAngle), Math.Sin(rightAngle)) * 0.98;
                }
                else
                {
                    // Worn springs return the stick to a slightly different spot every time.
                    left += new StickPoint(Hash(cycle, 11), Hash(cycle, 12)) * 0.02;
                    right += new StickPoint(Hash(cycle, 13), Hash(cycle, 14)) * 0.05;
                }

                break;
            }

            case DemoScenario.Playing:
            {
                var t = elapsed % 9000;
                if (t is >= 3000 and < 6000)
                {
                    var angle = (t - 3000) / 3000 * Math.PI * 4;
                    left = new StickPoint(Math.Cos(angle), Math.Sin(angle)) * 0.85;
                }

                if (t is >= 3500 and < 5000)
                {
                    rightTrigger = 255 * Math.Min(1, (t - 3500) / 500);
                }

                if (t is >= 6000 and < 7200)
                {
                    var k = Math.Sin((t - 6000) / 1200 * Math.PI);
                    right = new StickPoint(0.9 * k, 0.35 * k) + right * (1 - k);
                }

                if (t is >= 6300 and < 6500)
                {
                    buttons |= GamepadButtons.A;
                }

                if (t is >= 7600 and < 7760)
                {
                    buttons |= GamepadButtons.RightShoulder;
                }

                break;
            }
        }

        return new GamepadState(
            StickPoint.ToRaw(left.X),
            StickPoint.ToRaw(left.Y),
            StickPoint.ToRaw(right.X),
            StickPoint.ToRaw(right.Y),
            (byte)Math.Clamp(Math.Round(leftTrigger), 0, 255),
            (byte)Math.Clamp(Math.Round(rightTrigger), 0, 255),
            buttons);
    }

    /// <summary>Deterministic pseudo-random value in [-1, 1].</summary>
    private static double Hash(long n, int salt)
    {
        unchecked
        {
            var x = (ulong)n * 0x9E3779B97F4A7C15UL + (ulong)salt * 0xBF58476D1CE4E5B9UL;
            x ^= x >> 31;
            x *= 0x94D049BB133111EBUL;
            x ^= x >> 29;
            return x % 20001 / 10000.0 - 1.0;
        }
    }
}
