using System;
using System.Text.Json.Serialization;

namespace SnugWin.Core.PhoneScreen;

public readonly record struct PointD(double X, double Y)
{
    public static readonly PointD Zero = new(0, 0);

    public double DistanceTo(PointD other) => MathEx.Hypot(X - other.X, Y - other.Y);
}

public readonly record struct SizeD(double Width, double Height)
{
    public static readonly SizeD Zero = new(0, 0);
}

public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public static readonly RectD Zero = new(0, 0, 0, 0);

    [JsonIgnore]
    public double MinX => X;
    [JsonIgnore]
    public double MinY => Y;
    [JsonIgnore]
    public double MaxX => X + Width;
    [JsonIgnore]
    public double MaxY => Y + Height;
    [JsonIgnore]
    public double MidX => X + Width / 2.0;
    [JsonIgnore]
    public double MidY => Y + Height / 2.0;
    [JsonIgnore]
    public SizeD Size => new(Width, Height);
    [JsonIgnore]
    public PointD Center => new(MidX, MidY);

    public bool Contains(PointD p) =>
        p.X >= MinX && p.X <= MaxX && p.Y >= MinY && p.Y <= MaxY;

    public bool Matches(RectD other, double tolerance = 2.0) =>
        Math.Abs(MinX - other.MinX) < tolerance
        && Math.Abs(MinY - other.MinY) < tolerance
        && Math.Abs(Width - other.Width) < tolerance
        && Math.Abs(Height - other.Height) < tolerance;

    public string Short => $"({(int)MinX},{(int)MinY} {(int)Width}x{(int)Height})";
}

public static class MathEx
{
    public static int Clamp(this int value, int min, int max) => Math.Clamp(value, min, max);
    public static double Clamp(this double value, double min, double max) => Math.Clamp(value, min, max);
    public static double Hypot(double x, double y) => Math.Sqrt(x * x + y * y);
}
