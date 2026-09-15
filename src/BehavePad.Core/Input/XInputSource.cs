using System.Runtime.InteropServices;

namespace BehavePad.Core.Input;

/// <summary>Reads Xbox-compatible controllers through the Windows XInput runtime.</summary>
public sealed unsafe class XInputSource : IGamepadSource
{
    public const int MaxSlots = 4;
    private const uint ErrorSuccess = 0;

    // Ordinal 100 is XInputGetStateEx, which also reports the Guide button.
    private const nint GetStateExOrdinal = 100;

    private readonly delegate* unmanaged[Stdcall]<uint, XInputState*, uint> _getState;
    private readonly delegate* unmanaged[Stdcall]<uint, XInputVibration*, uint> _setState;
    private readonly delegate* unmanaged[Stdcall]<uint, byte, XInputBatteryInformation*, uint> _getBattery;

    public XInputSource()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("XInput is only available on Windows.");
        }

        nint library = 0;
        foreach (var name in new[] { "xinput1_4.dll", "xinput1_3.dll", "xinput9_1_0.dll" })
        {
            if (NativeLibrary.TryLoad(name, out library))
            {
                RuntimeName = name;
                break;
            }
        }

        if (library == 0)
        {
            throw new DllNotFoundException("No XInput runtime was found on this PC.");
        }

        var getStateEx = GetProcAddress(library, GetStateExOrdinal);
        if (getStateEx != 0)
        {
            _getState = (delegate* unmanaged[Stdcall]<uint, XInputState*, uint>)getStateEx;
            SupportsGuideButton = true;
        }
        else
        {
            _getState = (delegate* unmanaged[Stdcall]<uint, XInputState*, uint>)NativeLibrary.GetExport(library, "XInputGetState");
        }

        _setState = (delegate* unmanaged[Stdcall]<uint, XInputVibration*, uint>)NativeLibrary.GetExport(library, "XInputSetState");

        if (NativeLibrary.TryGetExport(library, "XInputGetBatteryInformation", out var battery))
        {
            _getBattery = (delegate* unmanaged[Stdcall]<uint, byte, XInputBatteryInformation*, uint>)battery;
        }
    }

    public string RuntimeName { get; } = "";

    public bool SupportsGuideButton { get; }

    public string DisplayName => "Xbox controller";

    public bool IsSimulated => false;

    public bool TryRead(int slot, out GamepadReading reading)
    {
        reading = default;
        if ((uint)slot >= MaxSlots)
        {
            return false;
        }

        XInputState state = default;
        if (_getState((uint)slot, &state) != ErrorSuccess)
        {
            return false;
        }

        var buttons = (GamepadButtons)state.Buttons & GamepadButtonsExtensions.All;
        if (!SupportsGuideButton)
        {
            buttons &= ~GamepadButtons.Guide;
        }

        reading = new GamepadReading(
            new GamepadState(state.ThumbLX, state.ThumbLY, state.ThumbRX, state.ThumbRY, state.LeftTrigger, state.RightTrigger, buttons),
            state.PacketNumber);
        return true;
    }

    public void SetVibration(int slot, double lowFrequency, double highFrequency)
    {
        if ((uint)slot >= MaxSlots)
        {
            return;
        }

        var vibration = new XInputVibration
        {
            LeftMotorSpeed = (ushort)Math.Round(Math.Clamp(lowFrequency, 0, 1) * ushort.MaxValue),
            RightMotorSpeed = (ushort)Math.Round(Math.Clamp(highFrequency, 0, 1) * ushort.MaxValue),
        };
        _setState((uint)slot, &vibration);
    }

    public BatteryStatus? GetBattery(int slot)
    {
        if (_getBattery == null || (uint)slot >= MaxSlots)
        {
            return null;
        }

        XInputBatteryInformation info = default;
        if (_getBattery((uint)slot, 0, &info) != ErrorSuccess || info.BatteryType == 0)
        {
            return null;
        }

        var kind = info.BatteryType switch
        {
            1 => BatteryKind.Wired,
            2 => BatteryKind.Alkaline,
            3 => BatteryKind.NiMH,
            _ => BatteryKind.Unknown,
        };
        return new BatteryStatus(kind, (BatteryLevel)Math.Clamp((int)info.BatteryLevel, 0, 3));
    }

    [DllImport("kernel32.dll", EntryPoint = "GetProcAddress", ExactSpelling = true)]
    private static extern nint GetProcAddress(nint module, nint ordinal);

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX;
        public short ThumbLY;
        public short ThumbRX;
        public short ThumbRY;

        // XInputGetStateEx can write past the documented structure, so leave room.
        public uint Reserved0;
        public uint Reserved1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputVibration
    {
        public ushort LeftMotorSpeed;
        public ushort RightMotorSpeed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputBatteryInformation
    {
        public byte BatteryType;
        public byte BatteryLevel;
    }
}
