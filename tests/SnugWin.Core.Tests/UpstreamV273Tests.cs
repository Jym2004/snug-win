using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using SnugWin.Core.Config;
using SnugWin.Core.PhoneScreen;
using Xunit;

namespace SnugWin.Core.Tests;

public sealed class UpstreamV273Tests
{
    private static PhoneCatalog LoadBundledCatalog()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Assets", "device-models.json");
        Assert.True(File.Exists(path), $"Expected device-models.json at {path}");
        return PhoneCatalog.LoadFromFile(path);
    }

    [Fact]
    public void LegacyIpadModels_ResolveToTabletViaLegacyNames_AndFitAsTablet()
    {
        var catalog = LoadBundledCatalog();
        var legacyScreen = new PhoneScreenSpecData(1488, 2266, 744);
        var legacyFromPhone = new PhoneChoice("Apple iPad mini (6th generation)", legacyScreen, DeviceKind.Phone);

        var resolved = catalog.Resolve(legacyFromPhone);
        Assert.Equal("Apple iPad mini（第 6 代）", resolved.Model);
        Assert.Equal(DeviceKind.Tablet, resolved.Kind);
        Assert.Equal(legacyScreen, resolved.Screen);

        // Tablet fit does not subtract phone toolbar inset
        var portraitFit = ScreenFit.FromDevice(resolved.Screen, resolved.Kind, ScreenOrientation.Portrait);
        var landscapeFit = ScreenFit.FromDevice(resolved.Screen, resolved.Kind, ScreenOrientation.Landscape);
        Assert.Equal(DeviceKind.Tablet, portraitFit.Kind);
        Assert.Equal(DeviceKind.Tablet, landscapeFit.Kind);
        Assert.Equal(new SizeD(600, 914), portraitFit.Size(portraitFit.BaseWidth));
        Assert.Equal(new SizeD(914, 600), landscapeFit.Size(landscapeFit.BaseWidth));
    }

    [Fact]
    public void ResolveCatalog_MapsLegacyIpadInMigratedConfig_AndLeavesUnknownOrCustomUntouched()
    {
        var catalog = LoadBundledCatalog();
        var screen = new PhoneScreenSpecData(1488, 2266, 744);

        var newer = new PhoneChoice("Apple iPad mini (A17 Pro)", screen, DeviceKind.Phone);
        Assert.Equal("Apple iPad mini（A17 Pro）", catalog.Resolve(newer).Model);
        Assert.Equal(DeviceKind.Tablet, catalog.Resolve(newer).Kind);

        var unknown = new PhoneChoice("Apple iPad mini (unknown)", screen, DeviceKind.Phone);
        Assert.Equal(unknown, catalog.Resolve(unknown));

        var custom = new PhoneChoice(null, screen, DeviceKind.Phone);
        Assert.Equal(custom, catalog.Resolve(custom));

        var legacyJson = """
        {
          "phoneScreen": true,
          "phone": { "model": "Apple iPad mini (6th generation)", "screen": { "width": 1488, "height": 2266, "points": 744 } }
        }
        """;
        var cfg = AppConfig.FromJsonOrDefault(legacyJson).ResolveCatalog(catalog);
        var resolvedScreen = Assert.Single(cfg.EffectiveScreens());
        Assert.Equal("Apple iPad mini（第 6 代）", resolvedScreen.Device.Model);
        Assert.Equal(DeviceKind.Tablet, resolvedScreen.Device.Kind);
        Assert.Equal("Apple iPad mini（第 6 代）", cfg.Phone.Model);
        Assert.True(cfg.PhoneScreen);
    }

    [Fact]
    public void LegacyNamesValidation_RejectsEmptyAndDuplicateNames()
    {
        const string emptyLegacy = """
        {
          "tablet": [{ "name": "Apple", "models": [{ "name": "iPad", "legacyNames": ["  "], "screen": [1640, 2360] }] }]
        }
        """;
        Assert.Throws<PhoneCatalogException>(() => PhoneCatalog.FromJson(emptyLegacy));

        const string dupLegacy = """
        {
          "tablet": [{ "name": "Apple", "models": [
            { "name": "iPad A", "legacyNames": ["iPad Legacy"], "screen": [1640, 2360] },
            { "name": "iPad B", "legacyNames": ["iPad Legacy"], "screen": [1640, 2360] }
          ] }]
        }
        """;
        Assert.Throws<PhoneCatalogException>(() => PhoneCatalog.FromJson(dupLegacy));
    }

    [Fact]
    public void DeviceScreen_DimensionsAndSizeText_FollowOrientation()
    {
        var screen = new PhoneScreenSpecData(1080, 2400, 360);
        Assert.Equal((1080, 2400), screen.Dimensions(ScreenOrientation.Portrait));
        Assert.Equal((2400, 1080), screen.Dimensions(ScreenOrientation.Landscape));
        Assert.Equal("1080 × 2400", screen.SizeText(ScreenOrientation.Portrait));
        Assert.Equal("2400 × 1080", screen.SizeText(ScreenOrientation.Landscape));
    }

    [Fact]
    public void OtherOrientation_ProposesAddExistingOrLimitReached_AndSkipsComputer()
    {
        var cfg = new AppConfig();
        var primary = Assert.Single(cfg.EffectiveScreens());

        var addLandscape = Assert.IsType<AppConfig.OtherOrientationResult.Add>(cfg.OtherOrientation(primary));
        Assert.Equal(ScreenOrientation.Landscape, addLandscape.Orientation);

        // Add disabled landscape counterpart
        cfg = cfg.WithScreenAdded(ScreenOrientation.Landscape, primary.Device);
        var landscape = cfg.EffectiveScreens()[1];
        Assert.IsType<AppConfig.OtherOrientationResult.Existing>(cfg.OtherOrientation(primary));

        cfg = cfg.WithScreenUpdated(landscape.Id, s => s with { Enabled = false });
        var disabledMatch = Assert.IsType<AppConfig.OtherOrientationResult.Existing>(cfg.OtherOrientation(primary));
        Assert.False(disabledMatch.Screen.Enabled);
        Assert.Equal(landscape.Id, disabledMatch.Screen.Id);

        // Adding another enabled landscape counterpart makes OtherOrientation prefer the enabled one
        cfg = cfg.WithScreenAdded(ScreenOrientation.Landscape, primary.Device, "横屏2");
        var enabledMatch = Assert.IsType<AppConfig.OtherOrientationResult.Existing>(cfg.OtherOrientation(primary));
        Assert.True(enabledMatch.Screen.Enabled);

        // Computer returns null
        var withComputer = new AppConfig().WithScreenAdded(ScreenOrientation.Landscape, PhoneChoice.StandardComputer);
        var computerScreen = withComputer.EffectiveScreens()[1];
        Assert.Null(withComputer.OtherOrientation(computerScreen));

        // LimitReached when 4 screens exist without opposite orientation match
        var full = new AppConfig()
            .WithScreenAdded(ScreenOrientation.Landscape, PhoneChoice.StandardTablet)
            .WithScreenAdded(ScreenOrientation.Landscape, PhoneChoice.StandardComputer)
            .WithScreenAdded(ScreenOrientation.Portrait, new PhoneChoice("Other", new PhoneScreenSpecData(1080, 2340), DeviceKind.Phone));
        Assert.Equal(ScreenConfig.Limit, full.EffectiveScreens().Count);
        Assert.IsType<AppConfig.OtherOrientationResult.LimitReached>(full.OtherOrientation(full.EffectiveScreens()[0]));
    }

    [Fact]
    public void Sanitize_PreservesLegacyFieldsForMigratedTablet_WithoutPromotingNewlyAddedTablet()
    {
        var tablet = new PhoneChoice("Apple iPad mini（第 6 代）", new PhoneScreenSpecData(1488, 2266, 744), DeviceKind.Tablet);

        // LegacyPhoneId screen corrected to portrait tablet preserves legacy fields
        var migrated = new AppConfig()
            .WithScreenUpdated(ScreenConfig.LegacyPhoneId, s => s with { Device = tablet, Enabled = false })
            .Sanitize();
        Assert.Equal(tablet, migrated.Phone);
        Assert.False(migrated.PhoneScreen);

        // Newly added portrait tablet (not LegacyPhoneId) does not replace legacy Phone device
        var addedOnly = new AppConfig()
            .WithScreenRemoved(ScreenConfig.LegacyPhoneId)
            .WithScreenAdded(ScreenOrientation.Portrait, tablet)
            .Sanitize();
        Assert.Equal(PhoneChoice.Standard, addedOnly.Phone);
    }
}
