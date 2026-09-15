using System.Numerics;
using BehavePad.Core.Input;

namespace BehavePad.Core.Analysis;

/// <summary>Collects controller samples while the controller lies untouched.</summary>
public sealed class RestRecorder
{
    public const int MaxSamples = 180_000;

    /// <summary>Stick travel that means someone picked up or bumped the controller.</summary>
    public const double DisturbanceDistance = 0.35;

    private const int DisturbanceButtonCount = 3;

    private readonly object _gate = new();
    private readonly List<GamepadState> _states = new(16_000);
    private readonly List<double> _times = new(16_000);
    private GamepadState _first;
    private GamepadButtons _previousButtons;
    private GamepadButtons _distinctPressed;
    private int _pressEvents;
    private bool _disturbed;

    public int SampleCount
    {
        get
        {
            lock (_gate)
            {
                return _states.Count;
            }
        }
    }

    public double ElapsedMs
    {
        get
        {
            lock (_gate)
            {
                return _times.Count < 2 ? 0 : _times[^1] - _times[0];
            }
        }
    }

    /// <summary>Button presses seen so far, counted as each button going down.</summary>
    public int PressEvents
    {
        get
        {
            lock (_gate)
            {
                return _pressEvents;
            }
        }
    }

    public bool IsDisturbed
    {
        get
        {
            lock (_gate)
            {
                return _disturbed;
            }
        }
    }

    public void Add(in GamepadState state, double timeMs)
    {
        lock (_gate)
        {
            if (_states.Count >= MaxSamples)
            {
                return;
            }

            if (_states.Count == 0)
            {
                _first = state;
                _previousButtons = GamepadButtons.None;
            }

            _states.Add(state);
            _times.Add(timeMs);

            var rising = state.Buttons & ~_previousButtons;
            if (rising != GamepadButtons.None)
            {
                _pressEvents += BitOperations.PopCount((uint)rising);
                _distinctPressed |= rising;
            }

            _previousButtons = state.Buttons;

            if (!_disturbed &&
                (state.LeftStick.DistanceTo(_first.LeftStick) > DisturbanceDistance ||
                 state.RightStick.DistanceTo(_first.RightStick) > DisturbanceDistance ||
                 BitOperations.PopCount((uint)_distinctPressed) >= DisturbanceButtonCount))
            {
                _disturbed = true;
            }
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _states.Clear();
            _times.Clear();
            _first = default;
            _previousButtons = GamepadButtons.None;
            _distinctPressed = GamepadButtons.None;
            _pressEvents = 0;
            _disturbed = false;
        }
    }

    public RestCapture ToCapture()
    {
        lock (_gate)
        {
            return new RestCapture(_states.ToArray(), _times.ToArray(), _disturbed);
        }
    }
}

/// <summary>An immutable set of samples taken while the controller was resting.</summary>
public sealed class RestCapture
{
    public RestCapture(IReadOnlyList<GamepadState> states, IReadOnlyList<double> timesMs, bool wasDisturbed)
    {
        ArgumentNullException.ThrowIfNull(states);
        ArgumentNullException.ThrowIfNull(timesMs);
        if (states.Count != timesMs.Count)
        {
            throw new ArgumentException("Every sample needs a timestamp.", nameof(timesMs));
        }

        States = states;
        TimesMs = timesMs;
        WasDisturbed = wasDisturbed;
    }

    public IReadOnlyList<GamepadState> States { get; }

    public IReadOnlyList<double> TimesMs { get; }

    public bool WasDisturbed { get; }

    public double DurationMs => TimesMs.Count < 2 ? 0 : TimesMs[^1] - TimesMs[0];
}
