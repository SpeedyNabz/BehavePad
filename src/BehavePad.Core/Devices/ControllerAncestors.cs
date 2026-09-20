namespace BehavePad.Core.Devices;

/// <summary>
/// Walks up from a controller's input node and decides which parents still belong to that one controller.
/// <para>
/// HidHide can only hide a device it is loaded on, and it loads on the node the bus enumerated rather than the
/// "HID-compliant game controller" collection underneath it: <c>USB\VID_045E&amp;PID_028E&amp;IG_00\…</c> for a
/// cabled pad, <c>BTHLEDevice\{00001812-…}\…</c> for a Bluetooth one. So blocking the input node alone hides
/// nothing; the parents have to go on the list too.
/// </para>
/// <para>
/// The walk has to stop at the controller, though. A Bluetooth pad's grandparent is the radio, which every
/// Bluetooth device in the machine sits under and which enumerates as an ordinary USB device, so a rule written
/// only in terms of prefixes would climb straight into it and hide the user's keyboard and mouse along with the
/// drift. Once the walk has crossed into Bluetooth it therefore stays among nodes carrying that pad's own
/// address and never accepts a USB node again.
/// </para>
/// </summary>
public sealed class ControllerAncestors
{
    /// <summary>Instance-ID prefixes of the per-device Bluetooth nodes: each one names a single paired device.</summary>
    private static readonly string[] BluetoothDeviceNodes = [@"BTHLEDEVICE\", @"BTHENUM\", @"BTHLE\DEV_"];

    private bool _pastBluetooth;

    /// <summary>
    /// True when this parent is still part of the controller and should be hidden with it. Call it on each
    /// ancestor in turn, nearest first, and stop walking at the first one it turns down.
    /// </summary>
    public bool Accept(string instanceId)
    {
        if (string.IsNullOrEmpty(instanceId) || instanceId.Contains("ROOT_HUB", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (BluetoothDeviceNodes.Any(prefix => instanceId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            _pastBluetooth = true;
            return true;
        }

        // Anything above a Bluetooth device is the radio or the machine, shared with every other device on it.
        // Note "BTH\MS_BTHLE", the Bluetooth LE enumerator, is caught here rather than by name.
        return !_pastBluetooth &&
               (instanceId.StartsWith(@"USB\VID_", StringComparison.OrdinalIgnoreCase) ||
                instanceId.StartsWith(@"HID\", StringComparison.OrdinalIgnoreCase));
    }
}
