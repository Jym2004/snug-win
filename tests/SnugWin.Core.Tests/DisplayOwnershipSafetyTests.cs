using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SnugWin.Core.Persistence;
using SnugWin.Core.PhoneScreen;
using SnugWin.Windows.Display;
using Xunit;

namespace SnugWin.Core.Tests;

public sealed class DisplayOwnershipSafetyTests
{
    private sealed class RecordingVirtualDisplayIpc : IVirtualDisplayIpc
    {
        public bool IsLegacyProtocol => false;
        public List<uint> NotifiedIds { get; } = new();
        public List<uint> RemovedIds { get; } = new();
        public int RemoveAllCount { get; private set; }

        public Task NotifyAsync(IReadOnlyList<VddMonitor> monitors, CancellationToken ct = default)
        {
            foreach (var m in monitors)
                NotifiedIds.Add(m.Id);
            return Task.CompletedTask;
        }

        public Task ReplaceLegacyAsync(IReadOnlyList<uint> removeIds, IReadOnlyList<VddMonitor> addMonitors, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task RemoveAsync(IReadOnlyList<uint> ids, CancellationToken ct = default)
        {
            RemovedIds.AddRange(ids);
            return Task.CompletedTask;
        }

        public Task RemoveAllAsync(CancellationToken ct = default)
        {
            RemoveAllCount++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static MonitorSnapshot MakeVirtualMonitor(string deviceName, string friendlyName = "Cherry Virtual Display") =>
        new(
            StableId: 10,
            AdapterId: default,
            SourceId: 0,
            DeviceName: deviceName,
            FriendlyName: friendlyName,
            DeviceString: "Virtual Display Driver",
            IsPrimary: false,
            Bounds: new RectD(2560, 0, 1920, 1080),
            WorkArea: new RectD(2560, 0, 1920, 1040),
            Mode: new DisplayModeInfo(1920, 1080, 60, 100));

    [Fact]
    public async Task CleanupOrphansAsync_PreservesOwnedRecordsWhenPipeIsTemporarilyUnavailable()
    {
        string dir = Path.Combine(Path.GetTempPath(), "snug-pipe-down-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var paths = new SnugPaths(dir);
            var store = new OwnedDisplayStore(paths.OwnedDisplaysFile);
            store.RecordOwned(1);

            var ipc = new RecordingVirtualDisplayIpc();
            bool pipeUp = false;
            var svc = new PhoneDisplayService(ipc, paths, () => pipeUp, Array.Empty<MonitorSnapshot>, (_, _, _, _) => false);

            // Pipe unavailable on boot: must NOT call Remove/RemoveAll and must KEEP owned-displays.json
            await svc.CleanupOrphansAsync();
            Assert.Empty(ipc.RemovedIds);
            Assert.Equal(0, ipc.RemoveAllCount);
            Assert.Equal(new uint[] { 1 }, store.LoadIds());

            // Pipe recovers later: now CleanupOrphansAsync removes ONLY recorded ID #1 and clears file
            pipeUp = true;
            await svc.CleanupOrphansAsync();
            Assert.Equal(new uint[] { 1 }, ipc.RemovedIds);
            Assert.Equal(0, ipc.RemoveAllCount);
            Assert.Empty(store.LoadIds());
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task CreateAsync_TimeoutNeverDeletesCandidateIdsAndPrioritizesUnusedSlot()
    {
        string dir = Path.Combine(Path.GetTempPath(), "snug-timeout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var paths = new SnugPaths(dir);
            var ipc = new RecordingVirtualDisplayIpc();
            var preExistingUserScreen = MakeVirtualMonitor(@"\\.\DISPLAY99");

            var svc = new PhoneDisplayService(
                ipc,
                paths,
                () => true,
                () => [preExistingUserScreen],
                (_, _, _, _) => false,
                TimeSpan.FromMilliseconds(20));

            var ex = await Assert.ThrowsAsync<PhoneScreenException>(() => svc.CreateAsync(ScreenFit.Standard));
            Assert.Equal(PhoneScreenErrorKind.NeverOnline, ex.Kind);

            // Critical guarantee: timeouts MUST NOT emit Remove or RemoveAll on unowned candidate IDs
            Assert.Empty(ipc.RemovedIds);
            Assert.Equal(0, ipc.RemoveAllCount);
            Assert.Empty(new OwnedDisplayStore(paths.OwnedDisplaysFile).LoadIds());

            // Should try unused slot #1 first when 1 pre-existing virtual display is present
            Assert.Equal(new uint[] { 1, 2, 3, 0 }, ipc.NotifiedIds);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
