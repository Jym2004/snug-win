using System;
using SnugWin.Core.PhoneScreen;

namespace SnugWin.Core.Touch;

public enum ScrollPhase
{
    None = 0,
    Began = 1,
    Changed = 2,
    Ended = 4
}

public enum MomentumPhase
{
    None = 0,
    Begin = 1,
    Continue = 2,
    End = 3
}

public readonly record struct ScrollStep(
    int PixelsX,
    int PixelsY,
    double DeltaX,
    double DeltaY,
    ScrollPhase Phase,
    MomentumPhase Momentum,
    PointD Location);

internal struct ScrollWheel
{
    private double _remainderX;
    private double _remainderY;

    public ScrollStep Step(double dx, double dy, ScrollPhase phase, MomentumPhase momentum, PointD location)
    {
        _remainderX += dx;
        _remainderY += dy;
        double wholeX = Math.Truncate(_remainderX);
        double wholeY = Math.Truncate(_remainderY);
        _remainderX -= wholeX;
        _remainderY -= wholeY;
        return new ScrollStep((int)wholeX, (int)wholeY, dx, dy, phase, momentum, location);
    }
}

public struct Momentum
{
    public const double FrameRate = 60.0;
    private const double Decay = 0.95;
    private const double MinimumSpeed = 200.0;
    private const double StopSpeed = 40.0;

    private double _vx;
    private double _vy;
    private readonly double _stopSpeed;
    private bool _started;

    private Momentum(double vx, double vy, double stopSpeed)
    {
        _vx = vx;
        _vy = vy;
        _stopSpeed = stopSpeed;
        _started = false;
    }

    public static Momentum? TryCreate(double vx, double vy, double scale)
    {
        if (MathEx.Hypot(vx, vy) <= MinimumSpeed * scale) return null;
        return new Momentum(vx, vy, StopSpeed * scale);
    }

    public (double Dx, double Dy, MomentumPhase Phase)? Next()
    {
        _vx *= Decay;
        _vy *= Decay;
        if (MathEx.Hypot(_vx, _vy) < _stopSpeed) return null;
        var phase = _started ? MomentumPhase.Continue : MomentumPhase.Begin;
        _started = true;
        return (_vx / FrameRate, _vy / FrameRate, phase);
    }
}
