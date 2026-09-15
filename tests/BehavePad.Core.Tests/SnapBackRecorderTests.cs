using BehavePad.Core.Analysis;
using BehavePad.Core.Input;

namespace BehavePad.Core.Tests;

public class SnapBackRecorderTests
{
    private sealed class Feeder(SnapBackRecorder recorder)
    {
        private double _time;

        public void Hold(StickPoint left, StickPoint right, double durationMs)
        {
            var state = default(GamepadState).WithStick(StickSide.Left, left).WithStick(StickSide.Right, right);
            for (var end = _time + durationMs; _time < end; _time += 1)
            {
                recorder.Add(state, _time);
            }
        }
    }

    [Fact]
    public void Records_where_the_stick_settles_after_each_release()
    {
        var recorder = new SnapBackRecorder(targetReleasesPerStick: 2);
        var feed = new Feeder(recorder);

        feed.Hold(StickPoint.Zero, StickPoint.Zero, 100);
        feed.Hold(new StickPoint(0.95, 0), StickPoint.Zero, 200);
        feed.Hold(new StickPoint(0.03, -0.02), StickPoint.Zero, 300);
        feed.Hold(new StickPoint(0, -0.99), StickPoint.Zero, 200);
        feed.Hold(new StickPoint(-0.02, 0.01), StickPoint.Zero, 300);

        var settles = recorder.ToCapture().LeftSettles;
        Assert.Equal(2, settles.Count);
        Assert.Equal(0.03, settles[0].X, 3);
        Assert.Equal(-0.02, settles[0].Y, 3);
        Assert.Equal(-0.02, settles[1].X, 3);
        Assert.Equal(0, recorder.RightCount);
        Assert.False(recorder.IsComplete);
    }

    [Fact]
    public void Holding_the_stick_part_way_is_not_counted_as_a_release()
    {
        var recorder = new SnapBackRecorder();
        var feed = new Feeder(recorder);

        feed.Hold(new StickPoint(0.95, 0), StickPoint.Zero, 200);
        feed.Hold(new StickPoint(0.42, 0), StickPoint.Zero, 600);

        Assert.Equal(0, recorder.LeftCount);
    }

    [Fact]
    public void Both_sticks_are_tracked_independently_until_complete()
    {
        var recorder = new SnapBackRecorder(targetReleasesPerStick: 3);
        var feed = new Feeder(recorder);

        for (var i = 0; i < 3; i++)
        {
            feed.Hold(new StickPoint(0.9, 0.3), new StickPoint(-0.2, -0.97), 150);
            feed.Hold(new StickPoint(0.01, 0), new StickPoint(0.02, -0.12), 250);
        }

        Assert.Equal(3, recorder.LeftCount);
        Assert.Equal(3, recorder.RightCount);
        Assert.True(recorder.IsComplete);
        Assert.All(recorder.ToCapture().RightSettles, p => Assert.Equal(-0.12, p.Y, 3));
    }
}
