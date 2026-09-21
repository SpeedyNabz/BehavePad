using System.Windows;
using System.Windows.Media;
using BehavePad.Core.Input;

namespace BehavePad.Controls;

/// <summary>
/// Draws a thumbstick's range as a circle, with the raw position, what games receive,
/// the filter deadzone, and test measurements such as the rest cloud and snap-back points.
/// </summary>
public sealed class StickPlot : FrameworkElement
{
    private const int TrailLength = 28;

    public static readonly DependencyProperty RawPointProperty = Register(nameof(RawPoint), typeof(StickPoint), StickPoint.Zero, OnRawPointChanged);
    public static readonly DependencyProperty OutputPointProperty = Register(nameof(OutputPoint), typeof(StickPoint), StickPoint.Zero);
    public static readonly DependencyProperty ShowOutputProperty = Register(nameof(ShowOutput), typeof(bool), false);
    public static readonly DependencyProperty ShowRawProperty = Register(nameof(ShowRaw), typeof(bool), true);
    public static readonly DependencyProperty CenterProperty = Register(nameof(Center), typeof(StickPoint), StickPoint.Zero);
    public static readonly DependencyProperty DeadzoneProperty = Register(nameof(Deadzone), typeof(double), 0.0);
    public static readonly DependencyProperty RestPointsProperty = Register(nameof(RestPoints), typeof(IReadOnlyList<StickPoint>), null);
    public static readonly DependencyProperty SettlePointsProperty = Register(nameof(SettlePoints), typeof(IReadOnlyList<StickPoint>), null);
    public static readonly DependencyProperty OutlineProperty = Register(nameof(Outline), typeof(IReadOnlyList<StickPoint>), null);
    public static readonly DependencyProperty OutlineMarginProperty = Register(nameof(OutlineMargin), typeof(double), 0.0);
    public static readonly DependencyProperty GrownOutlineProperty = Register(nameof(GrownOutline), typeof(IReadOnlyList<StickPoint>), null);
    public static readonly DependencyProperty ZoomProperty = Register(nameof(Zoom), typeof(double), 1.0);
    public static readonly DependencyProperty ShowTrailProperty = Register(nameof(ShowTrail), typeof(bool), true);

    private static readonly Brush PlateBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x0F, 0x16, 0x1E)));
    private static readonly Pen PlatePen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x26, 0x33, 0x3F))), 1.5));
    private static readonly Pen GridPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x1C, 0x27, 0x33))), 1));
    private static readonly Pen RingPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x19, 0x23, 0x2D))), 1) { DashStyle = DashStyles.Dash });
    private static readonly Color Mint = Color.FromRgb(0x3E, 0xE6, 0xA8);
    private static readonly Color Coral = Color.FromRgb(0xFF, 0x6B, 0x6B);
    private static readonly Color Gold = Color.FromRgb(0xFF, 0xD1, 0x66);
    private static readonly Brush DeadzoneFill = Frozen(new SolidColorBrush(Color.FromArgb(0x26, 0x3E, 0xE6, 0xA8)));
    private static readonly Pen DeadzonePen = Frozen(new Pen(Frozen(new SolidColorBrush(Mint)), 1.6) { DashStyle = new DashStyle([4, 3], 0) });
    private static readonly Brush GrownFill = Frozen(new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xD1, 0x66)));
    private static readonly Pen GrownPen = Frozen(new Pen(Frozen(new SolidColorBrush(Gold)), 1.4) { DashStyle = new DashStyle([2, 3], 0) });
    private static readonly Brush RestBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0x6B, 0x6B)));
    private static readonly Brush SettleBrush = Frozen(new SolidColorBrush(Gold));
    private static readonly Pen SettlePen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x0B, 0x10, 0x16))), 1.5));
    private static readonly Brush RawBrush = Frozen(new SolidColorBrush(Coral));
    private static readonly Pen RawRingPen = Frozen(new Pen(Frozen(new SolidColorBrush(Coral)), 2.2));
    private static readonly Brush OutputBrush = Frozen(new SolidColorBrush(Mint));
    private static readonly Brush OutputGlow = Frozen(new RadialGradientBrush(Color.FromArgb(0x88, 0x3E, 0xE6, 0xA8), Color.FromArgb(0, 0x3E, 0xE6, 0xA8)));
    private static readonly Pen CenterPen = Frozen(new Pen(Frozen(new SolidColorBrush(Mint)), 1.5));
    private static readonly Brush ZoomTextBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x7C, 0x8F, 0xA5)));

    private readonly Queue<StickPoint> _trail = new();

    public StickPoint RawPoint
    {
        get => (StickPoint)GetValue(RawPointProperty);
        set => SetValue(RawPointProperty, value);
    }

    public StickPoint OutputPoint
    {
        get => (StickPoint)GetValue(OutputPointProperty);
        set => SetValue(OutputPointProperty, value);
    }

    public bool ShowOutput
    {
        get => (bool)GetValue(ShowOutputProperty);
        set => SetValue(ShowOutputProperty, value);
    }

    public bool ShowRaw
    {
        get => (bool)GetValue(ShowRawProperty);
        set => SetValue(ShowRawProperty, value);
    }

    public StickPoint Center
    {
        get => (StickPoint)GetValue(CenterProperty);
        set => SetValue(CenterProperty, value);
    }

    public double Deadzone
    {
        get => (double)GetValue(DeadzoneProperty);
        set => SetValue(DeadzoneProperty, value);
    }

    /// <summary>Corners of a fitted ignore zone. When set, the plot draws this zone instead of the round deadzone.</summary>
    public IReadOnlyList<StickPoint>? Outline
    {
        get => (IReadOnlyList<StickPoint>?)GetValue(OutlineProperty);
        set => SetValue(OutlineProperty, value);
    }

    public double OutlineMargin
    {
        get => (double)GetValue(OutlineMarginProperty);
        set => SetValue(OutlineMarginProperty, value);
    }

    /// <summary>Corners of the fitted zone including what it learned during play, drawn beneath the tested zone.</summary>
    public IReadOnlyList<StickPoint>? GrownOutline
    {
        get => (IReadOnlyList<StickPoint>?)GetValue(GrownOutlineProperty);
        set => SetValue(GrownOutlineProperty, value);
    }

    public IReadOnlyList<StickPoint>? RestPoints
    {
        get => (IReadOnlyList<StickPoint>?)GetValue(RestPointsProperty);
        set => SetValue(RestPointsProperty, value);
    }

    public IReadOnlyList<StickPoint>? SettlePoints
    {
        get => (IReadOnlyList<StickPoint>?)GetValue(SettlePointsProperty);
        set => SetValue(SettlePointsProperty, value);
    }

    /// <summary>Visible stick range. 1 shows full travel, 0.25 zooms in four times to reveal small drift.</summary>
    public double Zoom
    {
        get => (double)GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    public bool ShowTrail
    {
        get => (bool)GetValue(ShowTrailProperty);
        set => SetValue(ShowTrailProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = Math.Min(
            double.IsInfinity(availableSize.Width) ? 220 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 220 : availableSize.Height);
        return new Size(size, size);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size < 20)
        {
            return;
        }

        var origin = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = size / 2 - 4;
        var zoom = Math.Clamp(Zoom, 0.05, 1.0);
        var scale = radius / zoom;

        dc.DrawEllipse(PlateBrush, PlatePen, origin, radius, radius);
        dc.PushClip(new EllipseGeometry(origin, radius - 1, radius - 1));

        dc.DrawLine(GridPen, new Point(origin.X - radius, origin.Y), new Point(origin.X + radius, origin.Y));
        dc.DrawLine(GridPen, new Point(origin.X, origin.Y - radius), new Point(origin.X, origin.Y + radius));
        dc.DrawEllipse(null, RingPen, origin, radius * 0.5, radius * 0.5);

        if (Outline is { Count: > 0 } outline)
        {
            if (GrownOutline is { Count: > 0 } grown)
            {
                dc.DrawGeometry(GrownFill, GrownPen, ZoneGeometry(grown, OutlineMargin, origin, scale));
            }

            dc.DrawGeometry(DeadzoneFill, DeadzonePen, ZoneGeometry(outline, OutlineMargin, origin, scale));
        }
        else if (Deadzone > 0)
        {
            var center = ToScreen(Center, origin, scale, radius * 4);
            dc.DrawEllipse(DeadzoneFill, DeadzonePen, center, Deadzone * scale, Deadzone * scale);
            dc.DrawLine(CenterPen, center + new Vector(-5, 0), center + new Vector(5, 0));
            dc.DrawLine(CenterPen, center + new Vector(0, -5), center + new Vector(0, 5));
        }

        if (RestPoints is { Count: > 0 } rest)
        {
            var dot = Math.Max(1.6, size / 150);
            foreach (var point in rest)
            {
                dc.DrawEllipse(RestBrush, null, ToScreen(point, origin, scale, radius), dot, dot);
            }
        }

        if (SettlePoints is { Count: > 0 } settles)
        {
            foreach (var point in settles)
            {
                dc.DrawEllipse(SettleBrush, SettlePen, ToScreen(point, origin, scale, radius), 4.5, 4.5);
            }
        }

        if (ShowRaw && ShowTrail && _trail.Count > 1)
        {
            var index = 0;
            Point? previous = null;
            foreach (var point in _trail)
            {
                var screen = ToScreen(point, origin, scale, radius);
                if (previous is { } from)
                {
                    var alpha = (byte)(20 + 150 * index / _trail.Count);
                    dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(alpha, Coral.R, Coral.G, Coral.B)), 2), from, screen);
                }

                previous = screen;
                index++;
            }
        }

        dc.Pop();

        if (ShowRaw)
        {
            var raw = ToScreen(RawPoint, origin, scale, radius);
            if (ShowOutput)
            {
                dc.DrawEllipse(null, RawRingPen, raw, 6.5, 6.5);
            }
            else
            {
                dc.DrawEllipse(RawBrush, null, raw, 6.5, 6.5);
            }
        }

        if (ShowOutput)
        {
            var output = ToScreen(OutputPoint, origin, scale, radius);
            dc.DrawEllipse(OutputGlow, null, output, 16, 16);
            dc.DrawEllipse(OutputBrush, null, output, 6, 6);
        }

        if (zoom < 0.99)
        {
            var text = new FormattedText(
                $"{1 / zoom:0.#}× zoom",
                System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                11,
                ZoomTextBrush,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(text, new Point(origin.X - text.Width / 2, origin.Y + radius * 0.62));
        }
    }

    /// <summary>
    /// A fitted zone on screen: every edge of the outline pushed out by the margin, joined by round arcs at the corners.
    /// Each edge's outward side is to its left on screen, and the corner arcs turn counterclockwise.
    /// </summary>
    private static Geometry ZoneGeometry(IReadOnlyList<StickPoint> outline, double margin, Point origin, double scale)
    {
        var radius = Math.Max(margin * scale, 0.5);
        var corners = outline.Select(p => new Point(origin.X + p.X * scale, origin.Y - p.Y * scale)).ToArray();
        if (corners.Length == 1)
        {
            return new EllipseGeometry(corners[0], radius, radius);
        }

        var size = new Size(radius, radius);
        var figure = new PathFigure { IsClosed = true, IsFilled = true };
        for (var i = 0; i < corners.Length; i++)
        {
            var corner = corners[i];
            var next = corners[(i + 1) % corners.Length];
            var arriving = Outward(corners[(i + corners.Length - 1) % corners.Length], corner);
            var leaving = Outward(corner, next);

            // Split every corner arc in two, so the half turn at each end of a capsule is never ambiguous.
            var middle = arriving + leaving;
            if (middle.Length < 1e-6)
            {
                middle = corner - next;
            }

            middle.Normalize();

            var start = corner + arriving * radius;
            if (i == 0)
            {
                figure.StartPoint = start;
            }
            else
            {
                figure.Segments.Add(new LineSegment(start, true));
            }

            figure.Segments.Add(new ArcSegment(corner + middle * radius, size, 0, false, SweepDirection.Counterclockwise, true));
            figure.Segments.Add(new ArcSegment(corner + leaving * radius, size, 0, false, SweepDirection.Counterclockwise, true));
        }

        var geometry = new PathGeometry([figure]);
        geometry.Freeze();
        return geometry;
    }

    private static Vector Outward(Point from, Point to)
    {
        var normal = new Vector(-(to.Y - from.Y), to.X - from.X);
        normal.Normalize();
        return normal;
    }

    private static Point ToScreen(StickPoint point, Point origin, double scale, double limit)
    {
        var vector = new Vector(point.X * scale, -point.Y * scale);
        if (vector.Length > limit)
        {
            vector *= limit / vector.Length;
        }

        return origin + vector;
    }

    private static void OnRawPointChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var plot = (StickPlot)d;
        if (!plot.ShowTrail)
        {
            return;
        }

        plot._trail.Enqueue((StickPoint)e.NewValue);
        while (plot._trail.Count > TrailLength)
        {
            plot._trail.Dequeue();
        }
    }

    private static DependencyProperty Register(string name, Type type, object? defaultValue, PropertyChangedCallback? changed = null) =>
        DependencyProperty.Register(name, type, typeof(StickPlot), new FrameworkPropertyMetadata(defaultValue, FrameworkPropertyMetadataOptions.AffectsRender, changed));

    private static T Frozen<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
