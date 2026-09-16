using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text.Json;
using BehavePad.Core.Setup;
using BehavePad.Core.Storage;
using Microsoft.Win32;
using Nefarius.Drivers.HidHide;
using Nefarius.Drivers.HidHide.Exceptions;
using Nefarius.Utilities.DeviceManagement.Extensions;
using Nefarius.Utilities.DeviceManagement.PnP;

namespace BehavePad.Services;

/// <summary>What BehavePad changed in HidHide, so it can undo exactly that and nothing else.</summary>
public sealed record HideState(IReadOnlyList<string> AddedInstanceIds, bool ActivatedCloak, DateTimeOffset HiddenAt)
{
    /// <summary>True when HidHide is loaded on the controller, so blocking it has an effect.</summary>
    public bool FilterActive { get; init; } = true;

    /// <summary>True when BehavePad reconnected the controller so HidHide could attach to it.</summary>
    public bool Reconnected { get; init; }

    /// <summary>Why the controller could not be restarted, when that is the reason games may still be reading it.</summary>
    public string? ReconnectProblem { get; init; }

    /// <summary>True when the user should unplug the controller and plug it back in, so anything holding it lets go.</summary>
    public bool ReplugRecommended { get; init; }
}

public enum HideResultKind
{
    Done,
    NotInstalled,
    Cancelled,
    Failed,
}

public sealed record HideOutcome(
    HideResultKind Kind,
    string? Message = null,
    int DeviceCount = 0,
    bool FilterActive = true,
    bool Reconnected = false,
    string? ReconnectProblem = null,
    bool ReplugRecommended = false);

internal sealed record HelperRequest(string ApplicationPath, IReadOnlyList<string> InstanceIds, HideState? Restore, bool AllowRestart = false);

internal sealed record HelperResult(bool Success, string? Error, HideState? State);

/// <summary>
/// Hides physical controllers from games through HidHide while BehavePad is allowed to keep reading them.
/// HidHide only accepts changes from administrators, so a normal BehavePad asks Windows for permission once
/// and runs a tiny elevated copy of itself to make the change.
/// </summary>
public sealed class HidHideManager
{
    public const string HideArgument = "--hidhide-hide";
    public const string RestoreArgument = "--hidhide-restore";

    private const string HidHideEnumKey = @"SYSTEM\CurrentControlSet\Services\HidHide\Enum";
    /// <summary>A USB port power-cycle takes longer to come back than restarting a device node did.</summary>
    private static readonly TimeSpan ReconnectTimeout = TimeSpan.FromSeconds(20);

    private readonly JsonFileStore<HideState> _stateStore = new(AppPaths.HiddenDevicesPath);

    /// <summary>True when an earlier session hid controllers and never put them back, for example after a crash.</summary>
    public bool HasPendingRestore => _stateStore.Load() is { } state && (state.AddedInstanceIds.Count > 0 || state.ActivatedCloak);

    /// <param name="allowRestart">
    /// The user's opt-in for restarting the controller so anything already holding it has to let go. Off by default,
    /// because on some controllers the restart brings the device back without the half that anything can read.
    /// </param>
    public async Task<HideOutcome> HideAsync(IReadOnlyList<ControllerDevice> devices, bool allowRestart)
    {
        if (!DriverStatus.CheckHidHide().Ready)
        {
            return new HideOutcome(HideResultKind.NotInstalled);
        }

        if (devices.Count == 0)
        {
            return new HideOutcome(HideResultKind.Failed, "No physical controller was found to hide.");
        }

        var request = new HelperRequest(CurrentExecutable, devices.Select(d => d.InstanceId).ToList(), null, allowRestart);
        var result = await RunAsync(request, HideArgument);
        if (result.Kind != HideResultKind.Done || result.State is null)
        {
            return new HideOutcome(result.Kind, result.Message, devices.Count);
        }

        // Merge with anything still waiting to be restored from before.
        var previous = _stateStore.Load();
        var merged = previous is null
            ? result.State
            : result.State with
            {
                AddedInstanceIds = previous.AddedInstanceIds.Union(result.State.AddedInstanceIds, StringComparer.OrdinalIgnoreCase).ToList(),
                ActivatedCloak = previous.ActivatedCloak || result.State.ActivatedCloak,
            };
        _stateStore.Save(merged);

        return new HideOutcome(
            HideResultKind.Done,
            null,
            devices.Count,
            result.State.FilterActive,
            result.State.Reconnected,
            result.State.ReconnectProblem,
            result.State.ReplugRecommended);
    }

    public async Task<HideOutcome> RestoreAsync()
    {
        var state = _stateStore.Load();
        if (state is null)
        {
            return new HideOutcome(HideResultKind.Done);
        }

        if (!DriverStatus.CheckHidHide().Installed)
        {
            _stateStore.Delete();
            return new HideOutcome(HideResultKind.NotInstalled);
        }

        var result = await RunAsync(new HelperRequest(CurrentExecutable, [], state), RestoreArgument);
        if (result.Kind == HideResultKind.Done)
        {
            _stateStore.Delete();
        }

        return new HideOutcome(result.Kind, result.Message, state.AddedInstanceIds.Count);
    }

    /// <summary>Entry point for the elevated helper process. Returns the process exit code.</summary>
    public static int RunHelper(string[] args)
    {
        if (args.Length < 3)
        {
            return 2;
        }

        HelperResult result;
        try
        {
            var request = JsonSerializer.Deserialize<HelperRequest>(File.ReadAllText(args[1]), BehavePadJson.Options)
                          ?? throw new InvalidDataException("Empty helper request.");
            result = args[0] == HideArgument
                ? new HelperResult(true, null, ApplyHide(request.ApplicationPath, request.InstanceIds, request.AllowRestart))
                : Restore(request);
        }
        catch (Exception ex)
        {
            result = new HelperResult(false, ex.Message, null);
        }

        File.WriteAllText(args[2], JsonSerializer.Serialize(result, BehavePadJson.Options));
        return result.Success ? 0 : 1;
    }

    /// <summary>
    /// Device nodes HidHide is currently loaded on. HidHide can only hide a device from this list, and it only
    /// attaches to devices that were connected after HidHide was installed.
    /// </summary>
    internal static HashSet<string> FilteredDeviceNodes()
    {
        var nodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(HidHideEnumKey);
            if (key is null)
            {
                return nodes;
            }

            foreach (var name in key.GetValueNames())
            {
                if (int.TryParse(name, out _) && key.GetValue(name) is string instanceId)
                {
                    nodes.Add(instanceId);
                }
            }
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            Trace.TraceWarning($"Could not read HidHide device list: {ex.Message}");
        }

        return nodes;
    }

    private static string CurrentExecutable => Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName;

    private static HelperResult Restore(HelperRequest request)
    {
        if (request.Restore is not null)
        {
            ApplyRestore(request.Restore);
        }

        return new HelperResult(true, null, null);
    }

    private static HideState ApplyHide(string applicationPath, IReadOnlyList<string> instanceIds, bool allowRestart)
    {
        var service = new HidHideControlService();
        if (service.IsAppListInverted)
        {
            throw new InvalidOperationException("HidHide is set to an inverted application list. Turn that off in HidHide Configuration Client first.");
        }

        // Only restarting the controller needs administrator rights, so ask for them before changing anything, and
        // only when a restart is actually going to happen. HidHide itself accepts changes from a normal account on
        // most PCs, so the default path asks for nothing.
        if (HidePlan.ShouldRestart(NeedsReconnect(service, instanceIds), allowRestart) && !IsElevated)
        {
            throw new UnauthorizedAccessException("Restarting the controller needs administrator rights.");
        }

        if (!service.ApplicationPaths.Contains(applicationPath, StringComparer.OrdinalIgnoreCase))
        {
            service.AddApplicationPath(applicationPath, false);
        }

        var added = Block(service, instanceIds);

        var activated = false;
        if (!service.IsActive)
        {
            service.IsActive = true;
            activated = true;
        }

        var filterActive = IsFilterLoaded(instanceIds);

        // HidHide only turns away new opens, so whatever already holds the controller keeps reading it: a game that
        // started first, or GameInputSvc, which runs from boot. Only restarting the controller makes them let go and
        // ask again under the cloak, and that is the user's call, because on some controllers the device comes back
        // without the half that games and BehavePad read. Unasked, BehavePad says to unplug it instead.
        var changed = !filterActive || activated || added.Count > 0;
        bool? restartSucceeded = null;
        var controllerReturned = false;
        string? reconnectProblem = null;

        if (HidePlan.ShouldRestart(changed, allowRestart))
        {
            reconnectProblem = Reconnect(instanceIds);
            restartSucceeded = reconnectProblem is null;
            if (restartSucceeded == true)
            {
                var currentIds = WaitForFilter(out filterActive, out controllerReturned);
                if (!controllerReturned)
                {
                    reconnectProblem = "your controller did not come back after BehavePad restarted it";
                }

                // Reconnecting can give device nodes new IDs, so block whatever the controller came back as.
                added.AddRange(Block(service, currentIds));
            }
        }

        var followUp = HidePlan.Decide(changed, allowRestart, restartSucceeded, controllerReturned);
        return new HideState(added, activated, DateTimeOffset.Now)
        {
            FilterActive = filterActive,
            Reconnected = followUp == HideFollowUp.Restarted,
            ReplugRecommended = followUp == HideFollowUp.ReplugRecommended,
            ReconnectProblem = followUp == HideFollowUp.RestartFailed
                ? reconnectProblem ?? "BehavePad could not restart your controller"
                : null,
        };
    }

    /// <summary>
    /// True when something still has to be restarted before hiding takes hold: HidHide is not loaded on the
    /// controller, the cloak is off, or one of its device nodes is not blocked yet.
    /// </summary>
    private static bool NeedsReconnect(HidHideControlService service, IReadOnlyList<string> instanceIds)
    {
        if (!service.IsActive || !IsFilterLoaded(instanceIds))
        {
            return true;
        }

        var blocked = service.BlockedInstanceIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return instanceIds.Any(id => !blocked.Contains(id));
    }

    private static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    private static void ApplyRestore(HideState state)
    {
        var service = new HidHideControlService();
        foreach (var id in state.AddedInstanceIds)
        {
            service.RemoveBlockedInstanceId(id);
        }

        // BehavePad stays on the allow list. That is harmless and saves a permission prompt next time.
        if (state.ActivatedCloak && !service.BlockedInstanceIds.Any())
        {
            service.IsActive = false;
        }
    }

    private static List<string> Block(HidHideControlService service, IEnumerable<string> instanceIds)
    {
        var blocked = service.BlockedInstanceIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = new List<string>();
        foreach (var id in instanceIds)
        {
            if (blocked.Add(id))
            {
                service.AddBlockedInstanceId(id);
                added.Add(id);
            }
        }

        return added;
    }

    private static bool IsFilterLoaded(IEnumerable<string> instanceIds)
    {
        var filtered = FilteredDeviceNodes();
        return instanceIds.Any(filtered.Contains);
    }

    /// <summary>
    /// Restarts the top device node of each controller, so everything holding it has to let go and open it again
    /// under the cloak. Needs administrator rights. Returns null when done, or why it could not be.
    /// </summary>
    private static string? Reconnect(IReadOnlyList<string> instanceIds)
    {
        var ids = new HashSet<string>(instanceIds, StringComparer.OrdinalIgnoreCase);
        var restarted = false;
        string? problem = null;

        foreach (var id in instanceIds)
        {
            PnPDevice device;
            try
            {
                device = PnPDevice.GetDeviceByInstanceId(id, DeviceLocationFlags.Normal);
                if (device.Parent?.InstanceId is { } parent && ids.Contains(parent))
                {
                    continue;
                }
            }
            catch (Exception ex)
            {
                problem ??= ex.Message;
                Trace.TraceWarning($"Could not find {id}: {ex.Message}");
                continue;
            }

            if (TryRestart(device, out var restartProblem))
            {
                restarted = true;
            }
            else
            {
                problem ??= restartProblem;
            }
        }

        return restarted ? null : problem ?? "BehavePad could not restart the controller.";
    }

    /// <summary>
    /// Power-cycles the USB port the controller sits on, which is the same thing as unplugging it: the whole stack
    /// comes back, including the XInput node that games and BehavePad read.
    /// </summary>
    /// <remarks>
    /// Removing and re-adding the device node looks equivalent and is not. On an Xbox Series pad it can bring the
    /// composite back with no children at all, leaving a controller that Windows calls healthy and nothing can read.
    /// That path is kept only for a controller that is not on a USB port, where there is no port to cycle.
    /// </remarks>
    private static bool TryRestart(PnPDevice device, out string? problem)
    {
        problem = null;
        try
        {
            device.ToUsbPnPDevice().CyclePort();
            return true;
        }
        catch (Exception cycleError)
        {
            Trace.TraceWarning($"Could not power-cycle {device.InstanceId}: {cycleError.Message}");
        }

        try
        {
            device.RemoveAndSetup();
            return true;
        }
        catch (Exception removeError)
        {
            problem = removeError.Message;
            Trace.TraceWarning($"Could not restart {device.InstanceId}: {removeError.Message}");
            return false;
        }
    }

    /// <summary>
    /// Waits for the controller to come back after a restart, and for HidHide to load on it.
    /// </summary>
    /// <param name="controllerReturned">
    /// False when the controller never came back at all. A device node can return without the XInput child games
    /// read, which leaves a controller that looks healthy in Windows and answers nobody, so it is worth telling
    /// the user about instead of reporting it as a quiet failure to hide.
    /// </param>
    private static IReadOnlyList<string> WaitForFilter(out bool filterActive, out bool controllerReturned)
    {
        var stopwatch = Stopwatch.StartNew();
        IReadOnlyList<string> currentIds = [];
        filterActive = false;
        controllerReturned = false;
        while (stopwatch.Elapsed < ReconnectTimeout)
        {
            Thread.Sleep(500);
            currentIds = ControllerDevices.FindPhysicalControllerNodes().Select(d => d.InstanceId).ToList();
            if (currentIds.Count == 0)
            {
                continue;
            }

            controllerReturned = true;
            if (IsFilterLoaded(currentIds))
            {
                filterActive = true;
                break;
            }
        }

        return currentIds;
    }

    private static async Task<(HideResultKind Kind, string? Message, HideState? State)> RunAsync(HelperRequest request, string argument)
    {
        // Try directly first: this works when BehavePad already runs as administrator.
        try
        {
            var state = await Task.Run(() =>
            {
                if (argument == HideArgument)
                {
                    return ApplyHide(request.ApplicationPath, request.InstanceIds, request.AllowRestart);
                }

                if (request.Restore is not null)
                {
                    ApplyRestore(request.Restore);
                }

                return null;
            });

            return (HideResultKind.Done, null, state);
        }
        catch (Exception ex) when (ex is HidHideDriverAccessFailedException or UnauthorizedAccessException or HidHideHandleInvalidException)
        {
            // Not elevated. Fall through to the helper.
        }
        catch (Exception ex)
        {
            return (HideResultKind.Failed, ex.Message, null);
        }

        var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "BehavePad")).FullName;
        var requestPath = Path.Combine(folder, $"request-{Guid.NewGuid():N}.json");
        var resultPath = Path.Combine(folder, $"result-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request, BehavePadJson.Options));

        try
        {
            using var process = Process.Start(new ProcessStartInfo(CurrentExecutable)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = $"{argument} \"{requestPath}\" \"{resultPath}\"",
                WindowStyle = ProcessWindowStyle.Hidden,
            });

            if (process is null)
            {
                return (HideResultKind.Failed, "Windows did not start the permission helper.", null);
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await process.WaitForExitAsync(timeout.Token);

            if (!File.Exists(resultPath))
            {
                return (HideResultKind.Failed, "The permission helper finished without a result.", null);
            }

            var result = JsonSerializer.Deserialize<HelperResult>(await File.ReadAllTextAsync(resultPath), BehavePadJson.Options);
            return result is { Success: true }
                ? (HideResultKind.Done, null, result.State)
                : (HideResultKind.Failed, result?.Error ?? "HidHide rejected the change.", null);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return (HideResultKind.Cancelled, "Permission was declined, so games can still see the original controller.", null);
        }
        catch (OperationCanceledException)
        {
            return (HideResultKind.Failed, "The permission helper took too long.", null);
        }
        finally
        {
            TryDelete(requestPath);
            TryDelete(resultPath);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
        }
    }
}
