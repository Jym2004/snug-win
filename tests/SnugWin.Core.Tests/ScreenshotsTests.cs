using System;
using System.Threading.Tasks;
using SnugWin.Core.PhoneScreen;
using Xunit;

namespace SnugWin.Core.Tests;

public sealed class ScreenshotsTests
{
    private sealed class FakeTimeline : ITimeline
    {
        public double Now { get; set; }
        public Task SleepAsync(double seconds, System.Threading.CancellationToken ct = default)
        {
            Now += Math.Max(0, seconds);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeScreenshotControl : IScreenshotControl
    {
        public bool Trusted { get; set; } = true;
        public CapturedImage NextImage { get; set; } = new([1, 2, 3, 4], 1080, 1860);
        public CaptureFailure? NextFailure { get; set; }
        public bool CopyResult { get; set; } = true;
        public CapturedImage? LastCopied { get; private set; }
        public RectD? LastCropRect { get; private set; }

        public Task<CapturedImage> CaptureAsync(WindowKey window)
        {
            if (NextFailure is { } f) throw new CaptureException(f);
            return Task.FromResult(NextImage);
        }

        public CapturedImage? Crop(CapturedImage image, RectD rect)
        {
            LastCropRect = rect;
            if (rect.Width < 1 || rect.Height < 1) return null;
            return new CapturedImage([9, 8, 7], (int)rect.Width, (int)rect.Height);
        }

        public bool Copy(CapturedImage image)
        {
            if (!CopyResult) return false;
            LastCopied = image;
            return true;
        }
    }

    [Fact]
    public void CropGeometry_ConvertsBetweenUnitAndPixelRects()
    {
        var shown = new RectD(20, 60, 500, 800);
        var sel = new RectD(120, 260, 250, 400);
        var unit = CropGeometry.UnitRect(sel, shown);
        Assert.Equal(0.2, unit.X, 4);
        Assert.Equal(0.25, unit.Y, 4);
        Assert.Equal(0.5, unit.Width, 4);
        Assert.Equal(0.5, unit.Height, 4);

        var px = CropGeometry.PixelRect(unit, new SizeD(1000, 2000));
        Assert.Equal(new RectD(200, 500, 500, 1000), px);
    }

    [Fact]
    public void CropGeometry_GrabsHandlesAndResizesWithinBounds()
    {
        var rect = new RectD(100, 100, 200, 300);
        var bounds = new RectD(50, 50, 400, 500);
        Assert.Equal(CropHandle.TopLeft, CropGeometry.HandleAt(new PointD(102, 102), rect, 20));
        Assert.Equal(CropHandle.Bottom, CropGeometry.HandleAt(new PointD(200, 398), rect, 20));
        Assert.Null(CropGeometry.HandleAt(new PointD(200, 250), rect, 20));

        var moved = CropGeometry.Resized(rect, CropHandle.TopLeft, -100, 280, bounds, minSide: 40);
        Assert.Equal(50, moved.X);
        Assert.Equal(360, moved.Y); // clamped to maxY - 40 = 360
        Assert.Equal(250, moved.Width);
        Assert.Equal(40, moved.Height);
    }

    [Fact]
    public async Task Screenshots_TakeCropAndExpireLifecycle()
    {
        var ctl = new FakeScreenshotControl();
        var timeline = new FakeTimeline();
        var shots = new Screenshots(ctl, timeline);

        var win = new WindowInfo(new WindowKey(10, 1), "App", "app.exe", "Doc");
        await shots.TakeAsync(win);
        Assert.IsType<ScreenshotOverlay.Preview>(shots.Overlay);
        Assert.Equal(ctl.NextImage, ctl.LastCopied);

        shots.OpenCrop();
        Assert.IsType<ScreenshotOverlay.Cropping>(shots.Overlay);

        // Should not expire while cropping
        timeline.Now += 100;
        shots.Expire();
        Assert.IsType<ScreenshotOverlay.Cropping>(shots.Overlay);

        shots.FinishCrop(new RectD(0.1, 0.2, 0.5, 0.5));
        var notice = Assert.IsType<ScreenshotOverlay.Notice>(shots.Overlay);
        Assert.Equal(ScreenshotNotice.Cropped, notice.Kind);
        Assert.Equal(new RectD(108, 372, 540, 930), ctl.LastCropRect);

        // Expires after NoticeDuration (3s)
        timeline.Now += 3.1;
        shots.Expire();
        Assert.Null(shots.Overlay);
    }
}
