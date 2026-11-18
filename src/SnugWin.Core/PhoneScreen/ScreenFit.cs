using System;
using System.Collections.Generic;
using SnugWin.Core.Touch;

namespace SnugWin.Core.PhoneScreen;

public sealed record ScreenFit : IEquatable<ScreenFit>
{
    public const int DesignWidth = 540;
    public const int MinWidth = 540;
    public const int MinHeight = 800;
    public const int MaxWidth = 1080;
    public const int Step = 10;
    public const double MinRatio = 1.0;
    public const double MaxRatio = 2.0;
    public static readonly SizeD MaxPixels = new(MaxWidth * 2, (int)(MaxWidth * MaxRatio) * 2);

    private const double UuInset = 91.0;
    private const double AssumedPoints = 400.0;
    private const double BaseHomeBarHeight = 28.0;

    public double Ratio { get; }
    public int BaseWidth { get; }
    public IReadOnlyList<int> Widths { get; }

    public static readonly ScreenFit Standard = FromPhone(PhoneChoice.Standard.Screen);

    public ScreenFit(double ratio)
    {
        Ratio = ratio.Clamp(MinRatio, MaxRatio);
        var widths = new List<int>();
        int? baseWidth = null;
        for (int w = MinWidth; w <= MaxWidth; w += Step)
        {
            if (baseWidth is null && Math.Round(w * Ratio, MidpointRounding.AwayFromZero) >= MinHeight)
                baseWidth = w;
            if (baseWidth is not null)
                widths.Add(w);
        }
        BaseWidth = baseWidth ?? MaxWidth;
        if (widths.Count == 0) widths.Add(MaxWidth);
        Widths = widths;
    }

    public static ScreenFit FromPhone(PhoneScreenSpecData phone)
    {
        double points = phone.Points ?? AssumedPoints;
        return new ScreenFit((double)phone.Height / phone.Width - 2.0 * UuInset / points);
    }

    public SizeD Size(int width) =>
        new(width, Math.Round(width * Ratio, MidpointRounding.AwayFromZero));

    /// <summary>
    /// Physical 2x pixel size for Windows 200% DPI virtual display mode.
    /// </summary>
    public (int PixelWidth, int PixelHeight) PixelSize(int logicalWidth)
    {
        var logical = Size(logicalWidth);
        // Ensure even pixel dimensions for display drivers / encoders
        int pw = (int)logical.Width * 2;
        int ph = (int)logical.Height * 2;
        if (pw % 2 != 0) pw++;
        if (ph % 2 != 0) ph++;
        return (pw, ph);
    }

    public int WidthToFit(SizeD window, SizeD area, int currentWidth)
    {
        const double tolerance = 2.0;
        if (window.Width <= area.Width + tolerance && window.Height <= area.Height + tolerance)
            return currentWidth;

        double needed = Math.Max(
            currentWidth * window.Width / area.Width,
            currentWidth * window.Height / area.Height);
        int target = (int)Math.Ceiling(needed);
        foreach (int w in Widths)
        {
            if (w >= target) return w;
        }
        return MaxWidth;
    }

    public static double Scale(double screenWidth) => screenWidth / DesignWidth;

    public static double HomeBarHeight(double screenWidth) =>
        Math.Round(BaseHomeBarHeight * Scale(screenWidth), MidpointRounding.AwayFromZero);

    public static (double DesignWidth, double DesignHeight, double Scale) ComputePreviewLayout(SizeD workAreaDip, ScreenFit fit)
    {
        var logical = fit.Size(fit.BaseWidth);
        double maxW = Math.Max(300.0, workAreaDip.Width * 0.72);
        double maxH = Math.Max(420.0, workAreaDip.Height * 0.84 - 48.0);
        double scale = Math.Min(0.84, Math.Min(maxW / logical.Width, maxH / logical.Height));
        scale = Math.Max(0.35, scale);
        return (logical.Width, logical.Height, scale);
    }

    public bool Equals(ScreenFit? other) =>
        other is not null && Ratio.Equals(other.Ratio) && BaseWidth == other.BaseWidth;

    public override int GetHashCode() => HashCode.Combine(Ratio, BaseWidth);

    public override string ToString()
    {
        var s = Size(BaseWidth);
        return $"{BaseWidth}x{(int)s.Height}";
    }
}
