namespace BehavePad.Core.Input;

/// <summary>Digital buttons on an XInput controller. Values match the XINPUT_GAMEPAD bit flags.</summary>
[Flags]
public enum GamepadButtons : ushort
{
    None = 0,
    DPadUp = 0x0001,
    DPadDown = 0x0002,
    DPadLeft = 0x0004,
    DPadRight = 0x0008,
    Start = 0x0010,
    Back = 0x0020,
    LeftThumb = 0x0040,
    RightThumb = 0x0080,
    LeftShoulder = 0x0100,
    RightShoulder = 0x0200,
    Guide = 0x0400,
    A = 0x1000,
    B = 0x2000,
    X = 0x4000,
    Y = 0x8000,
}

public static class GamepadButtonsExtensions
{
    /// <summary>Every defined button bit.</summary>
    public const GamepadButtons All = (GamepadButtons)0xF7FF;

    private static readonly GamepadButtons[] Singles =
    [
        GamepadButtons.A, GamepadButtons.B, GamepadButtons.X, GamepadButtons.Y,
        GamepadButtons.LeftShoulder, GamepadButtons.RightShoulder,
        GamepadButtons.Back, GamepadButtons.Start, GamepadButtons.Guide,
        GamepadButtons.LeftThumb, GamepadButtons.RightThumb,
        GamepadButtons.DPadUp, GamepadButtons.DPadDown, GamepadButtons.DPadLeft, GamepadButtons.DPadRight,
    ];

    public static IReadOnlyList<GamepadButtons> AllSingles => Singles;

    public static IEnumerable<GamepadButtons> Each(this GamepadButtons buttons)
    {
        foreach (var button in Singles)
        {
            if ((buttons & button) != 0)
            {
                yield return button;
            }
        }
    }

    public static string DisplayName(this GamepadButtons button) => button switch
    {
        GamepadButtons.DPadUp => "D-pad up",
        GamepadButtons.DPadDown => "D-pad down",
        GamepadButtons.DPadLeft => "D-pad left",
        GamepadButtons.DPadRight => "D-pad right",
        GamepadButtons.Start => "Menu",
        GamepadButtons.Back => "View",
        GamepadButtons.LeftThumb => "Left stick click",
        GamepadButtons.RightThumb => "Right stick click",
        GamepadButtons.LeftShoulder => "LB",
        GamepadButtons.RightShoulder => "RB",
        GamepadButtons.Guide => "Guide",
        _ => button.ToString(),
    };
}
