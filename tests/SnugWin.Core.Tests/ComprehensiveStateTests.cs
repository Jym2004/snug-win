using System;
using System.Collections.Generic;
using SnugWin.Core.Config;
using SnugWin.Core.PhoneScreen;
using SnugWin.Core.Touch;
using Xunit;

namespace SnugWin.Core.Tests;

public sealed class ComprehensiveStateTests
{
    [Fact]
    public void AppList_OrderedAndKeepingOrder_PreserveZOrderAndExistingListOrder()
    {
        var appA = new AppInfo("a.exe", "Alpha", @"C:\a.exe");
        var appB = new AppInfo("b.exe", "Beta", @"C:\b.exe");
        var appC = new AppInfo("c.exe", "Gamma", @"C:\c.exe");

        var wA1 = new WindowInfo(new WindowKey(1, 0x10), "Alpha", "a.exe", "Doc1");
        var wA2 = new WindowInfo(new WindowKey(1, 0x20), "Alpha", "a.exe", "Doc2");
        var wB1 = new WindowInfo(new WindowKey(2, 0x30), "Beta", "b.exe", "Browser");
        var wC1 = new WindowInfo(new WindowKey(3, 0x40), "Gamma", "c.exe", "Notes");

        var ordered = AppList.Ordered(
            [new RunningApp(appA, 1, [wA1, wA2]), new RunningApp(appB, 2, [wB1])],
            frontToBackHandles: [0x30, 0x20, 0x10]);

        Assert.Equal("b.exe", ordered[0].App.AppId);
        Assert.Equal(0x20UL, ordered[1].Windows[0].Key.Handle);

        // KeepingOrder preserves visible position even if fresh order reverses, drops closed apps, appends new apps
        var kept = AppList.KeepingOrder(
            shown: ordered,
            fresh: [new RunningApp(appA, 1, [wA1, wA2]), new RunningApp(appC, 3, [wC1])]);

        Assert.Equal(2, kept.Count);
        Assert.Equal("a.exe", kept[0].App.AppId);
        Assert.Equal(new[] { 0x20UL, 0x10UL }, new[] { kept[0].Windows[0].Key.Handle, kept[0].Windows[1].Key.Handle });
        Assert.Equal("c.exe", kept[1].App.AppId);
    }

    [Fact]
    public void AppList_SortedGroupedAndSearch_WorkAcrossChinesePinyinAndWindows()
    {
        var bwl = new AppInfo("notes.exe", "备忘录", @"C:\notes.exe");
        var zc = new AppInfo("7zip.exe", "7-Zip", @"C:\7z.exe");
        var edge = new AppInfo("msedge.exe", "Microsoft Edge", @"C:\msedge.exe");
        var edgeDup = new AppInfo("MSEDGE.EXE", "Microsoft Edge Dup", @"C:\msedge.exe");

        var sorted = AppList.Sorted([zc, edge, bwl, edgeDup]);
        Assert.Equal(3, sorted.Count); // case-insensitive dedup

        var groups = AppList.GroupedByLetter(sorted);
        Assert.Equal("#", groups[^1].Letter);
        Assert.Contains(groups, g => g.Letter == "B" && g.Apps[0].Name == "备忘录");

        var win = new WindowInfo(new WindowKey(99, 0x99), "Microsoft Edge", "msedge.exe", "GitHub Pull Request");
        var running = new[] { new RunningApp(edge, 99, [win]) };

        var resPinyin = AppList.Search("bwl", sorted, running);
        Assert.Single(resPinyin.Apps);
        Assert.Equal("备忘录", resPinyin.Apps[0].Name);

        var resWindow = AppList.Search("github", sorted, running);
        Assert.Single(resWindow.Windows);
        Assert.Equal("GitHub Pull Request", resWindow.Windows[0].Title);

        Assert.True(AppList.Search("   ", sorted, running).IsEmpty);
        Assert.IsType<AppAction.Pull>(AppList.ActionFor(edge, running));
        Assert.IsType<AppAction.Open>(AppList.ActionFor(bwl, running));
    }

    [Theory]
    [InlineData(1080, 2400, 1080, 2400)]
    [InlineData(2400, 1080, 1080, 2400)] // normalizes landscape input to portrait
    [InlineData(9, 20, 9, 20)]
    public void PhoneScreenSpecData_CustomNormalizesValidPortraitDimensions(int a, int b, int ew, int eh)
    {
        var spec = PhoneScreenSpecData.Custom(a, b);
        Assert.NotNull(spec);
        Assert.True(spec!.IsValid);
        Assert.Equal(ew, spec.Width);
        Assert.Equal(eh, spec.Height);
    }

    [Theory]
    [InlineData(null, 2400)]
    [InlineData(1080, null)]
    [InlineData(0, 2400)]
    [InlineData(-5, 2400)]
    [InlineData(20001, 2400)]
    public void PhoneScreenSpecData_CustomRejectsInvalidBounds(int? a, int? b)
    {
        Assert.Null(PhoneScreenSpecData.Custom(a, b));
    }

    [Theory]
    [InlineData("""{"notBrands":[]}""")]
    [InlineData("""{"brands":[{"name":"","models":[{"name":"M","screen":[100,200]}]}]}""")]
    [InlineData("""{"brands":[{"name":"A","models":[{"name":"M","screen":[100,200]}]},{"name":"a","models":[{"name":"N","screen":[100,200]}]}]}""")]
    [InlineData("""{"brands":[{"name":"A","models":[{"name":"M","screen":[200,100]}]}]}""")]
    [InlineData("""{"brands":[{"name":"A","models":[{"name":"M","screen":[100]}]}]}""")]
    public void PhoneCatalog_FromJsonRejectsMalformedOrDuplicateEntries(string badJson)
    {
        Assert.Throws<PhoneCatalogException>(() => PhoneCatalog.FromJson(badJson));
    }

    [Fact]
    public void PhoneCatalog_SearchSupportsAliasesAndResolveUpdatesKnownModel()
    {
        const string json = """
            {
              "brands": [
                {
                  "name": "Xiaomi",
                  "aliases": ["小米", "mi"],
                  "models": [
                    { "name": "15 Ultra", "screen": [1440, 3200], "points": 412 }
                  ]
                }
              ]
            }
            """;
        var catalog = PhoneCatalog.FromJson(json);
        var hits = catalog.Search("小米 15");
        Assert.Single(hits);
        Assert.Equal("Xiaomi 15 Ultra", hits[0].Models[0].Id);

        var stale = new PhoneChoice("Xiaomi 15 Ultra", new PhoneScreenSpecData(100, 200));
        var resolved = catalog.Resolve(stale);
        Assert.Equal(1440, resolved.Screen.Width);
        Assert.Equal(3200, resolved.Screen.Height);
        Assert.Equal(412, resolved.Screen.Points);
    }

    [Fact]
    public void TouchTranslator_InterruptsActiveMomentumWhenPressOutsideRegion()
    {
        var region = new TouchRegion(new RectD(2560, 0, 540, 960), 1.0);
        var t = new TouchTranslator();

        // Start fast vertical fling inside phone region
        t.Handle(new TouchInput(TouchInputKind.Press, new PointD(2700, 600), 0.0), region);
        t.Handle(new TouchInput(TouchInputKind.Drag, new PointD(2700, 560), 0.02), region);
        t.Handle(new TouchInput(TouchInputKind.Drag, new PointD(2700, 300), 0.06), region);
        t.Handle(new TouchInput(TouchInputKind.Release, new PointD(2700, 150), 0.09), region);
        Assert.True(t.IsCoasting);

        // Next press OUTSIDE region passes original press AND emits momentum end scroll step
        var outside = t.Handle(new TouchInput(TouchInputKind.Press, new PointD(200, 200), 0.12), region);
        Assert.True(outside.PassOriginal);
        var cmd = Assert.IsType<TouchCommand.Scroll>(Assert.Single(outside.Commands));
        Assert.Equal(MomentumPhase.End, cmd.Step.Momentum);
        Assert.False(t.IsCoasting);
    }

    [Fact]
    public void CloseButton_IsFrontmostChecksOcclusionCorrectly()
    {
        var btn = new CloseButton(new RectD(2990, 5, 40, 30), 0x1111, new RectD(2560, 0, 540, 960));
        var pt = new PointD(3005, 15);

        Assert.True(btn.IsFrontmost(pt, [(0x1111UL, new RectD(2560, 0, 540, 960))]));
        // Occluded by top-most dialog at z-index 0
        Assert.False(btn.IsFrontmost(pt, [(0x9999UL, new RectD(2600, 0, 480, 400)), (0x1111UL, new RectD(2560, 0, 540, 960))]));
        // Point outside close button frame
        Assert.False(btn.IsFrontmost(new PointD(2700, 200), [(0x1111UL, new RectD(2560, 0, 540, 960))]));
    }

    [Theory]
    [InlineData(CropHandle.Top, 0, -25, 100, 75, 200, 325)]
    [InlineData(CropHandle.TopRight, 30, 20, 100, 120, 230, 280)]
    [InlineData(CropHandle.Left, 50, 0, 150, 100, 150, 300)]
    [InlineData(CropHandle.Bottom, 0, -100, 100, 100, 200, 200)]
    [InlineData(CropHandle.BottomLeft, -30, 40, 70, 100, 230, 340)]
    public void CropGeometry_HandlesRemainingEdgeAndCornerHandles(
        CropHandle handle,
        double dx,
        double dy,
        double ex,
        double ey,
        double ew,
        double eh)
    {
        var rect = new RectD(100, 100, 200, 300);
        var bounds = new RectD(50, 50, 400, 500);
        var resized = CropGeometry.Resized(rect, handle, dx, dy, bounds, minSide: 40);
        Assert.Equal(new RectD(ex, ey, ew, eh), resized);
    }

    [Theory]
    [InlineData("\u200EHello\u200F World\u200B", "Hello World")]
    [InlineData("  👨‍👩‍👧 Family  ", "👨‍👩‍👧 Family")]
    [InlineData("", "")]
    public void WindowInfo_VisibleTitleStripsInvisibleMarksPreservingZwj(string raw, string expected)
    {
        Assert.Equal(expected, WindowInfo.VisibleTitle(raw));
    }

    [Theory]
    [InlineData("{invalid json", 2, 1320, 2868)]
    [InlineData("""{"zoomSteps":99,"phone":{"model":"Bad","screen":{"width":-1,"height":100}}}""", AppConfig.MaxZoomSteps, 1320, 2868)]
    [InlineData("""{"zoomSteps":-5,"copyAfterSelection":false,"autoCheckUpdates":false}""", AppConfig.MinZoomSteps, 1320, 2868)]
    public void AppConfig_FromJsonOrDefaultSanitizesCorruptAndOutOfRangeValues(
        string json,
        int expectedZoom,
        int expectedW,
        int expectedH)
    {
        var cfg = AppConfig.FromJsonOrDefault(json);
        Assert.Equal(expectedZoom, cfg.ZoomSteps);
        Assert.Equal(expectedW, cfg.Phone.Screen.Width);
        Assert.Equal(expectedH, cfg.Phone.Screen.Height);
    }

    [Fact]
    public void GeometryHelpers_RectContainsMatchesUnionIntersectionAndClamp()
    {
        var r1 = new RectD(10, 20, 100, 200);
        var r2 = new RectD(10.4, 19.6, 100.3, 199.7);
        Assert.True(r1.Matches(r2));
        Assert.True(r1.Contains(new PointD(10, 20)));
        Assert.False(r1.Contains(new PointD(111, 221)));

        Assert.Equal(10, 15.Clamp(0, 10));
        Assert.Equal(0, (-3).Clamp(0, 10));
    }

    [Fact]
    public void Momentum_DoesNotCreateBelowThresholdAndStopsOnLowVelocity()
    {
        Assert.Null(Momentum.TryCreate(150, 100, 1.0)); // hypot = 180.2 <= 200
        var m = Momentum.TryCreate(300, 400, 1.0);       // hypot = 500 > 200
        Assert.NotNull(m);

        var cur = m!.Value;
        var first = cur.Next();
        Assert.NotNull(first);
        Assert.Equal(MomentumPhase.Begin, first!.Value.Phase);

        var second = cur.Next();
        Assert.NotNull(second);
        Assert.Equal(MomentumPhase.Continue, second!.Value.Phase);
    }

    [Theory]
    [InlineData(1920, 1032, 1320, 2868)] // 1080p work area
    [InlineData(2048, 1240, 1320, 2868)] // 2560x1600 @ 125%
    [InlineData(1280, 680, 1080, 2400)]  // compact 720p screen
    public void ScreenFit_ComputePreviewLayout_FitsWithinWorkAreaAcrossResolutions(
        double workW,
        double workH,
        int phoneW,
        int phoneH)
    {
        var fit = ScreenFit.FromPhone(new PhoneScreenSpecData(phoneW, phoneH));
        var (designW, designH, scale) = ScreenFit.ComputePreviewLayout(new SizeD(workW, workH), fit);

        Assert.InRange(scale, 0.35, 0.84);
        Assert.True(designW * scale <= workW * 0.72 + 0.01);
        Assert.True(designH * scale <= workH * 0.84 + 0.01);
    }
}
