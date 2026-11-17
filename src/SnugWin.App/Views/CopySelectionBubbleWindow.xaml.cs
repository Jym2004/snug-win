using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SnugWin.App.Controllers;
using SnugWin.Core.PhoneScreen;

namespace SnugWin.App.Views;

public partial class CopySelectionBubbleWindow : Window
{
    private readonly AppController _controller;
    private readonly DispatcherTimer _hideTimer;
    private PointD _targetPoint;

    public CopySelectionBubbleWindow(AppController controller)
    {
        InitializeComponent();
        _controller = controller;
        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4.5) };
        _hideTimer.Tick += (_, _) => HideBubble();
    }

    public void ShowAtSelection(PointD selectionPoint, RectD phoneBounds, double scale, double dpiRatio)
    {
        _targetPoint = selectionPoint;
        double s = Math.Max(1.0, scale);
        BubbleBorder.LayoutTransform = new ScaleTransform(s, s);

        if (!IsVisible)
        {
            ShowActivated = false;
            Show();
        }
        UpdateLayout();

        double pxW = Math.Max(60, ActualWidth * dpiRatio);
        double pxH = Math.Max(32, ActualHeight * dpiRatio);
        double x = Math.Clamp(selectionPoint.X - pxW / 2.0, phoneBounds.X + 8, Math.Max(phoneBounds.X + 8, phoneBounds.MaxX - pxW - 8));
        double y = Math.Clamp(selectionPoint.Y - pxH - 18 * dpiRatio, phoneBounds.Y + 8, Math.Max(phoneBounds.Y + 8, phoneBounds.MaxY - pxH - 80));

        WindowPlacementHelper.PlacePhysical(this, new RectD(x, y, pxW, pxH), topmost: true, noActivate: true);
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    public void HideBubble()
    {
        _hideTimer.Stop();
        if (IsVisible) Hide();
    }

    private async void OnCopyClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var pt = _targetPoint;
        HideBubble();
        await _controller.CopySelectionAtAsync(pt);
    }
}
