using System.Text.Json.Serialization;

namespace BehavePad.Core.Input;

/// <summary>A normalized thumbstick position. Each axis spans -1 (full left or down) to 1 (full right or up).</summary>
public readonly record struct StickPoint(double X, double Y)
{
    /// <summary>Largest positive value an XInput thumbstick axis reports.</summary>
    public const double RawScale = 32767.0;

    public static StickPoint Zero => default;

    [JsonIgnore]
    public double Magnitude => Math.Sqrt(X * X + Y * Y);

    public double DistanceTo(StickPoint other)
    {
        var dx = X - other.X;
        var dy = Y - other.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public static StickPoint FromRaw(short x, short y) => new(Normalize(x), Normalize(y));

    public static double Normalize(short value) => Math.Clamp(value / RawScale, -1.0, 1.0);

    public static short ToRaw(double value) => (short)Math.Round(Math.Clamp(value, -1.0, 1.0) * RawScale);

    public static StickPoint operator +(StickPoint a, StickPoint b) => new(a.X + b.X, a.Y + b.Y);

    public static StickPoint operator -(StickPoint a, StickPoint b) => new(a.X - b.X, a.Y - b.Y);

    public static StickPoint operator *(StickPoint a, double scale) => new(a.X * scale, a.Y * scale);
}
