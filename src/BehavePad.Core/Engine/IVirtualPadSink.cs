using BehavePad.Core.Input;

namespace BehavePad.Core.Engine;

public sealed class RumbleEventArgs(double lowFrequency, double highFrequency) : EventArgs
{
    /// <summary>Strength of the heavy motor, from 0 to 1.</summary>
    public double LowFrequency { get; } = lowFrequency;

    /// <summary>Strength of the light motor, from 0 to 1.</summary>
    public double HighFrequency { get; } = highFrequency;
}

/// <summary>A virtual controller that games read instead of the physical one.</summary>
public interface IVirtualPadSink : IDisposable
{
    /// <summary>Raised when a game asks the virtual controller to rumble.</summary>
    event EventHandler<RumbleEventArgs>? RumbleRequested;

    void Submit(in GamepadState state);
}
