using System;
using System.Linq;
using System.Threading.Tasks;
using SnugWin.Core.Support;

namespace SnugWin.Core.PhoneScreen;

public sealed record CapturedImage(byte[] Png, int PixelWidth, int PixelHeight)
{
    public SizeD Size => new(PixelWidth, PixelHeight);

    public bool Equals(CapturedImage? other) =>
        other is not null
        && PixelWidth == other.PixelWidth
        && PixelHeight == other.PixelHeight
        && Png.AsSpan().SequenceEqual(other.Png);

    public override int GetHashCode() => HashCode.Combine(PixelWidth, PixelHeight, Png.Length);
}

public enum CaptureFailure
{
    NoPermission,
    WindowGone,
    Unavailable
}

public sealed class CaptureException : Exception
{
    public CaptureFailure Failure { get; }
    public CaptureException(CaptureFailure failure) : base(failure.ToString()) => Failure = failure;
}

public interface IScreenshotControl
{
    bool Trusted { get; }
    Task<CapturedImage> CaptureAsync(WindowKey window);
    CapturedImage? Crop(CapturedImage image, RectD rect);
    bool Copy(CapturedImage image);
}

public enum ScreenshotNotice
{
    Cropped,
    NeedsPermission,
    WindowGone,
    Failed,
    CopyFailed,
    CropFailed
}

public abstract record ScreenshotOverlay
{
    public sealed record Preview(CapturedImage Image) : ScreenshotOverlay;
    public sealed record Cropping(CapturedImage Image) : ScreenshotOverlay;
    public sealed record Notice(ScreenshotNotice Kind) : ScreenshotOverlay;

    public bool IsCropping => this is Cropping;
}

/// <summary>
/// Port of SnugCore Screenshots.swift: captures placed window to clipboard,
/// shows 5s preview thumbnail or 3s notice, and handles in-screen crop state.
/// </summary>
public sealed class Screenshots
{
    public const double PreviewDuration = 5.0;
    public const double NoticeDuration = 3.0;

    private readonly IScreenshotControl _system;
    private readonly ITimeline _timeline;
    private double? _expiresAt;
    private int _serial;

    public ScreenshotOverlay? Overlay { get; private set; }
    public event Action<ScreenshotOverlay?>? Changed;

    public Screenshots(IScreenshotControl system, ITimeline? timeline = null)
    {
        _system = system;
        _timeline = timeline ?? new SystemTimeline();
    }

    public bool Trusted => _system.Trusted;

    public async Task TakeAsync(WindowInfo window)
    {
        if (Overlay?.IsCropping == true) return;
        if (!_system.Trusted)
        {
            Log.Info("截图：没有录屏权限");
            Set(new ScreenshotOverlay.Notice(ScreenshotNotice.NeedsPermission));
            return;
        }

        _serial++;
        int mine = _serial;
        double started = _timeline.Now;

        CapturedImage? image = null;
        CaptureFailure? failure = null;
        try
        {
            image = await _system.CaptureAsync(window.Key).ConfigureAwait(false);
        }
        catch (CaptureException ex)
        {
            failure = ex.Failure;
        }
        catch (Exception ex)
        {
            Log.Warn($"截图失败（{window.Label}）：{ex.Message}");
            failure = CaptureFailure.Unavailable;
        }

        if (mine != _serial || Overlay?.IsCropping == true)
        {
            Log.Info($"截图：{window.Label} 截完时已不需要，丢弃");
            return;
        }

        if (image is not null)
        {
            if (!_system.Copy(image))
            {
                Log.Warn($"截图：剪贴板没写进去（{window.Label}）");
                Set(new ScreenshotOverlay.Notice(ScreenshotNotice.CopyFailed));
                return;
            }
            Log.Info($"截图：{window.Label} → 剪贴板 {image.PixelWidth}×{image.PixelHeight}，{image.Png.Length} 字节，{_timeline.Now - started:0.2}s");
            Set(new ScreenshotOverlay.Preview(image));
            return;
        }

        var notice = failure switch
        {
            CaptureFailure.NoPermission => ScreenshotNotice.NeedsPermission,
            CaptureFailure.WindowGone => ScreenshotNotice.WindowGone,
            _ => ScreenshotNotice.Failed
        };
        Log.Warn($"截图失败（{window.Label}）：{notice}");
        Set(new ScreenshotOverlay.Notice(notice));
    }

    public void OpenCrop()
    {
        if (Overlay is ScreenshotOverlay.Preview p)
            Set(new ScreenshotOverlay.Cropping(p.Image));
    }

    public void CancelCrop()
    {
        if (Overlay is ScreenshotOverlay.Cropping c)
            Set(new ScreenshotOverlay.Preview(c.Image));
    }

    public void FinishCrop(RectD selection)
    {
        if (Overlay is not ScreenshotOverlay.Cropping c) return;
        var rect = CropGeometry.PixelRect(selection, c.Image.Size);
        var cropped = _system.Crop(c.Image, rect);
        if (cropped is null)
        {
            Log.Warn($"截图裁剪：{c.Image.PixelWidth}×{c.Image.PixelHeight} 裁不出 {rect.Short}");
            Set(new ScreenshotOverlay.Notice(ScreenshotNotice.CropFailed));
            return;
        }
        if (!_system.Copy(cropped))
        {
            Log.Warn("截图裁剪：剪贴板没写进去");
            Set(new ScreenshotOverlay.Notice(ScreenshotNotice.CopyFailed));
            return;
        }
        Log.Info($"截图裁剪：{c.Image.PixelWidth}×{c.Image.PixelHeight} 的 {rect.Short} → 剪贴板，{cropped.Png.Length} 字节");
        Set(new ScreenshotOverlay.Notice(ScreenshotNotice.Cropped));
    }

    public void Expire()
    {
        if (_expiresAt is { } exp && _timeline.Now >= exp)
            Set(null);
    }

    public void Dismiss()
    {
        _serial++;
        Set(null);
    }

    private void Set(ScreenshotOverlay? overlay)
    {
        double? duration = overlay switch
        {
            ScreenshotOverlay.Preview => PreviewDuration,
            ScreenshotOverlay.Notice => NoticeDuration,
            _ => null
        };
        _expiresAt = duration.HasValue ? _timeline.Now + duration.Value : null;
        if (Equals(overlay, Overlay)) return;
        Overlay = overlay;
        Changed?.Invoke(overlay);
    }
}
