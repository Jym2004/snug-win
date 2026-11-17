using SnugWin.Core.PhoneScreen;
using SnugWin.Core.Touch;
using Xunit;

namespace SnugWin.Core.Tests;

public sealed class ScreenFitTests
{
    private static readonly SizeD BaseArea = new(540, 897);
    private readonly ScreenFit _fit = ScreenFit.Standard;

    [Fact]
    public void Standard_iPhone17ProMax_Produces540x950AndAllSteps()
    {
        Assert.Equal(new SizeD(540, 950), _fit.Size(540));
        Assert.Equal(new SizeD(1080, 1900), _fit.Size(1080));
        Assert.Equal((1080, 1900), _fit.PixelSize(540));
        Assert.Equal(540, _fit.Widths[0]);
        Assert.Equal(1080, _fit.Widths[^1]);
        for (int i = 1; i < _fit.Widths.Count; i++)
            Assert.Equal(10, _fit.Widths[i] - _fit.Widths[i - 1]);
    }

    [Fact]
    public void ShortScreen_WidensBaseWidthToMeet800Height()
    {
        var iphone8 = ScreenFit.FromPhone(new PhoneScreenSpecData(750, 1334, 375));
        Assert.Equal(620, iphone8.BaseWidth);
        Assert.True(iphone8.Size(620).Height >= 800);
        Assert.True(iphone8.Size(610).Height < 800);
    }

    [Fact]
    public void Ratio_IsClampedBetweenOneAndTwo()
    {
        var fold = ScreenFit.FromPhone(new PhoneScreenSpecData(1856, 2160));
        Assert.Equal(1.0, fold.Ratio);
        Assert.Equal(new SizeD(800, 800), fold.Size(fold.BaseWidth));

        var tall = ScreenFit.FromPhone(new PhoneScreenSpecData(9, 30));
        Assert.Equal(2.0, tall.Ratio);
        Assert.Equal(new SizeD(540, 1080), tall.Size(540));
    }

    [Fact]
    public void WidthToFit_SelectsNarrowestStepThatFits()
    {
        Assert.Equal(540, _fit.WidthToFit(BaseArea, BaseArea, 540));
        Assert.Equal(540, _fit.WidthToFit(new SizeD(542, 898), BaseArea, 540));
        Assert.Equal(740, _fit.WidthToFit(new SizeD(740, 897), BaseArea, 540));
        Assert.Equal(580, _fit.WidthToFit(new SizeD(574, 897), BaseArea, 540));
        Assert.Equal(580, _fit.WidthToFit(new SizeD(580, 897), BaseArea, 540));
        Assert.Equal(610, _fit.WidthToFit(new SizeD(540, 1000), BaseArea, 540));
        Assert.Equal(1080, _fit.WidthToFit(new SizeD(1200, 897), BaseArea, 540));
    }

    [Fact]
    public void Scale_And_HomeBarHeight_MatchSpec()
    {
        Assert.Equal(1.0, ScreenFit.Scale(540));
        Assert.Equal(2.0, ScreenFit.Scale(1080));
        Assert.Equal(28.0, ScreenFit.HomeBarHeight(540));
        Assert.Equal(56.0, ScreenFit.HomeBarHeight(1080));
    }
}
