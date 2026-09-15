using BehavePad.Core.Input;

namespace BehavePad.Core.Filtering;

/// <summary>
/// A fitted ignore zone: the convex outline around every spot a drifting stick was seen, grown by a margin.
/// The outline stays convex so its nearest point moves smoothly with the stick, which keeps output free of jumps.
/// Distances use normalized stick units where 1 is full deflection.
/// </summary>
public sealed class StickZone
{
    /// <summary>Shortest ramp from the zone edge to full output, so a zone near the gate never turns into a switch.</summary>
    public const double MinRange = 0.05;

    private const double Epsilon = 1e-12;

    private readonly StickPoint[] _hull;

    public StickZone(IEnumerable<StickPoint> points, double margin)
    {
        ArgumentNullException.ThrowIfNull(points);
        _hull = ConvexHull(points);
        if (_hull.Length == 0)
        {
            _hull = [StickPoint.Zero];
        }

        Margin = Math.Max(0, double.IsFinite(margin) ? margin : 0);
        AreaShare = Math.Min(Area(_hull, Margin) / Math.PI, 1);
    }

    /// <summary>Corners of the outline, counterclockwise. One corner makes a circle and two make a capsule.</summary>
    public IReadOnlyList<StickPoint> Hull => _hull;

    public double Margin { get; }

    /// <summary>Share of the stick's round gate that the zone covers.</summary>
    public double AreaShare { get; }

    /// <summary>Closest point on the outline and the distance to it. A point inside the outline is its own nearest point.</summary>
    public (StickPoint Point, double Distance) Nearest(StickPoint point)
    {
        var hull = _hull;
        if (hull.Length == 1)
        {
            return (hull[0], point.DistanceTo(hull[0]));
        }

        if (hull.Length == 2)
        {
            var closest = ClosestOnSegment(point, hull[0], hull[1]);
            return (closest, point.DistanceTo(closest));
        }

        var inside = true;
        var best = hull[0];
        var bestDistance = double.MaxValue;
        for (var i = 0; i < hull.Length; i++)
        {
            var a = hull[i];
            var b = hull[(i + 1) % hull.Length];
            if (Cross(a, b, point) < 0)
            {
                inside = false;
            }

            var closest = ClosestOnSegment(point, a, b);
            var distance = point.DistanceTo(closest);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = closest;
            }
        }

        return inside ? (point, 0) : (best, bestDistance);
    }

    public bool Contains(StickPoint point) => Nearest(point).Distance <= Margin;

    /// <summary>The same zone stretched to also cover <paramref name="point"/>, or this zone when it already does.</summary>
    public StickZone With(StickPoint point) =>
        Nearest(point).Distance <= 1e-9 ? this : new StickZone(_hull.Append(point), Margin);

    /// <summary>
    /// Output for a stick <paramref name="distance"/> away from its <paramref name="nearest"/> outline point. It points
    /// away from that point and ramps from zero at the zone edge to full where the push meets the outer deadzone,
    /// so every direction still reaches full deflection.
    /// </summary>
    public StickPoint Output(StickPoint point, StickPoint nearest, double distance, double outerDeadzone)
    {
        if (distance <= Margin || distance <= 1e-9)
        {
            return StickPoint.Zero;
        }

        var ux = (point.X - nearest.X) / distance;
        var uy = (point.Y - nearest.Y) / distance;

        // Travel from the nearest outline point, along the push, to the outer deadzone ring.
        var along = nearest.X * ux + nearest.Y * uy;
        var squared = nearest.X * nearest.X + nearest.Y * nearest.Y;
        var reach = -along + Math.Sqrt(Math.Max(along * along - squared + outerDeadzone * outerDeadzone, 0));

        var range = Math.Max(reach - Margin, MinRange);
        var scaled = Math.Min((distance - Margin) / range, Math.Sqrt(2));
        return new StickPoint(Math.Clamp(ux * scaled, -1, 1), Math.Clamp(uy * scaled, -1, 1));
    }

    /// <summary>Corners of the smallest convex outline around the points, counterclockwise, without repeats.</summary>
    public static StickPoint[] ConvexHull(IEnumerable<StickPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        var sorted = points
            .Where(p => double.IsFinite(p.X) && double.IsFinite(p.Y))
            .Distinct()
            .OrderBy(p => p.X)
            .ThenBy(p => p.Y)
            .ToArray();
        if (sorted.Length <= 2)
        {
            return sorted;
        }

        // Andrew's monotone chain: build the lower edge left to right, then the upper edge back.
        var hull = new StickPoint[sorted.Length * 2];
        var count = 0;
        foreach (var point in sorted)
        {
            while (count >= 2 && Cross(hull[count - 2], hull[count - 1], point) <= Epsilon)
            {
                count--;
            }

            hull[count++] = point;
        }

        for (int i = sorted.Length - 2, lower = count + 1; i >= 0; i--)
        {
            while (count >= lower && Cross(hull[count - 2], hull[count - 1], sorted[i]) <= Epsilon)
            {
                count--;
            }

            hull[count++] = sorted[i];
        }

        return hull[..(count - 1)];
    }

    /// <summary>
    /// Drops the corners that matter least until at most <paramref name="maxCorners"/> remain. The result sits inside
    /// the original, and <paramref name="growth"/> is how far the original reaches past it. Add that to the margin
    /// to keep covering everything.
    /// </summary>
    public static StickPoint[] Simplify(IReadOnlyList<StickPoint> hull, int maxCorners, out double growth)
    {
        ArgumentNullException.ThrowIfNull(hull);
        var corners = hull.ToList();
        while (corners.Count > Math.Max(maxCorners, 3))
        {
            var drop = 0;
            var smallest = double.MaxValue;
            for (var i = 0; i < corners.Count; i++)
            {
                var previous = corners[(i + corners.Count - 1) % corners.Count];
                var next = corners[(i + 1) % corners.Count];
                var height = corners[i].DistanceTo(ClosestOnSegment(corners[i], previous, next));
                if (height < smallest)
                {
                    smallest = height;
                    drop = i;
                }
            }

            corners.RemoveAt(drop);
        }

        var simplified = corners.ToArray();
        if (simplified.Length == hull.Count)
        {
            growth = 0;
            return simplified;
        }

        var zone = new StickZone(simplified, 0);
        growth = hull.Max(p => zone.Nearest(p).Distance);
        return zone._hull;
    }

    /// <summary>Pulls the outline toward its middle until the zone covers no more than <paramref name="maxShare"/> of the stick.</summary>
    public static StickPoint[] ShrinkToArea(IReadOnlyList<StickPoint> hull, double margin, double maxShare)
    {
        ArgumentNullException.ThrowIfNull(hull);
        if (hull.Count == 0 || Area(hull, margin) / Math.PI <= maxShare)
        {
            return hull.ToArray();
        }

        var middle = new StickPoint(hull.Average(p => p.X), hull.Average(p => p.Y));
        StickPoint[] Scaled(double factor) => hull.Select(p => middle + (p - middle) * factor).ToArray();

        double low = 0, high = 1;
        for (var i = 0; i < 40; i++)
        {
            var factor = (low + high) / 2;
            if (Area(Scaled(factor), margin) / Math.PI <= maxShare)
            {
                low = factor;
            }
            else
            {
                high = factor;
            }
        }

        return ConvexHull(Scaled(low));
    }

    /// <summary>Area of a convex outline grown by a margin: the polygon, a strip along each edge, and a round cap at the corners.</summary>
    public static double Area(IReadOnlyList<StickPoint> hull, double margin)
    {
        var (polygon, perimeter) = Measure(hull);
        return polygon + perimeter * margin + Math.PI * margin * margin;
    }

    /// <summary>Enclosed area and perimeter of a convex outline. Two corners count as a flat shape walked there and back.</summary>
    internal static (double Polygon, double Perimeter) Measure(IReadOnlyList<StickPoint> hull)
    {
        if (hull.Count < 2)
        {
            return (0, 0);
        }

        double twiceArea = 0, perimeter = 0;
        for (var i = 0; i < hull.Count; i++)
        {
            var a = hull[i];
            var b = hull[(i + 1) % hull.Count];
            twiceArea += a.X * b.Y - b.X * a.Y;
            perimeter += a.DistanceTo(b);
        }

        return (Math.Abs(twiceArea) / 2, perimeter);
    }

    private static double Cross(StickPoint origin, StickPoint a, StickPoint b) =>
        (a.X - origin.X) * (b.Y - origin.Y) - (a.Y - origin.Y) * (b.X - origin.X);

    private static StickPoint ClosestOnSegment(StickPoint point, StickPoint a, StickPoint b)
    {
        var abX = b.X - a.X;
        var abY = b.Y - a.Y;
        var lengthSquared = abX * abX + abY * abY;
        if (lengthSquared <= Epsilon)
        {
            return a;
        }

        var t = Math.Clamp(((point.X - a.X) * abX + (point.Y - a.Y) * abY) / lengthSquared, 0, 1);
        return new StickPoint(a.X + abX * t, a.Y + abY * t);
    }
}
