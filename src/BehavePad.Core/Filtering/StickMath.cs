using BehavePad.Core.Input;

namespace BehavePad.Core.Filtering;

public static class StickMath
{
    /// <summary>
    /// Moves a measured rest center to zero while keeping both physical edges at -1 and 1,
    /// so recentering never costs any stick travel.
    /// </summary>
    public static double RecenterAxis(double value, double center)
    {
        if (value >= center)
        {
            var span = 1 - center;
            return span <= 1e-6 ? 0 : Math.Min((value - center) / span, 1);
        }

        var negativeSpan = 1 + center;
        return negativeSpan <= 1e-6 ? 0 : Math.Max((value - center) / negativeSpan, -1);
    }

    public static StickPoint Recenter(StickPoint point, StickPoint center) =>
        new(RecenterAxis(point.X, center.X), RecenterAxis(point.Y, center.Y));

    /// <summary>
    /// Scaled radial deadzone. Output is zero inside the deadzone, then ramps smoothly from zero
    /// so there is no jump at the edge, and still reaches full deflection at the outer edge.
    /// </summary>
    public static StickPoint ScaleRadial(StickPoint point, double deadzone, double outerDeadzone)
    {
        var magnitude = point.Magnitude;
        if (magnitude <= deadzone || magnitude <= 1e-9)
        {
            return StickPoint.Zero;
        }

        var range = Math.Max(outerDeadzone - deadzone, 1e-6);
        var scaled = Math.Min((magnitude - deadzone) / range, Math.Sqrt(2));
        var factor = scaled / magnitude;
        return new StickPoint(Math.Clamp(point.X * factor, -1, 1), Math.Clamp(point.Y * factor, -1, 1));
    }
}
