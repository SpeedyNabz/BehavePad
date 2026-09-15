using BehavePad.Core.Engine;
using BehavePad.Core.Input;

namespace BehavePad.Core.Tests;

public class VirtualSlotLocatorTests
{
    /// <summary>
    /// Slot 0 is a real controller resting off center. The virtual controller sits in another slot and
    /// reports whatever was last submitted, after a short delay like the real driver.
    /// </summary>
    private sealed class FakeXInput(int virtualSlot, int readsBeforeUpdate) : IGamepadSource
    {
        private static readonly GamepadState RealController = new(917, -222, 633, -2833, 0, 0, GamepadButtons.None);
        private GamepadState _submitted;
        private int _readsSinceSubmit;

        public List<GamepadState> Submitted { get; } = [];

        public string DisplayName => "Fake XInput";

        public bool IsSimulated => true;

        public void Submit(GamepadState state)
        {
            _submitted = state;
            _readsSinceSubmit = 0;
            Submitted.Add(state);
        }

        public bool TryRead(int slot, out GamepadReading reading)
        {
            reading = default;
            if (slot == 0)
            {
                reading = new GamepadReading(RealController, 1);
                return true;
            }

            if (slot != virtualSlot)
            {
                return false;
            }

            _readsSinceSubmit++;
            reading = new GamepadReading(_readsSinceSubmit > readsBeforeUpdate ? _submitted : default, 2);
            return true;
        }

        public void SetVibration(int slot, double lowFrequency, double highFrequency)
        {
        }

        public BatteryStatus? GetBattery(int slot) => null;
    }

    [Fact]
    public async Task Finds_the_slot_that_reports_the_signature()
    {
        var xinput = new FakeXInput(virtualSlot: 2, readsBeforeUpdate: 3);

        var slot = await VirtualSlotLocator.FindAsync(xinput, xinput.Submit, TimeSpan.FromSeconds(2));

        Assert.Equal(2, slot);
    }

    [Fact]
    public async Task Returns_nothing_when_no_slot_reports_the_signature()
    {
        var xinput = new FakeXInput(virtualSlot: -1, readsBeforeUpdate: 0);

        var slot = await VirtualSlotLocator.FindAsync(xinput, xinput.Submit, TimeSpan.FromMilliseconds(150));

        Assert.Null(slot);
    }

    [Fact]
    public async Task Leaves_the_virtual_controller_neutral_afterwards()
    {
        var xinput = new FakeXInput(virtualSlot: 1, readsBeforeUpdate: 0);

        await VirtualSlotLocator.FindAsync(xinput, xinput.Submit, TimeSpan.FromSeconds(1));

        Assert.Equal(VirtualSlotLocator.Signature, xinput.Submitted[0]);
        Assert.Equal(default, xinput.Submitted[^1]);
    }

    [Fact]
    public void Signature_stays_inside_typical_game_deadzones()
    {
        var signature = VirtualSlotLocator.Signature;

        Assert.True(signature.LeftStick.Magnitude < 0.07);
        Assert.True(signature.RightStick.Magnitude < 0.07);
        Assert.True(signature.RightTrigger < 10);
        Assert.Equal(GamepadButtons.None, signature.Buttons);
    }
}
