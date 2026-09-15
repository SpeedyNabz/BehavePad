using System.Windows;
using System.Windows.Media;

namespace BehavePad.Branding;

/// <summary>
/// The BehavePad mark: a friendly gamepad wearing a halo. Its thumbsticks are its eyes, so when the
/// sticks drift the eyes wander, and when the filter is on they look straight ahead.
/// All art is vector and drawn in a 256 unit square.
/// </summary>
public static class BrandArt
{
    public const double Size = 256;

    public const string BodyPathData =
        "M84,86 L172,86 C206,86 222,104 228,136 L236,178 C241,204 222,220 200,210 C188,204 180,194 172,184 " +
        "L166,176 L90,176 L84,184 C76,194 68,204 56,210 C34,220 15,204 20,178 L28,136 C34,104 50,86 84,86 Z";

    public const string SmilePathData = "M113,151 Q128,166 143,151";
    public const string UneasyMouthPathData = "M114,158 Q121,152 128,158 Q135,164 142,158";

    public const double EyeRadius = 21;
    public const double PupilRadius = 8.5;
    public const double PupilTravel = 9.5;
    public const double HaloRadiusX = 52;
    public const double HaloRadiusY = 14;
    public const double HaloThickness = 11;

    public static readonly Point LeftEye = new(92, 128);
    public static readonly Point RightEye = new(164, 128);
    public static readonly Point HaloCenter = new(128, 50);

    public static readonly Color MintTop = Color.FromRgb(0x62, 0xF4, 0xC2);
    public static readonly Color MintBottom = Color.FromRgb(0x1C, 0xC2, 0x92);
    public static readonly Color Gold = Color.FromRgb(0xFF, 0xD1, 0x66);
    public static readonly Color Ink = Color.FromRgb(0x0B, 0x10, 0x16);
    public static readonly Color Pupil = Color.FromRgb(0xF2, 0xFF, 0xF9);
    public static readonly Color IconTop = Color.FromRgb(0x18, 0x24, 0x31);
    public static readonly Color IconBottom = Color.FromRgb(0x0A, 0x0F, 0x15);

    private static readonly Geometry Body = Frozen(Geometry.Parse(BodyPathData));
    private static readonly Geometry Smile = Frozen(Geometry.Parse(SmilePathData));
    private static readonly Geometry UneasyMouth = Frozen(Geometry.Parse(UneasyMouthPathData));
    private static readonly Brush BodyBrush = Frozen(new LinearGradientBrush(MintTop, MintBottom, new Point(0.5, 0), new Point(0.5, 1)));
    private static readonly Brush InkBrush = Frozen(new SolidColorBrush(Ink));
    private static readonly Brush PupilBrush = Frozen(new SolidColorBrush(Pupil));
    private static readonly Pen MouthPen = Frozen(new Pen(InkBrush, 6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });

    public static DrawingImage MarkImage { get; } = Frozen(new DrawingImage(CreateMark()));

    /// <summary>Bolder version of the mark for sizes under about 64 pixels.</summary>
    public static DrawingImage SmallMarkImage { get; } = Frozen(new DrawingImage(CreateMark(simplified: true)));

    public static DrawingImage AppIconImage { get; } = Frozen(new DrawingImage(CreateAppIcon()));

    public static Pen CreateHaloPen(double opacity = 1) =>
        Frozen(new Pen(Frozen(new SolidColorBrush(Gold) { Opacity = opacity }), HaloThickness));

    /// <summary>The mark on a transparent background.</summary>
    public static DrawingGroup CreateMark(bool simplified = false)
    {
        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, Size, Size));
            DrawMark(dc, default, default, happy: true, haloOpacity: 1, simplified);
        }

        return Frozen(group);
    }

    /// <summary>The mark on the rounded dark tile used for the app icon.</summary>
    public static DrawingGroup CreateAppIcon(bool simplified = false)
    {
        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            var tile = Frozen(new LinearGradientBrush(IconTop, IconBottom, new Point(0.5, 0), new Point(0.5, 1)));
            dc.DrawRoundedRectangle(tile, null, new Rect(0, 0, Size, Size), 58, 58);

            var scale = simplified ? 0.96 : 0.86;
            dc.PushTransform(new MatrixTransform(scale, 0, 0, scale, Size / 2 * (1 - scale), Size / 2 * (1 - scale) + 6));
            DrawMark(dc, default, default, happy: true, haloOpacity: 1, simplified);
            dc.Pop();
        }

        return Frozen(group);
    }

    /// <summary>Draws the whole mark. Look vectors are stick positions from -1 to 1, with Y pointing up.</summary>
    public static void DrawMark(DrawingContext dc, Vector leftLook, Vector rightLook, bool happy, double haloOpacity, bool simplified = false)
    {
        if (haloOpacity > 0)
        {
            var pen = haloOpacity >= 1 ? HaloPen : CreateHaloPen(haloOpacity);
            dc.DrawEllipse(null, simplified ? CreateThickHaloPen() : pen, HaloCenter, HaloRadiusX, HaloRadiusY);
        }

        dc.DrawGeometry(BodyBrush, null, Body);
        DrawFace(dc, leftLook, rightLook, happy, simplified);
    }

    public static void DrawFace(DrawingContext dc, Vector leftLook, Vector rightLook, bool happy, bool simplified = false)
    {
        var pupilRadius = simplified ? PupilRadius * 1.3 : PupilRadius;
        dc.DrawEllipse(InkBrush, null, LeftEye, EyeRadius, EyeRadius);
        dc.DrawEllipse(InkBrush, null, RightEye, EyeRadius, EyeRadius);
        dc.DrawEllipse(PupilBrush, null, LeftEye + Look(leftLook), pupilRadius, pupilRadius);
        dc.DrawEllipse(PupilBrush, null, RightEye + Look(rightLook), pupilRadius, pupilRadius);

        if (!simplified)
        {
            dc.DrawGeometry(null, MouthPen, happy ? Smile : UneasyMouth);
        }
    }

    public static Geometry BodyGeometry => Body;

    private static readonly Pen HaloPen = CreateHaloPen();

    private static Pen CreateThickHaloPen() => Frozen(new Pen(Frozen(new SolidColorBrush(Gold)), HaloThickness * 1.5));

    private static Vector Look(Vector stick)
    {
        var length = stick.Length;
        if (length > 1)
        {
            stick /= length;
        }

        // Screen Y grows downward while stick Y grows upward.
        return new Vector(stick.X * PupilTravel, -stick.Y * PupilTravel);
    }

    private static T Frozen<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
