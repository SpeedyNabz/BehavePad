using BehavePad.Core.Analysis;
using BehavePad.Core.Filtering;
using BehavePad.Core.Input;
using BehavePad.Ipc;
using BehavePad.Services;
using BehavePad.ViewModels;

namespace BehavePad.Agent;

/// <summary>
/// Runs the drift test inside the agent, where the recorders still see every poll rather than the sixty
/// frames a second the window gets. It publishes a <see cref="TestSnapshot"/> as it goes, and the window
/// draws that; picking a protection level and saving stays in the window.
/// </summary>
public sealed class TestRunner
{
    private const double CountdownMs = 3000;
    private const int MaxCloudPoints = 700;

    /// <summary>Margin drawn around the growing outline while the test runs, before a preset is picked.</summary>
    private static readonly double PreviewMargin = FilterProfileBuilder.OutlineMargin(ProtectionLevel.Balanced);

    private readonly ControllerService _controller;
    private readonly List<StickPoint> _leftCloud = [];
    private readonly List<StickPoint> _rightCloud = [];
    private readonly HashSet<(int, int)> _leftSeen = [];
    private readonly HashSet<(int, int)> _rightSeen = [];
    private RestRecorder? _rest;
    private SnapBackRecorder? _snap;
    private RestCapture? _restCapture;
    private StickZone? _leftLiveZone;
    private StickZone? _rightLiveZone;
    private RestLength _length = RestLength.Standard;
    private double _phaseStart;
    private int _frameCount;

    public TestRunner(ControllerService controller)
    {
        _controller = controller;
        _controller.FrameUpdated += OnFrame;
    }

    /// <summary>Raised whenever the snapshot changed enough to be worth sending.</summary>
    public event EventHandler? Changed;

    public TestSnapshot Snapshot { get; private set; } = new() { OutlineMargin = PreviewMargin };

    public bool IsRunning => Snapshot.Step is TestStep.Countdown or TestStep.Resting or TestStep.SnapBack;

    private double RestSeconds => _length switch
    {
        RestLength.Quick => 6,
        RestLength.Thorough => 20,
        _ => 10,
    };

    public void Begin(RestLength length)
    {
        if (!_controller.IsConnected)
        {
            Publish(Snapshot with { Notice = "Connect a controller first, or turn on the demo controller in Setup." });
            return;
        }

        _length = length;
        StartCountdown(_controller.Frame.TimestampMs, null);
    }

    public void Cancel() => Abort(null);

    public void SkipSnapBack() =>
        Finish(includeSnapBack: _snap is { } snap && (snap.LeftCount > 0 || snap.RightCount > 0));

    /// <summary>Clears a finished result, so the window going back to the intro leaves nothing stale behind.</summary>
    public void Reset()
    {
        if (IsRunning)
        {
            Abort(null);
            return;
        }

        Publish(new TestSnapshot { OutlineMargin = PreviewMargin });
    }

    private void StartCountdown(double now, string? notice)
    {
        _controller.Pump?.SetObserver(null);
        _controller.SetDemoScenario(DemoScenario.Resting);
        _rest = new RestRecorder();
        _snap = null;
        _restCapture = null;
        ClearClouds();
        _phaseStart = now;
        Publish(new TestSnapshot
        {
            Step = TestStep.Countdown,
            Countdown = 3,
            Notice = notice,
            OutlineMargin = PreviewMargin,
        });
    }

    private void BeginResting()
    {
        var rest = _rest ??= new RestRecorder();
        rest.Reset();
        ClearClouds();
        _controller.Pump?.SetObserver((state, time) => rest.Add(state, time));
        Publish(Snapshot with { Step = TestStep.Resting, Notice = null });
    }

    private void FinishResting()
    {
        _controller.Pump?.SetObserver(null);
        var capture = _restCapture = _rest!.ToCapture();
        _controller.Pulse();

        // The preview only saw about 60 readings a second. Redraw the outline from every reading before snap-back adds to it.
        _leftLiveZone = Grow(null, capture.States.Select(s => s.LeftStick));
        _rightLiveZone = Grow(null, capture.States.Select(s => s.RightStick));

        _snap = new SnapBackRecorder(TestViewModel.TargetReleases);
        var snap = _snap;
        _controller.SetDemoScenario(DemoScenario.SnapBack);
        _controller.Pump?.SetObserver((state, time) => snap.Add(state, time));

        Publish(Snapshot with
        {
            Step = TestStep.SnapBack,
            LeftReleases = 0,
            RightReleases = 0,
            LeftSettles = [],
            RightSettles = [],
            LeftHint = "Push to the edge",
            RightHint = "Push to the edge",
            LeftOutline = _leftLiveZone?.Hull ?? [],
            RightOutline = _rightLiveZone?.Hull ?? [],
        });
    }

    private void Finish(bool includeSnapBack)
    {
        _controller.Pump?.SetObserver(null);
        _controller.SetDemoScenario(DemoScenario.Resting);
        if (_restCapture is null)
        {
            Abort(null);
            return;
        }

        var report = DriftAnalyzer.Analyze(
            _restCapture,
            includeSnapBack ? _snap?.ToCapture() : null,
            _controller.ControllerName);

        Publish(Snapshot with { Step = TestStep.Results, Report = report, Notice = null });
        _controller.Pulse(0.3, 120);
    }

    private void Abort(string? notice)
    {
        _controller.Pump?.SetObserver(null);
        _controller.SetDemoScenario(DemoScenario.Resting);
        _rest = null;
        _snap = null;
        _restCapture = null;
        ClearClouds();
        Publish(new TestSnapshot { Step = TestStep.Intro, Notice = notice, OutlineMargin = PreviewMargin });
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        if (!IsRunning)
        {
            return;
        }

        var frame = _controller.Frame;
        var now = frame.TimestampMs;
        _frameCount++;

        switch (Snapshot.Step)
        {
            case TestStep.Countdown:
            {
                if (!frame.Connected)
                {
                    Abort("The controller disconnected. Reconnect it and try again.");
                    return;
                }

                var remaining = CountdownMs - (now - _phaseStart);
                var countdown = Math.Max(1, (int)Math.Ceiling(remaining / 1000));
                if (remaining <= 0)
                {
                    BeginResting();
                }
                else if (countdown != Snapshot.Countdown)
                {
                    Publish(Snapshot with { Countdown = countdown });
                }

                break;
            }

            case TestStep.Resting:
            {
                if (!frame.Connected)
                {
                    Abort("The controller disconnected. Reconnect it and try again.");
                    return;
                }

                var rest = _rest!;
                if (rest.IsDisturbed)
                {
                    StartCountdown(now, "The controller moved, so the check started over. Leave it flat and don't touch it.");
                    return;
                }

                AddCloudPoint(frame.Raw);
                var elapsed = rest.ElapsedMs;
                if (elapsed >= RestSeconds * 1000)
                {
                    FinishResting();
                    return;
                }

                Publish(Snapshot with
                {
                    RestProgress = Math.Clamp(elapsed / (RestSeconds * 1000), 0, 1),
                    RestRemaining = (int)Math.Max(0, Math.Ceiling(RestSeconds - (elapsed / 1000))),
                    PhantomPresses = rest.PressEvents,
                    LeftCloud = _leftCloud.ToArray(),
                    RightCloud = _rightCloud.ToArray(),
                    LeftOutline = _leftLiveZone?.Hull ?? [],
                    RightOutline = _rightLiveZone?.Hull ?? [],
                    LeftZoom = ZoomFor(_leftCloud),
                    RightZoom = ZoomFor(_rightCloud),
                });
                break;
            }

            case TestStep.SnapBack:
            {
                var snap = _snap!;
                var next = Snapshot;
                if (snap.LeftCount != next.LeftReleases)
                {
                    var settles = snap.Settles(StickSide.Left);
                    _leftLiveZone = Grow(_leftLiveZone, settles);
                    next = next with { LeftReleases = snap.LeftCount, LeftSettles = settles, LeftOutline = _leftLiveZone?.Hull ?? [] };
                }

                if (snap.RightCount != next.RightReleases)
                {
                    var settles = snap.Settles(StickSide.Right);
                    _rightLiveZone = Grow(_rightLiveZone, settles);
                    next = next with { RightReleases = snap.RightCount, RightSettles = settles, RightOutline = _rightLiveZone?.Hull ?? [] };
                }

                next = next with
                {
                    LeftHint = Hint(snap.LeftPhase, snap.LeftCount),
                    RightHint = Hint(snap.RightPhase, snap.RightCount),
                };

                if (snap.IsComplete)
                {
                    Snapshot = next;
                    Finish(includeSnapBack: true);
                    return;
                }

                Publish(next);
                break;
            }
        }
    }

    private static string Hint(SnapPhase phase, int count) =>
        count >= TestViewModel.TargetReleases ? "Done"
        : phase switch
        {
            SnapPhase.Pushed => "Now let go",
            SnapPhase.Settling => "Settling",
            _ => count == 0 ? "Push to the edge" : "Again, another direction",
        };

    /// <summary>Stretches a preview outline to cover more points, starting one when there is none yet.</summary>
    private static StickZone? Grow(StickZone? zone, IEnumerable<StickPoint> points)
    {
        foreach (var point in points)
        {
            zone = zone?.With(point) ?? new StickZone([point], PreviewMargin);
        }

        return zone;
    }

    private static double ZoomFor(List<StickPoint> cloud) =>
        cloud.Count == 0 ? 0.3 : Math.Clamp((cloud.Max(p => p.Magnitude) + PreviewMargin) * 2.2, 0.15, 1.0);

    private void ClearClouds()
    {
        _leftCloud.Clear();
        _rightCloud.Clear();
        _leftSeen.Clear();
        _rightSeen.Clear();
        _leftLiveZone = _rightLiveZone = null;
    }

    private void AddCloudPoint(GamepadState state)
    {
        Add(state.LeftStick, _leftCloud, _leftSeen);
        Add(state.RightStick, _rightCloud, _rightSeen);
        _leftLiveZone = Grow(_leftLiveZone, [state.LeftStick]);
        _rightLiveZone = Grow(_rightLiveZone, [state.RightStick]);

        static void Add(StickPoint point, List<StickPoint> cloud, HashSet<(int, int)> seen)
        {
            if (cloud.Count < MaxCloudPoints && seen.Add(((int)Math.Round(point.X / 0.002), (int)Math.Round(point.Y / 0.002))))
            {
                cloud.Add(point);
            }
        }
    }

    /// <summary>Point clouds only go out every sixth frame, so a rest check does not flood the pipe.</summary>
    private void Publish(TestSnapshot snapshot)
    {
        var heavy = snapshot.Step != TestStep.Resting || _frameCount % 6 == 0;
        if (!heavy && ReferenceEquals(snapshot.LeftCloud, Snapshot.LeftCloud))
        {
            return;
        }

        if (!heavy)
        {
            snapshot = snapshot with
            {
                LeftCloud = Snapshot.LeftCloud,
                RightCloud = Snapshot.RightCloud,
                LeftOutline = Snapshot.LeftOutline,
                RightOutline = Snapshot.RightOutline,
            };
        }

        Snapshot = snapshot;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
