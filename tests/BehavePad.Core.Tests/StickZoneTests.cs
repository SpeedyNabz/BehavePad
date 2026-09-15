using BehavePad.Core.Filtering;
using BehavePad.Core.Input;

namespace BehavePad.Core.Tests;

public class StickZoneTests
{
    private static readonly StickZone Capsule = new([new StickPoint(0, -0.1), new StickPoint(0.04, 0.6)], 0.04);

    [Fact]
    public void Points_along_a_line_make_a_two_corner_outline()
    {
        var hull = StickZone.ConvexHull([new(0, -0.1), new(0, 0.3), new(0, 0.1), new(0, -0.1)]);

        Assert.Equal([new StickPoint(0, -0.1), new StickPoint(0, 0.3)], hull);
    }

    [Fact]
    public void Outline_keeps_only_the_outer_corners_counterclockwise()
    {
        var hull = StickZone.ConvexHull([new(0, 0), new(1, 0), new(1, 1), new(0, 1), new(0.5, 0.5), new(0.5, 0)]);

        Assert.Equal([new StickPoint(0, 0), new StickPoint(1, 0), new StickPoint(1, 1), new StickPoint(0, 1)], hull);
    }

    [Fact]
    public void Nearest_point_is_on_the_outline_or_the_point_itself_when_inside()
    {
        var zone = new StickZone([new(0, 0), new(0.2, 0), new(0.2, 0.2), new(0, 0.2)], 0.05);

        Assert.Equal(0, zone.Nearest(new StickPoint(0.1, 0.1)).Distance);

        var (edge, edgeDistance) = zone.Nearest(new StickPoint(0.5, 0.1));
        Assert.Equal(0.2, edge.X, 9);
        Assert.Equal(0.1, edge.Y, 9);
        Assert.Equal(0.3, edgeDistance, 9);

        var (corner, cornerDistance) = zone.Nearest(new StickPoint(0.5, 0.6));
        Assert.Equal(new StickPoint(0.2, 0.2), corner);
        Assert.Equal(0.5, cornerDistance, 9);

        Assert.True(zone.Contains(new StickPoint(0.24, 0.1)));
        Assert.False(zone.Contains(new StickPoint(0.26, 0.1)));
    }

    [Fact]
    public void Area_share_matches_random_sampling()
    {
        var zone = new StickZone([new(0.05, -0.12), new(0.04, 0.68), new(-0.02, 0.3)], 0.04);
        var random = new Random(7);
        int inside = 0, total = 0;
        while (total < 200_000)
        {
            var point = new StickPoint(random.NextDouble() * 2 - 1, random.NextDouble() * 2 - 1);
            if (point.Magnitude > 1)
            {
                continue;
            }

            total++;
            if (zone.Contains(point))
            {
                inside++;
            }
        }

        Assert.InRange(zone.AreaShare - (double)inside / total, -0.003, 0.003);
    }

    [Fact]
    public void Simplified_outline_still_covers_every_corner_once_the_growth_is_added()
    {
        var ring = Enumerable.Range(0, 100)
            .Select(i => new StickPoint(0.1 * Math.Cos(i * Math.Tau / 100), 0.1 * Math.Sin(i * Math.Tau / 100)))
            .ToArray();

        var simplified = StickZone.Simplify(StickZone.ConvexHull(ring), 12, out var growth);
        var zone = new StickZone(simplified, growth + 1e-9);

        Assert.Equal(12, simplified.Length);
        Assert.True(growth > 0);
        Assert.All(ring, p => Assert.True(zone.Contains(p), $"{p} is uncovered"));
    }

    [Fact]
    public void Every_push_ramps_from_zero_at_the_zone_edge_to_full_at_the_outer_deadzone()
    {
        const double outer = 0.97;
        for (var i = 0; i < 64; i++)
        {
            var angle = i * Math.Tau / 64;
            var rim = new StickPoint(Math.Cos(angle), Math.Sin(angle)) * outer;
            var (nearest, distance) = Capsule.Nearest(rim);
            var direction = (rim - nearest) * (1 / distance);

            StickPoint At(double travel)
            {
                var point = nearest + direction * travel;
                var (n, d) = Capsule.Nearest(point);
                return Capsule.Output(point, n, d, outer);
            }

            Assert.Equal(StickPoint.Zero, At(Capsule.Margin - 0.001));
            Assert.InRange(At(Capsule.Margin + 0.001).Magnitude, 0, 0.01);
            Assert.Equal(0.5, At((Capsule.Margin + distance) / 2).Magnitude, 3);
            Assert.True(At(distance).Magnitude >= 0.999, $"push at {angle:0.00} rad only reaches {At(distance).Magnitude}");
        }
    }

    [Fact]
    public void Output_moves_smoothly_as_the_stick_circles_the_zone()
    {
        var zone = new StickZone([new(0, -0.1), new(0.04, 0.6), new(-0.03, 0.2)], 0.04);
        StickPoint? previous = null;
        for (var i = 0; i <= 3600; i++)
        {
            var angle = i * Math.Tau / 3600;
            var point = new StickPoint(0.05 + 0.5 * Math.Cos(angle), 0.25 + 0.5 * Math.Sin(angle));
            var (nearest, distance) = zone.Nearest(point);
            var output = zone.Output(point, nearest, distance, 0.97);
            if (previous is { } last)
            {
                Assert.True(output.DistanceTo(last) < 0.01, $"output jumped {output.DistanceTo(last):0.000} at step {i}");
            }

            previous = output;
        }
    }
}
