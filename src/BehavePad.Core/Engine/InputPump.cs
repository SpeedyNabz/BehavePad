using System.Diagnostics;
using BehavePad.Core.Filtering;
using BehavePad.Core.Input;
using BehavePad.Core.Native;

namespace BehavePad.Core.Engine;

/// <summary>One poll of the input pipeline, safe to read from the UI thread.</summary>
public readonly record struct PumpFrame(
    bool Connected,
    int Slot,
    GamepadState Raw,
    GamepadState Output,
    uint PacketNumber,
    double TimestampMs,
    bool Filtering,
    bool Forwarding,
    FilterStatistics Statistics,
    StickPoint LeftCenter,
    StickPoint RightCenter,
    double PollRateHz);

/// <summary>
/// Polls the physical controller on a dedicated thread, runs the filter, and forwards the result
/// to a virtual controller. Game rumble sent to the virtual controller is passed back to the real one.
/// </summary>
public sealed class InputPump : IDisposable
{
    public const int AutoSlot = -1;
    private const double RescanIntervalMs = 400;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(1);

    private readonly object _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private IGamepadSource _source;
    private InputFilter? _filter;
    private IVirtualPadSink? _sink;
    private Action<GamepadState, double>? _observer;
    private int _preferredSlot = AutoSlot;
    private int _excludedSlot = -1;
    private PumpFrame _latest;
    private string? _sinkError;
    private Thread? _thread;
    private volatile bool _running;
    private volatile int _activeSlot = -1;

    public InputPump(IGamepadSource source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    public double NowMs => _clock.Elapsed.TotalMilliseconds;

    public bool IsRunning => _running;

    public IGamepadSource Source
    {
        get
        {
            lock (_gate)
            {
                return _source;
            }
        }

        set
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_gate)
            {
                _source = value;
                _activeSlot = -1;
            }
        }
    }

    /// <summary>The active filter, or null to pass input through untouched.</summary>
    public InputFilter? Filter
    {
        get
        {
            lock (_gate)
            {
                return _filter;
            }
        }

        set
        {
            lock (_gate)
            {
                _filter = value;
            }
        }
    }

    /// <summary>Where filtered input goes, or null when nothing should reach games.</summary>
    public IVirtualPadSink? Sink
    {
        get
        {
            lock (_gate)
            {
                return _sink;
            }
        }

        set
        {
            lock (_gate)
            {
                if (ReferenceEquals(_sink, value))
                {
                    return;
                }

                DetachSink();
                _sink = value;
                if (_sink is not null)
                {
                    _sink.RumbleRequested += OnRumbleRequested;
                }
            }
        }
    }

    /// <summary>Controller slot to read, or <see cref="AutoSlot"/> for the first connected one.</summary>
    public int PreferredSlot
    {
        get
        {
            lock (_gate)
            {
                return _preferredSlot;
            }
        }

        set
        {
            lock (_gate)
            {
                _preferredSlot = value;
                _activeSlot = -1;
            }
        }
    }

    /// <summary>A slot that must never be read, such as the virtual controller BehavePad itself creates.</summary>
    public int ExcludedSlot
    {
        get
        {
            lock (_gate)
            {
                return _excludedSlot;
            }
        }

        set
        {
            lock (_gate)
            {
                _excludedSlot = value;
                if (_activeSlot == value)
                {
                    _activeSlot = -1;
                }
            }
        }
    }

    public PumpFrame Latest
    {
        get
        {
            lock (_gate)
            {
                return _latest;
            }
        }
    }

    /// <summary>Receives every raw sample on the pump thread. Used by the drift test recorders.</summary>
    public void SetObserver(Action<GamepadState, double>? observer)
    {
        lock (_gate)
        {
            _observer = observer;
        }
    }

    /// <summary>Returns and clears the last error from the virtual controller, if it failed.</summary>
    public string? TakeSinkError()
    {
        lock (_gate)
        {
            var error = _sinkError;
            _sinkError = null;
            return error;
        }
    }

    public void Start()
    {
        if (_running)
        {
            return;
        }

        _running = true;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "BehavePad input pump",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join(1000);
        _thread = null;
    }

    public void Dispose()
    {
        Stop();
        lock (_gate)
        {
            DetachSink();
        }
    }

    private void Run()
    {
        ProcessThrottling.KeepResponsive();
        using var sleeper = new PrecisionSleeper();
        var lastScan = double.NegativeInfinity;
        var rateWindowStart = NowMs;
        var rateCount = 0;
        double rate = 0;
        var lastSubmitted = default(GamepadState);
        var hasSubmitted = false;
        IVirtualPadSink? lastSink = null;

        while (_running)
        {
            IGamepadSource source;
            InputFilter? filter;
            IVirtualPadSink? sink;
            Action<GamepadState, double>? observer;
            int preferred, excluded;
            lock (_gate)
            {
                source = _source;
                filter = _filter;
                sink = _sink;
                observer = _observer;
                preferred = _preferredSlot;
                excluded = _excludedSlot;
            }

            var now = NowMs;
            var slot = _activeSlot;
            GamepadReading reading = default;
            var connected = false;

            if (slot >= 0 && slot != excluded && (preferred == AutoSlot || preferred == slot))
            {
                connected = source.TryRead(slot, out reading);
            }

            if (!connected && now - lastScan >= RescanIntervalMs)
            {
                lastScan = now;
                slot = FindSlot(source, preferred, excluded);
                if (slot >= 0)
                {
                    connected = source.TryRead(slot, out reading);
                }
            }

            if (!connected)
            {
                slot = -1;
            }

            _activeSlot = slot;

            var raw = connected ? reading.State : default;
            var output = raw;
            if (connected)
            {
                if (filter is not null)
                {
                    output = filter.Apply(raw, now);
                }

                if (observer is not null)
                {
                    try
                    {
                        observer(raw, now);
                    }
                    catch (Exception ex)
                    {
                        Trace.TraceError($"BehavePad observer failed: {ex}");
                    }
                }
            }

            if (!ReferenceEquals(sink, lastSink))
            {
                hasSubmitted = false;
                lastSink = sink;
            }

            if (sink is not null)
            {
                var forwarded = connected ? output : default;
                if (!hasSubmitted || forwarded != lastSubmitted)
                {
                    try
                    {
                        sink.Submit(forwarded);
                        lastSubmitted = forwarded;
                        hasSubmitted = true;
                    }
                    catch (Exception ex)
                    {
                        lock (_gate)
                        {
                            _sinkError = ex.Message;
                            if (ReferenceEquals(_sink, sink))
                            {
                                DetachSink();
                            }
                        }
                    }
                }
            }

            rateCount++;
            if (now - rateWindowStart >= 1000)
            {
                rate = rateCount * 1000 / (now - rateWindowStart);
                rateCount = 0;
                rateWindowStart = now;
            }

            var frame = new PumpFrame(
                connected,
                slot,
                raw,
                output,
                reading.PacketNumber,
                now,
                filter is not null,
                sink is not null,
                filter?.Statistics ?? default,
                filter?.LeftCenter ?? default,
                filter?.RightCenter ?? default,
                rate);

            lock (_gate)
            {
                _latest = frame;
            }

            sleeper.Sleep(PollInterval);
        }
    }

    private static int FindSlot(IGamepadSource source, int preferred, int excluded)
    {
        if (preferred >= 0)
        {
            return preferred != excluded && source.TryRead(preferred, out _) ? preferred : -1;
        }

        for (var slot = 0; slot < XInputSource.MaxSlots; slot++)
        {
            if (slot != excluded && source.TryRead(slot, out _))
            {
                return slot;
            }
        }

        return -1;
    }

    private void DetachSink()
    {
        if (_sink is not null)
        {
            _sink.RumbleRequested -= OnRumbleRequested;
            _sink = null;
        }
    }

    private void OnRumbleRequested(object? sender, RumbleEventArgs e)
    {
        var slot = _activeSlot;
        if (slot < 0)
        {
            return;
        }

        try
        {
            Source.SetVibration(slot, e.LowFrequency, e.HighFrequency);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"BehavePad could not pass rumble through: {ex.Message}");
        }
    }
}
