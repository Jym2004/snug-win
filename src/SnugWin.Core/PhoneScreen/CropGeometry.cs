using System;

namespace SnugWin.Core.PhoneScreen;

public enum CropHandle
{
    TopLeft,
    Top,
    TopRight,
    Left,
    Right,
    BottomLeft,
    Bottom,
    BottomRight
}

/// <summary>
/// Port of SnugCore CropGeometry.swift: normalized [0..1] crop selection geometry,
/// fitted display rect, handle hit-testing, and bounded resizing.
/// </summary>
public static class CropGeometry
{
    private enum Side { Min, Max }

    public static RectD FittedRect(SizeD image, RectD area)
    {
        if (image.Width <= 0 || image.Height <= 0 || area.Width <= 0 || area.Height <= 0)
            return default;
        double scale = Math.Min(area.Width / image.Width, area.Height / image.Height);
        double w = image.Width * scale;
        double h = image.Height * scale;
        return new RectD(area.MidX - w / 2.0, area.MidY - h / 2.0, w, h);
    }

    public static RectD UnitRect(RectD rect, RectD shown)
    {
        if (shown.Width <= 0 || shown.Height <= 0) return default;
        return new RectD(
            (rect.X - shown.X) / shown.Width,
            (rect.Y - shown.Y) / shown.Height,
            rect.Width / shown.Width,
            rect.Height / shown.Height);
    }

    public static RectD Rect(RectD unit, RectD shown) =>
        new(
            shown.X + unit.X * shown.Width,
            shown.Y + unit.Y * shown.Height,
            unit.Width * shown.Width,
            unit.Height * shown.Height);

    public static RectD PixelRect(RectD unit, SizeD image)
    {
        double minX0 = Math.Max(0.0, unit.X);
        double minY0 = Math.Max(0.0, unit.Y);
        double maxX0 = Math.Min(1.0, unit.MaxX);
        double maxY0 = Math.Min(1.0, unit.MaxY);
        if (maxX0 <= minX0 || maxY0 <= minY0 || image.Width <= 0 || image.Height <= 0)
            return default;

        double minX = Math.Round(minX0 * image.Width, MidpointRounding.AwayFromZero);
        double minY = Math.Round(minY0 * image.Height, MidpointRounding.AwayFromZero);
        double maxX = Math.Round(maxX0 * image.Width, MidpointRounding.AwayFromZero);
        double maxY = Math.Round(maxY0 * image.Height, MidpointRounding.AwayFromZero);
        return new RectD(minX, minY, Math.Max(0, maxX - minX), Math.Max(0, maxY - minY));
    }

    public static CropHandle? HandleAt(PointD point, RectD rect, double radius)
    {
        var expanded = new RectD(
            rect.X - radius,
            rect.Y - radius,
            rect.Width + 2 * radius,
            rect.Height + 2 * radius);
        if (!expanded.Contains(point)) return null;

        var hx = Nearer(point.X, rect.MinX, rect.MaxX, radius);
        var hy = Nearer(point.Y, rect.MinY, rect.MaxY, radius);
        return (hx, hy) switch
        {
            (Side.Min, Side.Min) => CropHandle.TopLeft,
            (Side.Max, Side.Min) => CropHandle.TopRight,
            (Side.Min, Side.Max) => CropHandle.BottomLeft,
            (Side.Max, Side.Max) => CropHandle.BottomRight,
            (Side.Min, null) => CropHandle.Left,
            (Side.Max, null) => CropHandle.Right,
            (null, Side.Min) => CropHandle.Top,
            (null, Side.Max) => CropHandle.Bottom,
            _ => null
        };
    }

    public static RectD Resized(RectD rect, CropHandle handle, double dx, double dy, RectD bounds, double minSide)
    {
        double minX = rect.MinX;
        double maxX = rect.MaxX;
        double minY = rect.MinY;
        double maxY = rect.MaxY;

        switch (handle)
        {
            case CropHandle.TopLeft:
            case CropHandle.Left:
            case CropHandle.BottomLeft:
                minX = Math.Min(Math.Max(rect.MinX + dx, bounds.MinX), maxX - minSide);
                break;
            case CropHandle.TopRight:
            case CropHandle.Right:
            case CropHandle.BottomRight:
                maxX = Math.Max(Math.Min(rect.MaxX + dx, bounds.MaxX), minX + minSide);
                break;
        }

        switch (handle)
        {
            case CropHandle.TopLeft:
            case CropHandle.Top:
            case CropHandle.TopRight:
                minY = Math.Min(Math.Max(rect.MinY + dy, bounds.MinY), maxY - minSide);
                break;
            case CropHandle.BottomLeft:
            case CropHandle.Bottom:
            case CropHandle.BottomRight:
                maxY = Math.Max(Math.Min(rect.MaxY + dy, bounds.MaxY), minY + minSide);
                break;
        }

        return new RectD(minX, minY, maxX - minX, maxY - minY);
    }

    private static Side? Nearer(double value, double lower, double upper, double radius)
    {
        double toMin = Math.Abs(value - lower);
        double toMax = Math.Abs(value - upper);
        if (Math.Min(toMin, toMax) > radius) return null;
        return toMin <= toMax ? Side.Min : Side.Max;
    }
}
