using BehavePad.Core.Engine;
using BehavePad.Core.Filtering;
using BehavePad.Core.Input;

namespace BehavePad.Core.Tests;

public class InputPumpTests
{
    private sealed class RecordingSink : IVirtualPadSink
    {
        private readonly object _gate = new();
        private readonly List<GamepadState> _states = [];

        public event EventHandler<RumbleEventArgs>? RumbleRequested;

        public int Count
        {
            get
            {
                lock (_gate)
                {
                    return _states.Count;
                }
            }
        }

        public GamepadState Last
        {
            get
            {
                lock (_gate)
                {
                    return _states[^1];
                }
            }
        }

        public void Submit(in GamepadState state)
        {
            lock (_gate)
            {
                _states.Add(state);
            }
        }

        public void Rumble() => RumbleRequested?.Invoke(this, new RumbleEventArgs(1, 0.5));

        public void Dispose()
        {
        }
    }

    [Fact]
    public void Pump_reads_filters_and_forwards_the_controller()
    {
        var pad = new SimulatedGamepad { PhantomButton = GamepadButtons.None };
        var profile = new FilterProfile
        {
            LeftStick = new StickFilterSettings { Deadzone = 0.2 },
            RightStick = new StickFilterSettings { Deadzone = 0.4 },
            LeftTrigger = new TriggerFilterSettings { Deadzone = 0.1 },
        };
        var sink = new RecordingSink();
        using var pump = new InputPump(pad) { Filter = new InputFilter(profile), Sink = sink };

        pump.Start();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while ((sink.Count == 0 || !pump.Latest.Connected) && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(10);
        }

        sink.Rumble();
        pump.Stop();

        var frame = pump.Latest;
        Assert.True(frame.Connected);
        Assert.Equal(0, frame.Slot);
        Assert.True(frame.Filtering);
        Assert.True(frame.Forwarding);
        Assert.NotEqual(default, frame.Raw);
        Assert.Equal(default, sink.Last);
    }

    [Fact]
    public void Excluded_slot_is_never_read()
    {
        using var pump = new InputPump(new SimulatedGamepad()) { ExcludedSlot = 0 };

        pump.Start();
        Thread.Sleep(600);
        pump.Stop();

        Assert.False(pump.Latest.Connected);
    }

    [Fact]
    public void Pump_polls_hundreds_of_times_per_second()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var pump = new InputPump(new SimulatedGamepad());
        pump.Start();
        Thread.Sleep(2300);
        var rate = pump.Latest.PollRateHz;
        pump.Stop();

        Assert.True(rate > 300, $"Poll rate was {rate:0} per second.");
    }
}
