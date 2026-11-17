using System;
using System.Collections.Generic;
using SnugWin.Core.PhoneScreen;
using SnugWin.Core.Session;
using Xunit;

namespace SnugWin.Core.Tests;

public sealed class GeometryAndSessionExpandedTests
{
    [Theory]
    [InlineData(0, 0, 100, 100, 50, 50, true)]
    [InlineData(0, 0, 100, 100, 0, 0, true)]
    [InlineData(0, 0, 100, 100, 100, 100, true)]
    [InlineData(0, 0, 100, 100, 100.1, 100, false)]
    [InlineData(10, 20, 50, 60, 9.9, 20, false)]
    [InlineData(10, 20, 50, 60, 10, 79.9, true)]
    public void RectD_ContainsWorksAccurately(
        double rx, double ry, double rw, double rh,
        double px, double py, bool expected)
    {
        var rect = new RectD(rx, ry, rw, rh);
        Assert.Equal(expected, rect.Contains(new PointD(px, py)));
    }

    [Theory]
    [InlineData(0, 0, 100, 100, 1, 1, 99, 101, true)]
    [InlineData(0, 0, 100, 100, 3, 0, 100, 100, false)]
    [InlineData(0, 0, 100, 100, 0, 0, 103, 100, false)]
    public void RectD_MatchesRespectsTolerance(
        double x1, double y1, double w1, double h1,
        double x2, double y2, double w2, double h2,
        bool expected)
    {
        var a = new RectD(x1, y1, w1, h1);
        var b = new RectD(x2, y2, w2, h2);
        Assert.Equal(expected, a.Matches(b, tolerance: 2.0));
    }

    [Theory]
    [InlineData(1080, 2400, true)]
    [InlineData(9, 20, true)]
    [InlineData(1920, 1080, true)]
    [InlineData(0, 1080, false)]
    [InlineData(-5, 1080, false)]
    [InlineData(25000, 1080, false)]
    public void PhoneScreenSpecData_CustomValidation(int w, int h, bool valid)
    {
        var res = PhoneScreenSpecData.Custom(w, h);
        Assert.Equal(valid, res is not null);
        if (res is not null)
            Assert.True(res.Height >= res.Width);
    }

    [Theory]
    [InlineData(540, 1.0)]
    [InlineData(675, 1.25)]
    [InlineData(810, 1.5)]
    [InlineData(1080, 2.0)]
    public void ScreenFit_ScaleAndHomeBarHeightScaleProportionally(int width, double expectedScale)
    {
        Assert.Equal(expectedScale, ScreenFit.Scale(width), 3);
        Assert.Equal(Math.Round(28.0 * expectedScale, MidpointRounding.AwayFromZero), ScreenFit.HomeBarHeight(width));
    }

    [Theory]
    [InlineData("微信", "wx")]
    [InlineData("微信", "weixin")]
    [InlineData("QQ音乐", "qqyy")]
    [InlineData("腾讯会议", "txhy")]
    [InlineData("网易云音乐", "wyyyy")]
    [InlineData("爱思助手", "aszs")]
    [InlineData("沙漏验机", "slyj")]
    [InlineData("文件资源管理器", "wjzyglq")]
    [InlineData("计算器", "jsq")]
    [InlineData("设置", "sz")]
    public void PinyinSearch_MatchesCommonChineseAppNames(string appName, string query)
    {
        Assert.True(PinyinSearch.Matches(appName, query));
        Assert.True(PinyinSearch.Score(appName, query) >= 1);
    }

    [Theory]
    [InlineData("备忘录", "b", "a")]
    [InlineData("微信", "w", "a")]
    [InlineData("Zoom", "z", "a")]
    public void PinyinSearch_SortKeyOrdersAlphabetically(string text, string expectedPrefix, string earlierLetter)
    {
        string key = PinyinSearch.SortKey(text);
        Assert.StartsWith(expectedPrefix, key, StringComparison.OrdinalIgnoreCase);
        Assert.True(string.Compare(earlierLetter, key, StringComparison.OrdinalIgnoreCase) < 0);
    }

    [Fact]
    public void ConnectionTracker_TracksControllerChangesAndUnreadableState()
    {
        var tracker = new ConnectionTracker();
        var t0 = DateTimeOffset.UtcNow;

        Assert.Null(tracker.Update(new UUSignals(false, ControllerTable.Unreadable, false), t0));
        var c1 = tracker.Update(new UUSignals(true, ControllerTable.FromNames("iPhone 17"), true), t0.AddSeconds(1));
        Assert.IsType<ConnectionEvent.Connected>(c1);

        // Same controller set emits null
        Assert.Null(tracker.Update(new UUSignals(true, ControllerTable.FromNames("iPhone 17"), true), t0.AddSeconds(2)));

        // Controller change emits ControllersChanged
        var c2 = tracker.Update(new UUSignals(true, ControllerTable.FromNames("iPhone 17", "iPad Pro"), true), t0.AddSeconds(3));
        var changed = Assert.IsType<ConnectionEvent.ControllersChanged>(c2);
        Assert.Equal(2, changed.Controllers.Count);

        // Empty controllers while still present does not clear known controllers
        Assert.Null(tracker.Update(new UUSignals(true, ControllerTable.Unrecognized, true), t0.AddSeconds(4)));
        Assert.Equal(2, tracker.Controllers.Count);
    }

    [Fact]
    public void RemoteSession_TracksDepartedControllersWithinReconnectWindow()
    {
        var session = new RemoteSession();
        var t0 = DateTimeOffset.UtcNow;

        session.Update(new UUSignals(true, ControllerTable.FromNames("My MacBook", "iPhone 17"), true), t0);
        Assert.True(ControllerClassifier.WantsPhoneScreen(session.EpisodeControllers(t0)));

        // iPhone leaves, desktop remains; within 15s iPhone lingers in episode controllers
        session.Update(new UUSignals(true, ControllerTable.FromNames("My MacBook"), true), t0.AddSeconds(2));
        Assert.Contains("iPhone 17", session.EpisodeControllers(t0.AddSeconds(10)));

        // After 15s linger expires, only desktop remains
        var activeAfter = session.EpisodeControllers(t0.AddSeconds(20));
        Assert.DoesNotContain("iPhone 17", activeAfter);
        Assert.False(ControllerClassifier.WantsPhoneScreen(activeAfter));
    }

    [Theory]
    [InlineData("WindowInfo title", "WindowInfo title")]
    [InlineData("  \u200BHello\u200E World  ", "Hello World")]
    [InlineData("👨‍👩‍👧 Family", "👨‍👩‍👧 Family")]
    public void WindowInfo_VisibleTitleStripsInvisibleDirectionalMarksPreservingZwj(string raw, string expected)
    {
        Assert.Equal(expected, WindowInfo.VisibleTitle(raw));
    }

    [Fact]
    public void CropGeometry_PixelRectHandlesClippingAndEmptyRegions()
    {
        Assert.Equal(default, CropGeometry.PixelRect(new RectD(-0.5, -0.5, 0.2, 0.2), new SizeD(1000, 1000)));
        Assert.Equal(default, CropGeometry.PixelRect(new RectD(1.1, 0.1, 0.5, 0.5), new SizeD(1000, 1000)));
        var clipped = CropGeometry.PixelRect(new RectD(-0.2, 0.5, 0.5, 0.8), new SizeD(1000, 1000));
        Assert.Equal(new RectD(0, 500, 300, 500), clipped);
    }

    [Theory]
    [InlineData(5, 0, 10, 5)]
    [InlineData(-5, 0, 10, 0)]
    [InlineData(15, 0, 10, 10)]
    public void MathEx_ClampWorksForIntAndDouble(int v, int min, int max, int expected)
    {
        Assert.Equal(expected, v.Clamp(min, max));
        Assert.Equal((double)expected, ((double)v).Clamp(min, max));
    }

    [Theory]
    [InlineData("9:20", 900, 2000)]
    [InlineData("iPhone 16 Pro Max", 1320, 2868)]
    [InlineData("iPhone SE", 750, 1334)]
    [InlineData("1080x2400", 1080, 2400)]
    public void ScreenFit_FromArbitraryPortraitSizesProducesValidBaseWidth(string _, int w, int h)
    {
        var fit = ScreenFit.FromPhone(new PhoneScreenSpecData(w, h));
        Assert.InRange(fit.BaseWidth, ScreenFit.MinWidth, ScreenFit.MaxWidth);
        Assert.True(fit.Size(fit.BaseWidth).Height >= ScreenFit.MinHeight);
    }

    [Theory]
    [InlineData("iPhone 17", 1)]
    [InlineData("iPhone 17、iPad Air", 2)]
    [InlineData("MacBook、Windows PC", 0)]
    public void ControllerTableParser_RecognizesRemoteControlRowsOnly(string rowListCsv, int expectedPhoneOrTabletCount)
    {
        var rowNodes = new List<UuElement>();
        foreach (var name in rowListCsv.Split('、'))
        {
            rowNodes.Add(UuElement.RowNode([
                UuElement.TextNode(name),
                UuElement.TextNode("远程控制"),
                UuElement.TextNode("连接时长 00:05")
            ]));
        }
        var tree = UuElement.OtherNode([UuElement.TextNode("本机被控中"), UuElement.TableNode(rowNodes)]);
        var table = ControllerTableParser.ParseWindows([tree]);
        Assert.Equal(rowListCsv.Split('、').Length, table.Names.Count);
        int nonDesktop = 0;
        foreach (var n in table.Names)
        {
            if (ControllerClassifier.Classify(n) != ControllerKind.Desktop)
                nonDesktop++;
        }
        Assert.Equal(expectedPhoneOrTabletCount, nonDesktop);
    }

    [Theory]
    [InlineData(0.0, 0.0, 1.0, 1.0, 1000, 2000, 0, 0, 1000, 2000)]
    [InlineData(0.1, 0.2, 0.3, 0.4, 1000, 2000, 100, 400, 300, 800)]
    [InlineData(0.25, 0.25, 0.5, 0.5, 500, 500, 125, 125, 250, 250)]
    public void CropGeometry_PixelRectRoundTripsCleanly(
        double ux, double uy, double uw, double uh,
        double imgW, double imgH,
        double px, double py, double pw, double ph)
    {
        var pr = CropGeometry.PixelRect(new RectD(ux, uy, uw, uh), new SizeD(imgW, imgH));
        Assert.Equal(new RectD(px, py, pw, ph), pr);
    }

    [Theory]
    [InlineData("Chrome", "cr", false)]
    [InlineData("Chrome", "chr", true)]
    [InlineData("Edge", "msedge", false)]
    [InlineData("Microsoft Edge", "edge", true)]
    [InlineData("中国银行", "zgyh", true)]
    [InlineData("招商银行", "zsyh", true)]
    [InlineData("百度网盘", "bdwp", true)]
    [InlineData("飞书", "fs", true)]
    [InlineData("钉钉", "dd", true)]
    [InlineData("哔哩哔哩", "blbl", true)]
    public void PinyinSearch_HandlesBanksAndMoreApps(string name, string query, bool expected)
    {
        Assert.Equal(expected, PinyinSearch.Matches(name, query));
    }
}
