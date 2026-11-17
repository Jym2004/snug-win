using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnugWin.App.Controllers;
using SnugWin.Core.PhoneScreen;
using Point = System.Windows.Point;

namespace SnugWin.App.Views;

public partial class CropWindow : Window
{
    private readonly AppController _controller;
    private readonly CapturedImage _captured;
    private RectD _selection = new(0, 0, 1, 1);
    private (CropHandle Handle, RectD StartRect, Point StartMouse)? _dragging;

    public CropWindow(AppController controller, CapturedImage image)
    {
        InitializeComponent();
        _controller = controller;
        _captured = image;
        TargetImage.Source = DecodeBitmap(image.Png);
    }

    public void ShowOnDisplay(RectD bounds)
    {
        WindowPlacementHelper.PlacePhysical(this, bounds, topmost: true, noActivate: true);
        RenderCropOverlay();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => RenderCropOverlay();

    private (double Scale, double TopBar, double Margin, double GrabRadius, double MinSide, double Corner, RectD Shown) ComputeMetrics()
    {
        double w = Math.Max(1, CropCanvasGrid.ActualWidth);
        double h = Math.Max(1, CropCanvasGrid.ActualHeight);
        double scale = ScreenFit.Scale(w);
        double topBar = 64.0 * scale;
        double margin = 22.0 * scale;
        double grabRadius = 34.0 * scale;
        double minSide = 44.0 * scale;
        double corner = 20.0 * scale;
        var area = new RectD(margin, topBar, Math.Max(1, w - margin * 2), Math.Max(1, h - topBar - margin));
        var shown = CropGeometry.FittedRect(_captured.Size, area);
        return (scale, topBar, margin, grabRadius, minSide, corner, shown);
    }

    private void RenderCropOverlay()
    {
        if (CropCanvasGrid.ActualWidth <= 1 || CropCanvasGrid.ActualHeight <= 1) return;
        var m = ComputeMetrics();

        TopBarGrid.Height = m.TopBar;
        CancelBtn.FontSize = 18 * m.Scale;
        DoneBtn.FontSize = 18 * m.Scale;
        CancelBtn.Padding = new Thickness(m.Margin, 8 * m.Scale, m.Margin, 8 * m.Scale);
        DoneBtn.Padding = new Thickness(m.Margin, 8 * m.Scale, m.Margin, 8 * m.Scale);

        Canvas.SetLeft(TargetImage, m.Shown.X);
        Canvas.SetTop(TargetImage, m.Shown.Y);
        TargetImage.Width = m.Shown.Width;
        TargetImage.Height = m.Shown.Height;

        var sel = CropGeometry.Rect(_selection, m.Shown);

        // EvenOdd cutout mask outside selection
        var group = new GeometryGroup { FillRule = FillRule.EvenOdd };
        group.Children.Add(new RectangleGeometry(new Rect(0, 0, CropCanvasGrid.ActualWidth, CropCanvasGrid.ActualHeight)));
        group.Children.Add(new RectangleGeometry(new Rect(sel.X, sel.Y, sel.Width, sel.Height)));
        ScrimPath.Data = group;

        Canvas.SetLeft(SelectionBorder, sel.X);
        Canvas.SetTop(SelectionBorder, sel.Y);
        SelectionBorder.Width = sel.Width;
        SelectionBorder.Height = sel.Height;
        SelectionBorder.StrokeThickness = Math.Max(1, m.Scale);

        double line = 4.0 * m.Scale;
        double halfLine = line / 2.0;
        double oMinX = sel.MinX - halfLine;
        double oMaxX = sel.MaxX + halfLine;
        double oMinY = sel.MinY - halfLine;
        double oMaxY = sel.MaxY + halfLine;
        double oMidX = (oMinX + oMaxX) / 2.0;
        double oMidY = (oMinY + oMaxY) / 2.0;

        double cornerLen = Math.Min(m.Corner, Math.Min(sel.Width / 3.0, sel.Height / 3.0));
        double tickHalf = Math.Min(m.Corner, sel.Width / 4.0) / 2.0;
        double vTickHalf = Math.Min(m.Corner, sel.Height / 4.0) / 2.0;

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            // 4 corners
            AddL(ctx, oMinX, oMinY, cornerLen, cornerLen);
            AddL(ctx, oMaxX, oMinY, -cornerLen, cornerLen);
            AddL(ctx, oMinX, oMaxY, cornerLen, -cornerLen);
            AddL(ctx, oMaxX, oMaxY, -cornerLen, -cornerLen);

            // Top & bottom midpoint ticks
            ctx.BeginFigure(new Point(oMidX - tickHalf, oMinY), false, false);
            ctx.LineTo(new Point(oMidX + tickHalf, oMinY), true, false);
            ctx.BeginFigure(new Point(oMidX - tickHalf, oMaxY), false, false);
            ctx.LineTo(new Point(oMidX + tickHalf, oMaxY), true, false);

            // Left & right midpoint ticks
            ctx.BeginFigure(new Point(oMinX, oMidY - vTickHalf), false, false);
            ctx.LineTo(new Point(oMinX, oMidY + vTickHalf), true, false);
            ctx.BeginFigure(new Point(oMaxX, oMidY - vTickHalf), false, false);
            ctx.LineTo(new Point(oMaxX, oMidY + vTickHalf), true, false);
        }
        geo.Freeze();
        HandlesPath.StrokeThickness = line;
        HandlesPath.Data = geo;
    }

    private static void AddL(StreamGeometryContext ctx, double x, double y, double dx, double dy)
    {
        ctx.BeginFigure(new Point(x, y + dy), false, false);
        ctx.LineTo(new Point(x, y), true, false);
        ctx.LineTo(new Point(x + dx, y), true, false);
    }

    private void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(CropCanvasGrid);
        var m = ComputeMetrics();
        var current = CropGeometry.Rect(_selection, m.Shown);
        var handle = CropGeometry.HandleAt(new PointD(pos.X, pos.Y), current, m.GrabRadius);
        if (handle is not { } h) return;
        _dragging = (h, current, pos);
        CropCanvasGrid.CaptureMouse();
        e.Handled = true;
    }

    private void OnCanvasMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragging is not { } drag) return;
        var pos = e.GetPosition(CropCanvasGrid);
        var m = ComputeMetrics();
        double dx = pos.X - drag.StartMouse.X;
        double dy = pos.Y - drag.StartMouse.Y;
        var moved = CropGeometry.Resized(drag.StartRect, drag.Handle, dx, dy, m.Shown, m.MinSide);
        _selection = CropGeometry.UnitRect(moved, m.Shown);
        RenderCropOverlay();
    }

    private void OnCanvasMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging is null) return;
        _dragging = null;
        CropCanvasGrid.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => _controller.CancelCrop();
    private void OnDoneClick(object sender, RoutedEventArgs e) => _controller.FinishCrop(_selection);

    private static BitmapSource DecodeBitmap(byte[] png)
    {
        using var ms = new MemoryStream(png, writable: false);
        var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }
}
