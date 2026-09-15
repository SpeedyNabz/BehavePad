using System.Runtime.InteropServices;

namespace BehavePad.Core.Native;

/// <summary>Asks Windows for a finer sleep granularity so 1 ms polling really runs near 1 ms.</summary>
internal sealed class TimerResolution : IDisposable
{
    private readonly uint _period;
    private bool _active;

    private TimerResolution(uint period, bool active)
    {
        _period = period;
        _active = active;
    }

    public static TimerResolution Request(uint periodMs)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new TimerResolution(periodMs, false);
        }

        try
        {
            return new TimerResolution(periodMs, timeBeginPeriod(periodMs) == 0);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return new TimerResolution(periodMs, false);
        }
    }

    public void Dispose()
    {
        if (_active)
        {
            timeEndPeriod(_period);
            _active = false;
        }
    }

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint period);
}
