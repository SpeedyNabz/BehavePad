using BehavePad.Core.Input;

namespace BehavePad.Core.Analysis;

public enum SnapPhase
{
    WaitingForPush,
    Pushed,
    Settling,
}

/// <summary>
/// Watches the user push each stick to its edge and let go, then records where the stick comes to rest.
/// Worn springs return to a different spot each time, which a still-controller test alone cannot reveal.
/// </summary>
public sealed class SnapBackRecorder
{
    public const double PushThreshold = 0.72;
    public const double ReleaseThreshold = 0.45;
    public const double MaxSettleDistance = 0.4;
    public const double SettleRange = 0.02;
    public const double SettleWindowMs = 150;
    public const double SettleTimeoutMs = 1500;

    private readonly object _gate = new();
    private readonly Tracker _left = new();
    private readonly Tracker _right = new();

    public SnapBackRecorder(int targetReleasesPerStick = 6)
    {
        TargetReleases = Math.Max(1, targetReleasesPerStick);
    }

    public int TargetReleases { get; }

    public int LeftCount
    {
        get
        {
            lock (_gate)
            {
                return _left.Settles.Count;
            }
        }
    }

    public int RightCount
    {
        get
        {
            lock (_gate)
            {
                return _right.Settles.Count;
            }
        }
    }

    public SnapPhase LeftPhase
    {
        get
        {
            lock (_gate)
            {
                return _left.Phase;
            }
        }
    }

    public SnapPhase RightPhase
    {
        get
        {
            lock (_gate)
            {
                return _right.Phase;
            }
        }
    }

    public bool IsComplete
    {
        get
        {
            lock (_gate)
            {
                return _left.Settles.Count >= TargetReleases && _right.Settles.Count >= TargetReleases;
            }
        }
    }

    public void Add(in GamepadState state, double timeMs)
    {
        lock (_gate)
        {
            if (_left.Settles.Count < TargetReleases)
            {
                _left.Add(state.LeftStick, timeMs);
            }

            if (_right.Settles.Count < TargetReleases)
            {
                _right.Add(state.RightStick, timeMs);
            }
        }
    }

    public SnapBackCapture ToCapture()
    {
        lock (_gate)
        {
            return new SnapBackCapture(_left.Settles.ToArray(), _right.Settles.ToArray());
        }
    }

    public IReadOnlyList<StickPoint> Settles(StickSide side)
    {
        lock (_gate)
        {
            return (side == StickSide.Left ? _left : _right).Settles.ToArray();
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _left.Reset();
            _right.Reset();
        }
    }

    private sealed class Tracker
    {
        private readonly Queue<(double Time, StickPoint Point)> _window = new();
        private double _releaseTime;

        public SnapPhase Phase { get; private set; }

        public List<StickPoint> Settles { get; } = [];

        public void Reset()
        {
            Phase = SnapPhase.WaitingForPush;
            Settles.Clear();
            _window.Clear();
        }

        public void Add(StickPoint point, double time)
        {
            switch (Phase)
            {
                case SnapPhase.WaitingForPush:
                    if (point.Magnitude >= PushThreshold)
                    {
                        Phase = SnapPhase.Pushed;
                    }

                    break;

                case SnapPhase.Pushed:
                    if (point.Magnitude <= ReleaseThreshold)
                    {
                        Phase = SnapPhase.Settling;
                        _releaseTime = time;
                        _window.Clear();
                        _window.Enqueue((time, point));
                    }

                    break;

                case SnapPhase.Settling:
                    if (point.Magnitude >= PushThreshold)
                    {
                        Phase = SnapPhase.Pushed;
                        _window.Clear();
                        break;
                    }

                    _window.Enqueue((time, point));
                    while (_window.Count > 0 && _window.Peek().Time < time - SettleWindowMs)
                    {
                        _window.Dequeue();
                    }

                    if (time - _releaseTime >= SettleWindowMs && TryGetStillMean(out var mean))
                    {
                        // A still stick far from center means the user is holding it, not that it sprang back.
                        if (mean.Magnitude <= MaxSettleDistance)
                        {
                            Settles.Add(Stats.Round(mean));
                        }

                        Phase = SnapPhase.WaitingForPush;
                        _window.Clear();
                    }
                    else if (time - _releaseTime > SettleTimeoutMs)
                    {
                        Phase = SnapPhase.WaitingForPush;
                        _window.Clear();
                    }

                    break;
            }
        }

        private bool TryGetStillMean(out StickPoint mean)
        {
            mean = StickPoint.Zero;
            if (_window.Count == 0)
            {
                return false;
            }

            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
            double sumX = 0, sumY = 0;
            foreach (var (_, point) in _window)
            {
                minX = Math.Min(minX, point.X);
                maxX = Math.Max(maxX, point.X);
                minY = Math.Min(minY, point.Y);
                maxY = Math.Max(maxY, point.Y);
                sumX += point.X;
                sumY += point.Y;
            }

            mean = new StickPoint(sumX / _window.Count, sumY / _window.Count);
            return maxX - minX <= SettleRange && maxY - minY <= SettleRange;
        }
    }
}

/// <summary>Where each stick came to rest after being released.</summary>
public sealed record SnapBackCapture(IReadOnlyList<StickPoint> LeftSettles, IReadOnlyList<StickPoint> RightSettles)
{
    public IReadOnlyList<StickPoint> For(StickSide side) => side == StickSide.Left ? LeftSettles : RightSettles;
}
