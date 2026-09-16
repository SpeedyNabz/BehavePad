using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.Text.Json;
using BehavePad.Core.Storage;
using Microsoft.Win32;
using Nefarius.Drivers.HidHide;
using Nefarius.Drivers.HidHide.Exceptions;
using Nefarius.Utilities.DeviceManagement.PnP;

namespace BehavePad.Services;

/// <summary>What BehavePad changed in HidHide, so it can undo exactly that and nothing else.</summary>
public sealed record HideState(IReadOnlyList<string> AddedInstanceIds, bool ActivatedCloak, DateTimeOffset HiddenAt)
{
    /// <summary>True when HidHide is loaded on the controller, so blocking it has an effect.</summary>
    public bool FilterActive { get; init; } = true;

    /// <summary>True when BehavePad reconnected the controller so HidHide could attach to it.</summary>
    public bool Reconnected { get; init; }
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
    bool Reconnected = false);

internal sealed record HelperRequest(string ApplicationPath, IReadOnlyList<string> InstanceIds, HideState? Restore);

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
    private static readonly TimeSpan ReconnectTimeout = TimeSpan.FromSeconds(10);

    private readonly JsonFileStore<HideState> _stateStore = new(AppPaths.HiddenDevicesPath);

    /// <summary>True when an earlier session hid controllers and never put them back, for example after a crash.</summary>
    public bool HasPendingRestore => _stateStore.Load() is { } state && (state.AddedInstanceIds.Count > 0 || state.ActivatedCloak);

    public async Task<HideOutcome> HideAsync(IReadOnlyList<ControllerDevice> devices)
    {
        if (!DriverStatus.CheckHidHide().Ready)
        {
            return new HideOutcome(HideResultKind.NotInstalled);
        }

        if (devices.Count == 0)
        {
            return new HideOutcome(HideResultKind.Failed, "No physical controller was found to hide.");
        }

        var request = new HelperRequest(CurrentExecutable, devices.Select(d => d.InstanceId).ToList(), null);
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

        return new HideOutcome(HideResultKind.Done, null, devices.Count, result.State.FilterActive, result.State.Reconnected);
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
                ? new HelperResult(true, null, ApplyHide(request.ApplicationPath, request.InstanceIds))
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

    private static HideState ApplyHide(string applicationPath, IReadOnlyList<string> instanceIds)
    {
        var service = new HidHideControlService();
        if (service.IsAppListInverted)
        {
            throw new InvalidOperationException("HidHide is set to an inverted application list. Turn that off in HidHide Configuration Client first.");
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

        // HidHide only turns away new opens, so whatever already holds the controller keeps reading it: a game
        // that started first, or GameInputSvc, which runs from boot. Restarting the device node is the only way
        // to make them let go and ask again under the cloak, so do it whenever this run changed what HidHide
        // blocks, not only when the filter was missing altogether.
        var reconnected = false;
        if ((!filterActive || activated || added.Count > 0) && Reconnect(instanceIds))
        {
            reconnected = true;
            var currentIds = WaitForFilter(out filterActive);

            // Reconnecting can give device nodes new IDs, so block whatever the controller came back as.
            added.AddRange(Block(service, currentIds));
        }

        return new HideState(added, activated, DateTimeOffset.Now) { FilterActive = filterActive, Reconnected = reconnected };
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

    /// <summary>Restarts the top device node of each controller so HidHide loads on it. Needs administrator rights.</summary>
    private static bool Reconnect(IReadOnlyList<string> instanceIds)
    {
        var ids = new HashSet<string>(instanceIds, StringComparer.OrdinalIgnoreCase);
        var restarted = false;
        foreach (var id in instanceIds)
        {
            try
            {
                var device = PnPDevice.GetDeviceByInstanceId(id, DeviceLocationFlags.Normal);
                if (device.Parent?.InstanceId is { } parent && ids.Contains(parent))
                {
                    continue;
                }

                device.RemoveAndSetup();
                restarted = true;
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Could not reconnect {id}: {ex.Message}");
            }
        }

        return restarted;
    }

    private static IReadOnlyList<string> WaitForFilter(out bool filterActive)
    {
        var stopwatch = Stopwatch.StartNew();
        IReadOnlyList<string> currentIds = [];
        filterActive = false;
        while (stopwatch.Elapsed < ReconnectTimeout)
        {
            Thread.Sleep(500);
            currentIds = ControllerDevices.FindPhysicalControllerNodes().Select(d => d.InstanceId).ToList();
            if (currentIds.Count > 0 && IsFilterLoaded(currentIds))
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
                    return ApplyHide(request.ApplicationPath, request.InstanceIds);
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
