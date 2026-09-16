namespace BehavePad.Core.Setup;

/// <summary>What still has to happen after BehavePad blocks a controller in HidHide.</summary>
public enum HideFollowUp
{
    /// <summary>Nothing changed this time, so nothing can be holding the controller open from before.</summary>
    None,

    /// <summary>The user should unplug the controller and plug it back in, so anything holding it lets go.</summary>
    ReplugRecommended,

    /// <summary>BehavePad restarted the controller itself and it came back.</summary>
    Restarted,

    /// <summary>BehavePad tried to restart the controller and it did not come back.</summary>
    RestartFailed,
}

/// <summary>
/// Decides what BehavePad does, and tells the user, once a controller is blocked in HidHide.
/// </summary>
/// <remarks>
/// HidHide only turns away new opens. Anything already holding the controller, such as a game that started first
/// or a Windows service that runs from boot, keeps reading it until it lets go and asks again. Restarting the
/// device node forces that, but on some controllers the device comes back without the half that games and
/// BehavePad read, and nothing can use it until it is physically unplugged. That is why BehavePad asks the user
/// to replug by default and only restarts the controller itself when they have opted in.
/// </remarks>
public static class HidePlan
{
    /// <param name="changed">True when this run turned the cloak on, blocked a device, or found HidHide not loaded on one.</param>
    /// <param name="allowRestart">Whether the user has opted in to BehavePad restarting the controller.</param>
    public static bool ShouldRestart(bool changed, bool allowRestart) => changed && allowRestart;

    /// <param name="changed">True when this run turned the cloak on, blocked a device, or found HidHide not loaded on one.</param>
    /// <param name="allowRestart">Whether the user has opted in to BehavePad restarting the controller.</param>
    /// <param name="restartSucceeded">Null when no restart was attempted at all.</param>
    /// <param name="controllerReturned">Whether the controller could be read again afterwards.</param>
    public static HideFollowUp Decide(bool changed, bool allowRestart, bool? restartSucceeded, bool controllerReturned)
    {
        if (!changed)
        {
            return HideFollowUp.None;
        }

        // Either the user never opted in, or the restart never ran. Both leave the same job for the user to do.
        if (!allowRestart || restartSucceeded is null)
        {
            return HideFollowUp.ReplugRecommended;
        }

        return restartSucceeded == true && controllerReturned
            ? HideFollowUp.Restarted
            : HideFollowUp.RestartFailed;
    }
}
