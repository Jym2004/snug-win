using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SnugWin.App.Controllers;
using SnugWin.Core.PhoneScreen;

namespace SnugWin.App.Views;

public partial class PhoneReentryWindow : Window
{
    private readonly AppController _controller;

    public PhoneReentryWindow(AppController controller)
    {
        InitializeComponent();
        _controller = controller;
    }

    public void ShowAtBottomRight(RectD mainWorkArea)
    {
        if (!IsVisible)
        {
            ShowActivated = false;
            Show();
        }
        UpdateLayout();

        var source = PresentationSource.FromVisual(this);
        double dpi = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        if (dpi <= 0) dpi = 1.0;

        double pxW = Math.Max(120, ActualWidth * dpi);
        double pxH = Math.Max(36, ActualHeight * dpi);
        double margin = Math.Round(18 * dpi);
        double x = Math.Max(mainWorkArea.X + margin, mainWorkArea.MaxX - pxW - margin);
        double y = Math.Max(mainWorkArea.Y + margin, mainWorkArea.MaxY - pxH - margin);

        WindowPlacementHelper.PlacePhysical(this, new RectD(x, y, pxW, pxH), topmost: true, noActivate: true);
    }

    private void OnReenterClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Hide();
        _controller.ReenterScreen();
    }

    private void OnHidePillClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _controller.HideReentryPill();
    }
}
