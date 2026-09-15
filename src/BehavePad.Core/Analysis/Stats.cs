using BehavePad.Core.Input;

namespace BehavePad.Core.Analysis;

internal static class Stats
{
    public static double Percentile(IEnumerable<double> values, double percentile)
    {
        var sorted = values.ToArray();
        if (sorted.Length == 0)
        {
            return 0;
        }

        Array.Sort(sorted);
        var index = (int)Math.Ceiling(percentile * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    public static StickPoint Mean(IReadOnlyCollection<StickPoint> points)
    {
        if (points.Count == 0)
        {
            return StickPoint.Zero;
        }

        double x = 0, y = 0;
        foreach (var point in points)
        {
            x += point.X;
            y += point.Y;
        }

        return new StickPoint(x / points.Count, y / points.Count);
    }

    /// <summary>Removes near-duplicate points and thins the rest so plots stay light.</summary>
    public static IReadOnlyList<StickPoint> Decimate(IEnumerable<StickPoint> points, int maxPoints, double grid = 0.0025)
    {
        var seen = new HashSet<(long, long)>();
        var unique = new List<StickPoint>();
        foreach (var point in points)
        {
            if (seen.Add(((long)Math.Round(point.X / grid), (long)Math.Round(point.Y / grid))))
            {
                unique.Add(Round(point));
            }
        }

        if (unique.Count <= maxPoints)
        {
            return unique;
        }

        var step = (double)unique.Count / maxPoints;
        var result = new List<StickPoint>(maxPoints);
        for (var i = 0; i < maxPoints; i++)
        {
            result.Add(unique[(int)(i * step)]);
        }

        return result;
    }

    public static double Round(double value) => Math.Round(value, 5);

    public static StickPoint Round(StickPoint point) => new(Round(point.X), Round(point.Y));
}
