using BehavePad.Core.Devices;

namespace BehavePad.Core.Tests;

/// <summary>
/// Instance IDs here are copied from real machines: a cabled Xbox 360 pad, an Xbox Wireless Controller paired
/// over Bluetooth LE, and an Xbox One pad over Bluetooth Classic.
/// </summary>
public sealed class ControllerAncestorTests
{
    private const string BleHidNode = @"BTHLEDevice\{00001812-0000-1000-8000-00805f9b34fb}_Dev_VID&02045e_PID&0b13_REV&0523_686ce6836b76\9&108d14cf&0&0016";
    private const string BleDeviceNode = @"BTHLE\Dev_686ce6836b76\8&2f338c5a&0&686ce6836b76";
    private const string BluetoothRadio = @"USB\VID_8087&PID_0032\5&2a9f9d8d&0&14";

    private static List<string> Walk(params string[] ancestors)
    {
        var rule = new ControllerAncestors();
        return ancestors.TakeWhile(rule.Accept).ToList();
    }

    [Fact]
    public void A_cabled_pad_is_hidden_along_with_the_usb_device_hidhide_loads_on()
    {
        Assert.Equal(
            [@"USB\VID_045E&PID_028E&IG_00\2&dee0f28&0&00", @"USB\VID_045E&PID_028E\01"],
            Walk(
                @"USB\VID_045E&PID_028E&IG_00\2&dee0f28&0&00",
                @"USB\VID_045E&PID_028E\01",
                @"USB\ROOT_HUB30\4&1b04e9b7&0&0"));
    }

    [Fact]
    public void A_bluetooth_pad_is_hidden_along_with_the_ble_node_hidhide_loads_on()
    {
        // The regression: these were turned down for not starting with USB\ or HID\, so BehavePad blocked only
        // the input node, HidHide was not loaded on anything blocked, and games kept seeing the drift.
        Assert.Equal([BleHidNode, BleDeviceNode], Walk(BleHidNode, BleDeviceNode, @"BTH\MS_BTHLE\7&594def5&0&3"));
    }

    [Fact]
    public void A_bluetooth_classic_pad_is_hidden_along_with_its_bthenum_nodes()
    {
        Assert.Equal(
            [@"BTHENUM\{00001124-0000-1000-8000-00805f9b34fb}_VID&0002045e_PID&02fd\7&38dcd62&0&686ce6836b76_c00000000",
             @"BTHENUM\Dev_686ce6836b76\7&38dcd62&0&686ce6836b76"],
            Walk(
                @"BTHENUM\{00001124-0000-1000-8000-00805f9b34fb}_VID&0002045e_PID&02fd\7&38dcd62&0&686ce6836b76_c00000000",
                @"BTHENUM\Dev_686ce6836b76\7&38dcd62&0&686ce6836b76",
                BluetoothRadio));
    }

    [Fact]
    public void The_bluetooth_radio_is_left_alone_even_though_it_is_a_usb_device()
    {
        // Hiding the radio would hide every Bluetooth keyboard, mouse and headset on the machine with it.
        Assert.DoesNotContain(BluetoothRadio, Walk(BleHidNode, BleDeviceNode, BluetoothRadio));
    }

    [Fact]
    public void A_shared_usb_hub_is_left_alone()
    {
        Assert.Empty(Walk(@"USB\ROOT_HUB30\4&1b04e9b7&0&0"));
    }

    [Fact]
    public void Each_controller_is_walked_on_its_own_rule()
    {
        // Latching is per walk: a Bluetooth pad must not stop the next controller's USB parents being hidden.
        Walk(BleHidNode, BleDeviceNode);
        Assert.Equal([@"USB\VID_045E&PID_028E\01"], Walk(@"USB\VID_045E&PID_028E\01"));
    }
}
