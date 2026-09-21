using System.Windows.Threading;
using BehavePad.Core.Engine;
using BehavePad.Core.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BehavePad.Services;

/// <summary>
/// Publishes a fresh frame about 60 times a second. In the agent it owns the input pump and reads the
/// controller itself. In the window it owns nothing and replays what the agent sends, so only one process
/// ever opens XInput or the virtual controller.
/// </summary>
public sealed partial class ControllerService : ObservableObject, IDisposable
{
    private readonly DispatcherTimer? _frameTimer;
    private readonly XInputSource? _xinput;
    private readonly SimulatedGamepad _demo = new();
    private double _lastBatteryCheck = double.NegativeInfinity;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private bool _isDemo;

    [ObservableProperty]
    private int _slot = -1;

    [ObservableProperty]
    private string _statusText = "Looking for a controller";

    [ObservableProperty]
    private string _batteryText = "";

    /// <summary>The agent's constructor: opens XInput and drives a pump.</summary>
    public ControllerService(bool useDemo, int preferredSlot)
    {
        try
        {
            _xinput = new XInputSource();
        }
        catch (Exception ex)
        {
            XInputError = ex.Message;
        }

        HasXInput = _xinput is not null;
        IsDemo = useDemo || _xinput is null;
        Pump = new InputPump(IsDemo ? _demo : _xinput!) { PreferredSlot = preferredSlot };
        _frameTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _frameTimer.Tick += OnTick;
    }

    /// <summary>The window's constructor: no pump, no XInput. Frames arrive from the agent instead.</summary>
    private ControllerService()
    {
    }

    public static ControllerService Remote() => new();

    public event EventHandler? FrameUpdated;

    /// <summary>Raised on the UI thread when sending input to the virtual controller failed.</summary>
    public event EventHandler<string>? ForwardingFailed;

    /// <summary>The live pump, or null in the window, where the agent owns it.</summary>
    public InputPump? Pump { get; }

    public SimulatedGamepad Demo => _demo;

    /// <summary>Direct XInput access, available even while the demo controller is in use.</summary>
    public XInputSource? XInput => _xinput;

    public string? XInputError { get; private set; }

    public bool HasXInput { get; private set; } = true;

    public PumpFrame Frame { get; private set; }

    public string ControllerName => IsDemo ? _demo.DisplayName : "Xbox controller";

    public void Start()
    {
        Pump?.Start();
        _frameTimer?.Start();
    }

    public void UseDemo(bool demo)
    {
        if (Pump is null)
        {
            return;
        }

        if (!demo && _xinput is null)
        {
            demo = true;
        }

        IsDemo = demo;
        Pump.Source = demo ? _demo : _xinput!;
        OnPropertyChanged(nameof(ControllerName));
    }

    public void SetDemoScenario(DemoScenario scenario) => _demo.Scenario = scenario;

    /// <summary>A short buzz so the user can feel that a test step finished. Agent only; the window has no pump.</summary>
    public async void Pulse(double strength = 0.45, int milliseconds = 160)
    {
        var slot = Frame.Slot;
        if (Pump is null || slot < 0)
        {
            return;
        }

        try
        {
            Pump.Source.SetVibration(slot, strength, strength);
            await Task.Delay(milliseconds);
            Pump.Source.SetVibration(slot, 0, 0);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"Rumble pulse failed: {ex.Message}");
        }
    }

    /// <summary>Replays one frame the agent sent, so the window's plots and readouts move as they always did.</summary>
    public void ApplyRemoteFrame(PumpFrame frame)
    {
        Frame = frame;
        FrameUpdated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Copies the agent's controller status into the window.</summary>
    public void ApplyRemoteStatus(bool connected, int slot, bool demo, string statusText, string batteryText, bool hasXInput)
    {
        IsConnected = connected;
        Slot = slot;
        StatusText = statusText;
        BatteryText = batteryText;
        HasXInput = hasXInput;
        if (IsDemo != demo)
        {
            IsDemo = demo;
            OnPropertyChanged(nameof(ControllerName));
        }
    }

    public void Dispose()
    {
        _frameTimer?.Stop();
        Pump?.Dispose();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var pump = Pump!;
        Frame = pump.Latest;
        IsConnected = Frame.Connected;
        Slot = Frame.Slot;
        StatusText = !Frame.Connected
            ? "No controller found"
            : IsDemo ? "Demo controller" : $"Controller {Frame.Slot + 1} connected";

        if (Frame.TimestampMs - _lastBatteryCheck > 3000)
        {
            _lastBatteryCheck = Frame.TimestampMs;
            BatteryText = DescribeBattery(Frame.Connected ? pump.Source.GetBattery(Frame.Slot) : null);
        }

        if (pump.TakeSinkError() is { } error)
        {
            ForwardingFailed?.Invoke(this, error);
        }

        FrameUpdated?.Invoke(this, EventArgs.Empty);
    }

    private static string DescribeBattery(BatteryStatus? battery) => battery switch
    {
        null => "",
        { Kind: BatteryKind.Wired } => "Wired",
        { Level: BatteryLevel.Full } => "Battery full",
        { Level: BatteryLevel.Medium } => "Battery medium",
        { Level: BatteryLevel.Low } => "Battery low",
        _ => "Battery empty",
    };
}
