using System.Runtime.InteropServices;

namespace BehavePad.Core.Native;

/// <summary>
/// Asks Windows not to run BehavePad in power-saving mode while its window is hidden.
/// Throttling an input filter would add lag in the game the user is playing.
/// </summary>
internal static class ProcessThrottling
{
    private const int ProcessPowerThrottling = 4;
    private const uint ExecutionSpeed = 0x1;
    private const uint IgnoreTimerResolution = 0x4;
    private static int _applied;

    public static void KeepResponsive()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299) || Interlocked.Exchange(ref _applied, 1) == 1)
        {
            return;
        }

        // A control bit with its state bit cleared opts out of that kind of throttling.
        var state = new PowerThrottlingState
        {
            Version = 1,
            ControlMask = ExecutionSpeed | IgnoreTimerResolution,
            StateMask = 0,
        };

        try
        {
            SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf<PowerThrottlingState>());
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(nint process, int informationClass, ref PowerThrottlingState information, uint size);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();
}
