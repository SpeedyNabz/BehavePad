using Nefarius.Utilities.DeviceManagement.PnP;

namespace BehavePad.Services;

public sealed record ControllerDevice(string InstanceId, string Name);

/// <summary>Finds the Windows device nodes that belong to physical XInput controllers.</summary>
public static class ControllerDevices
{
    private const int MaxAncestorDepth = 3;

    /// <summary>
    /// Returns every device node HidHide must block so games stop seeing physical controllers.
    /// That is the XInput device itself plus its USB parents, such as the "Xbox Controller"
    /// composite device. Virtual controllers created by ViGEmBus are skipped.
    /// </summary>
    public static IReadOnlyList<ControllerDevice> FindPhysicalControllerNodes()
    {
        var nodes = new Dictionary<string, ControllerDevice>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < 64; index++)
        {
            PnPDevice device;
            try
            {
                if (!Devcon.FindByInterfaceGuid(DeviceInterfaceIds.XUsbDevice, out device, index, true))
                {
                    break;
                }
            }
            catch (Exception)
            {
                break;
            }

            try
            {
                if (IsVirtual(device))
                {
                    continue;
                }

                Add(nodes, device);
                IPnPDevice? ancestor = device.Parent;
                for (var depth = 0; ancestor is not null && depth < MaxAncestorDepth; depth++)
                {
                    if (!IsControllerAncestor(ancestor.InstanceId))
                    {
                        break;
                    }

                    Add(nodes, ancestor);
                    ancestor = ancestor.Parent;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning($"Skipping controller device: {ex.Message}");
            }
        }

        return nodes.Values.ToList();
    }

    private static bool IsControllerAncestor(string instanceId) =>
        (instanceId.StartsWith(@"USB\VID_", StringComparison.OrdinalIgnoreCase) ||
         instanceId.StartsWith(@"HID\", StringComparison.OrdinalIgnoreCase)) &&
        !instanceId.Contains("ROOT_HUB", StringComparison.OrdinalIgnoreCase);

    private static bool IsVirtual(IPnPDevice device)
    {
        IPnPDevice? current = device;
        for (var depth = 0; current is not null && depth < 8; depth++)
        {
            var service = TryGet(current, DevicePropertyKey.Device_Service);
            if (string.Equals(service, "ViGEmBus", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (current.HardwareIds?.Any(id => id.Contains("ViGEmBus", StringComparison.OrdinalIgnoreCase)) == true)
            {
                return true;
            }

            current = current.Parent;
        }

        return false;
    }

    private static void Add(Dictionary<string, ControllerDevice> nodes, IPnPDevice device)
    {
        var name = TryGet(device, DevicePropertyKey.Device_FriendlyName)
                   ?? TryGet(device, DevicePropertyKey.Device_DeviceDesc)
                   ?? device.InstanceId;
        nodes.TryAdd(device.InstanceId, new ControllerDevice(device.InstanceId, name));
    }

    private static string? TryGet(IPnPDevice device, DevicePropertyKey key)
    {
        try
        {
            return device.GetProperty<string>(key);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
