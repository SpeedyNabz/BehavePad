using BehavePad.Core.Input;

namespace BehavePad.Core.Filtering;

/// <summary>
/// Grows a fitted zone during play to cover spots a released stick creeps to. Drift shows up as a spring return
/// followed by slow movement while every other control sits idle, so that is the only pattern it learns from.
/// A spot must come back after separate releases before it counts, and the zone never grows past fixed limits.
/// Feed it every poll from a single thread.
/// </summary>
internal sealed class ZoneLearner
{
    /// <summary>How far past the zone a stick must be pushed before letting go counts as a release.</summary>
    public const double PushDistance = 0.4;

    /// <summary>A spring brings a released stick back near the zone this quickly. A slower return is a thumb guiding it.</summary>
    public const double SpringReturnMs = 250;

    /// <summary>After returning, the stick must stay within this range for <see cref="SettleMs"/> before tracking starts.</summary>
    public const double SettleRange = 0.02;

    public const double SettleMs = 120;

    public const double SettleTimeoutMs = 1000;

    /// <summary>Moving further than this within <see cref="SpeedWindowMs"/>, about a third of full travel per second, means a thumb is on the stick.</summary>
    public const double TouchDistance = 0.04;

    public const double SpeedWindowMs = 120;

    /// <summary>Path recorded this long before a touch is thrown away, since the thumb may already have been on the stick.</summary>
    public const double TouchDiscardMs = 600;

    public const double SampleIntervalMs = 50;

    /// <summary>A spot is only learned when it lies this close to the zone as it stands, so growth follows a connected path.</summary>
    public const double Reach = 0.1;

    /// <summary>Learning never reaches further than this past the tested zone.</summary>
    public const double MaxShift = 0.3;

    /// <summary>Separate releases after which the stick must creep to the same spot before it is learned.</summary>
    public const int ConfirmReleases = 2;

    public const double CellSize = 0.02;

    public const int MaxLearnedPoints = 32;

    private const int MaxSpots = 1024;

    private readonly StickZone _tested;
    private readonly double _maxAreaShare;
    private readonly Dictionary<(int X, int Y), Spot> _spots = [];
    private readonly Queue<(double Time, StickPoint Point)> _recent = new();
    private readonly Queue<(double Time, StickPoint Point)> _path = new();
    private readonly List<StickPoint> _learned;
    private Phase _phase;
    private double _phaseTime;
    private StickPoint _anchor;
    private double _anchorTime;
    private double _lastSampleTime;
    private int _release;

    public ZoneLearner(StickZone tested, IReadOnlyList<StickPoint> learned, double maxAreaShare)
    {
        ArgumentNullException.ThrowIfNull(tested);
        ArgumentNullException.ThrowIfNull(learned);
        _tested = tested;
        _maxAreaShare = maxAreaShare;
        _learned = [.. learned];
        Zone = new StickZone(tested.Hull.Concat(learned), tested.Margin);
        Learned = _learned.ToArray();
    }

    private enum Phase
    {
        Waiting,
        Pushed,
        Settling,
        Tracking,
    }

    public StickZone Zone { get; private set; }

    /// <summary>The learned spots. Replaced with a new list whenever the zone grows, so another thread can read it.</summary>
    public IReadOnlyList<StickPoint> Learned { get; private set; }

    /// <summary>Feeds one reading and its distance from the zone's outline. Returns true when the zone grew.</summary>
    public bool Observe(StickPoint point, double outlineDistance, bool othersIdle, double now)
    {
        var outside = outlineDistance - Zone.Margin;
        var touched = MovedFast(point, now);

        switch (_phase)
        {
            case Phase.Waiting:
                if (outside >= PushDistance)
                {
                    Enter(Phase.Pushed, now);
                }

                break;

            case Phase.Pushed:
                if (outside >= PushDistance)
                {
                    _phaseTime = now;
                }
                else if (now - _phaseTime > SpringReturnMs)
                {
                    Enter(Phase.Waiting, now);
                }
                else if (outside <= Reach)
                {
                    Enter(Phase.Settling, now);
                    _anchor = point;
                    _anchorTime = now;
                }

                break;

            case Phase.Settling:
                if (outside >= PushDistance)
                {
                    Enter(Phase.Pushed, now);
                }
                else if (now - _phaseTime > SettleTimeoutMs)
                {
                    Enter(Phase.Waiting, now);
                }
                else if (point.DistanceTo(_anchor) > SettleRange)
                {
                    _anchor = point;
                    _anchorTime = now;
                }
                else if (now - _anchorTime >= SettleMs)
                {
                    _release++;
                    _path.Clear();
                    _lastSampleTime = double.NegativeInfinity;
                    Enter(Phase.Tracking, now);
                }

                break;

            case Phase.Tracking:
                if (touched || !othersIdle || outside >= PushDistance)
                {
                    _path.Clear();
                    Enter(outside >= PushDistance ? Phase.Pushed : Phase.Waiting, now);
                    break;
                }

                if (now - _lastSampleTime >= SampleIntervalMs)
                {
                    _path.Enqueue((now, point));
                    _lastSampleTime = now;
                }

                var grew = false;
                while (_path.Count > 0 && now - _path.Peek().Time >= TouchDiscardMs)
                {
                    grew |= Record(_path.Dequeue().Point);
                }

                return grew;
        }

        return false;
    }

    private void Enter(Phase phase, double now)
    {
        _phase = phase;
        _phaseTime = now;
    }

    /// <summary>Remembers recent readings and reports whether the stick just moved faster than drift can.</summary>
    private bool MovedFast(StickPoint point, double now)
    {
        _recent.Enqueue((now, point));
        while (now - _recent.Peek().Time > SpeedWindowMs)
        {
            _recent.Dequeue();
        }

        foreach (var (_, earlier) in _recent)
        {
            if (point.DistanceTo(earlier) > TouchDistance)
            {
                return true;
            }
        }

        return false;
    }

    private bool Record(StickPoint point)
    {
        if (Zone.Contains(point) || _tested.Nearest(point).Distance - _tested.Margin > MaxShift)
        {
            return false;
        }

        var key = ((int)Math.Floor(point.X / CellSize), (int)Math.Floor(point.Y / CellSize));
        if (!_spots.TryGetValue(key, out var spot))
        {
            if (_spots.Count >= MaxSpots)
            {
                return false;
            }

            _spots[key] = spot = new Spot();
        }

        spot.Add(point, _release);
        return spot.Releases >= ConfirmReleases && LearnConfirmedSpots();
    }

    /// <summary>Adds every confirmed spot within reach. Each one can bring the next within reach, so repeat until none fit.</summary>
    private bool LearnConfirmedSpots()
    {
        var grew = false;
        for (var added = true; added;)
        {
            added = false;
            foreach (var spot in _spots.Values)
            {
                if (spot.Done || spot.Releases < ConfirmReleases)
                {
                    continue;
                }

                var mean = spot.Mean;
                var outside = Zone.Nearest(mean).Distance - Zone.Margin;
                if (outside <= 0)
                {
                    spot.Done = true;
                    continue;
                }

                if (outside > Reach || _learned.Count >= MaxLearnedPoints)
                {
                    continue;
                }

                var grown = Zone.With(mean);
                spot.Done = true;
                if (grown.AreaShare > _maxAreaShare)
                {
                    continue;
                }

                Zone = grown;
                _learned.Add(mean);
                _learned.RemoveAll(p => !grown.Hull.Contains(p));
                added = grew = true;
            }
        }

        if (grew)
        {
            Learned = _learned.ToArray();
        }

        return grew;
    }

    private sealed class Spot
    {
        private double _sumX;
        private double _sumY;
        private int _count;
        private int _lastRelease;

        public int Releases { get; private set; }

        public bool Done { get; set; }

        public StickPoint Mean => new(_sumX / _count, _sumY / _count);

        public void Add(StickPoint point, int release)
        {
            if (release != _lastRelease)
            {
                Releases++;
                _lastRelease = release;
            }

            _sumX += point.X;
            _sumY += point.Y;
            _count++;
        }
    }
}
