namespace BehavePad.Core.Input;

public readonly record struct GamepadReading(GamepadState State, uint PacketNumber);

public enum BatteryKind
{
    Unknown,
    Wired,
    Alkaline,
    NiMH,
}

public enum BatteryLevel
{
    Empty,
    Low,
    Medium,
    Full,
}

public readonly record struct BatteryStatus(BatteryKind Kind, BatteryLevel Level);

/// <summary>Anything that can be polled for controller state, such as XInput or the built-in demo controller.</summary>
public interface IGamepadSource
{
    string DisplayName { get; }

    bool IsSimulated { get; }

    bool TryRead(int slot, out GamepadReading reading);

    /// <summary>Sets rumble strength for each motor, from 0 to 1.</summary>
    void SetVibration(int slot, double lowFrequency, double highFrequency);

    BatteryStatus? GetBattery(int slot);
}
