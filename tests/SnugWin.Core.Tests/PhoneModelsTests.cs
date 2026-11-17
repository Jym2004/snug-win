using System;
using System.IO;
using System.Linq;
using SnugWin.Core.PhoneScreen;
using Xunit;

namespace SnugWin.Core.Tests;

public sealed class PhoneModelsTests
{
    [Fact]
    public void BundledPhoneModelsJson_IsValidAndContainsStandard()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Assets", "phone-models.json");
        Assert.True(File.Exists(path), $"Missing {path}");

        var catalog = PhoneCatalog.LoadFromFile(path);
        Assert.True(catalog.Brands.Count >= 10);
        Assert.True(catalog.Models.Count() >= 500);

        var std = catalog.FindModel(PhoneChoice.Standard.Model!);
        Assert.NotNull(std);
        Assert.Equal(PhoneChoice.Standard.Screen, std!.Screen);
    }

    [Fact]
    public void Search_MatchesBrandAliasAndModelWithoutSpaces()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Assets", "phone-models.json");
        var catalog = PhoneCatalog.LoadFromFile(path);

        var apple = catalog.Search("苹果iphone17promax");
        Assert.NotEmpty(apple);
        Assert.Contains(apple.SelectMany(b => b.Models), m => m.Id == "Apple iPhone 17 Pro Max");
    }

    [Fact]
    public void CustomResolution_NormalizesToPortrait()
    {
        var custom = PhoneScreenSpecData.Custom(2400, 1080);
        Assert.Equal(new PhoneScreenSpecData(1080, 2400), custom);
        Assert.Null(PhoneScreenSpecData.Custom(0, 1080));
        Assert.Null(PhoneScreenSpecData.Custom(null, 1080));
    }
}
