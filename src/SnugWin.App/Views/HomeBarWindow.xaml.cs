using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SnugWin.App.Controllers;
using SnugWin.Core.PhoneScreen;

namespace SnugWin.App.Views;

public partial class HomeBarWindow : Window
{
    private const double SwipeThreshold = 12.0;
    private const double CameraTouchWidth = 56.0;
    private readonly AppController _controller;
    private double _scale = 1.0;
    private bool _showsCamera;
    private double _swipeAccum;
    private DateTime _lastWheelAt = DateTime.MinValue;

    public HomeBarWindow(AppController controller)
    {
        InitializeComponent();
        _controller = controller;
    }

    public void ShowAtBounds(RectD bounds, double scale, bool showsCamera)
    {
        double clampedScale = Math.Max(1.0, scale);
        if (Math.Abs(_scale - clampedScale) > 0.01)
        {
            _scale = clampedScale;
            ChevronPath.LayoutTransform = new ScaleTransform(_scale, _scale);
            CameraGlyph.LayoutTransform = new ScaleTransform(_scale, _scale);
            CameraBox.Width = CameraTouchWidth * _scale;
        }
        if (_showsCamera != showsCamera)
        {
            _showsCamera = showsCamera;
            CameraBox.Visibility = showsCamera ? Visibility.Visible : Visibility.Collapsed;
        }
        WindowPlacementHelper.PlacePhysical(this, bounds, topmost: true, noActivate: true);
    }

    private void OnBarClick(object sender, MouseButtonEventArgs e)
    {
        _swipeAccum = 0;
        var pos = e.GetPosition(RootGrid);
        if (_showsCamera && pos.X >= 0 && pos.X <= CameraBox.Width)
            _controller.TakeScreenshot();
        else
            _controller.ShowList();
    }

    private void OnBarWheel(object sender, MouseWheelEventArgs e)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastWheelAt).TotalMilliseconds > 350)
            _swipeAccum = 0;
        _lastWheelAt = now;

        if (e.Delta < 0)
        {
            _swipeAccum += -e.Delta / 2.5;
            if (_swipeAccum >= SwipeThreshold * _scale)
            {
                _swipeAccum = 0;
                _controller.ShowList();
            }
        }
        e.Handled = true;
    }
}
