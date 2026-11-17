using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using SnugWin.Core.PhoneScreen;
using SnugWin.Core.Support;
using SnugWin.Windows.Native;

namespace SnugWin.Windows.Windows;

public sealed class ScreenshotService : IScreenshotControl
{
    public bool Trusted => true;

    public Task<CapturedImage> CaptureAsync(WindowKey window)
    {
        var hwnd = (IntPtr)window.Handle;
        if (!Win32.IsWindow(hwnd))
            throw new CaptureException(CaptureFailure.WindowGone);

        Win32.GetWindowThreadProcessId(hwnd, out int pid);
        if (pid != window.Pid)
            throw new CaptureException(CaptureFailure.WindowGone);

        if (!Win32.GetWindowRect(hwnd, out var rawRect) || rawRect.Width <= 0 || rawRect.Height <= 0)
            throw new CaptureException(CaptureFailure.Unavailable);

        RectD visibleBounds = WindowService.TryGetVisibleBounds(hwnd, out var ext)
            ? ext
            : new RectD(rawRect.Left, rawRect.Top, rawRect.Width, rawRect.Height);

        using var fullBmp = new Bitmap(rawRect.Width, rawRect.Height, PixelFormat.Format32bppArgb);
        bool captured;
        using (var g = Graphics.FromImage(fullBmp))
        {
            IntPtr hdc = g.GetHdc();
            try
            {
                captured = Win32.PrintWindow(hwnd, hdc, Win32.PW_RENDERFULLCONTENT)
                           || Win32.PrintWindow(hwnd, hdc, 0);
            }
            finally
            {
                g.ReleaseHdc(hdc);
            }
        }

        Bitmap finalBmp;
        if (captured)
        {
            int cropX = Math.Clamp((int)Math.Round(visibleBounds.X - rawRect.Left), 0, Math.Max(0, fullBmp.Width - 1));
            int cropY = Math.Clamp((int)Math.Round(visibleBounds.Y - rawRect.Top), 0, Math.Max(0, fullBmp.Height - 1));
            int cropW = Math.Clamp((int)Math.Round(visibleBounds.Width), 1, fullBmp.Width - cropX);
            int cropH = Math.Clamp((int)Math.Round(visibleBounds.Height), 1, fullBmp.Height - cropY);
            if (cropX > 0 || cropY > 0 || cropW != fullBmp.Width || cropH != fullBmp.Height)
            {
                finalBmp = fullBmp.Clone(new Rectangle(cropX, cropY, cropW, cropH), PixelFormat.Format32bppArgb);
            }
            else
            {
                finalBmp = (Bitmap)fullBmp.Clone();
            }
        }
        else
        {
            int w = Math.Max(1, (int)Math.Round(visibleBounds.Width));
            int h = Math.Max(1, (int)Math.Round(visibleBounds.Height));
            finalBmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(finalBmp);
            g.CopyFromScreen((int)Math.Round(visibleBounds.X), (int)Math.Round(visibleBounds.Y), 0, 0, new System.Drawing.Size(w, h), CopyPixelOperation.SourceCopy);
        }

        using (finalBmp)
        {
            byte[] png = EncodePng(finalBmp);
            return Task.FromResult(new CapturedImage(png, finalBmp.Width, finalBmp.Height));
        }
    }

    public CapturedImage? Crop(CapturedImage image, RectD rect)
    {
        if (rect.Width < 1 || rect.Height < 1) return null;
        try
        {
            using var ms = new MemoryStream(image.Png, writable: false);
            using var src = new Bitmap(ms);
            int x = Math.Clamp((int)Math.Round(rect.X), 0, src.Width - 1);
            int y = Math.Clamp((int)Math.Round(rect.Y), 0, src.Height - 1);
            int w = Math.Clamp((int)Math.Round(rect.Width), 1, src.Width - x);
            int h = Math.Clamp((int)Math.Round(rect.Height), 1, src.Height - y);
            using var cropped = src.Clone(new Rectangle(x, y, w, h), PixelFormat.Format32bppArgb);
            return new CapturedImage(EncodePng(cropped), cropped.Width, cropped.Height);
        }
        catch (Exception ex)
        {
            Log.Warn($"裁剪截图异常：{ex.Message}");
            return null;
        }
    }

    public bool Copy(CapturedImage image)
    {
        Exception? lastError = null;
        bool ok = false;

        void DoCopy()
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    using var ms = new MemoryStream(image.Png, writable: false);
                    var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    BitmapSource bmpSource = decoder.Frames[0];
                    bmpSource.Freeze();

                    var data = new System.Windows.DataObject();
                    data.SetImage(bmpSource);
                    data.SetData("PNG", new MemoryStream(image.Png, writable: false));
                    System.Windows.Clipboard.SetDataObject(data, copy: true);
                    ok = true;
                    return;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    Thread.Sleep(40);
                }
            }
        }

        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            DoCopy();
        }
        else
        {
            var thread = new Thread(DoCopy);
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join(2000);
        }

        if (!ok && lastError is not null)
            Log.Warn($"写入剪贴板失败：{lastError.Message}");
        return ok;
    }

    private static byte[] EncodePng(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }
}
