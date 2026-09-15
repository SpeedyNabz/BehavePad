using BehavePad.Core.Engine;
using BehavePad.Core.Input;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace BehavePad.Services;

/// <summary>A virtual Xbox 360 controller created through ViGEmBus. Games read this instead of the worn one.</summary>
public sealed class VirtualXboxPad : IVirtualPadSink
{
    private readonly object _gate = new();
    private readonly ViGEmClient _client;
    private readonly IXbox360Controller _controller;
    private bool _disposed;

    private VirtualXboxPad(ViGEmClient client, IXbox360Controller controller)
    {
        _client = client;
        _controller = controller;
    }

    public event EventHandler<RumbleEventArgs>? RumbleRequested;

    /// <summary>Creates and plugs in the virtual controller. Throws when ViGEmBus is missing.</summary>
    public static VirtualXboxPad Create()
    {
        var client = new ViGEmClient();
        try
        {
            var controller = client.CreateXbox360Controller();
            controller.AutoSubmitReport = false;
            var pad = new VirtualXboxPad(client, controller);
            controller.FeedbackReceived += pad.OnFeedbackReceived;
            controller.Connect();
            return pad;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    // ViGEm's UserIndex is not used on purpose: it does not match the XInput slot when an Xbox One or Series
    // controller is connected. FilterService finds the real slot with VirtualSlotLocator instead.
    public void Submit(in GamepadState state)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            // XUSB report button bits match XInput, so they pass straight through.
            _controller.SetButtonsFull((ushort)state.Buttons);
            _controller.SetAxisValue(Xbox360Axis.LeftThumbX, state.LeftX);
            _controller.SetAxisValue(Xbox360Axis.LeftThumbY, state.LeftY);
            _controller.SetAxisValue(Xbox360Axis.RightThumbX, state.RightX);
            _controller.SetAxisValue(Xbox360Axis.RightThumbY, state.RightY);
            _controller.SetSliderValue(Xbox360Slider.LeftTrigger, state.LeftTrigger);
            _controller.SetSliderValue(Xbox360Slider.RightTrigger, state.RightTrigger);
            _controller.SubmitReport();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _controller.FeedbackReceived -= OnFeedbackReceived;
            try
            {
                _controller.Disconnect();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning($"Virtual controller disconnect failed: {ex.Message}");
            }

            _client.Dispose();
        }
    }

    private void OnFeedbackReceived(object sender, Xbox360FeedbackReceivedEventArgs e) =>
        RumbleRequested?.Invoke(this, new RumbleEventArgs(e.LargeMotor / 255.0, e.SmallMotor / 255.0));
}
