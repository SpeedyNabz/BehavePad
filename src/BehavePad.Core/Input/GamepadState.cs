namespace BehavePad.Core.Input;

public enum StickSide
{
    Left,
    Right,
}

public enum TriggerSide
{
    Left,
    Right,
}

/// <summary>One immutable snapshot of every input on an XInput controller, in raw XInput units.</summary>
public readonly record struct GamepadState(
    short LeftX,
    short LeftY,
    short RightX,
    short RightY,
    byte LeftTrigger,
    byte RightTrigger,
    GamepadButtons Buttons)
{
    public StickPoint LeftStick => StickPoint.FromRaw(LeftX, LeftY);

    public StickPoint RightStick => StickPoint.FromRaw(RightX, RightY);

    public StickPoint Stick(StickSide side) => side == StickSide.Left ? LeftStick : RightStick;

    public byte Trigger(TriggerSide side) => side == TriggerSide.Left ? LeftTrigger : RightTrigger;

    public GamepadState WithStick(StickSide side, StickPoint point) => side == StickSide.Left
        ? this with { LeftX = StickPoint.ToRaw(point.X), LeftY = StickPoint.ToRaw(point.Y) }
        : this with { RightX = StickPoint.ToRaw(point.X), RightY = StickPoint.ToRaw(point.Y) };

    public GamepadState WithTrigger(TriggerSide side, byte value) => side == TriggerSide.Left
        ? this with { LeftTrigger = value }
        : this with { RightTrigger = value };
}
