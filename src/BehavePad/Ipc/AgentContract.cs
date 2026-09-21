using System.Text.Json;
using BehavePad.Core.Analysis;
using BehavePad.Core.Engine;
using BehavePad.Core.Filtering;
using BehavePad.Core.Input;
using BehavePad.Core.Storage;
using BehavePad.Services;
using BehavePad.ViewModels;

namespace BehavePad.Ipc;

/// <summary>
/// What the background agent and the window say to each other. The agent owns the controller, the filter
/// and the drivers; the window is a client that shows what the agent reports and asks it to do things.
/// </summary>
public static class AgentContract
{
    /// <summary>Per-user, so two signed-in people each get their own agent.</summary>
    public static string PipeName => $"BehavePad.Agent.{Environment.UserName}";

    /// <summary>Set on the agent process so a second one never starts.</summary>
    public static string AgentMutexName => @"Local\BehavePad.Agent";

    public const string AgentArgument = "--agent";

    /// <summary>
    /// One message per line, so it must never be indented: the stored-file options are pretty-printed and
    /// a newline inside a payload would split the message in two.
    /// </summary>
    public static JsonSerializerOptions Json { get; } = new(BehavePadJson.Options) { WriteIndented = false };
}

/// <summary>What the window asks the agent to do.</summary>
public enum AgentCommand
{
    /// <summary>Sent on connect. The agent replies with a full state snapshot.</summary>
    Hello,
    StartFilter,
    StopFilter,
    ToggleFilter,
    RefreshDrivers,
    InstallDrivers,
    RestoreVisibility,
    DismissMessage,
    UpdateSettings,
    SaveProfile,
    SaveTest,
    ResetProfile,
    ForgetLearned,
    CheckForUpdates,
    InstallUpdate,
    SetDemoScenario,
    BeginTest,
    CancelTest,
    SkipSnapBack,
    /// <summary>Stop the agent as well as the window, so nothing keeps filtering in the background.</summary>
    ExitAgent,
}

/// <summary>What the agent tells the window.</summary>
public enum AgentEvent
{
    /// <summary>Everything that is not per-frame. Sent on connect and whenever any of it changes.</summary>
    State,

    /// <summary>One poll of the pipeline, sent at about sixty a second while a window is attached.</summary>
    Frame,

    /// <summary>Drift test progress, sent while a test runs.</summary>
    Test,

    /// <summary>Asks the window to come to the front, because the tray icon was clicked.</summary>
    Activate,
}

/// <summary>One line on the pipe. <see cref="Payload"/> is the JSON of whatever the kind calls for.</summary>
public sealed record IpcEnvelope(string Kind, string? Payload)
{
    public static IpcEnvelope For(AgentCommand command, object? payload = null) =>
        new(command.ToString(), payload is null ? null : JsonSerializer.Serialize(payload, AgentContract.Json));

    public static IpcEnvelope For(AgentEvent notification, object? payload = null) =>
        new(notification.ToString(), payload is null ? null : JsonSerializer.Serialize(payload, AgentContract.Json));

    public T? Read<T>() => Payload is null ? default : JsonSerializer.Deserialize<T>(Payload, AgentContract.Json);
}

/// <summary>Everything the window shows that does not change every frame.</summary>
public sealed record AgentState
{
    // Controller
    public bool Connected { get; init; }

    public int Slot { get; init; } = -1;

    public bool IsDemo { get; init; }

    public string ControllerName { get; init; } = "Xbox controller";

    public string ControllerStatusText { get; init; } = "Looking for a controller";

    public string BatteryText { get; init; } = "";

    public bool HasXInput { get; init; } = true;

    // Filter
    public FilterState FilterState { get; init; }

    public bool PhysicalHidden { get; init; }

    public int? VirtualSlot { get; init; }

    public string? Message { get; init; }

    public bool MessageIsError { get; init; }

    public bool IsArmed { get; init; }

    public bool HasPendingRestore { get; init; }

    // Drivers
    public DriverInfo Vigem { get; init; } = new(false, null, null);

    public DriverInfo HidHide { get; init; } = new(false, null, null);

    public string? DriverStatus { get; init; }

    public bool DriverStatusIsError { get; init; }

    public bool DriverBusy { get; init; }

    public double DriverProgress { get; init; }

    // Stored data
    public AppSettings Settings { get; init; } = new();

    public FilterProfile? Profile { get; init; }

    public DriftReport? Report { get; init; }

    // Updates
    public UpdateState UpdateState { get; init; }

    public string? UpdateStatus { get; init; }

    public bool UpdateStatusIsError { get; init; }

    public double UpdateProgress { get; init; }

    public string? StagedVersion { get; init; }

    /// <summary>True when the agent runs elevated, so hiding never has to ask Windows for permission.</summary>
    public bool IsElevated { get; init; }
}

/// <summary>How far the drift test has got. The agent runs the recorders so they still see every poll.</summary>
public sealed record TestSnapshot
{
    public TestStep Step { get; init; }

    public int Countdown { get; init; }

    public double RestProgress { get; init; }

    public int RestRemaining { get; init; }

    public int PhantomPresses { get; init; }

    public int LeftReleases { get; init; }

    public int RightReleases { get; init; }

    public string LeftHint { get; init; } = "";

    public string RightHint { get; init; } = "";

    public string? Notice { get; init; }

    public bool SnapSkipped { get; init; }

    public double LeftZoom { get; init; } = 1;

    public double RightZoom { get; init; } = 1;

    public double OutlineMargin { get; init; }

    public IReadOnlyList<StickPoint> LeftCloud { get; init; } = [];

    public IReadOnlyList<StickPoint> RightCloud { get; init; } = [];

    public IReadOnlyList<StickPoint> LeftOutline { get; init; } = [];

    public IReadOnlyList<StickPoint> RightOutline { get; init; } = [];

    public IReadOnlyList<StickPoint> LeftSettles { get; init; } = [];

    public IReadOnlyList<StickPoint> RightSettles { get; init; } = [];

    /// <summary>Set once the test finishes, so the window can show the results and build a filter.</summary>
    public DriftReport? Report { get; init; }
}

/// <summary>Payload for <see cref="AgentCommand.BeginTest"/>.</summary>
public sealed record BeginTestRequest(RestLength Length);

/// <summary>Payload for <see cref="AgentCommand.SaveTest"/>.</summary>
public sealed record SaveTestRequest(DriftReport Report, FilterProfile Profile, bool TurnOn);

/// <summary>Payload for <see cref="AgentCommand.SetDemoScenario"/>.</summary>
public sealed record DemoScenarioRequest(DemoScenario Scenario);
