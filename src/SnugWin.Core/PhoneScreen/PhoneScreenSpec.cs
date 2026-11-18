using System;

namespace SnugWin.Core.PhoneScreen;

public static class PhoneScreenSpec
{
    public const string Name = "Snug 手机屏";
    public const uint MonitorId = 0;
    // virtual-display-rs maps Monitor.id directly to IddCx ConnectorIndex (< MaxMonitorsSupported = 10)
    public static readonly uint[] CandidateIds = [0, 1, 2, 3, 4, 5, 6, 7];
    public static readonly TimeSpan MinimumLifetime = TimeSpan.FromSeconds(2.5);
}
