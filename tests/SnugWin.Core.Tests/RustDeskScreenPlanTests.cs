using System;
using System.Linq;
using System.Text.Json;
using SnugWin.Core.Config;
using SnugWin.Core.PhoneScreen;
using Xunit;

namespace SnugWin.Core.Tests;

public sealed class RustDeskScreenPlanTests
{
    private static AppConfig Config() => new()
    {
        MainScreen = "main",
        Screens =
        [
            new ScreenConfig { Id = "main", Name = "Persistent Screen", Device = Desktop(), Orientation = ScreenOrientation.Landscape },
            new ScreenConfig { Id = "touch", Name = "Touch Screen", Device = PhoneChoice.Standard },
            new ScreenConfig { Id = "computer", Name = "Computer Screen", Device = Desktop() }
        ]
    };

    private static PhoneChoice Desktop() => new("Generic Desktop", new PhoneScreenSpecData(1080, 1920), DeviceKind.Computer);

    [Fact]
    public void AutomaticModeConnectAndDisconnectKeepPersistentMain()
    {
        var config = Config();
        Assert.Equal(["main"], ScreenPlan.DesiredWishes(config, false, false, ManualRemoteScreenMode.Auto, false).Select(w => w.Id));
        Assert.Equal(["main", "touch"], ScreenPlan.DesiredWishes(config, false, false, ManualRemoteScreenMode.Auto, true).Select(w => w.Id));
        Assert.Equal(["main"], ScreenPlan.DesiredWishes(config, false, false, ManualRemoteScreenMode.Auto, false).Select(w => w.Id));
        Assert.False(ScreenPlan.IsRemoteActive(false, ManualRemoteScreenMode.Auto, false));
        Assert.True(ScreenPlan.IsRemoteActive(false, ManualRemoteScreenMode.Auto, true));
    }

    [Fact]
    public void UuAndRustDeskRequestsRemainIndependent()
    {
        var config = Config();
        var combined = ScreenPlan.DesiredWishes(config, true, false, ManualRemoteScreenMode.Auto, true);
        Assert.Equal(["main", "computer", "touch"], combined.Select(w => w.Id));
        Assert.Equal(combined.Count, combined.Select(w => w.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(["main", "computer"], ScreenPlan.DesiredWishes(config, true, false, ManualRemoteScreenMode.Auto, false).Select(w => w.Id));
        Assert.Equal(["main", "touch"], ScreenPlan.DesiredWishes(config, false, false, ManualRemoteScreenMode.Auto, true).Select(w => w.Id));
    }

    [Fact]
    public void ExplicitModesOverrideAutomaticPresence()
    {
        var config = Config();
        Assert.Equal(["main"], ScreenPlan.DesiredWishes(config, false, true, ManualRemoteScreenMode.Off, true).Select(w => w.Id));
        Assert.Equal(["main", "computer"], ScreenPlan.DesiredWishes(config, false, true, ManualRemoteScreenMode.Computer, true).Select(w => w.Id));
        Assert.Equal(["main", "touch"], ScreenPlan.DesiredWishes(config, false, false, ManualRemoteScreenMode.TouchScreen, false).Select(w => w.Id));
        Assert.Empty(ScreenPlan.ForManualMode(ScreenPlan.Wishes(config, true), ManualRemoteScreenMode.Auto));
    }

    [Fact]
    public void AutomaticModeWithNoTouchTargetDoesNotRequestComputerScreen()
    {
        var config = Config().WithScreenUpdated("touch", screen => screen with { Enabled = false });
        Assert.False(ScreenPlan.HasManualModeTarget(ScreenPlan.Wishes(config, true), ManualRemoteScreenMode.Auto));
        Assert.Equal(["main"], ScreenPlan.DesiredWishes(config, false, false, ManualRemoteScreenMode.Auto, true).Select(w => w.Id));
        Assert.Contains("未找到", ScreenPlan.ManualModeStatus(ManualRemoteScreenMode.Auto, false, true));
    }

    [Fact]
    public void PlanningDoesNotPersistRemoteStateOrChangeInputFilter()
    {
        var config = Config();
        string before = JsonSerializer.Serialize(config);
        _ = ScreenPlan.DesiredWishes(config, false, false, ManualRemoteScreenMode.Auto, true);
        _ = ScreenPlan.DesiredWishes(config, false, false, ManualRemoteScreenMode.Auto, false);
        Assert.Equal(before, JsonSerializer.Serialize(config));
        Assert.True(config.GesturesInjectedOnly);
        Assert.DoesNotContain("rustDesk", before, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("manualRemote", before, StringComparison.OrdinalIgnoreCase);
    }
}
