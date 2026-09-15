using System.Diagnostics;
using BehavePad.Core.Input;

namespace BehavePad.Core.Engine;

/// <summary>
/// Finds the XInput slot where a newly created virtual controller appears.
/// ViGEmBus reports its own player index, and that index does not match the XInput slot when an Xbox One or
/// Series controller is also connected. Trusting it made BehavePad read its own output instead of the real controller.
/// </summary>
public static class VirtualSlotLocator
{
    /// <summary>
    /// A report small enough to sit inside any game's deadzone, and specific enough that a real controller never matches it.
    /// </summary>
    public static readonly GamepadState Signature = new(1234, -1432, -1789, 1021, 0, 2, GamepadButtons.None);

    public static async Task<int?> FindAsync(
        IGamepadSource source,
        Action<GamepadState> submit,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(submit);

        submit(Signature);
        try
        {
            var stopwatch = Stopwatch.StartNew();
            do
            {
                for (var slot = 0; slot < XInputSource.MaxSlots; slot++)
                {
                    if (source.TryRead(slot, out var reading) && reading.State == Signature)
                    {
                        return slot;
                    }
                }

                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            }
            while (stopwatch.Elapsed < timeout);

            return null;
        }
        finally
        {
            submit(default);
        }
    }
}
