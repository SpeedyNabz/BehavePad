using BehavePad.Core.Filtering;
using BehavePad.Core.Input;

namespace BehavePad.Core.Tests;

public class StickMathTests
{
    [Theory]
    [InlineData(1.0, 0.2, 1.0)]
    [InlineData(-1.0, 0.2, -1.0)]
    [InlineData(0.2, 0.2, 0.0)]
    [InlineData(0.6, 0.2, 0.5)]
    [InlineData(-0.4, 0.2, -0.5)]
    [InlineData(-1.0, -0.3, -1.0)]
    public void Recentering_keeps_both_edges_at_full_range(double value, double center, double expected)
    {
        Assert.Equal(expected, StickMath.RecenterAxis(value, center), 6);
    }

    [Fact]
    public void Radial_deadzone_outputs_nothing_inside_the_radius()
    {
        Assert.Equal(StickPoint.Zero, StickMath.ScaleRadial(new StickPoint(0.05, 0.05), 0.1, 1.0));
    }

    [Fact]
    public void Radial_deadzone_reaches_full_output_at_the_outer_edge()
    {
        var result = StickMath.ScaleRadial(new StickPoint(0.97, 0), 0.1, 0.97);
        Assert.Equal(1.0, result.X, 6);
        Assert.Equal(0.0, result.Y, 6);
    }

    [Fact]
    public void Radial_deadzone_preserves_direction_and_ramps_from_zero()
    {
        var input = new StickPoint(0.3, -0.4);
        var result = StickMath.ScaleRadial(input, 0.1, 1.0);
        Assert.Equal(Math.Atan2(input.Y, input.X), Math.Atan2(result.Y, result.X), 6);
        Assert.Equal((0.5 - 0.1) / 0.9, result.Magnitude, 6);
    }
}
