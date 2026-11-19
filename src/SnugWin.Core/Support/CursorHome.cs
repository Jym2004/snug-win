using System.Collections.Generic;
using SnugWin.Core.PhoneScreen;
using SnugWin.Core.Touch;

namespace SnugWin.Core.Support;

/// <summary>
/// Pure helper computing where to place the mouse cursor when virtual screens retire
/// or enter reconnect grace so the pointer never gets stranded off-screen.
/// Mirrors upstream Snug v2.7.0/v2.7.1 CursorHome.
/// </summary>
public static class CursorHome
{
    public static PointD? Landing(PointD pointer, IReadOnlyList<RectD> kept, RectD main)
    {
        foreach (var rect in kept)
        {
            if (rect.Width > 0 && rect.Height > 0 && rect.Contains(pointer))
                return null;
        }

        RectD target = main.Width > 0 && main.Height > 0
            ? main
            : FirstNonEmpty(kept);

        if (target.Width <= 0 || target.Height <= 0)
            return null;

        return new PointD(target.X + target.Width / 2.0, target.Y + target.Height / 2.0);
    }

    private static RectD FirstNonEmpty(IReadOnlyList<RectD> rects)
    {
        foreach (var r in rects)
        {
            if (r.Width > 0 && r.Height > 0)
                return r;
        }
        return default;
    }
}
