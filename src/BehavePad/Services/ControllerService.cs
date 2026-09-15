using System.Windows.Threading;
using BehavePad.Core.Engine;
using BehavePad.Core.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BehavePad.Services;

/// <summary>Owns the input pump and publishes a fresh frame to the UI about 60 times a second.</summary>
public sealed partial class ControllerService : ObservableObject, IDisposable
{
    private readonly DispatcherTimer _frameTimer;
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

        IsDemo = useDemo || _xinput is null;
        Pump = new InputPump(IsDemo ? _demo : _xinput!) { PreferredSlot = preferredSlot };
        _frameTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _frameTimer.Tick += OnTick;
    }

    public event EventHandler? FrameUpdated;

    /// <summary>Raised on the UI thread when sending input to the virtual controller failed.</summary>
    public event EventHandler<string>? ForwardingFailed;

    public InputPump Pump { get; }

    public SimulatedGamepad Demo => _demo;

    /// <summary>Direct XInput access, available even while the demo controller is in use.</summary>
    public XInputSource? XInput => _xinput;

    public string? XInputError { get; }

    public bool HasXInput => _xinput is not null;

    public PumpFrame Frame { get; private set; }

    public string ControllerName => IsDemo ? _demo.DisplayName : "Xbox controller";

    public void Start()
    {
        Pump.Start();
        _frameTimer.Start();
    }

    public void UseDemo(bool demo)
    {
        if (!demo && _xinput is null)
        {
            demo = true;
        }

        IsDemo = demo;
        Pump.Source = demo ? _demo : _xinput!;
        OnPropertyChanged(nameof(ControllerName));
    }

    public void SetDemoScenario(DemoScenario scenario) => _demo.Scenario = scenario;

    /// <summary>A short buzz so the user can feel that a test step finished.</summary>
    public async void Pulse(double strength = 0.45, int milliseconds = 160)
    {
        var slot = Frame.Slot;
        if (slot < 0)
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

    public void Dispose()
    {
        _frameTimer.Stop();
        Pump.Dispose();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        Frame = Pump.Latest;
        IsConnected = Frame.Connected;
        Slot = Frame.Slot;
        StatusText = !Frame.Connected
            ? "No controller found"
            : IsDemo ? "Demo controller" : $"Controller {Frame.Slot + 1} connected";

        if (Frame.TimestampMs - _lastBatteryCheck > 3000)
        {
            _lastBatteryCheck = Frame.TimestampMs;
            BatteryText = DescribeBattery(Frame.Connected ? Pump.Source.GetBattery(Frame.Slot) : null);
        }

        if (Pump.TakeSinkError() is { } error)
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
