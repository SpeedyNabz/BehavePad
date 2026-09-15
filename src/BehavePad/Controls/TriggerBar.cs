using System.Windows;
using System.Windows.Media;

namespace BehavePad.Controls;

/// <summary>Two slim bars: what the trigger physically reports, and what games receive after filtering.</summary>
public sealed class TriggerBar : FrameworkElement
{
    public static readonly DependencyProperty RawValueProperty = Register(nameof(RawValue), 0.0);
    public static readonly DependencyProperty OutputValueProperty = Register(nameof(OutputValue), 0.0);
    public static readonly DependencyProperty DeadzoneProperty = Register(nameof(Deadzone), 0.0);
    public static readonly DependencyProperty ShowOutputProperty = DependencyProperty.Register(
        nameof(ShowOutput), typeof(bool), typeof(TriggerBar), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    private static readonly Brush TrackBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x1B, 0x25, 0x30)));
    private static readonly Brush RawBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)));
    private static readonly Brush OutputBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x3E, 0xE6, 0xA8)));
    private static readonly Brush DeadzoneBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x40, 0x3E, 0xE6, 0xA8)));
    private static readonly Pen DeadzoneEdge = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x3E, 0xE6, 0xA8))), 1.5));

    public double RawValue
    {
        get => (double)GetValue(RawValueProperty);
        set => SetValue(RawValueProperty, value);
    }

    public double OutputValue
    {
        get => (double)GetValue(OutputValueProperty);
        set => SetValue(OutputValueProperty, value);
    }

    public double Deadzone
    {
        get => (double)GetValue(DeadzoneProperty);
        set => SetValue(DeadzoneProperty, value);
    }

    public bool ShowOutput
    {
        get => (bool)GetValue(ShowOutputProperty);
        set => SetValue(ShowOutputProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 160 : availableSize.Width, ShowOutput ? 18 : 8);

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        if (width < 4)
        {
            return;
        }

        const double barHeight = 7;
        DrawBar(dc, 0, width, barHeight, RawValue, RawBrush, Deadzone);
        if (ShowOutput)
        {
            DrawBar(dc, barHeight + 4, width, barHeight, OutputValue, OutputBrush, 0);
        }
    }

    private static void DrawBar(DrawingContext dc, double top, double width, double height, double value, Brush fill, double deadzone)
    {
        var radius = height / 2;
        dc.DrawRoundedRectangle(TrackBrush, null, new Rect(0, top, width, height), radius, radius);

        if (deadzone > 0)
        {
            var zone = Math.Clamp(deadzone, 0, 1) * width;
            dc.DrawRoundedRectangle(DeadzoneBrush, null, new Rect(0, top, zone, height), radius, radius);
            dc.DrawLine(DeadzoneEdge, new Point(zone, top - 2), new Point(zone, top + height + 2));
        }

        var filled = Math.Clamp(value, 0, 1) * width;
        if (filled > 0.5)
        {
            dc.DrawRoundedRectangle(fill, null, new Rect(0, top, Math.Max(filled, height), height), radius, radius);
        }
    }

    private static DependencyProperty Register(string name, double defaultValue) =>
        DependencyProperty.Register(name, typeof(double), typeof(TriggerBar), new FrameworkPropertyMetadata(defaultValue, FrameworkPropertyMetadataOptions.AffectsRender));

    private static T Frozen<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
