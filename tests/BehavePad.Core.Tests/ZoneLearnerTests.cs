using BehavePad.Core.Filtering;
using BehavePad.Core.Input;

namespace BehavePad.Core.Tests;

/// <summary>Plays scripted sessions through a filter that learns its zone, at 500 readings a second.</summary>
public class ZoneLearnerTests
{
    private static readonly StickPoint Landing = new(0.03, -0.1);
    private static readonly StickPoint Crept = new(0.03, 0.12);

    private static FilterProfile LearningProfile() => new()
    {
        LearnZone = true,
        RightStick = new StickFilterSettings { Shape = ZoneShape.Fitted, Outline = [new(0.02, -0.12), new(0.04, -0.08)], OutlineMargin = 0.03 },
    };

    [Fact]
    public void Creep_that_follows_two_separate_releases_is_learned()
    {
        var filter = new InputFilter(LearningProfile());
        var session = new Session(filter);

        session.FlickAndRelease();
        session.Glide(Crept, 6000);
        session.Hold(3000);
        Assert.NotEqual(StickPoint.Zero, session.Output.RightStick);
        session.Grab();
        Assert.Equal(0, filter.Statistics.ZoneGrowths);

        session.FlickAndRelease();
        session.Glide(Crept, 6000);
        session.Hold(3000);

        Assert.True(filter.Statistics.ZoneGrowths > 0);
        Assert.Equal(StickPoint.Zero, session.Output.RightStick);
        Assert.NotEmpty(filter.LearnedPoints(StickSide.Right));
    }

    [Theory]
    [InlineData(6000)]
    [InlineData(30000)]
    public void Holding_a_push_after_a_release_is_never_learned(double holdMs)
    {
        var filter = new InputFilter(LearningProfile());
        var session = new Session(filter);
        var held = new StickPoint(0.03, 0.2);

        for (var i = 0; i < 3; i++)
        {
            session.FlickAndRelease();
            session.Glide(held, 120);
            session.Hold(holdMs);
            Assert.NotEqual(StickPoint.Zero, session.Output.RightStick);
            session.Glide(Landing, 60);
            session.Hold(500);
        }

        Assert.Equal(0, filter.Statistics.ZoneGrowths);
        Assert.Empty(filter.LearnedPoints(StickSide.Right));
    }

    [Fact]
    public void Creep_while_another_control_is_held_is_not_learned()
    {
        var filter = new InputFilter(LearningProfile());
        var session = new Session(filter) { LeftTrigger = 200 };

        for (var i = 0; i < 3; i++)
        {
            session.FlickAndRelease();
            session.Glide(Crept, 6000);
            session.Hold(3000);
            session.Grab();
        }

        Assert.Equal(0, filter.Statistics.ZoneGrowths);
    }

    [Fact]
    public void Creep_without_a_release_first_is_not_learned()
    {
        var filter = new InputFilter(LearningProfile());
        var session = new Session(filter);

        for (var i = 0; i < 3; i++)
        {
            session.Glide(Crept, 6000);
            session.Hold(3000);
            session.Glide(Landing, 6000);
            session.Hold(3000);
        }

        Assert.Equal(0, filter.Statistics.ZoneGrowths);
    }

    [Fact]
    public void Learning_stays_within_its_limits()
    {
        var filter = new InputFilter(LearningProfile());
        var session = new Session(filter);
        var farCreep = new StickPoint(0.03, 0.7);

        for (var i = 0; i < 4; i++)
        {
            session.FlickAndRelease();
            session.Glide(farCreep, 20000);
            session.Hold(3000);
            session.Grab();
        }

        var tested = new StickZone(LearningProfile().Sanitized().RightStick.Outline, 0.03);
        var zone = filter.Zone(StickSide.Right)!;

        Assert.True(filter.Statistics.ZoneGrowths > 0);
        Assert.All(filter.LearnedPoints(StickSide.Right), p => Assert.True(tested.Nearest(p).Distance - tested.Margin <= ZoneLearner.MaxShift + 1e-9));
        Assert.False(zone.Contains(farCreep));
        Assert.True(zone.AreaShare <= FilterProfileBuilder.MaxZoneArea);
    }

    [Fact]
    public void Learned_spots_carry_over_through_the_profile_until_forgotten()
    {
        var filter = new InputFilter(LearningProfile());
        var session = new Session(filter);
        for (var i = 0; i < 2; i++)
        {
            session.FlickAndRelease();
            session.Glide(Crept, 6000);
            session.Hold(3000);
            session.Grab();
        }

        var saved = LearningProfile().WithLearned([], filter.LearnedPoints(StickSide.Right));
        var resting = default(GamepadState).WithStick(StickSide.Right, Crept);

        Assert.Equal(StickPoint.Zero, new InputFilter(saved).Apply(resting, 0).RightStick);
        Assert.Equal(StickPoint.Zero, new InputFilter(saved with { LearnZone = false }).Apply(resting, 0).RightStick);
        Assert.NotEqual(StickPoint.Zero, new InputFilter(saved.WithLearned([], [])).Apply(resting, 0).RightStick);
    }

    private sealed class Session(InputFilter filter)
    {
        private const double Step = 2;
        private double _now;

        public StickPoint Right { get; private set; } = Landing;

        public byte LeftTrigger { get; init; }

        public GamepadState Output { get; private set; }

        public void Hold(double ms) => Glide(Right, ms);

        public void Glide(StickPoint to, double ms)
        {
            var from = Right;
            for (var t = Step; t <= ms + 1e-9; t += Step)
            {
                Right = from + (to - from) * (t / ms);
                Feed();
            }

            Right = to;
        }

        /// <summary>Pushes the stick to the edge and lets its spring bring it back to where the drift starts.</summary>
        public void FlickAndRelease()
        {
            Glide(new StickPoint(0, 0.95), 60);
            Hold(250);
            Glide(Landing, 50);
            Hold(400);
        }

        /// <summary>Takes hold of the stick and moves it away quickly, as a player does.</summary>
        public void Grab()
        {
            Glide(new StickPoint(0.6, Right.Y), 80);
            Hold(300);
        }

        private void Feed()
        {
            _now += Step;
            var raw = new GamepadState(0, 0, StickPoint.ToRaw(Right.X), StickPoint.ToRaw(Right.Y), LeftTrigger, 0, GamepadButtons.None);
            Output = filter.Apply(raw, _now);
        }
    }
}
