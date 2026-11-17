using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnugWin.App.Controllers;
using SnugWin.Core.PhoneScreen;

namespace SnugWin.App.Views;

public partial class ScreenshotOverlayWindow : Window
{
    private readonly AppController _controller;
    private ScreenshotOverlay? _shown;
    private double _shownScale;

    public ScreenshotOverlayWindow(AppController controller)
    {
        InitializeComponent();
        _controller = controller;
    }

    public void UpdateAndShow(ScreenshotOverlay overlay, RectD barBounds, double scale, double dpiRatio)
    {
        double s = Math.Max(1.0, scale);
        if (!Equals(overlay, _shown) || Math.Abs(s - _shownScale) > 0.01)
        {
            _shown = overlay;
            _shownScale = s;
            ScalerRoot.LayoutTransform = new ScaleTransform(s, s);

            if (overlay is ScreenshotOverlay.Preview p)
            {
                PreviewCard.Visibility = Visibility.Visible;
                NoticeCard.Visibility = Visibility.Collapsed;
                ThumbImage.Source = DecodeBitmap(p.Image.Png);
            }
            else if (overlay is ScreenshotOverlay.Notice n)
            {
                ThumbImage.Source = null;
                PreviewCard.Visibility = Visibility.Collapsed;
                NoticeCard.Visibility = Visibility.Visible;
                (string glyph, string headline, string? detail) = n.Kind switch
                {
                    ScreenshotNotice.Cropped => ("✓", "已裁剪", "已替换剪贴板中的截图"),
                    ScreenshotNotice.NeedsPermission => ("⚠", "需要权限", "请在系统中允许截图"),
                    ScreenshotNotice.WindowGone => ("✕", "无法截图", "窗口已关闭"),
                    ScreenshotNotice.Failed => ("✕", "截图失败", "请重试"),
                    ScreenshotNotice.CopyFailed => ("✕", "未能写入剪贴板", "请重试"),
                    ScreenshotNotice.CropFailed => ("✕", "裁剪失败", "剪贴板中仍是完整截图"),
                    _ => ("!", "提示", null)
                };
                NoticeGlyph.Text = glyph;
                NoticeHeadline.Text = headline;
                if (string.IsNullOrEmpty(detail))
                {
                    NoticeDetail.Visibility = Visibility.Collapsed;
                }
                else
                {
                    NoticeDetail.Text = detail;
                    NoticeDetail.Visibility = Visibility.Visible;
                }
            }
        }

        if (!IsVisible)
        {
            ShowActivated = false;
            Show();
        }
        UpdateLayout();

        double pxW = Math.Max(1, ActualWidth * dpiRatio);
        double pxH = Math.Max(1, ActualHeight * dpiRatio);
        bool isPreview = overlay is ScreenshotOverlay.Preview;
        double x = isPreview
            ? barBounds.X + 12 * s * dpiRatio
            : barBounds.MidX - pxW / 2.0;
        double y = barBounds.Y - pxH - 8 * s * dpiRatio;

        WindowPlacementHelper.PlacePhysical(
            this,
            new RectD(x, y, pxW, pxH),
            topmost: true,
            noActivate: true,
            passthrough: !isPreview);
    }

    private void OnPreviewClick(object sender, MouseButtonEventArgs e)
    {
        _controller.OpenCrop();
    }

    private static BitmapSource DecodeBitmap(byte[] png)
    {
        using var ms = new MemoryStream(png, writable: false);
        var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }
}
