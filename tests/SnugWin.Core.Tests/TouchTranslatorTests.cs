using System.Linq;
using SnugWin.Core.PhoneScreen;
using SnugWin.Core.Touch;
using Xunit;

namespace SnugWin.Core.Tests;

public sealed class TouchTranslatorTests
{
    private static readonly PointD P0 = new(100, 300);
    private static readonly TouchRegion Phone = new(new RectD(0, 0, 540, 950), 1.0);
    private static readonly TouchRegion Elsewhere = new(new RectD(2000, 0, 540, 950), 1.0);

    private static TouchInput Input(TouchInputKind kind, double x, double y, double t) =>
        new(kind, new PointD(x, y), t);

    [Fact]
    public void OutsideRegion_PassesThrough()
    {
        var t = new TouchTranslator();
        Assert.Equal(TouchResponse.Pass, t.Handle(Input(TouchInputKind.Press, 100, 300, 0), Elsewhere));
        Assert.Equal(TouchResponse.Pass, t.Handle(Input(TouchInputKind.Drag, 160, 300, 0.1), Elsewhere));
        Assert.Equal(TouchResponse.Pass, t.Handle(Input(TouchInputKind.Release, 160, 300, 0.2), Elsewhere));
    }

    [Fact]
    public void Tap_HoldsPressThenReplaysOnRelease()
    {
        var t = new TouchTranslator();
        Assert.False(t.Handle(Input(TouchInputKind.Press, 100, 300, 0), Phone).PassOriginal);
        Assert.Empty(t.Handle(Input(TouchInputKind.Drag, 104, 305, 0.05), Phone).Commands);
        var rel = t.Handle(Input(TouchInputKind.Release, 104, 305, 0.1), Phone);
        Assert.False(rel.PassOriginal);
        Assert.Equal(2, rel.Commands.Count);
        Assert.IsType<TouchCommand.ReplayPress>(rel.Commands[0]);
        Assert.IsType<TouchCommand.ReplayCurrent>(rel.Commands[1]);
    }

    [Fact]
    public void CloseButtonTap_EmitsCloseWindowAndVerifiesZOrder()
    {
        var winRect = new RectD(0, 22, 540, 900);
        var cb = new CloseButton(new RectD(8, 30, 14, 14), 7, winRect);
        var region = Phone with { CloseButton = cb };
        var t = new TouchTranslator();

        Assert.Empty(t.Handle(Input(TouchInputKind.Press, 15, 37, 0), region).Commands);
        var rel = t.Handle(Input(TouchInputKind.Release, 16, 38, 0.1), region);
        var cw = Assert.IsType<TouchCommand.CloseWindow>(Assert.Single(rel.Commands));
        Assert.Equal(new PointD(15, 37), cw.At);

        Assert.True(cb.IsFrontmost(new PointD(15, 37), [(7, winRect), (3, new RectD(-500, 0, 1500, 1500))]));
        Assert.False(cb.IsFrontmost(new PointD(15, 37), [(9, new RectD(0, 22, 540, 892)), (7, winRect)]));
    }

    [Fact]
    public void Swipe_ProducesScrollEventsAndFlingMomentum()
    {
        var t = new TouchTranslator();
        t.Handle(Input(TouchInputKind.Press, 100, 300, 0), Phone);
        var first = t.Handle(Input(TouchInputKind.Drag, 100, 340, 0.05), Phone);
        var steps = first.Commands.OfType<TouchCommand.Scroll>().Select(c => c.Step).ToList();
        Assert.Equal(new[] { ScrollPhase.Began, ScrollPhase.Changed }, steps.Select(s => s.Phase).ToArray());
        Assert.Equal(40, steps[1].PixelsY);

        t.Handle(Input(TouchInputKind.Drag, 100, 420, 0.09), Phone);
        var end = t.Handle(Input(TouchInputKind.Release, 100, 460, 0.12), Phone);
        Assert.Single(end.Commands.OfType<TouchCommand.Scroll>(), c => c.Step.Phase == ScrollPhase.Ended);
        Assert.True(t.IsCoasting);

        var m1 = t.MomentumFrame();
        Assert.NotNull(m1);
        Assert.Equal(MomentumPhase.Begin, m1!.Value.Momentum);

        // Tap while coasting stops momentum without replaying click
        var stopResp = t.Handle(Input(TouchInputKind.Press, 100, 300, 0.2), Phone);
        Assert.Single(stopResp.Commands.OfType<TouchCommand.Scroll>(), c => c.Step.Momentum == MomentumPhase.End);
        Assert.False(t.IsCoasting);
        Assert.Empty(t.Handle(Input(TouchInputKind.Release, 100, 300, 0.25), Phone).Commands);
    }

    [Fact]
    public void HoldThenDrag_SelectsText_HoldThenRelease_RightClicks()
    {
        var t = new TouchTranslator();
        t.Handle(Input(TouchInputKind.Press, 100, 300, 0), Phone);
        var drag = t.Handle(Input(TouchInputKind.Drag, 130, 300, 0.6), Phone);
        Assert.Equal(2, drag.Commands.Count);
        Assert.IsType<TouchCommand.ReplayPress>(drag.Commands[0]);
        Assert.IsType<TouchCommand.ReplayCurrent>(drag.Commands[1]);
        Assert.Equal(TouchResponse.Pass, t.Handle(Input(TouchInputKind.Drag, 160, 300, 0.7), Phone));
        Assert.Equal(TouchResponse.Pass, t.Handle(Input(TouchInputKind.Release, 160, 300, 0.8), Phone));

        t.Handle(Input(TouchInputKind.Press, 100, 300, 1.0), Phone);
        var rc = t.Handle(Input(TouchInputKind.Release, 102, 301, 1.6), Phone);
        var cmd = Assert.IsType<TouchCommand.RightClick>(Assert.Single(rc.Commands));
        Assert.Equal(P0, cmd.At);
    }
}
