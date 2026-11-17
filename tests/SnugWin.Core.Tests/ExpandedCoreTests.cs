using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SnugWin.Core.Config;
using SnugWin.Core.Persistence;
using SnugWin.Core.PhoneScreen;
using SnugWin.Core.Session;
using SnugWin.Core.Touch;
using Xunit;

namespace SnugWin.Core.Tests;

public sealed class ExpandedCoreTests
{
    private sealed class FakeTimeline : ITimeline
    {
        public double Now { get; set; }
        public Task SleepAsync(double seconds, CancellationToken ct = default)
        {
            Now += Math.Max(0, seconds);
            return Task.CompletedTask;
        }
    }

    private sealed class SlowScreenshotControl : IScreenshotControl
    {
        public bool Trusted { get; set; } = true;
        public TaskCompletionSource<CapturedImage>? Pending { get; set; }
        public CaptureFailure? Failure { get; set; }
        public bool CopySuccess { get; set; } = true;
        public int CopyCount { get; private set; }

        public async Task<CapturedImage> CaptureAsync(WindowKey window)
        {
            if (Failure is { } f) throw new CaptureException(f);
            if (Pending is not null) return await Pending.Task;
            return new CapturedImage([1, 2], 100, 200);
        }

        public CapturedImage? Crop(CapturedImage image, RectD rect) =>
            rect.Width >= 1 && rect.Height >= 1 ? new CapturedImage([3], (int)rect.Width, (int)rect.Height) : null;

        public bool Copy(CapturedImage image)
        {
            if (!CopySuccess) return false;
            CopyCount++;
            return true;
        }
    }

    [Theory]
    [InlineData(-10, ZoomBook.MinSteps)]
    [InlineData(-4, -4)]
    [InlineData(0, 0)]
    [InlineData(4, 4)]
    [InlineData(8, 8)]
    [InlineData(20, ZoomBook.MaxSteps)]
    public void ZoomBook_ClampsRememberedAndDefaultSteps(int input, int expected)
    {
        var zb = new ZoomBook();
        Assert.True(ZoomBook.IsZoomable("WindowsTerminal.exe"));
        Assert.Equal(ZoomScope.Workspace, ZoomBook.ScopeOf("Code.exe"));
        Assert.Null(ZoomBook.ScopeOf("notepad.exe"));
        Assert.Equal(0, zb.StepsFor("notepad.exe", 2));

        zb.Remember(input, "WindowsTerminal.exe");
        Assert.Equal(expected, zb.StepsFor("WindowsTerminal.exe", 2));
        zb.ForgetAll();
        Assert.Empty(zb.Remembered);
    }

    [Fact]
    public void LeftBehindBook_MatchesKeyThenTitleThenNewestAndPrunes()
    {
        var t0 = DateTimeOffset.UtcNow;
        var book = new LeftBehindBook();

        // Ignored when Filled is null
        book.Add(new LeftBehindEntry { Record = new PhoneRecord { AppId = "a.exe" }, Since = t0 });
        Assert.Empty(book.Entries);

        var r1 = new PhoneRecord
        {
            Window = new WindowKey(1, 100),
            AppId = "Code.exe",
            Title = "Main",
            Filled = new RectD(2560, 0, 1080, 1800),
            Original = new RectD(10, 10, 800, 600),
            Zoom = 2
        };
        var r2 = new PhoneRecord
        {
            Window = new WindowKey(2, 200),
            AppId = "Code.exe",
            Title = "Other",
            Filled = new RectD(2560, 0, 1080, 1800),
            Original = new RectD(20, 20, 800, 600),
            Zoom = 1
        };
        book.Add(new LeftBehindEntry { Record = r1, Since = t0 });
        book.Add(new LeftBehindEntry { Record = r2, Since = t0.AddMinutes(1) });

        Assert.True(book.Awaits("Code.exe", new SizeD(1080, 1800)));
        Assert.False(book.Awaits("Code.exe", new SizeD(540, 900)));

        // Exact key match wins over newer entry
        var byKey = book.Match(new WindowInfo(new WindowKey(1, 100), "Code", "Code.exe", "Changed"), new RectD(0, 0, 1080, 1800));
        Assert.Equal(0, byKey);

        // Title match beats newest
        var byTitle = book.Match(new WindowInfo(new WindowKey(9, 999), "Code", "Code.exe", "Main"), new RectD(0, 0, 1080, 1800));
        Assert.Equal(0, byTitle);

        // Workspace carries zoom across PIDs; LiveWindow only same PID
        Assert.Equal(2, book.Entries[0].ReturnedAs(new WindowKey(99, 1)).Zoom);

        var termEntry = new LeftBehindEntry
        {
            Record = r1 with { AppId = "WindowsTerminal.exe", Zoom = 3 },
            Since = t0
        };
        Assert.Equal(3, termEntry.ReturnedAs(new WindowKey(1, 555)).Zoom);
        Assert.Equal(0, termEntry.ReturnedAs(new WindowKey(2, 555)).Zoom);

        // Prune expired & cap limit
        for (int i = 0; i < 25; i++)
            book.Add(new LeftBehindEntry { Record = r1, Since = t0.AddSeconds(i) });
        book.Prune(t0.AddDays(1));
        Assert.Equal(LeftBehindBook.Limit, book.Entries.Count);

        book.Prune(t0.AddDays(20));
        Assert.Empty(book.Entries);
    }

    [Fact]
    public void AppList_OrderedKeepingOrderGroupedAndActionWork()
    {
        var w1 = new WindowInfo(new WindowKey(1, 11), "Edge", "msedge.exe", "Doc");
        var w2 = new WindowInfo(new WindowKey(1, 12), "Edge", "msedge.exe", "Home");
        var w3 = new WindowInfo(new WindowKey(2, 21), "Weixin", "Weixin.exe", "Chat");

        var edge = new AppInfo("msedge.exe", "Edge", null);
        var wx = new AppInfo("Weixin.exe", "微信", null);
        var notes = new AppInfo("notepad.exe", "记事本", null);

        var ordered = AppList.Ordered(
            [new RunningApp(edge, 1, [w1, w2]), new RunningApp(wx, 2, [w3])],
            [21, 12, 11]);
        Assert.Equal("Weixin.exe", ordered[0].App.AppId);
        Assert.Equal(12UL, ordered[1].Windows[0].Key.Handle);

        var kept = AppList.KeepingOrder(
            [new RunningApp(edge, 1, [w1, w2]), new RunningApp(wx, 2, [w3])],
            [new RunningApp(wx, 2, [w3]), new RunningApp(edge, 1, [w2, w1])]);
        Assert.Equal("msedge.exe", kept[0].App.AppId);
        Assert.Equal(11UL, kept[0].Windows[0].Key.Handle);

        var sorted = AppList.Sorted([notes, wx, edge, edge]);
        Assert.Equal(3, sorted.Count);
        var groups = AppList.GroupedByLetter(sorted);
        Assert.Contains(groups, g => g.Letter == "W" && g.Apps.Any(a => a.AppId == "Weixin.exe"));
        Assert.Contains(groups, g => g.Letter == "J" && g.Apps.Any(a => a.AppId == "notepad.exe"));

        var search = AppList.Search("wx", sorted, ordered);
        Assert.Single(search.Apps, a => a.AppId == "Weixin.exe");

        var pullAction = AppList.ActionFor(edge, ordered);
        Assert.IsType<AppAction.Pull>(pullAction);
        Assert.IsType<AppAction.Open>(AppList.ActionFor(notes, ordered));
    }

    [Theory]
    [InlineData("{\"phoneScreen\":false,\"gestures\":true,\"gestureScroll\":false,\"gestureLongPressRightClick\":false,\"copyAfterSelection\":false,\"zoomSteps\":99}", false, true, false, false, false, AppConfig.MaxZoomSteps)]
    [InlineData("not-json", true, true, true, true, true, 2)]
    [InlineData("{}", true, true, true, true, true, 2)]
    public void AppConfig_FromJsonOrDefault_HandlesMissingInvalidAndBounds(
        string json,
        bool phoneScreen,
        bool gestures,
        bool scroll,
        bool rc,
        bool copySel,
        int zoom)
    {
        var cfg = AppConfig.FromJsonOrDefault(json);
        Assert.Equal(phoneScreen, cfg.PhoneScreen);
        Assert.Equal(gestures, cfg.Gestures);
        Assert.Equal(scroll, cfg.GestureScroll);
        Assert.Equal(rc, cfg.GestureLongPressRightClick);
        Assert.Equal(copySel, cfg.CopyAfterSelection);
        Assert.Equal(zoom, cfg.ZoomSteps);
    }

    [Theory]
    [InlineData(1.0, 9.9, false)]
    [InlineData(1.0, 10.1, true)]
    [InlineData(2.0, 19.9, false)]
    [InlineData(2.0, 20.1, true)]
    public void TouchTranslator_RespectsScaleSlop(double scale, double deltaX, bool triggersScroll)
    {
        var region = new TouchRegion(new RectD(0, 0, 540 * scale, 950 * scale), scale);
        var t = new TouchTranslator();
        t.Handle(new TouchInput(TouchInputKind.Press, new PointD(100, 100), 0), region);
        var drag = t.Handle(new TouchInput(TouchInputKind.Drag, new PointD(100 + deltaX, 100), 0.1), region);
        if (triggersScroll)
            Assert.Contains(drag.Commands, c => c is TouchCommand.Scroll);
        else
            Assert.Empty(drag.Commands);
    }

    [Fact]
    public void TouchTranslator_GranularOptionsAndCopyNotificationWork()
    {
        var region = new TouchRegion(new RectD(0, 0, 540, 950), 1.0);
        var noScrollOpts = new TouchOptions(AllowScroll: false, AllowRightClick: false, NotifySelectionEnded: true);
        var t = new TouchTranslator();

        // AllowScroll=false turns immediate drag into selection and notifies on release
        t.Handle(new TouchInput(TouchInputKind.Press, new PointD(100, 100), 0), region, noScrollOpts);
        var drag = t.Handle(new TouchInput(TouchInputKind.Drag, new PointD(140, 100), 0.05), region, noScrollOpts);
        Assert.IsType<TouchCommand.ReplayPress>(drag.Commands[0]);
        Assert.IsType<TouchCommand.ReplayCurrent>(drag.Commands[1]);
        var rel = t.Handle(new TouchInput(TouchInputKind.Release, new PointD(180, 100), 0.2), region, noScrollOpts);
        Assert.True(rel.PassOriginal);
        var selDone = Assert.IsType<TouchCommand.SelectionCompleted>(Assert.Single(rel.Commands));
        Assert.Equal(new PointD(180, 100), selDone.At);

        // AllowRightClick=false turns hold+release into normal left click
        t.Handle(new TouchInput(TouchInputKind.Press, new PointD(100, 100), 1.0), region, noScrollOpts);
        var holdRel = t.Handle(new TouchInput(TouchInputKind.Release, new PointD(101, 101), 1.8), region, noScrollOpts);
        Assert.Equal(2, holdRel.Commands.Count);
        Assert.IsType<TouchCommand.ReplayPress>(holdRel.Commands[0]);
        Assert.IsType<TouchCommand.ReplayCurrent>(holdRel.Commands[1]);
    }

    [Fact]
    public void Momentum_DecaysToStopAndCapsMaxVelocity()
    {
        Assert.Null(Momentum.TryCreate(100, 0, 1.0));
        var m = Momentum.TryCreate(1000, 0, 1.0);
        Assert.NotNull(m);

        var cur = m!.Value;
        int frames = 0;
        while (cur.Next() is { } step && frames < 500)
        {
            frames++;
            Assert.True(step.Dx > 0);
        }
        Assert.InRange(frames, 10, 200);
    }

    [Theory]
    [InlineData(CaptureFailure.NoPermission, ScreenshotNotice.NeedsPermission)]
    [InlineData(CaptureFailure.WindowGone, ScreenshotNotice.WindowGone)]
    [InlineData(CaptureFailure.Unavailable, ScreenshotNotice.Failed)]
    public async Task Screenshots_MapsCaptureFailuresToNotice(CaptureFailure failure, ScreenshotNotice expected)
    {
        var ctl = new SlowScreenshotControl { Failure = failure };
        var shots = new Screenshots(ctl, new FakeTimeline());
        await shots.TakeAsync(new WindowInfo(new WindowKey(1, 2), "App", "a.exe", "Win"));
        var notice = Assert.IsType<ScreenshotOverlay.Notice>(shots.Overlay);
        Assert.Equal(expected, notice.Kind);
    }

    [Fact]
    public async Task Screenshots_DropsStaleCaptureOnDismissOrNewTake()
    {
        var tcs = new TaskCompletionSource<CapturedImage>();
        var ctl = new SlowScreenshotControl { Pending = tcs };
        var shots = new Screenshots(ctl, new FakeTimeline());

        var win = new WindowInfo(new WindowKey(1, 2), "App", "a.exe", "Win");
        var task = shots.TakeAsync(win);
        shots.Dismiss();
        tcs.SetResult(new CapturedImage([1], 10, 10));
        await task;

        Assert.Null(shots.Overlay);
        Assert.Equal(0, ctl.CopyCount);
    }

    [Theory]
    [InlineData(CropHandle.TopLeft, -20, -20, 80, 80, 220, 320)]
    [InlineData(CropHandle.Right, 250, 0, 100, 100, 350, 300)]
    [InlineData(CropHandle.BottomRight, -180, -280, 100, 100, 40, 40)]
    public void CropGeometry_ResizesAllHandlesWithinBoundsAndMinSide(
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

    [Fact]
    public void JsonFileStore_QuarantinesCorruptFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), "snug-store-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "config.json");
            File.WriteAllText(file, "{corrupt json");
            var store = new JsonFileStore<AppConfig>(file);
            Assert.Null(store.LoadOrQuarantine());
            Assert.False(File.Exists(file));
            Assert.NotEmpty(Directory.GetFiles(dir, "config.json.corrupt-*"));

            var cfg = new AppConfig { ZoomSteps = 4 };
            store.Save(cfg);
            Assert.Equal(4, store.LoadOrQuarantine()!.ZoomSteps);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData("iPhone 16", ControllerKind.Phone)]
    [InlineData("Huawei MatePad", ControllerKind.Tablet)]
    [InlineData("MacBook Pro", ControllerKind.Desktop)]
    [InlineData("MyCustomDevice", ControllerKind.Unknown)]
    public void ControllerClassifier_AllKindsCovered(string name, ControllerKind expected)
    {
        Assert.Equal(expected, ControllerClassifier.Classify(name));
    }

    [Theory]
    [InlineData(SelectionBubbleAction.Copy, "Notepad", "notepad.exe", SelectionShortcutResolver.VkC, true, false)]
    [InlineData(SelectionBubbleAction.Copy, "CASCADIA_HOSTING_WINDOW_CLASS", "WindowsTerminal.exe", SelectionShortcutResolver.VkC, true, true)]
    [InlineData(SelectionBubbleAction.Copy, "ConsoleWindowClass", "powershell.exe", SelectionShortcutResolver.VkC, true, true)]
    [InlineData(SelectionBubbleAction.Paste, "Notepad", "notepad.exe", SelectionShortcutResolver.VkV, true, false)]
    [InlineData(SelectionBubbleAction.Paste, "CASCADIA_HOSTING_WINDOW_CLASS", "WindowsTerminal.exe", SelectionShortcutResolver.VkV, true, true)]
    [InlineData(SelectionBubbleAction.Paste, "ConsoleWindowClass", "cmd.exe", SelectionShortcutResolver.VkV, true, false)]
    [InlineData(SelectionBubbleAction.Delete, "Notepad", "notepad.exe", SelectionShortcutResolver.VkBack, false, false)]
    [InlineData(SelectionBubbleAction.Delete, "CASCADIA_HOSTING_WINDOW_CLASS", "WindowsTerminal.exe", SelectionShortcutResolver.VkBack, false, false)]
    public void SelectionShortcutResolver_MapsCopyPasteDeleteAcrossAppsAndTerminals(
        SelectionBubbleAction action,
        string cls,
        string exe,
        ushort expectedVk,
        bool expectedCtrl,
        bool expectedShift)
    {
        var key = SelectionShortcutResolver.Resolve(action, cls, exe);
        Assert.Equal(new SelectionKeystroke(expectedVk, expectedCtrl, expectedShift), key);
    }
}
