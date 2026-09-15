using System.Runtime.InteropServices;

namespace BehavePad.Core.Native;

/// <summary>
/// Sleeps for about one millisecond. A plain Thread.Sleep(1) can take 15.6 ms on Windows, and Windows 11
/// ignores timer resolution requests from apps without a visible window, which would add input lag while
/// BehavePad filters from the notification area. High resolution waitable timers avoid both problems.
/// </summary>
internal sealed class PrecisionSleeper : IDisposable
{
    private const uint CreateWaitableTimerHighResolution = 0x00000002;
    private const uint TimerAllAccess = 0x1F0003;
    private const uint WaitObject0 = 0;
    private const uint Infinite = 0xFFFFFFFF;

    private readonly TimerResolution? _fallbackResolution;
    private nint _timer;

    public PrecisionSleeper()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                _timer = CreateWaitableTimerExW(0, null, CreateWaitableTimerHighResolution, TimerAllAccess);
            }
            catch (EntryPointNotFoundException)
            {
                _timer = 0;
            }
        }

        if (_timer == 0)
        {
            _fallbackResolution = TimerResolution.Request(1);
        }
    }

    public bool IsHighResolution => _timer != 0;

    public void Sleep(TimeSpan duration)
    {
        if (_timer != 0)
        {
            // Negative due time means relative, in 100 nanosecond units.
            var dueTime = -Math.Max(1, duration.Ticks);
            if (SetWaitableTimer(_timer, ref dueTime, 0, 0, 0, false) && WaitForSingleObject(_timer, Infinite) == WaitObject0)
            {
                return;
            }
        }

        Thread.Sleep(duration);
    }

    public void Dispose()
    {
        if (_timer != 0)
        {
            CloseHandle(_timer);
            _timer = 0;
        }

        _fallbackResolution?.Dispose();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWaitableTimerExW(nint attributes, string? name, uint flags, uint desiredAccess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(nint timer, ref long dueTime, int period, nint completionRoutine, nint completionArgument, [MarshalAs(UnmanagedType.Bool)] bool resume);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(nint handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
