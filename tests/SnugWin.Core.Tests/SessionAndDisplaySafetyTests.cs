using System;
using System.IO;
using SnugWin.Core.Persistence;
using SnugWin.Core.PhoneScreen;
using SnugWin.Core.Session;
using Xunit;

namespace SnugWin.Core.Tests;

public sealed class SessionAndDisplaySafetyTests
{
    [Fact]
    public void OwnedDisplayStore_TracksAndReleasesOnlySnugOwnedIds()
    {
        string dir = Path.Combine(Path.GetTempPath(), "snug-owned-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new OwnedDisplayStore(Path.Combine(dir, "owned-displays.json"));
            Assert.Empty(store.LoadIds());

            store.RecordOwned(0);
            store.RecordOwned(0); // idempotent
            store.RecordOwned(2);
            Assert.Equal(new uint[] { 0, 2 }, store.LoadIds());

            store.ReleaseOwned(0);
            Assert.Equal(new uint[] { 2 }, store.LoadIds());

            store.ReleaseOwned(2);
            Assert.Empty(store.LoadIds());
            Assert.False(File.Exists(Path.Combine(dir, "owned-displays.json")));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
