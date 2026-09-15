using System.Windows;
using System.Windows.Media;
using BehavePad.Branding;
using BehavePad.Core.Input;

namespace BehavePad.Controls;

/// <summary>
/// The BehavePad mascot, alive. Its eyes follow the thumbsticks, so drift makes them wander.
/// The halo glows when the filter is protecting games.
/// </summary>
public sealed class PadMascot : FrameworkElement
{
    public static readonly DependencyProperty LeftStickProperty = Register(nameof(LeftStick), typeof(StickPoint), StickPoint.Zero);
    public static readonly DependencyProperty RightStickProperty = Register(nameof(RightStick), typeof(StickPoint), StickPoint.Zero);
    public static readonly DependencyProperty HaloOnProperty = Register(nameof(HaloOn), typeof(bool), true);
    public static readonly DependencyProperty HappyProperty = Register(nameof(Happy), typeof(bool), true);
    public static readonly DependencyProperty IsPressedProperty = Register(nameof(IsPressed), typeof(bool), false);

    /// <summary>Amplifies small stick offsets so a few percent of drift is visible in the eyes.</summary>
    public static readonly DependencyProperty LookGainProperty = Register(nameof(LookGain), typeof(double), 1.0);

    private static readonly Brush GlowBrush = Frozen(new RadialGradientBrush(Color.FromArgb(0x60, 0xFF, 0xD1, 0x66), Color.FromArgb(0, 0xFF, 0xD1, 0x66)));
    private static readonly Brush PressedBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)));

    public StickPoint LeftStick
    {
        get => (StickPoint)GetValue(LeftStickProperty);
        set => SetValue(LeftStickProperty, value);
    }

    public StickPoint RightStick
    {
        get => (StickPoint)GetValue(RightStickProperty);
        set => SetValue(RightStickProperty, value);
    }

    public bool HaloOn
    {
        get => (bool)GetValue(HaloOnProperty);
        set => SetValue(HaloOnProperty, value);
    }

    public bool Happy
    {
        get => (bool)GetValue(HappyProperty);
        set => SetValue(HappyProperty, value);
    }

    public bool IsPressed
    {
        get => (bool)GetValue(IsPressedProperty);
        set => SetValue(IsPressedProperty, value);
    }

    public double LookGain
    {
        get => (double)GetValue(LookGainProperty);
        set => SetValue(LookGainProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = Math.Min(
            double.IsInfinity(availableSize.Width) ? 200 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 200 : availableSize.Height);
        return new Size(size, size);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size < 16)
        {
            return;
        }

        var scale = size / BrandArt.Size;
        dc.PushTransform(new TranslateTransform((ActualWidth - size) / 2, (ActualHeight - size) / 2));
        dc.PushTransform(new ScaleTransform(scale, scale));

        if (HaloOn)
        {
            dc.DrawEllipse(GlowBrush, null, BrandArt.HaloCenter, BrandArt.HaloRadiusX * 1.6, BrandArt.HaloRadiusY * 3.2);
        }

        var gain = LookGain;
        BrandArt.DrawMark(
            dc,
            new Vector(LeftStick.X * gain, LeftStick.Y * gain),
            new Vector(RightStick.X * gain, RightStick.Y * gain),
            Happy,
            HaloOn ? 1.0 : 0.22);

        if (IsPressed)
        {
            dc.DrawGeometry(PressedBrush, null, BrandArt.BodyGeometry);
        }

        dc.Pop();
        dc.Pop();
    }

    private static DependencyProperty Register(string name, Type type, object defaultValue) =>
        DependencyProperty.Register(name, type, typeof(PadMascot), new FrameworkPropertyMetadata(defaultValue, FrameworkPropertyMetadataOptions.AffectsRender));

    private static T Frozen<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
